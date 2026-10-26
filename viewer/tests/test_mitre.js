// Headless tests for mitred joints in beamGeometry.js: where two PIPE members of
// one section meet at a node and turn by at most 90 deg, both ends are cut on
// the bisecting plane (a chorded bend draws as one tube, no wedge gaps or cap
// seams). Square everywhere else: a sharper turn, a tee, another section or
// shape at the node, an offset, cuts that would eat a member, straight on.
// THREE under vm; no DOM, no mesh.
//   node viewer/tests/test_mitre.js
const fs = require('fs'), path = require('path'), vm = require('vm');
const V = path.join(__dirname, '..', 'scripts') + path.sep;
const L = path.join(__dirname, '..', 'lib') + path.sep;

global.window = global; global.self = global;
global.document = { getElementById: () => null };
global.log = () => {};
vm.runInThisContext(fs.readFileSync(L + 'three.min.js', 'utf8'), { filename: 'three.min.js' });
vm.runInThisContext(fs.readFileSync(V + 'format/v4Reader.js', 'utf8'), { filename: 'v4Reader.js' });
vm.runInThisContext(fs.readFileSync(V + 'format/v4Writer.js', 'utf8'), { filename: 'v4Writer.js' });
vm.runInThisContext(fs.readFileSync(V + 'beamGeometry.js', 'utf8'), { filename: 'beamGeometry.js' });

let fails = 0;
const assert = (c, m) => { if (!c) { fails++; console.error('FAIL ' + m); } else console.log('ok   ' + m); };

