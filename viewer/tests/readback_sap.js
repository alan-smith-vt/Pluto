// Headless readback of a SapToPluto export through the viewer's own reader.
//   node viewer/tests/readback_sap.js <outBase>            (reads <outBase>.bin [+ .features.json])
//   node viewer/tests/readback_sap.js <outBase> --tank     (adds the tank_hoop value assertions)
// No THREE, no DOM. The --tank checks are for the SapViewer sample model
// (pythonTools/sap/configs/tank_hoop.toml run through SAP2000): area 1 / joint 1
// DEAD forces and the HYDRO joint-1 displacement rotated out of its local axes.
const fs = require('fs'), path = require('path'), vm = require('vm');
const V = path.join(__dirname, '..', 'scripts', 'format') + path.sep;
global.window = global; global.document = { getElementById: () => null };
for (const f of ['v3Reader.js', 'v4Reader.js', 'pluto.js']) vm.runInThisContext(fs.readFileSync(V + f, 'utf8'), { filename: f });

const base = process.argv[2];
const tank = process.argv.includes('--tank');
if (!base) { console.error('usage: node readback_sap.js <outBase> [--tank]'); process.exit(2); }
const nearly = (a, b, tol) => Math.abs(a - b) <= tol;
let fails = 0;
function assert(c, m) { if (!c) { fails++; console.error('FAIL ' + m); } else console.log('ok   ' + m); }

(async () => {
  const m = await PlutoFormat.load(new Blob([fs.readFileSync(base + '.bin')]), () => {});
  console.log('v' + m.version + ' nodes=' + m.nNodes + ' domains=' + m.domains.map(d => d.name + ':' + d.nElem).join(',') +
    ' LCs=' + JSON.stringify(m.loadCases.map(l => l.name)) + ' units=' + JSON.stringify(m.units));
  const sh = PlutoFormat.shellView(m);
  const comps = sh.meta.components.map(c => c.name);
  console.log('components: ' + sh.meta.components.map(c => c.name + '[' + c.kind + ',' + c.unit + ']').join(' '));
  assert(m.version === 4, 'v4 file');
  assert(sh.labels && sh.labels.count === sh.header.nElements, 'shell LABL block present');
  assert(m.nodeLabels && m.nodeLabels.count === m.nNodes, 'node LABL block present');
  let zmax = -1e9; for (let i = 0; i < m.nNodes; i++) zmax = Math.max(zmax, m.nodes[i * 3 + 2]);
  console.log('zmax=' + zmax.toFixed(3) + ' (SAP Z up kept)');

  if (fs.existsSync(base + '.features.json')) {
    const sc = JSON.parse(fs.readFileSync(base + '.features.json', 'utf8'));
    assert(sc.model.geometryHash === m.geometryHash, 'sidecar geometryHash binds to the binary');
    console.log('groups: ' + sc.groups.items.map(g => g.name + '(' + g.members.map(mm => mm.domain + ':' + (mm.ids || mm.nodeIds).length).join('+') + ')').join(' | '));
  }

  if (tank) {
    assert(m.nNodes === 756 && sh.header.nElements === 720, 'tank_hoop counts 756/720');
    const e0 = Array.from(sh.elemIds).indexOf(1), n0 = Array.from(sh.nodeIds).indexOf(1);
    assert(sh.labels.get(e0) === '1' && m.nodeLabels.get(n0) === '1', 'labels are the SAP labels');
    const rec = Array.from(sh.elems.slice(e0 * 6, e0 * 6 + 6));
    let slot = -1; for (let k = 0; k < 4; k++) if (rec[1 + k] === n0) slot = k;
    assert(slot === 0, 'joint 1 is corner 0 of area 1');
    const nC = comps.length, nS = sh.header.maxCorners;
    const lcDead = m.loadCases.findIndex(l => l.name === 'DEAD'), lcHydro = m.loadCases.findIndex(l => l.name === 'HYDRO');
    assert(lcDead >= 0 && lcHydro >= 0, 'DEAD and HYDRO load cases present');
    const dead = await sh.readLC(lcDead), hydro = await sh.readLC(lcHydro);
    const at = (plane, comp) => plane[(e0 * nS + slot) * nC + comps.indexOf(comp)];
    assert(nearly(at(dead, 'F11'), 0.00138075688363791, 1e-6), 'DEAD F11 area1/joint1 = ' + at(dead, 'F11'));
    assert(nearly(at(dead, 'F22'), -0.317928172737093, 1e-6), 'DEAD F22 area1/joint1 = ' + at(dead, 'F22'));
    assert(nearly(at(dead, 'M22'), -1.03619591472662E-06, 1e-9), 'DEAD M22 area1/joint1 = ' + at(dead, 'M22'));
    assert(nearly(at(hydro, 'Translation X'), 0.0260965088850754, 1e-6), 'HYDRO joint1 Translation X (local U2, AngleA=-90 -> global +X) = ' + at(hydro, 'Translation X'));
    assert(nearly(at(hydro, 'Translation Y'), 0, 1e-6), 'HYDRO joint1 Translation Y ~ 0');
    if (comps.includes('Translation R'))
      assert(nearly(at(hydro, 'Translation R'), 0.0260965088850754, 1e-6), 'HYDRO joint1 Translation R = outward = ' + at(hydro, 'Translation R'));
    let nan = 0; for (let e = 0; e < 720; e++) for (let s = 0; s < nS; s++) if (Number.isNaN(dead[(e * nS + s) * nC])) nan++;
    assert(nan === 0, 'every quad corner has a DEAD F11 value (NaN slots: ' + nan + ')');
  }
  console.log(fails ? fails + ' FAILED' : 'all passed');
  process.exitCode = fails ? 1 : 0;
})().catch(e => { console.error(e); process.exitCode = 1; });
