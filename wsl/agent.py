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
import socket
import subprocess
import sys
import threading
import time

GIB = 1024 ** 3
ANALYSIS_COOLDOWN = 600
LOG = logging.getLogger('disk_space_monitor_agent')


def configure_logging(output):
    LOG.setLevel(logging.INFO)
    LOG.propagate = False
    log_path = pathlib.Path(output).with_name('wsl-agent.log')
    handler = RotatingFileHandler(log_path, maxBytes=1_048_576,
                                  backupCount=1, encoding='utf-8')
    handler.setFormatter(logging.Formatter('%(asctime)s %(levelname)s %(message)s'))
    LOG.addHandler(handler)


def atomic_json(path, data):
    target = pathlib.Path(path)
    target.parent.mkdir(parents=True, exist_ok=True)
    temporary = target.with_name(target.name + '.tmp.' + str(os.getpid()))
    try:
        with open(temporary, 'w', encoding='utf-8') as stream:
            json.dump(data, stream, ensure_ascii=False, indent=2)
        os.replace(temporary, target)
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
        paths.extend(p for p in home.iterdir() if p.is_dir() and p not in paths and not p.name.startswith('.'))
    except OSError:
        pass
    return list(dict.fromkeys(p for p in paths if p.exists()))


def analyze():
    results = []
    for path in analysis_paths():
        try:
            run = subprocess.run(['du', '-sk', '--', str(path)], capture_output=True,
                                 text=True, timeout=180, check=False)
            if run.returncode != 0:
                results.append({'path': str(path), 'sizeGiB': 0, 'error': run.stderr.strip()[:200]})
            else:
                results.append({'path': str(path), 'sizeGiB': int(run.stdout.split()[0]) * 1024 / GIB, 'error': None})
        except (OSError, ValueError, subprocess.TimeoutExpired) as error:
            results.append({'path': str(path), 'sizeGiB': 0, 'error': str(error)[:200]})
    return results


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--output', required=True)
    parser.add_argument('--request', required=True)
    parser.add_argument('--interval', type=int, default=5)
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
        return 2
    LOG.info('Agent started; pid=%s interval=%ss output=%s', os.getpid(), args.interval, args.output)
    previous = {}
    previous_time = time.monotonic()
    last_request = None
    last_analysis = 0.0
    directories = []
    analysis_timestamp = None
    analysis_thread = None
    analysis_result = []
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
        try:
            request = json.loads(pathlib.Path(args.request).read_text(encoding='utf-8'))
            request_id = request.get('requestId')
        except (OSError, ValueError):
            request_id = None
        if request_id and request_id != last_request and started - last_analysis >= ANALYSIS_COOLDOWN:
            last_request = request_id
            last_analysis = started
            LOG.info('Directory analysis started; request=%s', request_id)
            analysis_thread = threading.Thread(target=run_analysis, daemon=True)
            analysis_thread.start()
        if analysis_thread is not None and not analysis_thread.is_alive():
            directories = analysis_result
            analysis_timestamp = timestamp()
            LOG.info('Directory analysis finished; paths=%s errors=%s', len(directories),
                     sum(bool(item.get('error')) for item in directories))
            analysis_thread = None
        payload = {
            'schemaVersion': 1, 'timestamp': timestamp(), 'hostname': socket.gethostname(),
            **filesystem(), 'topWriter': top,
            'totalWriteBytesPerSecond': sum(item['writeBytesPerSecond'] for item in processes),
            'processes': processes, 'directories': directories,
            'directoryAnalysisTimestamp': analysis_timestamp,
        }
        json_started = time.monotonic()
        atomic_json(args.output, payload)
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
