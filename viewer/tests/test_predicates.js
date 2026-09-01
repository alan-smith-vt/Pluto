// Headless engine test for viewer/scripts/predicates.js (no THREE, no DOM).
'use strict';
const fs = require('fs');
const vm = require('vm');
const path = require('path');
const assert = require('assert');

const ROOT = path.join(__dirname, '..', '..');   // repo root (viewer/tests/ -> Pluto/)

// ---- environment stubs -------------------------------------------------
global.window = global;
global.document = { getElementById: function () { return null; } };
global.needsRender = false;

// offsets: world = local + WORLD_OFF + VIEW_REC
const WORLD_OFF = [1000, 2000, 3000];
global.viewRecenter = [10, 20, 30];
function W(x, y, z) { return [x + WORLD_OFF[0] + 10, y + WORLD_OFF[1] + 20, z + WORLD_OFF[2] + 30]; }

let _env = null;
let refreshCount = 0;
global.FEAFeatures = {
    worldOffset: function () { return WORLD_OFF; },
    envelope: function () { return _env; },
    ensureEnvelope: function () {
        if (!_env) _env = { format: 'pluto-features', version: 1,
            groups: { version: 1, items: [] }, predicates: { version: 1, items: [] } };
        return _env;
    },
    refresh: function () { refreshCount++; },
    exportJson: function () { return JSON.stringify(_env); },
    fileName: function () { return 't.features.json'; }
};
global.log = function () {};

// ---- fabricated model --------------------------------------------------
// Slab on Z=0: 3x3 node grid (x,y in {0,120,240}), 4 quads. Node idx r*3+c? use:
// idx = ix + iy*3 (ix over x, iy over y).
// Wall on X=0: extra nodes (0,0,120),(0,120,120),(0,240,120) = idx 9,10,11; 2 quads.
const nodes = new Float32Array(12 * 3);
let p = 0;
for (let iy = 0; iy < 3; iy++) for (let ix = 0; ix < 3; ix++) {
    nodes[p++] = ix * 120; nodes[p++] = iy * 120; nodes[p++] = 0;
}
nodes.set([0, 0, 120,  0, 120, 120,  0, 240, 120], 9 * 3);

const quads = [
    [0, 1, 4, 3], [1, 2, 5, 4], [3, 4, 7, 6], [4, 5, 8, 7],   // slab (normals +Z)
    [0, 3, 10, 9], [3, 6, 11, 10]                              // wall  (normals +/-X)
];
const elemNCount = new Uint8Array(6).fill(4);
const elemCorners = new Int32Array(6 * 4);
quads.forEach((q, e) => q.forEach((n, k) => { elemCorners[e * 4 + k] = n; }));
const elemNormals = new Float32Array([
    0,0,1, 0,0,1, 0,0,1, 0,0,1,
    1,0,0, 1,0,0
]);

global.feaModel = {
    header: { nElements: 6, nNodes: 12 },
    nodes: nodes,
    nodeIds: Uint32Array.from({ length: 12 }, (_, i) => 101 + i),
    elemIds: Uint32Array.from({ length: 6 }, (_, i) => 1001 + i)
};
global.feaBuild = {
    elemNCount: elemNCount,
    elemCorners: elemCorners,
    elemNormals: elemNormals,
    geometry: null, triToElem: null
};

// Beams: b0 along X from (0,0,0)-(240,0,0) [cg (120,0,0)], b1 vertical at
// (240,240,0)-(240,240,240) [cg (240,240,120)].
const beamNodes = new Float32Array([0,0,0, 240,0,0, 240,240,0, 240,240,240]);
const beamElems = new Uint32Array(2 * 6);
beamElems.set([2, 0, 1, 0, 0, 0], 0);
beamElems.set([2, 2, 3, 0, 0, 0], 6);
const beamView = {
    header: { nElements: 2 }, elemRecordU32: 6,
    elems: beamElems, nodes: beamNodes,
    elemIds: Uint32Array.from([5001, 5002])
};
global.FEABeams = { view: function () { return beamView; } };

// ---- load module -------------------------------------------------------
vm.runInThisContext(fs.readFileSync(path.join(ROOT, 'viewer/scripts/predicates.js'), 'utf8'),
    { filename: 'predicates.js' });
