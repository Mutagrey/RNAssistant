#!/usr/bin/env python3
"""Bounded paired profile pilot. Synthetic workspaces only; never changes model tags/defaults.
Build Harness once, then run with a new evidence directory. --scripted validates plumbing.
"""
import argparse
import hashlib
import http.server
import json
import os
from pathlib import Path
import subprocess
import threading
import time
import urllib.error
import urllib.request

ROOT = Path(__file__).resolve().parents[2]
DLL = ROOT / 'tests/RNAssistant.Harness/bin/Debug/net8.0/RNAssistant.Harness.dll'
MODELS = [('cloud', 'gemma4:cloud', 'json_object'), ('local', 'rna-gemma4-12b-32k:latest', 'json_schema')]

def write_json(path, value):
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding='utf-8')

def fetch(url):
    with urllib.request.urlopen(url, timeout=8) as response:
        return json.load(response)

def scripted_response(request, scenario, index):
    edit = [('core.read', {'target': 'src/reports/invoice.ts'}),
            ('core.patch', {'targets': [{'target': 'src/reports/invoice.ts', 'edits': [{'before': '  return "Old invoice";', 'after': '  return "Monthly invoice";'}]}]})]
    copy = [('core.read', {'target': 'assets/templates/письмо.txt'}),
            ('core.tools.read', {'id': 'resources.copy'}),
            ('resources.copy', {'target': 'assets/templates/письмо.txt', 'destination': 'exports/письмо-копия.txt'})]
    steps = edit if scenario == 'edit' else copy
    if index <= len(steps):
        name, arguments = steps[index - 1]
        result = dict(action='execute', reason='Perform the fixture step.', message='Inspect and update.',
                      tool_calls=[dict(name=name, arguments=arguments)])
    else:
        evidence = []
        for message in request['messages']:
            content = message.get('content', '')
            if isinstance(content, str) and (content.startswith('TOOL_RESULT:\n') or message['role'] == 'tool'):
                result = json.loads(content.removeprefix('TOOL_RESULT:\n'))
                if result.get('status') == 'ok':
                    evidence.append(result['tool_call_id'])
        result = dict(action='finish', reason='Report verified fixture outcomes.', outcome='completed', message='Готово.',
                      results=[dict(summary='Requested file change verified.', evidence=evidence, assessment='satisfied')])
    if request.get('response_format', {}).get('type') == 'json_schema':
        result = {'response': result}
    return json.dumps({'choices': [{'message': {'content': json.dumps(result, ensure_ascii=False)}}]}, ensure_ascii=False).encode()

