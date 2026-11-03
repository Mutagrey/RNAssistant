(function () {
  'use strict';
  function key(target){return 'plugin:' + encodeURIComponent(target.pluginId);}
  function create(target){
  var root=document.getElementById('pluginWorkbenchTemplate').content.firstElementChild.cloneNode(true);
  RNAssistantIcons.hydrate(root);
  root.hidden=false;root.dataset.pluginId=target.pluginId;
  var selected=null,candidate=null,view=null,busy=false,epoch=0,disposed=false,startup=null;
  var previewBusy=false,previewTask=null,sourceStamp=null,candidateStamp=null,sourceTimer=null,sourceDebounce=null;
  var previewOpen=false,browserView=null,previewSurface=null;
  var pluginInspector=null,pluginAnnotations=null,pluginAnnotateButton=null,pluginInspectStatus=null,pluginDraftBox=null;
  var visualSessionId=null,frameReady=false,pendingNavigation=null,versionCursor=null,versionEpoch=0;
  var publishedSourceStamps=Object.create(null),events=new AbortController();
  var navigation={projectName:'Проект данных',view:'sources',templateId:'',templates:[],expanded:{project:true,templates:true,dictionary:true}};
  function el(id){return id==='Workbench'?root:root.querySelector('[data-plugin-node="'+id+'"]')||window.RNAssistantWorkspaceChat.node(id);}
  function ownsChat(){var snapshot=window.RNAssistantProjectChats.snapshot();return !!selected&&window.RNAssistantWorkspaceChat.owner()==='plugin'&&
    snapshot&&snapshot.project&&snapshot.project.workspaceId===selected.sourceWorkspaceId&&document.getElementById('tab-chat').classList.contains('active');}
  function chatStatus(text,error){if(ownsChat()){el('Notice').textContent=text||'';el('Notice').dataset.error=error?'true':'false';}}
  var statusTimer;
  function status(text,error){clearTimeout(statusTimer);el('WorkbenchStatus').textContent=text||'';el('WorkbenchStatus').setAttribute('role',error?'alert':'status');
    if(text&&!error)statusTimer=setTimeout(function(){el('WorkbenchStatus').textContent='';},4500);}
  function refresh(){return RNAssistantPlugins.refresh();}
  function openSourceChat(sessionId){return RNAssistantPlugins.openSourceChat(sessionId,selected);}
  function applyAppearance(frame){RNAssistantPlugins.applyAppearance(frame);}
  function dataPlugin(){return selected&&selected.pluginId==='data-templates';}
  function icon(name){return window.RNAssistantNavigationTree.icon(name);}
  function syncAppearance(){
    if(!dataPlugin())return;
    var frame=el('PreviewMount').querySelector('iframe');
    if(!frame||!frame.contentWindow)return;
    applyAppearance(frame);
  }
  function flushNavigation(){
    var frame=el('PreviewMount').querySelector('iframe');
    if(!frameReady||!frame||!frame.contentWindow||!pendingNavigation)return;
    frame.contentWindow.postMessage({type:'rna.dataTemplates.navigate',view:pendingNavigation.view,
      templateId:pendingNavigation.templateId},'*');
    pendingNavigation=null;
  }
  function navigateData(viewName,templateId){
    navigation.view=viewName;navigation.templateId=templateId||'';renderNavigation();
    el('NavigationPane').hidden=true;el('NavigationToggle').setAttribute('aria-expanded','false');
    pendingNavigation={view:viewName,templateId:templateId||''};flushNavigation();
  }
  function renderNavigation(){
    var tree=el('NavigationTree');
    el('NavigationToggle').hidden=!dataPlugin();
    if(!dataPlugin()){tree.replaceChildren();el('NavigationPane').hidden=true;return;}
    var leaf=function(key,title,viewName,iconName,templateId){return {key:key,title:title,kind:'item',iconHtml:icon(iconName),
      selected:navigation.view===viewName&&(!templateId||navigation.templateId===templateId),view:viewName,templateId:templateId||''};};
    var templates=navigation.templates.map(function(item){return leaf('template:'+item.id,item.id,'templates','file',item.id);});
    var nodes=[{key:'project',kind:'parent',title:navigation.projectName,expanded:navigation.expanded.project,children:[
      leaf('sources','Источники','sources','file'),
      {key:'templates',kind:'parent',title:'Шаблоны',selectable:true,selected:navigation.view==='templates'&&!navigation.templateId,
        expanded:navigation.expanded.templates,view:'templates',children:templates.concat([
        leaf('drafts','Снимки черновиков','drafts','clock')])},
      {key:'dictionary',kind:'parent',title:'Словарь',expanded:navigation.expanded.dictionary,children:[
        leaf('parameters','Параметры','parameters','code'),leaf('mapping','Соответствия','mapping','link')]},
      leaf('processing','Обработка','processing','play'),leaf('results','Результаты','results','chart'),
      leaf('format','Форматы и ограничения','format','file')]}];
    var query=el('NavigationSearch').value.trim().toLocaleLowerCase('ru');
    if(query){var filter=function(item){var own=item.title.toLocaleLowerCase('ru').includes(query);
      var children=item.children&&item.children.map(filter).filter(Boolean);
      if(!own&&(!children||!children.length))return null;
      return Object.assign({},item,{children:own?item.children:children,expanded:own?item.expanded:true});};
      nodes=nodes.map(filter).filter(Boolean);}
    window.RNAssistantNavigationTree.render(tree,nodes,{label:'Разделы проекта данных',onSelect:function(item){
      if(item.view)navigateData(item.view,item.templateId);
    },onToggle:function(item,expanded){navigation.expanded[item.key]=expanded;}});
  }
  window.addEventListener('message',function(event){
    var frame=el('PreviewMount')&&el('PreviewMount').querySelector('iframe');
    if(!dataPlugin()||!frame||event.source!==frame.contentWindow||!event.data||event.data.type!=='rna.dataTemplates.state')return;
    frameReady=event.data.ready!==false;
    var requested=frameReady&&!!pendingNavigation;
    flushNavigation();
    var next=event.data;
    if(typeof next.projectName==='string')navigation.projectName=next.projectName.slice(0,200)||'Проект данных';
    if(frameReady&&!requested){
      if(['sources','templates','parameters','mapping','results','format','processing','drafts'].includes(next.view))navigation.view=next.view;
      if(typeof next.templateId==='string')navigation.templateId=next.templateId;
    }
    if(Array.isArray(next.templates))navigation.templates=next.templates.filter(function(item){return item&&typeof item.id==='string';}).slice(0,100);
    var previewTitle=el('PreviewMount').querySelector('.preview-surface-title');
    if(previewTitle)previewTitle.textContent=navigation.projectName;
    renderNavigation();
  },{signal:events.signal});
  async function run(action){if(busy)return;busy=true;controls();try{await action();}catch(error){status(error.message,true);}finally{busy=false;controls();}}
  function controls(){['Preview','Apply','Reopen','Disable','SourceChat'].forEach(function(id){el(id).disabled=busy||previewBusy||!selected||
    id==='Apply'&&(!candidate||!candidate.hasChanges)||(id==='Reopen'||id==='Disable')&&!selected.enabled;});
    el('Apply').hidden=!candidate||!candidate.hasChanges||!selected;
    el('DecisionStrip').hidden=el('Apply').hidden;
    el('DecisionText').textContent=!el('Apply').hidden?'Проверенные изменения исходников':'';
    if(ownsChat()&&window.RNAssistantProjectChats)window.RNAssistantProjectChats.refreshControls();
    if(pluginInspector)pluginInspector.refresh();
}
  function currentChat(){var snapshot=window.RNAssistantProjectChats.snapshot();
    return ownsChat()&&selected&&snapshot&&snapshot.project&&
      snapshot.project.workspaceId===selected.sourceWorkspaceId?snapshot:null;}
  function pluginChatTarget(){var chat=currentChat();return chat&&chat.project&&chat.sessionId ?
    {workspaceId:chat.project.workspaceId,sessionId:chat.sessionId}:null;}
  function pluginDraft(){return window.RNAssistantChatDrafts.readWorkspace(pluginChatTarget())||null;}
  function renderVisualDrafts(){
    var panel=window.RNAssistantConversationPanel&&RNAssistantConversationPanel.selected();
    pluginDraftBox=panel&&panel.target.source==='workspace'?panel.node('pluginAnnotationDrafts'):null;
    if(!pluginDraftBox||!ownsChat())return;
    window.RNAssistantPluginAnnotations=pluginAnnotations;
    pluginDraftBox.replaceChildren();
    var visible=ownsChat()&&pluginAnnotations&&pluginChatTarget();
    var items=visible?pluginAnnotations.visibleDrafts():[];
    pluginDraftBox.hidden=!visible||!items.length;
    if(items.length)pluginAnnotations.renderDrafts(pluginDraftBox,items);
    if(pluginAnnotations)pluginAnnotations.sync();
    if(ownsChat()&&window.RNAssistantProjectChats)window.RNAssistantProjectChats.refreshTree();
  }
  function createVisual(frame){
    pluginInspector=window.RNAssistantVisualInspector.createHost({frame:frame,button:pluginAnnotateButton,
      status:pluginInspectStatus,enabled:function(){return ownsChat()&&!!pluginChatTarget()&&!!view&&
        !window.RNAssistantWorkspaceChat.readOnly(currentChat())&&previewOpen;},
      onSelectionChanged:function(available,reason){
        if(pluginAnnotations)pluginAnnotations.onSelection(available?pluginInspector.selectionRecord():null,reason);
      },onLocations:function(items){if(pluginAnnotations)pluginAnnotations.onLocations(items);},
      onReveal:function(result){if(pluginAnnotations)pluginAnnotations.onReveal(result);}});
    var context={mount:el('PreviewFrameHost'),browser:el('PreviewMount'),button:pluginAnnotateButton,
      statusNode:pluginInspectStatus,get draftBox(){return pluginDraftBox;},
      sessionId:function(){var target=pluginChatTarget();return target&&target.sessionId;},
      isOpen:function(){return previewOpen&&!el('PreviewMount').hidden&&
        el('PreviewFrameHost').contains(frame);},
      source:function(){return view&&selected&&view.pluginId===selected.pluginId?
        {id:view.releaseId,revision:1}:null;},
      pending:function(){var draft=pluginDraft();return draft&&draft.pendingVisualAnnotation||null;},
      setPending:function(value){var target=pluginChatTarget();if(target)window.RNAssistantChatDrafts.updateWorkspace(target,
        {pendingVisualAnnotation:value});},
      drafts:function(){var draft=pluginDraft();return draft&&draft.attachments||[];},
      messages:function(){var chat=currentChat();return chat&&chat.messages||[];},
      updated:function(){renderVisualDrafts();controls();},
      remove:function(item){var target=pluginChatTarget();if(!target)return;
        var remaining=context.drafts().filter(function(candidate){return attachmentId(candidate)!==attachmentId(item);});
        window.RNAssistantChatDrafts.updateWorkspace(target,{attachments:remaining});
        renderVisualDrafts();controls();
        window.RNAssistantChatDrafts.flushWorkspace(target).then(function(){return send('discardPluginVisualSelection',
          {workspaceId:target.workspaceId,sessionId:target.sessionId,id:attachmentId(item)});}).catch(function(error){
          chatStatus(error.detail||error.message,true);});},
      reserve:function(){return window.RNAssistantChatDrafts.reserveWorkspaceAnnotationNumber(pluginChatTarget());},
      openSource:function(){if(!view)return false;setPreviewOpen(true);return true;},
      status:function(message){chatStatus(message,true);}};
    pluginAnnotations=window.RNAssistantVisualAnnotations.create({frame:frame,inspector:pluginInspector,
      context:context,stage:async function(record,comment,number,replacing){
        var target=pluginChatTarget(),source=view;
        if(!target||!source||record.sessionId!==target.sessionId||record.sourceId!==source.releaseId||
            record.generation!==pluginInspector.generation())throw new Error('Предпросмотр изменился. Выберите элемент заново.');
        var attachment=await send('stagePluginVisualSelection',{instanceId:source.instanceId,
          workspaceId:target.workspaceId,sessionId:target.sessionId,previewGeneration:record.generation,
          selection:record.selection,comment:comment,annotationNumber:number});
        if(!attachment||!attachment.Id)throw new Error('Аннотация не была подготовлена.');
        function discardNew(){return send('discardPluginVisualSelection',{workspaceId:target.workspaceId,
          sessionId:target.sessionId,id:attachment.Id}).catch(function(){/* The staged draft expires. */});}
        if(view!==source||!pluginChatTarget()||pluginChatTarget().sessionId!==target.sessionId||
            !pluginInspector||pluginInspector.generation()!==record.generation){await discardNew();
          throw new Error('Предпросмотр или чат изменились. Выберите элемент заново.');
        }
        attachment.visualAnnotation={selection:record.selection,comment:comment,annotationNumber:number};
        attachment.visualSourceArtifactId=source.releaseId;attachment.visualSourceRevision=1;
        var drafts=context.drafts(),index=replacing?drafts.findIndex(function(item){return attachmentId(item)===attachmentId(replacing);}):-1;
        if(index<0&&drafts.length>=10){await discardNew();throw new Error('В сообщении может быть не более 10 аннотаций.');}
        var next=drafts.slice();if(index>=0)next.splice(index,1,attachment);else next.push(attachment);
        window.RNAssistantChatDrafts.updateWorkspace(target,{attachments:next,pendingVisualAnnotation:null});
        await window.RNAssistantChatDrafts.flushWorkspace(target);
        if(index>=0)send('discardPluginVisualSelection',{workspaceId:target.workspaceId,
          sessionId:target.sessionId,id:attachmentId(replacing)}).catch(function(error){chatStatus(error.message,true);});
        return attachment;}});
    if(ownsChat())window.RNAssistantPluginAnnotations=pluginAnnotations;
    pluginInspector.reset();
    renderVisualDrafts();
  }
  function disposeVisual(){if(window.RNAssistantPluginAnnotations===pluginAnnotations)window.RNAssistantPluginAnnotations=null;if(pluginAnnotations){pluginAnnotations.dispose();pluginAnnotations=null;}if(pluginInspector){pluginInspector.dispose();pluginInspector=null;}}
  async function closeView(){frameReady=false;pendingNavigation=null;disposeVisual();previewSurface.clear();var old=view;view=null;el('PreviewFrameHost').replaceChildren();syncPanelView();if(old)await send('closePlugin',{instanceId:old.instanceId});}
  async function showView(preview){var current=epoch,previous=view,opened;
    var label={title:selected.name,path:preview?'Неприменённые исходники':'Активная версия'};
    try{opened=await send('openPlugin',{
      pluginId:selected.pluginId,candidateId:preview?candidate.candidateId:null});}
    catch(error){if(current===epoch)previewSurface.fail(label,error.detail||error.message);throw error;}
    if(current!==epoch){await send('closePlugin',{instanceId:opened.instanceId});return;}
    disposeVisual();view=opened;frameReady=false;if(dataPlugin())pendingNavigation={view:navigation.view,templateId:navigation.templateId};
    var frame=document.createElement('iframe');frame.title=selected.name+(preview?' — предпросмотр':'');frame.sandbox='allow-scripts allow-same-origin allow-downloads';frame.referrerPolicy='no-referrer';
    if(dataPlugin())frame.addEventListener('load',syncAppearance);
    previewSurface.watch(frame,label);
    el('PreviewFrameHost').replaceChildren(frame);
    createVisual(frame);
    frame.src=view.entryUrl;
    syncPanelView();
    if(previous)await send('closePlugin',{instanceId:previous.instanceId});
    status('');}
  function stopSourceWatch(){clearInterval(sourceTimer);clearTimeout(sourceDebounce);sourceTimer=null;sourceDebounce=null;}
  async function checkAndPreview(stamp,force){
    if(!selected||!previewOpen||disposed)return false;
    if(previewTask){await previewTask;return checkAndPreview(stamp,force);}
    var id=selected.pluginId,current=epoch,source=stamp;
    previewBusy=true;controls();
    var task=(async function(){
      try{
        source=source||((await send('getPluginSourceState',{pluginId:id})).stamp);
        if(current!==epoch||!selected||selected.pluginId!==id)return false;
        if(!force&&candidateStamp===source&&view&&view.preview&&view.pluginId===id){sourceStamp=source;return true;}
        var checked=await send('checkPlugin',{pluginId:id});
        if(current!==epoch||!selected||selected.pluginId!==id)return false;
        candidate=checked;candidateStamp=source;await showView(true);
        if(current!==epoch)return false;
        sourceStamp=source;delete publishedSourceStamps[id];status('');return true;
      }catch(error){if(current===epoch){candidate=null;candidateStamp=null;sourceStamp=source;
          var message='Предпросмотр не обновлён: '+error.message;
          status('');
          previewSurface.fail({title:selected.name,path:'Новый предпросмотр недоступен'},message+' Открытый экран не подтверждает текущие исходники.');}return false;}
    })();
    previewTask=task;
    try{return await task;}finally{if(previewTask===task)previewTask=null;previewBusy=false;controls();}
  }
  async function pollSource(){if(!selected||busy||el('Workbench').hidden||!previewOpen||document.hidden||previewBusy)return;
    var id=selected.pluginId,current=epoch;
    try{var state=await send('getPluginSourceState',{pluginId:id});if(current!==epoch||!selected||selected.pluginId!==id||state.stamp===sourceStamp)return;
      sourceStamp=state.stamp;clearTimeout(sourceDebounce);sourceDebounce=setTimeout(function(){checkAndPreview(state.stamp);},450);
    }catch(error){if(current===epoch)status('Не удалось проверить изменения исходников: '+error.message,true);}}
  function startSourceWatch(){clearInterval(sourceTimer);sourceTimer=setInterval(function(){pollSource();},1200);}
  function syncPanelView(){
    if(!previewOpen&&pluginInspector)pluginInspector.deactivate();
    if(pluginAnnotateButton)pluginAnnotateButton.hidden=!view;
    if(pluginInspectStatus)pluginInspectStatus.hidden=!view;
    el('Preview').hidden=!previewOpen;
  }
  function setPreviewOpen(value){if(value)RNAssistantDockWorkspace.openTarget(target);else RNAssistantDockWorkspace.close(key(target));}
  function onChatSnapshot(snapshot){
    var sessionId=ownsChat()&&selected&&snapshot&&snapshot.project&&
      snapshot.project.workspaceId===selected.sourceWorkspaceId?snapshot.sessionId:null;
    if(visualSessionId!==sessionId){if(pluginAnnotations)pluginAnnotations.collapseEditor();visualSessionId=sessionId;}
    renderVisualDrafts();controls();
  }
  async function loadVersionPage(cursor){
    if(!selected||!el('Versions').open)return;
    var id=selected.pluginId,current=epoch,request=++versionEpoch,list=el('VersionItems');
    el('VersionMore').hidden=true;
    if(!cursor)list.textContent='Загрузка версий…';
    try{
      var page=await send('getPluginVersions',{pluginId:id,cursor:cursor||null});
      if(current!==epoch||request!==versionEpoch||!selected||selected.pluginId!==id||!el('Versions').open)return;
      if(!cursor)list.replaceChildren();
      if(!page.items.length&&!cursor)list.textContent='Опубликованных версий пока нет.';
      page.items.forEach(function(item){
        var row=document.createElement('button'),name=document.createElement('strong'),meta=document.createElement('small'),marker=document.createElement('em');
        row.type='button';row.className='plugin-version-row'+(item.active?' is-active':'');
        name.textContent='v'+item.version;marker.textContent=item.active?'Активная':'';
        var date=new Date(item.capturedUtc);
        meta.textContent=(Number.isNaN(date.getTime())?'Сохранённая версия':date.toLocaleString('ru-RU',{day:'numeric',month:'short',year:'numeric',hour:'2-digit',minute:'2-digit'}))+
          ' · '+item.releaseId.slice(-8);
        row.append(name,marker,meta);row.title=item.name+' · '+item.releaseId;
        row.onclick=function(){el('Versions').open=false;RNAssistantPluginReleaseView.open(selected,item);};
        list.appendChild(row);
      });
      versionCursor=page.nextCursor;el('VersionMore').hidden=!versionCursor;
    }catch(error){if(current===epoch&&request===versionEpoch&&el('Versions').open){
      if(!cursor)list.textContent=error.detail||error.message;
      else status(error.detail||error.message,true);
      versionCursor=null;el('VersionMore').hidden=true;
    }}
  }
    previewSurface=window.RNAssistantPreviewSurface.attach(el('PreviewMount'),{
      reloadButton:el('Preview'),canReload:function(){return !!selected;},reloadLabel:'Проверить исходники и обновить предпросмотр'});
    pluginAnnotateButton=document.createElement('button');pluginAnnotateButton.type='button';
    pluginAnnotateButton.className='button compact html-workspace-inspect-button';pluginAnnotateButton.dataset.browserControl='true';
    pluginAnnotateButton.disabled=true;previewSurface.actions.prepend(pluginAnnotateButton);
    pluginInspectStatus=document.createElement('span');pluginInspectStatus.className='html-workspace-inspect-status';
    pluginInspectStatus.setAttribute('role','status');el('Workbench').appendChild(pluginInspectStatus);
    var toolbar=el('Toolbar');
    [el('Versions'),el('NavigationToggle')]
      .forEach(function(control){previewSurface.actions.insertBefore(control,el('Preview'));});
    toolbar.remove();
    var mount=document.createElement('div');mount.className='browser-view';
    el('PreviewMount').hidden=false;
    var content=document.createElement('div');content.className='plugin-preview-content';
    content.append(el('NavigationPane'),el('PreviewFrameHost'));el('PreviewMount').appendChild(content);
    browserView=window.RNAssistantViewerRegistry.mount('browser',mount,{surface:el('Workbench'),portalHost:document.querySelector('.shell')});
    var chatButton=document.createElement('button');chatButton.type='button';chatButton.dataset.pluginNode='SourceChat';chatButton.className='icon-btn';
    chatButton.title='Чат исходников';chatButton.setAttribute('aria-label','Чат исходников');
    chatButton.innerHTML=window.RNAssistantIcons.render('chat');
    chatButton.onclick=function(){run(function(){return openSourceChat();});};previewSurface.actions.prepend(chatButton);
    previewSurface.waiting({title:target.title},'Подготовка предпросмотра…');
    root.querySelector('[data-plugin-search]').onclick=function(){var input=el('NavigationSearch');input.hidden=!input.hidden;this.setAttribute('aria-expanded',String(!input.hidden));if(!input.hidden)input.focus();};
    el('NavigationSearch').onkeydown=function(event){if(event.key==='Escape'){this.value='';this.hidden=true;renderNavigation();root.querySelector('[data-plugin-search]').focus();}};
    el('NavigationProject').onclick=function(){var frame=el('PreviewMount').querySelector('iframe');if(frame&&frame.contentWindow)frame.contentWindow.postMessage({type:'rna.dataTemplates.project'},'*');};
    el('NavigationSearch').oninput=renderNavigation;
    el('Preview').onclick=function(){controls();checkAndPreview(null,true);};
    el('Apply').onclick=function(){run(async function(){var latest=(await send('getPluginSourceState',{pluginId:selected.pluginId})).stamp;
      if(latest!==candidateStamp){await checkAndPreview(latest);status('Исходники изменились. Проверьте обновлённый предпросмотр и нажмите «Применить» ещё раз.');return;}
      selected=await send('applyPlugin',{pluginId:selected.pluginId,candidateId:candidate.candidateId});candidate=null;candidateStamp=null;delete publishedSourceStamps[selected.pluginId];await refresh();
      status('');});};
    el('Versions').addEventListener('toggle',function(){if(this.open){versionCursor=null;loadVersionPage();}else versionEpoch++;});
    el('VersionMore').onclick=function(){if(versionCursor)loadVersionPage(versionCursor);};
    document.addEventListener('pointerdown',function(event){if(el('Versions').open&&!el('Versions').contains(event.target))el('Versions').open=false;},{signal:events.signal});
    el('Versions').addEventListener('keydown',function(event){if(event.key==='Escape'&&this.open){this.open=false;this.querySelector('summary').focus();event.preventDefault();event.stopPropagation();}});
    el('Reopen').onclick=function(){el('Reopen').closest('details').open=false;run(async function(){await showView(false);candidate=null;candidateStamp=null;publishedSourceStamps[selected.pluginId]=sourceStamp;});};
    el('Disable').onclick=function(){el('Disable').closest('details').open=false;run(async function(){selected=await send('disablePlugin',{pluginId:selected.pluginId});candidate=null;candidateStamp=null;await closeView();await refresh();status('Плагин отключён. Исходники и данные сохранены.');});};
    el('NavigationToggle').onclick=function(){var pane=el('NavigationPane');pane.hidden=!pane.hidden;
      el('NavigationToggle').setAttribute('aria-expanded',String(!pane.hidden));};
    var theme=new MutationObserver(syncAppearance);theme.observe(document.documentElement,{attributes:true,attributeFilter:['data-theme','data-accent']});
    function updateItem(item){if(item&&item.pluginId===target.pluginId){selected=item;RNAssistantDockWorkspace.setTitle(key(target),item.name);controls();}}
    async function ready(){
      if(startup){await startup.catch(function(){});return;}
      if(disposed||!previewOpen)return;
      if(view){startSourceWatch();return;}
      var current=epoch;
      startup=(async function(){
        if(!RNAssistantPlugins.item(target.pluginId))await refresh();
        if(current!==epoch||!previewOpen||disposed)return;
        selected=RNAssistantPlugins.item(target.pluginId);
        if(!selected)throw new Error('Плагин больше не доступен на панели.');
        updateItem(selected);renderNavigation();
        try{sourceStamp=(await send('getPluginSourceState',{pluginId:selected.pluginId})).stamp;}catch(error){status(error.message,true);}
        if(current!==epoch||!previewOpen||disposed)return;
        if(publishedSourceStamps[selected.pluginId]!==sourceStamp)await checkAndPreview(sourceStamp);
        if(current!==epoch||!previewOpen||disposed)return;
        if(!view&&selected.enabled)await showView(false);
        if(current===epoch&&previewOpen&&!disposed)startSourceWatch();
      })();
      try{await startup;}catch(error){if(!disposed&&current===epoch)previewSurface.fail({title:target.title},error.detail||error.message);}
      finally{startup=null;if(current!==epoch&&previewOpen&&!disposed)ready();}
    }
    controls();
    return {element:mount,pluginId:target.pluginId,ready:ready,updateItem:updateItem,
      activate:function(){previewOpen=true;root.hidden=false;browserView.activate();syncPanelView();controls();renderVisualDrafts();ready();},
      deactivate:function(){previewOpen=false;if(startup||previewTask)epoch++;stopSourceWatch();browserView.deactivate();syncPanelView();if(pluginAnnotations)pluginAnnotations.collapseEditor();},
      layout:browserView.layout,
      onChatSnapshot:onChatSnapshot,
      isBusy:function(chatTarget){return (busy||previewBusy)&&(!chatTarget||selected&&selected.sourceWorkspaceId===chatTarget.workspaceId);},
      invalidate:async function(item){epoch++;versionEpoch++;stopSourceWatch();sourceStamp=null;candidate=null;candidateStamp=null;updateItem(item);await closeView();if(previewOpen)ready();},
      dispose:function(){if(disposed)return;disposed=true;epoch++;versionEpoch++;previewOpen=false;events.abort();theme.disconnect();clearTimeout(statusTimer);stopSourceWatch();
        RNAssistantNavigationTree.unmount(el('NavigationTree'));closeView().catch(function(){});RNAssistantViewerRegistry.unmount(mount);root.remove();}
    };
  }
  window.RNAssistantPluginView={key:key,create:create};
})();
