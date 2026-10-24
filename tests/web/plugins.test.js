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
  await context.addInitScript({content:'if(location.hostname.endsWith(".rnassistant-plugin.invalid") && location.pathname==="/index.html") {'+
    read('js/app-visual-inspector.js').toString()+'\nwindow.RNAssistantVisualInspector.install(1);\n}'});
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
    else {
      assert.equal(url.search,'','package assets use exact paths, as required by PluginWebViewHost');
      body=name.startsWith('.host/')?read('plugin-host/'+name.slice(6)):realm.files(name);
    }
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
    const page=await context.newPage();page.on('pageerror',error=>errors.push(error.stack||error.message));
    await page.goto('file://'+path.join(web,'index.html'));assert.equal(await page.title(),'RN Assistant');
    let active=null,candidate=null,version=1,iconPrompt='',savedIcons=[],drawCalls=0,sourceChatCreated=false,sourceChatCreateCalls=0;
    let visualAttachment=null,visualSent=false,visualRequest=null;
    const publishedVersions=[];
    const item={pluginId:'example',name:'Example',sourceWorkspaceId:'source-1',sourceRoot:'C:\\Plugins\\Example',releaseId:null,enabled:false};
    await page.exposeFunction('testPluginHost',async (type,payload)=>{
      switch(type){
        case 'getChatDrafts':return {drafts:[]};
        case 'saveChatDraft':return {saved:true};
        case 'getPlugins':return {defaultSourceParent:'C:\\Users\\Demo\\AppData\\Roaming\\RNAssistant\\plugin-workspaces',items:item.hidden?[]:[{...item}]};
        case 'getRemovedPlugins':return {items:item.hidden?[{...item}]:[]};
        case 'getPluginIcons':return {items:savedIcons.slice()};
        case 'savePluginIcon':{const saved={id:'icon-test',name:payload.name,svg:'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24"><path d="M4 4h16v16H4Z"/></svg>'};savedIcons=[saved];return saved;}
        case 'deletePluginIcon':assert.equal(payload.id,'icon-test');savedIcons=[];return {items:[]};
        case 'drawPluginIcon':assert.equal(payload.description,'Таблица с восходящей линией');drawCalls++;{const saved={id:'icon-drawn',name:payload.name,svg:'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24"><path d="M3 4h18v16H3z M3 10h18M9 4v16"/></svg>'};savedIcons=[saved];return saved;}
        case 'createPlugin':assert.equal(payload.name,'Example');assert.equal(payload.rootPath,null);assert.equal(payload.iconId,'icon-drawn');assert.equal(payload.iconDescription,'');return {...item};
        case 'getPluginSourceState':return {pluginId:payload.pluginId,stamp:'source-'+version};
        case 'getPluginChat':return sourceChatCreated?{project:{workspaceId:'source-1'},sessionId:'source-chat-1',messages:[{Id:'ready',Role:'assistant',Content:'Исходники готовы к изменению.'}],state:{Status:'idle'}}:
          {project:{workspaceId:'source-1'},sessionId:null,messages:[],state:null};
        case 'createProjectChat':assert.equal(payload.workspaceId,'source-1');sourceChatCreated=true;sourceChatCreateCalls++;
          return {project:{workspaceId:'source-1'},sessionId:'source-chat-1',messages:[],state:{Status:'idle'}};
        case 'getProjectChat':return {project:{workspaceId:'source-1'},sessionId:'source-chat-1',messages:[{Id:'ready',Role:'assistant',Content:'Исходники готовы к изменению.'}],state:{Status:'idle'}};
        case 'submitProjectChat':assert.equal(payload.sessionId,'source-chat-1');assert.equal(payload.input.sessionId,payload.sessionId);
          if((payload.input.ResourceDraftIds||[]).length){assert.deepEqual(payload.input.ResourceDraftIds,[visualAttachment.Id]);visualSent=true;
            return {project:{workspaceId:'source-1'},sessionId:payload.sessionId,messages:[
              {Id:'visual-user',Role:'user',Content:'',Attachments:[visualAttachment]}],state:{Status:'completed'}};}
          iconPrompt=payload.input.message;return {project:{workspaceId:'source-1'},sessionId:payload.sessionId,messages:[
          {Id:'step',Role:'assistant',RunId:'plugin-test-run',Activity:{RunId:'plugin-test-run',StepId:'check',Kind:'tool',Title:'Проверка исходников',Status:'completed',ToolId:'common.resources_read'}},
          {Id:'answer',Role:'assistant',Content:'SVG-иконка смоделирована.'}],state:{Status:'completed',runId:'plugin-test-run',runViewState:{RunId:'plugin-test-run',TurnId:'turn',Lifecycle:'completed',ExecutionHealth:'clean',SuccessfulReads:1,VerifiedWrites:0,NoChangeWrites:0,UnverifiedWrites:0,FailedCalls:0,UnknownEffects:0}}};
        case 'checkPlugin':candidate='candidate-'+version;return {candidateId:candidate,hasChanges:!item.enabled||version!==active,files:3,bytes:100};
        case 'applyPlugin':assert.equal(payload.candidateId,candidate);active=version;item.releaseId='release-'+version;item.enabled=true;item.icon='icon.svg';item.iconSvg='<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24"><path d="M4 18V6h3v12M10 18v-7h3v7M16 18V4h3v14"/></svg>';publishedVersions.unshift({releaseId:item.releaseId,name:item.name,version:'1.0.0',capturedUtc:new Date().toISOString()});return {...item};
        case 'getPluginVersions':{const offset=payload.cursor?Number(payload.cursor):0;return {items:publishedVersions.slice(offset,offset+12).map(saved=>({...saved,active:item.enabled&&saved.releaseId===item.releaseId})),nextCursor:publishedVersions.length>offset+12?String(offset+12):null};}
        case 'restorePluginVersion':{assert.equal(payload.expectedActiveReleaseId,item.releaseId);const saved=publishedVersions.find(entry=>entry.releaseId===payload.releaseId);assert.ok(saved);item.releaseId=saved.releaseId;item.enabled=true;active=Number(saved.releaseId.split('-').pop());publishedVersions.splice(publishedVersions.indexOf(saved),1);publishedVersions.unshift(saved);return {...item};}
        case 'openPlugin':{const origin='https://view-'+(++sequence)+'.rnassistant-plugin.invalid';const openedVersion=payload.releaseId?Number(payload.releaseId.split('-').pop()):active;realms.set(origin,{origin,files:fixture(payload.candidateId?'preview-v'+version:'v'+openedVersion)});return {instanceId:origin,pluginId:'example',entryUrl:origin+'/index.html',releaseId:payload.releaseId||'release-'+version,preview:!!payload.candidateId||!!payload.releaseId};}
        case 'stagePluginVisualSelection':assert.equal(payload.workspaceId,'source-1');assert.equal(payload.sessionId,'source-chat-1');assert.ok(payload.instanceId.startsWith('https://view-'));assert.equal(payload.selection.tag,'h1');visualRequest=payload;
          visualAttachment={Id:'visual-draft-1',FileName:'html-selection-h1.json',Kind:'text',Size:300,visualAnnotation:{selection:payload.selection,comment:payload.comment,annotationNumber:payload.annotationNumber},visualSourceArtifactId:'release-1',visualSourceRevision:1};
          return {Id:visualAttachment.Id,FileName:visualAttachment.FileName,Kind:visualAttachment.Kind,Size:visualAttachment.Size};
        case 'discardPluginVisualSelection':return true;
        case 'closePlugin':realms.delete(payload.instanceId);return {};
        case 'disablePlugin':item.enabled=false;return {...item};
        case 'removePlugin':assert.equal(payload.pluginId,'example');item.hidden=true;return {pluginId:'example',hidden:true};
        case 'restorePlugin':assert.equal(payload.pluginId,'example');item.hidden=false;return {...item};
        default:throw Error('Unexpected host call '+type);
      }
    });
    await page.evaluate(async()=>{window.send=window.testPluginHost;await RNAssistantChatDrafts.hydrate();return RNAssistantPlugins.refresh();});
    const railWidths={};
    for(const [section,label] of [['chat','Рабочая область'],['artifacts','Материалы'],['library','Библиотека'],['vba','VBA'],['settings','Настройки']]){
      await page.locator('.section-rail [data-section="'+section+'"]').hover();
      assert.equal(await page.locator('.rail-hover-title').textContent(),label,'one shared rail label for '+section);
      railWidths[section]=(await page.locator('.rail-hover-card').boundingBox()).width;
    }
    assert.ok(railWidths.vba<70&&railWidths.chat>railWidths.vba,'tooltip width follows label length');
    assert.equal(await page.locator('.rail-hover-card').count(),1,'rail uses one shared popover');
    assert.equal(await page.locator('.rail-hover-title').evaluate(node=>getComputedStyle(node).fontWeight),'400','menu labels use regular weight');
    assert.equal(await page.locator('.section-rail [data-section="chat"]').getAttribute('title'),null,'native rail tooltip is replaced');
    await page.evaluate(()=>RNAssistantPlugins.select('example'));
    await page.locator('#messages').getByText('Чатов пока нет').waitFor();
    assert.equal(sourceChatCreateCalls,0,'opening development does not create a source chat');
    assert.equal(await page.locator('#sendButton').isDisabled(),true,'empty source chat locks the composer');
    assert.equal(await page.locator('#pluginChatNew').isEnabled(),true,'explicit creation remains available');
    await page.locator('#pluginAdd').hover();
    await page.locator('.rail-hover-card').getByText('Удалённых с панели плагинов нет.').waitFor();
    assert.equal(await page.locator('.rail-hover-title').textContent(),'Добавить плагины');
    assert.equal(await page.locator('.rail-hover-title').evaluate(node=>getComputedStyle(node).fontSize),'10px','add menu heading is muted and small');
    await page.locator('.rail-hover-card').getByRole('button',{name:'Создать плагин'}).waitFor();
    await page.locator('#pluginAdd').focus();
    await page.locator('#pluginAdd').press('Tab');
    assert.equal(await page.evaluate(()=>document.activeElement.textContent.trim()),'Создать плагин','keyboard reaches hover action');
    await page.keyboard.press('Shift+Tab');
    assert.equal(await page.locator('#pluginAdd').evaluate(node=>node===document.activeElement),true,'keyboard returns to rail');
    await page.locator('#pluginAdd').click();
    await page.locator('#pluginCreateForm').waitFor({state:'visible'});
    await page.waitForFunction(()=>!document.querySelector('.shell')?.classList.contains('navigation-transition'));
    assert.equal(await page.locator('#productMobileMenuButton').isVisible(),false,'creation screen has no redundant wide tree toggle');
    assert.ok((await page.locator('.plugin-create-heading').boundingBox()).x<170,'plugin form aligns to the left');
    await page.locator('#pluginName').fill('Example');
    assert.equal(await page.locator('#pluginCreateTitle').textContent(),'Example','plugin heading follows name');
    await page.locator('#pluginIconTrigger').click();
    assert.equal(await page.locator('#pluginBuiltinIcons [data-icon-id="dataTemplates"] svg path').count(),1,'original Data Templates glyph is available');
    await page.locator('.plugin-icon-import summary').click();
    await page.locator('#pluginIconImportName').fill('Мой знак');
    await page.locator('#pluginIconImportSvg').fill('<svg viewBox="0 0 24 24"><path d="M4 4h16v16H4Z"/></svg>');
    await page.locator('#pluginIconImportButton').click();
    await page.locator('#pluginSavedIcons [data-icon-id="icon-test"]').waitFor();
    assert.equal(await page.locator('#pluginIconBriefField').isVisible(),false,'ready SVG disables drawing brief');
    await page.locator('#pluginSavedIcons .plugin-icon-remove').click();
    assert.equal(await page.locator('#pluginIconBriefField').isVisible(),false,'removing selected SVG returns to a base icon');
    await page.locator('#pluginBuiltinIcons [data-icon-id="draw"]').click();
    await page.locator('#pluginIconDescription').fill('Таблица с восходящей линией');
    await page.locator('#pluginIconDrawButton').click();
    await page.locator('#pluginSavedIcons [data-icon-id="icon-drawn"]').waitFor();
    assert.equal(drawCalls,1,'drawing saves a reusable icon immediately');
    await page.locator('#pluginIconTrigger').click();
    await page.screenshot({path:path.join(captures,'plugin-create-desktop.png')});
    await page.setViewportSize({width:390,height:844});
    await page.waitForFunction(()=>!document.querySelector('.shell')?.classList.contains('navigation-transition'));
    assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),true,'new plugin form fits narrow view');
    await page.screenshot({path:path.join(captures,'plugin-create-narrow.png')});
    await page.locator('#pluginCreateSubmit').scrollIntoViewIfNeeded();
    assert.equal(await page.locator('#pluginCreateSubmit').isVisible(),true,'submit stays reachable in the narrow form');
    await page.screenshot({path:path.join(captures,'plugin-create-narrow-bottom.png')});
    await page.setViewportSize({width:1280,height:800});
    await page.locator('#pluginConnect').check();
    assert.equal(await page.locator('#pluginNewFields').isVisible(),false,'connection hides creation-only fields');
    assert.equal(await page.locator('#pluginCreateSubmit').textContent(),'Подключить папку');
    await page.locator('#pluginConnect').uncheck();
    await page.locator('#pluginTask').fill('Сводка продаж');
    assert.equal(await page.locator('#pluginRoot').isVisible(),false,'default folder is used without a path input');
    await page.locator('#pluginSourceChange').click();
    assert.equal(await page.locator('#pluginRoot').isVisible(),true,'custom source path is available on demand');
    await page.locator('#pluginRoot').fill('C:\\Plugins\\Example');
    await page.locator('#pluginSourceChange').click();
    assert.equal(await page.locator('#pluginRoot').isVisible(),false,'returning to the default path hides the custom input');
    await page.locator('#pluginCreateForm button[type=submit]').click();
    await page.waitForFunction(()=>document.querySelector('#messages')?.textContent.includes('SVG-иконка смоделирована.'));
    assert.equal(sourceChatCreateCalls,1,'creation task explicitly opens one source chat before submit');
    assert.equal(await page.locator('#pluginCheck').count(),0,'package check is integrated into preview refresh');
    assert.equal(await page.locator('#pluginPreview').getAttribute('aria-label'),'Проверить исходники и обновить предпросмотр');
    assert.equal(await page.locator('#chatInspectorViewTitle').textContent(),'Предпросмотр');
    const visibleFrame=await page.locator('#pluginPreviewMount iframe').getAttribute('src');
    await page.locator('#chatInspectorSwitcher summary').click();
    await page.screenshot({path:path.join(captures,'plugin-panel-sections.png'),animations:'disabled'});
    await page.locator('[data-chat-inspector-view="activity"]').click();
    assert.equal(await page.locator('#chatInspectorBody').isVisible(),true,'plugin activity uses the same right slot');
    assert.match(await page.locator('#chatInspectorBody').textContent(),/Проверка исходников/,
      'plugin activity derives from the selected chat history');
    assert.equal(await page.locator('#chatInspectorViewTitle').textContent(),'Ход работы');
    assert.equal(await page.locator('#pluginPreview').isVisible(),false,'refresh belongs only to preview');
    await page.screenshot({path:path.join(captures,'plugin-activity.png'),animations:'disabled'});
    await page.locator('#chatInspectorSwitcher summary').click();
    await page.locator('[data-chat-inspector-view="preview"]').click();
    assert.equal(await page.locator('#pluginPreviewMount iframe').getAttribute('src'),visibleFrame,
      'switching right-panel sections keeps the exact plugin frame');
    const splitter=page.locator('#pluginSurfaceSplitter');
    const splitBefore=Number(await splitter.getAttribute('aria-valuenow'));
    await splitter.press('ArrowLeft');
    assert.ok(Number(await splitter.getAttribute('aria-valuenow'))<splitBefore,'development split resizes from the keyboard');
    assert.match(iconPrompt,/workspace\.plugin_authoring/);
    assert.match(iconPrompt,/Сводка продаж/);
    assert.doesNotMatch(iconPrompt,/Таблица с восходящей линией/,'created icon is already in source');
    assert.equal(drawCalls,1,'creation reuses the drawn icon');
    await page.locator('#pluginPreviewMount .chat-browser-annotate').waitFor({state:'visible'});
    await page.waitForFunction(()=>!document.querySelector('#pluginPreviewMount .chat-browser-annotate').disabled);
    await page.locator('#pluginPreviewMount .chat-browser-annotate').click();
    await page.frameLocator('#pluginPreviewMount iframe').locator('h1').hover();
    await page.frameLocator('#pluginPreviewMount iframe').locator('h1').click();
    await page.locator('#pluginPreviewFrameHost .visual-annotation-editor textarea').fill('Fix the heading');
    await page.screenshot({path:path.join(captures,'plugin-annotation-editor.png'),animations:'disabled'});
    await page.locator('#pluginPreviewFrameHost .visual-annotation-editor button.primary').click();
    await page.locator('#pluginAnnotationDrafts .visual-annotation-list').waitFor();
    assert.equal(visualRequest.selection.tag,'h1','the shared inspector captures the plugin DOM target');
    assert.equal(visualRequest.annotationNumber,1,'plugin annotations use the shared numbered draft');
    assert.equal(await page.locator('#sendButton').isEnabled(),true,'an annotation can be sent without extra text');
    await page.locator('#sendButton').click();
    await page.waitForFunction(()=>document.querySelector('#messages')?.textContent.includes('Fix the heading'));
    assert.equal(visualSent,true,'the shared chat submits the exact staged attachment ID');
    await page.locator('#pluginPreviewFrameHost .visual-annotation-marker.is-sent').waitFor();
    await page.screenshot({path:path.join(captures,'plugin-annotation-sent.png'),animations:'disabled'});
    await page.locator('#pluginPreviewMount .chat-browser-annotate').click();
    await page.frameLocator('#pluginPreviewMount iframe').locator('#run').click();
    await page.frameLocator('#pluginPreviewMount iframe').locator('#answer').filter({hasText:'hello preview-v1'}).waitFor();
    const refreshPreview=async()=>{const previous=await page.locator('#pluginPreviewMount iframe').getAttribute('src');
      await page.locator('#pluginPreview').click();
      await page.waitForFunction(value=>document.querySelector('#pluginPreviewMount iframe')?.getAttribute('src')!==value,previous);};
    await refreshPreview();
    await page.frameLocator('#pluginPreviewMount iframe').locator('#run').click();await page.frameLocator('#pluginPreviewMount iframe').locator('#answer').filter({hasText:'hello preview-v1'}).waitFor();
    await page.locator('#pluginApply').click();await page.locator('.plugin-toolbar-actions summary').click();await page.locator('#pluginReopen').click();
    assert.equal(await page.locator('#pluginRail [data-plugin-id="example"] .plugin-icon-image').count(),1,'published SVG icon appears in the rail');
    await page.frameLocator('#pluginPreviewMount iframe').locator('#run').click();await page.frameLocator('#pluginPreviewMount iframe').locator('#answer').filter({hasText:'hello v1'}).waitFor();
    version=2;await refreshPreview();await page.frameLocator('#pluginPreviewMount iframe').locator('#run').click();
    await page.frameLocator('#pluginPreviewMount iframe').locator('#answer').filter({hasText:'hello preview-v2'}).waitFor();
    await page.locator('#pluginApply').click();
    assert.equal(await page.frameLocator('#pluginPreviewMount iframe').locator('#answer').textContent(),'hello preview-v2','apply preserves the pinned visible instance');
    await page.locator('.plugin-toolbar-actions summary').click();await page.locator('#pluginReopen').click();await page.frameLocator('#pluginPreviewMount iframe').locator('#run').click();await page.frameLocator('#pluginPreviewMount iframe').locator('#answer').filter({hasText:'hello v2'}).waitFor();
    await page.locator('#pluginVersions summary').click();
    await page.screenshot({path:path.join(captures,'plugin-versions.png')});
    await page.locator('#pluginVersionItems button').nth(1).click();
    assert.equal(await page.locator('#pluginRestoreVersion').isVisible(),true,'historical preview offers restore');
    assert.equal(await page.locator('#pluginApply').isVisible(),false,'historical preview hides source Apply');
    await page.frameLocator('#pluginPreviewMount iframe').locator('#run').click();
    await page.frameLocator('#pluginPreviewMount iframe').locator('#answer').filter({hasText:'hello v1'}).waitFor();
    await page.locator('#pluginRestoreVersion').click();
    await page.frameLocator('#pluginPreviewMount iframe').locator('#run').click();
    await page.frameLocator('#pluginPreviewMount iframe').locator('#answer').filter({hasText:'hello v1'}).waitFor();
    assert.equal(version,2,'restore leaves editable source version untouched');
    await refreshPreview();
    await page.frameLocator('#pluginPreviewMount iframe').locator('#run').click();
    await page.frameLocator('#pluginPreviewMount iframe').locator('#answer').filter({hasText:'hello preview-v2'}).waitFor();
    const previousFrame=await page.locator('#pluginPreviewMount iframe').getAttribute('src');
    version=3;
    await page.waitForFunction(previous=>document.querySelector('#pluginPreviewMount iframe')?.getAttribute('src')!==previous,previousFrame);
    await page.frameLocator('#pluginPreviewMount iframe').locator('#run').click();
    await page.frameLocator('#pluginPreviewMount iframe').locator('#answer').filter({hasText:'hello preview-v3'}).waitFor();
    await page.screenshot({path:path.join(captures,'plugins-desktop.png')});
    await page.setViewportSize({width:390,height:844});await page.screenshot({path:path.join(captures,'plugins-narrow.png')});
    assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),true,'no page overflow at 390px');
    assert.ok(await page.locator('#messages').innerText(),'development chat rendered');
    await page.locator('.plugin-toolbar-actions summary').click();await page.locator('#pluginDisable').click();await page.waitForFunction(()=>!document.querySelector('#pluginPreviewMount iframe'));
    await page.setViewportSize({width:1280,height:800});
    await page.locator('#pluginRail [data-plugin-id="example"]').hover();
    assert.equal(await page.locator('.rail-hover-title').textContent(),'Example','plugin uses the same rail popover');
    await page.locator('.rail-hover-card').getByRole('button',{name:'Удалить с панели'}).waitFor();
    assert.deepEqual(await page.locator('.rail-hover-card[data-kind="plugin-item"]').evaluate(card=>{
      const title=card.querySelector('.rail-hover-title'),line=card.querySelector('.rail-hover-separator'),remove=card.querySelector('button');
      return {separator:!!line,ordered:title.getBoundingClientRect().bottom<line.getBoundingClientRect().top&&
        line.getBoundingClientRect().bottom<remove.getBoundingClientRect().top,
        fontFamily:getComputedStyle(remove).fontFamily===getComputedStyle(title).fontFamily,
        fontSize:getComputedStyle(remove).fontSize};
    }),{separator:true,ordered:true,fontFamily:true,fontSize:'12px'},'plugin removal has a separated action in the shell font');
    page.once('dialog',dialog=>dialog.accept());
    await page.locator('.rail-hover-card').getByRole('button',{name:'Удалить с панели'}).click();
    await page.locator('#pluginRail [data-plugin-id="example"]').waitFor({state:'detached'});
    assert.equal(item.releaseId,'release-1','removal preserves the restored publication');
    assert.equal(await page.locator('#pluginCreateTitle').textContent(),'Новый плагин','successful creation clears its form before the next plugin');
    const restoredName='Плагин сводки продаж и проверки показателей';item.name=restoredName;
    await page.locator('#pluginAdd').hover();
    await page.locator('.rail-hover-card').getByRole('button',{name:restoredName}).waitFor();
    assert.deepEqual(await page.locator('.rail-hover-body button').allTextContents(),[restoredName,'Создать плагин'],'restore choices precede create');
    assert.ok((await page.locator('.rail-hover-card').boundingBox()).width>240,'menu grows to fit a plugin name');
    const restoreMenuHeight=(await page.locator('.rail-hover-card').boundingBox()).height;
    assert.ok(restoreMenuHeight<100,'rail menu stays compact: '+restoreMenuHeight);
    await page.screenshot({path:path.join(captures,'plugin-restore-menu.png')});
    await page.locator('.rail-hover-card').getByRole('button',{name:restoredName}).click();
    await page.locator('#pluginRail [data-plugin-id="example"]').waitFor();
    console.log('PASS plugins UI: create/check/preview/apply/update/disable, actual isolated Worker, desktop/narrow screenshots');
    const manifest=JSON.parse(read('data-templates/plugin.json')),members=new Set(manifest.files),files=name=>members.has(name)?read('data-templates/'+name):null;
    const uiOrigin='https://templates-ui.rnassistant-plugin.invalid';
    realms.set(uiOrigin,{origin:uiOrigin,files});
    const templatePage=await context.newPage();
    await templatePage.goto(uiOrigin+'/index.html');
    await templatePage.waitForFunction(()=>typeof RNPlugin!=='undefined' && document.querySelector('.dt-header'));
    assert.match(await templatePage.locator('body').evaluate(node=>getComputedStyle(node).fontFamily),/Segoe UI/,'base stylesheet loads inside the isolated plugin');
    assert.equal(await templatePage.locator('button svg').first().evaluate(node=>getComputedStyle(node).width),'16px','button icons retain their intended size');
    await templatePage.close();realms.delete(uiOrigin);
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
