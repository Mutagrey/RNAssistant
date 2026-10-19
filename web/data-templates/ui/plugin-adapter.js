(function(root){
  'use strict';
  if(root.parent!==root)root.addEventListener('message',function(event){
    if(event.source!==root.parent||!event.data||event.data.type!=='rna.appearance')return;
    var message=event.data,colors=message.colors||{},map={
      '--bg':'panel','--panel':'panel','--secondary-bg':'secondary',
      '--secondary-bg-hover':'secondaryHover','--text':'text','--muted':'muted',
      '--line':'line','--line-strong':'lineStrong','--control-border':'lineStrong',
      '--accent':'accent','--accent-strong':'accent','--button-accent-bg':'accentSoft',
      '--button-primary-bg':'accent','--button-primary-bg-hover':'accentHover',
      '--button-primary-text':'accentInk','--danger':'danger','--success':'success'
    };
    var theme=message.theme==='dark'?'dark':'light',element=document.documentElement;
    element.dataset.hostTheme=theme;element.style.colorScheme=theme;
    Object.keys(map).forEach(function(property){
      var color=colors[map[property]];
      if(typeof color==='string'&&root.CSS&&root.CSS.supports('color',color))
        element.style.setProperty(property,color);
    });
  });
  if (!root.chrome || !root.chrome.webview || !location.hostname.endsWith('.rnassistant-plugin.invalid')) return;
  // Parser-blocking script ensures the trusted SDK is ready before composition.
  var sdk=document.createElement('script');sdk.src='/.host/sdk.js';
  var ready=new Promise(function(resolve,reject){sdk.onload=resolve;sdk.onerror=function(){reject(new Error('Plugin SDK failed to load'));};});document.head.appendChild(sdk);
  root.showDirectoryPicker=async function(){await ready;return RNPlugin.pickProject();};
  async function inspect(command,input,signal){await ready;var call=RNPlugin.invoke(command,{input:input});var cancel=function(){call.cancel();};signal.addEventListener('abort',cancel,{once:true});try{if(signal.aborted)cancel();return (await call).inspection;}finally{signal.removeEventListener('abort',cancel);}}
  async function pickFiles(){
    await ready;
    return new Promise(function(resolve,reject){
      var current=RNPlugin.files.root(),dialog=document.createElement('dialog'),title=document.createElement('h3'),list=document.createElement('div'),close=document.createElement('button');
      title.textContent='Источники из подключённого проекта';close.textContent='Отмена';dialog.append(title,list,close);document.body.appendChild(dialog);dialog.showModal();
      var chosen=[];
      function finish(error){dialog.close();dialog.remove();if(error)reject(error);else resolve(chosen);}
      close.onclick=function(){finish(new DOMException('Отменено','AbortError'));};dialog.oncancel=function(event){event.preventDefault();close.click();};
      async function render(directory){list.replaceChildren();var choose=document.createElement('button');choose.textContent='Добавить выбранные';choose.onclick=function(){finish();};list.appendChild(choose);
        if(directory.path){var back=document.createElement('button');back.textContent='К корню проекта';back.onclick=function(){render(current).catch(finish);};list.appendChild(back);}
        var count=0;for await(var pair of directory.entries()){
          if(++count>500)throw new Error('В папке больше 500 элементов. Выберите вложенную папку.');
          (function(name,handle){var row=document.createElement('div');if(handle.kind==='directory'){var button=document.createElement('button');button.textContent='▸ '+name;button.onclick=function(){render(handle).catch(finish);};row.appendChild(button);}
            else if(/\.(xlsx|json|csv|xls|xlsm|ods|pdf|docx)$/i.test(name)){var check=document.createElement('input');check.type='checkbox';var label=document.createElement('label');label.append(check,document.createTextNode(name));row.appendChild(label);check.onchange=async function(){try{if(check.checked){var file=await handle.getFile();await file.close();chosen.push(file);check.file=file;}else{chosen=chosen.filter(function(file){return file!==check.file;});await check.file.close();}}catch(error){finish(error);}};}list.appendChild(row);})(pair[0],pair[1]);}
      }render(current).catch(finish);
    });
  }
  root.RNDataTemplatesPlugin={pickFiles:pickFiles,inspect:inspect,extract:async function(input,file,templateName,remainingBytes,signal,onProgress){
    await ready;if(!file.rnPluginPath || file.rnPluginProjectId!==RNPlugin.files.root().projectId)throw new Error('Выберите источник из текущего подключённого проекта.');
    var call=RNPlugin.invoke('extract',{input:input,sourcePath:file.rnPluginPath,templateName:templateName,maximumBytes:Math.min(remainingBytes,256*1048576)},function(message){onProgress(JSON.parse(message));});
    var cancel=function(){call.cancel();};signal.addEventListener('abort',cancel,{once:true});
    try{if(signal.aborted)cancel();var result=await call;var output=await RNPlugin.results.open(result.name);var snapshot=Object.freeze({summary:result.summary,preview:result.preview,bytes:result.bytes,mode:'native'});
      return Object.freeze({snapshot:function(){return snapshot;},file:async function(){return output;},discard:async function(){await output.close();}});
    }finally{signal.removeEventListener('abort',cancel);}
  }};
})(window);
