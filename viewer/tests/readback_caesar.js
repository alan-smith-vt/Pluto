// Headless readback of a CaesarToPluto export (geometry only, stage 1) through the viewer's own reader.
//   node viewer/tests/readback_caesar.js <outBase>                    (reads <outBase>.bin [+ .features.json])
//   node viewer/tests/readback_caesar.js <outBase> --expect <json>    (the export's RESULT json -- the runner's
//                                                                       "RESULT {...}" line saved to a file, or
//                                                                       CaesarExportResult.ToJson(): counts to match)
//   --up Y|Z  --unit in|mm|...                                        (expected META upAxis / units.length,
//                                                                       default: from --expect, else Y and in)
//   --merged                                                          (the sidecar merged a previous one: the
//                                                                       user may have changed hidden flags; the
//                                                                       defaults are then printed, not asserted.
//                                                                       Implied by a RESULT json whose
//                                                                       sidecarMerge is not empty)
// No THREE, no DOM. Checks: one beam domain and no shells; finite nodes; every ELEM record (2 stations,
// valid nodes, section A valid, taper slot 4 = 0 or a valid end-B section + 1); tapered beams = reducers
// + 2 x valves, every taper a Reducer / Valve beam, valve halves narrowing to the mid node (bow-tie);
// META upAxis / units; labels; the sidecar binds (geometryHash) and its groups: sizes cover
// every beam once and are shown, component groups follow them (Bend hidden), line groups hidden, SIF/tee
// node groups hidden, support-combination node groups non-empty, shown and written after every other
// caesar group (user groups that MergeFrom carried over follow them).
const fs = require('fs'), path = require('path'), vm = require('vm');
const V = path.join(__dirname, '..', 'scripts', 'format') + path.sep;
global.window = global; global.document = { getElementById: () => null };
for (const f of ['v3Reader.js', 'v4Reader.js', 'pluto.js']) vm.runInThisContext(fs.readFileSync(V + f, 'utf8'), { filename: f });

const argv = process.argv.slice(2);
const base = argv[0];
const opt = name => { const i = argv.indexOf(name); return i > 0 && i + 1 < argv.length ? argv[i + 1] : null; };
if (!base || base.startsWith('--')) { console.error('usage: node readback_caesar.js <outBase> [--expect <result.json>] [--up Y|Z] [--unit in]'); process.exit(2); }
let expect = null;
if (opt('--expect')) {
  const txt = fs.readFileSync(opt('--expect'), 'utf8').trim().replace(/^RESULT\s+/, '');
  expect = JSON.parse(txt);
}
const ec = expect ? expect.counts : null;
const wantUp = opt('--up') || (expect && expect.upAxis) || 'Y';
const wantUnit = opt('--unit') || (expect && expect.units && expect.units.length) || 'in';
const merged = argv.indexOf('--merged') > 0 || !!(expect && expect.sidecarMerge);

let fails = 0;
function assert(c, m) { if (!c) { fails++; console.error('FAIL ' + m); } else console.log('ok   ' + m); }
// export defaults (hidden flags): asserted on a fresh sidecar, reported on a merged one (user edits survive)
function dflt(c, m) { if (merged) console.log((c ? 'ok   ' : 'info ') + m + (c ? '' : ' -- differs (merged sidecar: user edit)')); else assert(c, m); }
const COMPONENTS = ['Valve', 'Flange', 'Flange pair', 'Rigid', 'Rigid link', 'Expansion joint', 'Reducer', 'Bend'];

