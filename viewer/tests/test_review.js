// Headless tests for pipe.js (the sidecar's review, properties and model.source) and labels.js: checks
// read in order, a check located (its nodes and beams -> the points the fly-to frames and flashes), the
// property item of each beam, the readout summary, the source line, node-number labels (synthetic bend /
// valve points skipped in a pipe model), screen placement (behind the camera, off the canvas and
// overlapping labels dropped, priority first), and the sections
// round-tripping untouched. THREE under vm; no DOM.
//   node viewer/tests/test_review.js
const fs = require('fs'), path = require('path'), vm = require('vm');
const V = path.join(__dirname, '..', 'scripts') + path.sep;
const L = path.join(__dirname, '..', 'lib') + path.sep;

global.window = global; global.self = global;
global.document = { getElementById: () => null, createElement: () => ({ style: {}, classList: { add() {}, remove() {}, toggle() {} }, addEventListener() {}, appendChild() {} }) };
global.log = () => {};
global.needsRender = false;
global.localStorage = { getItem: () => null, setItem() {} };
vm.runInThisContext(fs.readFileSync(L + 'three.min.js', 'utf8'), { filename: 'three.min.js' });
global.scene = new THREE.Scene();
global.feaBuild = null; global.feaMaterial = null;
global.FEAAttributes = { updateCatIdx() {} };
global.fmt = v => String(Math.round(v * 1e4) / 1e4);

let fails = 0;
const assert = (c, m) => { if (!c) { fails++; console.error('FAIL ' + m); } else console.log('ok   ' + m); };
const near = (a, b, t = 1e-6) => Math.abs(a - b) <= t;

// nodes 10 (0,0,0), 20 (100,0,0), 30 (100,100,0), a bend point 10000000 (95,5,0); beams 100 (10-20), 200 (20-bend), 201 (bend-30)
const NODES = Float64Array.from([0, 0, 0, 100, 0, 0, 100, 100, 0, 95, 5, 0]);
const elems = new Uint32Array(3 * 6);
elems.set([0, 0, 1, 0, 0, 0], 0); elems.set([0, 1, 3, 0, 0, 0], 6); elems.set([0, 3, 2, 0, 0, 0], 12);
const view = { header: { nElements: 3 }, nodes: NODES, nodeIds: Uint32Array.from([10, 20, 30, 10000000]), elems, elemRecordU32: 6,
               elemIds: Uint32Array.from([100, 200, 201]), sections: [], unified: { upAxis: 'Y' } };
global.FEABeams = { view: () => view, build: () => null, mesh: () => null, lcData: () => null };
global.feaModel = { header: { nElements: 0, nNodes: 4 }, nodeIds: view.nodeIds, nodes: NODES, elemIds: new Uint32Array(0),
                    unified: { domains: [{ name: 'beams', family: 'beam' }], geometryHash: 'h' } };
// viewer.js globals the fly-to uses
const calls = { tween: [], highlight: 0 };
global.camera = new THREE.PerspectiveCamera(45, 1.5, 0.1, 1e5); camera.position.set(50, 50, 400); camera.lookAt(50, 50, 0); camera.updateMatrixWorld();
global.controls = { target: new THREE.Vector3(50, 50, 0) };
global.focusTween = null;
global.tweenFocusTo = p => { calls.tween.push(p.clone()); global.focusTween = { from: controls.target.clone(), to: p.clone() }; };
global.startHighlight = (g, mats) => { calls.highlight = g.children.length; };

vm.runInThisContext(fs.readFileSync(V + 'features.js', 'utf8'), { filename: 'features.js' });
vm.runInThisContext(fs.readFileSync(V + 'labels.js', 'utf8'), { filename: 'labels.js' });
vm.runInThisContext(fs.readFileSync(V + 'pipe.js', 'utf8'), { filename: 'pipe.js' });

const REVIEW = { version: 1, summary: '1 error(s), 1 warning(s), 1 notice(s), 1 info', items: [
  { id: 'r1', level: 'error', code: 'unsupported', text: 'nothing holds the model to ground', nodeIds: [10, 20, 30], elements: [1, 2], members: [{ domain: 'beams', ids: [100, 200, 201] }] },
  { id: 'r2', level: 'warning', code: 'tee-no-sif', text: '3 elements meet at node 20', nodeIds: [20], elements: [1, 2] },
  { id: 'r3', level: 'notice', code: 'restraint-code', text: 'restraint type(s) XROD (27) ...', nodeIds: [30] },
  { id: 'r4', level: 'info', code: 'build', text: 'a message with no place' }
] };
const PROPS = { version: 1, units: { length: 'in.', diameter: 'in.', wall: 'in.', temperature: 'F', pressure: 'lb./sq.in.' }, items: [
  { element: 1, from: 10, to: 20, kind: 'Pipe', members: [{ domain: 'beams', ids: [100] }], values: { dx: 100, dy: 0, dz: 0, length: 100, od: 6.625, wall: 0.28, T: [300], P: [150], material: 106 } },
  { element: 2, from: 20, to: 30, kind: 'Bend', name: 'B1', members: [{ domain: 'beams', ids: [200, 201] }],
    values: { dx: 0, dy: 100, dz: 0, length: 100, od: 6.625, wall: 0.28, T: [300, 0, 250], material: 106, bend: { radius: -4.0404, named: 'Short', code: 4, type: 0, nodes: [['M', 25]] } } }
] };
const SOURCE = { file: 'line.xlsx', kind: 'xlsx', bytes: 1234, saved: '2026-10-23T10:41:07Z', sha256: 'ab', model: 'cd', written: '2026-10-23T10:41:09Z' };
const env = () => ({ format: 'pluto-features', version: 1, model: { geometryHash: 'h', units: { length: 'in', worldOffset: '1000 2000 0' }, source: SOURCE },
                     groups: { version: 1, items: [] }, review: JSON.parse(JSON.stringify(REVIEW)), properties: JSON.parse(JSON.stringify(PROPS)) });

