'use strict';
(function () {
  var worker=new Worker('/.host/worker.js'), native=window.chrome.webview;
  native.addEventListener('message',function (event) { var message=event.data; if(message.type==='start') worker.postMessage(message);else worker.postMessage({type:'rpc-result',response:message}); });
  worker.addEventListener('message',function (event) { native.postMessage(JSON.stringify(event.data)); });
  worker.addEventListener('error',function (event) { native.postMessage(JSON.stringify({type:'failed',message:event.message}));worker.terminate(); });
  native.postMessage(JSON.stringify({type:'ready'}));
})();