const SECTIONS = [
  { name: 'P2', type: 'PIPE', params: { od: 2, t: 0.2 } },     // 0
  { name: 'P4', type: 'PIPE', params: { od: 4, t: 0.25 } },    // 1
  { name: 'R2x3', type: 'RECT', params: { b: 2, h: 3 } }       // 2
];
// pts: [[x,y,z]...], beams: [{n0, n1, sec}], props: optional Float32Array(n * 8)
function build(pts, beams, props) {
  return FEABeamGeometry.build({ domain: { nElem: beams.length }, elemRecordU32: 6, elems: FEAv4Writer.encodeBeamElems(beams),
    nodes: Float64Array.from([].concat.apply([], pts)), beamProps: props || null, sections: SECTIONS });
}
const deg = Math.PI / 180;
// a member of length 10 into the origin along +X, then one of length 10 out at `turn` deg in the XY plane
function corner(turn, sec0, sec1) {
  const out = [10 * Math.cos(turn * deg), 10 * Math.sin(turn * deg), 0];
  return build([[-10, 0, 0], [0, 0, 0], out], [{ n0: 0, n1: 1, sec: sec0 }, { n0: 1, n1: 2, sec: sec1 === undefined ? sec0 : sec1 }]);
}
// vertices of element e at end t (beamT 0 / 1)
function endVerts(b, e, t) {
  const pos = b.geometry.getAttribute('position').array, bt = b.geometry.getAttribute('beamT').array, out = [];
  for (let v = b.vertStart[e]; v < b.vertStart[e] + b.vertCount[e]; v++) if (bt[v] === t) out.push([pos[v * 3], pos[v * 3 + 1], pos[v * 3 + 2]]);
  return out;
}
const dot = (a, b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
const sub = (a, b) => [a[0] - b[0], a[1] - b[1], a[2] - b[2]];
// distance of p from the line through a with unit direction u
function lineDist(p, a, u) { const d = sub(p, a), t = dot(d, u); return Math.hypot(d[0] - t * u[0], d[1] - t * u[1], d[2] - t * u[2]); }

// 1. a 30 deg turn: both ends at the node on the bisecting plane, on both pipes' surfaces (one ellipse: no gap)
{
  const b = corner(30, 0), sq = corner(0, 0);
  assert(b.mitredEnds === 2, '30 deg: the two ends at the node are mitred (' + b.mitredEnds + ')');
  assert(b.vertCount[0] === sq.vertCount[0] && b.vertCount[1] === sq.vertCount[1], 'vertex counts unchanged');
  const N = [1 + Math.cos(30 * deg), Math.sin(30 * deg), 0], nl = Math.hypot(N[0], N[1]);
  const n = [N[0] / nl, N[1] / nl, 0], u0 = [1, 0, 0], u1 = [Math.cos(30 * deg), Math.sin(30 * deg), 0];
  const ends = endVerts(b, 0, 1).concat(endVerts(b, 1, 0));
  assert(ends.every(p => Math.abs(dot(p, n)) < 1e-5), 'every vertex of both cut ends lies on the bisecting plane');
  assert(ends.every(p => Math.abs(lineDist(p, [0, 0, 0], u0) - 1) < 1e-5 && Math.abs(lineDist(p, [0, 0, 0], u1) - 1) < 1e-5),
    'every cut vertex is on both pipes\' surfaces (r = 1): the two cuts are one ellipse');
  assert(endVerts(b, 0, 0).every(p => Math.abs(p[0] + 10) < 1e-6), 'the free end stays square');
}
// 2. limits of the turn
assert(corner(90, 0).mitredEnds === 2, '90 deg corner: mitred');
assert(corner(120, 0).mitredEnds === 0, '120 deg: square (a mitre that sharp would spike)');
{
  const a = corner(0, 0), pos = a.geometry.getAttribute('position').array;
  assert(a.mitredEnds === 0, 'straight on: square (exact already)');
  const ref = build([[-10, 0, 0], [0, 0, 0], [10, 0, 0]], [{ n0: 0, n1: 1, sec: 0 }, { n0: 1, n1: 2, sec: 0 }]).geometry.getAttribute('position').array;
  assert(pos.every((v, i) => v === ref[i]), 'straight on: the same vertices as before');
}
// 3. not for another section, another shape, a tee, an offset
assert(corner(30, 0, 1).mitredEnds === 0, 'P2 into P4 at the node: square');
assert(corner(30, 2).mitredEnds === 0, 'RECT members: square');
{
  const b = build([[-10, 0, 0], [0, 0, 0], [10 * Math.cos(30 * deg), 10 * Math.sin(30 * deg), 0], [0, 10, 0]],
    [{ n0: 0, n1: 1, sec: 0 }, { n0: 1, n1: 2, sec: 0 }, { n0: 1, n1: 3, sec: 0 }]);
  assert(b.mitredEnds === 0, 'three members at a node (a tee): square');
}
{
  const props = new Float32Array(2 * 8);
  props[2] = 1; props[8 + 2] = 1;          // local y = +Z for both
  props[5] = 0.5;                          // element 0, end B offset in y
  const b = build([[-10, 0, 0], [0, 0, 0], [10 * Math.cos(30 * deg), 10 * Math.sin(30 * deg), 0]], [{ n0: 0, n1: 1, sec: 0 }, { n0: 1, n1: 2, sec: 0 }], props);
  assert(b.mitredEnds === 0, 'an offset end at the node: square');
}
// 4. a bend drawn tighter than the pipe: the cuts would eat a chord, so square
{
  const b = build([[-1, 0, 0], [0, 0, 0], [0, 1, 0]], [{ n0: 0, n1: 1, sec: 1 }, { n0: 1, n1: 2, sec: 1 }]);   // r = 2 on members 1 long
  assert(b.mitredEnds === 0, 'members shorter than the cut (r tan(turn/2) = 2 on length 1): square');
}
// 5. a chorded bend: every inner joint mitred, the end joints square
{
  const R = 6, n = 6, pts = [[-10, 0, R]], beams = [];
  for (let k = 0; k <= n; k++) { const a = (90 * k / n) * deg; pts.push([R * Math.sin(a), 0, R - R * Math.cos(a)]); }
  pts.push([R + 0, 0, R + 10]);
  pts[0] = [-10, 0, 0];
  for (let k = 0; k + 1 < pts.length; k++) beams.push({ n0: k, n1: k + 1, sec: 0 });
  const b = build(pts, beams);
  assert(b.mitredEnds === 2 * (pts.length - 2), 'a 90 deg bend in 6 chords between two straights: all ' + 2 * (pts.length - 2) + ' joint ends mitred (' + b.mitredEnds + ')');
}

console.log(fails ? fails + ' MITRE TEST(S) FAILED' : 'ALL MITRE TESTS PASSED');
process.exit(fails ? 1 : 0);
