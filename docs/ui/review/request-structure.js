(function () {
  "use strict";
  // Small synthetic UX fixture, not a domain projection or an alternative reader.
  var $ = function (id) { return document.getElementById(id); };
  var samples = [
    { role:"developer", group:"instructions", title:"Правила и каталог", cost:180, source:"Системная инструкция и каталог · полный текст; доли отдельных навыков не записаны", content:"## Правила работы\n\nПроверяй факты по данным документа. Разделяй оценку и подтверждённый результат.\n\n## Каталог\n\n- `core.read` — чтение данных\n- `core.search` — поиск\n\nКаталог и правила находятся в одном сообщении. Точная атрибуция отдельных блоков не сохранена." },
    { role:"user", group:"conversation", title:"Задача пользователя", cost:80, source:"Сообщение чата · полный текст", content:"## Сверка продаж\n\nСравни **план и факт** за октябрь.\n\n| Показатель | Что проверить |\n|---|---|\n| Выручка | Сумма по регионам |\n| Отклонение | Факт минус план |\n\n1. Проверь итоговые суммы.\n2. Перечисли расхождения.\n\n> Документ пока не изменяй." },
    { role:"assistant", group:"conversation", title:"Исторический ответ", cost:60, source:"Историческая проекция · не новый ответ модели", content:'{"action":"execute","reason":"Проверить исходные данные","tool_calls":[{"name":"core.read","arguments":{"target":"Продажи"}}],"message":"Проверяю данные."}' },
    { role:"system", group:"tools", title:"Результат чтения", cost:70, source:"Результат core.read · T1 · доставлено в сообщении №4", content:'{"sheet":"Продажи","rows":[{"region":"Север","plan":100,"actual":112},{"region":"Юг","plan":90,"actual":84}],"complete":true}' },
    { role:"user", group:"resources", title:"Контекст: заметка", cost:50, source:"Контекст ресурса · R1 · сокращённая проекция; локальный оригинал доступен отдельно", content:"## Примечание к отчёту\n\nФакт указан **без НДС**. Для итоговой сверки использовать данные листа «Продажи»." },
    { role:"user", group:"conversation", title:"Уточнение задачи", cost:40, source:"Сообщение чата · полный текст", content:"Покажи итог **в таблице**. Добавь краткое пояснение по каждому региону." },
    { role:"developer", group:"instructions", title:"Указание для повтора", cost:100, source:"Записанная инструкция следующей попытки · предыдущий отказ tool_calls[0].arguments", content:"Предыдущий ответ отклонён: `tool_calls[0].arguments` должен быть объектом, получена строка. Исправь форму вызова." }
  ];
  var wire = {model:"synthetic-model",messages:samples.map(function (m) { return {role:m.role,content:m.content}; }),response_format:{type:"json_object"},temperature:0,stream:true};
  var groups = {instructions:"Инструкции и каталог",conversation:"История сообщений",tools:"Вызовы и результаты",resources:"Контекст ресурсов"};
  var roleNames = {developer:"Приложение",user:"Пользователь",assistant:"Модель",system:"Системное"};
  var selected = "overview", selectionAnchor = null, format = "markdown", loaded = false, task = "structure", expanded = {}, positions = {};
  var viewer = $("viewer"), content = document.querySelector(".request-content");
  function node(tag,text,cls) { var n=document.createElement(tag); n.textContent=text; if(cls)n.className=cls; return n; }
  function note(text) { viewer.appendChild(node("p",text)); }
  function copy(text) { return navigator.clipboard.writeText(text); }
  function clear() { RNAssistantViewerRegistry.unmount(viewer); viewer.replaceChildren(); }
  function mount(kind,text,partial) { RNAssistantViewerRegistry.mount(kind,viewer,{text:text,fullText:partial?undefined:text,complete:!partial,completeness:partial?"preview":"full",compact:true,onCopy:copy,limits:{maxChars:2000000}}); }
  function child(m,index) { return {key:"m"+index,title:(index+1)+" · "+m.title,tooltip:roleNames[m.role]+" · "+m.content.slice(0,100),badge:"≈ "+m.cost,selected:selected==="m"+index||((selected==="whole"||selected.indexOf("source:")===0)&&selectionAnchor==="m"+index)}; }
  function drawTree() {
    var all=[{key:"overview",title:"Обзор запроса",selected:selected==="overview",iconHtml:RNAssistantIcons.render("info")}];
    if($("order").value==="wire" || $("sample").value==="unknown") all=all.concat(samples.map(child));
    else Object.keys(groups).forEach(function (g) {
      var members=samples.map(function(m,i){return {m:m,i:i};}).filter(function(x){return x.m.group===g;});
      all.push({key:g,kind:"parent",selectable:true,title:groups[g],badge:String(members.length),expanded:!!expanded[g],selected:selected===g,children:loaded?members.map(function(x){return child(x.m,x.i);}):[]});
    });
    all.push({key:"parameters",title:"Параметры и формат",selected:selected==="parameters"},{key:"omitted",title:"Не отправлено",badge:"1",selected:selected==="omitted"},{key:"links",title:"Ресурсы и доказательства",selected:selected==="links"});
    RNAssistantNavigationTree.render($("contentsTree"),all,{label:"Оглавление запроса",onSelect:function(n){ choose(n.key); },onToggle:function(n,open){expanded[n.key]=open;if(open&&!loaded){loaded=true;drawTree();}}});
  }
  function facts(items) { var dl=node("dl","","facts"); items.forEach(function(p){dl.append(node("dt",p[0]),node("dd",p[1]));});viewer.append(dl); }
  function choose(key) { positions[selected]=viewer.scrollTop; selected=key; if(/^m\d+$/.test(key))selectionAnchor=key; loaded=loaded||key!=="overview"; if(groups[key])expanded[key]=true; if(task!=="structure")selectTask("structure"); drawTree(); content.dataset.detail="true"; render(); if(key==="whole"||key==="raw"||key.indexOf("source:")===0||document.querySelector(".request-view").clientWidth<=640)requestAnimationFrame(function(){$("title").focus({preventScroll:true});}); }
  function render() {
    RNAssistantActionMenu.closeAll(); clear(); document.querySelector(".formats").hidden=true; $("messageActions").hidden=true; $("origin").hidden=true; $("typeNote").textContent=""; $("typeNote").className="";
    var index=/^m\d+$/.test(selected)?Number(selected.slice(1)):-1, sample=$("sample").value;
    $("title").textContent="Обзор запроса"; $("path").textContent=loaded?"Синтетическая запись · тело открыто":"Metadata · тело ещё не открыто";
    if(sample==="missing" && selected!=="overview") { $("title").textContent="Тело недоступно"; note("Metadata сохранены, но тело запроса недоступно. Это не пустой запрос и не успешное чтение."); return; }
    if(task!=="structure") {
      $("title").textContent=task==="response"?"Ответ отклонён":"Изменения Q13 → Q14";
      facts(task==="response"?[["Поле","tool_calls[0].arguments"],["Причина","Ожидался объект, получена строка"],["Runtime","Вызов инструмента не принят"]]:[["Добавлено","Сообщение №7 — инструкция для повторной попытки"],["Остальные сообщения","Без изменений"],["Формат ответа","json_object — без изменений"]]); return;
    }
    if(index>=0) {
      var m=samples[index], text=m.content; $("title").textContent=(index+1)+" · "+m.title;
      $("path").textContent="messages["+index+"].content · string · "+roleNames[m.role]+(m.role==="assistant"?" · историческая проекция":"");
      document.querySelector(".formats").hidden=false;
      $("messageActions").hidden=false;
      document.querySelectorAll("[data-format]").forEach(function(b){var active=b.dataset.format===(sample==="partial"?"text":format);b.setAttribute("aria-pressed",String(active));b.classList.toggle("active",active);b.disabled=sample==="partial"&&b.dataset.format!=="text";});
      $("origin").hidden=false; $("origin").textContent="Откуда: "+(sample==="unknown"?"Классификация не сохранена; роль и текст доступны.":m.source);
      if(sample==="partial") { $("typeNote").textContent="Запись неполная · показан сохранённый фрагмент. Markdown и разбор JSON недоступны.";$("typeNote").className="attention";mount("text",text.slice(0,90),true); }
      else if(format==="json") {
        var doc=RNAssistantJsonViewer.parse(text); if(doc.error){note("Содержимое — текстовая строка, не валидный JSON. Выберите Markdown или Текст. Это не ошибка runtime.");}
        else {$("typeNote").textContent="JSON внутри строки · в запросе тип string. Разобрано только для просмотра.";$("typeNote").className="attention";mount("json",text);}
      } else if(format==="text") mount("text",text);
      else { var md=node("article","","markdown");md.innerHTML=window.markdown(text);viewer.append(md); }
    } else if(selected==="raw" || selected==="whole") {
      $("title").textContent=selected==="raw"?"Исходник запроса":"Сообщение целиком"; $("path").textContent="Сохранённый JSON синтетического примера · исходный тип content — string";
      var raw=JSON.stringify(selected==="raw"?wire:wire.messages[Number(viewer.dataset.messageIndex)],null,2);
      if(sample==="partial") {$("typeNote").textContent="Неполная запись · только сохранённый фрагмент JSON";$("typeNote").className="attention";mount("text",raw.slice(0,200),true);}
      else mount("json",raw);
    } else if(selected.indexOf("source:")===0) {
      var sourceIndex=Number(selected.slice(7));$("title").textContent="Источники сообщения №"+(sourceIndex+1);
      note(sample==="unknown"?"Точная связь с исходным источником не записана.":samples[sourceIndex].source);
      note("Сохранённые связи, покрытие и локальный оригинал в production открывает существующий ModelContext reader. Макет не создаёт такие данные.");
      var backToContent=node("button","К содержимому сообщения","button compact");backToContent.onclick=function(){choose("m"+sourceIndex);};viewer.append(backToContent);
    } else if(groups[selected]) {
      $("title").textContent=groups[selected]; note("Выберите сообщение в оглавлении. Номер соответствует позиции в исходном массиве messages.");
      if(selected==="instructions") note("Инструкции, навыки и каталог внутри общего сообщения пока не разделены. Показываем записанный состав без предположений.");
    } else if(selected==="parameters") {$("title").textContent="Параметры и формат";facts([["Модель",wire.model],["Формат нового ответа","json_object"],["Роль результатов в истории","system"],["Temperature / Stream","0 / true"]]);}
    else if(selected==="omitted") {$("title").textContent="Не отправлено";note("Старый снимок отчёта исключён: есть более новая версия. Он не входит в 7 сообщений запроса и сумму отправленного контекста.");}
    else if(selected==="links") {$("title").textContent="Связанные данные";facts([["T1 · core.read","Результат передан в сообщении №4"],["R1 · заметка","Сокращённый текст в сообщении №5"]]);note("В production отсюда открываются существующие readers ресурсов и доказательств. В макете показаны только связи.");}
    else {facts([["Попытка","2 · после отказа предыдущего ответа"],["Состав","7 сообщений · ≈ 580 токенов"],["Из них","Инструкции 2 · история 3 · результаты 1 · ресурсы 1"],["Расход по API","Не записан; ≈ 580 — локальная оценка"],["Новое сообщение","№7 — уточнение формата tool_calls"]]);var b=node("button","Открыть сообщения","button compact");b.onclick=function(){loaded=true;expanded.instructions=true;expanded.conversation=true;drawTree();choose("m1");};viewer.append(node("p","Выберите раздел или сообщение. Точный запрос доступен отдельно; интерфейс не меняет его состав.","group-note"),b);}
    viewer.scrollTop=positions[selected]||0;
  }
  document.querySelectorAll("[data-format]").forEach(function(b){b.onclick=function(){format=b.dataset.format;render();};});
  $("taskTabs").innerHTML=RNAssistantEditorTabs.render([{label:"Состав",value:"structure"},{label:"Ответ",value:"response"},{label:"Изменения",value:"changes"}],{label:"Задачи диагностики",dataAttribute:"data-task",selected:task});
  function selectTask(next) {task=next;document.querySelectorAll("[data-task]").forEach(function(b){var active=b.dataset.task===task;b.classList.toggle("active",active);if(active)b.setAttribute("aria-current","page");else b.removeAttribute("aria-current");});content.dataset.detail="true";render();}
  document.querySelectorAll("[data-task]").forEach(function(b){b.onclick=function(){selectTask(b.dataset.task);};});
  $("requestActions").innerHTML=$("messageActions").innerHTML=RNAssistantIcons.render("more");
  $("requestActions").onclick=function(){RNAssistantActionMenu.open(this,[
    {label:"Исходник запроса",icon:"code",onSelect:function(){selectTask("structure");choose("raw");}},
    {label:"Копировать запрос",icon:"copy",disabled:$("sample").value!=="ready",onSelect:function(){copy(JSON.stringify(wire,null,2)).then(function(){$("actionStatus").textContent="Запрос скопирован";}).catch(function(){$("actionStatus").textContent="Копирование недоступно";});}},
    {label:"Сравнить с предыдущим",icon:"compare",onSelect:function(){selectTask("changes");}}
  ],{label:"Действия запроса Q14"});};
  $("messageActions").onclick=function(){var index=selected.slice(1);RNAssistantActionMenu.open(this,[
    {label:"Сообщение целиком",icon:"code",onSelect:function(){viewer.dataset.messageIndex=index;choose("whole");}},
    {label:"Источники сообщения",icon:"link",onSelect:function(){choose("source:"+index);}}
  ],{label:"Действия сообщения №"+(Number(index)+1)});};
  $("order").onchange=function(){loaded=true;drawTree();};
  $("sample").onchange=function(){drawTree();render();};
  $("theme").onclick=function(){var dark=document.documentElement.dataset.theme!=="dark";document.documentElement.dataset.theme=dark?"dark":"light";$("theme").textContent=dark?"Светлая тема":"Тёмная тема";};
  $("back").onclick=function(){content.dataset.detail="false";var active=$("contentsTree").querySelector('[aria-current="page"]');if(active)active.focus();};
  window.addEventListener("pagehide",function(){RNAssistantActionMenu.closeAll();clear();RNAssistantNavigationTree.unmount($("contentsTree"));});
  drawTree();render();
}());
