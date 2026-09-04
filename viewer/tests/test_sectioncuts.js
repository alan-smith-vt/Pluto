// Headless tests for sectionCut.js: sidecar round trip (item -> cut -> item),
// axis detection, unknown-key preservation, legacy section_cuts.json import,
// selection / delete / group bookkeeping. THREE under vm; no DOM, no mesh.
//   node viewer/tests/test_sectioncuts.js
const fs = require('fs'), path = require('path'), vm = require('vm');
const V = path.join(__dirname, '..', 'scripts') + path.sep;
const L = path.join(__dirname, '..', 'lib') + path.sep;

global.window = global; global.self = global;
const stub = () => ({ style: {}, classList: { add() {}, remove() {}, toggle() {} }, addEventListener() {}, appendChild() {},
                      getContext: () => ({ clearRect() {}, fillRect() {}, fillText() {}, beginPath() {}, moveTo() {}, lineTo() {}, stroke() {}, fill() {}, closePath() {}, arc() {}, save() {}, restore() {}, setLineDash() {} }),
                      width: 240, height: 150, value: '', disabled: false, innerHTML: '', textContent: '' });
global.document = { getElementById: () => null, createElement: stub };
vm.runInThisContext(fs.readFileSync(L + 'three.min.js', 'utf8'), { filename: 'three.min.js' });
// viewer.js globals the module touches
global.scene = new THREE.Scene(); global.camera = new THREE.PerspectiveCamera();
global.mesh = null; global.feaEdges = null; global.feaModel = null; global.feaBuild = null; global.feaLCData = null;
global.currentComp = 0; global.currentStr = 0; global.smoothing = false; global.absValue = false;
global.envGlobalDsrValue = null; global.inDsrMode = () => false; global.inStrMode = () => false;
global.strengthComponents = () => []; global.nodeVec = () => new THREE.Vector3(); global.fmt = (v) => String(v);
global.requestRender = () => {}; global.log = () => {}; global.activeComponent = () => null;
let envelope = null;
global.FEAFeatures = { envelope: () => envelope, ensureEnvelope: () => (envelope = envelope || { format: 'pluto-features', version: 1, sectionCuts: { version: 1, items: [] } }) };
vm.runInThisContext(fs.readFileSync(V + 'sectionCut.js', 'utf8'), { filename: 'sectionCut.js' });

let fails = 0;
const assert = (c, m) => { if (!c) { fails++; console.error('FAIL ' + m); } else console.log('ok   ' + m); };
const near = (a, b, t = 1e-9) => Math.abs(a - b) <= t;

// 1. item -> cut -> item: an X cut on a wall panel whose normal is +Y
const item = { id: 'c-1', name: 'Wall base', group: 'Walls', visible: true, axis: 'x',
  plane: { point: [1, 2, 3], normal: [0, 0, 1] }, bounds: { up: [0, 1, 0], halfWidth: 5, halfHeight: 2 },
  domains: ['shells'], extra: { keep: 'me' } };
let c = FEASectionCut._itemToCut(item);
assert(c && c.axis === 'x' && near(c.length, 10) && near(c.center.y, 2), 'item -> cut: axis x, length 10, center (1,2,3)');
assert(near(c.normal.y, 1) && near(c.dir.x, 1), 'panel normal = bounds.up, dir = axis');
let back = FEASectionCut._cutToItem(c);
assert(back.id === 'c-1' && back.group === 'Walls' && back.extra.keep === 'me' && back.bounds.halfHeight === 2, 'cut -> item keeps id, group, unknown keys');
assert(near(back.bounds.halfWidth, 5) && back.axis === 'x' && back.domains[0] === 'shells', 'halfWidth, axis, domains written');
// section-plane normal = up x dir = (0,1,0) x (1,0,0) = (0,0,-1)
assert(near(back.plane.normal[2], -1), 'plane normal = up x dir: ' + back.plane.normal);
// dir recovers from plane.normal x up when axis is absent
const noAxis = JSON.parse(JSON.stringify(back)); delete noAxis.axis;
const c2 = FEASectionCut._itemToCut(noAxis);
assert(near(Math.abs(c2.dir.x), 1) && c2.axis === 'x', 'dir recovered from normal x up without the axis key');