class Proxy(http.server.BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def do_POST(self):
        state = self.server.run_state
        with state['lock']:
            state['count'] += 1
            index = state['count']
        payload = self.rfile.read(int(self.headers['Content-Length']))
        wire = state['directory'] / 'wire'
        (wire / f'{index:03}-request.json').write_bytes(payload)
        started = time.monotonic()
        status = 200
        try:
            if self.server.scripted:
                body = scripted_response(json.loads(payload), state['scenario'], index)
            else:
                req = urllib.request.Request(self.server.upstream + self.path, data=payload,
                                             headers={'Content-Type': 'application/json'})
                with urllib.request.urlopen(req, timeout=120) as response:
                    body = response.read()
        except urllib.error.HTTPError as error:
            status, body = error.code, error.read()
        except Exception as error:
            status, body = 502, json.dumps({'error': str(error)}).encode()
        (wire / f'{index:03}-response.json').write_bytes(body)
        write_json(wire / f'{index:03}-transport.json', {'status': status, 'seconds': time.monotonic() - started})
        self.send_response(status)
        self.send_header('Content-Type', 'application/json')
        self.send_header('Content-Length', str(len(body)))
        self.end_headers()
        try:
            self.wfile.write(body)
        except (BrokenPipeError, ConnectionResetError):
            pass

def metrics(directory):
    requests = sorted((directory / 'wire').glob('*-request.json'))
    responses = sorted((directory / 'wire').glob('*-response.json'))
    usages = [json.loads(path.read_text()).get('usage') for path in responses]
    measured = [item for item in usages if item]
    acceptance_path = directory / 'agent/acceptance.json'
    result = json.loads(acceptance_path.read_text()) if acceptance_path.exists() else {'passed': False, 'failure': 'missing acceptance'}
    result.update(http_calls=len(requests), first_request_bytes=requests[0].stat().st_size if requests else None,
                  request_bytes=sum(path.stat().st_size for path in requests), usage_responses=len(measured),
                  prompt_tokens=sum(item.get('prompt_tokens', 0) for item in measured) if measured else None,
                  completion_tokens=sum(item.get('completion_tokens', 0) for item in measured) if measured else None)
    return result

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('evidence', type=Path)
    parser.add_argument('--scripted', action='store_true')
    parser.add_argument('--model', choices=['cloud', 'local', 'both'], default='both')
    parser.add_argument('--upstream', default='http://127.0.0.1:11434')
    args = parser.parse_args()
    out = args.evidence.resolve()
    out.mkdir(parents=True, exist_ok=False)
    models = [('scripted', 'scripted', 'json_schema')] if args.scripted else [item for item in MODELS if args.model in ('both', item[0])]
    tags = {} if args.scripted else {item['name']: item['digest'] for item in fetch(args.upstream + '/api/tags')['models']}
    manifest = dict(source=subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip(),
                    harness_sha256=hashlib.sha256(DLL.read_bytes()).hexdigest(), scripted=args.scripted,
                    models=models, digests=tags, upstream=args.upstream,
                    limits={'context': 32768, 'output': 4096, 'iterations': 12, 'tools': 20, 'seconds': 240})
    write_json(out / 'manifest.json', manifest)
    patch = subprocess.check_output(['git', 'diff', 'HEAD'], cwd=ROOT)
    for name in subprocess.check_output(['git', 'ls-files', '--others', '--exclude-standard', '--', 'src', 'tests'], cwd=ROOT, text=True).splitlines():
        if Path(name).suffix in ('.cs', '.csproj', '.py'):
            diff = subprocess.run(['git', 'diff', '--no-index', '--', '/dev/null', name], cwd=ROOT, capture_output=True)
            if diff.returncode not in (0, 1):
                raise RuntimeError(diff.stderr.decode())
            patch += diff.stdout
    (out / 'source.patch').write_bytes(patch)
    # New diagnostic sources may be untracked before the pilot's evidence commit.
    for path in ['tests/RNAssistant.Harness/Program.ToolProfileDiagnostic.cs', 'tests/cli/tool_profile_comparison.py']:
        (out / Path(path).name).write_bytes((ROOT / path).read_bytes())
    server = http.server.ThreadingHTTPServer(('127.0.0.1', 0), Proxy)
    server.scripted, server.upstream = args.scripted, args.upstream.rstrip('/')
    threading.Thread(target=server.serve_forever, daemon=True).start()
    rows = []
    try:
        for label, model, mode in models:
            if not args.scripted and model not in tags:
                raise RuntimeError('Pinned model not installed: ' + model)
            if not args.scripted:
                running = fetch(args.upstream + '/api/ps')
                write_json(out / f'{label}-models-before.json', running)
                if label == 'local' and any(item['name'] != model for item in running['models']):
                    raise RuntimeError('Another model is loaded; refusing to disturb an external workload.')
            for scenario in ['edit', 'copy']:
                order = ['broad', 'compact'] if (scenario == 'edit') == (label != 'local') else ['compact', 'broad']
                for profile in order:
                    name = f'{label}-{scenario}-{profile}'
                    directory = out / name
                    (directory / 'wire').mkdir(parents=True)
                    server.run_state = dict(directory=directory, scenario=scenario, count=0, lock=threading.Lock())
                    env = os.environ.copy()
                    env.update(RNA_BASE_URL=f'http://127.0.0.1:{server.server_port}/v1', RNA_MODEL=model,
                               RNA_RESPONSE_MODE=mode, RNA_MODEL_DIGEST=tags.get(model, 'scripted'))
                    print('START ' + name, flush=True)
                    with (directory / 'console.log').open('w') as log:
                        try:
                            run = subprocess.run(['dotnet', str(DLL), '--workspace-profile-live', str(directory / 'agent'), profile, scenario],
                                                 cwd=ROOT, env=env, stdout=log, stderr=subprocess.STDOUT, timeout=270)
                            code = run.returncode
                        except subprocess.TimeoutExpired:
                            code = 124
                    row = dict(name=name, exit_code=code, **metrics(directory))
                    rows.append(row)
                    write_json(out / 'results.json', rows)
                    print('END ' + name + ' ' + json.dumps({key: row.get(key) for key in ['passed', 'taskPassed', 'http_calls', 'calls', 'seconds']}), flush=True)
                pair = [out / f'{label}-{scenario}-{profile}' / 'agent' for profile in order]
                for file in ['task.txt', 'corpus.json']:
                    if (pair[0] / file).read_bytes() != (pair[1] / file).read_bytes():
                        raise RuntimeError('Paired fixture mismatch: ' + file)
            if not args.scripted:
                write_json(out / f'{label}-models-after.json', fetch(args.upstream + '/api/ps'))
    finally:
        server.shutdown()
        write_json(out / 'sha256.json', {str(path.relative_to(out)): hashlib.sha256(path.read_bytes()).hexdigest()
                                        for path in sorted(out.rglob('*')) if path.is_file() and path.name != 'sha256.json'})
    return 0 if all(row.get('passed') for row in rows) else 1

if __name__ == '__main__':
    raise SystemExit(main())
