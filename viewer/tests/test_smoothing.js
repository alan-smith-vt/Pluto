// Headless test for the element-mean smoothing updater in attributeUpdaters.js:
// two quads sharing an edge, one component; the shared nodes must show the mean of
// the two element means, the outer nodes each element's own mean, NaN corners skipped.
//   node viewer/tests/test_smoothing.js
const fs = require('fs'), path = require('path'), vm = require('vm');
const V = path.join(__dirname, '..', 'scripts') + path.sep;

global.window = global;
vm.runInThisContext(fs.readFileSync(V + 'attributeUpdaters.js', 'utf8'), { filename: 'attributeUpdaters.js' });

let fails = 0;
const assert = (c, m) => { if (!c) { fails++; console.error('FAIL ' + m); } else console.log('ok   ' + m); };
const near = (a, b) => Math.abs(a - b) < 1e-6;

// model: 6 nodes, 2 quads: e0 = (0,1,4,3), e1 = (1,2,5,4); 1 component per corner
const model = { header: { nElements: 2, nNodes: 6, maxCorners: 4, cornerComponents: 1 } };
const elemCorners = Int32Array.from([0, 1, 4, 3, 1, 2, 5, 4]);
// normal groups: node -> [ [ {e, k}, ... ] ] (one group per node, all coplanar)
const normalGroups = [];
for (let n = 0; n < 6; n++) {
  const grp = [];
  for (let e = 0; e < 2; e++) for (let k = 0; k < 4; k++) if (elemCorners[e * 4 + k] === n) grp.push({ e, k });
  normalGroups.push([grp]);
}
const cv = new Float32Array((6 + 6) * 4);
const build = {
  elemNCount: Uint8Array.from([4, 4]),
  elemCorners, normalGroups,
  geometry: { getAttribute: () => ({ array: cv, needsUpdate: false }) }
};
// corner values: e0 = [-1.0, -0.6, -0.8, -0.8] (mean -0.8, a spike at corner 0);
//                e1 = [-0.6, NaN, -0.7, -0.5]  (NaN skipped: mean -0.6)
const lc = Float32Array.from([-1.0, -0.6, -0.8, -0.8, -0.6, NaN, -0.7, -0.5]);

FEAAttributes.updateCornerValsElemAveraged(build, model, lc, 0);
const em = build.elemMeanCache, na = build.nodeAvgCache, ca = build.cornerAvgCache;
assert(near(em[0], -0.8) && near(em[1], -0.6), 'element means -0.8 / -0.6 (NaN corner skipped)');
assert(near(na[0], -0.8) && near(na[3], -0.8), 'nodes only on e0 show e0 mean');
assert(near(na[2], -0.6) && near(na[5], -0.6), 'nodes only on e1 show e1 mean');
assert(near(na[1], -0.7) && near(na[4], -0.7), 'shared nodes show the mean of the two element means');
assert(near(ca[0], -0.8) && near(ca[1], -0.7) && near(ca[4], -0.7) && near(ca[5], -0.6), 'per-(element,corner) cache matches the node values');
assert(near(cv[0], -0.8) && near(cv[1], -0.7), 'cornerVals attribute fanned out from the cache');
// the spike at e0 corner 0 (-1.0) never appears anywhere
let spike = false;
for (let i = 0; i < ca.length; i++) if (ca[i] === ca[i] && ca[i] < -0.85) spike = true;
assert(!spike, 'a single corner overshoot does not survive element-mean smoothing');

// node averaging, for contrast: node 0 keeps the -1.0 corner
FEAAttributes.updateCornerValsNodeAveraged(build, model, lc, 0);
assert(near(build.nodeAvgCache[0], -1.0), 'node averaging keeps the corner spike at an unshared node');

if (fails) { console.error(fails + ' failure(s)'); process.exit(1); }
console.log('all passed');
