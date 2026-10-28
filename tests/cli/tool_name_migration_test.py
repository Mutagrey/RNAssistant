"""Pure temporary-fixture test; never accesses installed user packages."""
import importlib.util
import json
from pathlib import Path
import tempfile

root = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('migrate_names', root / 'tools/migrate-tool-names.py')
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
names = json.loads((root / 'docs/tool-name-migration.json').read_text())
with tempfile.TemporaryDirectory() as directory:
    path = Path(directory) / 'SKILL.md'
    manifest = {'id': 'user_existing_id', 'requiredTools': [{'id': 'common.vba_write_module', 'revision': 'old'}],
                'optionalTools': [{'id': 'files.create'}, {'id': 'user.custom_tool', 'revision': 'keep'}]}
    original = ('\ufeff---\r\nname: old_id\r\nrnassistant: ' + json.dumps(manifest) + '\r\n---\r\nBody common.vba_write_module unchanged.\r\n').encode('utf-8')
    path.write_bytes(original)
    assert module.migrate(path, names) and path.read_bytes() == original
    assert module.migrate(path, names, True)
    changed = path.read_bytes().decode('utf-8-sig')
    migrated = json.loads(changed.split('rnassistant: ', 1)[1].split('\r\n', 1)[0])
    assert migrated['id'] == manifest['id']
    assert migrated['requiredTools'] == [{'id': 'core.write', 'contractVersion': '1'}]
    assert migrated['optionalTools'] == [{'id': 'user.custom_tool', 'revision': 'keep'}]
    assert changed.endswith('Body common.vba_write_module unchanged.\r\n')
    assert path.with_name('SKILL.md.before-tool-names-v1').read_bytes() == original
    assert not module.migrate(path, names, True)
print('PASS tool names: dry-run, backup, BOM/CRLF/body/custom IDs, dependency deduplication and idempotence')
