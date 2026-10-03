// Headless tests for tapered beams in beamGeometry.js (ELEM slot 4 = end-B
// section index + 1, 0 = straight): ring radii at each end, vertex counts,
// sectionOfB, slot 4 = 0 building the same geometry as a straight member,
// and the PIPE -> I fallback. THREE under vm; no DOM, no mesh.
//   node viewer/tests/test_beamtaper.js
const fs = require('fs'), path = require('path'), vm = require('vm');
const V = path.join(__dirname, '..', 'scripts') + path.sep;
const L = path.join(__dirname, '..', 'lib') + path.sep;

global.window = global; global.self = global;
global.document = { getElementById: () => null };
let logged = [];
global.log = (m) => { logged.push(m); };
vm.runInThisContext(fs.readFileSync(L + 'three.min.js', 'utf8'), { filename: 'three.min.js' });
vm.runInThisContext(fs.readFileSync(V + 'format/v4Reader.js', 'utf8'), { filename: 'v4Reader.js' });
vm.runInThisContext(fs.readFileSync(V + 'format/v4Writer.js', 'utf8'), { filename: 'v4Writer.js' });
vm.runInThisContext(fs.readFileSync(V + 'beamGeometry.js', 'utf8'), { filename: 'beamGeometry.js' });

let fails = 0;
const assert = (c, m) => { if (!c) { fails++; console.error('FAIL ' + m); } else console.log('ok   ' + m); };
const near = (a, b, t = 1e-5) => Math.abs(a - b) <= t;

// Sections as the reader exposes them (params keyed by name).
const SECTIONS = [
  { name: 'P2', type: 'PIPE', params: { od: 2, t: 0.2 } },          // 0
  { name: 'P4', type: 'PIPE', params: { od: 4, t: 0.25 } },         // 1
  { name: 'W8', type: 'I', params: { d: 8, bfTop: 4, tfTop: 0.4, bfBot: 4, tfBot: 0.4, tw: 0.25 } },   // 2
  { name: 'R2x3', type: 'RECT', params: { b: 2, h: 3 } },           // 3
  { name: 'R4x6', type: 'RECT', params: { b: 4, h: 6 } }            // 4
];
// Three members along +X, each 10 long, offset in Y so they never touch.
const NODES = Float64Array.from([0, 0, 0, 10, 0, 0, 0, 20, 0, 10, 20, 0, 0, 40, 0, 10, 40, 0]);

// beams: [{n0, n1, sec, secB?}] -> view (encoded by the demo writer, so it is tested too)
function view(beams, rawSlot4) {
  const elems = FEAv4Writer.encodeBeamElems(beams);
  if (rawSlot4) rawSlot4.forEach((v, i) => { if (v !== undefined) elems[i * 6 + 4] = v; });
  return { domain: { nElem: beams.length }, elemRecordU32: 6, elems: elems, nodes: NODES,
           beamProps: null, sections: SECTIONS };
}
const straight3 = [{ n0: 0, n1: 1, sec: 0 }, { n0: 2, n1: 3, sec: 0 }, { n0: 4, n1: 5, sec: 3 }];
function attrs(b) {
  const g = b.geometry;
  return ['position', 'normal', 'beamT', 'endVals', 'dispVec', 'elemVis', 'catIdx'].map(n => g.getAttribute(n).array);
}
function sameArrays(a, b) {
  if (a.length !== b.length) return false;
  for (let i = 0; i < a.length; i++) {
    if (a[i].length !== b[i].length) return false;
    for (let k = 0; k < a[i].length; k++) if (!Object.is(a[i][k], b[i][k])) return false;
  }
  return true;
}
// Radius (distance from the X axis through the member's own y) of every vertex
// of element e at end t (0 or 1), as [min, max].
function endRadius(b, e, t, yc) {
  const pos = b.geometry.getAttribute('position').array, bt = b.geometry.getAttribute('beamT').array;
  let lo = Infinity, hi = -Infinity;
  for (let v = b.vertStart[e]; v < b.vertStart[e] + b.vertCount[e]; v++) {
    if (bt[v] !== t) continue;
    const r = Math.hypot(pos[v * 3 + 1] - yc, pos[v * 3 + 2]);
    if (r < lo) lo = r;
    if (r > hi) hi = r;
  }
  return [lo, hi];
}

