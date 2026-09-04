// Headless tests for features.js group resolution: precedence, hidden groups,
// painted counts, node groups, reorder. No THREE, no DOM.
//   node viewer/tests/test_groups.js
const fs = require('fs'), path = require('path'), vm = require('vm');
const V = path.join(__dirname, '..', 'scripts') + path.sep;

global.window = global;
global.document = { getElementById: () => null, createElement: () => ({ style: {}, classList: { add() {}, remove() {}, toggle() {} }, addEventListener() {}, appendChild() {} }) };
global.log = () => {};
global.needsRender = false;
global.feaBuild = null;
global.feaMaterial = null;
global.FEAShaders = {
  categoryColor: i => [i, i, i],
  makePaletteTexture: colors => ({ colors, dispose() {} })
};
global.FEAAttributes = { updateCatIdx() {} };
// 10 shells with real ids 101..110; 4 nodes with real ids 1..4
global.feaModel = {
  header: { nElements: 10, nNodes: 4 },
  elemIds: Uint32Array.from([101, 102, 103, 104, 105, 106, 107, 108, 109, 110]),
  nodeIds: Uint32Array.from([1, 2, 3, 4]),
  nodes: Float64Array.from([0, 0, 0, 1, 0, 0, 2, 0, 0, 3, 0, 0]),
  unified: { domains: [{ name: 'shells', family: 'shell' }], geometryHash: 'h' }
};
vm.runInThisContext(fs.readFileSync(V + 'features.js', 'utf8'), { filename: 'features.js' });

let fails = 0;
const assert = (c, m) => { if (!c) { fails++; console.error('FAIL ' + m); } else console.log('ok   ' + m); };
const env = () => ({
  format: 'pluto-features', version: 1, model: { geometryHash: 'h' },
  groups: { version: 1, items: [
    { name: 'WALL', members: [{ domain: 'shells', ids: [101, 102, 103, 104, 105, 106, 107, 108, 109, 110] }] },
    { name: 'C1', members: [{ domain: 'shells', ids: [101, 102, 103, 104, 105] }] },
    { name: 'C2', members: [{ domain: 'shells', ids: [106, 107, 108, 109, 110] }] },
    { name: 'BASE', members: [{ domain: 'nodes', nodeIds: [1, 2, 3] }] },
    { name: 'RIM', members: [{ domain: 'nodes', nodeIds: [3, 4, 99] }] }
  ] }
});

// 1. precedence: later groups shadow WALL completely
FEAFeatures.setEnvelope(env(), 't.features.json');
let gl = FEAFeatures._groupList();
assert(gl.map(g => g.name).join() === 'WALL,C1,C2,BASE,RIM', 'five groups listed');
assert(gl[0].count === 10 && gl[0].painted === 0, 'WALL: 10 members, 0 painted (shadowed by courses)');
assert(gl[1].painted === 5 && gl[2].painted === 5, 'C1 / C2 paint 5 each');
assert(gl[3].nodeCount === 3 && gl[3].count === 0, 'BASE is a node group (3 nodes, no elements)');
assert(gl[3].nodePainted === 2 && gl[4].nodePainted === 2, 'node precedence: RIM (later) takes node 3 from BASE');
assert(Array.from(FEAFeatures._resolved().nodes).join() === '3,3,4,4', 'per-node category: BASE,BASE,RIM,RIM');
assert(FEAFeatures._resolved().unmatched === 1, 'unknown node id 99 counted as unmatched');

// 2. hiding the courses lets WALL paint
const e = FEAFeatures.envelope();
e.groups.items[1].hidden = true; e.groups.items[2].hidden = true;
FEAFeatures.refresh();
gl = FEAFeatures._groupList();
assert(gl[0].painted === 10, 'WALL paints all 10 once courses are hidden');
assert(gl[1].hidden && gl[1].painted === 0 && gl[1].count === 5, 'hidden course keeps its member count, paints 0');

// 3. reorder: WALL moved to the end wins over the courses
delete e.groups.items[1].hidden; delete e.groups.items[2].hidden;
FEAFeatures._moveGroup(0, 5);            // to the end (index in original list)
FEAFeatures.refresh();
gl = FEAFeatures._groupList();
assert(gl.map(g => g.name).join() === 'C1,C2,BASE,RIM,WALL', 'WALL now last: ' + gl.map(g => g.name).join());
assert(gl[4].painted === 10 && gl[0].painted === 0, 'last group wins after reorder');
// 3b. hiding a node group frees its nodes for the one above
e.groups.items[3].hidden = true;                 // RIM
FEAFeatures.refresh();
gl = FEAFeatures._groupList();
assert(gl[2].nodePainted === 3 && gl[3].nodePainted === 0, 'hidden RIM: BASE paints all 3 nodes');
delete e.groups.items[3].hidden;
FEAFeatures.refresh();

// 4. export round-trips hidden + order
e.groups.items[0].hidden = true;
const out = JSON.parse(FEAFeatures.exportJson());
assert(out.groups.items[0].name === 'C1' && out.groups.items[0].hidden === true, 'export keeps order and hidden flag');

// 5. groupOf reports the winning group when colouring is on
FEAFeatures._setEnabled(true);
assert(FEAFeatures.groupOf('shell', 0) === 'WALL', 'groupOf(shell 0) = WALL (winner)');
assert(FEAFeatures.groupOf('node', 3) === 'RIM' && FEAFeatures.groupOf('node', 0) === 'BASE', 'groupOf(node) reports the node group');

console.log(fails ? fails + ' FAILED' : 'ALL GROUP TESTS PASSED');
process.exitCode = fails ? 1 : 0;
