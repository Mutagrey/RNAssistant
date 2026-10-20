(function () {
  'use strict';
  var items=[],selected=null,candidate=null,view=null,chat=null,busy=false,chatBusy=false,poll=null,epoch=0;
  var previewBusy=false,selecting=false,sourceStamp=null,candidateStamp=null,sourceTimer=null,sourceDebounce=null;
  var chatComposer=null,pendingChatSend=null,chatEpoch=0,stopTitleWatch=null;
  var developmentOpen=false,developmentToggle=null;
  var savedIcons=[],selectedIconId='puzzle',customSource=false,editingPlugin=null;
  var frameReady=false,pendingNavigation=null;
  var navigation={projectName:'Проект данных',view:'sources',templateId:'',templates:[],expanded:{project:true,templates:true,dictionary:true}};
  function el(id){return document.getElementById('plugin'+id);}
  var statusTimer=null;
  function status(text,error){clearTimeout(statusTimer);el('Status').textContent=text || '';el('Status').setAttribute('role',error?'alert':'status');
    if(text&&!error)statusTimer=setTimeout(function(){el('Status').textContent='';},4500);}
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
    if(window.RNAssistantShellLayout&&window.RNAssistantShellLayout.mode()!=='wide')
      window.RNAssistantShellLayout.setNavigationOpen(false);
    pendingNavigation={view:viewName,templateId:templateId||''};flushNavigation();
  }
  function renderNavigation(){
    var tree=el('NavigationTree');
    if(!dataPlugin()){tree.replaceChildren();return;}
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
    el('Title').textContent=navigation.projectName;
    renderNavigation();
  });
  async function run(action){if(busy)return;busy=true;controls();try{await action();}catch(error){status(error.message,true);}finally{busy=false;controls();}}
  function controls(){['Preview','Apply','Reopen','Disable'].forEach(function(id){el(id).disabled=busy||previewBusy||!selected||
    id==='Apply'&&!candidate||(id==='Reopen'||id==='Disable')&&!selected.enabled;});
    if(chatComposer)chatComposer.lock(chatBusy||!chat||!!chat.pendingId||!!(chat.state&&(chat.state.Status||chat.state.status)==='running'));
    el('ChatNew').disabled=busy||chatBusy||!selected;el('ChatHistory').disabled=!chat;
    el('Send').disabled=chatBusy||!selected||!chat||!el('Message').value.trim()||!!(chat&&chat.pendingId)||!!(chat&&chat.state&&(chat.state.Status||chat.state.status)==='running');el('CreateSubmit').disabled=busy;}
  async function refresh(){var result=await send('getPlugins',{});items=result.items;var rail=el('Rail');
    if(result.defaultSourceParent)el('DefaultSourcePath').textContent=result.defaultSourceParent;
    if(window.RNAssistantRailPopovers)window.RNAssistantRailPopovers.hide();rail.replaceChildren();items.forEach(function(item){
    var node=button('',function(){select(item.pluginId);});node.innerHTML=window.RNAssistantIcons.renderPlugin(item.icon || (item.pluginId==='data-templates'?'dataTemplates':'puzzle'),item.iconSvg,item.iconColor);node.className='rail-button';node.setAttribute('aria-label',item.name);node.dataset.railLabel=item.name;node.dataset.railPopover='plugin-item';node.dataset.railError=item.iconError||'';node.dataset.pluginId=item.pluginId;node.dataset.section='plugins';rail.appendChild(node);
  });if(result.error)status(result.error,true);else if(items.some(function(item){return item.iconError;}))status('У одного из плагинов некорректная SVG-иконка. Исправьте исходник и проверьте пакет.',true);return items;}
  async function closeView(){frameReady=false;pendingNavigation=null;if(!view)return;var old=view;view=null;el('PreviewMount').replaceChildren();await send('closePlugin',{instanceId:old.instanceId});}
  async function showView(preview){var current=epoch,previous=view,opened=await send('openPlugin',{pluginId:selected.pluginId,candidateId:preview?candidate.candidateId:null});
    if(current!==epoch){await send('closePlugin',{instanceId:opened.instanceId});return;}
    view=opened;frameReady=false;if(dataPlugin())pendingNavigation={view:navigation.view,templateId:navigation.templateId};
    var frame=document.createElement('iframe');frame.title=selected.name+(preview?' — предпросмотр':'');frame.sandbox='allow-scripts allow-same-origin allow-downloads';frame.referrerPolicy='no-referrer';
    if(dataPlugin())frame.addEventListener('load',syncAppearance);
    frame.src=view.entryUrl;el('PreviewMount').replaceChildren(frame);
    if(previous)await send('closePlugin',{instanceId:previous.instanceId});
    status('');}
  function stopSourceWatch(){clearInterval(sourceTimer);clearTimeout(sourceDebounce);sourceTimer=null;sourceDebounce=null;sourceStamp=null;}
  async function checkAndPreview(stamp){if(!selected||previewBusy)return;previewBusy=true;controls();var id=selected.pluginId,current=epoch;
    try{var source=stamp||((await send('getPluginSourceState',{pluginId:id})).stamp);
      var checked=await send('checkPlugin',{pluginId:id});if(current!==epoch||!selected||selected.pluginId!==id)return;
      candidate=checked;candidateStamp=source;await showView(true);sourceStamp=source;status('');}
    catch(error){if(current===epoch){candidate=null;candidateStamp=null;status('Предпросмотр не обновлён: '+error.message,true);}}
    finally{previewBusy=false;controls();
      if(current!==epoch&&!selecting&&selected&&(!view||!view.preview)&&!el('Workbench').hidden&&developmentOpen)checkAndPreview(sourceStamp);}}
  async function pollSource(){if(!selected||el('Workbench').hidden||!developmentOpen||document.hidden||!el('Workbench').closest('.panel.active')||previewBusy)return;
    var id=selected.pluginId,current=epoch;
    try{var state=await send('getPluginSourceState',{pluginId:id});if(current!==epoch||!selected||selected.pluginId!==id||state.stamp===sourceStamp)return;
      sourceStamp=state.stamp;clearTimeout(sourceDebounce);sourceDebounce=setTimeout(function(){checkAndPreview(state.stamp);},450);
    }catch(error){if(current===epoch)status('Не удалось проверить изменения исходников: '+error.message,true);}}
  function startSourceWatch(){clearInterval(sourceTimer);sourceTimer=setInterval(function(){pollSource();},1200);}
  function updateDevelopment(refresh){var open=developmentOpen;el('Chat').hidden=!open;el('DevelopmentSplitter').hidden=!open;
    developmentToggle.sync();
    el('Workbench').classList.toggle('plugin-developing',open);
    if(open)refreshSplitPanes();
    if(refresh&&open){if(!view||!view.preview)checkAndPreview();else pollSource();}}
  async function select(id,sessionId){selecting=true;switchTab('plugins');try{await run(async function(){epoch++;stopSourceWatch();await closeView();selected=items.find(function(item){return item.pluginId===id;});candidate=null;candidateStamp=null;chat=null;chatEpoch++;
    if(stopTitleWatch){stopTitleWatch();stopTitleWatch=null;}
    if(sessionId){window.RNAssistantWorkspaceChat.rememberSession(selected.sourceWorkspaceId,sessionId);developmentOpen=true;}
    switchTab('plugins');el('Manager').hidden=true;el('Workbench').hidden=false;el('Title').textContent=dataPlugin()?navigation.projectName:selected.name;el('Source').textContent=selected.sourceRoot;
    el('ChatSource').textContent=selected.sourceRoot;el('ChatSource').title=selected.sourceRoot;el('ChatMessages').replaceChildren();el('ChatMessages')._chatSignature=null;
    el('Workbench').classList.toggle('plugin-data-templates',dataPlugin());el('Workbench').classList.toggle('split-layout',dataPlugin());el('Project').hidden=!dataPlugin();
    if(typeof refreshSplitPanes==='function')refreshSplitPanes();
    navigation={projectName:'Проект данных',view:'sources',templateId:'',templates:[],expanded:{project:true,templates:true,dictionary:true}};el('ProjectName').textContent=dataPlugin()?selected.name:'';el('Title').textContent=dataPlugin()?navigation.projectName:selected.name;renderNavigation();
    if(window.RNAssistantShellLayout)window.RNAssistantShellLayout.refresh();
    if(selected.enabled)await showView(false);else developmentOpen=true;
    updateDevelopment(false);
    try{sourceStamp=(await send('getPluginSourceState',{pluginId:id})).stamp;}catch(error){status(error.message,true);}
    if(developmentOpen)await checkAndPreview(sourceStamp);
    startSourceWatch();await readChat();});}finally{selecting=false;
      if(selected&&!el('Workbench').hidden&&developmentOpen&&(!view||!view.preview))checkAndPreview(sourceStamp);}}
  function renderChat(){if(!chat)return;
    el('ChatTitle').textContent=chat.state.title||chat.state.Title||'Чат исходников';
    el('ChatTitle').title=el('ChatTitle').textContent;
    window.RNAssistantWorkspaceChat.renderMessages(el('ChatMessages'),chat);
    var s=chat.state||{},running=(s.Status||s.status)==='running';
    el('ChatStatus').textContent=chat.pendingId?'Нужно подтвердить действие агента.':
      s.interruptedEffectPossible?'Возможное изменение требует проверки. Не повторяйте действие автоматически.':
      (s.Status||s.status)==='interrupted'?'Выполнение прервано. Проверьте ход работы.':running?'Агент работает…':'';
    el('Stop').hidden=!running;el('Approval').hidden=!chat.pendingId;
    el('ApprovalText').textContent=chat.pendingId ? chat.pendingTool+'\n'+chat.pendingArguments : '';
    chatComposer.select(chat.sessionId,s);
    window.RNAssistantWorkspaceChat.renderQuestions(el('Questions'),s.userInput||s.UserInput,function(answers){
      return chatAction(function(workspaceId,sessionId){return send('submitProjectChat',{workspaceId:workspaceId,sessionId:sessionId,input:{sessionId:sessionId,requestId:crypto.randomUUID(),answers:answers}});});});
    controls();
  }
  async function readChat(sessionId){if(!selected)return;
    var id=selected.pluginId,workspaceId=selected.sourceWorkspaceId,current=chatEpoch;
    var remember=!sessionId;sessionId=sessionId||window.RNAssistantWorkspaceChat.selectedSession(workspaceId);
    var value=await send(sessionId?'getProjectChat':'getPluginChat',sessionId?{workspaceId:workspaceId,sessionId:sessionId}:{pluginId:id});
    if(selected&&id===selected.pluginId&&current===chatEpoch){chat=value;
      if(remember)window.RNAssistantWorkspaceChat.rememberSession(workspaceId,value.sessionId);renderChat();}}
  async function createChat(){if(chatBusy||busy||!selected)return;
    var id=selected.pluginId,workspaceId=selected.sourceWorkspaceId,current=++chatEpoch;
    if(stopTitleWatch){stopTitleWatch();stopTitleWatch=null;}
    chatBusy=true;controls();
    try{var value=await send('createProjectChat',{workspaceId:workspaceId,title:'Новый чат'});
      if(selected&&selected.pluginId===id&&current===chatEpoch){chat=value;pendingChatSend=null;
        window.RNAssistantWorkspaceChat.rememberSession(workspaceId,value.sessionId);renderChat();el('Message').focus();}}
    catch(error){if(current===chatEpoch)el('ChatStatus').textContent=error.message;}
    finally{chatBusy=false;controls();}}
  async function chatAction(action){if(chatBusy||!selected||!chat)return;
    var id=selected.pluginId,workspaceId=chat.project.workspaceId,sessionId=chat.sessionId,current=chatEpoch;
    function active(){return selected&&selected.pluginId===id&&chat&&chat.sessionId===sessionId&&current===chatEpoch;}
    chatBusy=true;controls();
    poll=setInterval(function(){if(active())readChat(sessionId).catch(function(error){if(active())el('ChatStatus').textContent=error.message;});},1200);
    try{var result=await action(workspaceId,sessionId);if(active()){chat=result;renderChat();
      if(stopTitleWatch)stopTitleWatch();
      stopTitleWatch=window.RNAssistantWorkspaceChat.watchTitle(result,
        function(){return send('getProjectChat',{workspaceId:workspaceId,sessionId:sessionId});},
        function(latest){if(active()){chat=latest;renderChat();}});}}
    catch(error){if(active())el('ChatStatus').textContent=error.message;}
    finally{clearInterval(poll);chatBusy=false;controls();}}
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
    epoch++;stopSourceWatch();await closeView();editingPlugin=item||null;
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
        popovers.hide();run(async function(){await send('removePlugin',{pluginId:id});if(selected&&selected.pluginId===id){epoch++;stopSourceWatch();await closeView();selected=null;candidate=null;candidateStamp=null;chat=null;
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
    registerRailPopovers();
    new MutationObserver(function(){syncAppearance();renderIconChoices();el('Rail').querySelectorAll('[data-plugin-id]').forEach(function(node){var item=items.find(function(value){return value.pluginId===node.dataset.pluginId;});if(item)node.innerHTML=window.RNAssistantIcons.renderPlugin(item.icon||'puzzle',item.iconSvg,item.iconColor);});}).observe(document.documentElement,{attributes:true,attributeFilter:['data-theme','data-accent']});
    el('Project').onclick=function(){var frame=el('PreviewMount').querySelector('iframe');if(frame&&frame.contentWindow)frame.contentWindow.postMessage({type:'rna.dataTemplates.project'},'*');};
    el('NavigationProject').onclick=function(){el('Project').click();};
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
      await refresh();status((connecting?'Исходники подключены: ':'Исходники созданы: ')+created.sourceRoot);busy=false;await select(created.pluginId);developmentToggle.setOpen(true);
      if(!connecting&&(task||iconBrief)){var message='Загрузи навыки workspace.plugin_authoring и workspace.files. Прочитай README.md и plugin.json. ';
        if(task)message+='Создай плагин по задаче: '+task+'. ';
        if(iconBrief)message+='Нарисуй SVG-иконку по описанию: '+iconBrief+'. Создай icon.svg, добавь его в files и укажи в plugin.json. ';
        message+='После изменений проверь пакет и предпросмотр; применяй только после подтверждения.';
        el('Message').value=message;el('Message').dispatchEvent(new Event('input',{bubbles:true}));el('ChatForm').requestSubmit();}
      el('CreateForm').reset();el('IconDescription').value='';el('IconImportName').value='';el('IconImportSvg').value='';
      selectedIconId='puzzle';customSource=false;renderIconChoices();el('Connect').onchange();
    });};
    el('Connect').onchange=function(){var connecting=this.checked;el('NewFields').hidden=connecting;el('Name').required=!connecting;el('Task').disabled=connecting;el('IconDescription').disabled=connecting||selectedIconId!=='draw';if(connecting)setIconEditor(false);updateCreatePreview();updateSourceLocation();
      el('CreateSubmit').textContent=editingPlugin?'Сохранить':connecting?'Подключить папку':'Создать плагин';el('ModeHint').textContent=editingPlugin?'Оформление на панели обновится сразу.':connecting?
        'Будет подключён существующий plugin.json без изменения исходников.':'Проверка и публикация доступны в разделе «Разработка».';};
    el('Preview').onclick=function(){checkAndPreview();};
    el('Apply').onclick=function(){run(async function(){var latest=(await send('getPluginSourceState',{pluginId:selected.pluginId})).stamp;
      if(latest!==candidateStamp){await checkAndPreview(latest);status('Исходники изменились. Проверьте обновлённый предпросмотр и нажмите «Применить» ещё раз.');return;}
      selected=await send('applyPlugin',{pluginId:selected.pluginId,candidateId:candidate.candidateId});candidate=null;candidateStamp=null;await refresh();
      status('Версия опубликована. Открытый предпросмотр сохраняет свой снимок.');});};
    el('Reopen').onclick=function(){el('Reopen').closest('details').open=false;run(function(){return showView(false);});};
    el('Disable').onclick=function(){el('Disable').closest('details').open=false;run(async function(){selected=await send('disablePlugin',{pluginId:selected.pluginId});candidate=null;candidateStamp=null;await closeView();await refresh();status('Плагин отключён. Исходники и данные сохранены.');});};
    developmentToggle=window.RNAssistantPanelToggle.bind(el('Develop'),{
      controls:'pluginChat',isOpen:function(){return developmentOpen;},
      setOpen:function(open){developmentOpen=open;updateDevelopment(true);}
    });
    el('Chat').addEventListener('keydown',function(event){
      if(event.key!=='Escape'||event.defaultPrevented)return;
      var menu=el('Chat').querySelector('.plugin-actions-menu[open]');
      if(menu){menu.open=false;menu.querySelector('summary').focus();}
      else developmentToggle.setOpen(false);
      event.preventDefault();event.stopPropagation();
    });
    chatComposer=window.RNAssistantWorkspaceChat.bindComposer(el('ChatForm'));
    el('Message').addEventListener('input',controls);
    el('ChatNew').onclick=createChat;
    el('ChatHistory').onclick=function(){if(chat)window.RNAssistantProjectChats.open(chat.project.workspaceId,chat.sessionId);};
    el('ChatForm').onsubmit=function(event){event.preventDefault();var message=el('Message').value.trim();
      if(!message||el('Send').disabled||!chat)return;
      var sessionId=chat.sessionId,options=chatComposer.options();
      if(!pendingChatSend||pendingChatSend.sessionId!==sessionId||pendingChatSend.message!==message||JSON.stringify(pendingChatSend.options)!==JSON.stringify(options))
        pendingChatSend={sessionId:sessionId,message:message,options:options,requestId:crypto.randomUUID()};
      var request=pendingChatSend;
      chatAction(function(workspaceId){return send('submitProjectChat',{workspaceId:workspaceId,sessionId:sessionId,
        input:Object.assign({sessionId:sessionId,message:message,requestId:request.requestId},request.options)}).then(function(result){
        chatComposer.accepted(sessionId,message);if(pendingChatSend===request)pendingChatSend=null;return result;});});};
    el('Stop').onclick=async function(){if(!chat||!selected)return;
      var sessionId=chat.sessionId,workspaceId=chat.project.workspaceId,current=chatEpoch,s=chat.state;
      try{var result=await send('cancelProjectChat',{workspaceId:workspaceId,sessionId:sessionId,runId:s.RunId||s.runId,cursor:s.Cursor||s.cursor});
        if(chat&&chat.sessionId===sessionId&&current===chatEpoch){chat=result;renderChat();}}
      catch(error){if(current===chatEpoch)el('ChatStatus').textContent=error.message;}};
    ['Approve','Reject'].forEach(function(id){el(id).onclick=function(){if(!chat)return;var pending=chat.pendingId;
      chatAction(function(workspaceId,sessionId){return send('resolveProjectChat',{workspaceId:workspaceId,sessionId:sessionId,pendingId:pending,approve:id==='Approve'});});};});
    el('VendorsRefresh').onclick=function(){run(async function(){var catalog=await send('getPluginVendors',{});el('Vendors').replaceChildren();catalog.items.forEach(function(item){var row=document.createElement('p');row.textContent=item.id+' @ '+item.version+' · '+item.license;el('Vendors').appendChild(row);});status('Каталог обновлён. Проверка хешей выполняется при разрешении зависимости.');});};
    el('VendorImport').onclick=function(){run(async function(){var result=await send('importPluginVendor',{rootPath:el('VendorRoot').value});status('Добавлен '+result.id+' @ '+result.version);});};controls();
  }
  window.RNAssistantPlugins={bind:bind,refresh:refresh,show:function(){if(!selected){el('Manager').hidden=false;el('Workbench').hidden=true;}},select:select,selectedId:function(){return selected&&selected.pluginId;}};
})();
