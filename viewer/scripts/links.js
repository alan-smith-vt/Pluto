// ================================================================
// links.js  --  link elements from the sidecar `links` section (2026-11-05).
// Spec: vault/format/features-sidecar.md (links), written by SapToPluto
// (FrameSidecar.Links) for every two-joint SAP link; a one-joint
// (grounded) link is a `supports` item instead (supports.js).
//
// Items: {id, name, label, type: linear | gap | hook | friction |
// multilinear | damper | other, linkType (SAP's name), nodeIds: [i, j],
// property, length, axis1, axis2 (unit, the link's local 1 and 2), dofs:
// {U1: {fixed, nonlinear, ke, ce, k, open, muSlow, muFast, radius, c, cexp, points: [{f, d}, ...]}, ...},
// tags}. Each draws a thin line between its two joints and a symbol at its
// midpoint, local 1 along the link:
//   linear       a coil spring
//   gap          two plates facing each other across a gap
//   hook         two hooks caught in each other (tension only)
//   friction     a slide plate with a block on it and hatching under it
//   multilinear  a zig-zag
//   damper       a dashpot (a pot with a rod out of it)
//   other        a diamond
// A zero-length link (coincident joints) draws the symbol alone at the
// joint, oriented by axis1. Sizes follow the drawn beams at the joints
// (3/4 of their radius, as the supports), with a floor from the model span,
// times the Size slider; along the link the symbol keeps inside its length.
//
// A link shorter than the members at its joints reach (a pipe shoe link
// from a beam's axis to a pipe's) runs inside them: its symbol also draws
// as a faint ghost on top of everything (depth test off), so it still
// shows. One InstancedMesh per primitive (the ghost shares its instances,
// buried ones first), headlight shaded, frustumCulled = false.
// Hover: the link (label, property, type, DOFs); a pin: a card with every
// DOF. A link inside a member wins the hover over that member's surface
// when it lies within its own size behind it (pick().slack, viewer.js
// feaPick).
//
// Visibility: the item's `hidden`, the section-cut isolate (a link shows
// when either joint is kept), the panel's Show.
//
// Hooks (guarded by window.FEALinks): viewer.js load / clear, feaPick,
// showReadout, pin; features.js sync -> onFeatures; sectionCut.js writeVis.
// FEALinks.text(itemIndex) gives the link as one line of text.
// ================================================================

