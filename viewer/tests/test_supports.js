// Headless tests for supports.js (support symbols from the sidecar `supports`
// section, CAESAR's language): sidecar items -> symbol instances per restraint
// kind, where the anchor plate and the arrows sit and which way they point
// (across the pipe, along its top surface, double-headed for a rotation, a
// hanger below the pipe), colours from the node groups / restraint kind /
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
  { id: 'h', name: '2 HGR', nodeIds: [2], dof: { ty: 'hanger' }, tags: ['caesar', 'support'],
    restraints: [{ type: 'HGR', kind: 'hanger', direction: [0, 1, 0], cnode: 0 }] },
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

// 1. items -> entries -> instances (CAESAR's language: a plate for the anchor, arrows for the rest)
assert(S._entries().length === 6, 'six entries: one per item node on the model (the off-model node 99 is skipped)');
assert(S.count() === 5, 'five drawn: the hidden item is not');
assert(count('box') === 1, 'one box: the anchor plate (no other plates)');
// arrows: +Y 1, hand-made +Y 1, hanger 1, guide 2, limit 2, axial X 2, RX 1 (two heads)
assert(count('cone') === 11 && count('cyl') === 10, 'arrows: 10 shafts, 11 heads (the rotation has two): ' + count('cone') + ' / ' + count('cyl'));
assert(!meshes().ball, 'no unknown-kind ball');

// tip (local +0.5 y) and back (local -0.5 y) of a cone instance, and the direction it points
const coneTip = k => instPoint('cone', k, [0, 0.5, 0]);
const coneDir = k => coneTip(k).sub(instPoint('cone', k, [0, -0.5, 0])).normalize();

// 2. where they sit (R = 1, u = R x size = 1, arrow length 2.4u)
{
  const k = find('cone', '2 +Y')[0];
  const tip = coneTip(k), dir = coneDir(k);
  assert(near(tip.x, 10) && near(tip.y, -1) && near(tip.z, 0), 'one-way +Y: arrow tip on the pipe bottom (10,-1,0): ' + tip.toArray().map(v => v.toFixed(3)));
  assert(near(dir.y, 1), 'one-way +Y: the arrow points up (pushes the pipe +Y), below the pipe');
  const kc = find('cyl', '2 +Y')[0], c = instPoint('cyl', kc, [0, 0, 0]);
  assert(near(c.x, 10) && near(c.y, -1 - 0.7 - (2.4 - 0.7) / 2) && near(c.z, 0), 'one-way +Y: shaft below the head');
  const kh = find('cone', '2 HGR')[0];
  assert(near(coneTip(kh).y, -1) && near(coneDir(kh).y, 1), 'hanger: an arrow below the pipe pointing up at it');
}
{
  const name = '3 GUI + LIM + X + RX';
  const cones = find('cone', name).map(k => ({ tip: coneTip(k), dir: coneDir(k) }));
  const guide = cones.filter(c => near(Math.abs(c.dir.z), 1));
  assert(guide.length === 2 && guide.every(c => near(c.tip.x, 20) && near(c.tip.y, 0) && near(Math.abs(c.tip.z), 1) && near(c.dir.z, -Math.sign(c.tip.z))),
         'guide: two arrows across the pipe, tips on its surface (z = -/+1), pointing at it');
  const along = cones.filter(c => near(Math.abs(c.dir.x), 1));
  const tipsX = along.map(c => +c.tip.x.toFixed(4)).sort((a, b) => a - b);
  // limit (gap 0.25 -> 0.35u off): tips 20 -/+ 0.8; axial X: 20 -/+ 0.45; rotation RX: two heads beyond, on the + side
  assert(along.length === 6, 'six heads along the pipe: limit 2, axial X 2, rotation 2: ' + along.length);
  assert(near(tipsX[0], 19.2) && near(tipsX[1], 19.55) && near(tipsX[2], 20.45) && near(tipsX[3], 20.8),
         'along the pipe: limit stops 0.8 off the node (gap), the axial restraint 0.45: ' + tipsX);
  assert(along.filter(c => c.tip.x < 21).every(c => near(c.dir.x, c.tip.x < 20 ? 1 : -1)), 'stops point back at the node from both sides');
  assert(along.filter(c => c.tip.x < 21).every(c => near(c.tip.y, 1.32) && near(c.tip.z, 0)), 'stops run on the top surface (up = Y), heads resting on the pipe');
  const rot = along.filter(c => c.tip.x > 21).sort((a, b) => a.tip.x - b.tip.x);
  assert(rot.length === 2 && near(rot[0].tip.x, 20 + 0.45 + 2.4 + 0.3) && near(rot[1].tip.x - rot[0].tip.x, 0.8 * 0.7 * 0.75) && rot.every(c => near(c.dir.x, -1)),
         'rotation RX: a double-headed arrow at 3/4 size beyond the axial arrows, on the + side: ' + rot.map(c => c.tip.x.toFixed(3)));
}
{
  const k = find('box', '1 ANC')[0], m = new THREE.Matrix4(); meshes().box.mesh.getMatrixAt(k, m);
  const p = new THREE.Vector3(), q = new THREE.Quaternion(), s = new THREE.Vector3(); m.decompose(p, q, s);
  assert(near(p.x, 0) && near(p.y, 0) && near(s.x, 3.6) && near(s.y, 3.6) && near(s.z, 0.14), 'anchor: a 2R + 1.6u square plate, 0.14u thick, centred on the node');
  const n = new THREE.Vector3(0, 0, 1).applyQuaternion(q);
  assert(near(Math.abs(n.x), 1), 'anchor plate square to the pipe: its thin side runs along the pipe axis');
}