// 1. ELEM encoding of the demo writer
{
  const el = FEAv4Writer.encodeBeamElems([{ n0: 3, n1: 4, sec: 1 }, { n0: 4, n1: 5, sec: 1, secB: 2 }, { n0: 5, n1: 6, sec: 2, secB: 2 }, { n0: 6, n1: 7, sec: 0, secB: -1 }]);
  assert(Array.from(el.slice(0, 6)).join() === '2,3,4,1,0,0', 'straight member encodes {2,n0,n1,sec,0,0}');
  assert(el[6 + 4] === 3, 'tapered member: slot 4 = secB + 1 (= 3)');
  assert(el[12 + 4] === 0 && el[18 + 4] === 0, 'secB equal to sec, or negative, writes 0 (straight)');
}

// 2. slot 4 = 0: straight geometry, sectionOfB == sectionOf
const b0 = FEABeamGeometry.build(view(straight3));
const segs = 24, capPipe = segs - 2;
assert(b0.vertCount[0] === segs * 6 + capPipe * 3 * 2, 'straight PIPE member: m*6 + caps*6 vertices (' + b0.vertCount[0] + ')');
assert(b0.vertCount[2] === 4 * 6 + 2 * 3 * 2, 'straight RECT member: 4*6 + 2*6 vertices');
assert(Array.from(b0.sectionOfB).join() === Array.from(b0.sectionOf).join() && b0.nTaper === 0 && b0.taperMismatch === 0,
  'straight: sectionOfB equals sectionOf, nothing tapered');
{
  const ra = endRadius(b0, 0, 0, 0), rb = endRadius(b0, 0, 1, 0);
  assert(near(ra[1], 1) && near(rb[1], 1), 'straight P2: radius 1 at both ends');
}
// slot 4 = sec + 1 (same section at B) is still straight and builds the same arrays
{
  const bSame = FEABeamGeometry.build(view(straight3, [1, 1, 4]));
  assert(sameArrays(attrs(bSame), attrs(b0)) && bSame.nTaper === 0, 'slot 4 = own section + 1 builds the identical geometry');
  const bOut = FEABeamGeometry.build(view(straight3, [99, undefined, 6]));
  assert(sameArrays(attrs(bOut), attrs(b0)) && bOut.sectionOfB[0] === 0, 'slot 4 past the section table reads as straight');
}

