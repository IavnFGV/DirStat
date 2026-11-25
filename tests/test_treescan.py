"""Tree scan tests; run inside WSL: python3 tests/test_treescan.py"""
import os
import pathlib
import sys
import tempfile

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1] / 'wsl'))
import treescan  # noqa: E402


def check(condition, name):
    assert condition, name
    print('PASS', name)


def walk(node):
    yield node
    for child in node.get('c', []):
        yield from walk(child)


check(treescan.extension('movie.MP4') == '.mp4', 'extension lowercased')
check(treescan.extension('libssl.so.3') == '.so', 'versioned library')
check(treescan.extension('libfoo.so.1.2.3') == '.so', 'multi-part version')
check(treescan.extension('README') == treescan.NO_EXTENSION, 'no extension')
check(treescan.extension('.bashrc') == treescan.NO_EXTENSION, 'dotfile has no extension')
check(treescan.extension('archive.tar.gz') == '.gz', 'last suffix')
check(treescan.extension('blob.d41d8cd98f00b204e98') == treescan.NO_EXTENSION, 'hash suffix ignored')

with tempfile.TemporaryDirectory(dir=os.path.expanduser('~')) as directory:
    root = pathlib.Path(directory)
    (root / 'big').mkdir()
    (root / 'big' / 'video.mp4').write_bytes(os.urandom(4 * 1024 * 1024))
    (root / 'big' / 'link.mp4').hardlink_to(root / 'big' / 'video.mp4')
    (root / 'small').mkdir()
    for index in range(40):
        (root / 'small' / f'note{index}.txt').write_bytes(b'x' * 100)
    (root / 'escape').symlink_to('/usr')

    tree, extensions, files, total, errors, _ = treescan.scan(str(root))
    check(files == 42, 'hard link counted once, symlink counted as an entry')
    check(extensions['.mp4'][1] == 1 and extensions['.txt'][1] == 40, 'extension counts')
    check(extensions[treescan.OTHER_TYPE][1] == 1, 'symlink typed as non-file')
    check(len(tree.dirs) == 2, 'symlinked dir not followed')
    check(tree.count == 42, 'recursive file count')

    document = treescan.build(str(root), used_bytes=None, now='t')
    nodes = list(walk(document['tree']))
    check(all(node['s'] == sum(c['s'] for c in node['c']) for node in nodes if 'c' in node), 'children sum to parent')
    check(document['tree']['s'] == document['scannedBytes'], 'root equals scanned bytes')
    video = [node for node in nodes if node.get('n') == 'video.mp4' or node.get('n') == 'link.mp4']
    check(len(video) == 1 and video[0]['k'] == 'f' and video[0]['e'] == '.mp4', 'large file kept as leaf')
    small = next(node for node in document['tree']['c'] if node['n'] == 'small' or node['k'] == 'o')
    check(small['k'] in ('d', 'o'), 'small files folded')
    check(not any(node['k'] == 'f' and node.get('e') == '.txt' for node in nodes), 'tiny files below threshold folded')

    padded = treescan.build(str(root), used_bytes=document['scannedBytes'] + 10 * 1024 * 1024, now='t')
    unaccounted = [node for node in padded['tree']['c'] if node['k'] == 'u']
    check(len(unaccounted) == 1 and padded['unaccountedBytes'] == 10 * 1024 * 1024, 'unaccounted space shown')
    check(padded['tree']['s'] == document['scannedBytes'] + 10 * 1024 * 1024, 'map adds up to used space')

    # Top-K eviction keeps the largest files and folds the rest without losing bytes.
    many = root / 'many'
    many.mkdir()
    for index in range(treescan.KEEP_FILES_PER_DIR + 10):
        (many / f'f{index}.bin').write_bytes(b'y' * (4096 * (index + 1)))
    tree, *_ = treescan.scan(str(many))
    kept = sorted(item[0] for item in tree.top)
    check(len(kept) == treescan.KEEP_FILES_PER_DIR and kept[0] >= 4096 * 11, 'largest files kept')
    check(tree.size == sum(f.stat().st_blocks * 512 for f in many.iterdir()) + 0, 'evicted bytes preserved')

print('treescan tests passed')
