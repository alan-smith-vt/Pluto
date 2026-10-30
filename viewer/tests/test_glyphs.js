// Headless tests for glyphs.js collect() and the features.js look-group rules:
// anchors (beam t, node, centroid), insets, dir, tiers, tints, missing ids,
// and "a look group never paints". No THREE, no DOM.
//   node viewer/tests/test_glyphs.js
const fs = require('fs'), path = require('path'), vm = require('vm');
const V = path.join(__dirname, '..', 'scripts') + path.sep;

global.window = global;
global.document = { getElementById: () => null, createElement: () => ({ style: {}, classList: { add() {}, remove() {}, toggle() {} }, addEventListener() {}, appendChild() {} }) };
global.log = () => {};
global.needsRender = false;
global.feaBuild = null;
global.feaMaterial = null;
global.FEAShaders = { categoryColor: i => [i, i, i], makePaletteTexture: colors => ({ colors, dispose() {} }) };
global.FEAAttributes = { updateCatIdx() {} };

// shells: 2 quads over nodes 1..6; beams: 2 members over beam nodes 10..12
global.feaModel = {
  header: { nElements: 2, nNodes: 6 },
  elemIds: Uint32Array.from([101, 102]),
  nodeIds: Uint32Array.from([1, 2, 3, 4, 5, 6]),
  nodes: Float64Array.from([0, 0, 0, 2, 0, 0, 2, 2, 0, 0, 2, 0, 4, 0, 0, 4, 2, 0]),
  elems: Uint32Array.from([4, 0, 1, 2, 3, 4, 1, 4, 5, 2]),     // stride 5: [count, n0..n3]
  unified: { domains: [{ name: 'shells', family: 'shell' }, { name: 'beams', family: 'beam' }], geometryHash: 'h' }
};
const beamView = {
  header: { nElements: 2 },
  elemIds: Uint32Array.from([12, 13]),
  nodeIds: Uint32Array.from([10, 11, 12]),
  nodes: Float64Array.from([0, 0, 10, 10, 0, 10, 10, 5, 10]),
  elems: Uint32Array.from([2, 0, 1, 0, 0, 0, 2, 1, 2, 0, 0, 0])  // stride 6: [2, i, j, ...]
};
global.FEABeams = { view: () => beamView, mesh: () => null };

vm.runInThisContext(fs.readFileSync(V + 'glyphs.js', 'utf8'), { filename: 'glyphs.js' });
vm.runInThisContext(fs.readFileSync(V + 'models.js', 'utf8'), { filename: 'models.js' });
vm.runInThisContext(fs.readFileSync(V + 'features.js', 'utf8'), { filename: 'features.js' });

let fails = 0;
const assert = (c, m) => { if (!c) { fails++; console.error('FAIL ' + m); } else console.log('ok   ' + m); };
const near = (a, b) => Math.abs(a - b) < 1e-6;
const views = { shell: feaModel, beam: beamView };
const P = (d, i) => [d.pos[i * 3], d.pos[i * 3 + 1], d.pos[i * 3 + 2]];
const A = (d, i) => [d.along[i * 3], d.along[i * 3 + 1], d.along[i * 3 + 2]];

const items = [
  { name: 'Steel', color: '#8b919c', tags: ['context'], members: [{ domain: 'beams', ids: [12, 13] }], hidden: false },
  { name: 'End: hinged', look: { glyph: 'half', tier: 'detail', label: 'Hinged', section: 'Member ends', insetPx: 12 },
    anchors: [{ domain: 'beams', id: 12, t: 0 }, { domain: 'beams', id: 12, t: 1, label: 'SF12 at S40' },
              { domain: 'beams', id: 13, t: 0.5 }, { domain: 'beams', id: 999, t: 0 }] },
  { name: 'Guide', color: '#ffc04d', look: { glyph: 'ring-arrows' }, anchors: [{ domain: 'nodes', id: 5, dir: [0, 1, 0] }] },
  { name: 'Flag: span', look: { glyph: 'warn', tier: 'exception' }, members: [{ domain: 'shells', ids: [102] }] },
  { name: 'Odd', look: { glyph: 'no-such-glyph' }, members: [{ domain: 'nodes', nodeIds: [1] }] },
  { name: 'Off', look: { glyph: 'disc' }, hidden: true, anchors: [{ domain: 'nodes', id: 2 }] }
];

