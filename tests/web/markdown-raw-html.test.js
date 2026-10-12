"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const root = path.join(__dirname, "../..");
const marked = require(path.join(root, "web/js/vendor/marked.min.js"));
let sanitized = 0;
const context = { marked, DOMPurify: { sanitize(html) { sanitized += 1; return html; } } };
context.window = context;
vm.runInNewContext(fs.readFileSync(path.join(root, "web/js/app-markdown.js"), "utf8"), context);

const page = "<!doctype html>\n<html>\n<body><h1>Тест</h1></body>\n</html>";
const rendered = context.markdown(page);
assert.match(rendered, /markdown-raw-html/);
assert.match(rendered, /&lt;!doctype html&gt;/);
assert.match(rendered, /&lt;h1&gt;Тест&lt;\/h1&gt;/);
assert.doesNotMatch(rendered, /<html>|<body>|<h1>/);
assert.match(context.markdown("Текст <b>жирный</b>"), /&lt;b&gt;жирный&lt;\/b&gt;/);
assert.match(context.markdown("```html\n<div>код</div>\n```"), /<code class="language-html">&lt;div&gt;код&lt;\/div&gt;/);
assert.match(context.markdown("**Markdown**"), /<strong>Markdown<\/strong>/);
assert.equal(sanitized, 4);
console.log("PASS raw HTML stays text in chat Markdown");