(async () => {
  const m = await PlutoFormat.load(new Blob([fs.readFileSync(base + '.bin')]), () => {});
  console.log('v' + m.version + ' nodes=' + m.nNodes + ' domains=' + m.domains.map(d => d.name + '(' + d.family + '):' + d.nElem).join(',') +
    ' LCs=' + m.loadCases.length + ' units=' + JSON.stringify(m.units) + ' upAxis=' + m.upAxis + ' sections=' + m.sections.length);

  // ---- profile: one beam domain, no shells, geometry only ----
  assert(m.version === 4, 'v4 file');
  assert(m.domains.length === 1 && m.domains[0].family === 'beam' && m.domains[0].name === 'beams', 'exactly one domain, family beam');
  assert(PlutoFormat.findDomain(m, 'shell') === -1 && PlutoFormat.shellView(m).header.nElements === 0, 'no shell elements');
  assert(m.loadCases.length === 0 && m.domains[0].fields === null, 'geometry only: no load cases, no field block');
  assert(m.upAxis === wantUp && m.meta.upAxis === wantUp, 'META upAxis ' + JSON.stringify(m.meta.upAxis) + ' = ' + wantUp);
  assert(m.units && m.units.length === wantUnit, 'META units.length ' + JSON.stringify(m.units && m.units.length) + ' = ' + wantUnit);
  assert(/^sha256:[0-9a-f]{64}$/.test(m.geometryHash || ''), 'META geometryHash present');

  // ---- nodes ----
  assert(m.nNodes > 0, 'nNodes > 0 (' + m.nNodes + ')');
  let bad = 0;
  for (let i = 0; i < m.nNodes * 3; i++) if (!Number.isFinite(m.nodes[i])) bad++;
  assert(bad === 0, 'every node coordinate finite (' + bad + ' not)');
  const nodeIdx = new Map();
  for (let i = 0; i < m.nNodes; i++) nodeIdx.set(m.nodeIds[i], i);
  assert(nodeIdx.size === m.nNodes, 'node ids unique');
  let lo = [Infinity, Infinity, Infinity], hi = [-Infinity, -Infinity, -Infinity];
  for (let i = 0; i < m.nNodes; i++) for (let k = 0; k < 3; k++) { lo[k] = Math.min(lo[k], m.nodes[i * 3 + k]); hi[k] = Math.max(hi[k], m.nodes[i * 3 + k]); }
  console.log('bbox ' + lo.map(v => v.toFixed(2)).join(',') + ' .. ' + hi.map(v => v.toFixed(2)).join(','));
  if (ec) assert(m.nNodes === ec.nodes, 'node count = RESULT counts.nodes (' + ec.nodes + ')');

  // ---- beams: ELEM records, sections, taper slot ----
  const bv = PlutoFormat.beamView(m);
  const nB = bv.header.nElements, REC = bv.elemRecordU32, el = bv.elems, S = m.sections.length;
  assert(REC === 6 && nB > 0, nB + ' beams, 6 x u32 records');
  let badRec = 0, badSec = 0, badB = 0, zeroLen = 0, tapered = 0, notPipe = 0;
  const beamIdx = new Map(), taperIds = new Set();
  for (let e = 0; e < nB; e++) {
    const r = el.subarray(e * REC, e * REC + REC);
    if (r[0] !== 2 || r[1] >= m.nNodes || r[2] >= m.nNodes || r[1] === r[2] || r[5] !== 0) badRec++;
    if (!(r[3] < S)) badSec++;
    if (r[4] !== 0) {
      if (!(r[4] - 1 < S) || r[4] - 1 === r[3]) badB++;
      else {
        tapered++;
        taperIds.add(bv.elemIds[e]);
        const a = m.sections[r[3]], b = m.sections[r[4] - 1];
        if (a.type !== 'PIPE' || b.type !== 'PIPE') notPipe++;
      }
    }
    const dx = m.nodes[r[2] * 3] - m.nodes[r[1] * 3], dy = m.nodes[r[2] * 3 + 1] - m.nodes[r[1] * 3 + 1], dz = m.nodes[r[2] * 3 + 2] - m.nodes[r[1] * 3 + 2];
    if (!(Math.sqrt(dx * dx + dy * dy + dz * dz) > 0)) zeroLen++;
    beamIdx.set(bv.elemIds[e], e);
  }
  assert(badRec === 0, 'every ELEM record {2, nodeA, nodeB, sec, slot4, 0} with two valid distinct nodes (' + badRec + ' bad)');
  assert(badSec === 0, 'every beam section index valid (' + badSec + ' bad of ' + S + ' sections)');
  assert(badB === 0, 'every taper slot 4 is 0 or a valid end-B section + 1 that differs from A (' + badB + ' bad)');
  assert(zeroLen === 0, 'no zero-length beam (' + zeroLen + ')');
  assert(beamIdx.size === nB, 'beam ids unique');
  assert(m.sections.every(s => s.type === 'PIPE' && s.params.od > 0 && s.params.t >= 0), 'every section a PIPE with od > 0');
  assert(notPipe === 0, 'tapers run PIPE -> PIPE');
  console.log('tapered beams: ' + tapered);
  if (ec) {
    assert(nB === ec.beams, 'beam count = RESULT counts.beams (' + ec.beams + ')');
    assert(S === ec.sections, 'section count = RESULT counts.sections (' + ec.sections + ')');
    assert(tapered === ec.reducers + 2 * ec.valves, 'tapered beams ' + tapered + ' = reducers ' + ec.reducers + ' + 2 x valves ' + ec.valves);
    assert(tapered === ec.taperedBeams, 'tapered beams = RESULT counts.taperedBeams (' + ec.taperedBeams + ')');
  } else assert(tapered > 0, 'some beams taper (reducers / valve halves)');

  // ---- labels ----
  assert(bv.labels && bv.labels.count === nB, 'beam LABL block present');
  let badLbl = 0, arcs = 0, valveHalves = 0, badBowTie = 0, badArcNo = 0;
  const odOf = s => m.sections[s].params.od;
  for (let e = 0; e < nB; e++) {
    const l = bv.labels ? bv.labels.get(e) : '';
    if (!/^\d+-\d+( |$)/.test(l)) badLbl++;
    const arc = / arc (\d+)\/(\d+)$/.exec(l);
    if (arc) { arcs++; if (!(+arc[1] >= 1 && +arc[1] <= +arc[2])) badArcNo++; }
    const half = / valve ([12])\/2$/.exec(l);
    if (half) {
      valveHalves++;
      // bow-tie: each half narrows towards the valve's mid node (half 1: A wide -> B waist, half 2: A waist -> B wide)
      const r = el.subarray(e * REC, e * REC + REC), odA = odOf(r[3]), odB = r[4] ? odOf(r[4] - 1) : odA;
      if (!(half[1] === '1' ? odB < odA : odA < odB)) badBowTie++;
    }
  }
  assert(badLbl === 0, 'every beam label starts "FROM-TO" (' + badLbl + ' not)');
  assert(badArcNo === 0, 'every "arc k/n" label has 1 <= k <= n');
  assert(badBowTie === 0, 'valve halves narrow towards the mid node (bow-tie, ' + badBowTie + ' not)');
  if (ec) {
    assert(arcs === ec.bendChords, 'arc chord labels ' + arcs + ' = bend chords ' + ec.bendChords);
    assert(valveHalves === 2 * ec.valves, 'valve half labels ' + valveHalves + ' = 2 x valves');
  }
  console.log('sample labels: ' + [0, 1, Math.floor(nB / 2), nB - 1].map(e => bv.elemIds[e] + '="' + bv.labels.get(e) + '"').join('  '));

  // ---- sidecar ----
  const scPath = base + '.features.json';
  if (!fs.existsSync(scPath)) { assert(false, 'sidecar ' + scPath + ' exists'); }
  else {
    const sc = JSON.parse(fs.readFileSync(scPath, 'utf8'));
    const items = (sc.groups && sc.groups.items) || [];
    assert(sc.format === 'pluto-features' && sc.version === 1, 'sidecar envelope pluto-features v1');
    assert(sc.model.geometryHash === m.geometryHash, 'sidecar geometryHash binds to the binary');
    assert(sc.model.units && sc.model.units.length === wantUnit, 'sidecar units.length = ' + wantUnit);
    const mine = items.filter(g => (g.tags || []).indexOf('caesar') >= 0);
    const tagged = t => mine.filter(g => g.tags.indexOf(t) >= 0);
    const names = mine.map(g => g.name);
    assert(new Set(items.map(g => g.name)).size === items.length, 'group names unique (' + items.length + ' groups)');
    assert(new Set(items.map(g => g.id)).size === items.length, 'group ids unique');
    assert(mine.every(g => g.color === undefined || /^#[0-9a-f]{6}$/i.test(g.color)), 'colours are #rrggbb');
    const pos = g => items.indexOf(g);

    // sizes: every beam exactly once, shown
    const sizes = tagged('size');
    const cover = new Map();
    sizes.forEach(g => g.members.forEach(mm => (mm.ids || []).forEach(id => cover.set(id, (cover.get(id) || 0) + 1))));
    let missing = 0, twice = 0, unknown = 0;
    for (const id of beamIdx.keys()) { const c = cover.get(id) || 0; if (c === 0) missing++; if (c > 1) twice++; }
    for (const id of cover.keys()) if (!beamIdx.has(id)) unknown++;
    assert(sizes.length > 0 && sizes.every(g => /^PIPE /.test(g.name) && g.members.every(mm => mm.domain === 'beams')),
      sizes.length + ' size groups "PIPE ...", on beams');
    dflt(sizes.every(g => g.hidden === false), 'size groups shown');
    assert(missing === 0 && twice === 0 && unknown === 0, 'size groups cover every beam exactly once (missing ' + missing + ', twice ' + twice + ', unknown ' + unknown + ')');
    if (ec) assert(sizes.length === ec.sizeGroups, 'size groups = RESULT counts.sizeGroups');

    // components: after every size group, known names, Bend hidden, the rest shown, disjoint, real beam ids
    const comps = tagged('component');
    const compIds = new Map();
    let compUnknown = 0, compTwice = 0;
    comps.forEach(g => g.members.forEach(mm => (mm.ids || []).forEach(id => {
      if (!beamIdx.has(id)) compUnknown++;
      if (compIds.has(id)) compTwice++;
      compIds.set(id, g.name);
    })));
    assert(comps.length > 0 && comps.every(g => COMPONENTS.indexOf(g.name) >= 0), comps.length + ' component groups: ' + comps.map(g => g.name).join(', '));
    dflt(comps.every(g => g.hidden === (g.name === 'Bend')), 'component groups shown, Bend hidden');
    assert(comps.every(c => sizes.every(s => pos(c) > pos(s))), 'component groups written after the size groups');
    assert(compUnknown === 0 && compTwice === 0, 'component members are real beams, in one component group each');
    const count = name => { const g = comps.find(x => x.name === name); return g ? g.members.reduce((n, mm) => n + (mm.ids || []).length, 0) : 0; };
    console.log('component beams: ' + COMPONENTS.map(c => c + ' ' + count(c)).join(', '));
    // tapers come from reducers and valve halves only; every valve half tapers
    let taperOther = 0, valveStraight = 0;
    for (const id of taperIds) { const c = compIds.get(id); if (c !== 'Reducer' && c !== 'Valve') taperOther++; }
    for (const [id, c] of compIds) if (c === 'Valve' && !taperIds.has(id)) valveStraight++;
    assert(taperOther === 0, 'every tapered beam is in the Reducer or Valve group (' + taperOther + ' not)');
    assert(valveStraight === 0, 'every Valve beam is tapered (' + valveStraight + ' straight)');
    if (ec) {
      assert(count('Valve') === 2 * ec.valves, 'Valve group = 2 x valves (' + count('Valve') + ')');
      assert(count('Reducer') === ec.reducers + ec.reducersStraight, 'Reducer group = reducers (' + count('Reducer') + ')');
      assert(count('Flange') === ec.flanges && count('Flange pair') === ec.flangePairs, 'Flange / Flange pair groups = flanges / flange pairs');
      assert(count('Rigid') === ec.rigids && count('Rigid link') === ec.rigidLinks, 'Rigid / Rigid link groups = rigids / rigid links');
      assert(count('Expansion joint') === ec.expJoints, 'Expansion joint group = expansion joints');
      assert(count('Bend') === ec.bendChords, 'Bend group = bend chords (' + count('Bend') + ')');
    }

    // line numbers: hidden
    const lines = tagged('line');
    dflt(lines.every(g => g.hidden === true), lines.length + ' line-number group(s), hidden');

    // node groups: SIF / tee hidden, support combinations shown, non-empty, last, disjoint, real nodes
    const sif = mine.filter(g => g.tags.indexOf('sif') >= 0 || g.tags.indexOf('tee') >= 0);
    const sup = tagged('support');
    let nodeUnknown = 0;
    [].concat(sif, sup).forEach(g => g.members.forEach(mm => (mm.nodeIds || []).forEach(n => { if (!nodeIdx.has(n)) nodeUnknown++; })));
    assert(sif.every(g => g.members.every(mm => mm.domain === 'nodes')), sif.length + ' SIF / tee node group(s) on nodes');
    dflt(sif.every(g => g.hidden === true), 'SIF / tee node groups hidden');
    assert(sup.length > 0 && sup.every(g => g.members.every(mm => mm.domain === 'nodes' && (mm.nodeIds || []).length > 0)),
      sup.length + ' support-combination node groups, non-empty');
    dflt(sup.every(g => g.hidden === false), 'support-combination node groups shown');
    assert(nodeUnknown === 0, 'node group members are nodes of the binary');
    assert(sup.every(s => mine.every(o => sup.indexOf(o) >= 0 || pos(o) < pos(s))), 'support groups written after every other caesar group (they win a node)');
    const supNodes = new Map();
    let supTwice = 0;
    sup.forEach(g => g.members.forEach(mm => mm.nodeIds.forEach(n => { if (supNodes.has(n)) supTwice++; supNodes.set(n, g.name); })));
    assert(supTwice === 0, 'one support group per node (' + supNodes.size + ' nodes)');
    console.log('support groups: ' + sup.map(g => g.name + ' (' + g.members[0].nodeIds.length + ')').join(' | '));
    if (ec) {
      assert(sup.length === ec.restraintCombos && supNodes.size === ec.restraintNodes, 'support groups / nodes = RESULT restraintCombos / restraintNodes');
      assert(sif.length === ec.sifTeeGroups, 'SIF / tee groups = RESULT counts.sifTeeGroups');
      assert(items.length >= ec.groups, 'sidecar groups >= RESULT counts.groups (user groups merged in)');
    }
    console.log('groups: ' + names.length + ' caesar (' + sizes.length + ' sizes, ' + comps.length + ' components, ' + lines.length +
      ' lines, ' + sif.length + ' SIF/tee, ' + sup.length + ' supports), ' + (items.length - mine.length) + ' other');
  }
  console.log(fails ? fails + ' FAILED' : 'all passed');
  process.exitCode = fails ? 1 : 0;
})().catch(e => { console.error(e); process.exitCode = 1; });
