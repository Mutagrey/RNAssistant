/* RNAssistant plugin protocol v1. Host-owned transport; never forwards to parent. */
(function (g) {
  'use strict';
  var pending = new Map(), sequence = 0, projectId = null;
  var worker = typeof document === 'undefined';
  var native = !worker && g.chrome && g.chrome.webview;
  function receive(message) {
    var data = message.data, call = data && pending.get(data.requestId);
    if (!call || data.version !== 1) return;
    if (data.progress != null) { if (call.progress) call.progress(data.progress); return; }
    clearTimeout(call.timer); pending.delete(data.requestId);
    if (data.ok) call.resolve(data.result || {});
    else { var error = new Error(data.message || 'Plugin request failed'); error.code = data.errorCode; call.reject(error); }
  }
  if (native) native.addEventListener('message', receive);
  else if (worker) g.addEventListener('message', function (event) { if (event.data && event.data.type === 'rpc-result') receive({data:event.data.response}); });
  function request(method, args, progress) {
    if (!native && !worker) return Promise.reject(new Error('RNAssistant plugin host is unavailable'));
    if (pending.size >= 32) return Promise.reject(new Error('Too many pending plugin requests'));
    var requestId = 'rpc_' + (++sequence), message = Object.assign({version:1, requestId:requestId, method:method, projectId:projectId}, args || {});
    if (JSON.stringify(message).length > 1024*1024) return Promise.reject(new Error('Plugin request exceeds 1 MiB'));
    var result = new Promise(function (resolve, reject) { var timer=setTimeout(function(){pending.delete(requestId);var error=new Error('Plugin request timed out. Inspect writes before retrying.');error.code=(method==='CommitWrite'||method==='MakeDirectory')?'effect_unknown':'plugin_timeout';reject(error);},360000);pending.set(requestId, {resolve:resolve,reject:reject,progress:progress,timer:timer});
      if (native) native.postMessage(JSON.stringify(message)); else g.postMessage({type:'rpc',request:message}); });
    result.cancel = function () { return request('Cancel', {invocationId:requestId}); };
    return result;
  }
  function child(parent, name) {
    if (typeof name !== 'string' || !name || /[\\/:]/.test(name) || name === '.' || name === '..') throw new TypeError('Expected a single file name');
    return parent ? parent + '/' + name : name;
  }
  async function bytes(url, offset, count) {
    var response = await fetch(url + '?offset=' + offset + '&count=' + count, {cache:'no-store',credentials:'omit'});
    if (!response.ok) throw new Error('Project read failed (' + response.status + ')');
    var buffer = await response.arrayBuffer(); if (buffer.byteLength !== count) throw new Error('Incomplete project read'); return buffer;
  }
  async function openFile(path, binding) {
    return remoteFile(await request('OpenFile', {path:path,projectId:binding || projectId}),path,binding || projectId,false);
  }
  function remoteFile(pin,path,binding,result) {
    var closed = false;
    async function range(offset, count) {
      if (closed) throw new Error('File reader is closed');
      if (!Number.isSafeInteger(offset) || !Number.isSafeInteger(count) || offset < 0 || count < 0 || offset + count > pin.byteLength) throw new RangeError('Invalid range');
      var result = new Uint8Array(count);
      for (var i = 0; i < count; i += 262144) result.set(new Uint8Array(await bytes(pin.url, offset + i, Math.min(262144,count - i))), i);
      return result.buffer;
    }
    var file = {name:pin.name,size:pin.byteLength,type:'application/octet-stream',lastModified:0,
      arrayBuffer:function () { if (pin.byteLength > 16777216) throw new Error('Use bounded readRange for files larger than 16 MiB'); return range(0,pin.byteLength); },
      text:async function () { return new TextDecoder('utf-8',{fatal:true}).decode(await file.arrayBuffer()); },
      slice:function (start,end) { start = start || 0; end = end == null ? pin.byteLength : Math.min(end,pin.byteLength); return {arrayBuffer:function () { return range(start,Math.max(0,end-start)); }}; },
      readRange:range, close:async function () { if (!closed) { closed = true; await request(result?'DropResult':'CloseFile',result?{path:path}:{handle:pin.handle,projectId:binding}); } },
      downloadUrl:result ? pin.url.replace('/.result/read/','/.result/download/') : null, rnPluginPath:path, rnPluginProjectId:binding, webkitRelativePath:path};
    return file;
  }
  async function writer(path,binding,result) {
    // Acceptance is an exact read, even when replacing an existing file wholesale.
    var old;
    try { if (!result) old = await openFile(path,binding); if(old) await old.close(); } catch (error) { if (error.code !== 'not_found' && error.code !== 'file_missing' && error.code !== 'file_not_found') throw error; }
    var target = await request(result?'BeginResult':'BeginWrite',{path:path,projectId:binding || projectId}), position = 0, closed = false;
    return {write:async function (value) {
      if (closed) throw new Error('Writer is closed');
      if (typeof value === 'string') value = new TextEncoder().encode(value);
      else if (value instanceof Blob) value = new Uint8Array(await value.arrayBuffer());
      else if (value instanceof ArrayBuffer) value = new Uint8Array(value);
      else if (ArrayBuffer.isView(value)) value = new Uint8Array(value.buffer,value.byteOffset,value.byteLength);
      else throw new TypeError('Expected bytes or text');
      for (var i=0;i<value.length;i+=262144) {
        var block=value.subarray(i,i+262144), response=await fetch(target.url+'?offset='+position,{method:'POST',body:block,credentials:'omit'});
        if (!response.ok) throw new Error('Project staging failed ('+response.status+')'); position+=block.length;
      }
    },close:async function () { if (closed) return; closed=true; var saved=await request(result?'CommitResult':'CommitWrite',{handle:target.handle,projectId:binding || projectId}); return {name:result?saved.name:path,byteLength:saved.byteLength}; },
    abort:async function () { if (closed) return; closed=true; return request('AbortWrite',{handle:target.handle}); }};
  }
  function FileHandle(path,binding) { this.projectId=binding || projectId;this.kind='file';this.name=path.split('/').pop();this.path=path; }
  FileHandle.prototype.getFile=function () { return openFile(this.path,this.projectId); };
  FileHandle.prototype.createWritable=function () { return writer(this.path,this.projectId); };
  FileHandle.prototype.queryPermission=FileHandle.prototype.requestPermission=async function () { return 'granted'; };
  function Directory(path,name,binding) { this.projectId=binding || projectId;this.kind='directory';this.path=path || '';this.name=name || (path || '').split('/').pop();this.rnPlugin=true; }
  Directory.prototype.queryPermission=Directory.prototype.requestPermission=async function () { return 'granted'; };
  Directory.prototype.getDirectoryHandle=async function (name,options) {
    var path=child(this.path,name);
    if (options && options.create) await request('MakeDirectory',{path:path,projectId:this.projectId});
    var stat;try{stat=await request('Stat',{path:path,projectId:this.projectId});}catch(error){if(error.code==='not_found')error.name='NotFoundError';throw error;} if(stat.kind!=='directory') throw new TypeError('Expected directory'); return new Directory(path,name,this.projectId);
  };
  Directory.prototype.getFileHandle=async function (name,options) {
    var path=child(this.path,name);
    try { var stat=await request('Stat',{path:path,projectId:this.projectId}); if(stat.kind!=='file') throw new TypeError('Expected file'); }
    catch(error) { if (error.code!=='not_found' || !options || !options.create) { if(error.code==='not_found') error.name='NotFoundError'; throw error; } }
    return new FileHandle(path,this.projectId);
  };
  Directory.prototype.entries=async function* () {
    var offset=0;
    do { var page=await request('List',{path:this.path,offset:offset,projectId:this.projectId});
      for(var item of page.items) yield [item.name,item.isDirectory ? new Directory(item.path,item.name,this.projectId) : new FileHandle(item.path,this.projectId)]; offset=page.nextOffset;
    } while (offset!=null);
  };
  Directory.prototype.values=async function* () { for await(var entry of this.entries()) yield entry[1]; };
  var files = {createWriter:function(path){return writer(path);},reconcile:async function(path){return JSON.parse((await request('ReconcileFile',{path:path})).json);},root:function () { return new Directory('','Project'); },open:openFile,
    readText:async function (path) { var file=await openFile(path);try{return await file.text();}finally{await file.close();} },
    writeText:async function (path,text) { var output=await writer(path);try{await output.write(text);await output.close();}catch(error){await output.abort();throw error;} }};
  g.RNPlugin=Object.freeze({version:1,files:files,request:request,
    connect:async function () { var ready=await request('Ready');projectId=ready.handle; },
    results:{create:function (name) {return writer(name,null,true);},open:async function (name) {return remoteFile(await request('OpenResult',{path:name}),name,null,true);}},
    pickProject:async function () { var selected=await request('PickProject');projectId=selected.handle;return new Directory('',selected.name,projectId); },
    invoke:function (command,args,progress) { var call=request('Invoke',{command:command,argumentsJson:JSON.stringify(args)},progress), result=call.then(function (value) { return JSON.parse(value.json); }); result.cancel=call.cancel; return result; }});
})(typeof self !== 'undefined' ? self : window);