// 2. sloped direction round-trips and reads as "sloped"
const sl = FEASectionCut._itemToCut({ id: 'c-2', plane: { point: [0, 0, 0], normal: [0, 0, 1] }, bounds: { up: [0, 1, 0], halfWidth: 3 } });
assert(sl.axis === 'x', 'normal (0,0,1) with up (0,1,0) -> dir along x');
const d = new THREE.Vector3(1, 0, 1).normalize();
assert(FEASectionCut._axisOf(d) === 'sloped' && FEASectionCut._axisOf(new THREE.Vector3(0, 0, -1)) === 'z', 'axisOf: diagonal is sloped, -z is z');
const slopedItem = FEASectionCut._cutToItem(FEASectionCut._makeCut({ center: new THREE.Vector3(), normal: new THREE.Vector3(0, 1, 0), dir: d, length: 4 }));
assert(!('axis' in slopedItem) && near(slopedItem.plane.normal[0], Math.SQRT1_2, 1e-6) && near(slopedItem.plane.normal[2], -Math.SQRT1_2, 1e-6), 'sloped cut: no axis key, plane normal stored');
const sl2 = FEASectionCut._itemToCut(slopedItem);
assert(sl2.axis === 'sloped' && near(sl2.dir.dot(d), 1, 1e-6), 'sloped cut round-trips its direction');

// 3. runtime bookkeeping: push, select, delete, groups pruned, envelope synced
FEASectionCut._push(FEASectionCut._makeCut({ name: 'A', group: 'G1', center: new THREE.Vector3(0, 0, 0), normal: new THREE.Vector3(0, 0, 1), dir: new THREE.Vector3(1, 0, 0), length: 2 }));
FEASectionCut._push(FEASectionCut._makeCut({ name: 'B', center: new THREE.Vector3(1, 0, 0), normal: new THREE.Vector3(0, 0, 1), dir: new THREE.Vector3(0, 1, 0), length: 2 }));
assert(FEASectionCut._cuts().length === 2 && envelope.sectionCuts.items.length === 2, 'two cuts pushed and written to the envelope');
assert(envelope.sectionCuts.items[0].name === 'A' && envelope.sectionCuts.items[1].axis === 'y', 'envelope items carry name and axis');
const idA = FEASectionCut._cuts()[0].id;
FEASectionCut._delete(idA);
assert(FEASectionCut._cuts().length === 1 && envelope.sectionCuts.items.length === 1, 'delete removes from list and envelope');

// 4. onEnvelope reloads from the sidecar, groups collected
FEASectionCut.onEnvelope({ sectionCuts: { version: 1, groups: [{ name: 'Walls', visible: false }], items: [item, slopedItem] } });
assert(FEASectionCut._cuts().length === 2 && FEASectionCut._groups().length === 1 && FEASectionCut._groups()[0].visible === false, 'onEnvelope: 2 cuts, group Walls hidden');

// 5. legacy import: inches -> model units left as-is when the unit dropdown is unknown
FEASectionCut._importLegacy({ version: 1, groups: [{ name: 'Old' }], cuts: [{ name: 'L1', group: 'Old', point: [12, 24, 36], axis: 'Z', length: 120, visible: true }] });
const legacy = FEASectionCut._cuts().find(x => x.name === 'L1');
assert(legacy && legacy.axis === 'z' && near(legacy.length, 120) && near(legacy.center.y, 24), 'legacy cut imported: axis z, length 120, point kept');
assert(FEASectionCut._groups().some(g => g.name === 'Old'), 'legacy group registered');

console.log(fails ? fails + ' FAILED' : 'ALL SECTION-CUT TESTS PASSED');
process.exitCode = fails ? 1 : 0;
