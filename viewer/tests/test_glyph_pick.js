// Hover pick in glyphs.js: nearest glyph under the cursor, but never one the
// scene hides, using the shader's rule (detail glyphs pulled PULL_FRAC x span
// toward the camera; exception glyphs drawn over everything). Real three.js
// maths, stubbed canvas / renderer, injected occluder.
//   node viewer/tests/test_glyph_pick.js
const fs = require('fs'), path = require('path'), vm = require('vm');
const V = path.join(__dirname, '..') + path.sep;

global.window = global;
global.THREE = require(V + 'lib/three.min.js');
const noop = new Proxy(function () {}, { get: () => noop, apply: () => noop });
global.document = { getElementById: () => null, body: null, createElement: () => ({ width: 0, height: 0, style: {}, getContext: () => noop, toDataURL: () => '' }) };
global.needsRender = false;
global.renderer = { getSize: v => v.set(800, 600), getPixelRatio: () => 1 };
global.camera = new THREE.PerspectiveCamera(50, 800 / 600, 1, 5000);
camera.position.set(0, 0, 100); camera.lookAt(0, 0, 0); camera.updateMatrixWorld();

vm.runInThisContext(fs.readFileSync(V + 'scripts/glyphs.js', 'utf8'), { filename: 'glyphs.js' });

let fails = 0;
const assert = (c, m) => { if (!c) { fails++; console.error('FAIL ' + m); } else console.log('ok   ' + m); };

// nodes: A (0,0,0), B (1,0,0) a few px apart, E (0,1,0), C far behind at (0,0,-480).
// The span is ~480, so the detail distance is ~216 and the pull is ~4.8.
const view = { nodeIds: Uint32Array.from([1, 2, 3, 4]), nodes: Float64Array.from([0, 0, 0, 1, 0, 0, 0, -40, -480, 0, 1, 0]),
               elemIds: new Uint32Array(0), elems: new Uint32Array(0) };
const scene = new THREE.Scene();
const ctx = { markerParent: () => scene, look: { visible: true } };
const items = [
  { name: 'A', look: { glyph: 'disc', label: 'A' }, anchors: [{ domain: 'nodes', id: 1 }] },
  { name: 'B', look: { glyph: 'half', label: 'B' }, anchors: [{ domain: 'nodes', id: 2 }] },
  { name: 'C', look: { glyph: 'circle', label: 'C' }, anchors: [{ domain: 'nodes', id: 3 }] },
  { name: 'E', look: { glyph: 'warn', tier: 'exception', label: 'E' }, anchors: [{ domain: 'nodes', id: 4 }] }
];
FEAGlyphs.update(ctx, items, { shell: view, beam: null }, () => false);
scene.updateMatrixWorld(true);
const px = p => { const v = new THREE.Vector3(...p).project(camera); return [(v.x + 1) * 400, (1 - v.y) * 300]; };
const [ax, ay] = px([0, 0, 0]), [ex, ey] = px([0, 1, 0]);
const at = z => () => new THREE.Vector3(0, 0, z);

FEAGlyphs.setOccluder(() => null);
assert(FEAGlyphs.pick(ax, ay).label === 'A', 'no occluder: the glyph under the cursor');
assert(FEAGlyphs.pick(ax + 200, ay) === null, 'nothing within the pick radius: null');

FEAGlyphs.setOccluder((sx, sy) => Math.hypot(sx - ax, sy - ay) < 1 ? new THREE.Vector3(0, 0, 50) : null);
assert(FEAGlyphs.pick(ax, ay).label === 'B', 'glyph behind geometry is skipped; the next visible candidate wins');

FEAGlyphs.setOccluder(at(50));
assert(FEAGlyphs.pick(ax, ay).label === 'E', 'detail glyphs behind a wall are skipped; only the exception glyph nearby is picked');
assert(FEAGlyphs.pick(ax + 5, ay - 30) === null, 'nothing visible near the cursor: null (no tooltip through geometry)');
assert(FEAGlyphs.pick(ex, ey).label === 'E', 'exception glyph is drawn over everything, so it stays pickable');

FEAGlyphs.setOccluder(at(-0.01));
assert(FEAGlyphs.pick(ax, ay).label === 'A', 'glyph lying on a surface stays pickable');
FEAGlyphs.setOccluder(at(3));
assert(FEAGlyphs.pick(ax, ay).label === 'A', 'surface in front but within the pull (anchor inside a solid): picked');
FEAGlyphs.setOccluder(at(10));
assert(FEAGlyphs.pick(ax, ay).label !== 'A', 'surface in front beyond the pull: A hidden');

FEAGlyphs.setOccluder(() => null);
const [cx, cy] = px([0, -40, -480]);
assert(FEAGlyphs.pick(cx, cy) === null, 'detail glyph beyond the detail distance is not picked');
camera.position.set(0, -40, -430); camera.lookAt(0, -40, -480); camera.updateMatrixWorld();
const [cx2, cy2] = px([0, -40, -480]);
assert((FEAGlyphs.pick(cx2, cy2) || {}).label === 'C', 'detail glyph picked once the camera is near');

// support glyphs: default dir world -Z, no inset, drawn larger
const d = FEAGlyphs.collect([{ name: 'S', look: { glyph: 'support-pinned' }, anchors: [{ domain: 'nodes', id: 1 }] },
                             { name: 'F', look: { glyph: 'support-fixed', insetPx: 10 }, anchors: [{ domain: 'nodes', id: 2, dir: [1, 0, 0] }] }],
                            { shell: view, beam: null }, () => false);
assert(d.dir[2] === -1 && d.dir[3] === 1, 'support glyph: default dir -Z; explicit dir kept');
const bv = { elemIds: Uint32Array.from([7]), elems: Uint32Array.from([2, 0, 1, 0, 0, 0]), nodes: Float64Array.from([0, 0, 0, 10, 0, 0]), nodeIds: Uint32Array.from([1, 2]) };
const e = FEAGlyphs.collect([{ name: 'P', look: { glyph: 'support-pinned' }, anchors: [{ domain: 'beams', id: 7, t: 0 }, { domain: 'beams', id: 7, t: 1 }] }], { shell: view, beam: bv }, () => false);
assert(e.dir[0] === -1 && e.dir[3] === 1, 'support glyph at a beam end: base points out of the member (I-end -x, J-end +x)');
assert(d.scale[0] > 1 && d.inset[1] === 0, 'support glyph: drawn larger, never inset');
assert(FEAGlyphs.names().indexOf('support-fixed') >= 0 && FEAGlyphs.names().indexOf('support-pinned') >= 0, 'support glyph names in the set');

if (fails) { console.error(fails + ' failure(s)'); process.exit(1); } else console.log('all passed');
