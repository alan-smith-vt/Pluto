// Headless test for sectionCut.js refineStations: a straight cut must read each element's own
// value at every element edge it crosses (narrow elements and the mesh boundary included), not a
// point part-way down the gradient (2026-11-19: with smoothing off a plate-to-shell junction peak
// sits on an element edge that even stations never land on). Synthetic fields, no project data.
//   node viewer/tests/test_cut_edges.js
const fs = require('fs'), path = require('path'), vm = require('vm');
const V = path.join(__dirname, '..', 'scripts') + path.sep;
const L = path.join(__dirname, '..', 'lib') + path.sep;

global.window = global; global.self = global;
const stub = () => ({ style: {}, classList: { add() {}, remove() {}, toggle() {} }, addEventListener() {}, appendChild() {},
                      getContext: () => ({}), width: 240, height: 150, value: '', disabled: false, innerHTML: '', textContent: '' });
global.document = { getElementById: () => null, createElement: stub };
vm.runInThisContext(fs.readFileSync(L + 'three.min.js', 'utf8'), { filename: 'three.min.js' });
global.scene = new THREE.Scene(); global.camera = new THREE.PerspectiveCamera();
global.mesh = null; global.feaEdges = null; global.feaModel = null; global.feaBuild = null; global.feaLCData = null;
global.currentComp = 0; global.currentStr = 0; global.smoothing = false; global.absValue = false;
global.envGlobalDsrValue = null; global.inDsrMode = () => false; global.inStrMode = () => false;
global.strengthComponents = () => []; global.nodeVec = () => new THREE.Vector3(); global.fmt = (v) => String(v);
global.requestRender = () => {}; global.log = () => {}; global.activeComponent = () => null;
global.FEAFeatures = { envelope: () => null, ensureEnvelope: () => ({ sectionCuts: { version: 1, items: [] } }) };
vm.runInThisContext(fs.readFileSync(V + 'sectionCut.js', 'utf8'), { filename: 'sectionCut.js' });

let fails = 0;
const assert = (c, m) => { if (!c) { fails++; console.error('FAIL ' + m); } else console.log('ok   ' + m); };
const near = (a, b, t) => Math.abs(a - b) <= t;
const refine = FEASectionCut._refineStations;
const vmaxOf = (r) => Math.max(...r.v.filter((x) => x === x));
const ordered = (r) => r.t.every((s, i) => i === 0 || s > r.t[i - 1]);
const TOL = 2 * 1e-6;                          // relative tolerance x the cut length

// 1. Two elements meeting at s = 1: element 0 on [0, 1) with v = 10 s (its edge value 10),
//    element 1 on [1, 2] with v = 2. 7 even stations (multiples of 2/7): none at s = 1.
const two = (s) => (s < 0 || s > 2) ? { elem: -1, val: NaN } : s < 1 ? { elem: 0, val: 10 * s } : { elem: 1, val: 2 };
const r = refine(two, 0, 2, 7);
assert(near(vmaxOf(r), 10, 1e-4), 'reads the edge value 10 of element 0 (even stations alone give ' + (10 * 6 / 7).toFixed(3) + '): ' + vmaxOf(r));
assert(r.t.some((s, i) => near(s, 1, TOL * 2) && r.v[i] === 2), 'a sample just past the edge in element 1, value 2');
assert(ordered(r), 'samples strictly in order along the cut');
assert(r.hit.length === r.t.length && r.v.length === r.t.length, 'parallel arrays');
assert(near(r.area, 7, 1e-4), 'area integrates each element (5 + 2): ' + r.area.toFixed(6));
assert(r.t[0] === 0 && r.t[r.t.length - 1] === 2, 'end stations unchanged');

// 2. Many narrow elements between two stations, the peak on an interior edge (a refined
//    junction: rings narrower than the station spacing). Elements 0.05 wide on [0, 1),
//    v = 10 s inside each; element 99 on [1, 2], v = 2. Stations 0.5 apart: 10 elements
//    lie between the stations at 0.5 and 1.0, and the peak 10 is on the edge at s = 1.
let calls = 0;
const narrow = (s) => {
  calls++;
  if (s < 0 || s > 2) return { elem: -1, val: NaN };
  return s < 1 ? { elem: Math.floor(s / 0.05), val: 10 * s } : { elem: 99, val: 2 };
};
const rn = refine(narrow, 0, 2, 4);
assert(near(vmaxOf(rn), 10, 1e-4), 'narrow elements: still reads the edge value 10 at s = 1: ' + vmaxOf(rn));
for (const e of [0.55, 0.75, 0.95]) {
  assert(rn.t.some((s) => near(s, e, TOL * 2)), 'narrow elements: interior edge at ' + e + ' sampled');
}
assert(ordered(rn), 'narrow elements: samples in order');
assert(calls < 20 * 42, 'narrow elements: ' + calls + ' probes for 20 crossed edges (about 20 per edge)');

// 3. A cut running off the mesh at s = 2 (a plate that ends at the shell): the last sample on
//    the hit side is at the boundary and carries that element's own edge value.
const edge = (s) => (s > 2) ? { elem: -1, val: NaN } : { elem: 0, val: 5 * s };   // rises to 10 at the boundary
const re = refine(edge, 1.25, 2.65, 7);           // stations every 0.2 from 1.25: none at 2
const lastHit = re.t.filter((s, i) => re.hit[i]).slice(-1)[0];
assert(near(lastHit, 2, TOL * 2) && near(vmaxOf(re), 10, 1e-4), 'runs off the mesh: reads the boundary value 10 at s = 2 (' + lastHit + ')');
assert(re.hit.some((h) => !h) && Number.isFinite(re.area), 'misses flagged, area finite');
assert(near(re.area, 0.5 * (6.25 + 10) * (2 - 1.25), 1e-4), 'area over the hit part only: ' + re.area.toFixed(6));

// 4. A single element: no refinement, the even stations as before
const r3 = refine(() => ({ elem: 4, val: 3 }), -1, 1, 10);
assert(r3.t.length === 11 && near(r3.area, 6, 1e-12), 'one element: 11 even stations, area 6');

if (fails) { console.error(fails + ' failure(s)'); process.exit(1); }
console.log('all passed');
