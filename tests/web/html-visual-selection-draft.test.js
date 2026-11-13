'use strict';
const assert=require('node:assert/strict'),fs=require('node:fs'),vm=require('node:vm');
(async()=>{
  const calls=[],drafts=new Map(),contexts=[];
  const key=t=>t.workspaceId+':'+t.sessionId;
  const context={console,window:null,document:{createElement:()=>({dataset:{},setAttribute(){},remove(){}})},ATTACHMENT_MAX_FILES:10,
    attachmentId:item=>item.id||item.Id,RNAssistantConversationInput:{isPending:()=>false},
    RNAssistantConversationPanel:{find:()=>({snapshot:()=>({messages:[]}),captureDraft(){},controls(){}})},
    RNAssistantChatDrafts:{readWorkspace:t=>drafts.get(key(t)),updateWorkspace:(t,c)=>drafts.set(key(t),{...drafts.get(key(t)),...c}),flushWorkspace:async()=>{},
      readOffice:id=>drafts.get(id),saveOffice:(id,text,attachments,unused,next,pending)=>drafts.set(id,{text,attachments,nextAnnotationNumber:next,pendingVisualAnnotation:pending}),flushOffice:async()=>{}},
    RNAssistantVisualInspector:{createHost:()=>({bind(){},receive(){},refresh(){},deactivate(){},dispose(){}})},
    RNAssistantVisualAnnotations:{create:options=>{contexts.push(options);return {sync(){},onSelection(){},dispose(){},collapseEditor(){}}}}
  };context.window=context;vm.createContext(context);vm.runInContext(fs.readFileSync('web/js/app-browser-annotations.js','utf8'),context);
  let response=async()=>({id:'plugin-draft'});
  function host(){return context.RNAssistantBrowserAnnotations.create({root:{appendChild(){}},viewport:{},surface:{setAnnotation(){}},layout(){},focus(){},command:async()=>{},isCurrent:()=>true,
    send:async(type,payload)=>{calls.push({type,payload});return type==='discardChatResourceDraft'?{}:response();}});}
  const target={source:'workspace',workspaceId:'project',sessionId:'plugin-chat'};drafts.set(key(target),{text:'Existing composer text',attachments:[{id:'file'}]});
  const page={sessionId:'native',documentGeneration:4,status:'Ready',ownerScope:{kind:'Workspace',scopeId:'project',chatId:'plugin-chat'},target:{capabilities:{annotations:true}},annotationContext:{sourceId:'release',sourceRevision:1,pluginInstanceId:'instance'}};
  const first=host();first.update(page);
  await contexts[0].stage({generation:4,pageSession:'native',sourceId:'release',sourceRevision:1,selection:{tag:'button'}},'Change',1,null);
  assert.equal(calls[0].type,'stagePluginVisualSelection');assert.equal(calls[0].payload.instanceId,'instance');assert.equal(calls[0].payload.sessionId,'plugin-chat');
  assert.equal(drafts.get(key(target)).text,'Existing composer text');assert.deepEqual(drafts.get(key(target)).attachments.map(item=>item.id),['file','plugin-draft']);
  let release;response=()=>new Promise(resolve=>release=resolve);
  const late=contexts[0].stage({generation:4,pageSession:'native',sourceId:'release',sourceRevision:1,selection:{tag:'button'}},'Late',2,null);
  first.update({...page,documentGeneration:5});release({id:'late'});await assert.rejects(late,/Страница изменилась/);
  assert.equal(calls.at(-1).type,'discardChatResourceDraft');assert.equal(calls.at(-1).payload.chatId,'plugin-chat');assert.equal(drafts.get(key(target)).attachments.length,2);
  response=async()=>({resource:{Id:'office-draft'}});drafts.set('office-chat',{text:'Office text',attachments:[]});const second=host();
  second.update({...page,ownerScope:{kind:'Document',scopeId:'doc',chatId:'office-chat'},annotationContext:{sourceId:'html-r2',sourceRevision:2}});
  await contexts[1].stage({generation:4,pageSession:'native',sourceId:'html-r2',sourceRevision:2,selection:{tag:'button'}},'',1,null);
  const office=calls.find(call=>call.type==='stageHtmlVisualSelection');assert.equal(office.payload.chatId,'office-chat');assert.equal(office.payload.artifactId,'html-r2');assert.equal(office.payload.artifactRevision,2);
  assert.equal(drafts.get('office-chat').text,'Office text');first.dispose();second.dispose();
  console.log('PASS common native annotation drafts: exact Office/plugin owner, existing composer data, generation guard and late cleanup');
})().catch(error=>{console.error(error);process.exitCode=1;});
