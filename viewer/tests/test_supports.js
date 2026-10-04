// Headless tests for supports.js (support symbols from the sidecar `supports`
// section): sidecar items -> symbol instances per restraint kind, where the
// arrows / plates / collars sit, colours from the node groups / restraint kind /
// a restraint-load component read from the beam plane, visibility (item hidden,
// unticked node group, section-cut mask), deflection lines, picking, and the
// section round-tripping untouched. THREE under vm; no DOM.
//   node viewer/tests/test_supports.js
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
global.fmt = (v) => String(v);

let fails = 0;
const assert = (c, m) => { if (!c) { fails++; console.error('FAIL ' + m); } else console.log('ok   ' + m); };
const near = (a, b, t = 1e-5) => Math.abs(a - b) <= t;

// A straight pipe along +X: nodes 1 (0,0,0), 2 (10,0,0), 3 (20,0,0); beams 1-2, 2-3; PIPE od 2 (R = 1).
const NODES = Float64Array.from([0, 0, 0, 10, 0, 0, 20, 0, 0]);
const COMPS = [
  { name: 'DX', kind: 'displacement', unit: 'in' }, { name: 'DY', kind: 'displacement', unit: 'in' },
  { name: 'DZ', kind: 'displacement', unit: 'in' }, { name: 'Restraint FY', kind: 'restraint', unit: 'lb' },
  { name: 'Restraint |F|', kind: 'restraint', unit: 'lb' }
];
const CC = COMPS.length, MS = 2;
// plane [beam][end][comp]: displacements at the nodes, |F| 100 at node 1, 50 at node 2, none at node 3
const plane = new Float32Array(2 * MS * CC).fill(NaN);
function setEnd(e, k, vals) { vals.forEach((v, c) => { plane[e * MS * CC + k * CC + c] = v; }); }
setEnd(0, 0, [0, 0, 0, -100, 100]);          // node 1
setEnd(0, 1, [0.1, 0.2, 0.3, -50, 50]);      // node 2 (end B of beam 0) ...
setEnd(1, 0, [0.1, 0.2, 0.3, -50, 50]);      // ... and end A of beam 1
setEnd(1, 1, [0.4, 0.5, 0.6, NaN, NaN]);     // node 3: no restraint load
const elems = new Uint32Array(2 * 6);
elems.set([0, 0, 1, 0, 0, 0], 0); elems.set([0, 1, 2, 0, 0, 0], 6);
const beamView = {
  header: { nElements: 2, cornerComponents: CC, maxCorners: MS },
  meta: { components: COMPS, dispVector: [0, 1, 2] },
  domain: { fields: {} },
  nodes: NODES, nodeIds: Uint32Array.from([1, 2, 3]), nodeLabels: null,
  elems: elems, elemRecordU32: 6, elemIds: Uint32Array.from([100, 200]),
  sections: [{ name: 'P2', type: 'PIPE', params: { od: 2, t: 0.2 } }]
};
global.FEABeams = {
  view: () => beamView,
  build: () => ({ sectionOf: Int32Array.from([0, 0]), sectionOfB: Int32Array.from([0, 0]), geometry: null }),
  mesh: () => null,
  lcData: () => plane
};
global.feaModel = {
  header: { nElements: 0, nNodes: 3 }, nodeIds: beamView.nodeIds, nodes: NODES,
  elemIds: new Uint32Array(0),
  unified: { domains: [{ name: 'beams', family: 'beam' }], geometryHash: 'h' }
};
vm.runInThisContext(fs.readFileSync(V + 'features.js', 'utf8'), { filename: 'features.js' });
vm.runInThisContext(fs.readFileSync(V + 'supports.js', 'utf8'), { filename: 'supports.js' });