// 3. a reducer P2 -> P4 in the middle member
logged = [];
const tap = [{ n0: 0, n1: 1, sec: 0 }, { n0: 2, n1: 3, sec: 0, secB: 1 }, { n0: 4, n1: 5, sec: 3 }];
const b1 = FEABeamGeometry.build(view(tap));
assert(b1.sectionOf[1] === 0 && b1.sectionOfB[1] === 1 && b1.nTaper === 1 && b1.taperMismatch === 0, 'taper: sectionOf 0, sectionOfB 1, nTaper 1');
assert(b1.vertCount[1] === b0.vertCount[1] && b1.totalVerts === b0.totalVerts, 'PIPE -> PIPE taper keeps the vertex count (same facet count, caps per end)');
{
  const ra = endRadius(b1, 1, 0, 20), rb = endRadius(b1, 1, 1, 20);
  // caps triangulate the ring's own points, so every end vertex sits on the ring
  assert(near(ra[0], 1) && near(ra[1], 1), 'taper end A (side + cap vertices): radius 1: [' + ra + ']');
  assert(near(rb[0], 2) && near(rb[1], 2), 'taper end B (side + cap vertices): radius 2: [' + rb + ']');
  // side triangles only (first m*6 verts): every A vertex at r=1, every B vertex at r=2
  const pos = b1.geometry.getAttribute('position').array, bt = b1.geometry.getAttribute('beamT').array;
  let okSide = true;
  for (let v = b1.vertStart[1]; v < b1.vertStart[1] + segs * 6; v++) {
    const r = Math.hypot(pos[v * 3 + 1] - 20, pos[v * 3 + 2]);
    if (!near(r, bt[v] === 0 ? 1 : 2)) okSide = false;
  }
  assert(okSide, 'cone side walls run from r=1 at A to r=2 at B');
}
{
  // the straight neighbours are untouched (same vertex slices as the all-straight build)
  const A = attrs(b1), B = attrs(b0);
  let ok = true;
  [0, 2].forEach(e => {
    const s = b0.vertStart[e], n = b0.vertCount[e];
    for (let k = s * 3; k < (s + n) * 3; k++) if (A[0][k] !== B[0][k] || A[1][k] !== B[1][k]) ok = false;
  });
  assert(ok, 'straight members beside a taper build exactly as before');
}
assert(logged.length === 0, 'a valid taper logs nothing');
assert(FEABeamGeometry.sectionText(b1, SECTIONS, 1) === 'P2 → P4 PIPE', 'readout text: "P2 -> P4 PIPE": ' + FEABeamGeometry.sectionText(b1, SECTIONS, 1));
assert(FEABeamGeometry.sectionText(b1, SECTIONS, 0) === 'P2 PIPE', 'straight readout text unchanged: "P2 PIPE"');

// 4. RECT -> RECT taper (4 points each): half-widths double at B
{
  const b = FEABeamGeometry.build(view([{ n0: 0, n1: 1, sec: 3, secB: 4 }]));
  const pos = b.geometry.getAttribute('position').array, bt = b.geometry.getAttribute('beamT').array;
  let ay = 0, by = 0, az = 0, bz = 0;
  for (let v = 0; v < b.totalVerts; v++) {
    const y = Math.abs(pos[v * 3 + 1]), z = Math.abs(pos[v * 3 + 2]);
    if (bt[v] === 0) { ay = Math.max(ay, y); az = Math.max(az, z); } else { by = Math.max(by, y); bz = Math.max(bz, z); }
  }
  // default local y = global Z for a member along X (BPRP absent): outline (y,z) -> world (Z, -Y)
  assert(near(Math.max(ay, az), 1.5) && near(Math.max(by, bz), 3) && near(Math.min(ay, az), 1) && near(Math.min(by, bz), 2),
    'RECT 2x3 -> 4x6: half sizes 1/1.5 at A, 2/3 at B');
  assert(b.nTaper === 1 && b.vertCount[0] === 4 * 6 + 2 * 6, 'RECT taper: 36 vertices');
}

// 5. PIPE -> I: outlines differ in point count -> drawn straight at A, counted, logged once
logged = [];
const mis = [{ n0: 0, n1: 1, sec: 0 }, { n0: 2, n1: 3, sec: 0, secB: 2 }, { n0: 4, n1: 5, sec: 3 }];
const b2 = FEABeamGeometry.build(view(mis));
assert(b2.taperMismatch === 1 && b2.nTaper === 0, 'PIPE -> I counted as a mismatch, not a taper');
assert(sameArrays(attrs(b2), attrs(b0)), 'PIPE -> I falls back to the straight end-A geometry');
assert(b2.sectionOfB[1] === 2, 'sectionOfB keeps the declared end-B section (I) for the readout');
assert(logged.length === 1 && /different outline/.test(logged[0]), 'one log line for the mismatch: ' + logged[0]);
assert(FEABeamGeometry.sectionText(b2, SECTIONS, 1) === 'P2 PIPE → W8 I', 'mixed-type readout: "P2 PIPE -> W8 I"');

console.log(fails ? fails + ' FAILED' : 'ALL BEAM TAPER TESTS PASSED');
process.exitCode = fails ? 1 : 0;
