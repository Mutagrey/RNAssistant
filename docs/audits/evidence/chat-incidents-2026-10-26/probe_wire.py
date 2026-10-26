import hashlib, json, os, subprocess, sys, tempfile, threading
from pathlib import Path
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from jsonschema import Draft202012Validator
repo=Path(__file__).resolve().parents[4]
sys.path.insert(0,str(repo/'tests/cli'))
from conversation_transport import conversation_content, finish, reply_to_title
cli=repo/'src/RNAssistant.Cli/bin/Debug/net8.0/RNAssistant.Cli.dll'
out=Path(sys.argv[1]) if len(sys.argv)>1 else Path(tempfile.mkdtemp(prefix='rna-incident-wire-'))
out.mkdir(parents=True,exist_ok=True)
report={'head':subprocess.check_output(['git','rev-parse','HEAD'],cwd=repo,text=True).strip(),'binarySha256':hashlib.sha256(cli.read_bytes()).hexdigest(),'qualification':'Existing CLI binary; no fresh build or complete proof of source equivalence. Scripted loopback HTTP; no real provider or Office.','requestRepresentation':'Decoded synthetic HTTP JSON; no claim of byte-for-byte CAS comparison in this probe.','runs':[]}
for mode in ('json_object','json_schema'):
 requests=[]
 class Handler(BaseHTTPRequestHandler):
  def do_POST(self):
   req=json.loads(self.rfile.read(int(self.headers['Content-Length'])))
   if reply_to_title(self,req): return
   requests.append(req)
   if len(requests)==1:
    logical={'action':'execute','reason':'Inspect current source.','tool_calls':[{'name':'common.resources_read','arguments':{'target':'app.js'}}],'message':'Inspecting app.js.'}
   else: logical=finish(req,'Created script.js.','satisfied')
   body=json.dumps({'choices':[{'message':{'role':'assistant','content':conversation_content(req,logical)},'finish_reason':'stop'}]}).encode()
   self.send_response(200);self.send_header('Content-Type','application/json');self.send_header('Content-Length',str(len(body)));self.end_headers();self.wfile.write(body)
  def log_message(self,*_):pass
 server=ThreadingHTTPServer(('127.0.0.1',0),Handler)
 thread=threading.Thread(target=server.serve_forever,daemon=True);thread.start()
 try:
  with tempfile.TemporaryDirectory(prefix='rna-incident-') as tmp:
   root=Path(tmp);workspace=root/'workspace';workspace.mkdir();(workspace/'app.js').write_text('const original = true;\n')
   env=dict(os.environ,RNA_STATE_ROOT=str(root/'state'),RNA_BASE_URL=f'http://127.0.0.1:{server.server_port}',RNA_MODEL='scripted',RNA_API_KEY='local-test-only')
   result=subprocess.run(['dotnet',str(cli),'run','--workspace',str(workspace),'--message','Create script.js with a working drawing interpreter.','--response-mode',mode,'--max-iterations','3','--max-tool-steps','2','--auto-confirm','on','--jsonl'],cwd=repo,env=env,text=True,capture_output=True,timeout=45)
   rows=[json.loads(line) for line in result.stdout.splitlines() if line.startswith('{')]
   # Persist only synthetic traffic, no user data or auth headers.
   (out/(mode+'-requests.json')).write_text(json.dumps(requests,ensure_ascii=False,indent=2))
   (out/(mode+'-output.json')).write_text(json.dumps(rows,ensure_ascii=False,indent=2))
   item={'mode':mode,'exitCode':result.returncode,'httpRequests':len(requests),'scriptFileExists':(workspace/'script.js').exists(),'files':[p.name for p in workspace.iterdir() if p.is_file()],'stderr':result.stderr}
   terminal=next((r['data'] for r in reversed(rows) if r.get('type')=='run.completed'),{})
   item['terminal']={key:terminal.get(key) for key in ('lifecycle','reason','health','AssistantMessage','ToolCounts','acceptance','verifiedFileChanges')}
   item['requestContents']=[]
   for index,request in enumerate(requests,1):
    contents=[m.get('content','') for m in request['messages']]
    if not all(isinstance(c,str) for c in contents):
     raise ValueError('This synthetic text-only probe does not measure multimodal content.')
    item['requestContents'].append({'request':index,'messages':len(contents),'messageTextCharacters':sum(len(c) for c in contents),'identicalWholeMessageDuplicates':len(contents)-len(set(contents)),'literalSourceOccurrences':sum(c.count('const original = true;') for c in contents)})
   if mode=='json_object':
    catalog=None
    for msg in requests[0]['messages']:
     text=msg.get('content','')
     marker='Available tools and exact argument schemas: '
     if isinstance(text,str) and marker in text: catalog=json.JSONDecoder().raw_decode(text.split(marker,1)[1])[0];break
    (out/'advertised-schemas.json').write_text(json.dumps([t for t in catalog if t['Id'] in ('common.resources_read','common.resources_search')],ensure_ascii=False,indent=2))
    item['schemaCounterexamples']=[]
    for name,args in [('common.resources_read',{'target':'app.js'}),('common.resources_search',{'target':'app.js','query':'original'})]:
     schema=next(t['Schema'] for t in catalog if t['Id']==name)
     errors=[e.message for e in Draft202012Validator(schema).iter_errors(args)]
     item['schemaCounterexamples'].append({'tool':name,'arguments':args,'validAccordingToAdvertisedSchema':not errors,'errors':errors})
   else:
    schema=requests[0]['response_format']['json_schema']['schema'];Draft202012Validator.check_schema(schema)
    item['strictRoot']={'type':schema.get('type'),'required':schema.get('required'),'rootAnyOf':'anyOf' in schema}
   report['runs'].append(item)
 finally: server.shutdown();server.server_close();thread.join()
(out/'wire-report.json').write_text(json.dumps(report,ensure_ascii=False,indent=2))
print(json.dumps(report,ensure_ascii=False,indent=2))