const SUPPORTS = { version: 1, items: [
  { id: 'a', name: '1 ANC', nodeIds: [1], dof: { tx: 'fixed', ty: 'fixed', tz: 'fixed', rx: 'fixed', ry: 'fixed', rz: 'fixed' },
    tags: ['caesar', 'support'], restraints: [{ type: 'ANC', kind: 'anchor', axis: [1, 0, 0] }] },
  { id: 'b', name: '2 +Y', nodeIds: [2], dof: { ty: '+' }, tags: ['caesar', 'support'],
    restraints: [{ type: '+Y', kind: 'oneway', direction: [0, 1, 0], sign: '+', axis: [1, 0, 0] }] },
  { id: 'c', name: '3 GUI + LIM + X + RX', nodeIds: [3], dof: {}, tags: ['caesar', 'support'],
    restraints: [{ type: 'GUI', kind: 'guide', direction: [0, 0, 1], axis: [1, 0, 0] },
                 { type: 'LIM', kind: 'limit', direction: [1, 0, 0], axis: [1, 0, 0], gap: 0.25 },
                 { type: 'X', kind: 'translation', direction: [1, 0, 0], axis: [1, 0, 0], cnode: 2 },
                 { type: 'RX', kind: 'rotation', direction: [1, 0, 0], axis: [1, 0, 0] }] },
  { id: 'user-1', name: 'My shoe', nodeIds: [2], dof: { ty: '+' }, tags: ['user'], note: 'kept verbatim', extra: { a: [1, 2] } },
  { id: 'e', name: 'hidden one', hidden: true, nodeIds: [1], dof: { ty: 'fixed' }, tags: ['user'] },
  { id: 'f', name: 'off the model', nodeIds: [99], dof: { tx: 'fixed' }, tags: ['user'] }
] };
const env = () => ({
  format: 'pluto-features', version: 1, model: { geometryHash: 'h' },
  groups: { version: 1, items: [
    { name: 'ANC', color: '#ff0000', hidden: false, tags: ['caesar', 'support'], members: [{ domain: 'nodes', nodeIds: [1] }] },
    { name: '+Y', color: '#00ff00', hidden: false, tags: ['caesar', 'support'], members: [{ domain: 'nodes', nodeIds: [2] }] }
  ] },
  supports: JSON.parse(JSON.stringify(SUPPORTS))
});

FEASupports.onModelLoaded();
FEAFeatures.setEnvelope(env(), 't.features.json');
scene.updateMatrixWorld(true);

const S = FEASupports;
const meshes = () => S._meshes();
const count = p => (meshes()[p] ? meshes()[p].mesh.count : 0);
const instPoint = (p, k, local) => {          // local point of instance k of primitive p, in world space
  const m = new THREE.Matrix4(); meshes()[p].mesh.getMatrixAt(k, m);
  return new THREE.Vector3(local[0], local[1], local[2]).applyMatrix4(m);
};
const instColor = (p, k) => { const c = new THREE.Color(); meshes()[p].mesh.getColorAt(k, c); return [c.r, c.g, c.b]; };
const ownerName = (p, k) => S._entries()[meshes()[p].owner[k]].name;
const find = (p, name) => { const m = meshes()[p]; const out = []; for (let k = 0; k < m.owner.length; k++) if (ownerName(p, k) === name) out.push(k); return out; };

// 1. items -> entries -> instances
assert(S._entries().length === 5, 'five entries: one per item node on the model (the off-model node 99 is skipped)');
assert(S.count() === 4, 'four drawn: the hidden item is not');
assert(count('box') === 5, 'boxes: anchor 1 + base plates 2 (two one-way arrows) + guide plates 2 = ' + count('box'));
assert(count('cone') === 2 && count('cyl') === 2, 'arrows: two one-way arrows (cone + shaft each)');
assert(count('collar') === 4, 'collars: limit 2 + axial translation 2 = ' + count('collar'));
assert(count('hoop') === 1, 'one rotation hoop');
assert(!meshes().oct && !meshes().ball, 'no imposed / unknown symbols');

