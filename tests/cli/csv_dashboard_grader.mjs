// Independent, dependency-free functional grader for csv_dashboard_task.md.
// Run: node tests/cli/csv_dashboard_grader.mjs <workspace> [--require-threshold]
// It reads a bounded immutable snapshot and never writes to the workspace.
import { createHash } from 'node:crypto';
import { spawn } from 'node:child_process';
import { createServer } from 'node:http';
import { mkdir, mkdtemp, readFile, readdir, rm, stat, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { basename, extname, join, relative, resolve } from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';

const root = process.argv[2] ? resolve(process.argv[2]) : null;
const requireThreshold = process.argv.includes('--require-threshold');
const allowed = new Set(['.html', '.htm', '.css', '.js', '.mjs', '.json', '.svg', '.csv', '.txt']);
const mime = { '.html': 'text/html', '.htm': 'text/html', '.css': 'text/css',
  '.js': 'text/javascript', '.mjs': 'text/javascript', '.svg': 'image/svg+xml',
  '.json': 'application/json', '.csv': 'text/csv' };
const fixture = 'name,category,amount\nAlpha,A,2\nBeta,B,10\nGamma,A,100\nDelta,B,3\nEpsilon,A,20\n';
const assertions = [];
const errors = [];
const externalRequests = [];
const check = (id, passed, detail = '') => assertions.push({ id, passed: !!passed, detail });
const sha = bytes => createHash('sha256').update(bytes).digest('hex');
const amount = value => Number(String(value).replace(/[^\d.\-]/g, ''));
const safeUrl = value => { try { const url = new URL(value); return url.origin + url.pathname; }
  catch { return 'unknown URL'; } };
const scrubUrls = value => String(value).replace(/https?:\/\/[^\s'"<>]+/gi, safeUrl);
const addError = value => { if (errors.length < 16) errors.push(scrubUrls(value).slice(0, 700)); };
const addExternal = value => { if (externalRequests.length < 16) externalRequests.push(safeUrl(value)); };

async function snapshotWorkspace(directory) {
  const files = new Map();
  let size = 0;
  async function visit(folder) {
    for (const entry of await readdir(folder, { withFileTypes: true })) {
      if (entry.name === '.rnassistant' || entry.name === '.git' || entry.name === '.codex' ||
          entry.name.startsWith('.env') || entry.name.endsWith('.pem') || entry.name.endsWith('.key')) continue;
      const full = join(folder, entry.name);
      if (entry.isSymbolicLink()) throw new Error('Linked workspace entry: ' + relative(directory, full));
      if (entry.isDirectory()) { await visit(full); continue; }
      if (!entry.isFile()) continue;
      const name = relative(directory, full).split('\\').join('/');
      if (!allowed.has(extname(name).toLowerCase())) continue;
      if (files.size >= 32) throw new Error('Snapshot exceeds 32 files.');
      const info = await stat(full);
      size += info.size;
      if (size > 4 * 1024 * 1024) throw new Error('Snapshot exceeds four MiB.');
      files.set(name, await readFile(full));
    }
  }
  await visit(directory);
  const manifest = [...files].sort(([a], [b]) => a.localeCompare(b))
    .map(([path, bytes]) => path + ' ' + sha(bytes)).join('\n');
  return { files, hash: sha(Buffer.from(manifest)) };
}

function browserPath() {
  const configured = process.env.RNA_BROWSER_EXECUTABLE;
  if (configured) return configured;
  return process.platform === 'darwin'
    ? '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome'
    : process.platform === 'win32'
      ? join(process.env.PROGRAMFILES || 'C:\\Program Files', 'Google', 'Chrome', 'Application', 'chrome.exe')
      : '/usr/bin/chromium';
}

function parseCsv(text) {
  const rows = [];
  let row = [], value = '', quoted = false;
  const input = text.replace(/^\uFEFF/, '');
  for (let index = 0; index < input.length; index++) {
    const ch = input[index];
    if (quoted && ch === '"' && input[index + 1] === '"') { value += '"'; index++; }
    else if (ch === '"') quoted = !quoted;
    else if (!quoted && ch === ',') { row.push(value); value = ''; }
    else if (!quoted && (ch === '\n' || ch === '\r')) {
      if (ch === '\r' && input[index + 1] === '\n') index++;
      row.push(value); value = '';
      if (row.some(cell => cell !== '')) rows.push(row);
      row = [];
    } else value += ch;
  }
  if (value !== '' || row.length) { row.push(value); rows.push(row); }
  return rows;
}

class DevTools {
  constructor(url, origin) {
    this.url = url;
    this.origin = origin;
    this.pending = new Map();
    this.nextId = 0;
    this.loaded = false;
  }
  async connect() {
    this.socket = new WebSocket(this.url);
    await new Promise((resolveConnection, rejectConnection) => {
      this.socket.addEventListener('open', resolveConnection, { once: true });
      this.socket.addEventListener('error', rejectConnection, { once: true });
    });
    this.socket.addEventListener('message', event => {
      const data = JSON.parse(event.data);
      if (data.id) {
        const item = this.pending.get(data.id);
        if (!item) return;
        this.pending.delete(data.id);
        clearTimeout(item.timeout);
        data.error ? item.reject(new Error(data.error.message)) : item.resolve(data.result || {});
      } else this.observe(data);
    });
  }
  observe(event) {
    const args = event.params || {};
    if (event.method === 'Page.loadEventFired') this.loaded = true;
    if (event.method === 'Runtime.exceptionThrown')
      addError('JavaScript: ' + (args.exceptionDetails?.exception?.description || args.exceptionDetails?.text));
    if (event.method === 'Runtime.consoleAPICalled' && args.type === 'error')
      addError('Console: ' + args.args.map(item => item.value || item.description || '').join(' '));
    if (event.method === 'Log.entryAdded' && args.entry?.level === 'error') {
      const raw = args.entry.text || '';
      const link = raw.match(/https?:\/\/[^\s'"<>]+/i)?.[0];
      if (link && /Content Security Policy|Refused to connect/i.test(raw)) {
        try { if (new URL(link).origin !== this.origin) addExternal(link); }
        catch { /* Invalid reported URL is still a browser error. */ }
      }
      addError('Browser: ' + raw);
    }
    if (event.method === 'Network.loadingFailed' && args.errorText !== 'net::ERR_ABORTED')
      addError('Asset: ' + args.errorText);
    if (event.method === 'Network.responseReceived' && args.response?.status >= 400)
      addError('HTTP ' + args.response.status + ': ' + safeUrl(args.response.url));
    if (event.method === 'Network.requestWillBeSent') {
      const url = args.request?.url || '';
      if (/^https?:/i.test(url)) {
        try { if (new URL(url).origin !== this.origin) addExternal(url); }
        catch { addError('Browser reported an invalid network URL.'); }
      }
    }
  }
  command(method, params = {}) {
    const id = ++this.nextId;
    return new Promise((resolveCommand, rejectCommand) => {
      const timeout = setTimeout(() => {
        this.pending.delete(id);
        rejectCommand(new Error('CDP timeout: ' + method));
      }, 10000);
      this.pending.set(id, { resolve: resolveCommand, reject: rejectCommand, timeout });
      this.socket.send(JSON.stringify({ id, method, params }));
    });
  }
  async evaluate(expression) {
    const reply = await this.command('Runtime.evaluate', { expression,
      returnByValue: true, awaitPromise: true });
    if (reply.exceptionDetails) throw new Error(reply.exceptionDetails.text || 'Page evaluation failed.');
    return reply.result?.value;
  }
  async until(expression, label, milliseconds = 5000) {
    const end = Date.now() + milliseconds;
    while (Date.now() < end) {
      if (await this.evaluate(expression)) return;
      await delay(100);
    }
    throw new Error('Timed out waiting for ' + label);
  }
  close() {
    this.socket?.close();
    for (const item of this.pending.values()) {
      clearTimeout(item.timeout);
      item.reject(new Error('Browser connection closed.'));
    }
    this.pending.clear();
  }
}

async function upload(devtools, path) {
  const doc = await devtools.command('DOM.getDocument');
  const target = await devtools.command('DOM.querySelector',
    { nodeId: doc.root.nodeId, selector: '#csv-file' });
  if (!target.nodeId) throw new Error('Upload control #csv-file is missing.');
  await devtools.command('DOM.setFileInputFiles', { nodeId: target.nodeId, files: [path] });
}

async function run() {
  if (!root) throw new Error('Usage: node tests/cli/csv_dashboard_grader.mjs <workspace> [--require-threshold]');
  const { files, hash } = await snapshotWorkspace(root);
  check('physical_files', ['index.html', 'styles.css', 'app.js'].every(path => files.has(path)),
    [...files.keys()].join(', '));
  if (!assertions[0].passed) return { status: 'failed', snapshotSha256: hash,
    checkedFiles: [...files.keys()], assertions, errors, externalRequests };
  const temp = await mkdtemp(join(tmpdir(), 'rna-csv-grader-'));
  const downloads = join(temp, 'downloads');
  await mkdir(downloads);
  const input = join(temp, 'fixture.csv');
  await writeFile(input, fixture);
  const server = createServer((request, response) => {
    let path;
    try { path = decodeURIComponent(new URL(request.url, 'http://127.0.0.1/').pathname).slice(1); }
    catch { response.writeHead(400).end(); return; }
    if (path === 'favicon.ico') { response.writeHead(204).end(); return; }
    const bytes = files.get(path);
    if (request.method !== 'GET' || !bytes) { response.writeHead(404).end(); return; }
    response.writeHead(200, { 'Content-Type': mime[extname(path).toLowerCase()] || 'text/plain',
      'Cache-Control': 'no-store', 'X-Content-Type-Options': 'nosniff',
      'Content-Security-Policy': "default-src 'self' data: blob:; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; connect-src 'self'; img-src 'self' data: blob:; object-src 'none'; base-uri 'none'" });
    response.end(bytes);
  });
  await new Promise(done => server.listen(0, '127.0.0.1', done));
  const origin = 'http://127.0.0.1:' + server.address().port;
  const browser = browserPath();
  let process = null, devtools = null;
  try {
    try { await stat(browser); }
    catch { return { status: 'not-run', browser, snapshotSha256: hash,
      checkedFiles: [...files.keys()], assertions, errors: ['Chromium executable is unavailable.'], externalRequests }; }
    process = spawn(browser, ['--headless=new', '--disable-gpu', '--no-first-run',
      '--no-default-browser-check', '--disable-background-networking', '--disable-extensions',
      '--disable-sync', '--remote-debugging-port=0', '--user-data-dir=' + join(temp, 'profile'),
      '--proxy-server=http://127.0.0.1:9', 'about:blank'], { stdio: 'ignore' });
    const active = join(temp, 'profile', 'DevToolsActivePort');
    let port = null;
    for (let index = 0; index < 100; index++) {
      try { port = Number((await readFile(active, 'utf8')).split('\n')[0]); break; }
      catch { if (process.exitCode != null) throw new Error('Chromium exited before DevTools was ready.'); }
      await delay(100);
    }
    if (!port) throw new Error('Chromium DevTools did not become ready.');
    const targets = await (await fetch('http://127.0.0.1:' + port + '/json/list')).json();
    const page = targets.find(target => target.type === 'page');
    if (!page) throw new Error('Chromium page target is unavailable.');
    devtools = new DevTools(page.webSocketDebuggerUrl, origin);
    await devtools.connect();
    for (const domain of ['Page.enable', 'Runtime.enable', 'Log.enable', 'Network.enable', 'DOM.enable'])
      await devtools.command(domain);
    await devtools.command('Network.setBypassServiceWorker', { bypass: true });
    await devtools.command('Browser.setDownloadBehavior',
      { behavior: 'allow', downloadPath: downloads, eventsEnabled: true });
    await devtools.command('Page.navigate', { url: origin + '/index.html' });
    await devtools.until('document.readyState === "complete"', 'document load');
    await upload(devtools, input);
    await devtools.until('document.querySelectorAll("#rows tr").length === 5', 'five CSV rows');
    let rows = await devtools.evaluate('Array.from(document.querySelectorAll("#rows tr"), tr => Array.from(tr.cells, cell => cell.textContent.trim()))');
    check('csv_loaded', rows.length === 5 && rows.some(row => row[0] === 'Gamma' && amount(row[2]) === 100));
    await devtools.evaluate('document.querySelector("#category-filter").value="A"; document.querySelector("#category-filter").dispatchEvent(new Event("change",{bubbles:true}));');
    await devtools.until('document.querySelectorAll("#rows tr").length === 3', 'category filter');
    rows = await devtools.evaluate('Array.from(document.querySelectorAll("#rows tr"), tr => Array.from(tr.cells, cell => cell.textContent.trim()))');
    check('category_filter', rows.length === 3 && rows.every(row => row[1] === 'A'));
    check('aggregate', Number((await devtools.evaluate('document.querySelector("#total")?.textContent || ""')).replace(/[^\d.\-]/g, '')) === 122);
    await devtools.evaluate('document.querySelector("#sort-amount").click()');
    rows = await devtools.evaluate('Array.from(document.querySelectorAll("#rows tr"), tr => Array.from(tr.cells, cell => cell.textContent.trim()))');
    const amounts = rows.map(row => amount(row[2]));
    check('numeric_sort', amounts.join(',') === '2,20,100' || amounts.join(',') === '100,20,2', amounts.join(','));
    const chart = await devtools.evaluate('Array.from(document.querySelectorAll("#chart rect"), rect => ({width:Number(rect.getAttribute("width")), height:Number(rect.getAttribute("height"))})).filter(rect => rect.width > 0 && rect.width < document.querySelector("#chart").getBoundingClientRect().width/2).map(rect => rect.height)');
    check('chart', chart.length === 3 && chart.every(height => height > 0), JSON.stringify(chart));
    await devtools.evaluate('document.querySelector("#export").click()');
    let exported = null;
    for (let index = 0; index < 50; index++) {
      const names = await readdir(downloads);
      const ready = names.find(name => name.endsWith('.csv'));
      if (ready) { exported = await readFile(join(downloads, ready), 'utf8'); break; }
      await delay(100);
    }
    const exportedRows = exported == null ? [] : parseCsv(exported);
    const exportNames = exportedRows.slice(1).map(row => row[0]).sort();
    check('filtered_export', exportNames.join(',') === 'Alpha,Epsilon,Gamma', exportNames.join(','));
    if (requireThreshold) {
      await devtools.evaluate('document.querySelector("#min-amount").value="10"; document.querySelector("#min-amount").dispatchEvent(new Event("input",{bubbles:true}));');
      await devtools.until('document.querySelectorAll("#rows tr").length === 2', 'minimum amount filter');
      const thresholdRows = await devtools.evaluate('Array.from(document.querySelectorAll("#rows tr"), tr => Array.from(tr.cells, cell => cell.textContent.trim()))');
      check('new_threshold', thresholdRows.every(row => amount(row[2]) >= 10) && thresholdRows.length === 2);
      check('threshold_aggregate', Number((await devtools.evaluate('document.querySelector("#total")?.textContent || ""')).replace(/[^\d.\-]/g, '')) === 120);
    }
    const invalid = join(temp, 'invalid.csv');
    await writeFile(invalid, 'name,category,amount\nBad,A,not-a-number\n');
    await upload(devtools, invalid);
    try { await devtools.until('/invalid|error|ошиб|некоррект/i.test(document.querySelector("#status")?.textContent || "")', 'invalid CSV message', 2000); }
    catch { /* Record the failed assertion below and continue with the empty fixture. */ }
    check('invalid_csv', /invalid|error|ошиб|некоррект/i.test(await devtools.evaluate('document.querySelector("#status").textContent')));
    const empty = join(temp, 'empty.csv');
    await writeFile(empty, '');
    await upload(devtools, empty);
    try { await devtools.until('/empty|no data|пуст/i.test(document.querySelector("#status")?.textContent || "")', 'empty CSV message', 2000); }
    catch { /* Record the failed assertion below. */ }
    check('empty_csv', /empty|no data|пуст/i.test(await devtools.evaluate('document.querySelector("#status").textContent')));
    check('browser_errors', errors.length === 0, errors.slice(0, 4).join('; '));
    check('offline', externalRequests.length === 0, externalRequests.slice(0, 4).join('; '));
  } catch (error) {
    addError(error.message);
  } finally {
    devtools?.close();
    if (process && process.exitCode == null) {
      process.kill();
      await Promise.race([new Promise(done => process.once('exit', done)), delay(2000)]);
    }
    await new Promise(done => server.close(done));
    await rm(temp, { recursive: true, force: true, maxRetries: 5, retryDelay: 100 });
  }
  return { status: assertions.length >= (requireThreshold ? 13 : 11) &&
      assertions.every(item => item.passed) && errors.length === 0 ? 'passed' : 'failed',
    browser: basename(browser), snapshotSha256: hash,
    checkedFiles: [...files.keys()].sort(), assertions, errors, externalRequests };
}

try {
  const report = await run();
  console.log(JSON.stringify(report));
  process.exitCode = report.status === 'passed' ? 0 : report.status === 'not-run' ? 4 : 5;
} catch (error) {
  console.log(JSON.stringify({ status: 'failed', assertions, errors: [scrubUrls(error.message)] }));
  process.exitCode = 5;
}
