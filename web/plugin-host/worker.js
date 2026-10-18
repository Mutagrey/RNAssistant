'use strict';
importScripts('/.host/sdk.js');
var handlers = new Map(), started = false;
self.RNPluginCommands = Object.freeze({register:function (id,handler) { if(started || handlers.has(id) || typeof handler!=='function') throw new Error('Invalid command registration');handlers.set(id,handler); }});
self.addEventListener('message', async function (event) {
  var input=event.data;
  if (!input || input.type!=='start' || started) return;
  try {
    await RNPlugin.connect();
    importScripts('/'+input.entry); started=true;
    var handler=handlers.get(input.command); if(!handler) throw new Error('Command entry did not register its handler');
    var result=await handler(JSON.parse(input.argumentsJson),Object.freeze({files:RNPlugin.files,results:RNPlugin.results,
      progress:function (message) { self.postMessage({type:'progress',message:String(message).slice(0,2000)}); }}));
    self.postMessage({type:'complete',json:JSON.stringify(result)});
  } catch(error) { self.postMessage({type:'failed',message:error.message,code:error.code || 'plugin_command_failed'}); }
});
