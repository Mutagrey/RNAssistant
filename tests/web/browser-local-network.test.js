'use strict';
// Real Chromium policy/interactivity evidence. This does not qualify WebView2/Office delivery.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const http = require('node:http');
const dgram = require('node:dgram');
const { chromium } = require('playwright');
const policy = fs.readFileSync('src/RNAssistant.Application/BrowserNetworkPolicy.cs', 'utf8');
function constant(name) {
  const source = policy.match(new RegExp('const string ' + name + ' = ([\\s\\S]*?);\\n'))[1];
  return [...source.matchAll(/"((?:[^"\\]|\\.)*)"/g)].map(match => JSON.parse('"' + match[1] + '"')).join('');
}
const listen = server => new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
(async () => {
  let browser, hits = 0, udpHits = 0;
  const external = http.createServer((request, response) => { hits++; response.end('unexpected'); });
  external.on('upgrade', (request, socket) => { hits++; socket.destroy(); });
  const udp = dgram.createSocket('udp4'); udp.on('message', () => udpHits++);
  const internal = http.createServer();
  try {
    await listen(external); await new Promise(resolve => udp.bind(0, '127.0.0.1', resolve));
    const remote = 'http://127.0.0.1:' + external.address().port;
    internal.on('request', (request, response) => {
      response.setHeader('Connection-Allowlist', constant('ConnectionAllowlist'));
      response.setHeader('Content-Security-Policy', constant('ContentSecurityPolicy'));
      response.setHeader('X-Content-Type-Options', 'nosniff');
      response.setHeader('Content-Type', request.url.endsWith('.js') ? 'text/javascript' : request.url === '/data.json' ? 'application/json; charset=utf-8' : 'text/html; charset=utf-8');
      const attempt = `fetch('${remote}/worker').then(()=> 'leak',()=> 'blocked')`;
      response.end(request.url === '/worker.js' ? `onmessage=async()=>postMessage(await ${attempt})` :
        request.url === '/shared.js' ? `onconnect=e=>{e.ports[0].onmessage=async()=>e.ports[0].postMessage(await ${attempt});e.ports[0].start()}` :
        request.url === '/service.js' ? `oninstall=e=>e.waitUntil(skipWaiting());onactivate=e=>e.waitUntil(clients.claim());onmessage=e=>e.waitUntil(${attempt}.then(value=>e.ports[0].postMessage(value)))` :
        request.url === '/data.json' ? '{"local":true}' :
        '<!doctype html><html><body><label>Имя<input></label><button onclick="this.textContent=\'Готово\';window.trusted=event.isTrusted">Изменить</button></body></html>');
    });
    await listen(internal);
    browser = await chromium.launch({ headless: true, ...(process.env.BROWSER_EXECUTABLE ? { executablePath: process.env.BROWSER_EXECUTABLE } : {}) });
    const page = await browser.newPage(); page.setDefaultTimeout(8000);
    await page.goto('http://127.0.0.1:' + internal.address().port);
    await page.getByLabel('Имя').fill('Локальные данные'); await page.getByRole('button').click();
    assert.equal(await page.getByRole('button').textContent(), 'Готово');
    assert.equal(await page.evaluate(() => trusted), true, 'native browser input reaches the interactive page');
    assert.equal(await page.getByLabel('Имя').inputValue(), 'Локальные данные');
    const result = await page.evaluate(async ({ remote, udpPort }) => {
      const local = await (await fetch('/data.json')).json();
      const blocked = await fetch(remote + '/fetch').then(() => false, () => true);
      const worker = new Worker('/worker.js');
      const dedicated = await new Promise(resolve => { worker.onmessage = event => resolve(event.data); worker.postMessage('test'); }); worker.terminate();
      const shared = new SharedWorker('/shared.js');
      const sharedResult = await new Promise(resolve => { shared.port.onmessage = event => resolve(event.data); shared.port.start(); shared.port.postMessage('test'); }); shared.port.close();
      const registration = await navigator.serviceWorker.register('/service.js'); await navigator.serviceWorker.ready;
      const channel = new MessageChannel();
      const service = await new Promise(resolve => { channel.port1.onmessage = event => resolve(event.data); registration.active.postMessage('test', [channel.port2]); });
      await registration.unregister(); channel.port1.close();
      const blob = new Blob([`onmessage=async()=>{try{await fetch('${remote}/blob');postMessage('leak')}catch{postMessage('blocked')}}`], { type: 'text/javascript' });
      const blobUrl = URL.createObjectURL(blob), blobWorker = new Worker(blobUrl);
      const blobResult = await new Promise(resolve => { blobWorker.onmessage = event => resolve(event.data); blobWorker.postMessage('test'); }); blobWorker.terminate(); URL.revokeObjectURL(blobUrl);
      const websocket = await new Promise(resolve => { const socket = new WebSocket(remote.replace('http:', 'ws:') + '/socket'); socket.onerror = () => resolve('blocked'); socket.onopen = () => { socket.close(); resolve('leak'); }; });
      const pc = new RTCPeerConnection({ iceServers: [{ urls: 'stun:127.0.0.1:' + udpPort }] }); let candidates = 0;
      pc.onicecandidate = event => { if (event.candidate) candidates++; }; pc.createDataChannel('probe');
      try { await pc.setLocalDescription(await pc.createOffer()); } catch { }
      await new Promise(resolve => setTimeout(resolve, 400)); pc.close();
      return { local, blocked, dedicated, shared: sharedResult, service, blob: blobResult, websocket, candidates };
    }, { remote, udpPort: udp.address().port });
    assert.deepEqual(result, { local: { local: true }, blocked: true, dedicated: 'blocked', shared: 'blocked', service: 'blocked', blob: 'blocked', websocket: 'blocked', candidates: 0 });
    assert.equal(hits, 0, 'no external HTTP/WebSocket request reaches the probe server');
    assert.equal(udpHits, 0, 'no WebRTC UDP packet reaches the probe server');
    console.log('PASS local Browser: interactive DOM and internal bytes; external page/worker/socket/WebRTC requests denied');
  } finally { if (browser) await browser.close(); internal.close(); external.close(); udp.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
