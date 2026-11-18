'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const crypto = require('node:crypto');
const root = path.resolve(__dirname, '../..');
const bundlePath = path.join(root, 'web/js/vendor/powerquery-parser-5.0.0.js');
const bundle = fs.readFileSync(bundlePath, 'utf8');
const wrapper = fs.readFileSync(path.join(root, 'web/powerquery/analyze.js'), 'utf8');
const hash = source => crypto.createHash('sha256').update(source, 'utf8').digest('hex');
function context(loadParser = true) {
  // Only a browser-standard clock is supplied. No Node globals, DOM, network,
  // Office bridge or model-supplied executable JavaScript is present.
  const value = vm.createContext({ performance: { now: () => 0 } });
  if (loadParser) vm.runInContext(bundle, value, { timeout: 2000 });
  vm.runInContext(wrapper, value, { timeout: 2000 });
  return value;
}
function request(source) { return { requestId: 'parser_test_1', source, sourceSha256: hash(source) }; }
function plain(value) { return JSON.parse(JSON.stringify(value)); }
async function analyze(ctx, source) {
  const input = request(source), result = plain(await ctx.RNPowerQueryAnalysis.analyze(input));
  assert.equal(result.requestId, input.requestId);
  assert.equal(result.sourceSha256, input.sourceSha256);
  assert.equal(result.parserVersion, '5.0.0');
  assert.ok(Buffer.byteLength(JSON.stringify(result), 'utf8') <= 32768);
  assert.ok(result.diagnostics.length <= 8);
  for (const item of result.diagnostics) {
    assert.ok(item.message.length > 0 && item.message.length <= 2048);
    assert.ok(Number.isInteger(item.startOffset) && item.startOffset >= 0);
    assert.ok(Number.isInteger(item.endOffset) && item.endOffset >= item.startOffset && item.endOffset <= source.length);
  }
  return result;
}

(async function () {
  const ctx = context();
  assert.equal(vm.runInContext('typeof require+":"+typeof process+":"+typeof module+":"+typeof Buffer+":"+typeof fetch+":"+typeof window', ctx),
    'undefined:undefined:undefined:undefined:undefined:undefined');
  const valid = [
    'let x = 1 in x',
    'let #"Продажи 2024" = #table({"Код","Сумма"}, {{1,10},{2,20}}) in #"Продажи 2024"',
    'let f = (x as number, optional y as nullable number) as number => x + (y ?? 0) in f(1)',
    'let x = {1..3}, r = [a=1, b = "a""b#(lf)c"] in r[a]',
    'each [Amount] > 0',
    'let f=(n)=> if n=0 then 1 else n * @f(n - 1) in f(4)',
    'let a = try error "test" otherwise 1 in a',
    '42 meta [IsParameterQuery=true, Type="Number", IsParameterQueryRequired=true]',
    'let x = MissingQuery, y = Web.Contents("https://example.invalid") in Expression.Evaluate("1+1")'
  ];
  for (const source of valid) {
    const result = await analyze(ctx, source);
    assert.equal(result.syntax, 'passed', source);
    assert.deepEqual(result.diagnostics, []);
  }
  console.log('PASS Power Query parser: real upstream expression grammar, Unicode and no execution/source access');

  for (const source of ['', 'let x = in x', 'let x = "unfinished', 'let x = #"unfinished',
    '/* unfinished', 'let x = 1 in $', 'let x = 1 in x extra', 'section X; shared y = 1;']) {
    const result = await analyze(ctx, source);
    assert.equal(result.syntax, 'failed', source);
    assert.equal(result.diagnostics.length, 1, 'Upstream reports its first error');
  }
  const source = 'let x = "e\u0301😀"\r\nin x extra';
  const invalid = await analyze(ctx, source);
  assert.equal(invalid.diagnostics[0].startOffset, source.indexOf('extra'));
  assert.equal(invalid.diagnostics[0].endOffset, source.length);
  const eof = 'let x = "😀" in';
  const atEof = await analyze(ctx, eof);
  assert.equal(atEof.diagnostics[0].startOffset, eof.length);
  assert.equal(atEof.diagnostics[0].endOffset, eof.length);
  console.log('PASS Power Query parser: lexical/parse failures, expression-only scope and UTF-16 offsets');

  const exactBound = '1' + ' '.repeat(262143);
  assert.equal((await analyze(ctx, exactBound)).syntax, 'passed');
  const tooLarge = await analyze(ctx, exactBound + ' ');
  assert.equal(tooLarge.syntax, 'not_checked');
  assert.equal(tooLarge.reason, 'parser_source_limit');
  const missing = await analyze(context(false), '1');
  assert.equal(missing.syntax, 'not_checked');
  assert.equal(missing.reason, 'parser_unavailable');
  const broken = context();
  broken.RNPowerQueryParser.TaskUtils.tryLexParse = async () => { throw new RangeError('Simulated runtime budget failure'); };
  assert.equal((await analyze(broken, '1')).syntax, 'not_checked');
  const common = context();
  common.RNPowerQueryParser.TaskUtils.tryLexParse = async () => ({ stage: 'Parse', resultKind: 'Error', isCommonError: true,
    error: new Error('Simulated cancellation; not an upstream ParseError') });
  assert.equal((await analyze(common, '1')).syntax, 'not_checked');
  await assert.rejects(ctx.RNPowerQueryAnalysis.analyze({ ...request('1'), sourceSha256: 'bad' }), /Invalid/);
  await assert.rejects(ctx.RNPowerQueryAnalysis.analyze({ ...request('1'), extra: true }), /Invalid/);
  console.log('PASS Power Query parser: exact bounds and unavailable/internal failures never claim invalid syntax');

  const inventory = JSON.parse(fs.readFileSync(path.join(root, 'web/vendor-manifest.json')));
  const item = inventory.packages.find(value => value.id === 'powerquery-parser');
  assert.equal(item.version, '5.0.0');
  assert.equal(item.internalOnly, true);
  assert.equal(item.sourceRevision, 'npm-tarball-only');
  assert.equal(inventory.files.find(value => value.package === item.id).sha256, hash(bundle));
  const packages = JSON.parse(fs.readFileSync(path.join(root, 'web/vendor-packages.json')));
  assert.equal(packages.some(value => value.manifest.id === 'powerquery-parser'), false);
  assert.doesNotMatch(bundle, /sourceMappingURL=/);
  console.log('PASS Power Query parser: fixed local bundle and internal-only vendor scope');
}()).catch(error => { console.error(error); process.exitCode = 1; });
