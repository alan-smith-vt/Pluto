// Headless test for files.js: JSON classification and sidecar-to-model name matching.
//   node viewer/tests/test_files.js
const fs = require('fs'), path = require('path'), vm = require('vm');
global.window = global; global.document = { getElementById: () => null };
global.log = () => {};
vm.runInThisContext(fs.readFileSync(path.join(__dirname, '..', 'scripts', 'files.js'), 'utf8'), { filename: 'files.js' });
let fails = 0;
const assert = (c, m) => { if (!c) { fails++; console.error('FAIL ' + m); } else console.log('ok   ' + m); };
const C = FEAFiles.classify;
assert(C({ format: 'pluto-features', version: 1 }) === 'features', 'pluto-features sidecar');
assert(C({ version: 1, groups: [{ name: 'g' }], cuts: [{ point: [0, 0, 0], axis: 'X', length: 12 }] }) === 'legacy-cuts', 'archived viewer section_cuts.json');
assert(C({ version: 3, expressions: [{ name: 'e', type: 'elements', root: {} }] }) === 'legacy-predicates', 'archived viewer predicates.json v3');
assert(C({ groups: [{ name: 'g', predicate: {} }] }) === 'legacy-predicates', 'archived viewer predicates.json v1');
assert(C({ groups: [{ name: 'g' }] }) === 'unknown' && C(null) === 'unknown' && C([1]) === 'unknown', 'anything else is unknown');
assert(FEAFiles._sidecarModel('TANK-A.features.json') === 'TANK-A' && FEAFiles._sidecarModel('TANK-A.json') === 'TANK-A', 'sidecar name -> model name');
assert(FEAFiles.hasFileSystemAccess() === false, 'no File System Access API under node');
console.log(fails ? fails + ' FAILED' : 'ALL FILES TESTS PASSED');
process.exitCode = fails ? 1 : 0;
