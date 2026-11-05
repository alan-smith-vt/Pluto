// Headless tests for releases.js (member end releases from BPRP slot 7):
// the bit decoding of the five release patterns a SAP probe writes, the
// text ("I: M2 M3; J: fixed"), a float in the slot read as no release,
// which end each symbol sits at and how far in (clear of another member
// at the node), the symbol per DOF and its orientation in the member's
// local axes, partial fixity flagged and coloured, visibility following
// the beams (Show, elemVis), the Size slider, and the readout row.
// THREE and the real beam geometry builder under vm; no DOM.
//   node viewer/tests/test_releases.js
const fs = require('fs'), path = require('path'), vm = require('vm');
const V = path.join(__dirname, '..', 'scripts') + path.sep;
const L = path.join(__dirname, '..', 'lib') + path.sep;

const els = {};
const el = id => (els[id] = els[id] || { id: id, style: {}, textContent: '', value: '', checked: true, addEventListener() {} });
global.window = global; global.self = global;
global.document = { getElementById: id => (id === 'roRelRow' || id === 'roRel' ? el(id) : null) };
global.log = () => {};
global.needsRender = false;
vm.runInThisContext(fs.readFileSync(L + 'three.min.js', 'utf8'), { filename: 'three.min.js' });
vm.runInThisContext(fs.readFileSync(V + 'format/v4Reader.js', 'utf8'), { filename: 'v4Reader.js' });
vm.runInThisContext(fs.readFileSync(V + 'format/v4Writer.js', 'utf8'), { filename: 'v4Writer.js' });
vm.runInThisContext(fs.readFileSync(V + 'beamGeometry.js', 'utf8'), { filename: 'beamGeometry.js' });
global.scene = new THREE.Scene();

let fails = 0;
const assert = (c, m) => { if (!c) { fails++; console.error('FAIL ' + m); } else console.log('ok   ' + m); };
const near = (a, b, t = 1e-4) => Math.abs(a - b) <= t;

// Beams 0-5 along +X, 120 long, at y = 24 e; beam 6 a column up from beam 0's end A.
// Beams 0-5 are PIPE od 8 (outline radius 4), the column a 20 x 20 RECT (outline radius 14.142).
const SECTIONS = [{ name: 'P8', type: 'PIPE', params: { od: 8, t: 0.3 } }, { name: 'C20', type: 'RECT', params: { b: 20, h: 20 } }];
const xyz = [];
for (let e = 0; e < 6; e++) xyz.push(0, 24 * e, 0, 120, 24 * e, 0);
xyz.push(0, 0, 100);                                               // node 12: the column top
const NODES = Float64Array.from(xyz);
const beams = [];
for (let e = 0; e < 6; e++) beams.push({ n0: 2 * e, n1: 2 * e + 1, sec: 0 });
beams.push({ n0: 0, n1: 12, sec: 1 });
const nE = beams.length;
const props = new Float32Array(nE * 8), bits = new Uint32Array(props.buffer);
for (let e = 0; e < nE; e++) props.set(e === 6 ? [1, 0, 0] : [0, 1, 0], e * 8);   // local y
const MASKS = [0xc30, 0x30, 0x240, 0x6, 0x820820];
MASKS.forEach((m, e) => { bits[e * 8 + 7] = m; });
props[5 * 8 + 7] = 1.0;                                            // a float, not a release pattern
const beamView = {
  domain: { nElem: nE }, header: { nElements: nE }, elemRecordU32: 6, elems: FEAv4Writer.encodeBeamElems(beams),
  nodes: NODES, nodeIds: Uint32Array.from(Array.from({ length: NODES.length / 3 }, (_, i) => 100 + i)),
  elemIds: Uint32Array.from(Array.from({ length: nE }, (_, i) => i + 1)), beamProps: props, sections: SECTIONS
};
const build = FEABeamGeometry.build(beamView);
const beamMesh = { visible: true };
global.FEABeams = { view: () => beamView, build: () => build, mesh: () => beamMesh };
vm.runInThisContext(fs.readFileSync(V + 'releases.js', 'utf8'), { filename: 'releases.js' });
const S = FEAReleases;
S.onModelLoaded();

