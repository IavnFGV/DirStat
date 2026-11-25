#!/usr/bin/env python3
"""WSL filesystem and current-user write metrics. Standard library only."""
import argparse
import datetime as dt
import fcntl
import json
import logging
from logging.handlers import RotatingFileHandler
import os
import pathlib
import shutil
import socket
import subprocess
import sys
import threading
import time

# The agent ships with its helper module beside it; make that importable however
# the agent is started (python3 path/agent.py, importlib in tests, any cwd).
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import treescan  # noqa: E402

GIB = 1024 ** 3
ANALYSIS_COOLDOWN = 600
REPLACE_ATTEMPTS = 6
ALREADY_RUNNING = 2
LOG = logging.getLogger('disk_space_monitor_agent')


def configure_logging(output):
    LOG.setLevel(logging.INFO)
    LOG.propagate = False
    log_path = pathlib.Path(output).with_name('wsl-agent.log')
    handler = RotatingFileHandler(log_path, maxBytes=1_048_576,
                                  backupCount=1, encoding='utf-8')
    handler.setFormatter(logging.Formatter('%(asctime)s %(levelname)s %(message)s'))
    LOG.addHandler(handler)


def atomic_json(path, data, compact=False):
    target = pathlib.Path(path)
    target.parent.mkdir(parents=True, exist_ok=True)
    temporary = target.with_name(target.name + '.tmp.' + str(os.getpid()))
    try:
        with open(temporary, 'w', encoding='utf-8') as stream:
            if compact:
                json.dump(data, stream, ensure_ascii=False, separators=(',', ':'))
            else:
                json.dump(data, stream, ensure_ascii=False, indent=2)
        # On /mnt/<drive> (drvfs) Windows refuses to replace a file that a Windows
        # process is reading at this exact moment. Retry briefly instead of crashing.
        for attempt in range(REPLACE_ATTEMPTS):
            try:
                os.replace(temporary, target)
                return
            except PermissionError:
                if attempt == REPLACE_ATTEMPTS - 1:
                    raise
                time.sleep(0.05 * (attempt + 1))
    finally:
        try:
            temporary.unlink(missing_ok=True)
        except OSError:
            pass


def timestamp():
    return dt.datetime.now(dt.timezone.utc).isoformat()


def filesystem():
    stat = os.statvfs('/')
    total = stat.f_blocks * stat.f_frsize
    free = stat.f_bfree * stat.f_frsize
    available = stat.f_bavail * stat.f_frsize
    used = total - free
    return {
        'totalGiB': total / GIB,
        'usedGiB': used / GIB,
        'availableGiB': available / GIB,
        'freeGiB': free / GIB,
        'reservedGiB': max(0, free - available) / GIB,
        'usedPercent': used / total * 100 if total else 0,
    }


def process_writes(previous, elapsed):
    current = {}
    top = None
    processes = []
    uid = os.getuid()
    for proc in pathlib.Path('/proc').iterdir():
        if not proc.name.isdigit():
            continue
        try:
            if proc.stat().st_uid != uid:
                continue
            io_lines = (proc / 'io').read_text(encoding='ascii').splitlines()
            io = dict(line.split(':', 1) for line in io_lines if ':' in line)
            written = int(io['write_bytes'].strip())
            pid = int(proc.name)
            current[pid] = written
            if pid in previous and elapsed > 0:
                rate = max(0, written - previous[pid]) / elapsed
                if rate <= 0:
                    continue
                command = (proc / 'cmdline').read_bytes().split(b'\0')[0].decode('utf-8', 'replace')
                command = os.path.basename(command) or (proc / 'comm').read_text().strip()
                processes.append({'pid': pid, 'command': command[:120], 'writeBytesPerSecond': rate})
                if top is None or rate > top['writeBytesPerSecond']:
                    top = {'pid': pid, 'command': command[:120], 'writeBytesPerSecond': rate}
        except (OSError, ValueError, KeyError, IndexError):
            continue
    return current, top, sorted(processes, key=lambda item: item['writeBytesPerSecond'], reverse=True)


def analysis_paths():
    home = pathlib.Path.home()
    paths = [home / 'projects', home / '.cache', home / '.local/share/containers',
             home / '.local/share/containers/storage']
    try:
        # Hidden dirs included: .ollama, .vscode-server, .nvm etc. often hold most of home.
        paths.extend(p for p in home.iterdir() if p.is_dir() and not p.is_symlink() and p not in paths)
    except OSError:
        pass
    return list(dict.fromkeys(p for p in paths if p.exists()))


def du_command(path):
    """Rootless Podman stores layers under subordinate UIDs; plain du cannot read
    them and undercounts, so measure that tree inside the user namespace."""
    command = ['du', '-skx', '--', str(path)]
    containers = pathlib.Path.home() / '.local/share/containers'
    if (path == containers or containers in path.parents) and shutil.which('podman'):
        return ['podman', 'unshare'] + command
    return command


