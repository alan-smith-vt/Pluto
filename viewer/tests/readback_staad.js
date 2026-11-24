// Headless readback of a StaadToPluto export (scripts/staad/Run-StaadFrame.ps1) through the viewer's own reader.
//   node viewer/tests/readback_staad.js <outBase>              (reads <outBase>.bin + .features.json)
//   node viewer/tests/readback_staad.js <outBase> --probe      (adds the value checks for scripts/lib/probes/anl-prints-probe.anl)
// No THREE, no DOM. Checks: one beam domain and no shells, upAxis Y, length in inches; two stations per beam, every
// section index valid; the components in kind runs (force, moment, displacement, restraint, code) with |V| |M| |D| |F|
// the hypot of their parts; displacements and reactions one value per joint; a code ratio the same at both ends of a
// member; the sidecar bound to the binary, its groups and supports naming only nodes and beams that exist. --probe:
// the printed numbers of the probe, end A negated (the force inside the member), the PRIS 6 in square, the single
// angle turned 45 deg to its principal axes, the checks as entries of their own.
const fs = require('fs'), path = require('path'), vm = require('vm');
const V = path.join(__dirname, '..', 'scripts', 'format') + path.sep;
global.window = global; global.document = { getElementById: () => null };
for (const f of ['v3Reader.js', 'v4Reader.js', 'pluto.js']) vm.runInThisContext(fs.readFileSync(V + f, 'utf8'), { filename: f });

const base = process.argv[2];
const probe = process.argv.includes('--probe');
if (!base) { console.error('usage: node readback_staad.js <outBase> [--probe]'); process.exit(2); }
let fails = 0;
function assert(c, m) { if (!c) { fails++; console.error('FAIL ' + m); } else console.log('ok   ' + m); }
const near = (a, b, tol) => Math.abs(a - b) <= (tol === undefined ? 1e-4 * Math.max(1, Math.abs(a), Math.abs(b)) : tol);

