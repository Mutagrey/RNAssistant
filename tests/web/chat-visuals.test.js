'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');
const {chromium} = require('playwright');
const root = path.resolve(__dirname, '../../web');
const scripts = ['vendor/marked.min.js','vendor/purify.min.js','app-icons.js','app-chat-visuals.js','app-markdown.js'];
const styles=Array.from(fs.readFileSync(path.join(root,'index.html'),'utf8').matchAll(/<link rel="stylesheet"[^>]+>/g),m=>m[0]).join('');
const shell = `<!doctype html><html data-theme="light"><head><meta charset="utf-8">${styles}</head><body>
<script>window.state={};window.detectCodeLanguage=c=>(c.className.match(/language-(\\S+)/)||[])[1]||'';window.normalizeCodeLanguage=x=>x;window.iconSvg=()=>'';window.logOnce=()=>{};window.copyText=t=>window.copied=t;window.htmlPreviews=[];</script>
${scripts.map(file=>`<script src="/js/${file}"></script>`).join('')}
<script>window.RNAssistantBrowser={openHtmlPreview:function(html,options){htmlPreviews.push({html:html,options:options});return true;}};</script>
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
  const html='<!doctype html><html lang="ru"><head><title>Preview</title><meta http-equiv="refresh" content="0;url=https://example.invalid/meta"></head><body class="authored-body"><img src="https://example.invalid/image"><iframe src="https://example.invalid/frame"></iframe><link rel="stylesheet" href="https://example.invalid/style"><h1>Пример</h1><a id="outside" href="https://example.invalid/navigation">Снаружи</a><button id="plus">Нажать</button><output id="count">0</output><div id="chart" style="height:220px"></div><script>let n=0;document.getElementById("plus").onclick=()=>document.getElementById("count").textContent=++n;echarts.init(document.getElementById("chart")).setOption({xAxis:{data:["A","B"]},yAxis:{},series:[{type:"bar",data:[2,4]}]});fetch("https://example.invalid/leak").catch(()=>{});try{parent.document.body.dataset.leak="yes"}catch(e){}</script></body></html>';
  await show('```html\n'+html+'\n```');
  await page.locator('.chat-visual.is-static iframe').waitFor();
  const staticFrame=page.frameLocator('.chat-visual.is-static iframe');
  assert.equal(await page.locator('.chat-visual pre').isVisible(),false,'completed HTML opens as a static preview by default');
  assert.equal(await page.locator('.chat-visual-surface').isVisible(),true);
  assert.equal(await page.locator('.chat-visual-status').textContent().then(value=>value.includes('Скрипты не выполняются')),true);
  assert.equal(await page.locator('.chat-visual iframe').getAttribute('sandbox'),'');
  assert.equal(await staticFrame.locator('#chart canvas').count(),0,'static preview does not run authored scripts');
  assert.equal(await staticFrame.locator('#outside').getAttribute('href'),null,'static preview neutralizes authored link navigation');
  assert.equal((await page.locator('.chat-visual iframe').getAttribute('srcdoc')).includes('http-equiv="refresh"'),false,'static preview removes meta refresh');
  const outside = staticFrame.locator('#outside'), outsideBox = await outside.boundingBox();
  assert.ok(outsideBox,'static link remains inspectable after navigation attributes are removed');
  await page.mouse.click(outsideBox.x + outsideBox.width / 2, outsideBox.y + outsideBox.height / 2);
  await outside.focus(); await page.keyboard.press('Enter');
  await outside.evaluate(node=>node.click());
  await page.waitForTimeout(300);
  assert.deepEqual(external,[],'trusted, keyboard and synthetic link activation cannot issue a network request');
  await staticFrame.locator('#plus').click();
  assert.equal(await staticFrame.locator('#count').textContent(),'0','static preview blocks inline event handlers');
  assert.equal(await page.locator('.chat-visual').getByText('Native Browser недоступен',{exact:false}).count(),0,
    'inline HTML never reports native Browser availability');
  assert.equal(await page.getByRole('button',{name:'Запустить',exact:true}).count(),1,
    'static HTML exposes an explicit Play control');
  assert.equal(await page.locator('.chat-visual > .block-tools button').count(),4);
  const height=await page.locator('.chat-visual.kind-html .chat-visual-surface').evaluate(node=>node.getBoundingClientRect().height);
  assert.ok(height>=360&&height<=680,'inline HTML preview has a useful default height');
  assert.equal(await page.locator('.chat-visual.kind-html .chat-visual-surface').evaluate(node=>getComputedStyle(node).resize),'vertical',
    'inline HTML preview remains directly resizable');
  await page.getByRole('button',{name:'Запустить',exact:true}).click();
  await page.waitForFunction(()=>htmlPreviews.length===1);
  assert.equal(await page.evaluate(()=>htmlPreviews[0].html),html,'Play hands the exact source to the isolated Browser executor');
  assert.equal(await page.locator('.chat-visual iframe').getAttribute('sandbox'),'','the message iframe never receives script execution');
  assert.match(await page.locator('.chat-visual-status').textContent(),/изолированный Browser/i);
  await page.getByRole('button',{name:'Показать код'}).click();
  assert.equal(await page.locator('.chat-visual pre').isVisible(),true);
  assert.equal(await page.locator('.chat-visual-surface').isVisible(),false,'code and live HTML are separate views');
  await page.getByRole('button',{name:'Показать HTML'}).click();
  await page.locator('.chat-visual.is-static iframe').waitFor();
  assert.ok(external.every(url=>blocked.has(url)),'all external asset attempts must fail with CSP, not DNS/network errors');
  assert.equal(await page.evaluate(()=>document.body.dataset.leak),undefined);
  await page.screenshot({path:'/tmp/rna-chat-html-preview.png'});
  await page.getByRole('button',{name:'Увеличить',exact:true}).click();
  await page.getByRole('button',{name:'Свернуть',exact:true}).click();
  await show('New chat');assert.equal(await page.locator('iframe').count(),0);
  await show('```html\n'+html+'\n```');
  await page.locator('.chat-visual.is-static iframe').waitFor();
  await show('New chat');assert.equal(await page.locator('iframe').count(),0);
  await page.evaluate(()=>{window.savedHtmlExecutor=RNAssistantBrowser.openHtmlPreview;RNAssistantBrowser.openHtmlPreview=null;});
  await show('```html\n<p>No executor</p>\n```');
  await page.locator('.chat-visual.is-static iframe').waitFor();
  await page.getByRole('button',{name:'Запустить',exact:true}).click();
  await page.waitForFunction(()=>document.querySelector('.chat-visual-status').textContent.includes('недоступен'));
  await page.evaluate(()=>{RNAssistantBrowser.openHtmlPreview=savedHtmlExecutor;});
  for(const theme of ['light','dark']){
   await page.evaluate(theme=>document.documentElement.setAttribute('data-theme',theme),theme);
   await page.setViewportSize({width:360,height:740});
   await show('```mermaid\nflowchart TD\n A[Запрос] --> B[Результат]\n```\n\n| Показатель | Значение |\n|---|---:|\n| Пример | 42 |');
   await page.waitForSelector('.chat-visual iframe');
   assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),true);
   await page.screenshot({path:`/tmp/rna-chat-visuals-${theme}.png`});
  }
  assert.deepEqual(errors,[]);
  console.log('PASS chat visuals: real Mermaid, static no-script HTML, explicit Play handoff to isolated Browser, source toggle, resize and themes');
 } finally {await browser.close();await new Promise(resolve=>server.close(resolve));}
})().catch(error=>{console.error(error);process.exitCode=1;});
