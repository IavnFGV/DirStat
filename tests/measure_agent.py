"""Measure agent CPU and I/O over a short run inside WSL (standard library only)."""
import argparse
import os
import pathlib
import subprocess
import sys
import time


def counters(pid):
    stat = pathlib.Path(f'/proc/{pid}/stat').read_text()
    fields = stat[stat.rfind(')') + 2:].split()
    io = dict(line.split(':', 1) for line in pathlib.Path(f'/proc/{pid}/io').read_text().splitlines())
    ticks = int(fields[11]) + int(fields[12])
    return ticks, int(io['rchar']), int(io['wchar']), int(io['read_bytes']), int(io['write_bytes'])


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--seconds', type=int, default=30)
    parser.add_argument('--output', required=True)
    args = parser.parse_args()
    script = pathlib.Path(__file__).resolve().parents[1] / 'wsl' / 'agent.py'
    output = pathlib.Path(args.output)
    output.parent.mkdir(parents=True, exist_ok=True)
    log_path = output.parent / 'wsl-agent.log'
    log_before = log_path.stat().st_size if log_path.exists() else 0
    command = [sys.executable, str(script), '--output', str(output),
               '--request', str(output.parent / 'analysis-request.json'), '--interval', '5']
    process = subprocess.Popen(command, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE, text=True)
    try:
        time.sleep(1)
        if process.poll() is not None:
            raise RuntimeError('Agent exited: ' + process.stderr.read())
        start = counters(process.pid)
        started = time.monotonic()
        time.sleep(args.seconds)
        elapsed = time.monotonic() - started
        end = counters(process.pid)
        cpu_seconds = (end[0] - start[0]) / os.sysconf('SC_CLK_TCK')
        print(f'elapsed={elapsed:.1f}s cpu={cpu_seconds:.3f}s one_core_cpu={cpu_seconds / elapsed * 100:.2f}%')
        print(f'read_syscall_bytes={end[1] - start[1]} write_syscall_bytes={end[2] - start[2]}')
        print(f'disk_read_bytes={end[3] - start[3]} disk_write_bytes={end[4] - start[4]}')
        print(f'json_bytes={output.stat().st_size} log_appended_bytes={log_path.stat().st_size - log_before}')
    finally:
        process.terminate()
        try:
            process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait()


if __name__ == '__main__':
    main()
