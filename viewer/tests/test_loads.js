// Headless tests for loads.js (applied loads from the sidecar `loads` section): items -> entries per
// vector (forces and moments apart, displacements and rotations, uniform rows, wind), where the arrows
// sit and which way they point (a force pushing on the pipe, a moment / rotation double-headed out from
// it, a uniform row along the beams with tips on the surface, wind from the side picked), lengths by
// magnitude, the type / vector filters, labels, picking, the readout text, itemsAt, the section-cut mask,
// and the section round-tripping untouched. THREE under vm; no DOM.
//   node viewer/tests/test_loads.js
const fs = require('fs'), path = require('path'), vm = require('vm');
const V = path.join(__dirname, '..', 'scripts') + path.sep;
const L = path.join(__dirname, '..', 'lib') + path.sep;

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

console.log(fails ? fails + ' FAILED' : 'all passed');
process.exit(fails ? 1 : 0);