const P = global.FEAPredicates;
assert(P, 'module loaded');
const T = P._test;

function leafAt(worldPoint, normal, opts) {
    const l = T.makeLeaf(worldPoint, normal);
    Object.assign(l, opts || {});
    if (opts && opts.normal_tol_deg !== undefined) l.cosNormTol = Math.cos(opts.normal_tol_deg * Math.PI / 180);
    if (l.finite) T.buildPredAxes(l);
    return l;
}
function ids(members, domain, key) {
    const m = (members || []).find(x => x.domain === domain);
    return m ? Array.from(m[key || 'ids']).sort((a, b) => a - b) : [];
}

// ---- T1: slab plane, offsets respected, shells + beams -----------------
T.reset();
let l1 = leafAt(W(120, 120, 0), [0, 0, 1], { tol: 0.5 });
let it1 = T.addItem(T.makeItem(l1, 'slab'));
let m1 = P.resolveMembers(it1.id);
assert.deepStrictEqual(ids(m1, 'shells'), [1001, 1002, 1003, 1004], 'T1 shells');
assert.deepStrictEqual(ids(m1, 'beams'), [5001], 'T1 beams (b0 on plane, no normal test)');
console.log('T1 ok  slab plane + offsets + beams');

// ---- T2: wall plane via normal test; nodes target ----------------------
T.reset();
let l2 = leafAt(W(0, 100, 50), [1, 0, 0], { tol: 0.5 });
let it2 = T.addItem(T.makeItem(l2, 'wall'));
let m2 = P.resolveMembers(it2.id);
assert.deepStrictEqual(ids(m2, 'shells'), [1005, 1006], 'T2 wall shells');
let l2n = leafAt(W(0, 100, 50), [1, 0, 0], { tol: 0.5 });
let it2n = T.addItem(T.makeItem(l2n, 'wall nodes', 'nodes'));
let m2n = P.resolveMembers(it2n.id);
assert.deepStrictEqual(ids(m2n, 'nodes', 'nodeIds'), [101, 104, 107, 110, 111, 112], 'T2 nodes at x=0');
console.log('T2 ok  normal test + nodes target');

// ---- T3: AND with negated finite child ---------------------------------
T.reset();
let a = leafAt(W(120, 120, 0), [0, 0, 1], { tol: 0.5 });
let b = leafAt(W(60, 60, 0), [0, 0, 1], { tol: 0.5, finite: true, width: 130, length: 130, angle_deg: 0 });
b.negated = true;
let op = T.makeOp('and', [a, b]);
let it3 = T.addItem(T.makeItem(op, 'slab minus corner'));
let m3 = P.resolveMembers(it3.id);
assert.deepStrictEqual(ids(m3, 'shells'), [1002, 1003, 1004], 'T3 shells: elem 0 negated away');
assert.deepStrictEqual(ids(m3, 'beams'), [], 'T3 beams: b0 caught by finite rect, negated away');
console.log('T3 ok  AND + negation + finite extents');

// ---- T4: buildPredAxes angle math --------------------------------------
let l4 = leafAt(W(0, 0, 0), [0, 0, 1], { finite: true, angle_deg: 90 });
assert(Math.abs(l4.uAxis[0]) < 1e-9 && Math.abs(l4.uAxis[1] - 1) < 1e-9, 'T4 u rotated to +Y');
assert(Math.abs(l4.vAxis[0] + 1) < 1e-9 && Math.abs(l4.vAxis[1]) < 1e-9, 'T4 v = n x u = -X');
console.log('T4 ok  in-plane axes rotation');

// ---- T5: production-dialect round-trip ---------------------------------
T.reset();
let leafF = leafAt(W(1, 2, 3), [0, 0, 1], { tol: 0.25, normal_tol_deg: 7.5, finite: true, width: 100, length: 200, angle_deg: 15 });
let leafP = leafAt(W(4, 5, 6), [1, 0, 0], { tol: 0.5 });
leafP.negated = true;
let tree = T.makeOp('or', [leafF, leafP]);
tree.negated = true;
let dial = P.exportNode(tree);
assert.strictEqual(dial.kind, 'or');
assert.strictEqual(dial.negated, true);
assert.deepStrictEqual(Object.keys(dial.children[0]).sort(),
    ['angle_deg', 'kind', 'length', 'negated', 'normal', 'normal_tol_deg', 'point', 'tol', 'width'],
    'T5 finitePlane keys match Groups.cs dialect');