FEAPipe.onModelLoaded();
FEAFeatures.setEnvelope(env(), 'line.features.json');
const P = FEAPipe;

// 1. checks and source
assert(P._items().length === 4 && P._items()[0].level === 'error', 'four checks, the error first');
assert(P.sourceText() === 'from line.xlsx (xlsx) · saved 2026-10-23 10:41 UTC · preview 2026-10-23 10:41 UTC', 'source line: ' + P.sourceText());

// 2. locate: the error's nodes and beams
{
  const g = P._points(P._items()[0]);
  assert(g.pts.length === 3 && g.segs.length === 3, 'r1: 3 node points, 3 beam segments');
  assert(P.locate(P._items()[0]) === true, 'r1 located');
  const c = calls.tween[calls.tween.length - 1];
  assert(near(c.x, 50) && near(c.y, 50) && near(c.z, 0), 'fly-to centre = the middle of what it covers (50, 50, 0)');
  assert(focusTween.camTo && focusTween.camTo.distanceTo(c) > 100, 'the camera backs off to frame it');
  assert(calls.highlight === 6, 'flash: a ball per node and a sleeve per beam (' + calls.highlight + ')');
  assert(P.locate(P._items()[3]) === false, 'a check with no place cannot be located');
}

// 3. properties by beam
{
  assert(P.hasProperties(), 'properties read');
  assert(P._prop(0).element === 1 && P._prop(1).element === 2 && P._prop(2).element === 2, 'beams 100 -> element 1; 200 and 201 -> element 2 (the bend)');
  const s1 = P._summary(P._prop(0)), s2 = P._summary(P._prop(1));
  assert(s1 === 'Pipe 10-20 · 6.625 x 0.28 in. · T1 300 F · P1 150 lb./sq.in. · Mat 106', 'summary: ' + s1);
  assert(s2.indexOf('Bend 20-30 B1') === 0 && s2.indexOf('R Short') > 0, 'bend summary names its radius: ' + s2);
}

// 4. node numbers: real nodes only in a pipe model
{
  P._setNodeNums(true);
  const lab = FEALabels._layers().nodes;
  assert(lab.length === 3 && lab.map(l => l.text).join() === '10,20,30', 'node labels: 10, 20, 30 (the bend point 10000000 skipped)');
  P._setNodeNums(false);
  assert(FEALabels._layers().nodes.length === 0, 'off: none');
}

// 5. placement on screen
{
  FEALabels.set('nodes', []);
  FEALabels.set('t', [
    { pos: new THREE.Vector3(50, 50, 0), text: 'centre', priority: 1 },
    { pos: new THREE.Vector3(50.2, 50, 0), text: 'overlaps', priority: 1 },
    { pos: new THREE.Vector3(50.1, 50.1, 0), text: 'wins', priority: 5 },
    { pos: new THREE.Vector3(50, 50, 1000), text: 'behind', priority: 9 },
    { pos: new THREE.Vector3(5000, 50, 0), text: 'off', priority: 9 },
    { pos: new THREE.Vector3(0, 0, 0), text: 'corner', priority: 0 }
  ]);
  const placed = FEALabels.place(camera, 1200, 800).map(p => p.label.text);
  assert(placed[0] === 'wins', 'the highest priority is placed first: ' + placed.join(','));
  assert(placed.indexOf('centre') < 0 && placed.indexOf('overlaps') < 0, 'labels under it are dropped');
  assert(placed.indexOf('behind') < 0 && placed.indexOf('off') < 0, 'behind the camera and off the canvas: dropped');
  assert(placed.indexOf('corner') >= 0, 'a label elsewhere stays');
  FEALabels.set('t', []);
}

// 7. round trip
{
  const out = JSON.parse(FEAFeatures.exportJson());
  assert(JSON.stringify(out.review) === JSON.stringify(REVIEW) && JSON.stringify(out.properties) === JSON.stringify(PROPS) &&
         JSON.stringify(out.model.source) === JSON.stringify(SOURCE), 'review, properties and model.source export exactly as loaded');
}

// 8. cleared
FEAPipe.onModelCleared();
assert(!P.hasProperties() && P._items().length === 0, 'cleared');

console.log(fails ? fails + ' FAILED' : 'all passed');
process.exit(fails ? 1 : 0);
