(function () {
  'use strict';
  var items=[],selected=null,candidate=null,view=null,chat=null,busy=false,chatBusy=false,poll=null,epoch=0;
  var previewBusy=false,selecting=false,sourceStamp=null,candidateStamp=null,sourceTimer=null,sourceDebounce=null;
  var savedIcons=[],selectedIconId='puzzle',customSource=false;
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
    el('Send').disabled=chatBusy||!selected;el('CreateSubmit').disabled=busy;}
  async function refresh(){var result=await send('getPlugins',{});items=result.items;var rail=el('Rail');
    if(result.defaultSourceParent)el('DefaultSourcePath').textContent=result.defaultSourceParent;
    if(window.RNAssistantRailPopovers)window.RNAssistantRailPopovers.hide();rail.replaceChildren();items.forEach(function(item){
    var node=button('',function(){select(item.pluginId);});node.innerHTML=window.RNAssistantIcons.renderPlugin(item.pluginId==='data-templates'?'dataTemplates':item.icon || 'puzzle',item.iconPaths);node.className='rail-button';node.setAttribute('aria-label',item.name);node.dataset.railLabel=item.name;node.dataset.railPopover='plugin-item';node.dataset.railError=item.iconError||'';node.dataset.pluginId=item.pluginId;node.dataset.section='plugins';rail.appendChild(node);
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
      if(current!==epoch&&!selecting&&selected&&(!view||!view.preview)&&!el('Workbench').hidden&&el('Develop').checked)checkAndPreview(sourceStamp);}}
  async function pollSource(){if(!selected||el('Workbench').hidden||!el('Develop').checked||document.hidden||!el('Workbench').closest('.panel.active')||previewBusy)return;
    var id=selected.pluginId,current=epoch;
    try{var state=await send('getPluginSourceState',{pluginId:id});if(current!==epoch||!selected||selected.pluginId!==id||state.stamp===sourceStamp)return;
      sourceStamp=state.stamp;clearTimeout(sourceDebounce);sourceDebounce=setTimeout(function(){checkAndPreview(state.stamp);},450);
    }catch(error){if(current===epoch)status('Не удалось проверить изменения исходников: '+error.message,true);}}
  function startSourceWatch(){clearInterval(sourceTimer);sourceTimer=setInterval(function(){pollSource();},1200);}
  function updateDevelopment(refresh){var open=el('Develop').checked;el('Chat').hidden=!open;el('DevelopmentSplitter').hidden=!open;
    el('Develop').setAttribute('aria-label',open?'Скрыть разработку':'Показать разработку');
    el('Workbench').classList.toggle('plugin-developing',open);
    if(refresh&&open){if(!view||!view.preview)checkAndPreview();else pollSource();}}
  function bindDevelopmentSplitter(){var handle=el('DevelopmentSplitter'),body=el('Body'),key='rnassistant.plugin.development.width';
    var ratio=0.56;try{ratio=Number(localStorage.getItem(key))||ratio;}catch(error){}
    handle.title='Перетащите для изменения ширины. Стрелки — шаг, Enter или двойной щелчок — исходная ширина.';
    function set(next,persist){var box=body.getBoundingClientRect(),minimum=Math.min(240,box.width/3),maximum=Math.max(minimum,box.width-300);
      var pixels=Math.max(minimum,Math.min(maximum,next*box.width));ratio=box.width?pixels/box.width:0.56;
      body.style.setProperty('--plugin-preview-width',(ratio*100).toFixed(2)+'%');
      handle.setAttribute('aria-valuemin',String(box.width?Math.round(minimum/box.width*100):0));
      handle.setAttribute('aria-valuemax',String(box.width?Math.round(maximum/box.width*100):100));
      handle.setAttribute('aria-valuenow',String(Math.round(ratio*100)));
      handle.setAttribute('aria-valuetext',Math.round(ratio*100)+'% предпросмотру');
      if(persist)try{localStorage.setItem(key,String(ratio));}catch(error){}
    }
    set(ratio,false);new ResizeObserver(function(){if(body.getBoundingClientRect().width)set(ratio,false);}).observe(body);
    var drag=null;handle.addEventListener('pointerdown',function(event){if(event.button!==0)return;drag={id:event.pointerId,ratio:ratio};handle.setPointerCapture(event.pointerId);handle.classList.add('dragging');event.preventDefault();});
    handle.addEventListener('pointermove',function(event){if(!drag||event.pointerId!==drag.id)return;var box=body.getBoundingClientRect();set((event.clientX-box.left)/box.width,false);});
    function finish(cancel){if(!drag)return;var old=drag;drag=null;handle.classList.remove('dragging');if(cancel)set(old.ratio,false);else set(ratio,true);if(handle.hasPointerCapture(old.id))handle.releasePointerCapture(old.id);}
    handle.addEventListener('pointerup',function(){finish(false);});handle.addEventListener('pointercancel',function(){finish(true);});
    handle.addEventListener('lostpointercapture',function(){finish(false);});handle.addEventListener('dblclick',function(){set(0.56,true);});
    document.addEventListener('keydown',function(event){if(drag&&event.key==='Escape'){event.preventDefault();finish(true);}},true);
    handle.addEventListener('keydown',function(event){if(event.key==='Enter'){set(0.56,true);event.preventDefault();}
      else if(event.key==='ArrowLeft'||event.key==='ArrowRight'){set(ratio+(event.key==='ArrowLeft'?-0.03:0.03),true);event.preventDefault();}});
  }
  async function select(id){selecting=true;switchTab('plugins');try{await run(async function(){epoch++;stopSourceWatch();await closeView();selected=items.find(function(item){return item.pluginId===id;});candidate=null;candidateStamp=null;chat=null;
    switchTab('plugins');el('Manager').hidden=true;el('Workbench').hidden=false;el('Title').textContent=dataPlugin()?navigation.projectName:selected.name;el('Source').textContent=selected.sourceRoot;
    el('ChatSource').textContent=selected.sourceRoot;el('ChatSource').title=selected.sourceRoot;el('ChatMessages').replaceChildren();
    el('Workbench').classList.toggle('plugin-data-templates',dataPlugin());el('Workbench').classList.toggle('split-layout',dataPlugin());el('Project').hidden=!dataPlugin();
    if(typeof refreshSplitPanes==='function')refreshSplitPanes();
    navigation={projectName:'Проект данных',view:'sources',templateId:'',templates:[],expanded:{project:true,templates:true,dictionary:true}};el('ProjectName').textContent=dataPlugin()?selected.name:'';el('Title').textContent=dataPlugin()?navigation.projectName:selected.name;renderNavigation();
    if(window.RNAssistantShellLayout)window.RNAssistantShellLayout.refresh();
    if(selected.enabled)await showView(false);else el('Develop').checked=true;
    updateDevelopment(false);
    try{sourceStamp=(await send('getPluginSourceState',{pluginId:id})).stamp;}catch(error){status(error.message,true);}
    if(el('Develop').checked)await checkAndPreview(sourceStamp);
    startSourceWatch();await readChat();});}finally{selecting=false;
      if(selected&&!el('Workbench').hidden&&el('Develop').checked&&(!view||!view.preview))checkAndPreview(sourceStamp);}}
  function renderChat(){if(!chat)return;var list=el('ChatMessages'),atBottom=list.scrollHeight-list.scrollTop-list.clientHeight<64;list.replaceChildren();
    chat.messages.forEach(function(message){var article=document.createElement('article');article.className='message '+(message.role==='user'?'user':'assistant');
      if(message.text){var body=document.createElement('div');body.className='markdown';body.innerHTML=window.markdown(message.text,{preserveLineBreaks:message.role==='user'});article.appendChild(body);}
      if(message.actions&&message.actions.length){var actions=document.createElement('span');actions.className='message-actions-summary';actions.textContent='Действия: '+message.actions.join(', ');article.appendChild(actions);}
      list.appendChild(article);});if(atBottom)list.scrollTop=list.scrollHeight;
    var s=chat.state||{},running=(s.Status||s.status)==='running';el('ChatStatus').textContent=chat.pendingId?'Нужно подтвердить действие агента.':running?'Агент работает…':'';
    el('Stop').hidden=!running;el('Approval').hidden=!chat.pendingId;el('ApprovalText').textContent=chat.pendingId ? chat.pendingTool+'\n'+chat.pendingArguments : '';renderQuestions(s.userInput||s.UserInput);}
  function renderQuestions(input){var form=el('Questions'),set=input&&(input.QuestionSet||input.questionSet);
    if(!set){form.hidden=true;form.replaceChildren();delete form.dataset.questionSet;return;}
    if(form.dataset.questionSet===set.questionSetId)return;
    form.replaceChildren();form.hidden=false;form.dataset.questionSet=set.questionSetId;
    var fields=set.questions.map(function(question){var group=document.createElement('fieldset'),title=document.createElement('legend');title.textContent=question.prompt;group.appendChild(title);var choices=[];
      (question.options||[]).forEach(function(option){var label=document.createElement('label'),choice=document.createElement('input');choice.type=question.selection==='multiple'?'checkbox':'radio';choice.name=question.id;choice.value=option.id;label.append(choice,document.createTextNode(option.label));group.appendChild(label);choices.push(choice);});
      var text=document.createElement('textarea');text.rows=2;text.placeholder='Ваш ответ';text.hidden=!question.allowFreeText;group.appendChild(text);form.appendChild(group);return {question:question,choices:choices,text:text};});
    var submit=button('Ответить',null);submit.type='submit';form.appendChild(submit);form.onsubmit=function(event){event.preventDefault();var answers={runId:input.RunId||input.runId,questionSetId:set.questionSetId,answers:fields.map(function(field){return {questionId:field.question.id,optionIds:field.choices.filter(function(choice){return choice.checked;}).map(function(choice){return choice.value;}),freeText:field.text.value};})};
      chatAction(function(id){return send('submitPluginChat',{pluginId:id,input:{RequestId:crypto.randomUUID(),Answers:answers}});});};
  }
  async function readChat(){if(!selected)return;var id=selected.pluginId,value=await send('getPluginChat',{pluginId:id});if(selected&&id===selected.pluginId){chat=value;renderChat();}}
  async function chatAction(action){if(chatBusy||!selected)return;chatBusy=true;controls();var id=selected.pluginId;poll=setInterval(function(){readChat().catch(function(error){el('ChatStatus').textContent=error.message;});},1200);
    try{var result=await action(id);if(selected&&selected.pluginId===id){chat=result;renderChat();}}catch(error){el('ChatStatus').textContent=error.message;}finally{clearInterval(poll);chatBusy=false;controls();}}
  function setIconEditor(open){el('IconEditor').hidden=!open;el('IconTrigger').setAttribute('aria-expanded',String(open));
    if(!open&&el('IconEditor').contains(document.activeElement))el('IconTrigger').focus();}
  function updateSourceLocation(){var connecting=el('Connect').checked,manual=connecting||customSource;
    el('SourceLocation').hidden=connecting;el('SourceFolder').hidden=!manual;
    el('Root').required=manual;el('Root').disabled=!manual;
    el('SourceChange').textContent=customSource?'Использовать папку по умолчанию':'Указать другую папку';
  }
  function updateCreatePreview(){var connecting=el('Connect').checked,name=el('Name').value.trim();
    el('CreateTitle').textContent=connecting?'Подключить плагин':name||'Новый плагин';
    el('CreateSubtitle').textContent=connecting?'Укажите папку с plugin.json.':'Нажмите на значок, чтобы выбрать или нарисовать иконку.';
    el('IconTrigger').disabled=connecting;
    var saved=savedIcons.find(function(item){return item.id===selectedIconId;});
    el('IconPreview').innerHTML=window.RNAssistantIcons.renderPlugin(selectedIconId==='draw'?'spark':selectedIconId,saved&&saved.paths);
  }
  function iconOption(id,name,paths){
    var choice=button('',function(){selectedIconId=id;renderIconChoices();if(id==='draw')el('IconDescription').focus();else setIconEditor(false);});
    choice.className='plugin-icon-option';choice.dataset.iconId=id;choice.setAttribute('aria-label',name);
    choice.setAttribute('aria-pressed',String(selectedIconId===id));choice.title=name;
    choice.innerHTML=window.RNAssistantIcons.renderPlugin(id==='draw'?'spark':id,paths);
    var caption=document.createElement('span');caption.textContent=name;choice.appendChild(caption);return choice;
  }
  function renderIconChoices(){
    var builtins=el('BuiltinIcons'),saved=el('SavedIcons');builtins.replaceChildren();saved.replaceChildren();
    builtins.appendChild(iconOption('draw','Нарисовать'));
    window.RNAssistantIcons.pluginChoices.forEach(function(choice){builtins.appendChild(iconOption(choice[0],choice[1]));});
    if(!savedIcons.length){var empty=document.createElement('p');empty.className='plugin-icon-empty';empty.textContent='Пока нет сохранённых иконок.';saved.appendChild(empty);}
    savedIcons.forEach(function(item){
      var card=document.createElement('div');card.className='plugin-icon-saved-card';card.appendChild(iconOption(item.id,item.name,item.paths));
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
    try{var item=await send('drawPluginIcon',{name:el('Name').value.trim()||'Новый плагин',description:brief});
      if(!savedIcons.some(function(saved){return saved.id===item.id;}))savedIcons.push(item);
      selectedIconId=item.id;renderIconChoices();note.textContent=item.previewSimulated?'Пример: иконка создана в памяти вкладки.':'Иконка готова и сохранена в библиотеке.';status('');return item;
    }catch(error){note.textContent=error.message;throw error;}finally{draw.disabled=false;}}
  function registerRailPopovers(){var popovers=window.RNAssistantRailPopovers;if(!popovers)return;
    popovers.register('plugin-item',function(anchor,body){
      if(anchor.dataset.railError){var problem=document.createElement('p');problem.className='rail-hover-empty';problem.textContent=anchor.dataset.railError;body.appendChild(problem);}
      var line=document.createElement('div');line.className='rail-hover-separator';body.appendChild(line);
      var remove=popovers.action('Удалить с панели',window.RNAssistantIcons.render('trash'),function(){var id=anchor.dataset.pluginId,name=anchor.dataset.railLabel;
        if(!window.confirm('Убрать «'+name+'» с панели? Исходники, данные и опубликованная версия сохранятся. Плагин можно вернуть через «+».'))return;
        popovers.hide();run(async function(){await send('removePlugin',{pluginId:id});if(selected&&selected.pluginId===id){epoch++;stopSourceWatch();await closeView();selected=null;candidate=null;candidateStamp=null;chat=null;
          switchTab('plugins');el('Manager').hidden=false;el('Workbench').hidden=true;}await refresh();status('Плагин убран с панели. Вернуть его можно через «+».');});});
      remove.className='rail-hover-danger';body.appendChild(remove);
    });
    popovers.register('plugin-add',async function(anchor,body,isCurrent){body.textContent='Загружаю…';
      var catalog=await send('getRemovedPlugins',{});if(!isCurrent())return;body.replaceChildren();
      if(!(catalog.items||[]).length){var empty=document.createElement('p');empty.className='rail-hover-empty';empty.textContent='Удалённых с панели плагинов нет.';body.appendChild(empty);}
      (catalog.items||[]).forEach(function(item){var restore=popovers.action(item.name,window.RNAssistantIcons.renderPlugin(item.icon||'puzzle',item.iconPaths),function(){popovers.hide();run(async function(){await send('restorePlugin',{pluginId:item.pluginId});await refresh();status('Плагин возвращён на панель.');});});
        body.appendChild(restore);});
      var line=document.createElement('div');line.className='rail-hover-separator';body.appendChild(line);
      var create=popovers.action('Создать плагин',window.RNAssistantIcons.render('plus'),function(){popovers.hide();el('Add').click();});
      body.appendChild(create);
    });
  }
  function bind(){
    registerRailPopovers();
    bindDevelopmentSplitter();
    new MutationObserver(syncAppearance).observe(document.documentElement,{attributes:true,attributeFilter:['data-theme','data-accent']});
    el('Project').onclick=function(){var frame=el('PreviewMount').querySelector('iframe');if(frame&&frame.contentWindow)frame.contentWindow.postMessage({type:'rna.dataTemplates.project'},'*');};
    el('NavigationProject').onclick=function(){el('Project').click();};
    el('NavigationSearch').oninput=renderNavigation;
    el('Add').onclick=async function(){epoch++;stopSourceWatch();var closeError=null;try{await closeView();}catch(error){closeError=error;}
      switchTab('plugins');el('Manager').hidden=false;el('Workbench').hidden=true;status(closeError?closeError.message:'',!!closeError);customSource=false;updateSourceLocation();
      setIconEditor(false);renderIconChoices();refreshIcons().catch(function(error){status(error.message,true);});el('Name').focus();};
    el('Name').oninput=updateCreatePreview;
    el('IconTrigger').onclick=function(){setIconEditor(el('IconEditor').hidden);if(!el('IconEditor').hidden)el('BuiltinIcons').querySelector('button').focus();};
    el('IconEditor').onkeydown=function(event){if(event.key==='Escape'){event.preventDefault();setIconEditor(false);}};
    el('IconDrawButton').onclick=function(){run(drawIcon);};
    el('IconImportButton').onclick=function(){run(async function(){var item=await send('savePluginIcon',{name:el('IconImportName').value.trim(),svg:el('IconImportSvg').value.trim()});
      await refreshIcons();selectedIconId=item.id;renderIconChoices();el('IconImportName').value='';el('IconImportSvg').value='';status('SVG-иконка сохранена.');});};
    el('SourceChange').onclick=function(){customSource=!customSource;updateSourceLocation();if(customSource)el('Root').focus();};
    el('CreateForm').onsubmit=function(event){event.preventDefault();run(async function(){var connecting=el('Connect').checked,task=el('Task').value.trim(),iconBrief=el('IconDescription').value.trim();
      if(!connecting&&selectedIconId==='draw'){await drawIcon();iconBrief='';}
      if(selectedIconId!=='draw')iconBrief='';
      var created=await send(connecting?'connectPlugin':'createPlugin',{name:el('Name').value,rootPath:connecting||customSource?el('Root').value.trim():null,description:task,iconDescription:iconBrief,iconId:selectedIconId});
      await refresh();status((connecting?'Исходники подключены: ':'Исходники созданы: ')+created.sourceRoot);busy=false;await select(created.pluginId);el('Develop').checked=true;el('Develop').onchange();
      if(!connecting&&(task||iconBrief)){var message='Загрузи навыки workspace.plugin_authoring и workspace.files. Прочитай README.md и plugin.json. ';
        if(task)message+='Создай плагин по задаче: '+task+'. ';
        if(iconBrief)message+='Нарисуй SVG-иконку по описанию: '+iconBrief+'. Создай icon.svg, добавь его в files и укажи в plugin.json. ';
        message+='После изменений проверь пакет и предпросмотр; применяй только после подтверждения.';
        el('Message').value=message;el('ChatForm').requestSubmit();}
      el('CreateForm').reset();el('IconDescription').value='';el('IconImportName').value='';el('IconImportSvg').value='';
      selectedIconId='puzzle';customSource=false;renderIconChoices();el('Connect').onchange();
    });};
    el('Connect').onchange=function(){var connecting=this.checked;el('NewFields').hidden=connecting;el('Name').required=!connecting;el('Task').disabled=connecting;el('IconDescription').disabled=connecting||selectedIconId!=='draw';if(connecting)setIconEditor(false);updateCreatePreview();updateSourceLocation();
      el('CreateSubmit').textContent=connecting?'Подключить папку':'Создать плагин';el('ModeHint').textContent=connecting?
        'Будет подключён существующий plugin.json без изменения исходников.':'Проверка и публикация доступны в разделе «Разработка».';};
    el('Preview').onclick=function(){checkAndPreview();};
    el('Apply').onclick=function(){run(async function(){var latest=(await send('getPluginSourceState',{pluginId:selected.pluginId})).stamp;
      if(latest!==candidateStamp){await checkAndPreview(latest);status('Исходники изменились. Проверьте обновлённый предпросмотр и нажмите «Применить» ещё раз.');return;}
      selected=await send('applyPlugin',{pluginId:selected.pluginId,candidateId:candidate.candidateId});candidate=null;candidateStamp=null;await refresh();
      status('Версия опубликована. Открытый предпросмотр сохраняет свой снимок.');});};
    el('Reopen').onclick=function(){el('Reopen').closest('details').open=false;run(function(){return showView(false);});};
    el('Disable').onclick=function(){el('Disable').closest('details').open=false;run(async function(){selected=await send('disablePlugin',{pluginId:selected.pluginId});candidate=null;candidateStamp=null;await closeView();await refresh();status('Плагин отключён. Исходники и данные сохранены.');});};
    el('Develop').onchange=function(){updateDevelopment(true);};
    el('ChatForm').onsubmit=function(event){event.preventDefault();var message=el('Message').value.trim();if(!message)return;el('Message').value='';chatAction(function(id){return send('submitPluginChat',{pluginId:id,input:{Message:message,Mode:'agent',RequestId:crypto.randomUUID()}});});};
    el('Stop').onclick=async function(){if(!chat||!selected)return;var s=chat.state;try{chat=await send('cancelPluginChat',{pluginId:selected.pluginId,runId:s.RunId||s.runId,cursor:s.Cursor||s.cursor});renderChat();}catch(error){el('ChatStatus').textContent=error.message;}};
    ['Approve','Reject'].forEach(function(id){el(id).onclick=function(){var pending=chat.pendingId;chatAction(function(plugin){return send('resolvePluginChat',{pluginId:plugin,pendingId:pending,approve:id==='Approve'});});};});
    el('VendorsRefresh').onclick=function(){run(async function(){var catalog=await send('getPluginVendors',{});el('Vendors').replaceChildren();catalog.items.forEach(function(item){var row=document.createElement('p');row.textContent=item.id+' @ '+item.version+' · '+item.license;el('Vendors').appendChild(row);});status('Каталог обновлён. Проверка хешей выполняется при разрешении зависимости.');});};
    el('VendorImport').onclick=function(){run(async function(){var result=await send('importPluginVendor',{rootPath:el('VendorRoot').value});status('Добавлен '+result.id+' @ '+result.version);});};controls();
  }
  window.RNAssistantPlugins={bind:bind,refresh:refresh,show:function(){if(!selected){el('Manager').hidden=false;el('Workbench').hidden=true;}},select:select,selectedId:function(){return selected&&selected.pluginId;}};
})();
