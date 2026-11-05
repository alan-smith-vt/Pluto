// Headless tests for loads.js (applied loads from the sidecar `loads` section): items -> entries per
// vector (forces and moments apart, displacements and rotations, uniform rows, wind), where the arrows
// sit and which way they point (a force pushing on the pipe, a moment / rotation double-headed out from
// it, a uniform row along the beams with tips on the surface, wind from the side picked), lengths by
// magnitude, the type / vector filters, labels, picking, the readout text, itemsAt, the section-cut mask,
// and the section round-tripping untouched. Version 2 (a SAP frame model, inline sidecar): the "Loads of"
// pick resolved (pattern, case with sf, Linear Add with a -1 factor, nested combinations, an envelope's
// union, a cycle cut), point / distributed / temperature symbols on their beams, local directions from a
// beam's local y, factors flipping arrows, itemText, the pick remembered per model, and a version-1
// sidecar drawing as before. THREE under vm; no DOM.
//   node viewer/tests/test_loads.js
const fs = require('fs'), path = require('path'), vm = require('vm');
const V = path.join(__dirname, '..', 'scripts') + path.sep;
const L = path.join(__dirname, '..', 'lib') + path.sep;

const store = {};
global.localStorage = { getItem: k => (Object.prototype.hasOwnProperty.call(store, k) ? store[k] : null), setItem: (k, v) => { store[k] = String(v); } };
global.window = global; global.self = global;
global.document = { getElementById: () => null, createElement: () => ({ style: {}, classList: { add() {}, remove() {}, toggle() {} }, addEventListener() {}, appendChild() {} }) };
global.log = () => {};
global.needsRender = false;
vm.runInThisContext(fs.readFileSync(L + 'three.min.js', 'utf8'), { filename: 'three.min.js' });
vm.runInThisContext(fs.readFileSync(V + 'shaders.js', 'utf8'), { filename: 'shaders.js' });
global.scene = new THREE.Scene();
global.feaBuild = null;
global.feaMaterial = null;
global.FEAAttributes = { updateCatIdx() {} };
global.fmt = (v, d) => (Math.round(v * 1000) / 1000).toString();

let fails = 0;
const assert = (c, m) => { if (!c) { fails++; console.error('FAIL ' + m); } else console.log('ok   ' + m); };
const near = (a, b, t = 1e-5) => Math.abs(a - b) <= t;

// A straight pipe along +X: nodes 10 (0,0,0), 20 (100,0,0), 30 (200,0,0); beams 100 (10-20), 200 (20-30); PIPE od 2 (R = 1).
const NODES = Float64Array.from([0, 0, 0, 100, 0, 0, 200, 0, 0]);
const elems = new Uint32Array(2 * 6);
elems.set([0, 0, 1, 0, 0, 0], 0); elems.set([0, 1, 2, 0, 0, 0], 6);
const beamView = {
  header: { nElements: 2, cornerComponents: 1, maxCorners: 2 },
  meta: { components: [{ name: 'DX', kind: 'displacement', unit: 'in' }] },
  unified: { upAxis: 'Y' },
  nodes: NODES, nodeIds: Uint32Array.from([10, 20, 30]), nodeLabels: null,
  elems: elems, elemRecordU32: 6, elemIds: Uint32Array.from([100, 200]),
  sections: [{ name: 'P2', type: 'PIPE', params: { od: 2, t: 0.2 } }]
};
global.FEABeams = {
  view: () => beamView,
  build: () => ({ sectionOf: Int32Array.from([0, 0]), sectionOfB: Int32Array.from([0, 0]), geometry: null }),
  mesh: () => null, lcData: () => null
};
global.feaModel = { header: { nElements: 0, nNodes: 3 }, nodeIds: beamView.nodeIds, nodes: NODES, elemIds: new Uint32Array(0),
                    unified: { domains: [{ name: 'beams', family: 'beam' }], geometryHash: 'h' } };
vm.runInThisContext(fs.readFileSync(V + 'features.js', 'utf8'), { filename: 'features.js' });
vm.runInThisContext(fs.readFileSync(V + 'labels.js', 'utf8'), { filename: 'labels.js' });
vm.runInThisContext(fs.readFileSync(V + 'loads.js', 'utf8'), { filename: 'loads.js' });

const LOADS = { version: 1, units: { force: 'lb.', moment: 'in.lb.', length: 'in.', rotation: 'deg', uniform: 'lb./in.', g: "g's" },
  note: 'load vectors as CAESAR numbers them', items: [
  { id: 'caesar-force-20', name: 'F 20', kind: 'force', nodeIds: [20], elements: [1, 2],
    vectors: [{ v: 1, values: [0, -1000, 0, 0, 0, 0] }, { v: 2, values: [0, 0, 0, 0, 0, 3000] }, { v: 3, values: [500, 0, 0, 0, 0, 0] }],
    tags: ['caesar', 'load', 'force'], userKey: { kept: true } },
  { id: 'caesar-displacement-30', name: 'D 30', kind: 'displacement', nodeIds: [30], elements: [2],
    vectors: [{ v: 1, values: [0.5, null, null, null, null, null] }, { v: 2, values: [0, 0, 0, 0, 0, 0] }, { v: 3, values: [null, null, null, 0, 1.5, null] }] },
  { id: 'caesar-uniform-1', name: 'U 10-30', kind: 'uniform', elements: [1, 2], nodeIds: [10, 20, 30],
    members: [{ domain: 'beams', ids: [100, 200] }], vectors: [{ v: 1, values: [0, -5, 0], g: false }, { v: 2, values: [0.2, 0, 0], g: true }] },
  { id: 'caesar-wind-1', name: 'Wind 10-20', kind: 'wind', elements: [1], members: [{ domain: 'beams', ids: [100] }], wind: { type: 'wind', shape: 0.7 } },
  { id: 'off', name: 'F 99', kind: 'force', nodeIds: [99], vectors: [{ v: 1, values: [1, 0, 0, 0, 0, 0] }] }
] };
const env = () => ({ format: 'pluto-features', version: 1, model: { geometryHash: 'h' }, groups: { version: 1, items: [] },
                     loads: JSON.parse(JSON.stringify(LOADS)) });

