'use strict';
// Development-time rebuild only. The product ships the fixed browser output.
// No package restore, install script, network call or version selection happens here.
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const assert = require('node:assert/strict');
const root = path.resolve(__dirname, '..');
const lock = JSON.parse(fs.readFileSync(path.join(__dirname, 'powerquery-parser.lock.json'), 'utf8'));
const args = process.argv.slice(2);
const index = args.indexOf('--modules');
if (index < 0 || !args[index + 1] || args.some((value, at) => at !== index && at !== index + 1 && value !== '--check'))
  throw new Error('Usage: node tools/build-powerquery-parser.js --modules /exact/build/node_modules [--check]');
const modules = path.resolve(args[index + 1]);
const hash = bytes => crypto.createHash('sha256').update(bytes).digest('hex');
for (const item of lock.runtimePackages.concat([lock.buildTool])) {
  const manifest = JSON.parse(fs.readFileSync(path.join(modules, item.name, 'package.json'), 'utf8'));
  assert.equal(manifest.name, item.name);
  assert.equal(manifest.version, item.version, 'Pinned build dependency ' + item.name);
}
for (const item of lock.inputs) {
  assert.ok(item.scope === 'repository' || item.scope === 'node_modules');
  assert.ok(!path.isAbsolute(item.path) && !item.path.split('/').includes('..'));
  const input = path.join(item.scope === 'repository' ? root : modules, item.path);
  assert.equal(hash(fs.readFileSync(input)), item.sha256, 'Changed parser build input ' + item.path);
}
const esbuild = require(path.join(modules, 'esbuild'));
assert.equal(esbuild.version, lock.buildTool.version);
const options = lock.options;
const built = esbuild.buildSync({
  absWorkingDir: root, entryPoints: ['tools/powerquery-parser-entry.js'],
  bundle: true, platform: options.platform, format: options.format,
  globalName: options.globalName, target: options.target, minify: options.minify,
  legalComments: options.legalComments, charset: options.charset,
  banner: { js: options.banner }, nodePaths: [modules],
  write: false, sourcemap: false, metafile: true, logLevel: 'silent'
});
assert.equal(built.warnings.length, 0, 'Review bundler warnings before changing the fixed build');
const used = Object.keys(built.metafile.inputs).map(input => {
  const absolute = path.resolve(root, input);
  const packageInput = absolute.startsWith(modules + path.sep);
  return (packageInput ? 'node_modules:' : 'repository:') +
    path.relative(packageInput ? modules : root, absolute).split(path.sep).join('/');
}).sort();
assert.deepEqual(used, lock.inputs.map(input => input.scope + ':' + input.path).sort(), 'Build closure changed');
const bytes = built.outputFiles[0].contents;
assert.equal(bytes.length, lock.output.bytes);
assert.equal(hash(bytes), lock.output.sha256, 'Rebuild differs from the reviewed bundle');
const target = path.join(root, lock.output.path);
if (args.includes('--check')) assert.equal(hash(fs.readFileSync(target)), lock.output.sha256, 'Shipped bundle differs');
else fs.writeFileSync(target, bytes);
console.log('PASS Power Query parser: exact input closure and reproducible offline bundle (' + bytes.length + ' bytes)');
