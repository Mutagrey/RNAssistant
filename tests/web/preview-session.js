'use strict';
const path = require('node:path');
const {spawn} = require('node:child_process');
const {chromium} = require('playwright');

// Each test owns a fresh browser and preview process, including on failure.
async function withPreviewPage(test) {
  const server = spawn('python3', ['-B', 'prototypes/serve_ui.py', '--port', '0'],
    {cwd:path.resolve(__dirname, '../..'), stdio:['ignore', 'pipe', 'pipe']});
  let browser;
  try {
    const address = await new Promise((resolve, reject) => {
      let output = '', errors = '';
      const timeout = setTimeout(() => fail(Error('Preview startup timed out: ' + errors)), 10000);
      function fail(error) { clearTimeout(timeout); reject(error); }
      server.once('error', fail);
      server.once('exit', code => fail(Error('Preview exited: ' + code + '\n' + errors)));
      server.stderr.on('data', chunk => { errors = (errors + chunk).slice(-4096); });
      server.stdout.on('data', chunk => {
        output = (output + chunk).slice(-4096);
        const match = /RNAssistant design: (http:\/\/127\.0\.0\.1:\d+\/)/.exec(output);
        if (match) { clearTimeout(timeout); resolve(match[1]); }
      });
    });
    browser = await chromium.launch({headless:true,
      ...(process.env.BROWSER_EXECUTABLE ? {executablePath:process.env.BROWSER_EXECUTABLE} : {})});
    const page = await browser.newPage(), errors = [];
    page.on('pageerror', error => errors.push(error.message));
    await page.goto(address + 'preview/web/?scenario=ready');
    // These scenarios use one visible Chat View; duplicate visible nodes fail strictly.
    const chatNode = name => page.locator('.conversation-panel:visible [data-chat-node="' + name + '"]');
    await test({page, errors, chatNode});
  } finally {
    try { if (browser) await browser.close(); }
    finally { server.kill(); }
  }
}

module.exports = {withPreviewPage};
