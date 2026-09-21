#!/usr/bin/env python3
"""WSL filesystem and current-user write metrics. Standard library only."""
import argparse
import datetime as dt
import fcntl
import json
import os
import pathlib
import socket
import subprocess
import sys
import threading
import time

GIB = 1024 ** 3
ANALYSIS_COOLDOWN = 600


def atomic_json(path, data):
    target = pathlib.Path(path)
    target.parent.mkdir(parents=True, exist_ok=True)
    temporary = target.with_name(target.name + '.tmp.' + str(os.getpid()))
    try:
        with open(temporary, 'w', encoding='utf-8') as stream:
            json.dump(data, stream, ensure_ascii=False, indent=2)
            stream.flush()
            os.fsync(stream.fileno())
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
            command = (proc / 'cmdline').read_bytes().split(b'\0')[0].decode('utf-8', 'replace')
            command = os.path.basename(command) or (proc / 'comm').read_text().strip()
            pid = int(proc.name)
            current[pid] = written
            if pid in previous and elapsed > 0:
                rate = max(0, written - previous[pid]) / elapsed
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
    lock_path = pathlib.Path(args.output + '.lock')
    lock = open(lock_path, 'a+', encoding='utf-8')
    try:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
    except BlockingIOError:
        print('Agent already running for this output', file=sys.stderr)
        return 2
    previous = {}
    previous_time = time.monotonic()
    last_request = None
    last_analysis = 0.0
    directories = []
    analysis_timestamp = None
    analysis_thread = None
    analysis_result = []

    def run_analysis():
        nonlocal analysis_result
        analysis_result = analyze()

    while True:
        started = time.monotonic()
        current, top, processes = process_writes(previous, max(0, started - previous_time))
        previous, previous_time = current, started
        try:
            request = json.loads(pathlib.Path(args.request).read_text(encoding='utf-8'))
            request_id = request.get('requestId')
        except (OSError, ValueError):
            request_id = None
        if request_id and request_id != last_request and started - last_analysis >= ANALYSIS_COOLDOWN:
            last_request = request_id
            last_analysis = started
            analysis_thread = threading.Thread(target=run_analysis, daemon=True)
            analysis_thread.start()
        if analysis_thread is not None and not analysis_thread.is_alive():
            directories = analysis_result
            analysis_timestamp = timestamp()
            analysis_thread = None
        payload = {
            'schemaVersion': 1, 'timestamp': timestamp(), 'hostname': socket.gethostname(),
            **filesystem(), 'topWriter': top, 'processes': processes, 'directories': directories,
            'directoryAnalysisTimestamp': analysis_timestamp,
        }
        atomic_json(args.output, payload)
        time.sleep(max(0, args.interval - (time.monotonic() - started)))


if __name__ == '__main__':
    sys.exit(main())