// 1. bit decoding: the five probe patterns, a float, nothing
{
  const d = S._decode;
  const c30 = d(0xc30), x30 = d(0x30), x240 = d(0x240), x6 = d(0x6), p = d(0x820820);
  assert(c30.a === 0x30 && c30.b === 0x30 && !c30.pa && !c30.pb, '0xc30: M2 M3 released at both ends (pinned both ends)');
  assert(x30.a === 0x30 && x30.b === 0, '0x30: M2 M3 at end A only');
  assert(x240.a === 0 && x240.b === 0x9, '0x240: P (bit 0) and T (bit 3) at end B');
  assert(x6.a === 0x6 && x6.b === 0, '0x6: V2 V3 at end A');
  assert(p.a === 0x20 && p.b === 0x20 && p.pa === 0x20 && p.pb === 0x20, '0x820820: M3 released and partly fixed at both ends');
  assert(d(0x3f800000) === null && d(0) === null, 'a float in the slot (bits 24-31 set) and 0 are no release');
  assert(d(0x1000) === null && d(0x1020).a === 0x20 && d(0x1020).pa === 0, 'a partial bit without its release bit is ignored (alone: no release)');
  const texts = MASKS.map(S._textOfMask);
  assert(texts[0] === 'I: M2 M3; J: M2 M3' && texts[1] === 'I: M2 M3; J: fixed' && texts[2] === 'I: fixed; J: P T' &&
         texts[3] === 'I: V2 V3; J: fixed' && texts[4] === 'I: M3~; J: M3~', 'texts as the sidecar writes them: ' + texts.join(' | '));
  assert(S.text(2) === 'I: fixed; J: P T' && S.text(5) === '' && S.text(6) === '' && S.text(99) === '', 'text(e) by beam index; "" for none, a float slot, out of range');
  assert(JSON.stringify(S._elems()) === '[0,1,2,3,4]', 'the released members: beams 0-4 (not the float slot, not the column)');
}

// helpers on the parts of beam e
const parts = e => S._parts(e);
const axisOf = (pt, local) => new THREE.Vector3(local[0], local[1], local[2]).applyQuaternion(pt.quat);
const NAMES = ['P', 'V2', 'V3', 'T', 'M2', 'M3'];
const ofDof = (list, name) => list.filter(pt => NAMES[pt.dof] === name);
// u = outline radius 4 (span floor 0.3) x size 1; bending ring radius 4 + 0.6u = 6.4

// 2. pinned both ends, end A beside a column: two crossed rings at each end, no pins; end A further in
{
  const ps = parts(0);
  const rings = ps.filter(p => p.prim === 'ring');
  assert(rings.length === 4 && !ps.some(p => p.prim === 'rod'), 'beam 0 (0xc30): four rings (two per end), no pin');
  const a = rings.filter(p => p.end === 0), b = rings.filter(p => p.end === 1);
  assert(a.length === 2 && b.length === 2, 'two rings at each end');
  assert(a.every(p => near(p.pos.x, 14.1421 + 6.4, 1e-3) && near(p.pos.y, 0) && near(p.pos.z, 0)),
         'end A rings clear the 20 x 20 column at the node: centre 14.14 + 6.4 in along the member: ' + a.map(p => p.pos.x.toFixed(3)));
  assert(b.every(p => near(p.pos.x, 120 - 0.4 - 6.4)), 'end B rings 0.1u + 6.4 in from node B: ' + b.map(p => p.pos.x.toFixed(3)));
  assert(rings.every(p => near(p.scale.x, 6.4)), 'ring radius = outline radius + 0.6u = 6.4');
  const normals = a.map(p => axisOf(p, [0, 0, 1]));
  assert(normals.some(n => near(Math.abs(n.y), 1)) && normals.some(n => near(Math.abs(n.z), 1)),
         'the two rings lie in the x-z plane (M3) and the x-y plane (M2)');
}

// 3. pinned at end A only
{
  const ps = parts(1);
  assert(ps.length === 2 && ps.every(p => p.prim === 'ring' && p.end === 0 && p.pos.x < 60), 'beam 1 (0x30): two rings, both at end A');
}

// 4. P + T at end B: a collar round the member, then a slot of two square plates further in
{
  const ps = parts(2);
  assert(ps.every(p => p.end === 1 && p.pos.x > 60), 'beam 2 (0x240): everything at end B');
  const t = ofDof(ps, 'T'), pp = ofDof(ps, 'P');
  assert(t.length === 1 && t[0].prim === 'ring' && near(Math.abs(axisOf(t[0], [0, 0, 1]).x), 1) && near(t[0].scale.x, 5),
         'T: one ring round the member (normal along it), radius outline + 0.25u = 5');
  assert(near(t[0].pos.x, 120 - 0.4 - 0.8), 'T collar 0.1u + 0.2u in from node B: ' + t[0].pos.x.toFixed(3));
  assert(pp.length === 2 && pp.every(p => p.prim === 'box' && near(Math.abs(axisOf(p, [0, 1, 0]).x), 1) && near(p.scale.y, 0.4) && near(p.scale.x, 10)),
         'P: two square plates (10 x 10, 0.1u thick) square to the member');
  const xs = pp.map(p => p.pos.x).sort((m, n) => m - n);
  assert(near(xs[0], 114.8) && near(xs[1], 116.8), 'P plates further in than the collar, a 0.4u gap apart: ' + xs.map(x => x.toFixed(3)));
}

