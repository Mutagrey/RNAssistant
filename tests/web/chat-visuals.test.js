'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');
const {chromium} = require('playwright');
const root = path.resolve(__dirname, '../../web');
const scripts = ['vendor/marked.min.js','vendor/purify.min.js','app-echarts-sandbox-runtime.js','app-html-vendor-catalog.js','app-html-vendor-runtime.js','app-icons.js','app-chat-visuals.js','app-markdown.js'];
const styles=Array.from(fs.readFileSync(path.join(root,'index.html'),'utf8').matchAll(/<link rel="stylesheet"[^>]+>/g),m=>m[0]).join('');
const shell = `<!doctype html><html data-theme="light"><head><meta charset="utf-8">${styles}</head><body>
<script>window.state={};window.detectCodeLanguage=c=>(c.className.match(/language-(\\S+)/)||[])[1]||'';window.normalizeCodeLanguage=x=>x;window.iconSvg=()=>'';window.logOnce=()=>{};window.copyText=t=>window.copied=t;</script>
${scripts.map(file=>`<script src="/js/${file}"></script>`).join('')}
<div id="answer" class="markdown"></div><script>window.show=(text,options={})=>{let root=document.getElementById('answer');clearMarkdownEnhancements(root);root.innerHTML=markdown(text);enhanceMarkdown(root,{enableVisuals:true,enableJsonViewer:true,sourceText:text,...options});};</script></body></html>`;
(async () => {
 const server=http.createServer((req,res)=>{
  if(req.url==='/'){res.setHeader('Content-Type','text/html');return res.end(shell);}
  const file=path.join(root,req.url.split('?')[0]);
  if(!file.startsWith(root+path.sep)||!fs.existsSync(file)){res.statusCode=404;return res.end();}
  res.setHeader('Content-Type',file.endsWith('.js')?'text/javascript':file.endsWith('.css')?'text/css':'text/plain');res.end(fs.readFileSync(file));
 });
 await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve));
 const browser=await chromium.launch({headless:true,...(process.env.BROWSER_EXECUTABLE?{executablePath:process.env.BROWSER_EXECUTABLE}:{})});
 try {
  const page=await browser.newPage({viewport:{width:750,height:800}}), errors=[], external=[], blocked=new Set();
  page.on('pageerror',e=>errors.push(e.message));
  page.on('requestfailed',r=>{if(r.failure() && /csp/i.test(r.failure().errorText))blocked.add(r.url());});
  page.on('request',r=>{if(r.url().startsWith('https://example.invalid'))external.push(r.url());});
  await page.goto(`http://127.0.0.1:${server.address().port}/`);
  const show=(text,options)=>page.evaluate(([text,options])=>show(text,options),[text,options]);
  await show('```mermaid\nflowchart LR\n A[Вопрос] --> B[Ответ]\n```');
  await page.waitForSelector('.chat-visual iframe');
  assert.equal(await page.frameLocator('.chat-visual iframe').locator('svg').count(),1);
  assert.match(await page.frameLocator('.chat-visual iframe').locator('body').textContent(),/Вопрос/);
  assert.equal(await page.locator('.chat-visual iframe').getAttribute('sandbox'),'');
  await page.getByRole('button',{name:'Копировать',exact:true}).click({timeout:2000});
  assert.match(await page.evaluate(()=>copied),/flowchart LR/);
  await show('```mermaid\nflowchart LR\n A-->B\n```',{streaming:true});
  assert.equal(await page.locator('.chat-visual').count(),0);
  await show('```html\n<h1>Incomplete</h1>');
  assert.equal(await page.locator('.chat-visual').count(),0);
  await show('```mermaid\nnot a diagram\n```');
  await page.waitForFunction(()=>document.querySelector('.chat-visual-status').textContent.includes('Не удалось'));
  assert.equal(await page.locator('.chat-visual pre').isVisible(),true);
  const html='<!doctype html><html lang="ru"><head><title>Preview</title></head><body class="authored-body"><img src="https://example.invalid/image"><iframe src="https://example.invalid/frame"></iframe><link rel="stylesheet" href="https://example.invalid/style"><h1>Пример</h1><button id="plus">Нажать</button><output id="count">0</output><div id="chart" style="height:220px"></div><script>let n=0;document.getElementById("plus").onclick=()=>document.getElementById("count").textContent=++n;echarts.init(document.getElementById("chart")).setOption({xAxis:{data:["A","B"]},yAxis:{},series:[{type:"bar",data:[2,4]}]});fetch("https://example.invalid/leak").catch(()=>{});try{parent.document.body.dataset.leak="yes"}catch(e){}</script></body></html>';
  await show('```html\n'+html+'\n```');
  await page.frameLocator('.chat-visual iframe').locator('canvas').waitFor();
  assert.equal(await page.getByRole('button',{name:/Запустить|Остановить/}).count(),0,
    'completed HTML starts automatically without a Play or Stop step');
  assert.equal(await page.locator('.chat-visual > .block-tools button').count(),3);
  const frame=await page.locator('.chat-visual iframe').elementHandle();
  const content=await frame.contentFrame();
  assert.equal(await content.locator('body').getAttribute('class'),'authored-body');
  await content.locator('#plus').click();assert.equal(await content.locator('#count').textContent(),'1');
  await page.getByRole('button',{name:'Показать код'}).click();
  assert.equal(await page.locator('.chat-visual pre').isVisible(),true);
  assert.equal(await page.locator('.chat-visual-surface').isVisible(),false,
    'code and live HTML are separate views');
  await page.getByRole('button',{name:'Показать HTML'}).click();
  assert.equal(await content.locator('#count').textContent(),'1','returning to preview keeps the live frame');
  await page.getByRole('button',{name:'Увеличить',exact:true}).click();
  assert.equal(await content.locator('#count').textContent(),'1','enlarging preserves the live frame');
  await page.getByRole('button',{name:'Свернуть',exact:true}).click();
  assert.equal(await page.evaluate(()=>document.body.dataset.leak),undefined);
  assert.equal(await page.locator('iframe').getAttribute('sandbox'),'allow-scripts');
  assert.ok(external.every(url=>blocked.has(url)),'all external asset attempts must fail with CSP, not DNS/network errors');
  await show('New chat');assert.equal(await page.locator('iframe').count(),0);
  await show('```html\n'+html+'\n```');
  await page.frameLocator('.chat-visual iframe').locator('#count').waitFor();
  assert.equal(await page.frameLocator('.chat-visual iframe').locator('#count').textContent(),'0');
  await show('New chat');assert.equal(await page.locator('iframe').count(),0);
  await page.evaluate(() => {
    window.originalVendorRuntime=RNAssistantHtmlVendorRuntime;
    window.RNAssistantHtmlVendorRuntime={ensure:()=>new Promise(resolve=>window.resolveVendor=resolve),used:()=>[{}],source:()=>({js:''})};
  });
  await show('```html\n<script>echarts.init()</script>\n```');
  await page.waitForFunction(()=>typeof window.resolveVendor==='function');
  await show('Switched session');
  await page.evaluate(()=>resolveVendor());
  await page.evaluate(()=>{window.RNAssistantHtmlVendorRuntime=originalVendorRuntime;});
  assert.equal(await page.locator('iframe').count(),0,'late vendor load cannot resurrect a closed preview');

  await show('```html\n<script>throw new Error("preview failed")</script>\n```');
  await page.waitForFunction(()=>document.querySelector('.chat-visual-status').textContent.includes('preview failed'));
  errors.length=0;
  for(const theme of ['light','dark']){
   await page.evaluate(theme=>document.documentElement.setAttribute('data-theme',theme),theme);
   await page.setViewportSize({width:360,height:740});
   await show('```mermaid\nflowchart TD\n A[Запрос] --> B[Результат]\n```\n\n| Показатель | Значение |\n|---|---:|\n| Пример | 42 |');
   await page.waitForSelector('.chat-visual iframe');
   assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),true);
   await page.screenshot({path:`/tmp/rna-chat-visuals-${theme}.png`});
  }
  assert.deepEqual(errors,[]);
  console.log('PASS chat visuals: real Mermaid, completed fences, isolated offline HTML/ECharts, lifecycle, errors and themes');
 } finally {await browser.close();await new Promise(resolve=>server.close(resolve));}
})().catch(error=>{console.error(error);process.exitCode=1;});
