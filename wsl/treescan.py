"""Disk usage tree for the treemap view. Standard library only.

The full tree of a Linux home can hold millions of files, far too many to ship
to the Windows app. The scan therefore keeps only the largest files of each
directory while walking, then emits every directory and file above a size
threshold; everything smaller is folded into one "small items" node per
directory. Sizes are allocated bytes (st_blocks), not apparent sizes, so sparse
files and hard links do not inflate the picture.
"""
import heapq
import os
import stat as st
import time

KEEP_FILES_PER_DIR = 24
MAX_NODES_HINT = 60000
MIN_THRESHOLD = 64 * 1024
MAX_EXTENSIONS = 300
NO_EXTENSION = '(без расширения)'
OTHER_TYPE = '(не файл)'
OTHER_EXTENSIONS = '(прочие типы)'


def extension(name):
    parts = name.lower().split('.')
    # libfoo.so.1.2 -> .so: numeric version suffixes are not a type.
    while len(parts) > 2 and parts[-1].isdigit():
        parts.pop()
    if len(parts) < 2 or not parts[0] and len(parts) == 2 or not parts[-1]:
        return NO_EXTENSION  # "README", ".bashrc", "name."
    ext = '.' + parts[-1]
    # Hash-like suffixes (cache entries, object stores) are not real types.
    return ext if len(ext) <= 10 else NO_EXTENSION


class _Dir:
    __slots__ = ('name', 'dirs', 'top', 'small', 'small_count', 'files', 'size', 'count')

    def __init__(self, name):
        self.name = name
        self.dirs = []
        self.top = []          # min-heap of (size, name, ext): the largest files
        self.small = 0         # bytes of files evicted from top, plus own inode blocks
        self.small_count = 0
        self.files = 0
        self.size = 0
        self.count = 0


def scan(root, progress=None, progress_interval=0.5):
    """Walk root without leaving its filesystem. Returns the raw tree and stats."""
    device = os.lstat(root).st_dev
    seen_links = set()
    extensions = {}
    errors = []
    error_count = 0
    files = 0
    total = 0
    top = _Dir(root)
    all_dirs = [top]
    stack = [(root, top)]
    last_report = time.monotonic()
    while stack:
        path, node = stack.pop()
        try:
            iterator = os.scandir(path)
        except OSError as error:
            error_count += 1
            if len(errors) < 20:
                errors.append(f'{path}: {error.strerror or error}')
            continue
        with iterator:
            for entry in iterator:
                try:
                    info = entry.stat(follow_symlinks=False)
                except OSError:
                    error_count += 1
                    continue
                size = info.st_blocks * 512
                if st.S_ISDIR(info.st_mode):
                    if info.st_dev != device:
                        continue  # another filesystem: /mnt/c, /proc, /usr/lib/wsl...
                    child = _Dir(entry.name)
                    child.small = size  # the directory's own blocks
                    total += size
                    node.dirs.append(child)
                    all_dirs.append(child)
                    stack.append((entry.path, child))
                    continue
                if info.st_nlink > 1:
                    key = (info.st_dev, info.st_ino)
                    if key in seen_links:
                        continue
                    seen_links.add(key)
                files += 1
                total += size
                node.files += 1
                ext = extension(entry.name) if st.S_ISREG(info.st_mode) else OTHER_TYPE
                stat = extensions.get(ext)
                if stat is None:
                    extensions[ext] = [size, 1]
                else:
                    stat[0] += size
                    stat[1] += 1
                item = (size, entry.name, ext)
                if len(node.top) < KEEP_FILES_PER_DIR:
                    heapq.heappush(node.top, item)
                else:
                    evicted = heapq.heappushpop(node.top, item)
                    node.small += evicted[0]
                    node.small_count += 1
        if progress is not None and time.monotonic() - last_report >= progress_interval:
            progress(files, total)
            last_report = time.monotonic()
    # all_dirs lists parents before children, so reverse order is bottom-up.
    for directory in reversed(all_dirs):
        directory.size = directory.small + sum(item[0] for item in directory.top) + \
            sum(child.size for child in directory.dirs)
        directory.count = directory.files + sum(child.count for child in directory.dirs)
    return top, extensions, files, total, error_count, errors


def _emit(directory, threshold):
    node = {'n': directory.name, 's': directory.size, 'k': 'd', 'fc': directory.count}
    children = []
    small = directory.small
    small_count = directory.small_count
    for child in directory.dirs:
        if child.size >= threshold:
            children.append(_emit(child, threshold))
        else:
            small += child.size
            small_count += child.count
    for size, name, ext in directory.top:
        if size >= threshold:
            children.append({'n': name, 's': size, 'k': 'f', 'e': ext})
        else:
            small += size
            small_count += 1
    if small > 0:
        children.append({'n': '', 's': small, 'k': 'o', 'fc': small_count})
    children.sort(key=lambda item: item['s'], reverse=True)
    if children:
        node['c'] = children
    return node


def build(root, used_bytes=None, progress=None, now=None):
    """Scan root and return the JSON-ready document for the Windows app."""
    started = time.monotonic()
    tree, extensions, files, total, error_count, errors = scan(root, progress)
    threshold = max(MIN_THRESHOLD, total // MAX_NODES_HINT)
    node = _emit(tree, threshold)
    # Space the walk could not see (unreadable dirs, ext4 metadata) still fills
    # the disk; show it so the map adds up to what df reports.
    unaccounted = (used_bytes - total) if used_bytes is not None else 0
    if unaccounted > threshold:
        node.setdefault('c', []).append({'n': '', 's': unaccounted, 'k': 'u'})
        node['c'].sort(key=lambda item: item['s'], reverse=True)
        node['s'] += unaccounted
    ranked = sorted(extensions.items(), key=lambda item: item[1][0], reverse=True)
    ext_list = [{'ext': ext, 'size': size, 'count': count} for ext, (size, count) in ranked[:MAX_EXTENSIONS]]
    rest = ranked[MAX_EXTENSIONS:]
    if rest:
        ext_list.append({'ext': OTHER_EXTENSIONS, 'size': sum(item[1][0] for item in rest),
                         'count': sum(item[1][1] for item in rest)})
    return {
        'schemaVersion': 1,
        'root': root,
        'timestamp': now,
        'durationSeconds': round(time.monotonic() - started, 2),
        'scannedBytes': total,
        'scannedFiles': files,
        'unaccountedBytes': max(0, unaccounted),
        'errorCount': error_count,
        'errorSamples': errors,
        'thresholdBytes': threshold,
        'extensions': ext_list,
        'tree': node,
    }
