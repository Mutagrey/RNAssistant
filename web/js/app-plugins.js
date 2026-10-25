(function () {
  'use strict';
  var items=[],selected=null,candidate=null,view=null,busy=false,epoch=0;
  var previewBusy=false,previewTask=null,sourceStamp=null,candidateStamp=null,sourceTimer=null,sourceDebounce=null;
  var selectionSerial=0;
  var previewOpen=false,pluginWidth=520,pluginWidthPreference=520;
  var previewSurface=null;
  var pluginInspector=null,pluginAnnotations=null,pluginAnnotateButton=null,pluginInspectStatus=null,pluginDraftBox=null;
  var visualSessionId=null;
  var savedIcons=[],selectedIconId='puzzle',customSource=false,editingPlugin=null;
  var frameReady=false,pendingNavigation=null;
  var browsingRelease=null,publishedSourceStamps=Object.create(null),versionCursor=null,versionEpoch=0;
  try{pluginWidthPreference=Number(window.localStorage.getItem('rnassistant.ui.pluginPreview.width'))||520;
    pluginWidth=pluginWidthPreference;}
  catch(_){/* Optional layout preference. */}
  var navigation={projectName:'Проект данных',view:'sources',templateId:'',templates:[],expanded:{project:true,templates:true,dictionary:true}};
  function el(id){return window.RNAssistantWorkspaceChat.node(id)||document.getElementById('plugin'+id);}
  function ownsChat(){return window.RNAssistantWorkspaceChat.owner()==='plugin'&&
    document.getElementById('tab-chat').classList.contains('active');}
  function chatStatus(text,error){if(ownsChat()){el('Notice').textContent=text||'';el('Notice').dataset.error=error?'true':'false';}}
  var statusTimer=null;
  function status(text,error){clearTimeout(statusTimer);
    [el('Status'),el('WorkbenchStatus')].forEach(function(node){node.textContent=text||'';node.setAttribute('role',error?'alert':'status');});
    if(text&&!error)statusTimer=setTimeout(function(){[el('Status'),el('WorkbenchStatus')].forEach(function(node){node.textContent='';});},4500);}
  function syncRailSelection(){
    if(!document.getElementById('tab-chat').classList.contains('active'))return;
    var activeId=ownsChat()&&selected&&selected.pluginId;
    document.querySelectorAll('.section-rail [data-section="chat"], #pluginRail [data-plugin-id]').forEach(function(node){
      var active=node.dataset.pluginId?node.dataset.pluginId===activeId:!activeId;
      node.classList.toggle('active',active);
      if(active)node.setAttribute('aria-current','page');else node.removeAttribute('aria-current');
    });
  }
  function button(label,action){var node=document.createElement('button');node.type='button';node.textContent=label;node.onclick=action;return node;}
  function dataPlugin(){return selected&&selected.pluginId==='data-templates';}
  function icon(name){return window.RNAssistantNavigationTree.icon(name);}
  function syncAppearance(){
    if(!dataPlugin())return;
    var frame=el('PreviewMount').querySelector('iframe');
    if(!frame||!frame.contentWindow)return;
    var root=document.documentElement,style=getComputedStyle(root);
    var names={panel:'--surface',secondary:'--surface-subtle',secondaryHover:'--surface-hover',
      text:'--ink',muted:'--ink-muted',line:'--line',lineStrong:'--line-strong',
      accent:'--accent',accentHover:'--accent-hover',accentInk:'--accent-ink',
      accentSoft:'--accent-soft',danger:'--danger',success:'--success'};
    var colors={};Object.keys(names).forEach(function(key){colors[key]=style.getPropertyValue(names[key]).trim();});
    frame.contentWindow.postMessage({type:'rna.appearance',theme:root.dataset.theme==='dark'?'dark':'light',colors:colors},'*');
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
  });
  async function run(action){if(busy)return;busy=true;controls();try{await action();}catch(error){status(error.message,true);}finally{busy=false;controls();}}
  function controls(){['Preview','Apply','Reopen','Disable'].forEach(function(id){el(id).disabled=busy||previewBusy||!selected||
    id==='Apply'&&(!candidate||!candidate.hasChanges||!!browsingRelease)||(id==='Reopen'||id==='Disable')&&!selected.enabled;});
    el('Apply').hidden=!candidate||!candidate.hasChanges||!selected||!!browsingRelease;
    el('RestoreVersion').hidden=!browsingRelease;el('RestoreVersion').disabled=busy||previewBusy||!selected;
    el('DecisionStrip').hidden=el('Apply').hidden&&el('RestoreVersion').hidden;
    el('DecisionText').textContent=browsingRelease?'Сохранённая версия v'+browsingRelease.version:
      !el('Apply').hidden?'Проверенные изменения исходников':'';
    if(ownsChat()&&window.RNAssistantProjectChats)window.RNAssistantProjectChats.refreshControls();
    if(pluginInspector)pluginInspector.refresh();
    el('CreateSubmit').disabled=busy;}
  async function refresh(){var result=await send('getPlugins',{});items=result.items;var rail=el('Rail');
    if(result.defaultSourceParent)el('DefaultSourcePath').textContent=result.defaultSourceParent;
    if(window.RNAssistantRailPopovers)window.RNAssistantRailPopovers.hide();rail.replaceChildren();items.forEach(function(item){
    var node=button('',function(){select(item.pluginId);});node.innerHTML=window.RNAssistantIcons.renderPlugin(item.icon || (item.pluginId==='data-templates'?'dataTemplates':'puzzle'),item.iconSvg,item.iconColor);node.className='rail-button';node.setAttribute('aria-label',item.name);node.dataset.railLabel=item.name;node.dataset.railPopover='plugin-item';node.dataset.railError=item.iconError||'';node.dataset.pluginId=item.pluginId;node.dataset.section='plugins';rail.appendChild(node);
  });syncRailSelection();if(result.error)status(result.error,true);else if(items.some(function(item){return item.iconError;}))status('У одного из плагинов некорректная SVG-иконка. Исправьте исходник и проверьте пакет.',true);return items;}
  function currentChat(){var snapshot=window.RNAssistantProjectChats.snapshot();
    return ownsChat()&&selected&&snapshot&&snapshot.project&&
      snapshot.project.workspaceId===selected.sourceWorkspaceId?snapshot:null;}
  function pluginChatTarget(){var chat=currentChat();return chat&&chat.project&&chat.sessionId ?
    {workspaceId:chat.project.workspaceId,sessionId:chat.sessionId}:null;}
  function pluginDraft(){return window.RNAssistantChatDrafts.readWorkspace(pluginChatTarget())||null;}
  function renderVisualDrafts(){
    if(!pluginDraftBox)return;
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
      statusNode:pluginInspectStatus,draftBox:pluginDraftBox,
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
    window.RNAssistantPluginAnnotations=pluginAnnotations;
    pluginInspector.reset();
    renderVisualDrafts();
  }
  function disposeVisual(){if(pluginAnnotations){pluginAnnotations.dispose();pluginAnnotations=null;}if(pluginInspector){pluginInspector.dispose();pluginInspector=null;}window.RNAssistantPluginAnnotations=null;}
  async function closeView(){frameReady=false;pendingNavigation=null;disposeVisual();previewSurface.clear();var old=view;view=null;el('PreviewFrameHost').replaceChildren();syncPanelView();if(old)await send('closePlugin',{instanceId:old.instanceId});}
  async function showView(preview,releaseId){var current=epoch,previous=view,opened;
    var label={title:selected.name,path:releaseId?'Сохранённая версия':preview?'Неприменённые исходники':'Активная версия'};
    try{opened=await send('openPlugin',{
      pluginId:selected.pluginId,candidateId:preview&&!releaseId?candidate.candidateId:null,releaseId:releaseId||null});}
    catch(error){if(current===epoch)previewSurface.fail(label,error.detail||error.message);throw error;}
    if(current!==epoch){await send('closePlugin',{instanceId:opened.instanceId});return;}
    disposeVisual();view=opened;frameReady=false;if(dataPlugin())pendingNavigation={view:navigation.view,templateId:navigation.templateId};
    var frame=document.createElement('iframe');frame.title=selected.name+(releaseId?' — сохранённая версия':preview?' — предпросмотр':'');frame.sandbox='allow-scripts allow-same-origin allow-downloads';frame.referrerPolicy='no-referrer';
    if(dataPlugin())frame.addEventListener('load',syncAppearance);
    previewSurface.watch(frame,label);
    el('PreviewFrameHost').replaceChildren(frame);
    createVisual(frame);
    frame.src=view.entryUrl;
    syncPanelView();
    if(previous)await send('closePlugin',{instanceId:previous.instanceId});
    status('');}
  function stopSourceWatch(){clearInterval(sourceTimer);clearTimeout(sourceDebounce);sourceTimer=null;sourceDebounce=null;sourceStamp=null;}
  async function checkAndPreview(stamp,force){
    if(!selected||browsingRelease)return false;
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
          status('Предпросмотр не обновлён: '+error.message,true);}return false;}
    })();
    previewTask=task;
    try{return await task;}finally{if(previewTask===task)previewTask=null;previewBusy=false;controls();}
  }
  async function pollSource(){if(!selected||busy||browsingRelease||el('Workbench').hidden||!previewOpen||document.hidden||!el('Workbench').closest('.panel.active')||previewBusy)return;
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
    if(ownsChat())window.RNAssistantChatInspector.syncSwitcher();
  }
  function pluginWidthBounds(){
    var layout=document.getElementById('projectChatLayout'),nav=layout.querySelector('.chat-sidebar');
    if(!layout.clientWidth)return {min:320,max:780};
    var navWidth=window.RNAssistantShellLayout.isNavigationOpen()?nav.getBoundingClientRect().width:0;
    return {min:320,max:Math.max(320,Math.min(780,layout.clientWidth-navWidth-360))};
  }
  function setPluginWidth(value,persist){
    var bounds=pluginWidthBounds();pluginWidth=Math.max(bounds.min,Math.min(bounds.max,value));
    if(persist)pluginWidthPreference=pluginWidth;
    document.getElementById('projectChatLayout').style.setProperty('--plugin-surface-width',pluginWidth+'px');
    window.RNAssistantSplitter.update(el('SurfaceSplitter'),pluginWidth,bounds);
    if(persist)try{window.localStorage.setItem('rnassistant.ui.pluginPreview.width',String(pluginWidth));}catch(_){/* Optional. */}
  }
  function refreshPanelLayout(){
    var diagnostics=ownsChat()&&window.RNAssistantChatInspector.isOpen();
    var visible=ownsChat()&&previewOpen&&!diagnostics;
    if(!visible&&pluginInspector)pluginInspector.deactivate();
    el('PreviewMount').hidden=!visible;
    el('SurfaceSplitter').hidden=!visible;
    el('Workbench').hidden=!visible;
    document.getElementById('projectChatLayout').classList.toggle('plugin-surface-open',visible);
    var toggle=document.getElementById('workspaceInspectorToggle');
    var target=visible&&window.innerWidth<=1100?el('PreviewMount').querySelector('.preview-surface-panel-head'):
      document.querySelector('#workspaceChatView .workspace-chat-head');
    if(toggle.parentElement!==target){var focused=document.activeElement===toggle;target.appendChild(toggle);
      if(focused)toggle.focus({preventScroll:true});}
    if(visible)setPluginWidth(pluginWidthPreference,false);
    syncRailSelection();
    syncPanelView();
  }
  function setPreviewOpen(value){
    previewOpen=!!value;
    if(previewOpen&&ownsChat())window.RNAssistantChatInspector.setOpen(false);
    updateSurface(true);
  }
  function updateSurface(refresh){var open=previewOpen;
    window.RNAssistantWorkspaceChat.mount('plugin',document.getElementById('projectChatLayout'));
    if(open&&selected&&!view&&el('PreviewMount').querySelector('.preview-surface-bar').hidden)
      previewSurface.waiting({title:selected.name,path:'Исходники'},'Подготовка предпросмотра…');
    window.RNAssistantProjectChats.render();
    refreshPanelLayout();
    if(open||window.RNAssistantChatInspector.isOpen())refreshSplitPanes();
    if(refresh&&open&&!browsingRelease){if(!view)checkAndPreview();else pollSource();}}
  async function select(id,sessionId){
    var item=items.find(function(value){return value.pluginId===id;});
    if(!item||busy)return;
    var route=++selectionSerial;
    try{
      await send('getPluginChat',{pluginId:id});
      if(route!==selectionSerial)return;
      await window.RNAssistantProjectChats.open(item.sourceWorkspaceId,sessionId);
      if(route!==selectionSerial||window.RNAssistantProjectChats.workspaceId()!==item.sourceWorkspaceId||
          !document.getElementById('tab-chat').classList.contains('active'))return;
      if(selected&&selected.pluginId===id){
        if(!sessionId)previewOpen=true;
        updateSurface(false);window.RNAssistantChatInspector.setOpen(false);
        if(previewOpen&&(!view||!sourceStamp))await checkAndPreview();
        else await pollSource();
        if(!sourceTimer)startSourceWatch();
        return;
      }
      await run(async function(){
        epoch++;stopSourceWatch();await closeView();selected=item;candidate=null;candidateStamp=null;browsingRelease=null;
        versionEpoch++;versionCursor=null;el('Versions').open=false;
        previewOpen=true;
        navigation={projectName:'Проект данных',view:'sources',templateId:'',templates:[],expanded:{project:true,templates:true,dictionary:true}};
        renderNavigation();updateSurface(false);
        window.RNAssistantChatInspector.setOpen(false);
        try{sourceStamp=(await send('getPluginSourceState',{pluginId:id})).stamp;}catch(error){status(error.message,true);}
        if(previewOpen&&publishedSourceStamps[id]!==sourceStamp)await checkAndPreview(sourceStamp);
        if(!view&&selected.enabled)await showView(false);
        startSourceWatch();
      });
    }catch(error){if(route===selectionSerial)status(error.detail||error.message,true);}
  }
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
        row.type='button';row.className='plugin-version-row'+(item.active?' is-active':'')+
          (browsingRelease&&browsingRelease.releaseId===item.releaseId?' is-selected':'');
        name.textContent='v'+item.version;marker.textContent=item.active?'Активная':'';
        var date=new Date(item.capturedUtc);
        meta.textContent=(Number.isNaN(date.getTime())?'Сохранённая версия':date.toLocaleString('ru-RU',{day:'numeric',month:'short',year:'numeric',hour:'2-digit',minute:'2-digit'}))+
          ' · '+item.releaseId.slice(-8);
        row.append(name,marker,meta);row.title=item.name+' · '+item.releaseId;
        row.onclick=function(){el('Versions').open=false;run(async function(){
          var previous=browsingRelease;browsingRelease=item.active?null:item;epoch++;clearTimeout(sourceDebounce);
          try{await showView(!item.active,item.active?null:item.releaseId);}
          catch(error){browsingRelease=previous;throw error;}
          controls();
        });};
        list.appendChild(row);
      });
      versionCursor=page.nextCursor;el('VersionMore').hidden=!versionCursor;
    }catch(error){if(current===epoch&&request===versionEpoch&&el('Versions').open){
      if(!cursor)list.textContent=error.detail||error.message;
      else status(error.detail||error.message,true);
      versionCursor=null;el('VersionMore').hidden=true;
    }}
  }
  function setIconEditor(open){el('IconEditor').hidden=!open;el('IconTrigger').setAttribute('aria-expanded',String(open));
    if(!open&&el('IconEditor').contains(document.activeElement))el('IconTrigger').focus();}
  function updateSourceLocation(){var connecting=el('Connect').checked,manual=connecting||customSource;
    el('SourceLocation').hidden=connecting||!!editingPlugin;el('SourceFolder').hidden=!manual||!!editingPlugin;
    el('Root').required=manual&&!editingPlugin;el('Root').disabled=!manual||!!editingPlugin;
    el('SourceChange').textContent=customSource?'Использовать папку по умолчанию':'Указать другую папку';
  }
  function iconColor(){var mode=el('IconColorMode').value;return mode==='custom'?el('IconColor').value:mode==='original'?'original':null;}
  function updateCreatePreview(){var connecting=el('Connect').checked,name=el('Name').value.trim();
    el('CreateTitle').textContent=connecting?'Подключить плагин':name||'Новый плагин';
    el('CreateSubtitle').textContent=editingPlugin?'Название и иконка плагина в боковой панели.':connecting?'Укажите папку с plugin.json.':'Нажмите на значок, чтобы выбрать или нарисовать иконку.';
    el('IconTrigger').disabled=connecting;el('ColorControl').hidden=connecting;el('IconColor').hidden=el('IconColorMode').value!=='custom';
    var saved=selectedIconId==='current'&&editingPlugin?{svg:editingPlugin.iconSvg}:savedIcons.find(function(item){return item.id===selectedIconId;});
    el('IconPreview').innerHTML=window.RNAssistantIcons.renderPlugin(selectedIconId==='draw'?'spark':selectedIconId,saved&&saved.svg,iconColor());
  }
  function iconOption(id,name,svg){
    var choice=button('',function(){selectedIconId=id;renderIconChoices();if(id==='draw')el('IconDescription').focus();else setIconEditor(false);});
    choice.className='plugin-icon-option';choice.dataset.iconId=id;choice.setAttribute('aria-label',name);
    choice.setAttribute('aria-pressed',String(selectedIconId===id));choice.title=name;
    choice.innerHTML=window.RNAssistantIcons.renderPlugin(id==='draw'?'spark':id,svg);
    var caption=document.createElement('span');caption.textContent=name;choice.appendChild(caption);return choice;
  }
  function renderIconChoices(){
    var builtins=el('BuiltinIcons'),saved=el('SavedIcons');builtins.replaceChildren();saved.replaceChildren();
    builtins.appendChild(iconOption('draw','Нарисовать'));
    if(editingPlugin&&editingPlugin.iconSvg)builtins.appendChild(iconOption('current','Текущая иконка',editingPlugin.iconSvg));
    window.RNAssistantIcons.pluginChoices.forEach(function(choice){builtins.appendChild(iconOption(choice[0],choice[1]));});
    if(!savedIcons.length){var empty=document.createElement('p');empty.className='plugin-icon-empty';empty.textContent='Пока нет сохранённых иконок.';saved.appendChild(empty);}
    savedIcons.forEach(function(item){
      var card=document.createElement('div');card.className='plugin-icon-saved-card';card.appendChild(iconOption(item.id,item.name,item.svg));
      var remove=button('×',function(){run(async function(){var catalog=await send('deletePluginIcon',{id:item.id});savedIcons=catalog.items;
        if(selectedIconId===item.id)selectedIconId='puzzle';renderIconChoices();status('Иконка удалена из библиотеки. Плагины и их исходники не изменены.');});});
      remove.className='plugin-icon-remove';remove.title='Удалить '+item.name;remove.setAttribute('aria-label','Удалить '+item.name);card.appendChild(remove);saved.appendChild(card);
    });
    el('IconBriefField').hidden=selectedIconId!=='draw';el('IconDescription').disabled=selectedIconId!=='draw';
    updateCreatePreview();
  }
  async function refreshIcons(){var catalog=await send('getPluginIcons',{});savedIcons=catalog.items||[];renderIconChoices();}
  async function drawIcon(){var brief=el('IconDescription').value.trim();
    if(!brief)throw new Error('Опишите иконку перед рисованием.');
    var draw=el('IconDrawButton'),note=el('IconDrawStatus');draw.disabled=true;note.textContent='Модель рисует SVG…';
    try{var item=await send('drawPluginIcon',{name:el('Name').value.trim()||'Новый плагин',description:brief,iconColor:iconColor()});
      if(!savedIcons.some(function(saved){return saved.id===item.id;}))savedIcons.push(item);
      selectedIconId=item.id;renderIconChoices();note.textContent=item.previewSimulated?'Пример: иконка создана в памяти вкладки.':'Иконка готова и сохранена в библиотеке.';status('');return item;
    }catch(error){note.textContent=error.message;throw error;}finally{draw.disabled=false;}}
  async function openManager(item){
    selectionSerial++;epoch++;stopSourceWatch();await closeView();editingPlugin=item||null;
    switchTab('plugins');el('Manager').hidden=false;el('Workbench').hidden=true;status('');
    el('CreateForm').reset();el('Connect').checked=false;customSource=false;
    selectedIconId=item?(item.iconSvg?'current':item.icon||'puzzle'):'puzzle';
    var color=item&&item.iconColor;
    el('IconColorMode').value=color==='original'?'original':color?'custom':'theme';el('IconColor').value=color&&color!=='original'?color:'#368453';
    el('Name').value=item?item.name:'';el('Task').closest('label').hidden=!!item;
    el('Connect').closest('label').hidden=!!item;el('EditCancel').hidden=!item;
    el('Manager').querySelector('.plugin-vendors').hidden=!!item;
    el('Connect').onchange();setIconEditor(false);await refreshIcons();el('Name').focus();
  }
  function registerRailPopovers(){var popovers=window.RNAssistantRailPopovers;if(!popovers)return;
    popovers.register('plugin-item',function(anchor,body){
      if(anchor.dataset.railError){var problem=document.createElement('p');problem.className='rail-hover-empty';problem.textContent=anchor.dataset.railError;body.appendChild(problem);}
      var line=document.createElement('div');line.className='rail-hover-separator';body.appendChild(line);
      body.appendChild(popovers.action('Редактировать',window.RNAssistantIcons.render('edit'),function(){
        var item=items.find(function(value){return value.pluginId===anchor.dataset.pluginId;});popovers.hide();
        if(item)run(function(){return openManager(item);});}));
      var remove=popovers.action('Удалить с панели',window.RNAssistantIcons.render('trash'),function(){var id=anchor.dataset.pluginId,name=anchor.dataset.railLabel;
        if(!window.confirm('Убрать «'+name+'» с панели? Исходники, данные и опубликованная версия сохранятся. Плагин можно вернуть через «+».'))return;
        popovers.hide();run(async function(){await send('removePlugin',{pluginId:id});if(selected&&selected.pluginId===id){epoch++;stopSourceWatch();await closeView();selected=null;candidate=null;candidateStamp=null;
          switchTab('plugins');el('Manager').hidden=false;el('Workbench').hidden=true;}await refresh();status('Плагин убран с панели. Вернуть его можно через «+».');});});
      remove.className='rail-hover-danger';body.appendChild(remove);
    });
    popovers.register('plugin-add',async function(anchor,body,isCurrent){body.textContent='Загружаю…';
      var catalog=await send('getRemovedPlugins',{});if(!isCurrent())return;body.replaceChildren();
      if(!(catalog.items||[]).length){var empty=document.createElement('p');empty.className='rail-hover-empty';empty.textContent='Удалённых с панели плагинов нет.';body.appendChild(empty);}
      (catalog.items||[]).forEach(function(item){var restore=popovers.action(item.name,window.RNAssistantIcons.renderPlugin(item.icon||'puzzle',item.iconSvg,item.iconColor),function(){popovers.hide();run(async function(){await send('restorePlugin',{pluginId:item.pluginId});await refresh();status('Плагин возвращён на панель.');});});
        body.appendChild(restore);});
      var line=document.createElement('div');line.className='rail-hover-separator';body.appendChild(line);
      var create=popovers.action('Создать плагин',window.RNAssistantIcons.render('plus'),function(){popovers.hide();el('Add').click();});
      body.appendChild(create);
    });
  }
  function bind(){
    window.RNAssistantSplitter.bind(el('SurfaceSplitter'),{direction:-1,getValue:function(){return pluginWidth;},
      setValue:setPluginWidth,bounds:pluginWidthBounds,defaultValue:function(){return 520;},
      enabled:function(){return ownsChat()&&previewOpen&&window.innerWidth>1100;},
      collapse:function(){setPreviewOpen(false);}});
    window.addEventListener('resize',function(){if(ownsChat())refreshPanelLayout();});
    previewSurface=window.RNAssistantPreviewSurface.attach(el('PreviewMount'),{
      reloadButton:el('Preview'),reloadLabel:'Проверить исходники и обновить предпросмотр'});
    pluginAnnotateButton=document.createElement('button');pluginAnnotateButton.type='button';
    pluginAnnotateButton.className='icon-btn';pluginAnnotateButton.dataset.browserControl='true';
    pluginAnnotateButton.disabled=true;previewSurface.actions.prepend(pluginAnnotateButton);
    pluginInspectStatus=document.createElement('span');pluginInspectStatus.className='html-workspace-inspect-status';
    pluginInspectStatus.setAttribute('role','status');el('Workbench').appendChild(pluginInspectStatus);
    pluginDraftBox=document.createElement('div');pluginDraftBox.className='attachment-drafts has-visual-annotations';
    pluginDraftBox.id='pluginAnnotationDrafts';pluginDraftBox.hidden=true;
    el('Form').querySelector('#attachmentDrafts').after(pluginDraftBox);
    var toolbar=el('Toolbar');
    [el('Versions'),el('NavigationToggle')]
      .forEach(function(control){previewSurface.actions.insertBefore(control,pluginAnnotateButton);});
    toolbar.remove();
    syncPanelView();
    registerRailPopovers();
    new MutationObserver(function(){syncAppearance();renderIconChoices();el('Rail').querySelectorAll('[data-plugin-id]').forEach(function(node){var item=items.find(function(value){return value.pluginId===node.dataset.pluginId;});if(item)node.innerHTML=window.RNAssistantIcons.renderPlugin(item.icon||'puzzle',item.iconSvg,item.iconColor);});}).observe(document.documentElement,{attributes:true,attributeFilter:['data-theme','data-accent']});
    el('NavigationProject').onclick=function(){var frame=el('PreviewMount').querySelector('iframe');if(frame&&frame.contentWindow)frame.contentWindow.postMessage({type:'rna.dataTemplates.project'},'*');};
    el('NavigationSearch').oninput=renderNavigation;
    el('Add').onclick=function(){run(function(){return openManager(null);});};
    el('EditCancel').onclick=function(){if(editingPlugin){var id=editingPlugin.pluginId;editingPlugin=null;select(id);}};
    el('Name').oninput=updateCreatePreview;
    el('IconColorMode').onchange=updateCreatePreview;el('IconColor').oninput=updateCreatePreview;
    el('IconTrigger').onclick=function(){setIconEditor(el('IconEditor').hidden);if(!el('IconEditor').hidden)el('BuiltinIcons').querySelector('button').focus();};
    el('IconEditor').onkeydown=function(event){if(event.key==='Escape'){event.preventDefault();setIconEditor(false);}};
    el('IconDrawButton').onclick=function(){run(drawIcon);};
    el('IconImportButton').onclick=function(){run(async function(){var item=await send('savePluginIcon',{name:el('IconImportName').value.trim(),svg:el('IconImportSvg').value.trim()});
      await refreshIcons();selectedIconId=item.id;renderIconChoices();el('IconImportName').value='';el('IconImportSvg').value='';status('SVG-иконка сохранена.');});};
    el('SourceChange').onclick=function(){customSource=!customSource;updateSourceLocation();if(customSource)el('Root').focus();};
    el('CreateForm').onsubmit=function(event){event.preventDefault();run(async function(){var connecting=el('Connect').checked,task=el('Task').value.trim(),iconBrief=el('IconDescription').value.trim();
      if(!connecting&&selectedIconId==='draw'){await drawIcon();iconBrief='';}
      if(selectedIconId!=='draw')iconBrief='';
      if(editingPlugin){
        var changed=await send('updatePluginAppearance',{pluginId:editingPlugin.pluginId,name:el('Name').value.trim(),iconId:selectedIconId==='current'?null:selectedIconId,iconColor:iconColor()});
        editingPlugin=null;await refresh();busy=false;await select(changed.pluginId);status('Название и иконка сохранены.');return;
      }
      var created=await send(connecting?'connectPlugin':'createPlugin',{name:el('Name').value,rootPath:connecting||customSource?el('Root').value.trim():null,description:task,iconDescription:iconBrief,iconId:selectedIconId,iconColor:iconColor()});
      await refresh();status((connecting?'Исходники подключены: ':'Исходники созданы: ')+created.sourceRoot);busy=false;await select(created.pluginId);
      if(!connecting&&(task||iconBrief)){var message='Загрузи навыки workspace.plugin_authoring и workspace.files. Прочитай README.md и plugin.json. ';
        if(task)message+='Создай плагин по задаче: '+task+'. ';
        if(iconBrief)message+='Нарисуй SVG-иконку по описанию: '+iconBrief+'. Создай icon.svg, добавь его в files и укажи в plugin.json. ';
        message+='После изменений проверь пакет и предпросмотр; применяй только после подтверждения.';
        if(!currentChat()||!currentChat().sessionId){if(!await window.RNAssistantProjectChats.create())return;}
        el('Input').value=message;el('Input').dispatchEvent(new Event('input',{bubbles:true}));el('Form').requestSubmit();}
      el('CreateForm').reset();el('IconDescription').value='';el('IconImportName').value='';el('IconImportSvg').value='';
      selectedIconId='puzzle';customSource=false;renderIconChoices();el('Connect').onchange();
    });};
    el('Connect').onchange=function(){var connecting=this.checked;el('NewFields').hidden=connecting;el('Name').required=!connecting;el('Task').disabled=connecting;el('IconDescription').disabled=connecting||selectedIconId!=='draw';if(connecting)setIconEditor(false);updateCreatePreview();updateSourceLocation();
      el('CreateSubmit').textContent=editingPlugin?'Сохранить':connecting?'Подключить папку':'Создать плагин';el('ModeHint').textContent=editingPlugin?'Оформление на панели обновится сразу.':connecting?
        'Будет подключён существующий plugin.json без изменения исходников.':'Проверка и публикация доступны в разделе «Разработка».';};
    el('Preview').onclick=function(){browsingRelease=null;controls();checkAndPreview(null,true);};
    el('Apply').onclick=function(){run(async function(){var latest=(await send('getPluginSourceState',{pluginId:selected.pluginId})).stamp;
      if(latest!==candidateStamp){await checkAndPreview(latest);status('Исходники изменились. Проверьте обновлённый предпросмотр и нажмите «Применить» ещё раз.');return;}
      selected=await send('applyPlugin',{pluginId:selected.pluginId,candidateId:candidate.candidateId});candidate=null;candidateStamp=null;delete publishedSourceStamps[selected.pluginId];await refresh();
      status('');});};
    el('RestoreVersion').onclick=function(){if(!browsingRelease||!selected)return;run(async function(){
      var id=selected.pluginId,target=browsingRelease.releaseId,expected=selected.releaseId;
      clearTimeout(sourceDebounce);
      selected=await send('restorePluginVersion',{pluginId:id,releaseId:target,expectedActiveReleaseId:expected});
      candidate=null;candidateStamp=null;browsingRelease=null;versionCursor=null;versionEpoch++;
      await showView(false);await refresh();
      try{sourceStamp=(await send('getPluginSourceState',{pluginId:id})).stamp;publishedSourceStamps[id]=sourceStamp;}
      catch(error){status('Версия восстановлена, но проверка исходников недоступна: '+error.message,true);return;}
      status('Сохранённая версия восстановлена. Исходники не изменены.');
    });};
    el('Versions').addEventListener('toggle',function(){if(this.open){versionCursor=null;loadVersionPage();}else versionEpoch++;});
    el('VersionMore').onclick=function(){if(versionCursor)loadVersionPage(versionCursor);};
    document.addEventListener('pointerdown',function(event){if(el('Versions').open&&!event.target.closest('#pluginVersions'))el('Versions').open=false;});
    el('Versions').addEventListener('keydown',function(event){if(event.key==='Escape'&&this.open){this.open=false;this.querySelector('summary').focus();event.preventDefault();event.stopPropagation();}});
    el('Reopen').onclick=function(){el('Reopen').closest('details').open=false;run(async function(){await showView(false);browsingRelease=null;candidate=null;candidateStamp=null;publishedSourceStamps[selected.pluginId]=sourceStamp;});};
    el('Disable').onclick=function(){el('Disable').closest('details').open=false;run(async function(){selected=await send('disablePlugin',{pluginId:selected.pluginId});candidate=null;candidateStamp=null;browsingRelease=null;await closeView();await refresh();status('Плагин отключён. Исходники и данные сохранены.');});};
    el('NavigationToggle').onclick=function(){var pane=el('NavigationPane');pane.hidden=!pane.hidden;
      el('NavigationToggle').setAttribute('aria-expanded',String(!pane.hidden));};
    el('VendorsRefresh').onclick=function(){run(async function(){var catalog=await send('getPluginVendors',{});el('Vendors').replaceChildren();catalog.items.forEach(function(item){var row=document.createElement('p');row.textContent=item.id+' @ '+item.version+' · '+item.license;el('Vendors').appendChild(row);});status('Каталог обновлён. Проверка хешей выполняется при разрешении зависимости.');});};
    el('VendorImport').onclick=function(){run(async function(){var result=await send('importPluginVendor',{rootPath:el('VendorRoot').value});status('Добавлен '+result.id+' @ '+result.version);});};controls();
  }
  window.RNAssistantPlugins={preview:{hasSource:function(){return ownsChat()&&!!selected;},isOpen:function(){return ownsChat()&&previewOpen;},
    setOpen:setPreviewOpen,hide:function(){previewOpen=false;refreshPanelLayout();},refreshLayout:refreshPanelLayout},
    bind:bind,refresh:refresh,show:function(){el('Manager').hidden=false;},select:select,
    onChatSnapshot:onChatSnapshot,isChatBusy:function(){return ownsChat()&&busy;},
    sourceWorkspaceId:function(){return ownsChat()&&selected&&selected.sourceWorkspaceId;},
    selectedId:function(){return selected&&selected.pluginId;}};
})();
