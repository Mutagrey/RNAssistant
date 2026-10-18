'use strict';
const assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),os=require('node:os');
const {chromium}=require('playwright');
const web=path.resolve(__dirname,'../../web'),read=name=>fs.readFileSync(path.join(web,name));
const captures=process.env.RNA_PLUGIN_SCREENSHOTS || path.join(os.tmpdir(),'rnassistant-plugin-ui');fs.mkdirSync(captures,{recursive:true});
(async()=>{
  const browser=await chromium.launch({headless:true,...(process.env.BROWSER_EXECUTABLE?{executablePath:process.env.BROWSER_EXECUTABLE}:{})});
  const context=await browser.newContext();let sequence=0;const realms=new Map(),errors=[],network=[];
  await context.addInitScript(()=>{
    if(!location.hostname.endsWith('.rnassistant-plugin.invalid'))return;
    const listeners=[];window.chrome.webview={addEventListener:(name,fn)=>listeners.push(fn),
      postMessage:async message=>{try{const reply=await window.nativePluginMessage(JSON.parse(message));if(reply)listeners.forEach(fn=>fn({data:reply}));}catch(error){console.error(error.message);}}};
  });
  async function rpc(realm,request){
    const response={version:1,requestId:request.requestId,ok:true,result:{}};
    switch(request.method){
      case 'Ready':response.result={handle:'data-test'};break;
      case 'Invoke':response.result={json:await execute(realm.files,request.command,request.argumentsJson)};break;
      case 'OpenFile':{assert.equal(request.projectId,'data-test');if(request.path==='results/output.json'){response.ok=false;response.errorCode='not_found';break;}const bytes=read('data-templates/core/examples/matrix-source.xlsx');realm.input=bytes;response.result={handle:'source',name:'matrix-source.xlsx',byteLength:bytes.length,url:realm.origin+'/.data/read/source'};break;}
      case 'CloseFile':break;
      case 'BeginResult':realm.resultName=request.path;realm.output=[];response.result={handle:'out',url:realm.origin+'/.result/write/out'};break;
      case 'CommitResult':realm.result=Buffer.concat(realm.output);response.result={handle:'completed',name:realm.resultName,byteLength:realm.result.length};break;
      case 'BeginWrite':assert.equal(request.path,'results/output.json');realm.output=[];response.result={handle:'out',url:realm.origin+'/.data/write/out'};break;
      case 'CommitWrite':realm.result=Buffer.concat(realm.output);response.result={byteLength:realm.result.length};break;
      case 'AbortWrite':break;
      default:throw Error('Unexpected RPC '+request.method);
    }return response;
  }
  await context.exposeBinding('nativePluginMessage',async ({frame},message)=>{
    const realm=realms.get(new URL(frame.url()).origin);assert.ok(realm,'bound browser realm');
    if(!message.type)return rpc(realm,message);
    if(message.type==='ready')return {type:'start',entry:'commands.js',command:realm.command,argumentsJson:realm.inputJson};
    if(message.type==='rpc')return rpc(realm,message.request);
    if(message.type==='complete'){realm.resolve(message.json);return;}
    if(message.type==='failed'){realm.reject(new Error(message.message));return;}
    if(message.type==='progress'){realm.progress++;return;}
    throw Error('Unknown worker envelope');
  });
  await context.route('https://*.rnassistant-plugin.invalid/**',async route=>{
    const url=new URL(route.request().url()),realm=realms.get(url.origin),name=decodeURIComponent(url.pathname.slice(1));
    if(!realm){await route.fulfill({status:403,body:''});return;}
    let body;
    if(name==='.data/read/source'){const offset=Number(url.searchParams.get('offset')),count=Number(url.searchParams.get('count'));assert.ok(count<=262144);body=realm.input.subarray(offset,offset+count);}
    else if(name==='.result/write/out'||name==='.data/write/out'){const bytes=route.request().postDataBuffer();assert.ok(bytes.length<=262144);assert.equal(Number(url.searchParams.get('offset')),Buffer.concat(realm.output).length);realm.output.push(bytes);body=Buffer.alloc(0);}
    else if(name.startsWith('.host/'))body=read('plugin-host/'+name.slice(6));
    else body=realm.files(name);
    assert.ok(body,'manifest member '+name);
    await route.fulfill({status:200,body,contentType:name.endsWith('.html')?'text/html':name.endsWith('.js')?'text/javascript':name.endsWith('.css')?'text/css':'application/octet-stream',headers:{'Content-Security-Policy':"default-src 'none'; script-src 'self'; connect-src 'self'; worker-src 'self'; style-src 'self'"}});
  });
  async function execute(files,command,inputJson){
    const origin='https://operation-'+(++sequence)+'.rnassistant-plugin.invalid';let resolve,reject;const done=new Promise((a,b)=>{resolve=a;reject=b;});
    const realm={origin,files,command,inputJson,resolve,reject,progress:0};realms.set(origin,realm);
    const page=await context.newPage();page.on('pageerror',error=>reject(error));
    let timer;
    try{await page.goto(origin+'/.host/runner.html');const json=await Promise.race([done,new Promise((_,fail)=>{timer=setTimeout(()=>fail(Error('Worker deadline')),15000);})]);execute.lastRealm=realm;return json;}
    finally{clearTimeout(timer);await page.close();realms.delete(origin);}
  }
  try{
    const fixture=version=>name=>({
      'index.html':Buffer.from('<!doctype html><title>Example plugin</title><script defer src="/.host/sdk.js"></script><script defer src="app.js"></script><h1>Example plugin</h1><button id="run">Run command</button><output id="answer"></output>'),
      'app.js':Buffer.from('document.getElementById("run").onclick=async()=>{document.getElementById("answer").textContent=(await RNPlugin.invoke("echo",{text:"hello"})).text;};'),
      'commands.js':Buffer.from('RNPluginCommands.register("echo",async args=>({text:args.text+" '+version+'"}));')
    })[name];
    assert.deepEqual(JSON.parse(await execute(fixture('v1'),'echo','{"text":"worker"}')),{text:'worker v1'});
    const page=await context.newPage();page.on('pageerror',error=>errors.push(error.message));
    await page.goto('file://'+path.join(web,'index.html'));assert.equal(await page.title(),'RN Assistant');
    let active=null,candidate=null,version=1;
    const item={pluginId:'example',name:'Example',sourceWorkspaceId:'source-1',sourceRoot:'C:\\Plugins\\Example',releaseId:null,enabled:false};
    await page.exposeFunction('testPluginHost',async (type,payload)=>{
      switch(type){
        case 'getPlugins':return {items:[{...item}]};
        case 'createPlugin':assert.equal(payload.name,'Example');return {...item};
        case 'getPluginChat':return {messages:[{role:'assistant',text:'Исходники готовы к изменению.'}],state:{Status:'idle'}};
        case 'checkPlugin':candidate='candidate-'+version;return {candidateId:candidate,files:3,bytes:100};
        case 'applyPlugin':assert.equal(payload.candidateId,candidate);active=version;item.releaseId='release-'+version;item.enabled=true;return {...item};
        case 'openPlugin':{const origin='https://view-'+(++sequence)+'.rnassistant-plugin.invalid';realms.set(origin,{origin,files:fixture(payload.candidateId?'preview-v'+version:'v'+active)});return {instanceId:origin,entryUrl:origin+'/index.html',releaseId:'release-'+version,preview:!!payload.candidateId};}
        case 'closePlugin':realms.delete(payload.instanceId);return {};
        case 'disablePlugin':item.enabled=false;return {...item};
        default:throw Error('Unexpected host call '+type);
      }
    });
    await page.evaluate(()=>{window.send=window.testPluginHost;return RNAssistantPlugins.refresh();});
    await page.locator('#pluginAdd').click();await page.locator('#pluginName').fill('Example');await page.locator('#pluginRoot').fill('C:\\Plugins\\Example');await page.locator('#pluginCreateForm button[type=submit]').click();
    await page.locator('#pluginCheck').click();await page.locator('#pluginPreview').click();
    await page.frameLocator('#pluginPreviewMount iframe').locator('#run').click();await page.frameLocator('#pluginPreviewMount iframe').locator('#answer').filter({hasText:'hello preview-v1'}).waitFor();
    await page.locator('#pluginApply').click();await page.locator('#pluginReopen').click();
    await page.frameLocator('#pluginPreviewMount iframe').locator('#run').click();await page.frameLocator('#pluginPreviewMount iframe').locator('#answer').filter({hasText:'hello v1'}).waitFor();
    version=2;await page.locator('#pluginCheck').click();await page.locator('#pluginApply').click();
    assert.equal(await page.frameLocator('#pluginPreviewMount iframe').locator('#answer').textContent(),'hello v1','apply preserves the pinned visible instance');
    await page.locator('#pluginReopen').click();await page.frameLocator('#pluginPreviewMount iframe').locator('#run').click();await page.frameLocator('#pluginPreviewMount iframe').locator('#answer').filter({hasText:'hello v2'}).waitFor();
    await page.screenshot({path:path.join(captures,'plugins-desktop.png')});
    await page.setViewportSize({width:390,height:844});await page.screenshot({path:path.join(captures,'plugins-narrow.png')});
    assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),true,'no page overflow at 390px');
    assert.ok(await page.locator('#pluginChatMessages').innerText(),'development chat rendered');
    await page.locator('#pluginDisable').click();await page.waitForFunction(()=>!document.querySelector('#pluginPreviewMount iframe'));
    console.log('PASS plugins UI: create/check/preview/apply/update/disable, actual isolated Worker, desktop/narrow screenshots');
    const manifest=JSON.parse(read('data-templates/plugin.json')),members=new Set(manifest.files),files=name=>members.has(name)?read('data-templates/'+name):null;
    const input=read('data-templates/core/examples/matrix-xlsx-extraction.json').toString();
    const inspected=JSON.parse(await execute(files,'inspect-template',JSON.stringify({input})));assert.equal(inspected.inspection.template.id,'production-form');
    const extracted=JSON.parse(await execute(files,'extract',JSON.stringify({input,sourcePath:'matrix-source.xlsx',templateName:'template.json',maximumBytes:10485760})));
    const output=JSON.parse(execute.lastRealm.result.toString());assert.equal(output.records.length,extracted.summary.recordCount);assert.ok(output.records.length>0);assert.equal(output.kind,'data-templates-extraction');assert.equal(extracted.bytes,execute.lastRealm.result.length);
    const saved=JSON.parse(await execute(files,'extract-to-file',JSON.stringify({input,sourcePath:'matrix-source.xlsx',templateName:'template.json',maximumBytes:10485760,outputPath:'results/output.json'})));
    assert.equal(saved.path,'results/output.json');assert.equal(saved.bytes,execute.lastRealm.result.length);assert.deepEqual(JSON.parse(execute.lastRealm.result.toString()),output,'agent save uses the same engine and canonical output');
    assert.deepEqual(errors,[]);console.log('PASS Data Templates plugin: real Worker inspection, bounded native-source RPC, staged canonical result read-back');
    console.log('Screenshots: '+captures);
  }finally{await browser.close();}
})().catch(error=>{console.error(error);process.exitCode=1;});