def parse_du(path, returncode, stdout, stderr):
    """du exits with 1 when some entries are unreadable but still prints the total."""
    try:
        size = int(stdout.split()[0]) * 1024 / GIB
    except (IndexError, ValueError):
        return {'path': str(path), 'sizeGiB': 0, 'error': (stderr.strip() or f'du exit {returncode}')[:200]}
    if returncode == 0:
        return {'path': str(path), 'sizeGiB': size, 'error': None}
    errors = [line for line in stderr.splitlines() if line.strip()]
    first = errors[0] if errors else f'du exit {returncode}'
    return {'path': str(path), 'sizeGiB': size,
            'error': f'Частично: не прочитано {len(errors)} объектов, размер занижен. {first}'[:200]}


def analyze():
    results = []
    for path in analysis_paths():
        try:
            run = subprocess.run(du_command(path), capture_output=True,
                                 text=True, timeout=180, check=False)
            results.append(parse_du(path, run.returncode, run.stdout, run.stderr))
        except (OSError, subprocess.TimeoutExpired) as error:
            results.append({'path': str(path), 'sizeGiB': 0, 'error': str(error)[:200]})
    return results


def load_previous(output):
    """Restore the last analysis so a restarted agent neither loses it nor
    re-runs du for a request that was already handled."""
    try:
        data = json.loads(pathlib.Path(output).read_text(encoding='utf-8'))
        if not isinstance(data, dict):
            return None, [], None
        directories = data.get('directories')
        return (data.get('analysisRequestId'),
                directories if isinstance(directories, list) else [],
                data.get('directoryAnalysisTimestamp'))
    except (OSError, ValueError):
        return None, [], None


def read_request_id(path):
    try:
        request = json.loads(pathlib.Path(path).read_text(encoding='utf-8'))
    except (OSError, ValueError):
        return None
    return request.get('requestId') if isinstance(request, dict) else None


def load_tree_request(path):
    """Id of the tree scan already written, so a restart does not rescan."""
    try:
        data = json.loads(pathlib.Path(path).read_text(encoding='utf-8'))
    except (OSError, ValueError):
        return None
    return data.get('requestId') if isinstance(data, dict) else None


class TreeScanner:
    """Runs one tree scan at a time in a background thread and reports progress."""

    def __init__(self, output, root='/'):
        self.output = output
        self.root = root
        self.lock = threading.Lock()
        self.thread = None
        self.completed_request = load_tree_request(output)
        self.status = {'state': 'done' if self.completed_request else 'idle',
                       'requestId': self.completed_request}

    def running(self):
        return self.thread is not None and self.thread.is_alive()

    def start(self, request_id):
        if self.running():
            return
        with self.lock:
            self.status = {'state': 'running', 'requestId': request_id, 'startedAt': timestamp(),
                           'files': 0, 'bytes': 0}
        self.thread = threading.Thread(target=self._run, args=(request_id,), daemon=True)
        self.thread.start()

    def _progress(self, files, total):
        with self.lock:
            self.status['files'] = files
            self.status['bytes'] = total

    def _run(self, request_id):
        LOG.info('Tree scan started; request=%s root=%s', request_id, self.root)
        try:
            stat = os.statvfs(self.root)
            used = (stat.f_blocks - stat.f_bfree) * stat.f_frsize
            document = treescan.build(self.root, used, self._progress, timestamp())
            document['requestId'] = request_id
            atomic_json(self.output, document, compact=True)
            self.completed_request = request_id
            with self.lock:
                self.status = {'state': 'done', 'requestId': request_id, 'finishedAt': timestamp(),
                               'files': document['scannedFiles'], 'bytes': document['scannedBytes']}
            LOG.info('Tree scan finished; files=%s bytes=%s errors=%s seconds=%s',
                     document['scannedFiles'], document['scannedBytes'],
                     document['errorCount'], document['durationSeconds'])
        except Exception as error:
            LOG.exception('Tree scan failed')
            with self.lock:
                self.status = {'state': 'error', 'requestId': request_id, 'error': str(error)[:300]}

    def snapshot(self):
        with self.lock:
            return dict(self.status)


