// ================================================================
// loads.js  --  applied loads from the sidecar `loads` section (2026-10-23).
// Spec: vault/format/features-sidecar.md (loads); written by the CAESAR
// export (PipeSidecar.cs) from the pipe model: the viewer is the
// confirmation step before CAESAR imports the model.
//
// Items: {id, name, kind: force | displacement | uniform | wind, nodeIds,
// elements, members: [{domain: "beams", ids}], vectors: [{v, values, g}],
// wind: {type, shape}}. Load vectors are CAESAR's (F1..F9, D1..D9,
// U1..U9); load cases are not in the model. Drawn as:
//   force         an arrow pushing on the pipe along the force, its tip on
//                 the pipe surface; length grows with |F| (0.4 .. 1 of the
//                 longest)
//   moment        a double-headed arrow along the moment vector, out from
//                 the pipe (the right-hand rule)
//   displacement  an arrow out from the pipe along the imposed movement; a
//                 rotation double-headed; a vector imposing only zeros
//                 (held) draws nothing -- the support plate shows it
//   uniform       a row of arrows along the loaded pipe, tips on its surface
//                 (per length or in g's)
//   wind          a row of arrows from the side the panel's "Wind from"
//                 names: the neutral file holds the shape factor only, the
//                 direction and speed are in CAESAR's load cases
// The panel filters by type and by vector, sizes the symbols (relative to
// the pipe, like the supports) and labels them (FEALabels). Hover gives the
// load; a pin gives every vector of the item, and of the node or element.
//
// Version 2 (2026-11-05, SAP frame models, FrameSidecar.cs): the section
// also holds `patterns`, `cases` and `combinations`, and every item names
// its `pattern`. Joint forces / ground displacements stay force /
// displacement items (one vector); frame loads are
//   point         {members, frame, type: force | moment, dir, at: {rel, abs}, value}
//                 one arrow at `at` along the member (a moment double-headed)
//   distributed   {..., from, to, valueA, valueB}: a row of arrows from `from`
//                 to `to` whose lengths follow the value, a line through their
//                 tails (a trapezoid)
//   temperature   {members, frame, type, dT}: a translucent band along the
//                 member, red when hotter, blue when colder
// `dir` is SAP's: Gravity (-Z), X Y Z, the frame's local 1 2 3 (1 = I -> J,
// 2 = the viewer's local z, 3 = minus its local y; v4-schema 4.3) and the
// projected "X Proj" ... (drawn along the global axis). The "Loads of"
// select picks all patterns, a pattern, a case (its patterns x sf) or a
// combination (Linear Add: factors multiplied down the tree; any other
// type: the union of what it reaches); remembered per model. A pattern's
// self weight is listed, not drawn. Version-1 sections have no select and
// draw as before.
//
// Hooks (guarded by window.FEALoads): viewer.js load / clear, feaPick,
// showReadout, pin; features.js sync -> onFeatures; sectionCut.js
// writeVis. pipe.js lists itemsAt() with vectorText() / itemText().
// ================================================================

