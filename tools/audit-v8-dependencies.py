#!/usr/bin/env python3
"""Read-only, reproducible lexical inventory; never a dead-code detector."""
import argparse
import json
import re
import subprocess
from pathlib import Path
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
FOCUS = ('AgentKernel', 'AgentProgressTracker', 'ModelProtocolClient', 'ModelRequestPreparation',
         'ModelReferenceProjection', 'ModelContextCompiler', 'ContextCompactionRuntime', 'ConversationModelSession', 'ConversationRunService',
         'SkillTaskService', 'SkillLoadToolHandler', 'ChatStore', 'ToolRuntime', 'LlmClient',
         'WorkspaceConversationService', 'AssistantController', 'AssistantWebBridge',
         'BridgeOperationCatalog', 'OfficeToolExecutor', 'NativeToolRuntimeAdapter',
         'PromptContextInspectorService', 'ModelContextInspectorService', 'app-')

def inventory():
    tracked = subprocess.check_output(['git', 'ls-files'], cwd=ROOT, text=True).splitlines()
    projects, sources = [], []
    for name in tracked:
        path = ROOT / name
        if path.suffix in ('.csproj', '.projitems'):
            tree = ET.parse(path)
            edges = []
            for element in tree.iter():
                kind = element.tag.rsplit('}', 1)[-1]
                if kind in ('ProjectReference', 'Import', 'Compile'):
                    target = element.get('Include') or element.get('Project')
                    if target:
                        normalized = target.replace('\\', '/')
                        resolved = None
                        if '$(' not in normalized and '*' not in normalized:
                            resolved = str((path.parent / normalized).resolve().relative_to(ROOT))
                        edges.append(dict(kind=kind, target=target, resolved=resolved,
                                          condition=element.get('Condition')))
            projects.append(dict(path=name, edges=edges))
        if path.suffix not in ('.cs', '.js') or not name.startswith(('src/', 'web/js/')):
            continue
        if not path.name.startswith(FOCUS):
            continue
        rows = []
        for line_no, line in enumerate(path.read_text(encoding='utf-8-sig').splitlines(), 1):
            declarations = re.findall(r'\b(?:class|interface|enum|function)\s+([\w$]+)', line)
            declarations += re.findall(r'\b(?:public|private|internal|protected)\s+(?:[\w<>,?\[\].]+\s+)*([\w$]+)\s*\(', line)
            calls = sorted(set(re.findall(r'\b([A-Za-z_$][\w$]*(?:\.[\w$]+)*)\s*\(', line)) -
                           {'if', 'for', 'foreach', 'while', 'switch', 'catch', 'using', 'typeof', 'nameof'})
            dynamic = bool(re.search(r'GetMethod|GetType\(|Invoke\(|Activator|Reflection|eval\(|\bdynamic\b', line))
            routes = bool(re.search(r'Register\(|addEventListener|removeEventListener|\.on\(|\.off\(|\+=|-=|JsonProperty|case\s+"|window\.', line))
            if declarations or calls or dynamic or routes:
                rows.append(dict(line=line_no, declarations=declarations, callCandidates=calls,
                                 dynamic=dynamic, registrationOrSubscription=routes))
        sources.append(dict(path=name, sha256=__import__('hashlib').sha256(path.read_bytes()).hexdigest(), rows=rows))
    graph = {p['path']: [e['resolved'] for e in p['edges'] if e['kind']=='ProjectReference' and e['resolved']]
             for p in projects}
    cycles = []
    def visit(node, chain):
        if node in chain:
            cycle = chain[chain.index(node):] + [node]
            if cycle not in cycles: cycles.append(cycle)
            return
        for target in graph.get(node, []): visit(target, chain + [node])
    for node in graph: visit(node, [])
    return dict(baseline=subprocess.check_output(['git','rev-parse','HEAD'], cwd=ROOT,text=True).strip(),
        scope='Tracked projects plus focused runtime owners and app-*.js, current worktree bytes',
        limitations=['Lexical candidates, not Roslyn semantic call resolution',
                    'MSBuild conditions and property imports are retained, not evaluated',
                    'Dynamic/reflection/bridge/subscription candidates require manual review',
                    'Absence of an edge does not prove unused code; no dead-code inference'],
        projects=projects, projectReferenceCycles=cycles, sources=sources)

if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('output', type=Path)
    args = parser.parse_args()
    args.output.write_text(json.dumps(inventory(), ensure_ascii=False, separators=(',', ':'))+'\n', encoding='utf-8')
