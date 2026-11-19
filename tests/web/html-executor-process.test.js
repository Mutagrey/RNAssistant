'use strict';
const assert = require('node:assert/strict');
const childProcess = require('node:child_process');
const fs = require('node:fs');
const http = require('node:http');
const os = require('node:os');
const path = require('node:path');

const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));

function chromeExecutable() {
  return [process.env.BROWSER_EXECUTABLE, '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome',
    '/Applications/Chromium.app/Contents/MacOS/Chromium', '/usr/bin/google-chrome', '/usr/bin/chromium',
    '/usr/bin/chromium-browser', 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe']
    .filter(Boolean).find(candidate => fs.existsSync(candidate));
}

function processRows() {
  return childProcess.execFileSync('ps', ['-eo', 'pid=,ppid=,time='], {encoding: 'utf8'}).trim().split(/\r?\n/)
    .map(line => /^\s*(\d+)\s+(\d+)\s+(\S+)\s*$/.exec(line)).filter(Boolean)
    .map(match => ({pid: Number(match[1]), parent: Number(match[2]), cpu: cpuMilliseconds(match[3])}));
}

function cpuMilliseconds(value) {
  const parts = value.split(':').map(Number);
  if (parts.some(part => !Number.isFinite(part))) return 0;
  while (parts.length < 3) parts.unshift(0);
  return ((parts[0] * 60 + parts[1]) * 60 + parts[2]) * 1000;
}

function descendants(rootPid, rows) {
  const children = new Map(), result = [];
  rows.forEach(row => {
    if (!children.has(row.parent)) children.set(row.parent, []);
    children.get(row.parent).push(row);
  });
  const stack = [rootPid];
  while (stack.length) {
    const pid = stack.pop(), list = children.get(pid) || [];
    list.forEach(row => { result.push(row); stack.push(row.pid); });
  }
  return result;
}

async function processTreeCpuPercent(rootPid, durationMs) {
  const before = descendants(rootPid, processRows()).reduce((sum, row) => sum + row.cpu, 0);
  const started = Date.now();
  await sleep(durationMs);
  const after = descendants(rootPid, processRows()).reduce((sum, row) => sum + row.cpu, 0);
  return (after - before) / Math.max(1, Date.now() - started) * 100;
}

function killTree(rootPid) {
  if (process.platform === 'win32') {
    childProcess.spawnSync('taskkill', ['/PID', String(rootPid), '/T', '/F'], {stdio: 'ignore'});
    return;
  }
  const rows = descendants(rootPid, processRows()).sort((a, b) => b.pid - a.pid);
  rows.forEach(row => { try { process.kill(row.pid, 'SIGKILL'); } catch (ignore) {} });
  try { process.kill(rootPid, 'SIGKILL'); } catch (ignore) {}
}

(async () => {
  const chrome = chromeExecutable();
  assert.ok(chrome, 'Chrome/Chromium executable is unavailable');
  const profile = fs.mkdtempSync(path.join(os.tmpdir(), 'rna-html-executor-process-'));
  const server = http.createServer((request, response) => {
    response.setHeader('Content-Type', 'text/html; charset=utf-8');
    response.end('<!doctype html><title>Runaway executor</title><script>setTimeout(function(){for(;;){}},150);</' + 'script>');
  });
  await new Promise((resolve, reject) => server.listen(0, '127.0.0.1', resolve).once('error', reject));
  const browser = childProcess.spawn(chrome, ['--headless=new', '--disable-gpu', '--no-sandbox', '--disable-extensions',
    '--disable-background-networking', '--no-first-run', `--user-data-dir=${profile}`,
    `http://127.0.0.1:${server.address().port}/`], {stdio: 'ignore'});
  try {
    let heartbeat = 0;
    const timer = setInterval(() => heartbeat++, 20);
    await sleep(900);
    const busy = await processTreeCpuPercent(browser.pid, 400);
    clearInterval(timer);
    assert.ok(busy > 30, 'runaway script loads its separate browser process (cpu=' + busy.toFixed(1) + '%)');
    assert.ok(heartbeat >= 10, 'separate executor leaves the test-process event loop responsive');
    killTree(browser.pid);
    await sleep(500);
    const remaining = descendants(browser.pid, processRows());
    assert.equal(remaining.length, 0, 'process-tree termination stops the isolated executor');
    const stopped = await processTreeCpuPercent(browser.pid, 300);
    assert.ok(stopped < 10, 'CPU load disappears after executor termination (cpu=' + stopped.toFixed(1) + '%)');
    console.log(JSON.stringify({status: 'pass', busy, stopped, heartbeat}));
  } finally {
    if (browser.exitCode === null) killTree(browser.pid);
    await new Promise(resolve => server.close(resolve));
    fs.rmSync(profile, {recursive: true, force: true});
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
