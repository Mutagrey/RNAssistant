'use strict';
// Shared components run without Shell/bridge; production sections consume the same tree.
const assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path');
const {spawn}=require('node:child_process'),{chromium}=require('playwright');
const root=path.resolve(__dirname,'../..');
(async()=>{
 const server=spawn('python3',['-B','prototypes/serve_ui.py','--port','0'],{cwd:root});server.stderr.resume();let browser;
 try {
  const address=await new Promise((resolve,reject)=>{server.once('error',reject);server.stdout.on('data',b=>{const m=/RNAssistant design: (http:\/\/127\.0\.0\.1:\d+\/)/.exec(String(b));if(m)resolve(m[1]);});});
  browser=await chromium.launch({headless:true,...(process.env.BROWSER_EXECUTABLE?{executablePath:process.env.BROWSER_EXECUTABLE}:{})});
  const standalone=await browser.newPage({reducedMotion:"reduce",viewport:{width:700,height:600}}),product=await browser.newPage({reducedMotion:"reduce",viewport:{width:1280,height:900}}),errors=[];
  for(const page of [standalone,product]){page.setDefaultTimeout(8000);page.on('pageerror',e=>errors.push(e.message));}
  await standalone.setContent('<main style="width:340px"><div class="tree-toolbar"><button class="icon-btn" id="actions" aria-label="Действия">⋯</button></div><div id="tree"></div><section id="operation"></section></main>');
  for(const name of ['app-product-tokens.css','app-product-components.css'])await standalone.addStyleTag({path:path.join(root,'web/css',name)});
  for(const name of ['app-icons.js','app-accent.js','app-navigation-tree.js','app-action-menu.js'])await standalone.addScriptTag({path:path.join(root,'web/js',name)});
  await standalone.addScriptTag({path:path.join(root,'web/data-templates/ui/operation-view.js')});
  await standalone.evaluate(()=>{
   window.calls={select:0,menu:0,cancel:0};
   RNAssistantNavigationTree.render(document.getElementById('tree'),[{key:'g',kind:'parent',title:'Проект',expanded:true,iconHtml:RNAssistantIcons.render('folder'),children:[{key:'c',kind:'leaf',title:'Чат',selected:true,iconHtml:RNAssistantIcons.render('chat')}]}],{onSelect:()=>{calls.select++;}});
   document.getElementById('actions').addEventListener('click',function(){RNAssistantActionMenu.open(this,[{label:'Недоступно',disabled:true,onSelect:()=>{throw Error('disabled action');}},{label:'Открыть',icon:'open',onSelect:()=>calls.menu++},{label:'Закрыть',icon:'close',onSelect:()=>calls.menu++}]);});
   const operation=document.getElementById('operation');
   operation.innerHTML=['stage','title','current','elapsed','count','detail','note','meter','percent'].map(name=>'<span data-operation-part="'+name+'"></span>').join('')+'<progress data-operation-part="progress"></progress><button data-operation-part="cancel">Отменить</button><button data-operation-part="dismiss">Закрыть</button>';
   window.operation=DataTemplatesOperationView.create(operation,{cancel:()=>calls.cancel++,dismiss:()=>{}});
   window.operation.render({state:'running',stage:'read',title:'Чтение',currentItem:'',elapsedMs:0,measure:{completed:3,total:null,label:'Строки'},detail:'',note:''});
  });
  assert.deepEqual(await standalone.evaluate(()=>[typeof RNAssistantDockWorkspace,typeof send,typeof state]),['undefined','undefined','undefined']);
  await standalone.locator('[data-tree-key=c]').focus();await standalone.keyboard.press('Enter');
  assert.equal(await standalone.evaluate(()=>calls.select),1);
  await standalone.locator('#actions').click();await standalone.keyboard.press('ArrowDown');
  assert.equal(await standalone.evaluate(()=>document.activeElement.getAttribute('aria-label')),'Закрыть');
  await standalone.keyboard.press('Escape');assert.equal(await standalone.evaluate(()=>document.activeElement.id),'actions');
  await standalone.locator('#actions').click();await standalone.getByRole('menuitem',{name:'Открыть',exact:true}).click();
  assert.equal(await standalone.evaluate(()=>calls.menu),1);assert.equal(await standalone.getByRole('menu').count(),0);
  assert.equal(await standalone.locator('progress').getAttribute('value'),null,'unknown total stays indeterminate');
  await standalone.locator('[data-operation-part=cancel]').click();assert.equal(await standalone.evaluate(()=>calls.cancel),1);
  await standalone.evaluate(()=>{operation.dispose();document.querySelector('[data-operation-part=cancel]').click();RNAssistantNavigationTree.unmount(document.getElementById('tree'));document.querySelector('[data-tree-select=c]').click();});
  assert.deepEqual(await standalone.evaluate(()=>[calls.cancel,calls.select]),[1,1],'dispose releases component callbacks');
  // A third, VBA-shaped adapter proves the pane has no source-name branches or host dependency.
  await standalone.addScriptTag({path:path.join(root,'web/js/app-navigation-pane.js')});
  await standalone.evaluate(()=>{
   const nav=document.createElement('section');nav.id='nav';nav.style.width='318px';
   nav.innerHTML='<div id="paneHeading"></div><input id="paneQuery" aria-label="Поиск модулей" placeholder="Найти…"><div id="paneCommands" hidden><button>Добавить модуль</button></div><div id="paneTree" style="height:160px;overflow:auto"></div>';
   document.body.appendChild(nav);calls.query=0;calls.retry=0;calls.more=0;
   const options={root:nav,heading:nav.querySelector('#paneHeading'),search:nav.querySelector('input'),tree:nav.querySelector('#paneTree'),commands:nav.querySelector('#paneCommands'),title:'Проект с очень длинным названием',commandLabel:'Создать модуль'};
   const adapter={onQuery:()=>calls.query++,onRetry:()=>calls.retry++,onMore:()=>calls.more++};
   window.pane=RNAssistantNavigationPane.mount(options,adapter);
   if(pane!==RNAssistantNavigationPane.mount(options,adapter))throw Error('duplicate pane');
   RNAssistantNavigationPane.bindSearches(document);
   pane.render([{key:'project',kind:'group',title:'VBAProject',expanded:true,children:[{key:'module',kind:'leaf',title:'Модуль с длинным названием',selected:true}]}],{onSelect:()=>calls.select++});pane.setState({});
  });
  await standalone.getByRole('button',{name:'Поиск модулей',exact:true}).click();
  await standalone.locator('#paneQuery').fill('Модуль');
  await standalone.keyboard.press('Escape');
  assert.equal(await standalone.evaluate(()=>calls.query),2,'one query handler even after repeat bind; Escape clears filter');
  assert.equal(await standalone.locator('#paneQuery').isVisible(),false);
  assert.equal(await standalone.evaluate(()=>document.activeElement.getAttribute('aria-label')),'Поиск модулей');
  await standalone.locator('[data-tree-key=module]').focus();await standalone.keyboard.press('Enter');
  assert.equal(await standalone.evaluate(()=>calls.select),2,'keyboard opens the adapter target');
  await standalone.evaluate(()=>pane.setState({message:'Модули недоступны',retry:true,more:true}));
  await standalone.getByRole('button',{name:'Повторить загрузку'}).click();await standalone.getByRole('button',{name:'Показать ещё',exact:true}).click();
  assert.deepEqual(await standalone.evaluate(()=>[calls.retry,calls.more]),[1,1]);
  await standalone.evaluate(()=>{pane.setState({busy:true,message:'Загрузка…',retry:true,more:true});});
  assert.equal(await standalone.getByRole('button',{name:'Повторить загрузку'}).isDisabled(),true);
  await standalone.evaluate(()=>{pane.dispose();document.querySelector('#paneQuery').dispatchEvent(new Event('input'));});
  assert.equal(await standalone.evaluate(()=>calls.query),2,'dispose releases query callbacks');
  await product.goto(address+'preview/web/?scenario=ready');await product.waitForFunction(()=>state.activeChatId==='preview-chat'&&!state.initializePromise);
  for(const [section,selector] of [['instructions','#instructionsList'],['artifacts','#htmlWorkspaceTree'],['vba','#vbaModuleList'],['settings','#settingsNavTree']]){
   await product.locator(section==='instructions'?'#libraryRootTab':'.rail-button[data-tab="'+section+'"]').click();
   await product.waitForFunction(selector=>document.querySelector(selector)?.getAttribute('role')==='tree',selector);
   assert.equal(await product.locator(selector).getAttribute('role'),'tree');
   const heading=product.locator(section==='settings'?'.settings-nav-heading':'.panel.active .global-nav .product-nav-heading').first();
   assert.equal((await heading.boundingBox()).height,41,'shared sidebar heading: '+section);
   if(section==='settings') continue; // View-local categories no longer have a Shell splitter.
   const geometry=await product.locator('.panel.active .global-nav').evaluate(n=>({edge:n.getBoundingClientRect().right,divider:n.nextElementSibling.getBoundingClientRect().left,width:n.nextElementSibling.getBoundingClientRect().width}));
   assert.equal(geometry.divider,geometry.edge,'no legacy grid gap beside navigation: '+section);assert.equal(geometry.width,4);
  }
  await product.locator('#libraryRootTab').click();
  await product.locator('#instructionsList [data-tree-key="skill:excel.sales_analysis"]').click();
  assert.equal(await product.locator('#skillTitle').textContent(),'Анализ продаж');
  assert.equal(await product.evaluate(()=>RNAssistantDockWorkspace.openTarget({view:'library-object',kind:'skill',id:'excel.sales_analysis',host:'other',revision:''})),false,'wrong host cannot retarget a library object');
  const original=await product.evaluate(()=>[state.selectedInstructionKind,state.selectedSkillIndex,state.selectedToolIndex,state.selectedPromptIndex]);
  await product.getByRole('button',{name:'Поиск в библиотеке',exact:true}).click();
  await product.locator('#skillSearchInput').fill('несуществующий объект');
  assert.deepEqual(await product.evaluate(()=>[state.selectedInstructionKind,state.selectedSkillIndex,state.selectedToolIndex,state.selectedPromptIndex]),original,'filter preserves the open editor');
  await product.keyboard.press('Escape');
  await product.locator('.rail-button[data-tab="settings"]').click();
  const rowStyle=(page,selector)=>page.locator(selector).first().evaluate(n=>{const s=getComputedStyle(n);return[s.minHeight,s.borderRadius,s.padding,s.fontSize,s.lineHeight];});
  assert.deepEqual(await rowStyle(product,'#settingsNavTree .navigation-tree-row'),await rowStyle(standalone,'#tree [data-tree-key=c] > .navigation-tree-row'));
  for(const theme of ['light','dark']){
   await standalone.evaluate(theme=>{document.documentElement.dataset.theme=theme;RNAssistantAccents.apply('blue',false);},theme);
   await product.evaluate(theme=>{applyUiTheme({UiTheme:theme},false);RNAssistantAccents.apply('blue',false);},theme);
   const selected=page=>page.locator('.navigation-tree-row.selected').first().evaluate(n=>{const s=getComputedStyle(n);return[s.backgroundColor,s.color];});
   assert.deepEqual(await selected(product),await selected(standalone),'shared selected state in '+theme);
  }
  assert.deepEqual(errors,[]);console.log('PASS shared UI: standalone Tree/Toolbar/Menu/Progress, keyboard/disposal, production section headings, shared geometry/themes; no Shell/bridge dependency');
 }finally{if(browser)await browser.close();server.kill();}
})().catch(e=>{console.error(e);process.exitCode=1});
