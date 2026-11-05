// Headless tests for links.js (two-joint links from the sidecar `links`
// section): items -> entries (joints, type, midpoint, local axes, zero
// length), the symbol per type and its orientation from axis1 / axis2,
// zero-length links at their joint, sizes from the members at the joints,
// the ghost pass for a link buried in its members, the lines, the text /
// readout / pin card, picking, visibility (hidden, section-cut mask, Show)
// and the section round-tripping untouched. THREE under vm; no DOM.
//   node viewer/tests/test_links.js
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

// Two PIPE od 4 beams (R = 2): 1 (0,0,0) -> 2 (0,0,10) and 3 (0,0,13) -> 4 (10,0,13); the other joints carry links only.
const P = [[1, 0, 0, 0], [2, 0, 0, 10], [3, 0, 0, 13], [4, 10, 0, 13], [10, 100, 0, 0], [11, 112, 0, 0], [12, 100, 20, 0], [13, 100, 20, 0],
           [14, 200, 0, 0], [15, 200, 0, 12], [16, 100, 40, 0], [17, 112, 40, 0], [18, 100, 60, 0], [19, 112, 60, 0],
           [20, 100, 80, 0], [21, 112, 80, 0], [22, 100, 100, 0], [23, 112, 100, 0]];
const NODES = Float64Array.from(P.flatMap(p => p.slice(1)));
const IDS = Uint32Array.from(P.map(p => p[0]));
const idx = id => P.findIndex(p => p[0] === id);
const elems = new Uint32Array(2 * 6);
elems.set([0, idx(1), idx(2), 0, 0, 0], 0); elems.set([0, idx(3), idx(4), 0, 0, 0], 6);
const beamView = {
  header: { nElements: 2, cornerComponents: 1, maxCorners: 2 }, meta: { components: [{ name: 'DX', kind: 'displacement', unit: 'in' }], dispVector: null },
  domain: { fields: null }, nodes: NODES, nodeIds: IDS, nodeLabels: null,
  elems: elems, elemRecordU32: 6, elemIds: Uint32Array.from([100, 200]),
  sections: [{ name: 'P4', type: 'PIPE', params: { od: 4, t: 0.2 } }]
};
global.FEABeams = {
  view: () => beamView,
  build: () => ({ sectionOf: Int32Array.from([0, 0]), sectionOfB: Int32Array.from([0, 0]), geometry: null }),
  mesh: () => null, lcData: () => null
};
global.feaModel = {
  header: { nElements: 0, nNodes: IDS.length }, nodeIds: IDS, nodes: NODES, elemIds: new Uint32Array(0),
  unified: { domains: [{ name: 'beams', family: 'beam' }], geometryHash: 'h' }
};
vm.runInThisContext(fs.readFileSync(V + 'features.js', 'utf8'), { filename: 'features.js' });
vm.runInThisContext(fs.readFileSync(V + 'links.js', 'utf8'), { filename: 'links.js' });

const X = [1, 0, 0], Y = [0, 1, 0], Z = [0, 0, 1];
const item = (label, type, ids, axis1, axis2, extra) => Object.assign({ id: 'sap-link-' + label, name: label + ' PR_' + label, label: label, type: type,
  linkType: type.charAt(0).toUpperCase() + type.slice(1), nodeIds: ids, property: 'PR_' + label, axis1: axis1, axis2: axis2, dofs: {}, tags: ['sap', 'link'] }, extra || {});
const LINKS = { version: 1, items: [
  item('K1', 'gap', [10, 11], X, Z, { length: 12, property: 'GAPP', name: 'K1 GAPP', dofs: { U1: { fixed: false, nonlinear: true, ke: 100, k: 200, open: 0.5 } } }),
  item('K2', 'linear', [12, 13], Y, X, { length: 0, dofs: { U1: { fixed: false, nonlinear: false, ke: 111, ce: 0 }, U2: { fixed: true, nonlinear: false } } }),
  item('K3', 'hook', [14, 15], Z, X, { length: 12 }),
  item('K4', 'friction', [16, 17], X, Z, { length: 12, linkType: 'Friction Isolator',
    dofs: { U1: { fixed: false, nonlinear: true, ke: 1000, k: 1001 }, U2: { fixed: false, nonlinear: true, ke: 10, k: 11, muSlow: 0.3, muFast: 0.4, radius: 0 } } }),
  item('K5', 'multilinear', [18, 19], X, Z, { length: 12 }),
  item('K6', 'damper', [20, 21], X, Z, { length: 12 }),
  item('K7', 'weird', [22, 23], X, Z, { length: 12, linkType: 'Something Else' }),
  item('K8', 'linear', [2, 3], Z, X, { length: 3 }),
  item('K9', 'gap', [10, 11], X, Z, { length: 12, hidden: true }),
  item('K10', 'gap', [99, 98], X, Z, { length: 1 })
] };
const env = () => ({
  format: 'pluto-features', version: 1, model: { geometryHash: 'h', units: { length: 'in', force: 'kip' } },
  groups: { version: 1, items: [] },
  links: JSON.parse(JSON.stringify(LINKS))
});