FEALoads.onModelLoaded();
FEAFeatures.setEnvelope(env(), 't.features.json');
scene.updateMatrixWorld(true);
const D = FEALoads;
const ents = () => D._entries();
const meshes = () => D._meshes();
const count = p => (meshes() && meshes()[p] ? meshes()[p].mesh.count : 0);
const inst = (p, k, local) => { const m = new THREE.Matrix4(); meshes()[p].mesh.getMatrixAt(k, m); return new THREE.Vector3(local[0], local[1], local[2]).applyMatrix4(m); };
const ownerOf = (p, k) => ents()[meshes()[p].owner[k]];
const cones = (pred) => { const out = []; const m = meshes().cone; for (let k = 0; k < m.owner.length; k++) if (pred(ownerOf('cone', k))) out.push(k); return out; };
const tip = k => inst('cone', k, [0, 0.5, 0]);
const dir = k => tip(k).sub(inst('cone', k, [0, -0.5, 0])).normalize();

// 1. entries: F1 force, F2 moment, F3 force; D1 translation, D3 rotation (D2 = held zeros: nothing); U1, U2 (g); wind; F 99 off the model
const kinds = ents().map(e => e.kind + (e.rot ? 'R' : '') + e.v);
assert(ents().length === 8, 'eight entries: ' + kinds.join(' '));
assert(kinds.includes('force1') && kinds.includes('momentR2') && kinds.includes('force3'), 'force vectors: F1 / F3 as forces, F2 (MZ only) as a moment');
assert(kinds.includes('displacement1') && kinds.includes('displacementR3') && !kinds.includes('displacement2'), 'displacements: D1 a translation, D3 a rotation, D2 (zeros: held) not drawn');
assert(ents().filter(e => e.kind === 'uniform').length === 2 && ents().some(e => e.kind === 'uniform' && e.g), 'uniform: two vectors, U2 in g\'s');
assert(ents().filter(e => e.kind === 'wind').length === 1, 'wind: one entry');
assert(!ents().some(e => e.item.name === 'F 99'), 'a load at a node not in the model is skipped');

