"""Integration smoke test for /proc write sampling; run inside WSL."""
import importlib.util
import os
import pathlib
import tempfile


agent_path = pathlib.Path(__file__).resolve().parents[1] / 'wsl' / 'agent.py'
spec = importlib.util.spec_from_file_location('disk_space_agent', agent_path)
agent = importlib.util.module_from_spec(spec)
spec.loader.exec_module(agent)

before, _, _ = agent.process_writes({}, 1)
with tempfile.NamedTemporaryFile(dir='/tmp') as target:
    target.write(os.urandom(1024 * 1024))
    target.flush()
    os.fsync(target.fileno())
    after, top, processes = agent.process_writes(before, 1)

own = next((item for item in processes if item['pid'] == os.getpid()), None)
assert own is not None and own['writeBytesPerSecond'] > 0, processes
assert top is not None and top['writeBytesPerSecond'] > 0
print('PASS current-user write rate and top writer')