// 2. where they sit (R = 1, u = R x size = 1)
{
  const k = find('cone', '2 +Y')[0];
  const tip = instPoint('cone', k, [0, 0.5, 0]), base = instPoint('cone', k, [0, -0.5, 0]);
  assert(near(tip.x, 10) && near(tip.y, -1) && near(tip.z, 0), 'one-way +Y: arrow tip on the pipe bottom (10,-1,0): ' + tip.toArray().map(v => v.toFixed(3)));
  assert(base.y < tip.y, 'one-way +Y: the arrow points up (pushes the pipe +Y)');
  const kc = find('cyl', '2 +Y')[0], c = instPoint('cyl', kc, [0, 0, 0]);
  assert(near(c.x, 10) && c.y < -1.75 && near(c.z, 0), 'one-way +Y: shaft below the head');
}
{
  const ks = find('box', '3 GUI + LIM + X + RX');
  const zs = ks.map(k => instPoint('box', k, [0, 0, 0]).z).sort((a, b) => a - b);
  assert(ks.length === 2 && near(zs[0], -1.11) && near(zs[1], 1.11), 'guide: plates either side across the pipe (z = -/+1.11): ' + zs);
  const thick = instPoint('box', ks[0], [0.5, 0, 0]).sub(instPoint('box', ks[0], [-0.5, 0, 0]));
  assert(near(Math.abs(thick.z), 0.22) && near(thick.x, 0) && near(thick.y, 0), 'guide plate: its thickness runs along the guide direction');
  const len = instPoint('box', ks[0], [0, 0, 0.5]).sub(instPoint('box', ks[0], [0, 0, -0.5]));
  assert(near(Math.abs(len.x), 1.8), 'guide plate: its length runs along the pipe');
  const xs = find('collar', '3 GUI + LIM + X + RX').map(k => instPoint('collar', k, [0, 0, 0]).x).sort((a, b) => a - b);
  assert(xs.length === 4 && near(xs[0], 19.1) && near(xs[1], 19.55) && near(xs[2], 20.45) && near(xs[3], 20.9),
         'collars on the pipe axis: limit wide (+/-0.9), axial translation close (+/-0.45): ' + xs);
  const ring = instPoint('collar', 0, [1, 0, 0]).sub(instPoint('collar', 0, [0, 0, 0]));
  assert(near(ring.x, 0) && near(ring.length(), 1.3), 'collar ring lies square to the pipe, radius R + 0.3u');
}
{
  const k = find('box', '1 ANC')[0], m = new THREE.Matrix4(); meshes().box.mesh.getMatrixAt(k, m);
  const p = new THREE.Vector3(), q = new THREE.Quaternion(), s = new THREE.Vector3(); m.decompose(p, q, s);
  assert(near(p.x, 0) && near(p.y, 0) && near(s.x, 2.9) && near(s.y, 2.9), 'anchor: a 2R + 0.9u cube centred on the node');
}

// 3. colours: node group, else restraint kind
const red = instColor('box', find('box', '1 ANC')[0]);
assert(near(red[0], 1) && near(red[1], 0) && near(red[2], 0), 'anchor in its node group colour (#ff0000)');
const green = instColor('cone', find('cone', 'My shoe')[0]);
assert(near(green[1], 1) && near(green[0], 0), 'a hand-made item at a grouped node takes that group colour');
const teal = instColor('box', find('box', '3 GUI + LIM + X + RX')[0]);
assert(near(teal[0], 0 / 255) && near(teal[1], 137 / 255) && near(teal[2], 123 / 255), 'a node no group lists: the restraint kind colour (guide teal)');
S._set({ colorMode: 'kind' });
const kindRed = instColor('box', find('box', '1 ANC')[0]);
assert(near(kindRed[0], 229 / 255) && near(kindRed[1], 57 / 255), 'colour by restraint kind: the anchor in the kind red');

// 4. colour by a restraint load (beam plane, current LC)
S._set({ colorMode: 'c4' });
const lr = S._loadRange();
assert(lr && !lr.empty && lr.min === 50 && lr.max === 100 && lr.unit === 'lb', 'load range over the drawn supports: |F| 50..100 lb');
const anchors = FEAShaders.colormaps.turbo;
const top = FEAShaders.sampleAnchors(anchors, 1), bot = FEAShaders.sampleAnchors(anchors, 0);
const ca = instColor('box', find('box', '1 ANC')[0]), cb = instColor('cone', find('cone', '2 +Y')[0]);
assert(near(ca[0], top[0] / 255, 1e-3) && near(ca[2], top[2] / 255, 1e-3), 'node 1 (|F| 100) at the top of the colormap');
assert(near(cb[0], bot[0] / 255, 1e-3) && near(cb[2], bot[2] / 255, 1e-3), 'node 2 (|F| 50) at the bottom');
const cn = instColor('hoop', 0);
assert(near(cn[0], 0.35) && near(cn[1], 0.35), 'node 3 has no load: neutral grey');
global.absValue = true; S._set({ colorMode: 'c3' });
assert(S._loadRange().min === 50 && S._loadRange().max === 100, 'abs value applies to the load colouring (FY -100 / -50 -> 50..100)');
global.absValue = false; S._set({ colorMode: 'c3' });
assert(S._loadRange().min === -100 && S._loadRange().max === -50, 'signed FY without abs: -100..-50');
global.currentEnvelope = 'max'; S._set({ colorMode: 'c4' });
assert(S._loadRange().empty, 'an envelope view has no load data for the supports');
global.currentEnvelope = null; S._set({ colorMode: 'group' });
assert(S._loadRange() === null, 'group colouring has no load range');