def exit_when_stdin_closes():
    """The Windows app keeps stdin open. When wsl.exe exits or is killed the pipe
    closes; without this the Linux process would outlive it and hold the lock."""
    try:
        while sys.stdin.buffer.read(4096):
            pass
    except (OSError, ValueError):
        pass
    LOG.info('stdin closed; agent exiting')
    logging.shutdown()
    os._exit(0)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--output', required=True)
    parser.add_argument('--request', required=True)
    parser.add_argument('--interval', type=int, default=5)
    parser.add_argument('--tree-request', help='tree scan request file (default: beside --request)')
    parser.add_argument('--tree-output', help='tree scan result file (default: beside --output)')
    parser.add_argument('--exit-on-stdin-eof', action='store_true',
                        help='exit when stdin is closed (used by the Windows app)')
    args = parser.parse_args()
    if args.interval < 2:
        parser.error('interval must be at least 2 seconds')
    pathlib.Path(args.output).parent.mkdir(parents=True, exist_ok=True)
    configure_logging(args.output)
    lock_path = pathlib.Path(args.output + '.lock')
    lock = open(lock_path, 'a+', encoding='utf-8')
    try:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
    except BlockingIOError:
        LOG.warning('Agent already running for output=%s', args.output)
        print('Agent already running for this output', file=sys.stderr)
        return ALREADY_RUNNING
    if args.exit_on_stdin_eof:
        threading.Thread(target=exit_when_stdin_closes, daemon=True).start()
    LOG.info('Agent started; pid=%s interval=%ss output=%s', os.getpid(), args.interval, args.output)
    previous = {}
    previous_time = time.monotonic()
    completed_request, directories, analysis_timestamp = load_previous(args.output)
    last_request = completed_request
    last_analysis = 0.0
    write_error = None
    analysis_thread = None
    analysis_result = []
    tree_request_path = args.tree_request or str(pathlib.Path(args.request).with_name('wsl-tree-request.json'))
    tree = TreeScanner(args.tree_output or str(pathlib.Path(args.output).with_name('wsl-tree.json')))
    perf_since = time.monotonic()
    perf_samples = 0
    scan_total_ms = 0.0
    scan_max_ms = 0.0
    json_total_ms = 0.0
    json_max_ms = 0.0

    def run_analysis():
        nonlocal analysis_result
        try:
            analysis_result = analyze()
        except Exception:
            LOG.exception('Directory analysis failed')
            analysis_result = []

    while True:
        started = time.monotonic()
        current, top, processes = process_writes(previous, max(0, started - previous_time))
        scan_ms = (time.monotonic() - started) * 1000
        previous, previous_time = current, started
        request_id = read_request_id(args.request)
        tree_request = read_request_id(tree_request_path)
        if tree_request and tree_request != tree.completed_request and                 tree.snapshot().get('requestId') != tree_request and not tree.running():
            tree.start(tree_request)
        if (request_id and request_id != last_request and analysis_thread is None
                and started - last_analysis >= ANALYSIS_COOLDOWN):
            last_request = request_id
            last_analysis = started
            LOG.info('Directory analysis started; request=%s', request_id)
            analysis_thread = threading.Thread(target=run_analysis, daemon=True)
            analysis_thread.start()
        if analysis_thread is not None and not analysis_thread.is_alive():
            directories = analysis_result
            analysis_timestamp = timestamp()
            completed_request = last_request
            LOG.info('Directory analysis finished; paths=%s errors=%s', len(directories),
                     sum(bool(item.get('error')) for item in directories))
            analysis_thread = None
        payload = {
            'schemaVersion': 1, 'timestamp': timestamp(), 'hostname': socket.gethostname(),
            **filesystem(), 'topWriter': top,
            'totalWriteBytesPerSecond': sum(item['writeBytesPerSecond'] for item in processes),
            'processes': processes, 'directories': directories,
            'directoryAnalysisTimestamp': analysis_timestamp,
            'analysisRequestId': completed_request,
            'treeScan': tree.snapshot(),
        }
        json_started = time.monotonic()
        try:
            atomic_json(args.output, payload)
            if write_error is not None:
                LOG.info('Metrics JSON writable again after: %s', write_error)
                write_error = None
        except OSError as error:
            # One skipped sample is harmless; crashing the agent is not.
            if str(error) != write_error:
                LOG.warning('Metrics JSON write failed, sample skipped: %s', error)
                write_error = str(error)
        json_ms = (time.monotonic() - json_started) * 1000
        perf_samples += 1
        scan_total_ms += scan_ms
        scan_max_ms = max(scan_max_ms, scan_ms)
        json_total_ms += json_ms
        json_max_ms = max(json_max_ms, json_ms)
        if time.monotonic() - perf_since >= 600:
            LOG.info('Performance 10min: samples=%s procScanAvgMs=%.2f procScanMaxMs=%.2f '
                     'jsonAvgMs=%.2f jsonMaxMs=%.2f processes=%s',
                     perf_samples, scan_total_ms / perf_samples, scan_max_ms,
                     json_total_ms / perf_samples, json_max_ms, len(current))
            perf_since = time.monotonic()
            perf_samples = 0
            scan_total_ms = scan_max_ms = json_total_ms = json_max_ms = 0.0
        time.sleep(max(0, args.interval - (time.monotonic() - started)))


if __name__ == '__main__':
    try:
        sys.exit(main())
    except Exception:
        LOG.exception('Agent crashed')
        raise
