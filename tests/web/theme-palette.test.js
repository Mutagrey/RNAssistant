"use strict";

const assert = require("node:assert/strict");
const path = require("node:path");
const { spawn } = require("node:child_process");
const { chromium } = require("playwright");

const root = path.resolve(__dirname, "../..");
const accents = ["green", "blue", "turquoise", "lime", "yellow", "orange", "pink", "magenta", "violet", "white"];

async function serve() {
  const process = spawn("python3", ["-B", "prototypes/serve_ui.py", "--port", "0"], { cwd: root });
  const address = await new Promise((resolve, reject) => {
    let output = "";
    process.stdout.on("data", chunk => {
      output += chunk.toString();
      const match = /RNAssistant design: (http:\/\/127\.0\.0\.1:\d+\/)\n/.exec(output);
      if (match) resolve(match[1]);
    });
    process.once("exit", code => reject(new Error("Preview server exited: " + code)));
  });
  return { process, address };
}

(async () => {
  const server = await serve();
  let browser;
  try {
    browser = await chromium.launch({ headless: true,
      ...(process.env.BROWSER_EXECUTABLE ? { executablePath: process.env.BROWSER_EXECUTABLE } : {}) });
    const page = await browser.newPage({ viewport: { width: 1280, height: 800 } });
    await page.goto(server.address + "preview/web/?scenario=ready");
    await page.addStyleTag({ content: "*, *::before, *::after { transition: none !important; animation: none !important; }" });
    await page.locator('[data-section="settings"]').click();
    await page.getByText("Интерфейс", { exact: true }).first().click();
    await page.evaluate(() => {
      const probe = document.createElement("button");
      probe.id = "themePaletteProbe";
      probe.className = "button primary";
      probe.textContent = "Проверка темы";
      probe.style.cssText = "position:fixed;right:12px;bottom:12px;z-index:100";
      document.body.appendChild(probe);
    });
    await page.waitForFunction(() => getComputedStyle(document.querySelector('#themePaletteProbe')).backgroundColor !== 'rgb(239, 239, 239)');

    const read = () => page.evaluate(() => {
      const color = selector => getComputedStyle(document.querySelector(selector)).backgroundColor;
      return {
        theme: document.documentElement.dataset.theme,
        accent: document.documentElement.dataset.accent,
        canvas: getComputedStyle(document.documentElement).getPropertyValue('--canvas').trim(),
        surface: getComputedStyle(document.documentElement).getPropertyValue('--surface').trim(),
        primary: color("#themePaletteProbe"),
        selected: color("#settingsNavTree .navigation-tree-row.selected"),
        sidebar: color("#tab-settings .settings-nav")
      };
    });
    const results = {};
    for (const theme of ["light", "dark"]) {
      await page.locator("label.theme-choice:has(input[value='" + theme + "'])").click();
      results[theme] = {};
      for (const accent of accents) {
        await page.locator("#uiAccentChoices label.accent-choice:has(input[value='" + accent + "'])").click();
        const value = await read();
        assert.equal(value.theme, theme);
        assert.equal(value.accent, accent);
        await page.locator("#themePaletteProbe").hover();
        value.primaryHover = await page.locator("#themePaletteProbe").evaluate(element => getComputedStyle(element).backgroundColor);
        await page.locator(".section-rail .rail-button[data-section='artifacts']").hover();
        value.railHover = await page.locator(".section-rail .rail-button[data-section='artifacts']")
          .evaluate(element => getComputedStyle(element).backgroundColor);
        assert.notEqual(value.primary, value.primaryHover, theme + "/" + accent + ": primary hover");
        results[theme][accent] = value;
      }
      const green = results[theme].green;
      for (const accent of accents.slice(1)) {
        const value = results[theme][accent];
        for (const property of ["primary", "primaryHover", "selected", "railHover"])
          assert.notEqual(value[property], green[property], theme + "/" + accent + ": " + property);
        for (const property of ["canvas", "surface", "sidebar"])
          assert.equal(value[property], green[property], theme + "/" + accent + ": neutral " + property);
      }
    }
    assert.notEqual(results.light.green.sidebar, results.dark.green.sidebar);
    assert.notEqual(results.light.green.primary, results.dark.green.primary);
    await page.locator(".section-rail [data-plugin-id='data-templates']").click();
    const embedded = page.frameLocator("#pluginPreviewMount iframe").locator("html");
    await embedded.waitFor();
    await page.waitForFunction(() => {
      const frame = document.querySelector("#pluginPreviewMount iframe");
      return frame && frame.contentDocument && frame.contentDocument.documentElement.dataset.hostTheme === "dark";
    });
    const before = await embedded.evaluate(element => ({
      theme: element.dataset.hostTheme,
      accent: getComputedStyle(element).getPropertyValue("--accent").trim(),
      background: getComputedStyle(element).backgroundColor
    }));
    assert.equal(before.accent, "#FFFFFF");
    await page.evaluate(() => {
      applyUiTheme({ UiTheme: "light" }, false);
      RNAssistantAccents.apply("turquoise", false);
    });
    await page.waitForFunction(() => {
      const frame = document.querySelector("#pluginPreviewMount iframe");
      return frame && frame.contentDocument && frame.contentDocument.documentElement.dataset.hostTheme === "light";
    });
    const after = await embedded.evaluate(element => ({
      theme: element.dataset.hostTheme,
      accent: getComputedStyle(element).getPropertyValue("--accent").trim(),
      background: getComputedStyle(element).backgroundColor
    }));
    assert.equal(after.accent, "#007AAE");
    assert.notEqual(before.background, after.background);
    console.log("PASS theme palette: neutral surfaces, 10 accent controls and embedded Data Templates");
  } finally {
    if (browser) await browser.close();
    server.process.kill();
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