var FEALinks = (function () {

    var nodes = null;         // node table (Float64Array xyz) of the primary file
    var nodeIds = null;       // node index -> id
    var nodeLabels = null;
    var nodeIndex = null;     // node id -> index
    var nodeR = null;         // node index -> outer radius of the drawn beams there (0 = none)
    var span = 1;
    var entries = [];         // one per drawable item: {item, idx, ni, nj, A, B, C, a1, a2, a3, len, zero, type, u, h, w}
    var itemsRef = null;      // the envelope's items array the entries came from
    var units = null;         // envelope.model.units
    var nodeKeep = null;      // section-cut isolate mask (null = all)
    var visible = true;       // panel Show
    var size = 1;             // panel Size
    var lastSig = '';
    var meshes = null;        // {prim: {mesh, ghost, owner: Int32Array}}
    var root = null, line = null, material = null, ghostMaterial = null;
    var shown = [];           // entry indices drawn

    var TYPES = ['linear', 'gap', 'hook', 'friction', 'multilinear', 'damper', 'other'];
    var PRIMS = ['coil', 'rod', 'box', 'cup', 'arc', 'oct'];
    var TYPE_RGB = {
        linear: [30, 136, 229], gap: [67, 160, 71], hook: [216, 67, 21], friction: [121, 85, 72],
        multilinear: [251, 140, 0], damper: [142, 36, 170], other: [120, 120, 132]
    };
    var GHOST_ALPHA = 0.5;
    var MIN_UNIT_FRACTION = 0.0025;   // symbol unit floor = span x this

    var elSection = document.getElementById('lnkSection');
    var elShow    = document.getElementById('lnkShow');
    var elSize    = document.getElementById('lnkSize');
    var elCount   = document.getElementById('lnkCount');

    try {
        var sz = parseFloat(localStorage.getItem('pluto.lnkSize'));
        if (sz > 0) size = sz;
        if (localStorage.getItem('pluto.lnkShow') === '0') visible = false;
    } catch (e0) {}
    if (elSize) elSize.value = String(size);
    if (elShow) elShow.checked = visible;

    // ---- vectors ---------------------------------------------------------
    function unit(a) {
        if (!a || a.length < 3) return null;
        var v = new THREE.Vector3(+a[0], +a[1], +a[2]);
        var l = v.length();
        return (l > 1e-12 && isFinite(l)) ? v.multiplyScalar(1 / l) : null;
    }
    function perp(a) {
        var t = Math.abs(a.x) < 0.9 ? new THREE.Vector3(1, 0, 0) : new THREE.Vector3(0, 1, 0);
        return t.cross(a).normalize();
    }
    var Yax = new THREE.Vector3(0, 1, 0);
    function quatTo(from, to) { return new THREE.Quaternion().setFromUnitVectors(from, to); }
    // rotation taking the primitive's x / y / z to x / y / x cross y
    function quatBasis(x, y) {
        var z = new THREE.Vector3().crossVectors(x, y);
        return new THREE.Quaternion().setFromRotationMatrix(new THREE.Matrix4().makeBasis(x, y, z));
    }
    function nf(v) {
        v = +v;
        if (!isFinite(v)) return String(v);
        var a = Math.abs(v);
        return (a !== 0 && (a < 1e-4 || a >= 1e7)) ? v.toExponential(3) : String(+v.toPrecision(6));
    }

    // ---- model -----------------------------------------------------------
    function onModelLoaded() {
        onModelCleared();
        var bv = window.FEABeams ? FEABeams.view() : null;
        var src = bv || (typeof feaModel !== 'undefined' ? feaModel : null);
        if (!src || !src.nodes || !src.nodeIds) return;
        nodes = src.nodes; nodeIds = src.nodeIds; nodeLabels = src.nodeLabels || null;
        nodeIndex = new Map();
        for (var i = 0; i < nodeIds.length; i++) nodeIndex.set(nodeIds[i], i);
        var nN = nodes.length / 3;
        nodeR = new Float64Array(nN);
        if (bv && FEABeams.build && FEABeams.build()) {
            var build = FEABeams.build(), secs = bv.sections || [], REC = bv.elemRecordU32, el = bv.elems;
            for (var e = 0; e < bv.header.nElements; e++) for (var k = 0; k < 2; k++) {
                var r = sectionRadius(secs[k ? build.sectionOfB[e] : build.sectionOf[e]]);
                var n = el[e * REC + 1 + k];
                if (r > nodeR[n]) nodeR[n] = r;
            }
        }
        var lo = [Infinity, Infinity, Infinity], hi = [-Infinity, -Infinity, -Infinity];
        for (var j = 0; j < nodes.length; j += 3) for (var a = 0; a < 3; a++) {
            if (nodes[j + a] < lo[a]) lo[a] = nodes[j + a];
            if (nodes[j + a] > hi[a]) hi[a] = nodes[j + a];
        }
        span = Math.max(hi[0] - lo[0], hi[1] - lo[1], hi[2] - lo[2]);
        if (!(span > 0) || !isFinite(span)) span = 1;
    }
    function sectionRadius(sec) {
        if (!sec) return 0;
        var p = sec.params || {};
        if (sec.type === 'PIPE') return (p.od || 0) / 2;
        var m = 0;
        ['b', 'h', 'd', 'bf', 'bfTop', 'bfBot'].forEach(function (k) { if (p[k] > m) m = p[k]; });
        if (!m && sec.points && sec.points.length) {     // POLY: the outline's reach from the axis
            var r = 0;
            sec.points.forEach(function (q) { var d = Math.sqrt(q[0] * q[0] + q[1] * q[1]); if (d > r) r = d; });
            return r;
        }
        return m / 2;
    }
    function onModelCleared() {
        disposeMeshes();
        nodes = null; nodeIds = null; nodeLabels = null; nodeIndex = null; nodeR = null; span = 1;
        entries = []; itemsRef = null; units = null; nodeKeep = null; lastSig = ''; shown = [];
        if (elSection) elSection.style.display = 'none';
    }
    function disposeMeshes() {
        if (root && typeof scene !== 'undefined' && scene) scene.remove(root);
        if (meshes) PRIMS.forEach(function (p) {
            var m = meshes[p];
            if (!m) return;
            m.mesh.geometry.dispose();          // shared by the ghost
            if (m.mesh.dispose) m.mesh.dispose();
            if (m.ghost && m.ghost.dispose) m.ghost.dispose();
        });
        if (line) { line.geometry.dispose(); line.material.dispose(); }
        meshes = null; root = null; line = null;
    }

    // ---- sidecar items -> entries -----------------------------------------
    function envelopeSection() {
        var env = window.FEAFeatures ? FEAFeatures.envelope() : null;
        return env && env.links && Array.isArray(env.links.items) ? env.links : null;
    }
    function typeOf(item) {
        var t = String(item && item.type || '').toLowerCase();
        return TYPE_RGB[t] ? t : 'other';
    }
    function parseItems(sec) {
        entries = [];
        itemsRef = sec ? sec.items : null;
        var env = window.FEAFeatures ? FEAFeatures.envelope() : null;
        units = env && env.model && env.model.units ? env.model.units : null;
        if (!nodes || !itemsRef) return;
        itemsRef.forEach(function (item, idx) {
            if (!item || !Array.isArray(item.nodeIds) || !item.nodeIds.length) return;
            var ni = nodeIndex.get(item.nodeIds[0]);
            var nj = nodeIndex.get(item.nodeIds.length > 1 ? item.nodeIds[1] : item.nodeIds[0]);
            if (ni === undefined || nj === undefined) return;
            var A = new THREE.Vector3(nodes[ni * 3], nodes[ni * 3 + 1], nodes[ni * 3 + 2]);
            var B = new THREE.Vector3(nodes[nj * 3], nodes[nj * 3 + 1], nodes[nj * 3 + 2]);
            var d = A.distanceTo(B);
            var zero = !(d > span * 1e-6);
            var a1 = unit(item.axis1) || (zero ? null : B.clone().sub(A).normalize()) || new THREE.Vector3(0, 0, 1);
            var a2 = unit(item.axis2);
            if (!a2 || Math.abs(a2.dot(a1)) > 0.99) a2 = perp(a1);
            else a2.addScaledVector(a1, -a2.dot(a1)).normalize();
            var a3 = new THREE.Vector3().crossVectors(a1, a2).normalize();
            entries.push({ item: item, idx: idx, ni: ni, nj: nj, A: A, B: B, C: zero ? A.clone() : A.clone().lerp(B, 0.5),
                           a1: a1, a2: a2, a3: a3, len: zero ? 0 : d, zero: zero, type: typeOf(item) });
        });
    }

    // ---- visibility ----------------------------------------------------------
    function entryVisible(en) {
        if (en.item.hidden === true) return false;
        if (nodeKeep && !nodeKeep[en.ni] && !nodeKeep[en.nj]) return false;
        return true;
    }

    // ---- symbols -----------------------------------------------------------
    // Sizes of one entry: u (the unit), h (half-length along local 1), w (half-width, at most
    // 1.5 h so a short link's symbol stays in proportion).
    function sizes(en) {
        var R = Math.max(nodeR ? nodeR[en.ni] : 0, nodeR ? nodeR[en.nj] : 0);
        var u = Math.max(0.75 * R, span * MIN_UNIT_FRACTION) * size;
        var h = en.zero ? u : Math.min(u, 0.45 * en.len);
        return { u: u, h: h, w: Math.min(u, 1.5 * h) };
    }
    // A link shorter than the members at its joints reach runs inside them: its symbol gets the ghost pass.
    function buried(en) {
        var r = (nodeR ? nodeR[en.ni] : 0) + (en.nj !== en.ni && nodeR ? nodeR[en.nj] : 0);
        return r > 0 && en.len < r;
    }

    // Parts of one entry: [{prim, pos, quat, scale}], all in the link's colour.
    function symbolParts(en) {
        var s = sizes(en), u = s.u, h = s.h, w = s.w;
        var C = en.C, a1 = en.a1, a2 = en.a2, a3 = en.a3;
        var out = [];
        function put(prim, pos, quat, sx, sy, sz) {
            out.push({ prim: prim, pos: pos, quat: quat || new THREE.Quaternion(), scale: new THREE.Vector3(sx, sy, sz) });
        }
        function at(t, o2) { return C.clone().addScaledVector(a1, t).addScaledVector(a2, o2 || 0); }
        // a rod from p to q, radius r
        function rod(p, q, r) {
            var d = q.clone().sub(p), l = d.length();
            if (!(l > 0)) return;
            put('rod', p.clone().lerp(q, 0.5), quatTo(Yax, d.multiplyScalar(1 / l)), r, l, r);
        }
        var q1 = quatTo(Yax, a1);                     // primitive y along local 1
        var th = 0.1 * u;
        switch (en.type) {
            case 'linear':                            // coil spring, radius w/2
                put('coil', C.clone(), q1, 0.5 * w / 0.3, 2 * h, 0.5 * w / 0.3);
                break;
            case 'gap': {                             // two plates across local 1, a gap between
                var g = 0.5 * h;
                [-1, 1].forEach(function (sg) {
                    put('box', at(sg * (g / 2 + th / 2)), q1, 2 * w, th, 2 * w);
                    if (en.zero) rod(at(sg * (g / 2 + th)), at(sg * h), 0.06 * u);
                });
                break;
            }
            case 'hook': {                            // two C hooks caught in each other
                var r = 0.5 * h, dd = 0.3 * h;
                put('arc', at(-dd), quatBasis(a1, a2), r, r, r);                   // closed side towards +local 1
                put('arc', at(dd), quatBasis(a1.clone().negate(), a3), r, r, r);   // closed side towards -local 1
                if (en.zero) { rod(at(-dd - r), at(-h - 0.4 * h), 0.06 * u); rod(at(dd + r), at(h + 0.4 * h), 0.06 * u); }
                break;
            }
            case 'friction': {                        // slide plate, a block on it, hatching under it
                put('box', C.clone(), q1, 2 * w, th, 2 * w);
                put('box', at(th / 2 + 0.25 * h), q1, w, 0.5 * h, w);
                var hd = a1.clone().add(a2).normalize(), hl = 0.45 * h;
                [-0.75, -0.25, 0.25, 0.75].forEach(function (f) {
                    var mid = at(-th / 2 - 0.5 * hl * 0.7071, f * w);
                    put('rod', mid, quatTo(Yax, hd), 0.04 * u, hl, 0.04 * u);
                });
                break;
            }
            case 'multilinear': {                     // zig-zag in the local 1-2 plane
                var n = 6, prev = at(-h);
                for (var i = 1; i <= n; i++) {
                    var p = at(-h + 2 * h * i / n, i === n ? 0 : (i % 2 ? 1 : -1) * 0.7 * w);
                    rod(prev, p, 0.06 * u);
                    prev = p;
                }
                break;
            }
            case 'damper':                            // dashpot: a pot, a rod out of it, a plate on the rod
                put('cup', at(-0.45 * h), q1, 0.75 * w, 0.9 * h, 0.75 * w);
                rod(at(-0.2 * h), at(h), 0.08 * u);
                put('cup', at(h - 0.04 * u), q1, 0.55 * w, 0.08 * u, 0.55 * w);
                break;
            default:                                  // a diamond
                put('oct', C.clone(), q1, w, 1.2 * w, w);
        }
        return out;
    }

    function helixGeometry() {
        var pts = [], turns = 5, n = turns * 16;
        for (var i = 0; i <= n; i++) {
            var t = i / n, a = 2 * Math.PI * turns * t;
            pts.push(new THREE.Vector3(0.3 * Math.cos(a), t - 0.5, 0.3 * Math.sin(a)));
        }
        return new THREE.TubeGeometry(new THREE.CatmullRomCurve3(pts), n * 2, 0.035, 6, false);
    }
    function primGeometry(p) {
        switch (p) {
            case 'coil': return helixGeometry();
            case 'rod':  return new THREE.CylinderGeometry(1, 1, 1, 8);
            case 'box':  return new THREE.BoxGeometry(1, 1, 1);
            case 'cup':  return new THREE.CylinderGeometry(1, 1, 1, 20);
            case 'arc': {                             // a C in the x-y plane, open towards -x, radius 1
                var g = new THREE.TorusGeometry(1, 0.11, 8, 28, Math.PI * 4 / 3);
                g.rotateZ(Math.PI * 4 / 3);
                return g;
            }
            default:     return new THREE.OctahedronGeometry(1, 0);
        }
    }

    // Headlight shading on the instance colour (as supports.js); the ghost pass is
    // the same, faint, drawn over everything.
    function makeMaterial(alpha) {
        var m = new THREE.ShaderMaterial({
            uniforms: { uAlpha: { value: alpha } },
            vertexShader: [
                'varying vec3 vCol;',
                'varying vec3 vN;',
                'varying vec3 vV;',
                'void main() {',
                '  vec4 p = vec4(position, 1.0);',
                '  vec3 n = normal;',
                '#ifdef USE_INSTANCING',
                '  p = instanceMatrix * p;',
                '  mat3 m = mat3(instanceMatrix);',
                '  n = m * (n / vec3(dot(m[0], m[0]), dot(m[1], m[1]), dot(m[2], m[2])));',
                '#endif',
                '#ifdef USE_INSTANCING_COLOR',
                '  vCol = instanceColor;',
                '#else',
                '  vCol = vec3(0.8);',
                '#endif',
                '  vec4 mv = modelViewMatrix * p;',
                '  vN = normalize(normalMatrix * n);',
                '  vV = -mv.xyz;',
                '  gl_Position = projectionMatrix * mv;',
                '}'
            ].join('\n'),
            fragmentShader: [
                'uniform float uAlpha;',
                'varying vec3 vCol;',
                'varying vec3 vN;',
                'varying vec3 vV;',
                'void main() {',
                '  float l = 0.45 + 0.55 * abs(dot(normalize(vN), normalize(vV)));',
                '  gl_FragColor = vec4(vCol * l, uAlpha);',
                '}'
            ].join('\n')
        });
        if (alpha < 1) { m.transparent = true; m.depthTest = false; m.depthWrite = false; }
        return m;
    }

    function rebuild() {
        disposeMeshes();
        shown = [];
        if (!nodes || !entries.length) { syncPanel(); return; }
        var parts = {};
        PRIMS.forEach(function (p) { parts[p] = []; });
        var lpos = [], lcol = [];
        // a buried link's parts first in each primitive's list: the ghost draws the first n instances
        var under = {}, over = {};
        PRIMS.forEach(function (p) { under[p] = []; over[p] = []; });
        if (visible) entries.forEach(function (en, i) {
            if (!entryVisible(en)) return;
            shown.push(i);
            var b = buried(en);
            symbolParts(en).forEach(function (pt) { pt.owner = i; (b ? under : over)[pt.prim].push(pt); });
            if (!en.zero) {
                var c = TYPE_RGB[en.type];
                lpos.push(en.A.x, en.A.y, en.A.z, en.B.x, en.B.y, en.B.z);
                for (var t = 0; t < 2; t++) lcol.push(c[0] / 255, c[1] / 255, c[2] / 255);
            }
        });
        if (!material) { material = makeMaterial(1); ghostMaterial = makeMaterial(GHOST_ALPHA); }
        root = new THREE.Group();
        meshes = {};
        var m4 = new THREE.Matrix4(), col = new THREE.Color();
        PRIMS.forEach(function (p) {
            var list = under[p].concat(over[p]);
            if (!list.length) return;
            var geo = primGeometry(p);
            var mesh = new THREE.InstancedMesh(geo, material, list.length);
            var owner = new Int32Array(list.length);
            list.forEach(function (pt, k) {
                mesh.setMatrixAt(k, m4.compose(pt.pos, pt.quat, pt.scale));
                var c = TYPE_RGB[entries[pt.owner].type];
                mesh.setColorAt(k, col.setRGB(c[0] / 255, c[1] / 255, c[2] / 255));
                owner[k] = pt.owner;
            });
            mesh.instanceMatrix.needsUpdate = true;
            if (mesh.instanceColor) mesh.instanceColor.needsUpdate = true;
            mesh.frustumCulled = false;
            var ghost = null;
            if (under[p].length) {
                ghost = new THREE.InstancedMesh(geo, ghostMaterial, list.length);
                ghost.instanceMatrix = mesh.instanceMatrix;      // the same instances, drawn on top, faint
                ghost.instanceColor = mesh.instanceColor;
                ghost.count = under[p].length;                   // the buried links' parts only
                ghost.frustumCulled = false;
                ghost.renderOrder = 4;
            }
            meshes[p] = { mesh: mesh, ghost: ghost, owner: owner, ghosted: under[p].length };
            root.add(mesh);
            if (ghost) root.add(ghost);
        });
        if (lpos.length) {
            var g = new THREE.BufferGeometry();
            g.setAttribute('position', new THREE.BufferAttribute(new Float32Array(lpos), 3));
            g.setAttribute('color', new THREE.BufferAttribute(new Float32Array(lcol), 3));
            line = new THREE.LineSegments(g, new THREE.LineBasicMaterial({ vertexColors: true, transparent: true, opacity: 0.8 }));
            line.frustumCulled = false;
            root.add(line);
        }
        if (typeof scene !== 'undefined' && scene) scene.add(root);
        syncPanel();
    }

    // ---- features hook -------------------------------------------------------
    function onFeatures() {
        var sec = envelopeSection();
        var items = sec ? sec.items : null;
        if (items !== itemsRef) { parseItems(sec); lastSig = ''; }
        update();
    }
    function update() {
        var sig = visible + '|' + size + '|';
        for (var i = 0; i < entries.length; i++) sig += entryVisible(entries[i]) ? '1' : '0';
        if (sig !== lastSig || (!meshes && entries.length)) { lastSig = sig; rebuild(); }
        if (typeof needsRender !== 'undefined') needsRender = true;
    }
    function writeVis(keep) { nodeKeep = keep || null; update(); }

    // ---- text ------------------------------------------------------------------
    var DOF_ORDER = ['U1', 'U2', 'U3', 'R1', 'R2', 'R3'];
    var DOF_KEYS = [['ke', 'ke'], ['ce', 'ce'], ['k', 'k'], ['open', 'open'], ['muSlow', '\u03bc slow'], ['muFast', '\u03bc fast'], ['radius', 'radius'],
                    ['c', 'c'], ['cexp', 'exp']];
    function dofNames(item) {
        var d = item && item.dofs ? item.dofs : {};
        var names = DOF_ORDER.filter(function (k) { return d[k]; });
        Object.keys(d).forEach(function (k) { if (names.indexOf(k) < 0) names.push(k); });
        return names;
    }
    // "nonlinear, ke 1000, k 1000, open 0" / "fixed"
    function dofValues(d) {
        if (!d) return '';
        if (d.fixed) return 'fixed';
        var out = [d.nonlinear ? 'nonlinear' : 'linear'];
        DOF_KEYS.forEach(function (k) { if (d[k[0]] !== undefined && d[k[0]] !== null) out.push(k[1] + ' ' + nf(d[k[0]])); });
        // a multilinear curve: (displacement, force) pairs, as a graph reads them
        if (d.points && d.points.length) out.push('points (d, F) ' + d.points.map(function (p) { return '(' + nf(p.d) + ', ' + nf(p.f) + ')'; }).join(' '));
        return out.join(', ');
    }
    function dofText(item) {
        var d = item && item.dofs ? item.dofs : {};
        var names = dofNames(item);
        return names.length ? names.map(function (k) { return k + ' ' + dofValues(d[k]); }).join('; ') : 'no DOFs';
    }
    function typeText(item) {
        var t = typeOf(item), lt = item.linkType ? String(item.linkType) : '';
        return t + (lt && lt.toLowerCase() !== t ? ' (' + lt + ')' : '');
    }
    function lengthText(item) {
        var l = +item.length;
        return l > 0 ? 'length ' + nf(l) + (units && units.length ? ' ' + units.length : '') : 'zero length';
    }
    // "K1 REST: gap (Gap), property REST, length 6 in; U1 nonlinear, ke 1000, k 1000, open 0"
    function textOfItem(item) {
        if (!item) return '';
        return (item.label || item.name || item.id || 'link') + (item.property ? ' ' + item.property : '') + ': ' + typeText(item) +
            (item.property ? ', property ' + item.property : '') + ', ' + lengthText(item) + '; ' + dofText(item);
    }
    function text(itemIndex) {
        if (!itemsRef) { var sec = envelopeSection(); if (!sec) return ''; return textOfItem(sec.items[itemIndex]); }
        return textOfItem(itemsRef[itemIndex]);
    }

    // ---- picking / readout -------------------------------------------------------
    function pick(raycaster) {
        if (!meshes || !visible) return null;
        var best = null;
        PRIMS.forEach(function (p) {
            var m = meshes[p];
            if (!m) return;
            var hits = raycaster.intersectObject(m.mesh);
            for (var i = 0; i < hits.length; i++) {
                if (hits[i].instanceId == null) continue;
                if (!best || hits[i].distance < best.distance) {
                    var en = entries[m.owner[hits[i].instanceId]];
                    best = { link: true, entry: m.owner[hits[i].instanceId], point: hits[i].point, distance: hits[i].distance,
                             slack: 2 * sizes(en).u };
                }
                break;
            }
        });
        return best;
    }
    function nodeText(ni) {
        var lbl = nodeLabels && nodeLabels.get ? nodeLabels.get(ni) : '';
        return String(nodeIds[ni]) + (lbl ? ' [' + lbl + ']' : '');
    }
    function fillReadout(hit) {
        var en = entries[hit.entry], it = en.item;
        elRoValue.textContent = (it.label || it.name || 'link') + '  ' + typeOf(it);
        elRoValue.className = 'ro-value';
        elRoComp.textContent = 'link ' + typeText(it) + (it.property ? ', property ' + it.property : '');
        elRoElem.textContent = (it.name || it.label || '') + '  (link, ' + lengthText(it) + ')';
        elRoNode.textContent = nodeText(en.ni) + (en.nj !== en.ni ? ' \u2192 ' + nodeText(en.nj) : '');
        elRoCorners.textContent = dofText(it);
        elRoUV.textContent = '\u2014';
        elRoPos.textContent = roPosText(hit.point);
        if (typeof elRoControllingRow !== 'undefined' && elRoControllingRow) elRoControllingRow.style.display = 'none';
        if (typeof lastQuery !== 'undefined') lastQuery = null;
    }
    function showPinCard(hit) {
        if (typeof elCalcCard === 'undefined' || !elCalcCard) return;
        var en = entries[hit.entry], it = en.item;
        elCalcCard.style.display = '';
        elCalcCard.innerHTML = '';
        var head = document.createElement('div');
        head.className = 'cc-head';
        head.textContent = 'Link ' + (it.label || it.name || '') + (it.property ? '  (' + it.property + ')' : '');
        elCalcCard.appendChild(head);
        ccSub(elCalcCard, 'Link');
        var t = document.createElement('table');
        ccRow(t, 'type', typeText(it));
        ccRow(t, 'joints', nodeText(en.ni) + (en.nj !== en.ni ? ' \u2192 ' + nodeText(en.nj) : ''));
        ccRow(t, 'length', lengthText(it).replace(/^length /, ''));
        ccRow(t, 'local 1', [en.a1.x, en.a1.y, en.a1.z].map(function (x) { return nf(+x.toFixed(6)); }).join('  '));
        ccRow(t, 'local 2', [en.a2.x, en.a2.y, en.a2.z].map(function (x) { return nf(+x.toFixed(6)); }).join('  '));
        elCalcCard.appendChild(t);
        var u = units && units.force && units.length ? ' [' + units.force + ', ' + units.length + ']' : '';
        ccSub(elCalcCard, 'DOFs' + u);
        var t2 = document.createElement('table');
        var names = dofNames(it);
        if (!names.length) ccRow(t2, '\u2014', 'none written');
        names.forEach(function (k) { ccRow(t2, k, dofValues(it.dofs[k])); });
        elCalcCard.appendChild(t2);
    }

    // ---- panel ---------------------------------------------------------------
    function syncPanel() {
        if (!elSection) return;
        elSection.style.display = entries.length ? '' : 'none';
        if (elCount) {
            var by = {};
            entries.forEach(function (en) { by[en.type] = (by[en.type] || 0) + 1; });
            elCount.textContent = shown.length + ' / ' + entries.length + ' shown' +
                (entries.length ? ' (' + TYPES.filter(function (k) { return by[k]; }).map(function (k) { return by[k] + ' ' + k; }).join(', ') + ')' : '');
        }
    }

    // ---- UI ------------------------------------------------------------------
    if (elShow) elShow.addEventListener('change', function () {
        visible = this.checked;
        try { localStorage.setItem('pluto.lnkShow', visible ? '1' : '0'); } catch (e) {}
        update();
    });
    if (elSize) elSize.addEventListener('input', function () {
        var v = parseFloat(this.value);
        if (!(v > 0)) return;
        size = v;
        try { localStorage.setItem('pluto.lnkSize', String(v)); } catch (e) {}
        update();
    });

    return {
        onModelLoaded: onModelLoaded,
        onModelCleared: onModelCleared,
        onFeatures: onFeatures,
        writeVis: writeVis,
        pick: pick,
        fillReadout: fillReadout,
        showPinCard: showPinCard,
        // the link at index i of the sidecar's links items, as one line ('' when none)
        text: text,
        count: function () { return shown.length; },
        // test hooks (viewer/tests/test_links.js)
        _entries: function () { return entries; },
        _meshes: function () { return meshes; },
        _line: function () { return line; },
        _parts: symbolParts,
        _sizes: sizes,
        _set: function (o) {
            if (o.size !== undefined) size = o.size;
            if (o.visible !== undefined) visible = o.visible;
            update();
        }
    };
})();
