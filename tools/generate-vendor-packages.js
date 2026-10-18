'use strict';
// Inert inventory generation only; never evaluates a vendor or source carrier.
const fs=require('node:fs'),path=require('node:path'),crypto=require('node:crypto');
const root=path.resolve(__dirname,'../web'), hash=b=>crypto.createHash('sha256').update(b).digest('hex');
function generate(){
  const inventory=JSON.parse(fs.readFileSync(path.join(root,'vendor-manifest.json')));
  const tabular=JSON.parse(fs.readFileSync(path.join(root,'data-templates/vendor-lock.json'))).packages;
  return inventory.packages.filter(p=>!p.sourceOnly).map(p=>{
    const files=[],sources=[],entrypoints=[],licenses=[],dt=tabular.find(d=>d.id===p.id);
    function add(name,source,kind,carrierField){
      const original=fs.readFileSync(path.join(root,source));let bytes=original;
      if(carrierField){const text=original.toString('utf8'),prefix='window.RNAssistantHtmlVendorRuntime.register("'+p.id+'", ';
        const start=text.indexOf(prefix),end=text.lastIndexOf(');');if(start<0||end<=start)throw Error('Invalid inert carrier: '+source);
        bytes=Buffer.from(JSON.parse(text.slice(start+prefix.length,end))[carrierField]);}
      files.push({path:name,sha256:hash(bytes)});sources.push({path:name,sourcePath:source,sourceSha256:hash(original),carrierField:carrierField||null});
      if(kind)entrypoints.push({path:name,kind});else licenses.push(name);
    }
    const kind=name=>name.endsWith('.js')?'Script':name.endsWith('.css')?'Style':'Asset';
    if(dt){for(const f of dt.runtime)add(f.path.slice(7),'data-templates/'+f.path,kind(f.path));for(const f of dt.licenses)add(f.path.slice(7),'data-templates/'+f.path);}
    else {
      for(const f of inventory.files.filter(f=>f.package===p.id)){
        if(f.path.endsWith('.source.js')){
          const text=fs.readFileSync(path.join(root,f.path),'utf8'),prefix='window.RNAssistantHtmlVendorRuntime.register("'+p.id+'", ';
          const carrier=JSON.parse(text.slice(text.indexOf(prefix)+prefix.length,text.lastIndexOf(');')));
          if(carrier.js)add(p.htmlWorkspace.dependencyFile,f.path,'Script','js');
          if(carrier.css)add(p.id+'.min.css',f.path,'Style','css');
        }else add(f.path.replace(/^(js|css)\/vendor\//,''),f.path,kind(f.path));
      }
      for(const f of p.licenseFiles)add(f.path,f.path);
    }
    return {manifest:{formatVersion:1,id:p.id.replace(/\./g,'-'),version:p.version,description:p.htmlWorkspace?.purpose||p.id,
      source:p.npmTarball,license:p.license,licenses,files,entrypoints,
      dependencies:(p.htmlWorkspace?.requires||[]).map(id=>({id,version:inventory.packages.find(p=>p.id===id).version}))},sources};
  });
}
const output=JSON.stringify(generate(),null,2)+'\n',target=path.join(root,'vendor-packages.json');
if(process.argv.includes('--check')){if(fs.readFileSync(target,'utf8')!==output)throw Error('Regenerate web/vendor-packages.json');}
else fs.writeFileSync(target,output);