(async () => {
  const m = await PlutoFormat.load(new Blob([fs.readFileSync(base + '.bin')]), () => {});
  console.log('v' + m.version + ' nodes=' + m.nNodes + ' domains=' + m.domains.map(d => d.name + ':' + d.nElem).join(',') + ' LCs=' + m.loadCases.length);
  assert(m.version === 4, 'v4 file');
  assert(m.domains.length === 1 && m.domains[0].family === 'beam', 'one domain, family beam');
  assert(m.upAxis === 'Y', 'upAxis Y (STAAD\'s axes kept)');
  assert(m.units && m.units.length === 'in', 'length in inches');
  const bv = PlutoFormat.beamView(m);
  const nB = bv.header.nElements, REC = bv.elemRecordU32, el = bv.elems, S = m.sections.length;
  const comps = bv.meta.components, CC = comps.length, MS = bv.header.maxCorners;
  console.log('components: ' + comps.map(c => c.name + '[' + c.kind + (c.unit ? ',' + c.unit : '') + ']').join(' '));
  assert(MS === 2, 'two stations per beam');
  const beamIdx = new Map(), nodeIdx = new Map();
  for (let i = 0; i < m.nNodes; i++) nodeIdx.set(m.nodeIds[i], i);
  let badRec = 0;
  for (let e = 0; e < nB; e++) {
    const r = el.subarray(e * REC, e * REC + REC);
    if (r[0] !== 2 || r[1] >= m.nNodes || r[2] >= m.nNodes || r[1] === r[2] || !(r[3] < S) || r[4] !== 0) badRec++;
    beamIdx.set(bv.elemIds[e], e);
  }
  assert(badRec === 0, 'every ELEM record {2, nodeA, nodeB, valid section, straight} (' + badRec + ' bad)');
  // kinds in runs, each kind once
  const kinds = comps.map(c => c.kind), runs = kinds.filter((k, i) => i === 0 || kinds[i - 1] !== k);
  assert(new Set(runs).size === runs.length, 'each component kind one contiguous run (' + runs.join(' ') + ')');
  const ci = n => comps.findIndex(c => c.name === n);
  const has = n => ci(n) >= 0;

  const sc = JSON.parse(fs.readFileSync(base + '.features.json', 'utf8'));
  assert(sc.model.geometryHash === m.geometryHash, 'sidecar geometryHash binds to the binary');
  const beamSet = new Set(Array.from(bv.elemIds)), nodeSet = new Set(Array.from(m.nodeIds));
  let badGroup = 0;
  for (const g of sc.groups.items) for (const mm of g.members) {
    if (mm.domain === 'beams' && !(mm.ids || []).every(id => beamSet.has(id))) badGroup++;
    if (mm.nodeIds && !mm.nodeIds.every(id => nodeSet.has(id))) badGroup++;
  }
  assert(badGroup === 0, 'every group names beams and nodes that exist (' + sc.groups.items.length + ' groups)');
  const sup = sc.supports && sc.supports.items || [];
  assert(sup.every(it => (it.nodeIds || []).every(id => nodeSet.has(id))), sup.length + ' support items on joints that exist');
  const supportNodes = new Set(); sup.forEach(it => (it.nodeIds || []).forEach(n => supportNodes.add(n)));

  // beam ends per joint
  const endsAt = new Map();
  for (let e = 0; e < nB; e++) for (let k = 0; k < 2; k++) {
    const n = m.nodeIds[el[e * REC + 1 + k]];
    if (!endsAt.has(n)) endsAt.set(n, []);
    endsAt.get(n).push([e, k]);
  }
  const hyp = (a, b, c) => Math.hypot(a, b, c || 0);
  let magBad = 0, splitBad = 0, offSupport = 0, codeSplit = 0;
  const planes = [];
  for (let lc = 0; lc < m.loadCases.length; lc++) {
    const p = await bv.readLC(lc);
    planes.push(p);
    const at = (e, k, c) => p[e * MS * CC + k * CC + c];
    for (let e = 0; e < nB; e++) for (let k = 0; k < 2; k++) {
      const chk = (mag, parts) => { const v = at(e, k, ci(mag)); if (Number.isFinite(v) && !near(v, hyp.apply(null, parts.map(n => at(e, k, ci(n)))))) magBad++; };
      if (has('Shear |V|')) chk('Shear |V|', ['Shear Fy', 'Shear Fz']);
      if (has('Bending |M|')) chk('Bending |M|', ['Bending My', 'Bending Mz']);
      if (has('|D|')) chk('|D|', ['DX', 'DY', 'DZ']);
      if (has('Reaction |F|')) chk('Reaction |F|', ['Reaction FX', 'Reaction FY', 'Reaction FZ']);
      if (has('Code ratio')) { const a = at(e, 0, ci('Code ratio')), b = at(e, 1, ci('Code ratio')); if (!(Number.isNaN(a) && Number.isNaN(b)) && a !== b) codeSplit++; }
    }
    for (const [n, list] of endsAt) for (const name of ['DX', 'RZ', 'Reaction FY']) {
      if (!has(name)) continue;
      const vals = list.map(([e, k]) => at(e, k, ci(name))), fin = vals.filter(Number.isFinite);
      if (fin.length && (fin.length !== vals.length || fin.some(v => v !== fin[0]))) splitBad++;
      if (name === 'Reaction FY' && fin.length && !supportNodes.has(n)) offSupport++;
    }
  }
  assert(magBad === 0, '|V| |M| |D| |F| are the hypot of their parts (' + magBad + ' not)');
  assert(splitBad === 0, 'displacements and reactions one value per joint, on every member end there (' + splitBad + ' not)');
  assert(offSupport === 0, 'reactions only at support joints (' + offSupport + ' not)');
  assert(codeSplit === 0, 'a code ratio the same at both ends of a member (' + codeSplit + ' not)');

  if (probe) {
    const lcOf = name => m.loadCases.findIndex(l => l.name === name);
    const val = (lcName, member, end, comp) => planes[lcOf(lcName)][beamIdx.get(member) * MS * CC + end * CC + ci(comp)];
    assert(nB === 32 && m.nNodes === 18, '32 members, 18 joints');
    assert(lcOf('101: 1.4D') >= 0 && lcOf('Check 1: AISC 360-16 LRFD (V1.2), worst of every case') >= 0, 'case 101 and check 1 by name');
    // member 101, case 101: printed FX 21.29 / -20.46, MZ -41.21 / -92.67 (end A negated)
    assert(near(val('101: 1.4D', 101, 0, 'Axial Fx'), -21.29) && near(val('101: 1.4D', 101, 1, 'Axial Fx'), -20.46), 'member 101 axial: compression negative at both ends');
    assert(near(val('101: 1.4D', 101, 0, 'Bending Mz'), 41.21) && near(val('101: 1.4D', 101, 1, 'Bending Mz'), -92.67), 'member 101 Mz: end A negated, end B as printed');
    // member 201, case 102: MZ 556.45 / -815.61 -> both ends hogging, the same sign
    assert(near(val('102: 1.2D + 1.6L + 0.5LR', 201, 0, 'Bending Mz'), -556.45) && near(val('102: 1.2D + 1.6L + 0.5LR', 201, 1, 'Bending Mz'), -815.61), 'member 201 end moments the same sign');
    // reaction at joint 1, case 101: 2.07 22.18 0 0.18 0 -41.21
    const e1 = endsAt.get(1)[0];
    const r = n => planes[lcOf('101: 1.4D')][e1[0] * MS * CC + e1[1] * CC + ci(n)];
    assert(near(r('Reaction FX'), 2.07) && near(r('Reaction FY'), 22.18) && near(r('Reaction MZ'), -41.21), 'joint 1 reaction in case 101 as printed');
    // joint 11, case 1: 0.00069 -0.00514 -0.00038 0 -1e-05 -0.00034
    const e11 = endsAt.get(11)[0];
    const d = n => planes[lcOf('1: DEAD')][e11[0] * MS * CC + e11[1] * CC + ci(n)];
    assert(near(d('DX'), 0.00069, 1e-9) && near(d('DY'), -0.00514, 1e-9) && near(d('RZ'), -0.00034, 1e-9), 'joint 11 displacement in case 1 as printed');
    // code: member 101 check 1 FAIL 1.071 in case 104; 303 1.787 in case 1; 306 not designed (NaN)
    const ck = 'Check 1: AISC 360-16 LRFD (V1.2), worst of every case';
    assert(near(val(ck, 101, 0, 'Code ratio'), 1.071) && val(ck, 101, 0, 'Code case') === 104, 'member 101 check 1: 1.071 in case 104');
    assert(near(val(ck, 303, 1, 'Code ratio'), 1.787) && val(ck, 303, 1, 'Code case') === 1, 'member 303 check 1: 1.787 in case 1');
    assert(Number.isNaN(val(ck, 306, 0, 'Code ratio')), 'member 306 not designed: no ratio');
    assert(Number.isNaN(val('101: 1.4D', 101, 0, 'Code ratio')), 'no code ratio in case 101 (the probe checks lists of several cases)');
    // sections: the PRIS 6 in square; the single angle 303 turned -45 deg from its BETA-0 axes
    const sec = s => m.sections[el[beamIdx.get(s) * REC + 3]];
    const pris = sec(306);
    assert(pris.type === 'RECT' && near(pris.paramList[0], 6) && near(pris.paramList[1], 6), 'PRIS YD 0.5 ZD 0.5 (feet): a 6 in square');
    const bp = bv.beamProps, BP = 8, y = i => [bp[i * BP], bp[i * BP + 1], bp[i * BP + 2]];
    const y306 = y(beamIdx.get(306)), y303 = y(beamIdx.get(303));
    assert(near(y306[0], 0, 1e-6) && near(y306[1], 0, 1e-6) && near(y306[2], -1, 1e-6), 'member 306 (BETA 0): viewer y = -STAAD z');
    if (sec(303).type === 'L') {
      const cos = y303[0] * y306[0] + y303[1] * y306[1] + y303[2] * y306[2];
      assert(near(Math.abs(cos), Math.SQRT1_2, 1e-4), 'single angle 303 turned 45 deg to its principal axes (cos ' + cos.toFixed(5) + ')');
    } else console.log('skip angle 303 turn: drawn as ' + sec(303).type + ' (no SAP library entry)');
    const names = sc.groups.items.map(g => g.name);
    assert(names.includes('W10X49') && names.some(n => n.startsWith('FAIL ')), 'section groups and FAIL groups in the sidecar');
    assert(sup.length === 6, 'six supports');
  }
  console.log(fails ? fails + ' FAILED' : 'all passed');
  process.exit(fails ? 1 : 0);
})().catch(e => { console.error(e); process.exit(1); });
