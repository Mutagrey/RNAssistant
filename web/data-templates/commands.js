'use strict';
importScripts('/core/selectors.js','/core/snapshot.js','/core/json.js','/core/decimal.js','/core/sources.js','/core/byte-source.js',
  '/core/expression.js','/core/template.js','/core/transform.js','/core/engine.js','/vendor/sax.js','/core/xlsx-zip.js','/core/xlsx-reader.js','/core/template-inspection.js');
RNPluginCommands.register('inspect-template',async args=>({inspection:RNDataTemplateInspection.inspect(args.input)}));
RNPluginCommands.register('inspect-dictionary',async args=>{const value=RNTabularJson.parse(args.input);RNTabularTemplate.openDictionaries(value);return {inspection:{revision:value.revision}};});
async function extract(args,context,persist){
  if(persist && args.outputPath===args.sourcePath)throw new Error("Output must differ from the source file");
  const file=await context.files.open(args.sourcePath);
  const name=args.sourcePath+".canonical.json";
  let output;
  try { output=persist ? await context.files.createWriter(args.outputPath) : await context.results.create(name); }
  catch(error) { await file.close(); throw error; }
  let count=0,bytes=0,lastProgress=0,closing;const preview=[];
  const source=RNTabularByteSource.create({byteLength:file.size,label:file.name,readRange:file.readRange,close:()=>{closing=file.close();}});
  async function write(text){bytes+=new TextEncoder().encode(text).byteLength;if(bytes>Math.min(args.maximumBytes,256*1048576))throw new Error('Result budget exceeded');await output.write(text);}
  try{
    await write(JSON.stringify({version:1,kind:'data-templates-extraction',source:{name:file.name,path:args.sourcePath,size:file.size},templateFile:args.templateName}).slice(0,-1)+',"records":[');
    const summary=await RNTabularEngine.extractXlsx(args.input,{byteSource:source,isCancelled:()=>false,yieldControl:()=>new Promise(resolve=>setTimeout(resolve,0)),
      onProgress:value=>{if(performance.now()-lastProgress>200){lastProgress=performance.now();context.progress(JSON.stringify(value));}},
      onBatch:async batch=>{await write((count?',':'')+JSON.stringify(batch.records).slice(1,-1));count+=batch.records.length;if(preview.length<100)preview.push(...batch.records.slice(0,100-preview.length));}});
    if(summary.recordCount!==count)throw new Error('Result record count mismatch');
    await write('],"summary":'+JSON.stringify(summary)+'}');
    const result=await output.close();
    return persist ? {path:args.outputPath,bytes:result.byteLength,summary,preview} : {name:result.name,bytes:result.byteLength,summary,preview};
  }catch(error){await output.abort();throw error;}finally{source.dispose();await closing;}
}
RNPluginCommands.register("extract",(args,context)=>extract(args,context,false));
RNPluginCommands.register("extract-to-file",(args,context)=>extract(args,context,true));