FEALinks.onModelLoaded();
FEAFeatures.setEnvelope(env(), 'k.features.json');
scene.updateMatrixWorld(true);
const S = FEALinks;
const E = () => S._entries();
const byLabel = l => E().find(e => e.item.label === l);
const parts = l => S._parts(byLabel(l));
const axisOf = (pt, local) => new THREE.Vector3(local[0], local[1], local[2]).applyQuaternion(pt.quat);

// 1. parsing
{
  assert(E().length === 9, 'nine entries: K10 (joints off the model) is skipped: ' + E().length);
  assert(S.count() === 8, 'eight drawn: the hidden K9 is not');
  assert(E().map(e => e.type).join() === 'gap,linear,hook,friction,multilinear,damper,other,linear,gap', 'types: an unknown type draws as other');
  const k1 = byLabel('K1');
  assert(near(k1.C.x, 106) && near(k1.C.y, 0) && near(k1.C.z, 0) && near(k1.len, 12) && !k1.zero, 'K1: midpoint (106, 0, 0), length 12');
  assert(near(k1.a1.x, 1) && near(k1.a2.z, 1) && near(k1.a3.y, -1), 'K1: local 1 / 2 from axis1 / axis2, local 3 = 1 x 2');
  const k2 = byLabel('K2');
  assert(k2.zero && k2.len === 0 && near(k2.C.x, 100) && near(k2.C.y, 20), 'K2: zero length (coincident joints), placed at the joint');
}

// 2. symbols, sizes and orientation (span 200: no beams at a joint -> u = 0.5; K8 between two R = 2 pipes -> u = 1.5)
{
  const s1 = S._sizes(byLabel('K1')), s8 = S._sizes(byLabel('K8'));
  assert(near(s1.u, 0.5) && near(s1.h, 0.5) && near(s1.w, 0.5), 'K1 in the open: the span floor, u = h = w = 0.5');
  assert(near(s8.u, 1.5) && near(s8.h, 1.35) && near(s8.w, 1.5), 'K8 between pipes: u = 3/4 R = 1.5, h kept inside the link (0.45 x 3)');
  const g = parts('K1');
  assert(g.length === 2 && g.every(p => p.prim === 'box' && near(Math.abs(axisOf(p, [0, 1, 0]).x), 1) && near(p.scale.x, 1) && near(p.scale.y, 0.05)),
         'gap: two plates (1 x 1, 0.1u thick) square to local 1');
  const gx = g.map(p => p.pos.x).sort((a, b) => a - b);
  assert(near(gx[0], 105.85) && near(gx[1], 106.15), 'gap: the plates face each other across the midpoint, a gap between: ' + gx);
  const c = parts('K2');
  assert(c.length === 1 && c[0].prim === 'coil' && near(c[0].pos.x, 100) && near(c[0].pos.y, 20) && near(Math.abs(axisOf(c[0], [0, 1, 0]).y), 1),
         'zero-length linear: a coil at the joint, along axis1 (+Y)');
  const h = parts('K3');
  assert(h.length === 2 && h.every(p => p.prim === 'arc') && near(h[0].pos.z, 5.85) && near(h[1].pos.z, 6.15), 'hook: two C hooks either side of the midpoint (z 5.85 / 6.15)');
  assert(near(axisOf(h[0], [1, 0, 0]).z, 1) && near(axisOf(h[1], [1, 0, 0]).z, -1), 'hook: their closed sides face each other along local 1');
  assert(Math.abs(axisOf(h[0], [0, 0, 1]).dot(axisOf(h[1], [0, 0, 1]))) < 1e-6, 'hook: in planes square to each other, so they catch');
  const f = parts('K4');
  assert(f.filter(p => p.prim === 'box').length === 2 && f.filter(p => p.prim === 'rod').length === 4, 'friction: a plate, a block on it, four hatch strokes');
  assert(f.filter(p => p.prim === 'rod').every(p => p.pos.x < 106), 'friction: the hatching on the -local 1 side of the plate');
  const z = parts('K5');
  assert(z.length === 6 && z.every(p => p.prim === 'rod'), 'multilinear: a zig-zag of six strokes');
  const d = parts('K6');
  assert(d.filter(p => p.prim === 'cup').length === 2 && d.filter(p => p.prim === 'rod').length === 1, 'damper: a pot, a plate, a rod');
  const o = parts('K7');
  assert(o.length === 1 && o[0].prim === 'oct', 'other: a diamond');
}

