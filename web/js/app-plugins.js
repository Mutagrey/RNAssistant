(function () {
  'use strict';
  var items=[],busy=false,savedIcons=[],selectedIconId='puzzle',customSource=false,editingPlugin=null,chatSelectionSerial=0,refreshTask=null;
  function el(id){return document.getElementById('plugin'+id)||window.RNAssistantWorkspaceChat.node(id);}
  function item(id){return items.find(function(value){return value.pluginId===id;});}
  function each(callback){RNAssistantDockWorkspace.eachView('plugin',callback);}
  function status(text,error){el('Status').textContent=text||'';el('Status').setAttribute('role',error?'alert':'status');}
  function controls(){el('CreateSubmit').disabled=busy;}
  async function run(action){if(busy)return;busy=true;controls();try{await action();}catch(error){status(error.detail||error.message,true);}finally{busy=false;controls();}}
  function sourcePlugin(){var snapshot=RNAssistantProjectChats.snapshot();return RNAssistantWorkspaceChat.owner()==='plugin'&&snapshot&&snapshot.project&&items.find(function(value){return value.sourceWorkspaceId===snapshot.project.workspaceId;});}
  function selectedId(){var target=RNAssistantDockWorkspace.activeTarget();return target&&target.pluginId||((sourcePlugin()||{}).pluginId)||null;}
  function button(label,action){var node=document.createElement('button');node.type='button';node.textContent=label;node.onclick=action;return node;}
  function applyAppearance(frame){
    if(!frame||!frame.contentWindow)return;
    var root=document.documentElement,style=getComputedStyle(root);
    var names={panel:'--surface',secondary:'--surface-subtle',secondaryHover:'--surface-hover',
      text:'--ink',muted:'--ink-muted',line:'--line',lineStrong:'--line-strong',
      accent:'--accent',accentHover:'--accent-hover',accentInk:'--accent-ink',
      accentSoft:'--accent-soft',danger:'--danger',success:'--success'};
    var colors={};Object.keys(names).forEach(function(key){colors[key]=style.getPropertyValue(names[key]).trim();});
    frame.contentWindow.postMessage({type:'rna.appearance',theme:root.dataset.theme==='dark'?'dark':'light',colors:colors},'*');
  }
  async function refresh(){if(refreshTask)return refreshTask;refreshTask=loadPlugins();try{return await refreshTask;}finally{refreshTask=null;}}
  async function loadPlugins(){var result=await send('getPlugins',{});items=result.items;var rail=el('Rail');
    if(result.defaultSourceParent)el('DefaultSourcePath').textContent=result.defaultSourceParent;
    if(window.RNAssistantRailPopovers)window.RNAssistantRailPopovers.hide();rail.replaceChildren();items.forEach(function(item){
    var node=button('',function(){
      if(node.getAttribute('aria-current')==='page'){window.RNAssistantShellLayout.setNavigationOpen(!window.RNAssistantShellLayout.isNavigationOpen());return;}
      select(item.pluginId,null,true);
    });node.innerHTML=window.RNAssistantIcons.renderPlugin(item.icon || (item.pluginId==='data-templates'?'dataTemplates':'puzzle'),item.iconSvg,item.iconColor);node.className='rail-button';node.setAttribute('aria-label',item.name);node.dataset.railLabel=item.name;node.dataset.railPopover='plugin-item';node.dataset.railError=item.iconError||'';node.dataset.pluginId=item.pluginId;node.dataset.section='plugins';rail.appendChild(node);
  });each(function(view){view.updateItem(item(view.pluginId));});RNAssistantDockWorkspace.refreshPresentation();if(result.error)status(result.error,true);else if(items.some(function(item){return item.iconError;}))status('У одного из плагинов некорректная SVG-иконка. Исправьте исходник и проверьте пакет.',true);return items;}
  function conversationAddress(){
    var owner=window.RNAssistantWorkspaceChat.owner(),snapshot=window.RNAssistantProjectChats.snapshot();
    return JSON.stringify(owner==='office'?[owner,state.activeChatId]:
      [owner,snapshot&&snapshot.project&&snapshot.project.workspaceId,snapshot&&snapshot.sessionId]);
  }
  async function openSourceChat(sessionId,item,placement){
    item=item||sourcePlugin()||items.find(function(value){return value.pluginId===selectedId();});if(!item)return false;
    var id=item.pluginId,workspaceId=item.sourceWorkspaceId,route=++chatSelectionSerial,previous=conversationAddress();
    await send('getPluginChat',{pluginId:id});
    if(route!==chatSelectionSerial||conversationAddress()!==previous||placement&&placement.isCurrent&&!placement.isCurrent())return false;
    await window.RNAssistantProjectChats.open(workspaceId,sessionId);
    if(route!==chatSelectionSerial||window.RNAssistantProjectChats.workspaceId()!==workspaceId||placement&&placement.isCurrent&&!placement.isCurrent())return false;
    window.RNAssistantWorkspaceChat.mount('plugin');
    window.RNAssistantProjectChats.render();controls();
    var snapshot=RNAssistantProjectChats.snapshot();
    if(!snapshot||!snapshot.project||snapshot.project.workspaceId!==workspaceId||!snapshot.sessionId||sessionId&&snapshot.sessionId!==sessionId)return false;
    var discussion=snapshot&&snapshot.sessionId?{source:'workspace',workspaceId:workspaceId,sessionId:snapshot.sessionId}:null;
    if(discussion)each(function(view){if(view.pluginId===id)view.bindDiscussion(discussion);});
    RNAssistantPlugins.onChatSnapshot(snapshot);
    if(discussion&&placement)RNAssistantConversationPanel.open(discussion,placement);
    else window.RNAssistantDockWorkspace.show('conversation');
    return true;
  }
  async function select(id,sessionId){
    var plugin=item(id);if(!plugin)return false;
    if(sessionId)return openSourceChat(sessionId,plugin);
    if(!document.getElementById('tab-chat').classList.contains('active')&&!document.getElementById('tab-artifacts').classList.contains('active'))switchTab('chat',{preserveChatOwner:true});
    var target={view:'plugin',pluginId:id,title:plugin.name};
    if(!RNAssistantDockWorkspace.openTarget(target))return false;
    var view=RNAssistantDockWorkspace.getView(RNAssistantPluginView.key(target));if(view)await view.ready();return true;
  }
  function updateSourceLocation(){var connecting=el('Connect').checked,manual=connecting||customSource;
    el('SourceLocation').hidden=connecting||!!editingPlugin;el('SourceFolder').hidden=!manual||!!editingPlugin;
    el('Root').required=manual&&!editingPlugin;el('Root').disabled=!manual||!!editingPlugin;
    el('SourceChange').textContent=customSource?'Использовать папку по умолчанию':'Указать другую папку';
  }
  function iconColor(){var mode=el('IconColorMode').value;return mode==='custom'?el('IconColor').value:mode==='original'?'original':null;}
  function updateCreatePreview(){var connecting=el('Connect').checked;
    el('IconEditor').hidden=connecting;el('IconColor').hidden=el('IconColorMode').value!=='custom';
    var saved=selectedIconId==='current'&&editingPlugin?{svg:editingPlugin.iconSvg}:savedIcons.find(function(item){return item.id===selectedIconId;});
    el('IconPreview').innerHTML=window.RNAssistantIcons.renderPlugin(selectedIconId==='draw'?'spark':selectedIconId,saved&&saved.svg,iconColor());
  }
  function iconOption(id,name,svg){
    var choice=button('',function(){selectedIconId=id;el('IconColorMode').value='original';renderIconChoices();if(id==='draw')el('IconDescription').focus();});
    choice.className='plugin-icon-option';choice.dataset.iconId=id;choice.setAttribute('aria-label',name);
    choice.setAttribute('aria-pressed',String(selectedIconId===id));choice.title=name;
    choice.innerHTML=window.RNAssistantIcons.renderPlugin(id==='draw'?'spark':id,svg,iconColor());
    var caption=document.createElement('span');caption.textContent=name;choice.appendChild(caption);return choice;
  }
  function renderIconChoices(){
    var builtins=el('BuiltinIcons'),saved=el('SavedIcons');builtins.replaceChildren();saved.replaceChildren();
    builtins.appendChild(iconOption('draw','Нарисовать'));
    if(editingPlugin&&editingPlugin.iconSvg)builtins.appendChild(iconOption('current','Текущая иконка',editingPlugin.iconSvg));
    window.RNAssistantIcons.pluginChoices.forEach(function(choice){builtins.appendChild(iconOption(choice[0],choice[1]));});
    el('SavedIconGroup').hidden=!savedIcons.length;
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
      selectedIconId=item.id;el('IconColorMode').value='original';renderIconChoices();note.textContent=item.previewSimulated?'Пример: иконка создана в памяти вкладки.':'Иконка готова и сохранена в библиотеке.';status('');return item;
    }catch(error){note.textContent=error.message;throw error;}finally{draw.disabled=false;}}
  async function openManager(item){
    editingPlugin=item||null;
    switchTab('plugins');el('Manager').hidden=false;status('');
    el('CreateForm').reset();el('Connect').checked=false;customSource=false;
    selectedIconId=item?(item.iconSvg?'current':item.icon||'puzzle'):'puzzle';
    var color=item&&item.iconColor;
    el('IconColorMode').value=color==='original'?'original':color?'custom':item?'theme':'original';el('IconColor').value=color&&color!=='original'?color:'#368453';
    el('Name').value=item?item.name:'';el('Task').closest('label').hidden=!!item;
    el('Connect').closest('label').hidden=!!item;el('EditCancel').hidden=!item;
    el('Manager').querySelector('.plugin-vendors').hidden=!!item;
    el('Connect').onchange();await refreshIcons();el('Name').focus();
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
        popovers.hide();run(async function(){await send('removePlugin',{pluginId:id});RNAssistantDockWorkspace.close(RNAssistantPluginView.key({pluginId:id}));await refresh();status('Плагин убран с панели. Вернуть его можно через «+».');});});
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
    RNAssistantDockWorkspace.registerView('plugin',{perObject:true,maximumInstances:4,renderer:'onlyWhenVisible',
      accepts:function(target){return target&&target.view==='plugin'&&typeof target.pluginId==='string'&&!!target.pluginId&&target.pluginId.length<=256&&typeof target.title==='string'&&target.title.length<=1024;},
      key:RNAssistantPluginView.key,create:RNAssistantPluginView.create,titleFor:function(target){return target.title;}});
    window.addEventListener('pagehide',function(event){if(!event.persisted)each(function(view){view.dispose();});});
    registerRailPopovers();
    new MutationObserver(function(){renderIconChoices();el('Rail').querySelectorAll('[data-plugin-id]').forEach(function(node){var value=item(node.dataset.pluginId);if(value)node.innerHTML=RNAssistantIcons.renderPlugin(value.icon||'puzzle',value.iconSvg,value.iconColor);});}).observe(document.documentElement,{attributes:true,attributeFilter:['data-theme','data-accent']});
    el('Add').onclick=function(){run(function(){return openManager(null);});};
    el('EditCancel').onclick=function(){if(editingPlugin){var id=editingPlugin.pluginId;editingPlugin=null;select(id);}};
    el('IconColorMode').onchange=renderIconChoices;el('IconColor').oninput=renderIconChoices;
    el('IconDrawButton').onclick=function(){run(drawIcon);};
    el('IconImportButton').onclick=function(){run(async function(){var item=await send('savePluginIcon',{name:el('IconImportName').value.trim(),svg:el('IconImportSvg').value.trim()});
      await refreshIcons();selectedIconId=item.id;el('IconColorMode').value='original';renderIconChoices();el('IconImportName').value='';el('IconImportSvg').value='';el('IconImportButton').closest('details').open=false;status('SVG добавлен.');});};
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
      if(!connecting&&(task||iconBrief)){if(!await openSourceChat(null,created))return;var message='Загрузи навыки skills.workspace.plugin.authoring и skills.workspace.files. Прочитай README.md и plugin.json. ';
        if(task)message+='Создай плагин по задаче: '+task+'. ';
        if(iconBrief)message+='Нарисуй SVG-иконку по описанию: '+iconBrief+'. Создай icon.svg, добавь его в files и укажи в plugin.json. ';
        message+='После изменений проверь пакет и предпросмотр; применяй только после подтверждения.';
        if(!RNAssistantProjectChats.snapshot()||!RNAssistantProjectChats.snapshot().sessionId){if(!await window.RNAssistantProjectChats.create())return;}
        el('Input').value=message;el('Input').dispatchEvent(new Event('input',{bubbles:true}));el('Form').requestSubmit();}
      el('CreateForm').reset();el('IconDescription').value='';el('IconImportName').value='';el('IconImportSvg').value='';
      selectedIconId='puzzle';customSource=false;renderIconChoices();el('Connect').onchange();
    });};
    el('Connect').onchange=function(){var connecting=this.checked;el('NewFields').hidden=connecting;el('Name').required=!connecting;el('Task').disabled=connecting;el('IconDescription').disabled=connecting||selectedIconId!=='draw';el('Name').closest('label').hidden=connecting;updateCreatePreview();updateSourceLocation();
      el('CreateSubmit').textContent=editingPlugin?'Сохранить':connecting?'Подключить папку':'Создать плагин';el('ModeHint').textContent=editingPlugin?'Оформление на панели обновится сразу.':connecting?
        'Будет подключён существующий plugin.json без изменения исходников.':'Проверка и публикация доступны в разделе «Разработка».';};
    el('VendorsRefresh').onclick=function(){run(async function(){var catalog=await send('getPluginVendors',{});el('Vendors').replaceChildren();catalog.items.forEach(function(item){var row=document.createElement('p');row.textContent=item.id+' @ '+item.version+' · '+item.license;el('Vendors').appendChild(row);});status('Каталог обновлён. Проверка хешей выполняется при разрешении зависимости.');});};
    el('VendorImport').onclick=function(){run(async function(){var result=await send('importPluginVendor',{rootPath:el('VendorRoot').value});status('Добавлен '+result.id+' @ '+result.version);});};controls();
  }
  async function restoreVersion(target){
    var view=RNAssistantDockWorkspace.getView(RNAssistantPluginView.key(target));
    if(busy||view&&view.isBusy())throw new Error('Дождитесь завершения операции с плагином.');
    busy=true;controls();try{var restored=await send('restorePluginVersion',{pluginId:target.pluginId,releaseId:target.releaseId,expectedActiveReleaseId:target.expectedActiveReleaseId});
      if(view)await view.invalidate(restored);await refresh();
    }finally{busy=false;controls();}
  }
  window.RNAssistantPlugins={bind:bind,refresh:refresh,show:function(){el('Manager').hidden=false;},select:select,item:item,
    showAnnotation:async function(annotation,target){
      var plugin=items.find(function(item){return item.sourceWorkspaceId===target.workspaceId;});
      if(!plugin||!await openSourceChat(target.sessionId,plugin)||!await select(plugin.pluginId))return;
      var view=RNAssistantDockWorkspace.getView(RNAssistantPluginView.key(plugin));if(view){view.bindDiscussion(target);view.showAnnotation(annotation);}
    },
    applyAppearance:applyAppearance,restoreVersion:restoreVersion,openSourceChat:openSourceChat,
    onChatSnapshot:function(snapshot){each(function(view){view.onChatSnapshot(snapshot);});},
    isChatBusy:function(target){var pending=busy;each(function(view){pending=pending||view.isBusy(target);});return pending;},
    sourceWorkspaceId:function(){var source=sourcePlugin();return source&&source.sourceWorkspaceId;},selectedId:selectedId};
})();
