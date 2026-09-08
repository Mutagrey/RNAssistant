const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const context = { window: {} }; vm.createContext(context);
vm.runInContext(fs.readFileSync('web/js/app-text-diff.js', 'utf8'), context);
const format = context.window.RNAssistantTextDiff.format;

test('separated changes do not count unchanged middle lines', () => {
  const before = ['a', 'old', ...Array.from({length: 30}, (_, i) => 'line' + i), 'tail-old', 'z'].join('\n');
  const diff = format(before, before.replace('old', 'new').replace('tail-old', 'tail-new'));
  assert.equal(diff.added, 2); assert.equal(diff.removed, 2);
  assert.ok(diff.lines.some(line => line.type === 'note'));
  assert.equal(diff.lines.find(line => line.text === 'new').newLine, 2);
});
test('empty, newline, whitespace and source bounds', () => {
  assert.equal(format('', '').added, 0);
  assert.equal(format('', 'a\n').added, 1);
  assert.equal(format('a\n', '').removed, 1);
  assert.equal(format('a\r\n', 'a\n').added, 0);
  assert.equal(format('a', 'a\n').removed, 1);
  assert.equal(format('a', ' a').added, 1);
  assert.equal(format('', 'a\n'.repeat(10000)).added, 10000);
  assert.equal(format('x'.repeat(520000), 'y').added, null);
});
test('counts agree with independent LCS oracle', () => {
  let seed = 31;
  function random() { seed = (Math.imul(seed, 1664525) + 1013904223) >>> 0; return seed; }
  for (let n = 0; n < 300; n++) {
    const a = Array.from({length: random() % 12}, () => String(random() % 4));
    const b = Array.from({length: random() % 12}, () => String(random() % 4));
    const dp = Array.from({length: a.length + 1}, () => Array(b.length + 1).fill(0));
    for (let i = 1; i <= a.length; i++) for (let j = 1; j <= b.length; j++)
      dp[i][j] = a[i-1] === b[j-1] ? dp[i-1][j-1] + 1 : Math.max(dp[i-1][j], dp[i][j-1]);
    const diff = format(a.map(x => x + '\n').join(''), b.map(x => x + '\n').join(''));
    assert.equal(diff.added, b.length - dp[a.length][b.length]);
    assert.equal(diff.removed, a.length - dp[a.length][b.length]);
  }
});