// 2. a force pushes on the pipe: F1 -1000 Y at node 20 -> tip on the pipe top (100, 1, 0), pointing -Y
{
  const k = cones(e => e.kind === 'force' && e.v === 1)[0];
  const t = tip(k), d = dir(k);
  assert(near(t.x, 100) && near(t.y, 1) && near(t.z, 0), 'F1: arrow tip on the pipe surface above node 20: ' + t.toArray().map(v => v.toFixed(3)));
  assert(near(d.y, -1), 'F1: points -Y, the way the force pushes');
}
// 3. a moment is double-headed out from the pipe along +Z
{
  const ks = cones(e => e.kind === 'moment');
  assert(ks.length === 2, 'F2 MZ: two heads');
  assert(ks.every(k => near(dir(k).z, 1)) && ks.every(k => tip(k).z > 1), 'F2 MZ: heads point +Z, beyond the pipe surface');
}
// 4. lengths grow with the value: F3 (500) shorter than F1 (1000)
{
  const shaft = e => { const m = meshes().cyl; for (let k = 0; k < m.owner.length; k++) if (ents()[m.owner[k]] === e) { const a = inst('cyl', k, [0, 0.5, 0]), b = inst('cyl', k, [0, -0.5, 0]); return a.distanceTo(b); } return 0; };
  const f1 = ents().find(e => e.kind === 'force' && e.v === 1), f3 = ents().find(e => e.kind === 'force' && e.v === 3);
  assert(shaft(f3) < shaft(f1) && shaft(f3) > 0, 'F3 (500 lb.) draws shorter than F1 (1000 lb.)');
}
// 5. imposed displacement: out from the pipe along +X at node 30; the rotation double-headed along +Y
{
  const k = cones(e => e.kind === 'displacement' && !e.rot)[0];
  assert(near(dir(k).x, 1) && tip(k).x > 201, 'D1: arrow out from the pipe along +X past node 30');
  assert(cones(e => e.kind === 'displacement' && e.rot).length === 2, 'D3 RY: double-headed');
}
// 6. uniform rows: arrows along both beams, tips on the surface, pointing -Y (U1)
{
  const ks = cones(e => e.kind === 'uniform' && e.v === 1);
  assert(ks.length >= 4, 'U1: a row of arrows along the two beams (' + ks.length + ')');
  assert(ks.every(k => near(dir(k).y, -1) && near(tip(k).y, 1) && tip(k).x > 0 && tip(k).x < 200), 'U1: every arrow points -Y with its tip on the pipe top, between the ends');
  const kx = cones(e => e.kind === 'uniform' && e.v === 2);
  assert(kx.length > 0 && kx.every(k => near(Math.abs(dir(k).x), 1) && tip(k).y > 1), 'U2 (axial, in g\'s): arrows along the pipe, on its top');
}
// 7. wind: from the side picked, across the pipe only on the loaded beam
{
  D._set({ windFrom: '+Z' });
  const ks = cones(e => e.kind === 'wind');
  assert(ks.length > 0 && ks.every(k => near(dir(k).z, 1) && tip(k).x < 100.0001), 'wind towards +Z: arrows point +Z, on beam 100 only');
  D._set({ windFrom: '+X' });
  assert(cones(e => e.kind === 'wind').length === 0, 'wind along the pipe axis draws nothing (no crosswind component)');
  D._set({ windFrom: '-X' });
}
// 8. filters
{
  const all = D.count();
  D._set({ vector: 1 });
  assert(D._shown().every(i => ents()[i].v === 1 || ents()[i].kind === 'wind'), 'vector 1: only F1 / D1 / U1 (and wind) shown');
  D._set({ vector: 0, kinds: { force: false, moment: false } });
  assert(!D._shown().some(i => ents()[i].kind === 'force' || ents()[i].kind === 'moment'), 'forces and moments off: none shown');
  D._set({ kinds: { force: true, moment: true } });
  assert(D.count() === all, 'back on: all shown again');
}
// 9. labels and text
{
  const lab = FEALabels._layers().loads;
  assert(lab && lab.length === D.count(), 'one label per drawn load (' + (lab ? lab.length : 0) + ')');
  const f1 = ents().find(e => e.kind === 'force' && e.v === 1);
  assert(D._text(f1) === 'F1 Y -1000 lb.', 'label text: ' + D._text(f1));
  const m2 = ents().find(e => e.kind === 'moment');
  assert(D._text(m2) === 'F2 MZ 3000 in.lb.', 'moment label: ' + D._text(m2));
  D._set({ labels: false });
  assert(FEALabels._layers().loads.length === 0, 'labels off: none');
  D._set({ labels: true });
}
// 10. picking: a ray down onto the F1 arrow hits it
{
  const rc = new THREE.Raycaster(new THREE.Vector3(100, 1.5, 50), new THREE.Vector3(0, 0, -1));
  const h = D.pick(rc);
  assert(h && h.load && ents()[h.entry].kind === 'force' && ents()[h.entry].v === 1, 'pick: a ray through the F1 shaft finds F1');
}
// 11. itemsAt: by node and by element
{
  assert(D.itemsAt(20, 0).map(i => i.name).join() === 'F 20', 'itemsAt(node 20): F 20');
  assert(D.itemsAt(null, 1).map(i => i.name).sort().join() === 'U 10-30,Wind 10-20', 'itemsAt(element 1): the uniform and wind runs');
  assert(D.itemsAt(null, 100).length === 0, 'itemsAt(beam 100): a version-1 run is found by its CAESAR element, not its beams');
}
// 11b. itemText (version 1) and no select
{
  const t0 = D.itemText(LOADS.items[0]), t2 = D.itemText(LOADS.items[2]), t3 = D.itemText(LOADS.items[3]);
  assert(t0 === 'F 20: force F1 FY -1000; F2 MZ 3000; F3 FX 500', 'itemText force: ' + t0);
  assert(t2 === 'U 10-30: uniform U1 UY -5; U2 UX 0.2 (g)', 'itemText uniform: ' + t2);
  assert(t3 === 'Wind 10-20: wind shape 0.7', 'itemText wind: ' + t3);
  const s = D.selection();
  assert(!s.v2 && s.value === 'all' && s.text === '' && D.setSelection('p:DEAD') === false, 'version 1: no patterns, no select (setSelection refused)');
  assert(ents().length === 8 && D.factorOf(LOADS.items[0]) === 1, 'version 1: still the eight entries, every item x1');
}
// 12. section-cut mask: node 20 cut away hides its loads and the beams' rows
{
  D.writeVis(Uint8Array.from([1, 0, 1]));
  assert(!D._shown().some(i => ents()[i].ni === 1), 'node 20 masked: its loads hidden');
  D.writeVis(null);
}
// 13. round trip: the section is never edited
{
  const out = JSON.parse(FEAFeatures.exportJson());
  assert(JSON.stringify(out.loads) === JSON.stringify(LOADS), 'the loads section exports exactly as loaded (unknown keys kept)');
}
// 14. cleared
FEALoads.onModelCleared();
assert(D.count() === 0 && ents().length === 0, 'cleared: nothing drawn');

