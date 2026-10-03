// Headless tests for the deformed-shape gate (FEAAttributes.deformStatus, the
// pure core of viewer.js deformAvailable / refreshDispVecs), the beam
// displacement updater and the empty-plane beam range. No THREE, no DOM.
//   node viewer/tests/test_deform.js
const fs = require('fs'), path = require('path'), vm = require('vm');
const V = path.join(__dirname, '..', 'scripts') + path.sep;

global.window = global;
vm.runInThisContext(fs.readFileSync(V + 'attributeUpdaters.js', 'utf8'), { filename: 'attributeUpdaters.js' });

let fails = 0;
const assert = (c, m) => { if (!c) { fails++; console.error('FAIL ' + m); } else console.log('ok   ' + m); };
const near = (a, b) => Math.abs(a - b) < 1e-6;
const D = FEAAttributes.deformStatus;
const base = { model: true, shellElems: 0, shellDisp: false, beamDisp: false, envelope: false, haveLC: true };
const st = o => D(Object.assign({}, base, o));

// 1. beam-only file (stand-in shell domain: 0 elements, no vector)
let s = st({ beamDisp: true });
assert(s.ok && s.beams && !s.shells, 'beam-only with a beam displacement vector: enabled, beams only (shell updater skipped)');
s = st({});
assert(!s.ok && /No displacement vector/.test(s.hint), 'beam-only without any vector: disabled with the no-vector hint');
// 2. shell + beam files
s = st({ shellElems: 10, shellDisp: true, beamDisp: true });
assert(s.ok && s.shells && s.beams, 'shells + beams both with vectors: both updaters run');
s = st({ shellElems: 10, shellDisp: true, beamDisp: false });
assert(s.ok && s.shells && !s.beams, 'shells with a vector, beams without: shells only (as before)');
s = st({ shellElems: 10, shellDisp: false, beamDisp: true });
assert(!s.ok && /pull them off the plates/.test(s.hint), 'shells without a vector, beams with one: stays disabled, own hint');
s = st({ shellElems: 10 });
assert(!s.ok && /No displacement vector/.test(s.hint), 'shell file without a vector: the old hint');
// 3. envelope / no LC / no model
s = st({ beamDisp: true, envelope: true });
assert(!s.ok && /Envelopes mix LCs/.test(s.hint), 'envelope view: disabled with the envelope hint');
s = st({ beamDisp: true, haveLC: false });
assert(!s.ok && s.hint === '', 'no resident LC: disabled, no hint');
assert(!D(null).ok && !D({ model: false }).ok, 'no model: disabled');

// 4. computeBeamRange: empty flag for a plane with no finite value of the component
const view = { header: { cornerComponents: 2, maxCorners: 2, nElements: 2 } };
const plane = Float32Array.from([1, NaN, 2, NaN, -3, NaN, 5, NaN]);   // comp 0 has values, comp 1 is all NaN
let r = FEAAttributes.computeBeamRange(view, plane, 0);
assert(!r.empty && r.min === -3 && r.max === 5, 'component with values: range -3..5, not empty');
r = FEAAttributes.computeBeamRange(view, plane, 1);
assert(r.empty === true, 'all-NaN component in this LC: empty = true');

// 5. updateBeamDispVecs: peak |d| from the beam ends (what auto scale divides by)
{
  // one beam, 3 components (DX DY DZ), 2 slots; 4 vertices: 2 at A (t=0), 2 at B (t=1)
  const bt = Float32Array.from([0, 0, 1, 1]);
  const dv = new Float32Array(12);
  const geometry = { getAttribute: n => n === 'beamT' ? { array: bt } : { array: dv, needsUpdate: false } };
  const build = { nElem: 1, vertStart: Int32Array.from([0]), vertCount: Int32Array.from([4]), geometry };
  const bv = { header: { cornerComponents: 3, maxCorners: 2 } };
  const lc = Float32Array.from([0.3, 0.4, 0, 1, 2, 2]);              // |dA| = 0.5, |dB| = 3
  const m = FEAAttributes.updateBeamDispVecs(build, bv, lc, [0, 1, 2]);
  assert(near(m, 3), 'peak beam displacement = 3 (end B): ' + m);
  assert(near(dv[0], 0.3) && near(dv[6], 1) && near(dv[11], 2), 'A vertices carry dA, B vertices carry dB');
}

console.log(fails ? fails + ' FAILED' : 'ALL DEFORM TESTS PASSED');
process.exitCode = fails ? 1 : 0;