// 3. instances, the ghost of the buried link, the lines
{
  const m = S._meshes();
  assert(m.coil.mesh.count === 2 && m.coil.ghost && m.coil.ghost.count === 1 && m.coil.owner[0] === E().indexOf(byLabel('K8')),
         'K8 (shorter than the pipes at its joints reach) is first in the coil list and the only one ghosted');
  assert(!m.box.ghost && !m.rod.ghost, 'links in the open have no ghost');
  assert(m.coil.ghost.material.depthTest === false && m.coil.ghost.material.transparent, 'the ghost draws on top, faint');
  const c = new THREE.Color(); m.box.mesh.getColorAt(0, c);
  assert(near(c.g, 160 / 255, 1e-3), 'a gap link in its type colour (green)');
  const ln = S._line();
  assert(ln && ln.geometry.getAttribute('position').count === 14, 'one line per drawn link with a length (7)');
}

// 4. text, readout, pin card
{
  const t0 = S.text(0), t3 = S.text(3);
  assert(t0 === 'K1 GAPP: gap, property GAPP, length 12 in; U1 nonlinear, ke 100, k 200, open 0.5', 'text(0): ' + t0);
  assert(t3.indexOf('friction (Friction Isolator)') > 0 && t3.indexOf('U2 nonlinear, ke 10, k 11, \u03bc slow 0.3, \u03bc fast 0.4, radius 0') > 0, 'text(3): the type, SAP\'s name and the friction values');
  assert(S.text(1).indexOf('zero length; U1 linear, ke 111, ce 0; U2 fixed') > 0, 'text(1): zero length, a fixed DOF');
  assert(S.text(99) === '', 'text of no item: ""');
  const ro = {};
  ['elRoValue', 'elRoComp', 'elRoElem', 'elRoNode', 'elRoCorners', 'elRoUV', 'elRoPos'].forEach(k => { global[k] = ro[k] = { textContent: '', className: '' }; });
  global.roPosText = () => 'xyz';
  S.fillReadout({ entry: 0, point: new THREE.Vector3() });
  assert(ro.elRoValue.textContent === 'K1  gap' && ro.elRoNode.textContent === '10 \u2192 11' && ro.elRoCorners.textContent === 'U1 nonlinear, ke 100, k 200, open 0.5',
         'readout: label and type, the joints, the DOFs');
  const rows = [], subs = [];
  global.elCalcCard = { style: {}, innerHTML: '', appendChild() {} };
  global.ccSub = (h, t) => subs.push(t);
  global.ccRow = (t, a, b) => rows.push(a + '=' + b);
  S.showPinCard({ entry: 3 });
  assert(subs.join('|') === 'Link|DOFs [kip, in]' && rows.includes('U1=nonlinear, ke 1000, k 1001') && rows.includes('type=friction (Friction Isolator)'),
         'pin card: the link, then every DOF in the model\'s units');
}

// 5. picking: a ray down onto K1's far plate
{
  scene.updateMatrixWorld(true);
  const hit = S.pick(new THREE.Raycaster(new THREE.Vector3(106.15, 0, 10), new THREE.Vector3(0, 0, -1)));
  assert(hit && hit.link && E()[hit.entry].item.label === 'K1' && near(hit.distance, 9.5, 1e-3) && near(hit.slack, 1), 'pick: K1, its plate at z 0.5, slack 2u');
  assert(!S.pick(new THREE.Raycaster(new THREE.Vector3(150, 0, 10), new THREE.Vector3(0, 0, -1))), 'pick: nothing between links');
}

// 6. visibility: section-cut mask (either joint kept), Show
{
  const keep = new Uint8Array(IDS.length); keep[idx(10)] = 1;
  S.writeVis(keep);
  assert(S.count() === 1 && E()[S._meshes().box.owner[0]].item.label === 'K1', 'mask keeping joint 10: K1 alone (one kept joint is enough)');
  S.writeVis(null);
  assert(S.count() === 8, 'mask cleared: 8 drawn');
  S._set({ visible: false });
  assert(S.count() === 0 && !S.pick(new THREE.Raycaster(new THREE.Vector3(106.15, 0, 10), new THREE.Vector3(0, 0, -1))), 'Show off: nothing drawn, nothing picked');
  S._set({ visible: true });
  S._set({ size: 2 });
  assert(near(S._sizes(byLabel('K1')).u, 1) && near(parts('K1')[0].scale.x, 2), 'size 2: the unit doubles');
  S._set({ size: 1 });
}

// 7. the section round-trips verbatim; a sidecar without links draws nothing
{
  const out = JSON.parse(FEAFeatures.exportJson());
  assert(JSON.stringify(out.links) === JSON.stringify(LINKS), 'links section exported unchanged');
  FEAFeatures.setEnvelope({ format: 'pluto-features', version: 1, model: { geometryHash: 'h' }, groups: { version: 1, items: [] } }, 'none.features.json');
  assert(E().length === 0 && S.count() === 0 && !S._meshes(), 'no links section: no symbols');
  FEALinks.onModelCleared();
  assert(E().length === 0 && !S._meshes(), 'model cleared: symbols gone');
}

console.log(fails ? fails + ' FAILED' : 'ALL LINK TESTS PASSED');
process.exitCode = fails ? 1 : 0;