// 5. deflection lines: one per drawn support, node -> displaced node
{
  const ln = S._lines(), dv = ln.geometry.getAttribute('dispVec').array, pos = ln.geometry.getAttribute('position').array;
  assert(ln.geometry.getAttribute('position').count === 8, 'four deflection lines (two vertices each)');
  let found = false;
  for (let i = 0; i < 4; i++) if (pos[i * 6] === 10 && near(dv[i * 6 + 3], 0.1) && near(dv[i * 6 + 4], 0.2) && near(dv[i * 6 + 5], 0.3) && dv[i * 6] === 0) found = true;
  assert(found, 'node 2 line: from the node (no displacement) to the node + (0.1, 0.2, 0.3) x scale');
  assert(!ln.visible, 'lines hidden while the deformed shape is off');
  global.deform = { enabled: true }; S.setDispScale(50);
  assert(ln.visible && ln.material.uniforms.dispScale.value === 50, 'deformed shape on: lines shown at the deformation scale');
  global.deform = { enabled: false }; S.setDispScale(0);
}
{
  const cl = S._cnodeLines();              // the X restraint at node 3 names cnode 2, 10 away
  const p = cl ? cl.geometry.getAttribute('position').array : [];
  assert(cl && p.length === 6 && p[0] === 20 && p[3] === 10, 'CNODE apart from its node: one line node 3 -> node 2');
}

// 6. picking: a ray down onto node 1 hits the anchor cube
{
  scene.updateMatrixWorld(true);
  const rc = new THREE.Raycaster(new THREE.Vector3(0, 10, 0), new THREE.Vector3(0, -1, 0));
  const hit = S.pick(rc);
  assert(hit && hit.support && S._entries()[hit.entry].name === '1 ANC' && near(hit.distance, 10 - 1.45), 'pick: the anchor cube under the ray, distance 10 - 1.45');
  const miss = S.pick(new THREE.Raycaster(new THREE.Vector3(5, 10, 0), new THREE.Vector3(0, -1, 0)));
  assert(!miss, 'pick: nothing between supports');
}

// 7. visibility: an unticked node group hides its supports; the section-cut mask too
const e1 = FEAFeatures.envelope();
e1.groups.items[1].hidden = true;
FEAFeatures.refresh();
assert(S.count() === 2 && count('cone') === 0, 'node group "+Y" unticked: both supports at node 2 hide (2 drawn, no arrows)');
e1.groups.items[1].hidden = false;
FEAFeatures.refresh();
assert(S.count() === 4, 'ticked again: 4 drawn');
S.writeVis(Uint8Array.from([1, 0, 0]));
assert(S.count() === 1 && count('box') === 1, 'section-cut mask keeping node 1 only: the anchor alone');
S.writeVis(null);
assert(S.count() === 4, 'mask cleared: 4 drawn');
S._set({ visible: false });
assert(S.count() === 0 && !S.pick(new THREE.Raycaster(new THREE.Vector3(0, 10, 0), new THREE.Vector3(0, -1, 0))), 'Show off: nothing drawn, nothing picked');
S._set({ visible: true });

// 8. size follows the slider, the offset from the pipe does not
S._set({ size: 2 });
{
  const k = find('cone', '2 +Y')[0];
  const tip = instPoint('cone', k, [0, 0.5, 0]), base = instPoint('cone', k, [0, -0.5, 0]);
  assert(near(tip.y, -1) && near(tip.y - base.y, 1.5), 'size 2: the arrow head doubles (0.75 -> 1.5), the tip stays on the pipe');
}
S._set({ size: 1 });

// 9. gaps leave a space: a +Y with a gap starts 0.35u below the pipe
{
  const env2 = env();
  env2.supports.items[1].restraints[0].gap = 0.1;
  FEAFeatures.setEnvelope(env2, 't2.features.json');
  const k = find('cone', '2 +Y')[0];
  assert(near(instPoint('cone', k, [0, 0.5, 0]).y, -1.35), 'one-way with a gap: tip 0.35u off the pipe');
}

// 10. the section round-trips verbatim (features.js never edits it; supports.js only reads)
{
  FEAFeatures.setEnvelope(env(), 't.features.json');
  const out = JSON.parse(FEAFeatures.exportJson());
  assert(JSON.stringify(out.supports) === JSON.stringify(SUPPORTS), 'supports section exported unchanged, unknown keys included');
}

// 11. a sidecar without supports: nothing drawn, no error
FEAFeatures.setEnvelope({ format: 'pluto-features', version: 1, model: { geometryHash: 'h' }, groups: { version: 1, items: [] } }, 'none.features.json');
assert(S._entries().length === 0 && S.count() === 0 && !meshes(), 'no supports section: no symbols');
FEASupports.onModelCleared();
assert(S._entries().length === 0 && !meshes(), 'model cleared: symbols gone');

console.log(fails ? fails + ' FAILED' : 'ALL SUPPORT TESTS PASSED');
process.exitCode = fails ? 1 : 0;