assert.strictEqual(dial.children[0].kind, 'finitePlane');
assert.deepStrictEqual(Object.keys(dial.children[1]).sort(),
    ['kind', 'negated', 'normal', 'normal_tol_deg', 'point', 'tol'],
    'T5 plane keys match Groups.cs dialect');
let re = P.exportNode(P.importNode(dial));
assert.deepStrictEqual(re, dial, 'T5 import->export stable');
console.log('T5 ok  production dialect round-trip');

// ---- T6: legacy v3 predicates.json import ------------------------------
T.reset();
_env = null;
P.importLegacy({
    version: 3,
    expressions: [{ name: 'Legacy deck', type: 'plates',
        root: { kind: 'plane', negated: false, point: W(120, 120, 0), normal: [0, 0, 1], tol: 0.5, normal_tol_deg: 5 } }]
});
let items6 = P.items();
assert.strictEqual(items6.length, 1, 'T6 imported');
assert.strictEqual(items6[0].target, 'elements');
assert.deepStrictEqual(items6[0].domains, ['shells'], 'T6 plates -> shells domain');
let m6 = P.resolveMembers(items6[0].id);
assert.deepStrictEqual(ids(m6, 'shells'), [1001, 1002, 1003, 1004], 'T6 evaluates');
assert.strictEqual(ids(m6, 'beams').length, 0, 'T6 domain filter excludes beams');
assert(_env && _env.predicates.items.length === 1, 'T6 synced into (auto-created) envelope');
console.log('T6 ok  legacy v3 import + domain filter');

// ---- T7: envelope round-trip preserves unknown keys --------------------
T.reset();
_env = {
    format: 'pluto-features', version: 1,
    groups: { version: 1, items: [{ id: 'g-1', name: 'G', members: [{ predicateId: 'p-keep' }] }] },
    predicates: { version: 1, items: [{
        id: 'p-keep', name: 'Kept', target: 'elements', customKey: 'KEEP-ME',
        tree: { kind: 'plane', negated: false, point: W(120, 120, 0), normal: [0, 0, 1], tol: 0.5, normal_tol_deg: 5 }
    }] }
};
refreshCount = 0;
P.onEnvelope(_env);
assert.strictEqual(P.items().length, 1, 'T7 loaded');
assert(refreshCount > 0, 'T7 groups with predicate members trigger features refresh');
// mutate: rename, then check envelope
P.items()[0].name = 'Renamed';
P.createFromPick(W(0, 100, 50), [1, 0, 0]);     // adds a second item, triggers syncEnvelope
assert.strictEqual(_env.predicates.items.length, 2, 'T7 second item synced');
assert.strictEqual(_env.predicates.items[0].customKey, 'KEEP-ME', 'T7 unknown key preserved');
assert.strictEqual(_env.predicates.items[0].name, 'Renamed', 'T7 known key updated');
let m7 = P.resolveMembers('p-keep');
assert.deepStrictEqual(ids(m7, 'shells'), [1001, 1002, 1003, 1004], 'T7 resolves by stable id');
console.log('T7 ok  envelope round-trip, unknown keys, group refresh');

// ---- T8: offset invariance ---------------------------------------------
T.reset();
let l8 = leafAt(W(120, 120, 0), [0, 0, 1], { tol: 0.5 });
let it8 = T.addItem(T.makeItem(l8, 'inv'));
let before = ids(P.resolveMembers(it8.id), 'shells');
global.viewRecenter = null;   // model coordinates unchanged; only the stored->local mapping shifts
l8.point = [120 + WORLD_OFF[0], 120 + WORLD_OFF[1], 0 + WORLD_OFF[2]];
let after = ids(P.resolveMembers(it8.id), 'shells');
assert.deepStrictEqual(after, before, 'T8 same members under different recenter');
console.log('T8 ok  recenter invariance');

console.log('\nALL PREDICATE ENGINE TESTS PASSED');
