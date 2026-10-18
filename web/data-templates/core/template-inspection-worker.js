importScripts("selectors.js", "snapshot.js", "json.js", "decimal.js", "sources.js", "expression.js", "template.js");
importScripts("template-inspection.js");
self.onmessage=event=>{try{self.postMessage({type:"result",inspection:RNDataTemplateInspection.inspect(event.data)});}catch(error){self.postMessage({type:"error",error:{code:error.code||"TEMPLATE_INVALID",path:error.path||"",message:error.message}});}};
