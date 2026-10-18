(function () {
  'use strict';
  var items=[],selected=null,candidate=null,view=null,chat=null,busy=false,chatBusy=false,poll=null,epoch=0;
  function el(id){return document.getElementById('plugin'+id);}
  function status(text,error){el('Status').textContent=text || '';el('Status').setAttribute('role',error?'alert':'status');}
  function button(label,action){var node=document.createElement('button');node.type='button';node.textContent=label;node.onclick=action;return node;}
  async function run(action){if(busy)return;busy=true;controls();try{await action();}catch(error){status(error.message,true);}finally{busy=false;controls();}}
  function controls(){['Check','Preview','Apply','Disable'].forEach(function(id){el(id).disabled=busy||!selected||(id==='Apply'||id==='Preview')&&!candidate;});el('Send').disabled=chatBusy||!selected;}
  async function refresh(){var result=await send('getPlugins',{});items=result.items;var rail=el('Rail');rail.replaceChildren();items.forEach(function(item){
    var node=button('',function(){select(item.pluginId);});node.innerHTML=window.RNAssistantNavigationTree.icon(item.icon || (item.pluginId==='data-templates'?'chart':'puzzle'));node.className='rail-button';node.title=item.name;node.setAttribute('aria-label',item.name);node.dataset.pluginId=item.pluginId;node.dataset.section='plugins';rail.appendChild(node);
  });if(result.error)status(result.error,true);return items;}
  async function closeView(){if(!view)return;var old=view;view=null;el('PreviewMount').replaceChildren();await send('closePlugin',{instanceId:old.instanceId});}
  async function showView(preview){await closeView();var current=epoch,opened=await send('openPlugin',{pluginId:selected.pluginId,candidateId:preview?candidate.candidateId:null});
    if(current!==epoch){await send('closePlugin',{instanceId:opened.instanceId});return;}
    view=opened;var frame=document.createElement('iframe');frame.title=selected.name+(preview?' — предпросмотр':'');frame.sandbox='allow-scripts allow-same-origin allow-downloads';frame.referrerPolicy='no-referrer';frame.src=view.entryUrl;el('PreviewMount').replaceChildren(frame);
    status(preview?'Предпросмотр проверенного снимка. Данные подключаются к отдельному тестовому проекту.':'Работает опубликованная версия. Папка данных выбирается внутри плагина.');}
  async function select(id){switchTab('plugins');await run(async function(){epoch++;await closeView();selected=items.find(function(item){return item.pluginId===id;});candidate=null;chat=null;
    switchTab('plugins');el('Manager').hidden=true;el('Workbench').hidden=false;el('Title').textContent=selected.name;el('Source').textContent=selected.sourceRoot;el('ChatMessages').replaceChildren();
    if(selected.enabled)await showView(false);else status('Проверьте исходники и примените первую публикацию.');await readChat();});}
  function renderChat(){if(!chat)return;el('ChatMessages').replaceChildren();chat.messages.forEach(function(message){var p=document.createElement('p');p.className='plugin-message plugin-message-'+message.role;p.textContent=message.text;el('ChatMessages').appendChild(p);});
    var s=chat.state;el('ChatStatus').textContent=s.Status || s.status || '';el('Approval').hidden=!chat.pendingId;el('ApprovalText').textContent=chat.pendingId ? chat.pendingTool+'\n'+chat.pendingArguments : '';renderQuestions(s.userInput);}
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
  function bind(){
    el('Add').onclick=function(){switchTab('plugins');el('Manager').hidden=false;el('Workbench').hidden=true;el('Name').focus();};
    el('CreateForm').onsubmit=function(event){event.preventDefault();run(async function(){var created=await send(el('Connect').checked?'connectPlugin':'createPlugin',{name:el('Name').value,rootPath:el('Root').value,description:el('Task').value});await refresh();status('Исходники подключены.');busy=false;await select(created.pluginId);el('Develop').checked=true;el('Develop').onchange();if(!el('Connect').checked && el('Task').value.trim()){el('Message').value='Создай плагин по задаче: '+el('Task').value.trim()+'. Начни с README.md и plugin.json. После изменений проверь пакет; применяй только после подтверждения.';el('ChatForm').requestSubmit();}});};
    el('Connect').onchange=function(){el('Name').required=!this.checked;el('Task').disabled=this.checked;};
    el('Check').onclick=function(){run(async function(){candidate=await send('checkPlugin',{pluginId:selected.pluginId});status('Состав и зависимости проверены: '+candidate.files+' файлов. Можно открыть предпросмотр или применить.');});};
    el('Preview').onclick=function(){run(function(){return showView(true);});};
    el('Apply').onclick=function(){run(async function(){selected=await send('applyPlugin',{pluginId:selected.pluginId,candidateId:candidate.candidateId});candidate=null;await refresh();status('Публикация применена. Открытый экран сохраняет свою версию; после завершения операций откройте активную.');});};
    el('Reopen').onclick=function(){run(function(){return showView(false);});};
    el('Disable').onclick=function(){run(async function(){selected=await send('disablePlugin',{pluginId:selected.pluginId});candidate=null;await closeView();await refresh();status('Плагин отключён. Исходники и данные сохранены.');});};
    el('Develop').onchange=function(){el('Chat').hidden=!this.checked;el('Workbench').classList.toggle('plugin-developing',this.checked);};
    el('ChatForm').onsubmit=function(event){event.preventDefault();var message=el('Message').value.trim();if(!message)return;el('Message').value='';chatAction(function(id){return send('submitPluginChat',{pluginId:id,input:{Message:message,Mode:'agent',RequestId:crypto.randomUUID()}});});};
    el('Stop').onclick=async function(){if(!chat||!selected)return;var s=chat.state;try{chat=await send('cancelPluginChat',{pluginId:selected.pluginId,runId:s.RunId||s.runId,cursor:s.Cursor||s.cursor});renderChat();}catch(error){el('ChatStatus').textContent=error.message;}};
    ['Approve','Reject'].forEach(function(id){el(id).onclick=function(){var pending=chat.pendingId;chatAction(function(plugin){return send('resolvePluginChat',{pluginId:plugin,pendingId:pending,approve:id==='Approve'});});};});
    el('VendorsRefresh').onclick=function(){run(async function(){var catalog=await send('getPluginVendors',{});el('Vendors').replaceChildren();catalog.items.forEach(function(item){var row=document.createElement('p');row.textContent=item.id+' @ '+item.version+' · '+item.license;el('Vendors').appendChild(row);});status('Каталог обновлён. Проверка хешей выполняется при разрешении зависимости.');});};
    el('VendorImport').onclick=function(){run(async function(){var result=await send('importPluginVendor',{rootPath:el('VendorRoot').value});status('Добавлен '+result.id+' @ '+result.version);});};controls();
  }
  window.RNAssistantPlugins={bind:bind,refresh:refresh,show:function(){if(!selected){el('Manager').hidden=false;el('Workbench').hidden=true;}},select:select,selectedId:function(){return selected&&selected.pluginId;}};
})();