// ================================================================
// Version 2: a SAP frame model (Z up). Nodes 1 (0,0,0), 2 (100,0,0), 3 (100,0,100), 4 (0,100,0);
// beams 11 (1-2, +X), 12 (2-3, +Z), 13 (1-4, +Y); PIPE od 2 (R = 1). BPRP local y: 11 +Y and 12 -Y
// (SAP's default axes), 13 +X (turned: SAP local 2 = x cross y = -Z, local 3 = -y = -X).
// ================================================================
const FN = Float64Array.from([0, 0, 0, 100, 0, 0, 100, 0, 100, 0, 100, 0]);
const fe = new Uint32Array(3 * 6);
fe.set([0, 0, 1, 0, 0, 0], 0); fe.set([0, 1, 2, 0, 0, 0], 6); fe.set([0, 0, 3, 0, 0, 0], 12);
const bprp = new Float32Array(3 * 8);
bprp.set([0, 1, 0], 0); bprp.set([0, -1, 0], 8); bprp.set([1, 0, 0], 16);
const frameView = {
  header: { nElements: 3, cornerComponents: 1, maxCorners: 2 },
  meta: { components: [] },
  unified: { upAxis: 'Z' },
  nodes: FN, nodeIds: Uint32Array.from([1, 2, 3, 4]), nodeLabels: null,
  elems: fe, elemRecordU32: 6, elemIds: Uint32Array.from([11, 12, 13]),
  sections: [{ name: 'P2', type: 'PIPE', params: { od: 2, t: 0.2 } }],
  beamProps: bprp
};
const LP = (name, sf) => ({ type: 'Load pattern', name: name, sf: sf });
const CO = (name, sf) => ({ type: 'Response Combo', name: name, sf: sf });
const SAP = { version: 2, units: { force: 'kip', length: 'in', moment: 'kip-in', uniform: 'kip/in', temperature: 'F' },
  patterns: [{ name: 'DEAD', type: 'Dead', selfWeight: 1 }, { name: 'LIVE', type: 'Live', selfWeight: 0 }, { name: 'W', type: 'Wind', selfWeight: 0 },
             { name: 'LOC', type: 'Other', selfWeight: 0 }, { name: 'T1', type: 'Temperature', selfWeight: 0 }],
  cases: [{ name: 'DEAD', type: 'LinStatic', initial: 'Zero', loads: [LP('DEAD', 1)] },
          { name: 'SUS', type: 'NonStatic', initial: 'Zero', loads: [LP('DEAD', 1), LP('LIVE', 1.5), LP('LIVE', 0.5)] },
          { name: 'OPE', type: 'NonStatic', initial: 'SUS', loads: [LP('T1', 1)] },
          { name: 'MOD', type: 'LinStatic', initial: 'Zero', loads: [{ type: 'Accel', name: 'UX', sf: 1 }, LP('W', 1)] }],
  combinations: [{ name: 'EXP', type: 'Linear Add', items: [{ type: 'Nonlinear Static', name: 'OPE', sf: 1 }, { type: 'Nonlinear Static', name: 'SUS', sf: -1 }] },
                 { name: 'NEST', type: 'Linear Add', items: [CO('EXP', 2), { type: 'Linear Static', name: 'DEAD', sf: 1 }] },
                 { name: 'ENV', type: 'Envelope', items: [{ type: 'Linear Static', name: 'DEAD', sf: 1.4 }, CO('EXP', 1)] },
                 { name: 'NEG', type: 'Linear Add', items: [{ type: 'Nonlinear Static', name: 'OPE', sf: -1 }] },
                 { name: 'CYC1', type: 'Linear Add', items: [CO('CYC2', 1), { type: 'Linear Static', name: 'DEAD', sf: 1 }] },
                 { name: 'CYC2', type: 'Linear Add', items: [CO('CYC1', 1), { type: 'Nonlinear Static', name: 'OPE', sf: 1 }] }],
  items: [
    { id: 'sap-load-1', pattern: 'DEAD', coordSys: 'GLOBAL', name: 'B11 DEAD', kind: 'distributed', members: [{ domain: 'beams', ids: [11] }], frame: 'B11',
      type: 'force', dir: 'Gravity', from: { rel: 0, abs: 0 }, to: { rel: 1, abs: 100 }, valueA: 0.002, valueB: 0.002 },
    { id: 'sap-load-2', pattern: 'LIVE', coordSys: 'GLOBAL', name: 'B11 LIVE', kind: 'point', members: [{ domain: 'beams', ids: [11] }], frame: 'B11',
      type: 'force', dir: 'Gravity', at: { rel: 0.4, abs: 40 }, value: 0.85 },
    { id: 'sap-load-3', pattern: 'LIVE', coordSys: 'Local', name: 'B13 LIVE', kind: 'point', members: [{ domain: 'beams', ids: [13] }], frame: 'B13',
      type: 'moment', dir: '2', at: { rel: 0.25, abs: 25 }, value: 7.5 },
    { id: 'sap-load-4', pattern: 'LIVE', coordSys: 'GLOBAL', name: '2 LIVE', kind: 'force', nodeIds: [2], vectors: [{ v: 1, values: [0, 0, -0.5, 0, 0, 0] }] },
    { id: 'sap-load-5', pattern: 'W', coordSys: 'GLOBAL', name: 'B12 W', kind: 'distributed', members: [{ domain: 'beams', ids: [12] }], frame: 'B12',
      type: 'force', dir: 'X Proj', from: { rel: 0.25, abs: 25 }, to: { rel: 0.75, abs: 75 }, valueA: 0.1, valueB: 0.2 },
    { id: 'sap-load-6', pattern: 'W', coordSys: 'Local', name: 'B13 W', kind: 'distributed', members: [{ domain: 'beams', ids: [13] }], frame: 'B13',
      type: 'force', dir: '2', from: { rel: 0, abs: 0 }, to: { rel: 1, abs: 100 }, valueA: 0.004, valueB: 0.004 },
    { id: 'sap-load-7', pattern: 'LOC', coordSys: 'Local', name: 'B13 LOC', kind: 'point', members: [{ domain: 'beams', ids: [13] }], frame: 'B13',
      type: 'force', dir: '3', at: { rel: 0.5, abs: 50 }, value: 1 },
    { id: 'sap-load-8', pattern: 'T1', coordSys: 'GLOBAL', name: 'B11 T1', kind: 'temperature', members: [{ domain: 'beams', ids: [11] }], frame: 'B11', type: 'Temperature', dT: 230 },
    { id: 'sap-load-9', pattern: 'T1', coordSys: 'GLOBAL', name: 'B12 T1', kind: 'temperature', members: [{ domain: 'beams', ids: [12] }], frame: 'B12', type: 'Temperature', dT: -40 },
    { id: 'sap-load-10', pattern: 'T1', coordSys: 'GLOBAL', name: '3 T1', kind: 'displacement', nodeIds: [3], vectors: [{ v: 1, values: [0, 0, -0.125, 0, 0, 0] }] }
  ] };