// ---- collect ---------------------------------------------------------------
const d = FEAGlyphs.collect(items, views, g => g.hidden === true);
const names = FEAGlyphs.names();
assert(d.n === 6, 'six glyphs: 3 hinged (1 missing id), 1 guide, 1 flag, 1 odd; hidden group none');
assert(d.legend.length === 5, 'legend rows for the five look groups (plain groups excluded)');
assert(d.legend[0].n === 3 && d.legend[0].missing === 1, 'missing anchor id is counted, not drawn');
assert(near(P(d, 0)[0], 0) && near(P(d, 1)[0], 10) && near(P(d, 2)[1], 2.5), 'beam anchors at t=0, t=1, t=0.5 along the member');
assert(near(A(d, 0)[0], 1) && near(A(d, 1)[0], -1), 'inset direction points from the anchored end into the member');
assert(d.inset[0] === 12 && d.inset[1] === 12 && d.inset[2] === 0, 'insetPx applies to end anchors only');
assert(near(A(d, 2)[0], 0) && near(A(d, 2)[1], 0), 'mid-span anchor has no inset direction');
assert(d.meta[1].label === 'SF12 at S40', 'anchor label carried for the tooltip');
assert(near(P(d, 3)[0], 4) && near(P(d, 3)[1], 0), 'node anchor at the node (id 5 -> (4,0,0))');
assert(d.dir[3 * 3 + 1] === 1 && d.dir[0] === 0, 'dir carried for the node anchor, zero elsewhere');
assert(d.glyph[3] === names.indexOf('ring-arrows'), 'guide glyph index');
assert(near(d.col[3 * 3], 1) && near(d.col[3 * 3 + 1], 0xc0 / 255), 'group colour tints the glyph');
assert(near(P(d, 4)[0], 3) && near(P(d, 4)[1], 1), 'no anchors: glyph at the member centroid (quad 102)');
assert(d.tier[4] === 1 && d.tier[0] === 0, 'exception tier = 1, detail (and default) = 0');
assert(near(d.col[4 * 3], 1) && near(d.col[4 * 3 + 1], 0x4d / 255), 'warn without colour gets the reserved alarm tint');
assert(near(d.col[0], 0xe8 / 255), 'no colour: neutral tint');
assert(d.glyph[5] === names.indexOf('square') && near(P(d, 5)[0], 0), 'unknown glyph name draws square; node member placed');
assert(d.legend[4].hidden === true && d.legend[4].n === 0, 'hidden look group: listed as hidden, no glyphs');
assert(d.legend[0].label === 'Hinged' && d.legend[0].section === 'Member ends' && d.legend[2].label === 'Flag: span', 'legend label falls back to the group name');

// ---- features.js: look groups never paint, default shown --------------------
let calls = [];
FEAGlyphs.update = (ctx, its, vw, hiddenOf) => calls.push({ its, hiddenOf });
FEAFeatures.setEnvelope({ format: 'pluto-features', version: 1, model: { geometryHash: 'h' }, groups: { version: 1, items: JSON.parse(JSON.stringify(items)) } }, 't.features.json');
const gl = FEAFeatures._groupList(), res = FEAFeatures._resolved();
assert(calls.length >= 1 && calls[calls.length - 1].its.length === items.length, 'features resolve hands the groups to the glyph layer');
assert(Array.from(res.shell).join() === '-1,-1', 'flag (look) group does not paint its shells');
assert(Array.from(res.nodes)[0] === -1, 'look node group does not paint its node');
assert(gl[1].hidden === false && gl[3].hidden === false, 'look group without a hidden flag starts shown');
assert(gl[5].hidden === true, 'look group with hidden: true stays hidden');
assert(gl[3].count === 1 && gl[3].painted === 0, 'look group still resolves its members (selection), paints none');
assert(Array.from(res.beam).join() === '0,0', 'plain group paints as before');

if (fails) { console.error(fails + ' failure(s)'); process.exit(1); } else console.log('all passed');