// 5. V2 + V3 at end A: two hollow squares (V2 in the x-z plane, V3 in the x-y plane)
{
  const ps = parts(3);
  const v2 = ofDof(ps, 'V2'), v3 = ofDof(ps, 'V3');
  assert(v2.length === 4 && v3.length === 4 && ps.every(p => p.prim === 'box' && p.end === 0), 'beam 3 (0x6): two squares of four bars, at end A');
  const along = list => list.filter(p => near(Math.abs(axisOf(p, [0, 1, 0]).x), 1));
  assert(along(v2).every(p => near(Math.abs(p.pos.z), 4.8) && near(p.pos.y, 72)), 'V2 (along local 2 = z): its bars along the member sit at z = +/-4.8');
  assert(along(v3).every(p => near(Math.abs(p.pos.y - 72), 4.8) && near(p.pos.z, 0)), 'V3 (along local 3 = -y): its bars along the member sit at y = +/-4.8');
  assert(ps.every(p => p.pos.x > 0 && p.pos.x < 11), 'the squares sit within 11 of node A');
}

// 6. M3 partly fixed at both ends: a ring in the x-z plane with a pin along y (local 3), flagged partial
{
  const ps = parts(4);
  const rings = ps.filter(p => p.prim === 'ring'), rods = ps.filter(p => p.prim === 'rod');
  assert(rings.length === 2 && rods.length === 2 && rings[0].end !== rings[1].end, 'beam 4 (0x820820): a ring and a pin at each end');
  assert(rings.every(p => near(Math.abs(axisOf(p, [0, 0, 1]).y), 1)), 'M3 ring in the x-z plane (normal along local y)');
  assert(rods.every(p => near(Math.abs(axisOf(p, [0, 1, 0]).y), 1) && near(p.scale.y, 14)), 'M3 pin along local y (SAP local 3), 2 (ring + 0.15u) long');
  assert(ps.every(p => p.partial === true && NAMES[p.dof] === 'M3'), 'every part flagged partial (M3~)');
  assert(!parts(0).some(p => p.partial), 'a plain release is not partial');
}

// 7. instances: counts and colours (amber = partial, indigo = released)
{
  const m = S._meshes();
  assert(S.count() === 5, 'five members drawn');
  assert(m.ring.mesh.count === 4 + 2 + 1 + 2 && m.rod.mesh.count === 2 && m.box.mesh.count === 2 + 8, 'instances: 9 rings, 2 pins, 10 boxes');
  const col = (p, k) => { const c = new THREE.Color(); m[p].mesh.getColorAt(k, c); return c; };
  const kp = Array.from(m.rod.owner).indexOf(4), kr = Array.from(m.ring.owner).indexOf(0);
  const cp = col('rod', kp), cr = col('ring', kr);
  assert(near(cp.r, 1, 1e-3) && near(cp.g, 143 / 255, 1e-3) && near(cp.b, 0, 1e-3), 'partial pin amber');
  assert(near(cr.r, 57 / 255, 1e-3) && near(cr.b, 171 / 255, 1e-3), 'released ring indigo');
  assert(m.ring.partial.filter(Boolean).length === 2 && m.ring.dof.filter(d => d === 'M3').length === 2 + 1 + 2 &&
         m.ring.dof.filter(d => d === 'T').length === 1, 'ring instances carry their DOF and partial flag (M3: 2 + 1 + 2, T: 1, partial: 2)');
}

// 8. visibility follows the beams: elemVis (section cut), Beams > Show, the panel's Show
{
  const vis = build.geometry.getAttribute('elemVis');
  vis.array.fill(0, build.vertStart[0], build.vertStart[0] + build.vertCount[0]);
  S.writeVis(null);
  assert(S.count() === 4 && !Array.from(S._meshes().ring.owner).includes(0), 'beam 0 hidden by the section cut: its symbols go');
  vis.array.fill(1, build.vertStart[0], build.vertStart[0] + build.vertCount[0]);
  S.writeVis(null);
  assert(S.count() === 5, 'shown again');
  beamMesh.visible = false; S.update();
  assert(S.count() === 0, 'Beams > Show off: no releases');
  beamMesh.visible = true; S.update();
  S._set({ visible: false });
  assert(S.count() === 0, 'Show releases off: none drawn');
  S._set({ visible: true });
  assert(S.count() === 5, 'and back');
}

// 9. size: the rings grow with the slider, the clearance from the column does not
{
  S._set({ size: 2 });
  const a = parts(0).filter(p => p.end === 0);
  assert(a.every(p => near(p.scale.x, 4 + 0.6 * 8) && near(p.pos.x, 14.1421 + 8.8, 1e-3)), 'size 2: ring radius 4 + 0.6 x 8 = 8.8, still clear of the column');
  S._set({ size: 1 });
}

// 10. readout row
{
  S.fillReadout(2);
  assert(els.roRel.textContent === 'I: fixed; J: P T' && els.roRelRow.style.display === '', 'readout: the Releases row shows the member\'s text');
  S.fillReadout(5);
  assert(els.roRelRow.style.display === 'none', 'a member without releases hides the row');
  S.fillReadout(0); S.clearReadout();
  assert(els.roRelRow.style.display === 'none', 'clearReadout hides it');
}

// 11. cleared
S.onModelCleared();
assert(!S._meshes() && S._elems().length === 0 && S.text(0) === '', 'model cleared: nothing drawn, no text');

console.log(fails ? fails + ' FAILED' : 'ALL RELEASE TESTS PASSED');
process.exitCode = fails ? 1 : 0;
