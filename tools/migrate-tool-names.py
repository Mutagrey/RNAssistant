#!/usr/bin/env python3
"""Explicit migration of structured skill dependencies. Dry-run unless --apply.
Never edits custom IDs, source bodies, references, chats or runtime admissions.
"""
import argparse
import json
from pathlib import Path
import os
import tempfile


def migrate(path, names, apply=False):
    original = path.read_bytes()
    text = original.decode('utf-8-sig')
    lines = text.splitlines(keepends=True)
    if not lines or lines[0].strip() != '---':
        return False
    changed = False
    for index in range(1, len(lines)):
        line = lines[index]
        if line.strip() == '---':
            break
        if not line.startswith('rnassistant:'):
            continue
        manifest = json.loads(line.split(':', 1)[1])
        seen = set()
        for field in ('requiredTools', 'optionalTools'):
            dependencies = manifest.get(field, [])
            result = []
            for dep in dependencies:
                old = dep['id']
                new = names.get(old, old)
                if old != new:
                    dep['id'] = new
                    # Contract pins name a retired descriptor and cannot be reused.
                    dep.pop('revision', None)
                    dep['contractVersion'] = '1'
                    changed = True
                if new not in seen:
                    result.append(dep)
                    seen.add(new)
                else:
                    changed = True
            if field in manifest:
                manifest[field] = result
        eol = '\r\n' if line.endswith('\r\n') else '\n' if line.endswith('\n') else ''
        lines[index] = 'rnassistant: ' + json.dumps(manifest, ensure_ascii=False, separators=(',', ':')) + eol
    if not changed:
        return False
    if apply:
        backup = path.with_name(path.name + '.before-tool-names-v1')
        # Exclusive creation keeps the first original, including encoding and line endings.
        if not backup.exists():
            with backup.open('xb') as stream:
                stream.write(original)
        updated = ''.join(lines).encode('utf-8')
        if original.startswith(b'\xef\xbb\xbf'):
            updated = b'\xef\xbb\xbf' + updated
        fd, temporary = tempfile.mkstemp(prefix='.tool-names-', dir=path.parent)
        try:
            with os.fdopen(fd, 'wb') as stream:
                stream.write(updated)
                stream.flush()
                os.fsync(stream.fileno())
            os.replace(temporary, path)
        finally:
            if os.path.exists(temporary):
                os.unlink(temporary)
    return True


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('skills', type=Path, help='Directory containing custom SKILL.md packages')
    parser.add_argument('--apply', action='store_true')
    args = parser.parse_args()
    names = json.loads((Path(__file__).resolve().parents[1] / 'docs/tool-name-migration.json').read_text())
    for path in sorted(args.skills.rglob('SKILL.md')):
        if path.is_symlink() or any(parent.is_symlink() for parent in path.parents):
            raise ValueError('Refusing symlink package: ' + str(path))
        if migrate(path, names, args.apply):
            print(('Migrated ' if args.apply else 'Would migrate ') + str(path))


if __name__ == '__main__':
    main()
