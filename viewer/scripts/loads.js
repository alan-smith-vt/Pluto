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
// Hooks (guarded by window.FEALoads): viewer.js load / clear, feaPick,
// showReadout, pin; features.js sync -> onFeatures; sectionCut.js
// writeVis.
// ================================================================

var FEALoads = (function () {

    var view = null;          // beam domain view (primary file)
    var nodeIndex = null;     // node id -> index
    var beamIndex = null;     // beam id -> index
    var nodeR = null;         // node index -> outer radius drawn there
    var beamR = null;         // beam index -> outer radius
    var span = 1;
    var entries = [];         // one per drawn load: {item, kind, v, ni | beams, vec, mag, label}
    var itemsRef = null;
    var units = {};
    var nodeKeep = null;
    var visible = true, size = 1, labels = true;
    var vectorPick = 0;       // 0 = all vectors
    var windFrom = '-X';      // the side wind arrows come from (+X = blowing towards +X)
    var kindsOn = { force: true, moment: true, displacement: true, uniform: true, wind: true };
    var meshes = null, root = null, material = null, shown = [], lastSig = '';
    var maxMag = {};          // kind -> largest magnitude (arrow length scale)

    var PRIMS = ['cone', 'cyl'];
    var KIND_RGB = {
        force: [239, 83, 80], moment: [171, 71, 188], displacement: [236, 64, 122],
        uniform: [102, 187, 106], g: [38, 166, 154], wind: [100, 181, 246]
    };
    var MIN_UNIT_FRACTION = 0.0025;
    var MAX_ROW_ARROWS = 4000;

    var elSection = document.getElementById('ldSection');
    var elShow    = document.getElementById('ldShow');
    var elSize    = document.getElementById('ldSize');
    var elVector  = document.getElementById('ldVector');
    var elLabels  = document.getElementById('ldLabels');
    var elWind    = document.getElementById('ldWind');
    var elCount   = document.getElementById('ldCount');
    var elNote    = document.getElementById('ldNote');
    var elKinds   = {};
    ['force', 'moment', 'displacement', 'uniform', 'wind'].forEach(function (k) {
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
        view = null; nodeIndex = null; beamIndex = null; nodeR = null; beamR = null; span = 1;
        entries = []; itemsRef = null; nodeKeep = null; lastSig = ''; shown = []; maxMag = {};
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
        if (!view || !sec) return;
        sec.items.forEach(function (item, idx) {
            if (!item) return;
            var kind = String(item.kind || '');
            var vecs = Array.isArray(item.vectors) ? item.vectors : [];
            if (kind === 'force' || kind === 'displacement') {
                (item.nodeIds || []).forEach(function (id) {
                    var ni = nodeIndex.get(id);
                    if (ni === undefined) return;
                    vecs.forEach(function (vc) {
                        var vals = vc.values || [];
                        var t = [num(vals[0]), num(vals[1]), num(vals[2])], r = [num(vals[3]), num(vals[4]), num(vals[5])];
                        var tn = t.map(function (x) { return x === x ? x : 0; }), rn = r.map(function (x) { return x === x ? x : 0; });
                        var kT = kind === 'force' ? 'force' : 'displacement', kR = kind === 'force' ? 'moment' : 'displacement';
                        if (mag3(tn) > 0) add({ item: item, itemIdx: idx, kind: kT, rot: false, v: vc.v, ni: ni, vec: tn, raw: vals });
                        if (mag3(rn) > 0) add({ item: item, itemIdx: idx, kind: kR, rot: true, v: vc.v, ni: ni, vec: rn, raw: vals });
                    });
                });
            } else if (kind === 'uniform') {
                var bs = beamsOf(item);
                vecs.forEach(function (vc) {
                    var vals = (vc.values || []).map(function (x) { var y = num(x); return y === y ? y : 0; });
                    if (mag3(vals) > 0) add({ item: item, itemIdx: idx, kind: 'uniform', g: !!vc.g, v: vc.v, beams: bs, vec: vals, raw: vc.values });
                });
            } else if (kind === 'wind') {
                var w = item.wind || {};
                if (w.type === 'off') return;
                add({ item: item, itemIdx: idx, kind: 'wind', v: 0, beams: beamsOf(item), vec: null, shape: num(w.shape), wave: w.type === 'wave' });
            }
        });
        populateVectors();
    }
    function add(en) {
        en.mag = en.vec ? mag3(en.vec) : 1;
        var key = en.kind === 'uniform' ? (en.g ? 'g' : 'uniform') : en.kind + (en.rot ? 'R' : '');
        en.scaleKey = key;
        if (!(maxMag[key] >= en.mag)) maxMag[key] = en.mag;
        entries.push(en);
    }

    // ---- visibility --------------------------------------------------------
    function kindOn(en) { return !!kindsOn[en.kind === 'displacement' ? 'displacement' : en.kind]; }
    function entryVisible(en) {
        if (en.item.hidden === true) return false;
        if (!kindOn(en)) return false;
        if (vectorPick && en.kind !== 'wind' && +en.v !== vectorPick) return false;
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
    // arrow length: 0.4 .. 1 of the longest by |value|; node loads 6 pipe radii at most, rows 3.5
    function lengthFor(en, u) {
        var m = maxMag[en.scaleKey] || 1;
        var top = en.ni !== undefined ? Math.max(6 * u, 0.05 * span * size) : 3.5 * u;   // a node load reads on a long thin model
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

    function primGeometry(p) {
        return p === 'cone' ? new THREE.ConeGeometry(1, 1, 12) : new THREE.CylinderGeometry(1, 1, 1, 8);
    }
    function makeMaterial() {
        return new THREE.ShaderMaterial({
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
                '  gl_FragColor = vec4(vCol * l, 1.0);', '}'
            ].join('\n')
        });
    }
    function rgbOf(en) {
        var c = KIND_RGB[en.kind === 'uniform' && en.g ? 'g' : en.kind] || [200, 200, 200];
        return [c[0] / 255, c[1] / 255, c[2] / 255];
    }

    function rebuild() {
        disposeMeshes();
        shown = [];
        if (!view || !entries.length) { syncPanel(); syncLabels(); return; }
        var parts = { cone: [], cyl: [] };
        if (visible) {
            entries.forEach(function (en, i) {
                if (!entryVisible(en)) return;
                var ps = symbolParts(en);
                if (!ps.length) return;
                shown.push(i);
                ps.forEach(function (pt) { pt.owner = i; parts[pt.prim].push(pt); });
            });
        }
        if (!material) material = makeMaterial();
        root = new THREE.Group();
        meshes = {};
        var m4 = new THREE.Matrix4(), col = new THREE.Color();
        PRIMS.forEach(function (p) {
            var list = parts[p];
            if (!list.length) return;
            var mesh = new THREE.InstancedMesh(primGeometry(p), material, list.length);
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
    function unitOf(en) {
        if (en.kind === 'force') return units.force || '';
        if (en.kind === 'moment') return units.moment || '';
        if (en.kind === 'displacement') return en.rot ? (units.rotation || 'deg') : (units.length || '');
        if (en.kind === 'uniform') return en.g ? (units.g || 'g') : (units.uniform || '');
        return '';
    }
    function prefix(en) {
        return en.kind === 'force' ? 'F' : en.kind === 'moment' ? 'F' : en.kind === 'displacement' ? 'D' : en.kind === 'uniform' ? 'U' : '';
    }
    // "F1 1,200 lb." (the magnitude and its largest component's axis)
    function shortText(en) {
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

    function syncLabels() {
        if (!window.FEALabels) return;
        if (!labels || !visible) { FEALabels.set('loads', []); return; }
        var list = [];
        shown.forEach(function (i) {
            var en = entries[i];
            if (!en.anchor) return;
            list.push({ pos: en.anchor, text: shortText(en), cls: 'lbl-load lbl-' + en.kind, priority: 2 });
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
        var sig = visible + '|' + size + '|' + vectorPick + '|' + windFrom + '|' + JSON.stringify(kindsOn) + '|' + labels + '|';
        if (nodeKeep) for (var i = 0; i < nodeKeep.length; i++) sig += nodeKeep[i] ? '1' : '0';
        if (sig !== lastSig || (!meshes && entries.length)) { lastSig = sig; rebuild(); }
    }
    function writeVis(keep) { nodeKeep = keep || null; update(); }

    // ---- panel ---------------------------------------------------------------
    function populateVectors() {
        if (!elVector) return;
        var vs = {};
        entries.forEach(function (en) { if (en.kind !== 'wind' && +en.v > 0) vs[+en.v] = 1; });
        var keep = vectorPick;
        elVector.innerHTML = '';
        function opt(value, text) { var o = document.createElement('option'); o.value = value; o.textContent = text; elVector.appendChild(o); }
        opt('0', 'All vectors');
        Object.keys(vs).map(Number).sort(function (a, b) { return a - b; }).forEach(function (v) { opt(String(v), 'Vector ' + v + ' (F' + v + ' D' + v + ' U' + v + ')'); });
        vectorPick = vs[keep] ? keep : 0;
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
    function syncPanel() {
        if (!elSection) return;
        elSection.style.display = entries.length ? '' : 'none';
        if (elCount) {
            var by = {};
            entries.forEach(function (en) { by[en.kind] = (by[en.kind] || 0) + 1; });
            elCount.textContent = shown.length + ' / ' + entries.length + ' shown';
            ['force', 'moment', 'displacement', 'uniform', 'wind'].forEach(function (k) {
                var el = elKinds[k];
                if (!el) return;
                el.disabled = !by[k];
                var lab = el.parentNode && el.parentNode.querySelector ? el.parentNode.querySelector('.ld-n-' + k) : null;
                if (lab) lab.textContent = by[k] ? '(' + by[k] + ')' : '';
            });
        }
        if (elNote) elNote.textContent = (itemsRef && envelopeSection() && envelopeSection().note) || '';
    }

    // ---- picking / readout -----------------------------------------------------
    function pick(raycaster) {
        if (!meshes || !visible) return null;
        var best = null;
        PRIMS.forEach(function (p) {
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
        return en.kind === 'force' ? 'force' : en.kind === 'moment' ? 'moment' : en.kind === 'displacement' ? (en.rot ? 'imposed rotation' : 'imposed displacement')
             : en.kind === 'uniform' ? (en.g ? 'uniform load (g)' : 'uniform load') : en.wave ? 'wave' : 'wind';
    }
    function whereText(en) {
        if (en.ni !== undefined) return 'node ' + view.nodeIds[en.ni];
        var els = en.item.elements || [];
        return els.length ? 'element' + (els.length > 1 ? 's ' + els[0] + '..' + els[els.length - 1] : ' ' + els[0]) + ' (' + (en.item.name || '') + ')' : (en.item.name || '');
    }
    function fillReadout(hit) {
        var en = entries[hit.entry];
        elRoValue.textContent = shortText(en);
        elRoValue.className = 'ro-value';
        elRoComp.textContent = kindName(en) + (en.kind === 'wind' ? ': shape factor (direction set in CAESAR)' : ', vector ' + en.v);
        elRoElem.textContent = en.item.name + '  (load, ' + whereText(en) + ')';
        if (en.ni !== undefined) {
            var nlbl = view.nodeLabels ? view.nodeLabels.get(en.ni) : '';
            elRoNode.textContent = view.nodeIds[en.ni] + (nlbl ? ' [' + nlbl + ']' : '');
        } else elRoNode.textContent = (en.item.nodeIds || []).join(', ') || '—';
        elRoCorners.textContent = vectorText(en);
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
            td0.textContent = (kind === 'force' ? 'F' : kind === 'displacement' ? 'D' : 'U') + vc.v;
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

    // loads touching a node id / an element number (the property card lists them)
    function itemsAt(nodeId, elementNo) {
        return (itemsRef || []).filter(function (it) {
            if (nodeId !== undefined && nodeId !== null && (it.kind === 'force' || it.kind === 'displacement') && (it.nodeIds || []).indexOf(nodeId) >= 0) return true;
            if (elementNo && (it.kind === 'uniform' || it.kind === 'wind') && (it.elements || []).indexOf(elementNo) >= 0) return true;
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
    Object.keys(elKinds).forEach(function (k) {
        if (elKinds[k]) elKinds[k].addEventListener('change', function () { kindsOn[k] = this.checked; update(); });
    });

    return {
        onModelLoaded: onModelLoaded,
        onModelCleared: onModelCleared,
        onFeatures: onFeatures,
        writeVis: writeVis,
        pick: pick,
        fillReadout: fillReadout,
        showPinCard: showPinCard,
        itemsAt: itemsAt,
        vectorText: function (item, vc) { return vectorText({ kind: item.kind === 'force' ? 'force' : item.kind, raw: vc.values, vec: vc.values }); },
        units: function () { return units; },
        count: function () { return shown.length; },
        // test hooks (viewer/tests/test_loads.js)
        _entries: function () { return entries; },
        _meshes: function () { return meshes; },
        _shown: function () { return shown; },
        _parts: symbolParts,
        _text: shortText,
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
