"""Unit tests for agent helpers that do not need /proc; run inside WSL: python3 tests/test_agent_logic.py"""
import importlib.util
import json
import pathlib
import tempfile
from unittest import mock

agent_path = pathlib.Path(__file__).resolve().parents[1] / 'wsl' / 'agent.py'
spec = importlib.util.spec_from_file_location('disk_space_agent', agent_path)
agent = importlib.util.module_from_spec(spec)
spec.loader.exec_module(agent)


def check(condition, name):
    assert condition, name
    print('PASS', name)


# du exits 1 on unreadable entries but still prints a total: keep it, flag as partial.
partial = agent.parse_du('/home/u/.local/share/containers', 1, '2097152\t/x\n',
                         "du: cannot read directory 'a': Permission denied\n"
                         "du: cannot read directory 'b': Permission denied\n")
check(partial['sizeGiB'] == 2.0 and partial['error'].startswith('Частично: не прочитано 2'), 'du partial size kept')
check(agent.parse_du('/p', 0, '1048576\t/p\n', '') == {'path': '/p', 'sizeGiB': 1.0, 'error': None}, 'du success')
failed = agent.parse_du('/p', 1, '', 'du: cannot access')
check(failed['sizeGiB'] == 0 and 'cannot access' in failed['error'], 'du hard failure')

home = pathlib.Path('/home/u')
with mock.patch.object(agent.pathlib.Path, 'home', return_value=home), \
        mock.patch.object(agent.shutil, 'which', return_value='/usr/bin/podman'):
    check(agent.du_command(home / '.local/share/containers/storage')[:2] == ['podman', 'unshare'], 'podman unshare for containers')
    check(agent.du_command(home / 'projects')[0] == 'du', 'plain du elsewhere')
with mock.patch.object(agent.pathlib.Path, 'home', return_value=home), \
        mock.patch.object(agent.shutil, 'which', return_value=None):
    check(agent.du_command(home / '.local/share/containers')[0] == 'du', 'plain du without podman')

with tempfile.TemporaryDirectory() as directory:
    output = pathlib.Path(directory) / 'metrics.json'
    check(agent.load_previous(output) == (None, [], None), 'no previous output')
    output.write_text(json.dumps({'analysisRequestId': 'abc', 'directories': [{'path': '/p'}],
                                  'directoryAnalysisTimestamp': 't'}), encoding='utf-8')
    check(agent.load_previous(output) == ('abc', [{'path': '/p'}], 't'), 'previous analysis restored')
    output.write_text('{broken', encoding='utf-8')
    check(agent.load_previous(output) == (None, [], None), 'broken previous output ignored')

    # Transient PermissionError from drvfs must be retried, not crash the agent.
    real_replace = agent.os.replace
    calls = []

    def flaky(source, target):
        calls.append(1)
        if len(calls) < 3:
            raise PermissionError(13, 'Permission denied')
        real_replace(source, target)

    with mock.patch.object(agent.os, 'replace', side_effect=flaky):
        agent.atomic_json(output, {'ok': True})
    check(json.loads(output.read_text(encoding='utf-8')) == {'ok': True} and len(calls) == 3, 'replace retried')
    check(not list(pathlib.Path(directory).glob('*.tmp.*')), 'temporary file cleaned')

print('agent logic tests passed')