const senv = () => ({ format: 'pluto-features', version: 1, model: { geometryHash: 'h' }, groups: { version: 1, items: [] },
                      loads: JSON.parse(JSON.stringify(SAP)) });
const v1View = FEABeams.view, v1Build = FEABeams.build;
const frameBuild = () => ({ sectionOf: Int32Array.from([0, 0, 0]), sectionOfB: Int32Array.from([0, 0, 0]), geometry: null });
FEABeams.view = () => frameView;
FEABeams.build = frameBuild;
FEALoads.onModelLoaded();
FEAFeatures.setEnvelope(senv(), 'frame.features.json');
const P3 = (k, x, y, z, t = 1e-3) => { const p = tip(k); return near(p.x, x, t) && near(p.y, y, t) && near(p.z, z, t); };
const byId = id => ents().find(e => e.item.id === id);
const factorsText = r => r.factors === null ? 'null' : JSON.stringify(Object.keys(r.factors).sort().map(k => [k, Math.round(r.factors[k] * 1e9) / 1e9]));
const want = o => JSON.stringify(Object.keys(o).sort().map(k => [k, o[k]]));
// shafts (parallel to `axis`) of an entry: [{tail, len}]; the other rods of the entry: [[a, b]]
const shaftsOf = (en, axis, tipCoord) => {
  const out = { shafts: [], rods: [] }, m = meshes().cyl;
  for (let k = 0; k < m.owner.length; k++) {
    if (ents()[m.owner[k]] !== en) continue;
    const a = inst('cyl', k, [0, 0.5, 0]), b = inst('cyl', k, [0, -0.5, 0]);
    const d = a.clone().sub(b).normalize();
    if (Math.abs(d.getComponent(axis)) > 0.99) out.shafts.push({ tail: b, len: Math.abs(tipCoord - b.getComponent(axis)) });
    else out.rods.push([a, b]);
  }
  return out;
};