var FEALoads = (function () {

    var view = null;          // beam domain view (primary file)
    var nodeIndex = null;     // node id -> index
    var beamIndex = null;     // beam id -> index
    var nodeR = null;         // node index -> outer radius drawn there
    var beamR = null;         // beam index -> outer radius
    var frames = null;        // beam index -> X, Y, Z unit vectors (FEABeamGeometry build), or null
    var span = 1;
    var entries = [];         // one per drawn load: {item, kind, v, ni | beams, vec, mag, label}
    var itemsRef = null;
    var units = {};
    var nodeKeep = null;
    var visible = true, size = 1, labels = true;
    var vectorPick = 0;       // 0 = all vectors
    var windFrom = '-X';      // the side wind arrows come from (+X = blowing towards +X)
    var kindsOn = { force: true, moment: true, displacement: true, uniform: true, wind: true, temperature: true };
    var meshes = null, root = null, material = null, sleeveMaterial = null, shown = [], lastSig = '';
    var maxMag = {};          // kind -> largest magnitude (arrow length scale)
    // version 2
    var v2 = false;           // the section has patterns (a SAP model)
    var pick = 'all';         // 'all' | 'p:<pattern>' | 'c:<case>' | 'k:<combination>'
    var res = null;           // resolve(section, pick)
    var pickSec = null;       // the section the select was filled from
    var sleeveN = null;       // beam index -> bands drawn on it so far (one rebuild)

    var PRIMS = ['cone', 'cyl', 'sleeve'];
    var PICKABLE = ['cone', 'cyl'];           // a band would hide the member's own readout
    var KIND_RGB = {
        force: [239, 83, 80], moment: [171, 71, 188], displacement: [236, 64, 122],
        uniform: [102, 187, 106], g: [38, 166, 154], wind: [100, 181, 246],
        hot: [229, 57, 53], cold: [30, 136, 229]
    };
    var MIN_UNIT_FRACTION = 0.0025;
    var MAX_ROW_ARROWS = 4000;
    var KINDS = ['force', 'moment', 'displacement', 'uniform', 'wind', 'temperature'];
    var BEAM_KINDS = { point: 1, distributed: 1, temperature: 1 };

    var elSection = document.getElementById('ldSection');
    var elShow    = document.getElementById('ldShow');
    var elSize    = document.getElementById('ldSize');
    var elVector  = document.getElementById('ldVector');
    var elLabels  = document.getElementById('ldLabels');
    var elWind    = document.getElementById('ldWind');
    var elCount   = document.getElementById('ldCount');
    var elNote    = document.getElementById('ldNote');
    var elPick    = document.getElementById('ldPick');
    var elPickRow = document.getElementById('ldPickRow');
    var elPickNote = document.getElementById('ldPickNote');
    var elKinds   = {};
    KINDS.forEach(function (k) {
        elKinds[k] = document.getElementById('ldK' + k.charAt(0).toUpperCase() + k.slice(1));
    });

    try {
        var sz = parseFloat(localStorage.getItem('pluto.ldSize'));
        if (sz > 0) size = sz;
        var wf = localStorage.getItem('pluto.ldWind');
        if (wf) windFrom = wf;
    } catch (e0) {}
    if (elSize) elSize.value = String(size);

    // ---- vectors ---------------------------------------------------------
    function v3(a) { return new THREE.Vector3(a[0], a[1], a[2]); }
    function perp(a) {
        var t = Math.abs(a.x) < 0.9 ? new THREE.Vector3(1, 0, 0) : new THREE.Vector3(0, 1, 0);
        return t.cross(a).normalize();
    }
    var Y = new THREE.Vector3(0, 1, 0), Z = new THREE.Vector3(0, 0, 1);
    function modelUp() {
        var a = view && view.unified ? view.unified.upAxis : null;
        if (a === 'Z') return Z;
        if (a === 'Y') return Y;
        return (typeof zUp !== 'undefined' && zUp) ? Z : Y;
    }
    function num(x) { return (x === null || x === undefined || x === '') ? NaN : +x; }
    function mag3(a, o) { o = o || 0; var x = num(a[o]) || 0, y = num(a[o + 1]) || 0, z = num(a[o + 2]) || 0; return Math.sqrt(x * x + y * y + z * z); }
    function clamp01(t) { return t < 0 ? 0 : t > 1 ? 1 : t; }

    // ---- model -----------------------------------------------------------
    function onModelLoaded() {
        onModelCleared();
        view = window.FEABeams ? FEABeams.view() : null;
        if (!view) return;
        nodeIndex = new Map();
        for (var i = 0; i < view.nodeIds.length; i++) nodeIndex.set(view.nodeIds[i], i);
        beamIndex = new Map();
        for (var e = 0; e < view.elemIds.length; e++) beamIndex.set(view.elemIds[e], e);
        var REC = view.elemRecordU32, elems = view.elems, nE = view.header.nElements;
        var build = FEABeams.build(), secs = view.sections || [];
        frames = build && build.frames && build.frames.length >= nE * 9 ? build.frames : null;
        nodeR = new Float64Array(view.nodes.length / 3);
        beamR = new Float64Array(nE);
        for (var b = 0; b < nE; b++) {
            for (var k = 0; k < 2; k++) {
                var si = k === 0 ? build.sectionOf[b] : build.sectionOfB[b];
                var r = sectionRadius(secs[si]);
                var n = elems[b * REC + 1 + k];
                if (r > nodeR[n]) nodeR[n] = r;
                if (r > beamR[b]) beamR[b] = r;
            }
        }
        var lo = [Infinity, Infinity, Infinity], hi = [-Infinity, -Infinity, -Infinity], nd = view.nodes;
        for (var j = 0; j < nd.length; j += 3) for (var a = 0; a < 3; a++) {
            if (nd[j + a] < lo[a]) lo[a] = nd[j + a];
            if (nd[j + a] > hi[a]) hi[a] = nd[j + a];
        }
        span = Math.max(hi[0] - lo[0], hi[1] - lo[1], hi[2] - lo[2]);
        if (!(span > 0) || !isFinite(span)) span = 1;
        populateWind();
    }
    function sectionRadius(sec) {
        if (!sec || !sec.params) return 0;
        var p = sec.params;
        if (sec.type === 'PIPE') return (p.od || 0) / 2;
        var m = 0;
        ['b', 'h', 'd', 'bf', 'bfTop', 'bfBot'].forEach(function (k) { if (p[k] > m) m = p[k]; });
        return m / 2;
    }
    function onModelCleared() {
        disposeMeshes();
        view = null; nodeIndex = null; beamIndex = null; nodeR = null; beamR = null; frames = null; span = 1;
        entries = []; itemsRef = null; nodeKeep = null; lastSig = ''; shown = []; maxMag = {};
        v2 = false; res = null; pickSec = null;
        if (window.FEALabels) FEALabels.set('loads', []);
        if (elSection) elSection.style.display = 'none';
    }
    function disposeMeshes() {
        if (root && typeof scene !== 'undefined' && scene) scene.remove(root);
        if (meshes) PRIMS.forEach(function (p) {
            var m = meshes[p] && meshes[p].mesh;
            if (!m) return;
            m.geometry.dispose();
            if (m.dispose) m.dispose();
        });
        meshes = null; root = null;
    }

    // A beam's ends and unit axis (node to node), or null when it has no length.
    function beamLine(b) {
        var nd = view.nodes, REC = view.elemRecordU32;
        var a = view.elems[b * REC + 1], c = view.elems[b * REC + 2];
        var A = new THREE.Vector3(nd[a * 3], nd[a * 3 + 1], nd[a * 3 + 2]), B = new THREE.Vector3(nd[c * 3], nd[c * 3 + 1], nd[c * 3 + 2]);
        var axis = B.clone().sub(A), len = axis.length();
        if (!(len > 0)) return null;
        axis.multiplyScalar(1 / len);
        return { A: A, B: B, axis: axis, len: len };
    }
    // The beam's local frame as the beam mesh draws it (beamGeometry.js): x = I -> J, y = BPRP's
    // local y re-orthogonalised against x, z = x cross y. The build's frames when there are any.
    function beamFrame(b) {
        if (frames) {
            var o = b * 9;
            return { x: new THREE.Vector3(frames[o], frames[o + 1], frames[o + 2]),
                     y: new THREE.Vector3(frames[o + 3], frames[o + 4], frames[o + 5]),
                     z: new THREE.Vector3(frames[o + 6], frames[o + 7], frames[o + 8]) };
        }
        var g = beamLine(b), X = g ? g.axis.clone() : new THREE.Vector3(1, 0, 0);
        var props = view.beamProps, BP = (typeof FEAv4 !== 'undefined' && FEAv4.BPRP_F32) || 8;
        var Yl = props ? new THREE.Vector3(props[b * BP], props[b * BP + 1], props[b * BP + 2]) : new THREE.Vector3(0, 0, 1);
        Yl.addScaledVector(X, -Yl.dot(X));
        if (Yl.lengthSq() < 1e-8) {
            Yl.set(0, 0, 1).addScaledVector(X, -X.z);
            if (Yl.lengthSq() < 1e-8) Yl.set(0, 1, 0).addScaledVector(X, -X.y);
        }
        Yl.normalize();
        return { x: X, y: Yl, z: X.clone().cross(Yl).normalize() };
    }
    // SAP's load direction on beam b as a world unit vector, or null when the viewer cannot place it.
    function dirVector(dir, b) {
        var s = String(dir === undefined || dir === null || dir === '' ? 'Gravity' : dir).trim().toLowerCase().replace(/\s+/g, ' ');
        s = s.replace(/ ?proj(ected)?$/, '');
        if (s === 'gravity') return new THREE.Vector3(0, 0, -1);
        if (s === 'x') return new THREE.Vector3(1, 0, 0);
        if (s === 'y') return new THREE.Vector3(0, 1, 0);
        if (s === 'z') return new THREE.Vector3(0, 0, 1);
        if (s === '1' || s === '2' || s === '3') {
            var fr = beamFrame(b);
            return s === '1' ? fr.x : s === '2' ? fr.z : fr.y.clone().negate();
        }
        return null;
    }

    // ---- sidecar items -> entries -----------------------------------------
    function envelopeSection() {
        var env = window.FEAFeatures ? FEAFeatures.envelope() : null;
        return env && env.loads && Array.isArray(env.loads.items) ? env.loads : null;
    }
    function beamsOf(item) {
        var out = [];
        (item.members || []).forEach(function (m) {
            if (m && m.domain && m.domain !== 'beams') return;
            (m && m.ids || []).forEach(function (id) { var b = beamIndex.get(id); if (b !== undefined) out.push(b); });
        });
        return out;
    }
    function parseItems(sec) {
        entries = [];
        itemsRef = sec ? sec.items : null;
        units = (sec && sec.units) || {};
        maxMag = {};
        v2 = !!(sec && Array.isArray(sec.patterns));
        if (!v2) { res = null; pickSec = null; }
        else {
            if (sec !== pickSec) { pickSec = sec; pick = restorePick(sec); populatePick(sec); }
            res = resolve(sec, pick);
        }
        if (!view || !sec) return;
        sec.items.forEach(function (item, idx) {
            if (!item) return;
            var kind = String(item.kind || '');
            var vecs = Array.isArray(item.vectors) ? item.vectors : [];
            var fac = v2 ? factorOf(item) : 1;
            if (!fac) return;
            function scaled(vals) { return fac === 1 ? vals : vals.map(function (x) { var y = num(x); return y === y ? y * fac : x; }); }
            function tag(en) { if (v2) { en.v2 = true; en.factor = fac; } return en; }
            if (kind === 'force' || kind === 'displacement') {
                (item.nodeIds || []).forEach(function (id) {
                    var ni = nodeIndex.get(id);
                    if (ni === undefined) return;
                    vecs.forEach(function (vc) {
                        var vals = scaled(vc.values || []);
                        var t = [num(vals[0]), num(vals[1]), num(vals[2])], r = [num(vals[3]), num(vals[4]), num(vals[5])];
                        var tn = t.map(function (x) { return x === x ? x : 0; }), rn = r.map(function (x) { return x === x ? x : 0; });
                        var kT = kind === 'force' ? 'force' : 'displacement', kR = kind === 'force' ? 'moment' : 'displacement';
                        if (mag3(tn) > 0) add(tag({ item: item, itemIdx: idx, kind: kT, rot: false, v: vc.v, ni: ni, vec: tn, raw: vals }));
                        if (mag3(rn) > 0) add(tag({ item: item, itemIdx: idx, kind: kR, rot: true, v: vc.v, ni: ni, vec: rn, raw: vals }));
                    });
                });
            } else if (kind === 'uniform') {
                var bs = beamsOf(item);
                vecs.forEach(function (vc) {
                    var vals = scaled(vc.values || []).map(function (x) { var y = num(x); return y === y ? y : 0; });
                    if (mag3(vals) > 0) add(tag({ item: item, itemIdx: idx, kind: 'uniform', g: !!vc.g, v: vc.v, beams: bs, vec: vals, raw: vc.values }));
                });
            } else if (kind === 'wind') {
                var w = item.wind || {};
                if (w.type === 'off') return;
                add(tag({ item: item, itemIdx: idx, kind: 'wind', v: 0, beams: beamsOf(item), vec: null, shape: num(w.shape), wave: w.type === 'wave' }));
            } else if (BEAM_KINDS[kind]) {
                beamsOf(item).forEach(function (b) { addBeamEntry(item, idx, kind, b, fac); });
            }
        });
        populateVectors();
    }
    // A frame load (version 2) on beam b: one entry, values times the selection's factor.
    function relOf(o, len, def) {
        if (o && typeof o === 'object') {
            var r = num(o.rel);
            if (r === r) return r;
            var a = num(o.abs);
            if (a === a && len > 0) return a / len;
        }
        return def;
    }
    function addBeamEntry(item, idx, kind, b, fac) {
        var g = beamLine(b);
        if (!g) return;
        var en = { item: item, itemIdx: idx, v2: true, factor: fac, b: b, beams: [b] };
        if (kind === 'temperature') {
            var t = num(item.dT);
            if (!(t === t) || t === 0) return;
            en.kind = 'temperature'; en.form = 'sleeve'; en.dT = t * fac; en.vec = null; en.mag = Math.abs(en.dT);
            add(en);
            return;
        }
        var mom = String(item.type || 'force').toLowerCase() === 'moment';
        var d = dirVector(item.dir, b);
        if (!d) return;
        en.dir = d;
        en.kind = mom ? 'moment' : kind === 'point' ? 'force' : 'uniform';
        en.rot = mom;
        if (kind === 'point') {
            var val = num(item.value);
            if (!(val === val) || val === 0) return;
            en.form = 'point';
            en.at = clamp01(relOf(item.at, g.len, 0));
            en.value = val * fac;
            en.vec = [d.x * en.value, d.y * en.value, d.z * en.value];
        } else {
            var va = num(item.valueA), vb = num(item.valueB);
            if (!(va === va)) va = 0;
            if (!(vb === vb)) vb = 0;
            if (!va && !vb) return;
            en.form = 'trap';
            en.from = clamp01(relOf(item.from, g.len, 0));
            en.to = clamp01(relOf(item.to, g.len, 1));
            en.a = va * fac; en.bv = vb * fac;
            en.mag = Math.max(Math.abs(en.a), Math.abs(en.bv));
            var big = Math.abs(en.a) >= Math.abs(en.bv) ? en.a : en.bv;
            en.vec = [d.x * big, d.y * big, d.z * big];
            if (mom) en.scaleKey = 'momentU';          // per length: its own scale
        }
        add(en);
    }
    function add(en) {
        if (en.mag === undefined) en.mag = en.vec ? mag3(en.vec) : 1;
        var key = en.scaleKey || (en.kind === 'uniform' ? (en.g ? 'g' : 'uniform') : en.kind + (en.rot ? 'R' : ''));
        en.scaleKey = key;
        if (!(maxMag[key] >= en.mag)) maxMag[key] = en.mag;
        entries.push(en);
    }

    // ---- version 2: which patterns, by what factor -------------------------------
    function byName(list, name) {
        for (var i = 0; list && i < list.length; i++) if (list[i] && String(list[i].name) === name) return list[i];
        return null;
    }
    function sfOf(x) { var v = num(x); return isFinite(v) ? v : 1; }
    function isPatternLoad(t) { return String(t || '').replace(/\s+/g, '').toLowerCase() === 'loadpattern'; }
    function typeText(o) { return o && o.type ? ' (' + o.type + ')' : ''; }
    // The selection `value` resolved against the section: {value, kind, name, factors (pattern -> factor,
    // null = every pattern x1), order, union, notes, selfWeight, text}.
    //   pattern       its items x1
    //   case          each `Load pattern` entry x sf (a pattern listed twice sums); other entries
    //                 (Accel ...) are skipped with a note. A nonlinear case continuing from another
    //                 nonlinear case carries its loads (SAP's staged analysis: OPE from SUS holds SUS's
    //                 loads, so OPE - SUS is the thermal effect alone); any other `initial` (stiffness
    //                 only) is named, not included. A pattern whose factors cancel is dropped and named
    //   Linear Add    each item is a case, else a combination; factors multiply down the tree, a
    //                 pattern reached by several paths sums; a combination met again on its own path
    //                 (a cycle) is skipped
    //   other types   (Envelope, Abs Add, SRSS ...) the union of every pattern reached, x+-1 (the sign of
    //                 the first path that reaches it)
    function resolve(sec, value) {
        var r = { value: 'all', kind: 'all', name: '', factors: null, order: [], union: false, notes: [], selfWeight: 0, text: '' };
        if (!sec || !Array.isArray(sec.patterns)) return r;
        var pats = sec.patterns, cases = sec.cases || [], combos = sec.combinations || [];
        var m = /^([pck]):([\s\S]*)$/.exec(String(value || ''));
        var fac = Object.create(null), sign = Object.create(null), order = [];
        function note(s) { if (r.notes.indexOf(s) < 0) r.notes.push(s); }
        function addPat(name, f) {
            if (!(name in fac)) { fac[name] = 0; sign[name] = f < 0 ? -1 : 1; order.push(name); }
            fac[name] += f;
        }
        function nonlinear(c) { return /^non/i.test(String(c && c.type || '')); }
        function doCase(c, f, top, seen) {
            seen = seen || [];
            if (seen.indexOf(c.name) >= 0) { note('cycle at ' + c.name + ' cut'); return; }
            (c.loads || []).forEach(function (ld) {
                if (!ld) return;
                if (isPatternLoad(ld.type)) addPat(String(ld.name), f * sfOf(ld.sf));
                else note((ld.type || 'load') + (ld.name ? ' ' + ld.name : '') + ' skipped');
            });
            var ini = c.initial;
            if (!ini || /^zero$/i.test(String(ini))) return;
            var ic = byName(cases, String(ini));
            if (ic && nonlinear(c) && nonlinear(ic)) { note(c.name + ' includes ' + ini + ' (continues from it)'); doCase(ic, f, false, seen.concat([c.name])); }
            else note((top ? '' : c.name + ' ') + 'continues from ' + ini);
        }
        function doCombo(k, f, path) {
            if (path.indexOf(k.name) >= 0) { note('cycle at ' + k.name + ' cut'); return; }
            if (!/^linear\s*add$/i.test(String(k.type || ''))) r.union = true;
            var here = path.concat([k.name]);
            (k.items || []).forEach(function (it) {
                if (!it) return;
                var g = f * sfOf(it.sf), name = String(it.name);
                var c = byName(cases, name);
                if (c) { doCase(c, g, false); return; }
                var kk = byName(combos, name);
                if (kk) doCombo(kk, g, here);
                else note(name + ' not found');
            });
        }
        var pat = null, cs = null, cb = null;
        if (m && m[1] === 'p') pat = byName(pats, m[2]);
        else if (m && m[1] === 'c') cs = byName(cases, m[2]);
        else if (m && m[1] === 'k') cb = byName(combos, m[2]);
        if (pat) { r.kind = 'pattern'; addPat(m[2], 1); }
        else if (cs) { r.kind = 'case'; doCase(cs, 1, true); }
        else if (cb) { r.kind = 'combination'; doCombo(cb, 1, []); }
        if (r.kind === 'all') pats.forEach(function (p) { if (p && p.name !== undefined) order.push(String(p.name)); });
        else {
            r.value = value; r.name = m[2];
            if (r.union) order.forEach(function (p) { fac[p] = sign[p]; });
            else {
                var gone = order.filter(function (p) { return Math.abs(fac[p]) < 1e-12; });
                if (gone.length) { note(gone.join(', ') + (gone.length > 1 ? ' cancel' : ' cancels')); order = order.filter(function (p) { return gone.indexOf(p) < 0; }); }
            }
            r.factors = {};
            order.forEach(function (p) { r.factors[p] = fac[p]; });
        }
        r.order = order;
        // self weight: a multiplier per pattern (SAP); listed, never drawn
        var sw = 0, swNames = [];
        order.forEach(function (name) {
            var p = byName(pats, name), w = p ? num(p.selfWeight) : 0;
            var f = r.factors ? r.factors[name] : 1;
            if (w === w && w !== 0 && f) { sw += f * w; swNames.push(name); }
        });
        r.selfWeight = r.union ? 0 : sw;
        var list = order.map(function (p) { return p + ' \u00d7' + sig(r.factors ? r.factors[p] : 1); }).join(', ');
        var head = r.kind === 'all' ? 'All patterns'
                 : r.kind === 'pattern' ? 'Pattern ' + r.name + typeText(pat)
                 : r.kind === 'case' ? 'Case ' + r.name + typeText(cs) + ': ' + (list || 'no load patterns')
                 : 'Combination ' + r.name + typeText(cb) + ': ' + (r.union ? 'envelope: union shown (' + (order.join(', ') || 'nothing') + ')' : (list || 'no load patterns'));
        var parts = [head].concat(r.notes);
        if (swNames.length && (r.union || sw !== 0))
            parts.push('+ self weight' + (r.union ? '' : ' \u00d7' + sig(sw)) + (r.kind === 'pattern' ? '' : ' (' + swNames.join(', ') + ')'));
        r.text = parts.join('; ');
        return r;
    }
    function factorOf(item) {
        if (!res || !res.factors) return 1;
        var p = item && item.pattern !== undefined ? String(item.pattern) : '';
        return Object.prototype.hasOwnProperty.call(res.factors, p) ? res.factors[p] : 0;
    }
    function pickOptions(sec) {
        var out = [{ value: 'all', text: 'All patterns', group: '' }];
        if (!sec || !Array.isArray(sec.patterns)) return out;
        function each(list, pre, group) {
            (list || []).forEach(function (o) {
                if (o && o.name !== undefined && o.name !== null) out.push({ value: pre + o.name, text: o.name + typeText(o), group: group });
            });
        }
        each(sec.patterns, 'p:', 'Patterns');
        each(sec.cases, 'c:', 'Cases');
        each(sec.combinations, 'k:', 'Combinations');
        return out;
    }
    function validPick(sec, value) {
        return pickOptions(sec).some(function (o) { return o.value === value; });
    }
    function populatePick(sec) {
        if (!elPick) return;
        elPick.innerHTML = '';
        var groups = {};
        pickOptions(sec).forEach(function (o) {
            var host = elPick;
            if (o.group) {
                if (!groups[o.group]) { groups[o.group] = document.createElement('optgroup'); groups[o.group].label = o.group; elPick.appendChild(groups[o.group]); }
                host = groups[o.group];
            }
            var e = document.createElement('option');
            e.value = o.value; e.textContent = o.text;
            host.appendChild(e);
        });
        elPick.value = pick;
    }
    // remembered per model: the sidecar's file name and model id
    function pickKey() {
        var env = window.FEAFeatures && FEAFeatures.envelope ? FEAFeatures.envelope() : null;
        var name = window.FEAFeatures && FEAFeatures.fileName ? FEAFeatures.fileName() : '';
        var mid = env && env.model ? (env.model.modelId || env.model.geometryHash || '') : '';
        return 'pluto.ldPick.' + (name || '') + '|' + mid;
    }
    function restorePick(sec) {
        var want = 'all';
        try { want = localStorage.getItem(pickKey()) || 'all'; } catch (e) {}
        return validPick(sec, want) ? want : 'all';
    }
    function setPick(value, remember) {
        var sec = envelopeSection();
        if (!sec || !Array.isArray(sec.patterns)) return false;
        if (sec !== pickSec) { pickSec = sec; populatePick(sec); }
        pick = validPick(sec, value) ? value : 'all';
        if (remember !== false) { try { localStorage.setItem(pickKey(), pick); } catch (e) {} }
        if (elPick) elPick.value = pick;
        parseItems(sec);
        lastSig = '';
        update();
        return true;
    }
    function selection() {
        var r = res || resolve(null, 'all');
        var fac = null;
        if (r.factors) { fac = {}; Object.keys(r.factors).forEach(function (k) { fac[k] = r.factors[k]; }); }
        return { v2: v2, value: v2 ? pick : 'all', kind: r.kind, name: r.name, factors: fac, order: r.order.slice(),
                 union: r.union, notes: r.notes.slice(), selfWeight: r.selfWeight, text: v2 ? r.text : '' };
    }

    // ---- visibility --------------------------------------------------------
    function kindOn(en) { return !!kindsOn[en.kind === 'displacement' ? 'displacement' : en.kind]; }
    function entryVisible(en) {
        if (en.item.hidden === true) return false;
        if (!kindOn(en)) return false;
        if (vectorPick && !en.v2 && en.kind !== 'wind' && +en.v !== vectorPick) return false;
        if (nodeKeep && en.ni !== undefined && !nodeKeep[en.ni]) return false;
        return true;
    }
    function beamKept(b) {
        if (!nodeKeep) return true;
        var REC = view.elemRecordU32;
        return !!(nodeKeep[view.elems[b * REC + 1]] && nodeKeep[view.elems[b * REC + 2]]);
    }

    // ---- symbols -----------------------------------------------------------
    function unitAt(R) { return Math.max(R, span * MIN_UNIT_FRACTION) * size; }
    // arrow length: 0.4 .. 1 of the longest by |value|; node (and point) loads 6 pipe radii at most, rows 3.5
    function lengthFor(en, u) {
        var m = maxMag[en.scaleKey] || 1;
        var top = en.ni !== undefined || en.form === 'point' ? Math.max(6 * u, 0.05 * span * size) : 3.5 * u;   // a node load reads on a long thin model
        return top * (0.4 + 0.6 * Math.min(1, en.mag / m));
    }
    function windDir() {
        var s = String(windFrom || '-X'), d = new THREE.Vector3();
        var ax = s.charAt(1);
        d.setComponent(ax === 'X' ? 0 : ax === 'Y' ? 1 : 2, s.charAt(0) === '-' ? -1 : 1);
        return d;
    }

    // Parts of one entry: [{prim, pos, quat, scale, kind}]
    function symbolParts(en) {
        var out = [], nd = view.nodes, REC = view.elemRecordU32;
        function put(prim, pos, quat, sx, sy, sz) {
            out.push({ prim: prim, pos: pos, quat: quat, scale: new THREE.Vector3(sx, sy, sz) });
        }
        // an arrow along unit a, tip at `tip`; heads 2 = a moment / rotation
        function arrow(tip, a, len, u, heads) {
            var hl = 0.7 * u, hr = 0.3 * u, sr = 0.09 * u, q = new THREE.Quaternion().setFromUnitVectors(Y, a);
            var nh = heads || 1, back = 0;
            for (var k = 0; k < nh; k++) {
                put('cone', tip.clone().addScaledVector(a, -hl / 2 - back), q, hr, hl, hr);
                back += 0.8 * hl;
            }
            var headLen = hl + (nh - 1) * 0.8 * hl;
            var shaft = Math.max(len - headLen, 0.2 * u);
            put('cyl', tip.clone().addScaledVector(a, -headLen - shaft / 2), q, sr, shaft, sr);
        }
        // a thin rod from p to q (the line through a trapezoid's tails)
        function rod(p, q, r) {
            var d = q.clone().sub(p), l = d.length();
            if (!(l > 1e-9)) return;
            put('cyl', p.clone().lerp(q, 0.5), new THREE.Quaternion().setFromUnitVectors(Y, d.multiplyScalar(1 / l)), r, l, r);
        }
        if (en.form) return frameParts(en, put, arrow, rod, out);
        if (en.ni !== undefined) {
            var P = new THREE.Vector3(nd[en.ni * 3], nd[en.ni * 3 + 1], nd[en.ni * 3 + 2]);
            var R = nodeR[en.ni] > 0 ? nodeR[en.ni] : span * MIN_UNIT_FRACTION;
            var u = unitAt(R), L = lengthFor(en, u);
            var d = v3(en.vec).normalize();
            if (en.kind === 'force') arrow(P.clone().addScaledVector(d, -R), d, L, u, 1);       // pushing on the pipe
            else arrow(P.clone().addScaledVector(d, R + L), d, L, u, en.rot ? 2 : 1);           // out from the pipe
            en.anchor = en.kind === 'force' ? P.clone().addScaledVector(d, -(R + L)) : P.clone().addScaledVector(d, R + L);
            return out;
        }
        // rows along beams: uniform loads, wind
        var dirRow = en.kind === 'wind' ? windDir() : v3(en.vec).normalize();
        var up = modelUp(), budget = MAX_ROW_ARROWS, mid = null, best = -1;
        en.beams.forEach(function (b) {
            if (!beamKept(b)) return;
            var a = view.elems[b * REC + 1], c = view.elems[b * REC + 2];
            var A = new THREE.Vector3(nd[a * 3], nd[a * 3 + 1], nd[a * 3 + 2]), B = new THREE.Vector3(nd[c * 3], nd[c * 3 + 1], nd[c * 3 + 2]);
            var axis = B.clone().sub(A), len = axis.length();
            if (!(len > 0)) return;
            axis.multiplyScalar(1 / len);
            var R = beamR[b] > 0 ? beamR[b] : span * MIN_UNIT_FRACTION, u = unitAt(R);
            var L = en.kind === 'wind' ? 2.6 * u : lengthFor(en, u);
            var step = Math.max(4 * u, span * 0.004);
            var n = Math.max(1, Math.min(Math.round(len / step), budget));
            budget -= n;
            var d = dirRow.clone();
            var along = Math.abs(d.dot(axis)) > 0.95;            // axial: off to the top of the pipe
            if (en.kind === 'wind') { d.addScaledVector(axis, -d.dot(axis)); if (d.lengthSq() < 1e-6) return; d.normalize(); }
            for (var k = 0; k < n; k++) {
                var Q = A.clone().addScaledVector(axis, len * (k + 0.5) / n);
                var tip;
                if (along) {
                    var t = up.clone().addScaledVector(axis, -up.dot(axis));
                    t = t.lengthSq() > 1e-6 ? t.normalize() : perp(axis);
                    tip = Q.clone().addScaledVector(t, R + 0.4 * u).addScaledVector(d, L / 2);
                } else tip = Q.clone().addScaledVector(d, -R);
                arrow(tip, d, L, u, 1);
            }
            if (len > best) { best = len; mid = A.clone().lerp(B, 0.5).addScaledVector(d, -(R + L)); }
        });
        en.anchor = mid;
        return out;
    }

    // Version-2 frame loads on their beams: a point load, a distributed load (trapezoid), a temperature band.
    function frameParts(en, put, arrow, rod, out) {
        var up = en.v2 ? Z : modelUp(), budget = MAX_ROW_ARROWS, best = -1;
        en.anchor = null;
        en.beams.forEach(function (b) {
            if (!beamKept(b)) return;
            var g = beamLine(b);
            if (!g) return;
            var R = beamR[b] > 0 ? beamR[b] : span * MIN_UNIT_FRACTION, u = unitAt(R);
            var top = up.clone().addScaledVector(g.axis, -up.dot(g.axis));
            top = top.lengthSq() > 1e-6 ? top.normalize() : perp(g.axis);
            if (en.form === 'sleeve') {
                var k = 1;
                if (sleeveN) k = sleeveN[b] = (sleeveN[b] || 0) + 1;          // several on one member: each a little wider
                var Rs = R + u * (0.45 + 0.35 * (k - 1));
                var C = g.A.clone().lerp(g.B, 0.5);
                put('sleeve', C, new THREE.Quaternion().setFromUnitVectors(Y, g.axis), Rs, g.len, Rs);
                if (g.len > best) { best = g.len; en.anchor = C.clone().addScaledVector(top, Rs + 0.3 * u); }
                return;
            }
            var heads = en.kind === 'moment' ? 2 : 1;
            var along = Math.abs(en.dir.dot(g.axis)) > 0.95;               // axial: on the top of the member
            if (en.form === 'point') {
                var L = lengthFor(en, u), P = g.A.clone().addScaledVector(g.axis, g.len * en.at);
                var d = v3(en.vec).normalize(), tip, anchor;
                if (along) {
                    var base = P.clone().addScaledVector(top, R + 0.4 * u);
                    tip = heads === 2 ? base.clone().addScaledVector(d, L) : base;
                    anchor = heads === 2 ? tip : base.clone().addScaledVector(d, -L);
                } else if (heads === 2) { tip = P.clone().addScaledVector(d, R + L); anchor = tip; }   // out from the member
                else { tip = P.clone().addScaledVector(d, -R); anchor = P.clone().addScaledVector(d, -(R + L)); }   // pushing on it
                arrow(tip, d, L, u, heads);
                en.anchor = anchor;
                return;
            }
            // distributed: arrows from `from` to `to`, lengths in proportion to the value, a line through the tails
            var Lmax = lengthFor(en, u);
            var t0 = en.from, t1 = en.to, seg = g.len * Math.abs(t1 - t0);
            var step = Math.max(4 * u, span * 0.004);
            var n = seg > 1e-9 * g.len ? Math.max(2, Math.min(Math.round(seg / step) + 1, budget)) : 1;
            budget -= n;
            var outer = [];
            for (var j = 0; j < n; j++) {
                var s = n > 1 ? j / (n - 1) : 0;
                var val = en.a + (en.bv - en.a) * s;
                var Q = g.A.clone().addScaledVector(g.axis, g.len * (t0 + (t1 - t0) * s));
                var Lk = en.mag > 0 ? Lmax * Math.abs(val) / en.mag : 0;
                var dk = en.dir.clone().multiplyScalar(val < 0 ? -1 : 1);
                var uk = Math.min(u, Lk / (heads === 2 ? 1.6 : 1.1));     // a short arrow gets a smaller head
                var tipK, outK;
                if (along) { outK = Q.clone().addScaledVector(top, R + 0.4 * u); tipK = outK.clone().addScaledVector(dk, Lk / 2); }
                else if (heads === 2) { tipK = Q.clone().addScaledVector(dk, R + Lk); outK = tipK; }
                else { tipK = Q.clone().addScaledVector(dk, -R); outK = Q.clone().addScaledVector(dk, -(R + Lk)); }
                if (Lk > 1e-6 * u) arrow(tipK, dk, Lk, uk, heads);
                outer.push(outK);
            }
            if (!along) for (var i = 0; i + 1 < outer.length; i++) rod(outer[i], outer[i + 1], 0.07 * u);
            if (g.len > best) { best = g.len; en.anchor = outer[Math.floor((outer.length - 1) / 2)]; }
        });
        return out;
    }

    function primGeometry(p) {
        return p === 'cone' ? new THREE.ConeGeometry(1, 1, 12)
             : p === 'sleeve' ? new THREE.CylinderGeometry(1, 1, 1, 20, 1, true)
             : new THREE.CylinderGeometry(1, 1, 1, 8);
    }
    // alpha < 1: a see-through material (the temperature band)
    function makeMaterial(alpha) {
        var see = alpha < 1;
        var m = new THREE.ShaderMaterial({
            vertexShader: [
                'varying vec3 vCol;', 'varying vec3 vN;', 'varying vec3 vV;',
                'void main() {',
                '  vec4 p = vec4(position, 1.0);', '  vec3 n = normal;',
                '#ifdef USE_INSTANCING',
                '  p = instanceMatrix * p;', '  mat3 m = mat3(instanceMatrix);',
                '  n = m * (n / vec3(dot(m[0], m[0]), dot(m[1], m[1]), dot(m[2], m[2])));',
                '#endif',
                '#ifdef USE_INSTANCING_COLOR', '  vCol = instanceColor;', '#else', '  vCol = vec3(0.8);', '#endif',
                '  vec4 mv = modelViewMatrix * p;', '  vN = normalize(normalMatrix * n);', '  vV = -mv.xyz;',
                '  gl_Position = projectionMatrix * mv;', '}'
            ].join('\n'),
            fragmentShader: [
                'varying vec3 vCol;', 'varying vec3 vN;', 'varying vec3 vV;',
                'void main() {', '  float l = 0.45 + 0.55 * abs(dot(normalize(vN), normalize(vV)));',
                '  gl_FragColor = vec4(vCol * l, ' + (see ? alpha.toFixed(2) : '1.0') + ');', '}'
            ].join('\n')
        });
        if (see) { m.transparent = true; m.depthWrite = false; m.side = THREE.DoubleSide; }
        return m;
    }
    function rgbOf(en) {
        var c = en.kind === 'temperature' ? KIND_RGB[en.dT > 0 ? 'hot' : 'cold']
              : KIND_RGB[en.kind === 'uniform' && en.g ? 'g' : en.kind] || [200, 200, 200];
        return [c[0] / 255, c[1] / 255, c[2] / 255];
    }

    function rebuild() {
        disposeMeshes();
        shown = [];
        if (!view || !entries.length) { syncPanel(); syncLabels(); return; }
        var parts = { cone: [], cyl: [], sleeve: [] };
        sleeveN = {};
        if (visible) {
            entries.forEach(function (en, i) {
                if (!entryVisible(en)) return;
                var ps = symbolParts(en);
                if (!ps.length) return;
                shown.push(i);
                ps.forEach(function (pt) { pt.owner = i; parts[pt.prim].push(pt); });
            });
        }
        sleeveN = null;
        if (!material) material = makeMaterial();
        if (!sleeveMaterial && parts.sleeve.length) sleeveMaterial = makeMaterial(0.3);
        root = new THREE.Group();
        meshes = {};
        var m4 = new THREE.Matrix4(), col = new THREE.Color();
        PRIMS.forEach(function (p) {
            var list = parts[p];
            if (!list.length) return;
            var mesh = new THREE.InstancedMesh(primGeometry(p), p === 'sleeve' ? sleeveMaterial : material, list.length);
            var owner = new Int32Array(list.length);
            list.forEach(function (pt, k) {
                mesh.setMatrixAt(k, m4.compose(pt.pos, pt.quat, pt.scale));
                var c = rgbOf(entries[pt.owner]);
                mesh.setColorAt(k, col.setRGB(c[0], c[1], c[2]));
                owner[k] = pt.owner;
            });
            mesh.instanceMatrix.needsUpdate = true;
            if (mesh.instanceColor) mesh.instanceColor.needsUpdate = true;
            mesh.frustumCulled = false;
            if (p === 'sleeve') mesh.renderOrder = 2;                     // after the members it wraps
            meshes[p] = { mesh: mesh, owner: owner };
            root.add(mesh);
        });
        if (typeof scene !== 'undefined' && scene) scene.add(root);
        syncPanel();
        syncLabels();
        if (typeof needsRender !== 'undefined') needsRender = true;
    }

    // ---- text ----------------------------------------------------------------
    function f(v, d) { return typeof fmt === 'function' ? fmt(v, d) : String(v); }
    // a version-2 number as SAP gives it: up to 4 significant digits, no grouping ("0.0021", "230", "-1")
    function sig(v) {
        v = +v;
        if (!isFinite(v)) return '?';
        if (v === 0) return '0';
        return String(+v.toPrecision(4));
    }
    function sp(u) { return u ? ' ' + u : ''; }
    function perLen(u) { return u ? u + '/' + (units.length || '') : ''; }
    function dirText(dir) {
        var s = String(dir === undefined || dir === null || dir === '' ? 'Gravity' : dir);
        return /^[123]$/.test(s) ? 'local ' + s : s;
    }
    function unitOf(en) {
        if (en.kind === 'temperature') return units.temperature || '';
        if (en.form === 'trap') return en.kind === 'moment' ? perLen(units.moment) : (units.uniform || '');
        if (en.kind === 'force') return units.force || '';
        if (en.kind === 'moment') return units.moment || '';
        if (en.kind === 'displacement') return en.rot ? (units.rotation || (en.v2 ? 'rad' : 'deg')) : (units.length || '');
        if (en.kind === 'uniform') return en.g ? (units.g || 'g') : (units.uniform || '');
        return '';
    }
    function prefix(en) {
        return en.kind === 'force' ? 'F' : en.kind === 'moment' ? 'F' : en.kind === 'displacement' ? 'D' : en.kind === 'uniform' ? 'U' : '';
    }
    // "F1 1,200 lb." (the magnitude and its largest component's axis); version 2 "FZ -0.5 kip",
    // "0.85 kip Gravity", "0.1->0.2 kip/in X Proj", "dT 230 F" -- the drawn (factored) values
    function shortText(en) {
        if (en.form === 'sleeve') return 'ΔT ' + sig(en.dT) + sp(unitOf(en));
        if (en.form === 'point') return sig(en.value) + sp(unitOf(en)) + ' ' + dirText(en.item.dir);
        if (en.form === 'trap') return (en.a === en.bv ? sig(en.a) : sig(en.a) + '\u2192' + sig(en.bv)) + sp(unitOf(en)) + ' ' + dirText(en.item.dir);
        if (en.v2 && en.ni !== undefined) {
            var nm = en.kind === 'force' ? ['FX', 'FY', 'FZ'] : en.kind === 'moment' ? ['MX', 'MY', 'MZ'] : en.rot ? ['RX', 'RY', 'RZ'] : ['UX', 'UY', 'UZ'];
            var nz2 = [];
            for (var j = 0; j < 3; j++) if (en.vec[j]) nz2.push(j);
            return (nz2.length === 1 ? nm[nz2[0]] + ' ' + sig(en.vec[nz2[0]]) : '|' + nm[0].charAt(0) + '| ' + sig(en.mag)) + sp(unitOf(en));
        }
        if (en.kind === 'wind') return (en.wave ? 'Wave Cd ' : 'Wind ') + (en.shape === en.shape ? f(en.shape, 3) : '');
        var axes = en.kind === 'moment' || en.rot ? ['RX', 'RY', 'RZ'] : ['X', 'Y', 'Z'];
        if (en.kind === 'moment') axes = ['MX', 'MY', 'MZ'];
        var nz = [];
        for (var k = 0; k < 3; k++) if (en.vec[k]) nz.push(k);
        var body = nz.length === 1 ? axes[nz[0]] + ' ' + f(en.vec[nz[0]], 3) : '|' + f(en.mag, 3) + '|';
        var u = unitOf(en);
        return prefix(en) + en.v + ' ' + body + (u ? ' ' + u : '');
    }
    function vectorText(en) {
        if (en.kind === 'wind') return shortText(en);
        var names = en.kind === 'force' || en.kind === 'moment' ? ['FX', 'FY', 'FZ', 'MX', 'MY', 'MZ']
                  : en.kind === 'displacement' ? ['DX', 'DY', 'DZ', 'RX', 'RY', 'RZ'] : ['UX', 'UY', 'UZ'];
        var raw = en.raw || en.vec, out = [];
        for (var k = 0; k < names.length && k < raw.length; k++) {
            var x = num(raw[k]);
            if (en.kind === 'displacement' ? x === x : x) out.push(names[k] + ' ' + f(x, 4));
        }
        return out.join(', ') || 'zero';
    }
    // a version-2 joint vector: "FZ -0.5 kip; MX 4.5 kip-in" (non-zero components, in the section's units)
    function v2VectorText(kind, values) {
        var disp = kind === 'displacement';
        var names = disp ? ['UX', 'UY', 'UZ', 'RX', 'RY', 'RZ'] : ['FX', 'FY', 'FZ', 'MX', 'MY', 'MZ'];
        var us = disp ? [units.length || '', units.rotation || 'rad'] : [units.force || '', units.moment || ''];
        var out = [];
        for (var h = 0; h < 2; h++) {
            var p = [];
            for (var k = 0; k < 3; k++) { var x = num((values || [])[h * 3 + k]); if (x === x && x !== 0) p.push(names[h * 3 + k] + ' ' + sig(x)); }
            if (p.length) out.push(p.join(', ') + sp(us[h]));
        }
        return out.join('; ') || 'zero';
    }
    function atText(at) {
        if (!at || typeof at !== 'object') return '';
        var r = num(at.rel), a = num(at.abs);
        return ' at ' + (r === r ? sig(r) : '?') + (a === a ? ' (' + sig(a) + sp(units.length) + ')' : '');
    }
    function spanText(from, to) {
        var a = from && typeof from === 'object' ? num(from.rel) : 0, b = to && typeof to === 'object' ? num(to.rel) : 1;
        if (!(a === a)) a = 0;
        if (!(b === b)) b = 1;
        return Math.abs(a) < 1e-9 && Math.abs(b - 1) < 1e-9 ? '' : ' from ' + sig(a) + ' to ' + sig(b);
    }
    // One line for any item, version 1 or 2, as the sidecar holds it (not factored):
    // "P2 CONTENTS: point 0.85 kip Gravity at 0.4 (96 in)", "...: trapezoid 0.1->0.2 kip/in X from 0.25 to 0.75",
    // "...: dT 230 F", "F 20: force F1 FY -1000; F2 MZ 3000"
    function itemText(item) {
        if (!item) return '';
        var kind = String(item.kind || ''), body;
        var mom = String(item.type || '').toLowerCase() === 'moment';
        if (kind === 'point') {
            body = 'point ' + (mom ? 'moment ' : '') + sig(item.value) + sp(mom ? units.moment : units.force) + ' ' + dirText(item.dir) + atText(item.at);
        } else if (kind === 'distributed') {
            var a = num(item.valueA), b = num(item.valueB);
            body = (a === b ? 'uniform ' : 'trapezoid ') + (mom ? 'moment ' : '') + (a === b ? sig(a) : sig(a) + '\u2192' + sig(b)) +
                   sp(mom ? perLen(units.moment) : units.uniform) + ' ' + dirText(item.dir) + spanText(item.from, item.to);
        } else if (kind === 'temperature') {
            var ty = String(item.type || '');
            body = 'ΔT ' + sig(item.dT) + sp(units.temperature) + (ty && !/^temperature$/i.test(ty) ? ' (' + ty + ')' : '');
        } else if (kind === 'force' || kind === 'displacement') {
            var isV2 = item.pattern !== undefined, pre = kind === 'force' ? 'F' : 'D';
            body = kind + ' ' + (item.vectors || []).map(function (vc) {
                return isV2 ? v2VectorText(kind, vc.values) : pre + vc.v + ' ' + vectorText({ kind: kind, raw: vc.values, vec: vc.values });
            }).join('; ');
        } else if (kind === 'uniform') {
            body = 'uniform ' + (item.vectors || []).map(function (vc) {
                return 'U' + vc.v + ' ' + vectorText({ kind: 'uniform', raw: vc.values, vec: vc.values }) + (vc.g ? ' (g)' : '');
            }).join('; ');
        } else if (kind === 'wind') {
            var w = item.wind || {};
            body = (w.type === 'wave' ? 'wave Cd ' : w.type === 'off' ? 'wind off, shape ' : 'wind shape ') + f(num(w.shape), 3);
        } else body = kind || 'load';
        return (item.name ? item.name + ': ' : '') + body;
    }

    function syncLabels() {
        if (!window.FEALabels) return;
        if (!labels || !visible) { FEALabels.set('loads', []); return; }
        var list = [];
        shown.forEach(function (i) {
            var en = entries[i];
            if (!en.anchor) return;
            var cls = 'lbl-load lbl-' + en.kind + (en.kind === 'temperature' ? (en.dT > 0 ? ' lbl-temp-hot' : ' lbl-temp-cold') : '');
            list.push({ pos: en.anchor, text: shortText(en), cls: cls, priority: 2 });
        });
        FEALabels.set('loads', list);
    }

    // ---- features hook -------------------------------------------------------
    function onFeatures() {
        var sec = envelopeSection();
        var items = sec ? sec.items : null;
        if (items !== itemsRef) { parseItems(sec); lastSig = ''; }
        update();
    }
    function update() {
        var sig0 = visible + '|' + size + '|' + vectorPick + '|' + windFrom + '|' + JSON.stringify(kindsOn) + '|' + labels + '|' + (v2 ? pick : '') + '|';
        if (nodeKeep) for (var i = 0; i < nodeKeep.length; i++) sig0 += nodeKeep[i] ? '1' : '0';
        if (sig0 !== lastSig || (!meshes && entries.length)) { lastSig = sig0; rebuild(); }
    }
    function writeVis(keep) { nodeKeep = keep || null; update(); }

    // ---- panel ---------------------------------------------------------------
    function populateVectors() {
        if (!elVector) { if (v2) vectorPick = 0; return; }
        var vs = {};
        entries.forEach(function (en) { if (en.kind !== 'wind' && +en.v > 0) vs[+en.v] = 1; });
        var keep = vectorPick;
        elVector.innerHTML = '';
        function opt(value, text) { var o = document.createElement('option'); o.value = value; o.textContent = text; elVector.appendChild(o); }
        opt('0', 'All vectors');
        Object.keys(vs).map(Number).sort(function (a, b) { return a - b; }).forEach(function (v) { opt(String(v), 'Vector ' + v + ' (F' + v + ' D' + v + ' U' + v + ')'); });
        vectorPick = vs[keep] && !v2 ? keep : 0;          // a SAP model has no load vectors
        elVector.value = String(vectorPick);
    }
    function populateWind() {
        if (!elWind) return;
        var up = modelUp() === Z ? 'Z' : 'Y';
        var opts = ['+X', '-X'].concat(up === 'Z' ? ['+Y', '-Y'] : ['+Z', '-Z']);
        elWind.innerHTML = '';
        opts.forEach(function (o) { var e = document.createElement('option'); e.value = o; e.textContent = 'towards ' + o; elWind.appendChild(e); });
        if (opts.indexOf(windFrom) < 0) windFrom = opts[0];
        elWind.value = windFrom;
    }
    function rowOf(el) { return el && el.parentNode && el.parentNode.style ? el.parentNode : null; }
    function syncPanel() {
        if (!elSection) return;
        elSection.style.display = entries.length || (v2 && view) ? '' : 'none';     // a selection drawing nothing keeps its select
        if (elCount) {
            var by = {};
            entries.forEach(function (en) { by[en.kind] = (by[en.kind] || 0) + 1; });
            elCount.textContent = shown.length + ' / ' + entries.length + ' shown';
            KINDS.forEach(function (k) {
                var el = elKinds[k];
                if (!el) return;
                el.disabled = !by[k];
                var lab = el.parentNode && el.parentNode.querySelector ? el.parentNode.querySelector('.ld-n-' + k) : null;
                if (lab) lab.textContent = by[k] ? '(' + by[k] + ')' : '';
                if (k === 'temperature' && rowOf(el)) rowOf(el).style.display = by[k] ? '' : 'none';
            });
        }
        var rv = rowOf(elVector), rw = rowOf(elWind);
        if (rv) rv.style.display = v2 ? 'none' : '';
        if (rw) rw.style.display = v2 ? 'none' : '';
        if (elPickRow) elPickRow.style.display = v2 ? '' : 'none';
        if (elPickNote) {
            elPickNote.textContent = v2 && res ? res.text : '';
            elPickNote.style.display = v2 && res && res.text ? '' : 'none';
        }
        if (elNote) elNote.textContent = (itemsRef && envelopeSection() && envelopeSection().note) || '';
    }

    // ---- picking / readout -----------------------------------------------------
    function pickHit(raycaster) {
        if (!meshes || !visible) return null;
        var best = null;
        PICKABLE.forEach(function (p) {
            var m = meshes[p];
            if (!m) return;
            var hits = raycaster.intersectObject(m.mesh);
            for (var i = 0; i < hits.length; i++) {
                if (hits[i].instanceId == null) continue;
                if (!best || hits[i].distance < best.distance)
                    best = { load: true, entry: m.owner[hits[i].instanceId], point: hits[i].point, distance: hits[i].distance };
                break;
            }
        });
        return best;
    }
    function kindName(en) {
        if (en.form === 'point') return en.kind === 'moment' ? 'point moment' : 'point load';
        if (en.form === 'trap') return (en.kind === 'moment' ? 'distributed moment' : en.a === en.bv ? 'distributed load' : 'trapezoidal load') +
                                        (/proj/i.test(String(en.item.dir || '')) ? ' (projected)' : '');
        if (en.form === 'sleeve') return 'temperature load';
        if (en.v2 && en.kind === 'displacement') return en.rot ? 'ground rotation' : 'ground displacement';
        return en.kind === 'force' ? 'force' : en.kind === 'moment' ? 'moment' : en.kind === 'displacement' ? (en.rot ? 'imposed rotation' : 'imposed displacement')
             : en.kind === 'uniform' ? (en.g ? 'uniform load (g)' : 'uniform load') : en.wave ? 'wave' : 'wind';
    }
    function whereText(en) {
        if (en.ni !== undefined) return 'node ' + view.nodeIds[en.ni];
        if (en.form) return 'frame ' + (en.item.frame !== undefined ? en.item.frame : '') + ', beam ' + view.elemIds[en.b];
        var els = en.item.elements || [];
        return els.length ? 'element' + (els.length > 1 ? 's ' + els[0] + '..' + els[els.length - 1] : ' ' + els[0]) + ' (' + (en.item.name || '') + ')' : (en.item.name || '');
    }
    function patternText(en) {
        return 'pattern ' + en.item.pattern + (en.factor !== 1 ? ' \u00d7' + sig(en.factor) : '');
    }
    function fillReadout(hit) {
        var en = entries[hit.entry];
        elRoValue.textContent = shortText(en);
        elRoValue.className = 'ro-value';
        elRoComp.textContent = kindName(en) + (en.kind === 'wind' ? ': shape factor (direction set in CAESAR)' : en.v2 ? ', ' + patternText(en) : ', vector ' + en.v);
        elRoElem.textContent = en.item.name + '  (load, ' + whereText(en) + ')';
        if (en.ni !== undefined) {
            var nlbl = view.nodeLabels ? view.nodeLabels.get(en.ni) : '';
            elRoNode.textContent = view.nodeIds[en.ni] + (nlbl ? ' [' + nlbl + ']' : '');
        } else if (en.form) {
            var REC = view.elemRecordU32;
            elRoNode.textContent = view.nodeIds[view.elems[en.b * REC + 1]] + ' \u2192 ' + view.nodeIds[view.elems[en.b * REC + 2]];
        } else elRoNode.textContent = (en.item.nodeIds || []).join(', ') || '—';
        elRoCorners.textContent = en.v2 ? itemText(en.item) : vectorText(en);
        elRoUV.textContent = '—';
        elRoPos.textContent = roPosText(hit.point);
        if (typeof elRoControllingRow !== 'undefined' && elRoControllingRow) elRoControllingRow.style.display = 'none';
        if (typeof lastQuery !== 'undefined') lastQuery = null;
    }
    // every vector of the item, as a table
    function itemTable(card, item) {
        var t = document.createElement('table');
        var kind = item.kind;
        var names = kind === 'force' ? ['FX', 'FY', 'FZ', 'MX', 'MY', 'MZ'] : kind === 'displacement' ? ['DX', 'DY', 'DZ', 'RX', 'RY', 'RZ'] : ['UX', 'UY', 'UZ'];
        if (kind === 'wind') {
            var w = item.wind || {};
            ccRow(t, 'type', String(w.type || 'wind'));
            ccRow(t, w.type === 'wave' ? 'drag coefficient' : 'shape factor', f(num(w.shape), 4));
            ['addedMass', 'lift', 'marineGrowth', 'marineGrowthDensity'].forEach(function (k) { if (w[k] !== undefined) ccRow(t, k, f(num(w[k]), 4)); });
            card.appendChild(t);
            return;
        }
        var hr = document.createElement('tr');
        ['vec'].concat(names).concat(kind === 'uniform' ? ['g'] : []).forEach(function (h, k) {
            var td = document.createElement('td'); td.textContent = h; if (k) td.className = 'cc-num'; hr.appendChild(td);
        });
        t.appendChild(hr);
        (item.vectors || []).forEach(function (vc) {
            var tr = document.createElement('tr');
            if (vectorPick && +vc.v === vectorPick) tr.className = 'cc-win';
            var td0 = document.createElement('td');
            td0.textContent = item.pattern !== undefined ? String(item.pattern) : (kind === 'force' ? 'F' : kind === 'displacement' ? 'D' : 'U') + vc.v;
            tr.appendChild(td0);
            names.forEach(function (n, k) {
                var x = num((vc.values || [])[k]);
                var td = document.createElement('td'); td.className = 'cc-num';
                td.textContent = x === x ? f(x, 4) : (kind === 'displacement' ? 'free' : '—');
                tr.appendChild(td);
            });
            if (kind === 'uniform') { var tg = document.createElement('td'); tg.className = 'cc-num'; tg.textContent = vc.g ? 'yes' : ''; tr.appendChild(tg); }
            t.appendChild(tr);
        });
        card.appendChild(t);
    }
    function showPinCard(hit) {
        if (typeof elCalcCard === 'undefined' || !elCalcCard) return;
        var en = entries[hit.entry];
        elCalcCard.style.display = '';
        elCalcCard.innerHTML = '';
        var head = document.createElement('div');
        head.className = 'cc-head';
        head.textContent = 'Load ' + en.item.name + '  (' + whereText(en) + ')';
        elCalcCard.appendChild(head);
        if (en.v2) { pinCardV2(en); return; }
        var u = en.item.kind === 'force' ? (units.force || '') + ' / ' + (units.moment || '')
              : en.item.kind === 'displacement' ? (units.length || '') + ' / ' + (units.rotation || 'deg')
              : en.item.kind === 'uniform' ? (units.uniform || '') + ' (or g)' : '';
        ccSub(elCalcCard, (en.item.kind === 'wind' ? 'Wind data' : 'Load vectors') + (u ? ' [' + u + ']' : ''));
        itemTable(elCalcCard, en.item);
        // the other loads at the same node
        if (en.ni !== undefined) {
            var id = view.nodeIds[en.ni];
            (itemsRef || []).forEach(function (it) {
                if (it === en.item || !(it.nodeIds || []).some(function (x) { return x === id; }) || (it.kind !== 'force' && it.kind !== 'displacement')) return;
                ccSub(elCalcCard, 'Also at node ' + id + ': ' + it.name);
                itemTable(elCalcCard, it);
            });
        }
        var note = document.createElement('div');
        note.className = 'fea-hint';
        note.style.marginTop = '6px';
        note.textContent = 'Load vectors as CAESAR numbers them; CAESAR builds the load cases from them.';
        elCalcCard.appendChild(note);
    }
    // a SAP load: its fields, how the selection draws it, and the other loads on the same member / joint
    function pinCardV2(en) {
        var it = en.item, kind = String(it.kind || '');
        ccSub(elCalcCard, 'Pattern ' + it.pattern + (res && (res.kind === 'case' || res.kind === 'combination') ? ' (drawn \u00d7' + sig(en.factor) + ' in ' + res.kind + ' ' + res.name + ')' : ''));
        var t = document.createElement('table');
        ccRow(t, 'load', kindName(en));
        if (kind === 'force' || kind === 'displacement') {
            elCalcCard.appendChild(t);
            itemTable(elCalcCard, it);
            t = document.createElement('table');
        } else if (kind === 'temperature') {
            ccRow(t, 'type', String(it.type || 'Temperature'));
            ccRow(t, 'ΔT' + (units.temperature ? ' [' + units.temperature + ']' : ''), sig(it.dT));
        } else {
            ccRow(t, 'direction', dirText(it.dir) + (it.coordSys && !/^global$/i.test(String(it.coordSys)) ? ' (' + it.coordSys + ')' : ''));
            if (kind === 'point') {
                ccRow(t, 'at', atText(it.at).replace(/^ at /, ''));
                ccRow(t, 'value' + (unitOf(en) ? ' [' + unitOf(en) + ']' : ''), sig(it.value));
            } else {
                ccRow(t, 'from', atText(it.from).replace(/^ at /, ''));
                ccRow(t, 'to', atText(it.to).replace(/^ at /, ''));
                ccRow(t, 'value A' + (unitOf(en) ? ' [' + unitOf(en) + ']' : ''), sig(it.valueA));
                ccRow(t, 'value B' + (unitOf(en) ? ' [' + unitOf(en) + ']' : ''), sig(it.valueB));
            }
        }
        elCalcCard.appendChild(t);
        var others = en.ni !== undefined ? itemsAt(view.nodeIds[en.ni], 0) : itemsAt(null, view.elemIds[en.b]);
        others = others.filter(function (o) { return o !== it; });
        if (others.length) {
            ccSub(elCalcCard, en.ni !== undefined ? 'Also at this joint' : 'Also on this frame');
            var t2 = document.createElement('table');
            others.forEach(function (o) { var s = itemText(o), k = s.indexOf(': '); ccRow(t2, o.pattern !== undefined ? String(o.pattern) : (o.name || ''), k >= 0 ? s.slice(k + 2) : s); });
            elCalcCard.appendChild(t2);
        }
        var note = document.createElement('div');
        note.className = 'fea-hint';
        note.style.marginTop = '6px';
        note.textContent = 'As SAP holds it, in load pattern ' + it.pattern + '. Loads of: picks a pattern, case or combination to draw.';
        elCalcCard.appendChild(note);
    }

    // loads touching a node id / an element number (the property card lists them); a version-2 frame
    // load (point, distributed, temperature) by its beam id, whatever the selection
    function itemsAt(nodeId, elementNo) {
        return (itemsRef || []).filter(function (it) {
            if (!it) return false;
            if (nodeId !== undefined && nodeId !== null && (it.kind === 'force' || it.kind === 'displacement') && (it.nodeIds || []).indexOf(nodeId) >= 0) return true;
            if (elementNo && (it.kind === 'uniform' || it.kind === 'wind') && (it.elements || []).indexOf(elementNo) >= 0) return true;
            if (elementNo && BEAM_KINDS[it.kind] && (it.members || []).some(function (m) {
                return m && (!m.domain || m.domain === 'beams') && (m.ids || []).indexOf(elementNo) >= 0;
            })) return true;
            return false;
        });
    }

    // ---- UI ------------------------------------------------------------------
    if (elShow) elShow.addEventListener('change', function () { visible = this.checked; update(); });
    if (elLabels) elLabels.addEventListener('change', function () { labels = this.checked; syncLabels(); if (typeof needsRender !== 'undefined') needsRender = true; });
    if (elSize) elSize.addEventListener('input', function () {
        var v = parseFloat(this.value);
        if (!(v > 0)) return;
        size = v;
        try { localStorage.setItem('pluto.ldSize', String(v)); } catch (e) {}
        update();
    });
    if (elVector) elVector.addEventListener('change', function () { vectorPick = parseInt(this.value, 10) || 0; update(); });
    if (elWind) elWind.addEventListener('change', function () {
        windFrom = this.value;
        try { localStorage.setItem('pluto.ldWind', windFrom); } catch (e) {}
        update();
    });
    if (elPick) elPick.addEventListener('change', function () { setPick(this.value, true); });
    Object.keys(elKinds).forEach(function (k) {
        if (elKinds[k]) elKinds[k].addEventListener('change', function () { kindsOn[k] = this.checked; update(); });
    });

    return {
        onModelLoaded: onModelLoaded,
        onModelCleared: onModelCleared,
        onFeatures: onFeatures,
        writeVis: writeVis,
        pick: pickHit,
        fillReadout: fillReadout,
        showPinCard: showPinCard,
        itemsAt: itemsAt,
        vectorText: function (item, vc) { return vectorText({ kind: item.kind === 'force' ? 'force' : item.kind, raw: vc.values, vec: vc.values }); },
        itemText: itemText,
        // version 2: the "Loads of" pick ('all', 'p:NAME', 'c:NAME', 'k:NAME'; false on a version-1 section)
        // and what it resolves to ({v2, value, kind, name, factors, order, union, notes, selfWeight, text})
        setSelection: function (value) { return setPick(value, true); },
        selection: selection,
        factorOf: function (item) { return v2 ? factorOf(item) : 1; },
        units: function () { return units; },
        count: function () { return shown.length; },
        // test hooks (viewer/tests/test_loads.js)
        _entries: function () { return entries; },
        _meshes: function () { return meshes; },
        _shown: function () { return shown; },
        _parts: symbolParts,
        _text: shortText,
        _resolve: resolve,
        _options: pickOptions,
        _set: function (o) {
            if (o.size !== undefined) size = o.size;
            if (o.visible !== undefined) visible = o.visible;
            if (o.vector !== undefined) vectorPick = o.vector;
            if (o.windFrom !== undefined) windFrom = o.windFrom;
            if (o.kinds) Object.keys(o.kinds).forEach(function (k) { kindsOn[k] = !!o.kinds[k]; });
            if (o.labels !== undefined) labels = o.labels;
            update();
        }
    };
})();
