"use strict";
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

async function main() {
  let allowReplace = true;
  const scope = { TextEncoder, TextDecoder, AbortController, setTimeout, clearTimeout,
    confirm: () => allowReplace, crypto: { randomUUID: () => "12345678-1234-1234-1234-123456789abc" } };
  scope.window = scope;
  vm.runInNewContext(fs.readFileSync(path.join(__dirname, "../ui/draft-session.js"), "utf8"), scope);
  const drafts = scope.DataTemplatesDraftSession;
  assert.match(drafts.newFileName("Отчёт.json"), /^draft-\d{13}-12345678123412341234123456789abc-Отчёт\.draft$/u);

  const inspect = async input => {
    if (input === "{") throw Object.assign(new Error("Invalid JSON"), { code: "JSON_INVALID" });
    return { accepted: input };
  };
  const browser = drafts.create({ inspect });
  assert.equal(await browser.load("broken.json", "{"), true);
  assert.equal(browser.current().input, "{", "invalid JSON stays editable and saveable");
  assert.equal(browser.validTemplate(), null, "invalid JSON cannot run");
  browser.changeDraft('{"a":1}');
  browser.changeDraft('{"a":2}');
  await new Promise(resolve => setTimeout(resolve, 360));
  assert.equal(browser.validTemplate().input, '{"a":2}', "stale validation cannot replace the latest edit");
  browser.changeDraft("é".repeat(500001));
  assert.equal(browser.current(), null, "UTF-8 byte limit blocks saving oversized text");
  browser.changeDraft("{");
  await new Promise(resolve => setTimeout(resolve, 360));
  assert.equal(browser.current().input, "{", "invalid syntax remains a valid raw snapshot");
  browser.markSaved("{");
  assert.equal(browser.snapshot().draftDirty, false);
  browser.changeDraft("changed");
  allowReplace = false;
  assert.equal(await browser.load("other.json", "{}"), false, "unsaved work needs an explicit replace decision");
  assert.equal(browser.snapshot().draftText, "changed");
  browser.dispose();

  const native = drafts.create();
  assert.equal(await native.load("draft.json", "{"), true);
  assert.equal(native.snapshot().templateUnverified, true);
  assert.equal(native.current().input, "{");
  assert.equal(native.validTemplate(), null, "native raw editor does not claim Worker validation");
  native.dispose();
  console.log("PASS draft session: raw invalid JSON, latest inspection, byte limit, replacement guard and native scope");
}
main().catch(error => { console.error(error); process.exitCode = 1; });