// 15. the pick resolved
{
  const R = v => D._resolve(SAP, v);
  const all = R('all');
  assert(all.kind === 'all' && all.factors === null && all.text === 'All patterns; + self weight \u00d71 (DEAD)', 'all patterns: every item x1 (' + all.text + ')');
  const pd = R('p:DEAD');
  assert(factorsText(pd) === want({ DEAD: 1 }) && pd.text === 'Pattern DEAD (Dead); + self weight \u00d71', 'pattern DEAD: x1, self weight listed (' + pd.text + ')');
  const sus = R('c:SUS');
  assert(factorsText(sus) === want({ DEAD: 1, LIVE: 2 }), 'case SUS: DEAD x1, LIVE listed twice (1.5 + 0.5) sums to x2: ' + factorsText(sus));
  assert(sus.text === 'Case SUS (NonStatic): DEAD \u00d71, LIVE \u00d72; + self weight \u00d71 (DEAD)', 'case SUS text: ' + sus.text);
  const ope = R('c:OPE');
  assert(factorsText(ope) === want({ T1: 1, DEAD: 1, LIVE: 2 }) && ope.notes.indexOf('OPE includes SUS (continues from it)') >= 0,
         'case OPE (nonlinear, from nonlinear SUS): T1 plus the SUS loads it carries (' + ope.text + ')');
  const lin = D._resolve({ patterns: SAP.patterns, cases: [{ name: 'L', type: 'LinStatic', initial: 'SUS', loads: [LP('W', 1)] }].concat(SAP.cases), combinations: [] }, 'c:L');
  assert(factorsText(lin) === want({ W: 1 }) && lin.notes.indexOf('continues from SUS') >= 0, 'a linear case from a nonlinear one takes its stiffness, not its loads (' + lin.text + ')');
  const mod = R('c:MOD');
  assert(factorsText(mod) === want({ W: 1 }) && mod.notes.some(n => /Accel UX skipped/.test(n)), 'case MOD: the Accel load skipped with a note (' + mod.text + ')');
  const exp = R('k:EXP');
  assert(factorsText(exp) === want({ T1: 1 }) && !exp.union, 'Linear Add EXP = OPE - SUS: the SUS loads cancel, T1 x1 is left: ' + factorsText(exp));
  assert(exp.notes.indexOf('DEAD, LIVE cancel') >= 0 && !/self weight/.test(exp.text), 'EXP text: ' + exp.text);
  const nest = R('k:NEST');
  assert(factorsText(nest) === want({ T1: 2, DEAD: 1 }), 'nested: NEST = 2 EXP + DEAD multiplies down: ' + factorsText(nest));
  const env = R('k:ENV');
  assert(env.union && factorsText(env) === want({ DEAD: 1, T1: 1, LIVE: 1 }) && /envelope: union shown/.test(env.text),
         'envelope: the union of what it reaches, unfactored, the first path\'s sign (' + env.text + ')');
  const cyc = R('k:CYC1');
  assert(factorsText(cyc) === want({ T1: 1, DEAD: 2, LIVE: 2 }) && cyc.notes.some(n => /cycle at CYC1/.test(n)), 'a cycle (CYC1 -> CYC2 -> CYC1) is cut: ' + factorsText(cyc) + ' (' + cyc.text + ')');
  const opts = D._options(SAP).map(o => o.value);
  assert(opts.length === 16 && opts[0] === 'all' && opts.indexOf('p:DEAD') > 0 && opts.indexOf('c:DEAD') > 0 && opts.indexOf('k:CYC2') > 0,
         'select: All patterns, 5 patterns, 4 cases, 6 combinations (a pattern and a case may share a name)');
}
// 16. all patterns, then one pattern: point loads on their beams
{
  const s = D.selection();
  assert(s.v2 && s.value === 'all' && ents().length === 10, 'version 2: the select starts at All patterns, every item drawn (' + ents().length + ')');
  D.setSelection('p:LIVE');
  assert(ents().length === 3 && D.selection().value === 'p:LIVE', 'pattern LIVE: its point load, point moment and joint force only');
  const p1 = byId('sap-load-2'), k1 = cones(e => e === p1);
  assert(k1.length === 1 && P3(k1[0], 40, 0, 1) && near(dir(k1[0]).z, -1, 1e-4), 'point load: one arrow at rel 0.4 (x 40), tip on the pipe top, pointing down (Gravity)');
  assert(D._text(p1) === '0.85 kip Gravity', 'point label: ' + D._text(p1));
  const m1 = byId('sap-load-3'), km = cones(e => e === m1);
  assert(km.length === 2 && km.every(k => near(dir(k).z, -1, 1e-4) && near(tip(k).x, 0, 1e-3) && near(tip(k).y, 25, 1e-3) && tip(k).z < -1),
         'point moment about local 2 on beam 13 (local y +X, so 2 = -Z): double-headed, out from the pipe at rel 0.25');
  assert(D._text(m1) === '7.5 kip-in local 2', 'moment label: ' + D._text(m1));
  const n1 = byId('sap-load-4'), kn = cones(e => e === n1);
  assert(kn.length === 1 && P3(kn[0], 100, 0, 1) && near(dir(kn[0]).z, -1, 1e-4) && D._text(n1) === 'FZ -0.5 kip', 'joint force: as version 1, pushing down on node 2 (' + D._text(n1) + ')');
  // the size control scales the arrows
  const shaft = en => shaftsOf(en, 2, 1).shafts[0].len;
  const a = shaft(p1);
  D._set({ size: 2 });
  const b = shaft(p1);
  D._set({ size: 1 });
  assert(near(b / a, 2, 1e-3), 'size 2: the point load\'s shaft doubles (' + a.toFixed(3) + ' -> ' + b.toFixed(3) + ')');
}
// 17. a case: factors scale; distributed rows
{
  D.setSelection('c:SUS');
  const p1 = byId('sap-load-2');
  assert(p1.factor === 2 && D._text(p1) === '1.7 kip Gravity', 'case SUS: LIVE x2, the point load reads ' + D._text(p1));
  const d1 = byId('sap-load-1'), kd = cones(e => e === d1);
  assert(kd.length >= 3 && kd.every(k => near(dir(k).z, -1, 1e-4) && near(tip(k).z, 1, 1e-3) && near(tip(k).y, 0, 1e-3)),
         'distributed Gravity: a row of ' + kd.length + ' arrows pointing down, tips on the pipe top');
  const xs = kd.map(k => tip(k).x).sort((p, q) => p - q);
  assert(near(xs[0], 0, 1e-3) && near(xs[xs.length - 1], 100, 1e-3), 'full length: from end I (x 0) to end J (x 100)');
  const sh = shaftsOf(d1, 2, 1);
  assert(sh.shafts.every(s => near(s.len, sh.shafts[0].len, 1e-3)) && sh.rods.length === kd.length - 1, 'uniform: equal arrows, a straight line along their tails');
  assert(D.selection().text === 'Case SUS (NonStatic): DEAD \u00d71, LIVE \u00d72; + self weight \u00d71 (DEAD)', 'status: ' + D.selection().text);
}
// 18. a combination with a -1 factor flips the arrows
{
  D.setSelection('k:NEG');
  const p1 = byId('sap-load-2'), k1 = cones(e => e === p1)[0];
  assert(p1.factor === -2 && near(dir(k1).z, 1, 1e-4) && P3(k1, 40, 0, -1), 'NEG = -OPE: LIVE x-2 (carried from SUS) turns the point load up, its tip under the pipe');
  const d1 = byId('sap-load-1');
  assert(d1.factor === -1 && cones(e => e === d1).every(k => near(dir(k).z, 1, 1e-4) && near(tip(k).z, -1, 1e-3)), 'DEAD x-1: the distributed row points up');
  assert(!!byId('sap-load-8') && /OPE includes SUS/.test(D.selection().text), 'T1 x-1 from OPE; status: ' + D.selection().text);
  D.setSelection('k:EXP');
  assert(ents().length > 0 && ents().every(e => e.item.pattern === 'T1' && e.factor === 1), 'EXP = OPE - SUS draws T1 alone');
  D.setSelection('k:ENV');
  assert(ents().every(e => Math.abs(e.factor) === 1) && !!byId('sap-load-1') && !!byId('sap-load-2') && !!byId('sap-load-8') && !byId('sap-load-5'),
         'envelope: the union drawn x+-1 (DEAD, LIVE, T1; not W)');
}
// 19. a trapezoid: partial, projected, lengths linear in the value, a line through the tails; local 2 from the local y
{
  D.setSelection('p:W');
  const tw = byId('sap-load-5'), kt = cones(e => e === tw);
  assert(kt.length >= 3 && kt.every(k => near(dir(k).x, 1, 1e-4) && near(tip(k).x, 99, 1e-3) && near(tip(k).y, 0, 1e-3)),
         'X Proj on the vertical beam 12: ' + kt.length + ' arrows along +X, tips on the pipe surface (x 99)');
  const zs = kt.map(k => tip(k).z).sort((p, q) => p - q);
  assert(near(zs[0], 25, 1e-3) && near(zs[zs.length - 1], 75, 1e-3), 'partial: from rel 0.25 (z 25) to rel 0.75 (z 75)');
  const sh = shaftsOf(tw, 0, 99);
  const rows = sh.shafts.sort((p, q) => p.tail.z - q.tail.z), L0 = rows[0].len;
  assert(rows.length === kt.length && rows.every(s => near(s.len / L0, (0.1 + 0.1 * (s.tail.z - 25) / 50) / 0.1, 1e-3)),
         'lengths go linearly from valueA 0.1 to valueB 0.2: ' + rows.map(s => s.len.toFixed(3)).join(' '));
  assert(near(rows[rows.length - 1].len / L0, 2, 1e-3), 'the arrow at valueB is twice the one at valueA');
  const onTail = p => rows.some(s => s.tail.distanceTo(p) < 1e-3);
  assert(sh.rods.length === rows.length - 1 && sh.rods.every(r => onTail(r[0]) && onTail(r[1])), 'a line through the tails: ' + sh.rods.length + ' rods, tail to tail (a trapezoid)');
  assert(D._text(tw) === '0.1\u21920.2 kip/in X Proj', 'trapezoid label: ' + D._text(tw));
  const lw = byId('sap-load-6'), kl = cones(e => e === lw);
  assert(kl.length >= 3 && kl.every(k => near(dir(k).z, -1, 1e-4) && near(tip(k).z, 1, 1e-3) && near(tip(k).x, 0, 1e-3)),
         'local 2 on beam 13 follows its BPRP local y (+X): 2 = x cross y = -Z, arrows down onto the pipe top');
  D.setSelection('p:LOC');
  const p3 = byId('sap-load-7'), k3 = cones(e => e === p3)[0];
  assert(near(dir(k3).x, -1, 1e-4) && P3(k3, 1, 50, 0), 'local 3 = minus local y (-X): the arrow pushes on the +X side of beam 13 at rel 0.5');
}
// 20. temperature bands: red / blue, labelled, not pickable; a -1 factor turns hot to cold
{
  D.setSelection('p:T1');
  const sl = meshes().sleeve;
  assert(sl && sl.mesh.count === 2, 'temperature: one band per loaded frame');
  const kOf = en => { for (let k = 0; k < sl.owner.length; k++) if (ents()[sl.owner[k]] === en) return k; return -1; };
  const hot = byId('sap-load-8'), cold = byId('sap-load-9'), kh = kOf(hot), kc = kOf(cold);
  const c0 = inst('sleeve', kh, [0, 0, 0]), e0 = inst('sleeve', kh, [0, -0.5, 0]), e1 = inst('sleeve', kh, [0, 0.5, 0]), rim = inst('sleeve', kh, [1, 0, 0]);
  assert(near(c0.x, 50, 1e-3) && near(Math.min(e0.x, e1.x), 0, 1e-3) && near(Math.max(e0.x, e1.x), 100, 1e-3) && near(c0.y, 0, 1e-3) && near(c0.z, 0, 1e-3),
         'the band runs the whole of beam 11, on its axis');
  const radial = Math.hypot(rim.y - c0.y, rim.z - c0.z);
  assert(radial > 1 && radial < 3, 'the band sits just outside the pipe (R 1): radius ' + radial.toFixed(3));
  const col = new THREE.Color();
  sl.mesh.getColorAt(kh, col);
  const hr = col.r, hb = col.b;
  sl.mesh.getColorAt(kc, col);
  assert(hr > hb && col.b > col.r, 'dT > 0 red, dT < 0 blue');
  assert(sl.mesh.material.transparent && sl.mesh.material !== meshes().cone.mesh.material, 'the band is see-through');
  const labs = FEALabels._layers().loads.map(l => l.text);
  assert(labs.indexOf('\u0394T 230 F') >= 0 && labs.indexOf('\u0394T -40 F') >= 0, 'labels: ' + labs.join(' | '));
  const rc = new THREE.Raycaster(new THREE.Vector3(70, 0, 50), new THREE.Vector3(0, 0, -1));
  assert(D.pick(rc) === null, 'a band does not take the pick from its member');
  D._set({ kinds: { temperature: false } });
  assert(!meshes().sleeve, 'Temperature unticked: no bands');
  D._set({ kinds: { temperature: true } });
  D.setSelection('k:NEG');
  const hn = byId('sap-load-8'), g1 = byId('sap-load-10'), kg = cones(e => e === g1)[0];
  assert(hn.dT === -230 && D._text(hn) === '\u0394T -230 F' && rgbBlue(hn), 'NEG = -OPE: T1 x-1, the hot band reads -230 and turns blue');
  assert(near(dir(kg).z, 1, 1e-4) && tip(kg).z > 101, 'and the ground displacement at node 3 flips up');
}
function rgbBlue(en) { const sl = meshes().sleeve, col = new THREE.Color(); for (let k = 0; k < sl.owner.length; k++) if (ents()[sl.owner[k]] === en) { sl.mesh.getColorAt(k, col); return col.b > col.r; } return false; }
// 21. itemsAt and itemText (the properties card)
{
  assert(D.itemsAt(null, 11).map(i => i.id).sort().join() === 'sap-load-1,sap-load-2,sap-load-8', 'itemsAt(null, beam 11): its distributed, point and temperature loads, whatever the pick');
  assert(D.itemsAt(null, 13).map(i => i.id).sort().join() === 'sap-load-3,sap-load-6,sap-load-7', 'itemsAt(null, beam 13)');
  assert(D.itemsAt(2, 0).map(i => i.id).join() === 'sap-load-4', 'itemsAt(node 2): the joint force');
  const IT = id => D.itemText(SAP.items.find(i => i.id === id));
  const cases = [
    ['sap-load-2', 'B11 LIVE: point 0.85 kip Gravity at 0.4 (40 in)'],
    ['sap-load-3', 'B13 LIVE: point moment 7.5 kip-in local 2 at 0.25 (25 in)'],
    ['sap-load-5', 'B12 W: trapezoid 0.1\u21920.2 kip/in X Proj from 0.25 to 0.75'],
    ['sap-load-1', 'B11 DEAD: uniform 0.002 kip/in Gravity'],
    ['sap-load-8', 'B11 T1: \u0394T 230 F'],
    ['sap-load-4', '2 LIVE: force FZ -0.5 kip'],
    ['sap-load-10', '3 T1: displacement UZ -0.125 in']
  ];
  cases.forEach(c => assert(IT(c[0]) === c[1], 'itemText: ' + IT(c[0])));
}
// 22. the pick remembered per model; storage blocked is harmless
{
  D.setSelection('c:SUS');
  FEAFeatures.setEnvelope(senv(), 'frame.features.json');
  assert(D.selection().value === 'c:SUS' && byId('sap-load-2').factor === 2, 'reloading the same model comes back on case SUS');
  FEAFeatures.setEnvelope(senv(), 'other.features.json');
  assert(D.selection().value === 'all', 'another model starts at All patterns');
  store['pluto.ldPick.other.features.json|h'] = 'k:GONE';
  FEAFeatures.setEnvelope(senv(), 'other.features.json');
  assert(D.selection().value === 'all', 'a remembered pick the model no longer has falls back to All patterns');
  const keep = global.localStorage;
  global.localStorage = { getItem() { throw new Error('blocked'); }, setItem() { throw new Error('blocked'); } };
  let threw = false;
  try { FEAFeatures.setEnvelope(senv(), 'frame.features.json'); D.setSelection('p:LIVE'); } catch (e) { threw = true; }
  assert(!threw && D.selection().value === 'p:LIVE' && ents().length === 3, 'storage blocked: no error, the pick still applies');
  global.localStorage = keep;
}
// 23. the build's frames win over BPRP (they are what the beam mesh drew)
{
  const fr = Float32Array.from([1, 0, 0, 0, 1, 0, 0, 0, 1,   0, 0, 1, 0, -1, 0, 1, 0, 0,   0, 1, 0, 0, 0, 1, 1, 0, 0]);
  FEABeams.build = () => Object.assign(frameBuild(), { frames: fr });
  FEALoads.onModelLoaded();
  FEAFeatures.setEnvelope(senv(), 'frames.features.json');
  D.setSelection('p:LOC');
  const k3 = cones(e => e === byId('sap-load-7'))[0];
  assert(near(dir(k3).z, -1, 1e-4) && P3(k3, 0, 50, 1), 'beam 13 with frame y +Z: local 3 = -Z, the arrow pushes down onto the pipe top');
  FEABeams.build = frameBuild;
}
// 24. round trip, then the version-1 sidecar again
{
  const out = JSON.parse(FEAFeatures.exportJson());
  assert(JSON.stringify(out.loads) === JSON.stringify(SAP), 'the version-2 section exports exactly as loaded');
  FEABeams.view = v1View; FEABeams.build = v1Build;
  FEALoads.onModelLoaded();
  FEAFeatures.setEnvelope(env(), 't.features.json');
  const again = ents().map(e => e.kind + (e.rot ? 'R' : '') + e.v);
  assert(again.join(' ') === kinds.join(' ') && !D.selection().v2 && !ents().some(e => e.v2 || e.form), 'back on the version-1 sidecar: the same eight entries, no select');
  const f1 = ents().find(e => e.kind === 'force' && e.v === 1), k = cones(e => e === f1)[0];
  assert(P3(k, 100, 1, 0) && D._text(f1) === 'F1 Y -1000 lb.', 'and F1 draws and reads as before');
}

console.log(fails ? fails + ' FAILED' : 'all passed');
process.exit(fails ? 1 : 0);