// 3. colours: node group, else restraint kind
const red = instColor('box', find('box', '1 ANC')[0]);
assert(near(red[0], 1) && near(red[1], 0) && near(red[2], 0), 'anchor in its node group colour (#ff0000)');
const green = instColor('cone', find('cone', 'My shoe')[0]);
assert(near(green[1], 1) && near(green[0], 0), 'a hand-made item at a grouped node takes that group colour');
const guideCone = find('cone', '3 GUI + LIM + X + RX').filter(k => meshes().cone.kind[k] === 'guide')[0];
const teal = instColor('cone', guideCone);
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
const cn = instColor('cone', find('cone', '3 GUI + LIM + X + RX')[0]);
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
  assert(ln.geometry.getAttribute('position').count === 10, 'five deflection lines (two vertices each)');
  let found = false;
  for (let i = 0; i < 5; i++) if (pos[i * 6] === 10 && near(dv[i * 6 + 3], 0.1) && near(dv[i * 6 + 4], 0.2) && near(dv[i * 6 + 5], 0.3) && dv[i * 6] === 0) found = true;
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

// 6. picking: a ray down onto node 1 hits the anchor plate
{
  scene.updateMatrixWorld(true);
  const rc = new THREE.Raycaster(new THREE.Vector3(0, 10, 0), new THREE.Vector3(0, -1, 0));
  const hit = S.pick(rc);
  assert(hit && hit.support && S._entries()[hit.entry].name === '1 ANC' && near(hit.distance, 10 - 1.8), 'pick: the anchor plate under the ray, distance 10 - 1.8');
  const miss = S.pick(new THREE.Raycaster(new THREE.Vector3(5, 10, 0), new THREE.Vector3(0, -1, 0)));
  assert(!miss, 'pick: nothing between supports');
}

// 7. visibility: an unticked node group hides its supports; the section-cut mask too
const e1 = FEAFeatures.envelope();
e1.groups.items[1].hidden = true;
FEAFeatures.refresh();
assert(S.count() === 2 && count('cone') === 8, 'node group "+Y" unticked: the three supports at node 2 hide (2 drawn, only node 3\'s 8 heads left)');
e1.groups.items[1].hidden = false;
FEAFeatures.refresh();
assert(S.count() === 5, 'ticked again: 5 drawn');
S.writeVis(Uint8Array.from([1, 0, 0]));
assert(S.count() === 1 && count('box') === 1, 'section-cut mask keeping node 1 only: the anchor alone');
S.writeVis(null);
assert(S.count() === 5, 'mask cleared: 5 drawn');
S._set({ visible: false });
assert(S.count() === 0 && !S.pick(new THREE.Raycaster(new THREE.Vector3(0, 10, 0), new THREE.Vector3(0, -1, 0))), 'Show off: nothing drawn, nothing picked');
S._set({ visible: true });

// 8. size follows the slider, the offset from the pipe does not
S._set({ size: 2 });
{
  const k = find('cone', '2 +Y')[0];
  const tip = instPoint('cone', k, [0, 0.5, 0]), base = instPoint('cone', k, [0, -0.5, 0]);
  assert(near(tip.y, -1) && near(tip.y - base.y, 1.4), 'size 2: the arrow head doubles (0.7 -> 1.4), the tip stays on the pipe');
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
