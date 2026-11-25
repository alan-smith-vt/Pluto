// Headless test for modules/bridgePicker.js: the bridges.json format (makeDoc / readDoc). No THREE, no DOM.
//   node viewer/tests/test_bridge_picker.js
const fs = require('fs'), path = require('path'), vm = require('vm');
const src = fs.readFileSync(path.join(__dirname, '..', 'scripts', 'modules', 'bridgePicker.js'), 'utf8');
vm.runInThisContext(src + '\nglobalThis.FEABridgePicker = FEABridgePicker;', { filename: 'bridgePicker.js' });
const B = globalThis.FEABridgePicker;
let fails = 0;
const assert = (c, m) => { if (!c) { fails++; console.error('FAIL ' + m); } else console.log('ok   ' + m); };

const list = [{ mode: 'extend', a: { parts: ['400012', '400013'], label: 'clean | system SYSA | 24x12 | 3.5 ft | parts 400012 400013' },
                b: { parts: ['N:abc'], label: 'clean | system SYSA | 24x12 | 2 ft | parts Navisworks #abc' } }];
const doc = B.makeDoc(list);
assert(doc.kind === 'pluto-duct-bridges' && doc.version === 1 && doc.bridges.length === 1, 'makeDoc: kind, version, one bridge');
const back = B.readDoc(JSON.parse(JSON.stringify(doc)));
assert(JSON.stringify(back) === JSON.stringify(list), 'readDoc(makeDoc(x)) = x');
const odd = B.readDoc({ kind: 'pluto-duct-bridges', bridges: [{ mode: 'weird', a: { parts: [1] }, b: { parts: ['2'] } }, { a: { parts: [] }, b: { parts: ['2'] } }, null] });
assert(odd.length === 1 && odd[0].mode === 'rigid' && odd[0].a.parts[0] === '1' && odd[0].a.label === '', 'readDoc: unknown mode = rigid, ids as strings, empty sides and nulls dropped');
let refused = false; try { B.readDoc({ kind: 'other' }); } catch (e) { refused = /pluto-duct-bridges/.test(e.message); }
assert(refused, 'readDoc refuses another kind of file');

console.log(fails ? fails + ' FAILED' : 'all passed');
process.exit(fails ? 1 : 0);
