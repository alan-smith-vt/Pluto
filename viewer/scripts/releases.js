// ================================================================
// releases.js  --  member end releases from the binary (2026-11-05).
// Spec: vault/format/v4-schema.md section 4.3 (BPRP slot 7), written by
// SapToPluto from SAP's frame releases; the sidecar's `properties` carry
// the same as text.
//
// BPRP slot 7 of each beam holds a u32 bit pattern (read through a
// Uint32Array view on the same buffer, never as a float): bits 0-5
// released at end A (P, V2, V3, T, M2, M3, SAP's order), 6-11 the same at
// end B, 12-23 partly fixed (a spring, not free; same layout), 24-31 zero.
// A pattern with any of bits 24-31 set is not a release mask (an older
// writer's float) and draws nothing.
//
// Axes: viewer local x = node A -> node B, y = BPRP slots 0-2, z = x cross y;
// SAP's local 2 = viewer z, local 3 = minus viewer y. So M3 turns the end in
// the x-z plane, M2 in the x-y plane, V2 slides along z, V3 along y.
//
// Symbols, a short way in from each released end (clear of the other
// members at the node), stacked along the member from the end:
//   M2 + M3   a hollow "globe": two rings, one in each bending plane
//   M3 / M2   one ring in the plane it turns in, with a pin through it
//             along the hinge axis (local 3 for M3, local 2 for M2)
//   T         a ring round the member (a collar)
//   V2 / V3   a hollow square in the plane of the slide (both: a cage)
//   P         a slot: two square plates across the member, a gap between
// A partly fixed DOF draws the same symbol in amber instead of indigo.
// Sizes follow the member's own section (its outline's radius) with a
// floor from the model span, times the Size slider.
//
// Visibility follows the beams: Beams > Show, and each beam's elemVis
// (section-cut isolate / crop box). One InstancedMesh per primitive (ring,
// rod, box), headlight shaded, frustumCulled = false; not pickable
// (the beam under them gives the readout, with a Releases row).
//
// Hooks (guarded by window.FEAReleases): viewer.js load / clear,
// showReadout (fillReadout / clearReadout); sectionCut.js writeVis.
// FEAReleases.text(elemIndex) gives "I: M2 M3; J: fixed" ('' for none),
// the same text as the sidecar's properties `releases`.
// ================================================================

var FEAReleases = (function () {

    var NAMES = ['P', 'V2', 'V3', 'T', 'M2', 'M3'];
    var P = 0, V2 = 1, V3 = 2, T = 3, M2 = 4, M3 = 5;
    var view = null;          // beam domain view (primary file)
    var build = null;         // FEABeams.build()
    var masks = null;         // Uint32Array view on BPRP (slot 7 per beam), or null
    var relElems = [];        // beam indices with a release
    var nodeEnds = null;      // node index -> [elem, end, ...] (only built when there are releases)
    var span = 1;
    var visible = true;       // panel Show
    var size = 1;             // panel Size
    var nodeKeep = null;      // section-cut mask (kept for the signature; beams' elemVis decides)
    var lastSig = '';
    var meshes = null;        // {prim: {mesh, owner: Int32Array, dof: [], partial: []}}
    var root = null;
    var material = null;
    var shown = [];           // beam indices drawn

    var PRIMS = ['ring', 'rod', 'box'];
    var RGB_FREE = [57, 73, 171];       // released
    var RGB_PARTIAL = [255, 143, 0];    // partly fixed
    var MIN_UNIT_FRACTION = 0.0025;     // symbol unit floor = span x this
    var BP = 8;                         // BPRP floats per beam (FEAv4.BPRP_F32)

    var elSection = document.getElementById('relSection');
    var elShow    = document.getElementById('relShow');
    var elSize    = document.getElementById('relSize');
    var elCount   = document.getElementById('relCount');
    var elRoRow   = document.getElementById('roRelRow');
    var elRo      = document.getElementById('roRel');
    var elBeamShow = document.getElementById('beamShow');

    try {
        var sz = parseFloat(localStorage.getItem('pluto.relSize'));
        if (sz > 0) size = sz;
        if (localStorage.getItem('pluto.relShow') === '0') visible = false;
    } catch (e0) {}
    if (elSize) elSize.value = String(size);
    if (elShow) elShow.checked = visible;

    // ---- bits ------------------------------------------------------------
    // {a, b, pa, pb}: 6-bit masks of the released / partly fixed DOFs at end A and B, or null.
    function decode(m) {
        m = m >>> 0;
        if (!m || (m >>> 24)) return null;
        var a = m & 63, b = (m >>> 6) & 63;
        if (!a && !b) return null;                       // partial bits alone release nothing
        return { a: a, b: b, pa: (m >>> 12) & 63 & a, pb: (m >>> 18) & 63 & b };
    }
    function maskOf(e) {
        return masks && e >= 0 && e < masks.length / BP ? masks[e * BP + 7] >>> 0 : 0;
    }
    // "M2 M3~" (~ = partly fixed) or "fixed"
    function endText(rel, part) {
        var out = [];
        for (var i = 0; i < 6; i++) if (rel & (1 << i)) out.push(NAMES[i] + (part & (1 << i) ? '~' : ''));
        return out.length ? out.join(' ') : 'fixed';
    }
    // "I: M2 M3; J: fixed", '' when the mask releases nothing
    function textOfMask(m) {
        var d = decode(m);
        if (!d) return '';
        return 'I: ' + endText(d.a, d.pa) + '; J: ' + endText(d.b, d.pb);
    }
    function text(e) { return textOfMask(maskOf(e)); }

    // ---- model -----------------------------------------------------------
    function onModelLoaded() {
        onModelCleared();
        view = window.FEABeams ? FEABeams.view() : null;
        build = view && FEABeams.build ? FEABeams.build() : null;
        if (!view || !build) { view = null; build = null; return; }
        if (typeof FEAv4 !== 'undefined' && FEAv4.BPRP_F32) BP = FEAv4.BPRP_F32;
        var props = view.beamProps, nE = view.header.nElements;
        if (props && props.length >= nE * BP) masks = new Uint32Array(props.buffer, props.byteOffset, nE * BP);
        relElems = [];
        for (var e = 0; e < nE && masks; e++) if (decode(masks[e * BP + 7])) relElems.push(e);
        if (relElems.length) {
            var REC = view.elemRecordU32, elems = view.elems;
            nodeEnds = new Array(view.nodes.length / 3);
            for (var e2 = 0; e2 < nE; e2++) for (var k = 0; k < 2; k++) {
                var n = elems[e2 * REC + 1 + k];
                (nodeEnds[n] || (nodeEnds[n] = [])).push(e2, k);
            }
            var lo = [Infinity, Infinity, Infinity], hi = [-Infinity, -Infinity, -Infinity], nd = view.nodes;
            for (var i = 0; i < nd.length; i += 3) for (var a = 0; a < 3; a++) {
                if (nd[i + a] < lo[a]) lo[a] = nd[i + a];
                if (nd[i + a] > hi[a]) hi[a] = nd[i + a];
            }
            span = Math.max(hi[0] - lo[0], hi[1] - lo[1], hi[2] - lo[2]);
            if (!(span > 0) || !isFinite(span)) span = 1;
        }
        update();
    }

    function onModelCleared() {
        disposeMeshes();
        view = null; build = null; masks = null; relElems = []; nodeEnds = null; span = 1;
        nodeKeep = null; lastSig = ''; shown = []; radCache = {};
        if (elSection) elSection.style.display = 'none';
        clearReadout();
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

    // ---- geometry helpers ----------------------------------------------------
    var radCache = {};
    // radius of a section's drawn outline about the member axis (0 = unknown)
    function outlineRadius(si) {
        if (radCache[si] !== undefined) return radCache[si];
        var sec = view.sections ? view.sections[si] : null, r = 0;
        if (sec && typeof FEABeamGeometry !== 'undefined' && FEABeamGeometry.outline) {
            try {
                FEABeamGeometry.outline(sec).forEach(function (q) {
                    var d = Math.sqrt(q[0] * q[0] + q[1] * q[1]);
                    if (d > r && isFinite(d)) r = d;
                });
            } catch (e) { r = 0; }
        }
        radCache[si] = r;
        return r;
    }
    function endRadius(e, k) { return outlineRadius(k ? build.sectionOfB[e] : build.sectionOf[e]); }
    function v3(arr, o) { return new THREE.Vector3(arr[o], arr[o + 1], arr[o + 2]); }
    // unit vector from end k of beam e along it (into the member)
    function inward(e, k) {
        var REC = view.elemRecordU32, nd = view.nodes;
        var a = view.elems[e * REC + 1], b = view.elems[e * REC + 2];
        var from = k ? b : a, to = k ? a : b;
        var d = new THREE.Vector3(nd[to * 3] - nd[from * 3], nd[to * 3 + 1] - nd[from * 3 + 1], nd[to * 3 + 2] - nd[from * 3 + 2]);
        var l = d.length();
        return l > 1e-12 ? d.multiplyScalar(1 / l) : null;
    }
    // how far in from the node a symbol on (e, k) must start to clear the other members there
    function clearance(e, k, dIn) {
        var n = view.elems[e * view.elemRecordU32 + 1 + k], l = nodeEnds ? nodeEnds[n] : null, s = 0;
        if (!l) return 0;
        for (var i = 0; i < l.length; i += 2) {
            if (l[i] === e) continue;
            var r = endRadius(l[i], l[i + 1]), t = inward(l[i], l[i + 1]);
            var c = t ? dIn.dot(t) : 0;
            var need = c <= 0.2 ? r : r / Math.max(Math.sqrt(Math.max(0, 1 - c * c)), 0.3);
            if (need > s) s = need;
        }
        return s;
    }
    var Yax = new THREE.Vector3(0, 1, 0), Zax = new THREE.Vector3(0, 0, 1);
    function quatTo(from, to) { return new THREE.Quaternion().setFromUnitVectors(from, to); }

    // Parts of beam e: [{prim, pos, quat, scale, dof, partial, end}].
    function symbolParts(e) {
        var d = decode(maskOf(e));
        if (!d || !build) return [];
        var out = [];
        var X = v3(build.frames, e * 9), Yl = v3(build.frames, e * 9 + 3), Zl = v3(build.frames, e * 9 + 6);
        var A = v3(build.axisA, e * 3), B = v3(build.axisB, e * 3);
        var len = A.distanceTo(B);
        for (var k = 0; k < 2; k++) {
            var rel = k ? d.b : d.a, part = k ? d.pb : d.pa;
            if (!rel) continue;
            var E = k ? B : A, dIn = k ? X.clone().negate() : X.clone();
            var Rb = endRadius(e, k);
            var u = Math.max(Rb, span * MIN_UNIT_FRACTION) * size;
            if (!(Rb > 0)) Rb = 0.5 * u;
            var has = function (i) { return !!(rel & (1 << i)); };
            var isPart = function (i) { return !!(part & (1 << i)); };
            // groups along the member, from the end: rotation, shear, axial
            var rho = Rb + 0.6 * u;                     // bending ring radius
            var groups = [];
            if (has(M2) || has(M3) || has(T)) groups.push({ g: 'rot', h: (has(M2) || has(M3)) ? rho : 0.2 * u });
            if (has(V2) || has(V3)) groups.push({ g: 'shear', h: Rb + 0.2 * u });
            if (has(P)) groups.push({ g: 'axial', h: 0.2 * u + 0.1 * u });     // half the gap + a plate
            var gapBetween = 0.25 * u, total = 0;
            groups.forEach(function (g, i) { total += 2 * g.h + (i ? gapBetween : 0); });
            var at = Math.max(clearance(e, k, dIn), 0.1 * u);
            if (len > 0 && at + total > 0.48 * len) at = Math.max(0.02 * len, 0.48 * len - total);
            groups.forEach(function (g) {
                var c = E.clone().addScaledVector(dIn, at + g.h);
                at += 2 * g.h + gapBetween;
                var put = function (prim, pos, quat, sx, sy, sz, dof) {
                    out.push({ prim: prim, pos: pos, quat: quat, scale: new THREE.Vector3(sx, sy, sz), dof: dof, partial: isPart(dof), end: k });
                };
                if (g.g === 'rot') {
                    var both = has(M2) && has(M3);
                    if (has(M3)) {                               // turns in x-z: ring normal y, pin along y (local 3)
                        put('ring', c.clone(), quatTo(Zax, Yl), rho, rho, rho, M3);
                        if (!both) put('rod', c.clone(), quatTo(Yax, Yl), 0.09 * u, 2 * (rho + 0.15 * u), 0.09 * u, M3);
                    }
                    if (has(M2)) {                               // turns in x-y: ring normal z, pin along z (local 2)
                        put('ring', c.clone(), quatTo(Zax, Zl), rho, rho, rho, M2);
                        if (!both) put('rod', c.clone(), quatTo(Yax, Zl), 0.09 * u, 2 * (rho + 0.15 * u), 0.09 * u, M2);
                    }
                    if (has(T)) {                                // a collar round the member
                        var rt = Rb + 0.25 * u;
                        put('ring', c.clone(), quatTo(Zax, X), rt, rt, rt, T);
                    }
                } else if (g.g === 'shear') {
                    var q = g.h, t = 0.07 * u;
                    // hollow square in the plane of x and the slide direction s
                    var square = function (s, dof) {
                        [-1, 1].forEach(function (sg) {
                            put('box', c.clone().addScaledVector(s, sg * q), quatTo(Yax, X), t, 2 * q + t, t, dof);   // along the member
                            put('box', c.clone().addScaledVector(X, sg * q), quatTo(Yax, s), t, 2 * q + t, t, dof);   // across it
                        });
                    };
                    if (has(V2)) square(Zl, V2);                 // slides along local 2 = z
                    if (has(V3)) square(Yl, V3);                 // slides along local 3 = -y
                } else {                                         // a slot: two square plates across the member, a gap between
                    var sd = 2 * (Rb + 0.25 * u), th = 0.1 * u, gap = 0.4 * u;
                    var qp = new THREE.Quaternion().setFromRotationMatrix(new THREE.Matrix4().makeBasis(Zl, X, Yl));
                    [-1, 1].forEach(function (sg) {
                        put('box', c.clone().addScaledVector(dIn, sg * (gap / 2 + th / 2)), qp, sd, th, sd, P);
                    });
                }
            });
        }
        return out;
    }

    function primGeometry(p) {
        switch (p) {
            case 'ring': return new THREE.TorusGeometry(1, 0.09, 8, 36);
            case 'rod':  return new THREE.CylinderGeometry(1, 1, 1, 8);
            default:     return new THREE.BoxGeometry(1, 1, 1);
        }
    }

    // Headlight shading on the instance colour (as supports.js).
    function makeMaterial() {
        return new THREE.ShaderMaterial({
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
                'varying vec3 vCol;',
                'varying vec3 vN;',
                'varying vec3 vV;',
                'void main() {',
                '  float l = 0.45 + 0.55 * abs(dot(normalize(vN), normalize(vV)));',
                '  gl_FragColor = vec4(vCol * l, 1.0);',
                '}'
            ].join('\n')
        });
    }

    // ---- visibility ----------------------------------------------------------
    function beamVisible(e) {
        var m = window.FEABeams && FEABeams.mesh ? FEABeams.mesh() : null;
        if (m && !m.visible) return false;
        var g = build && build.geometry ? build.geometry.getAttribute('elemVis') : null;
        return !g || g.array[build.vertStart[e]] > 0.5;
    }

    function rebuild() {
        disposeMeshes();
        shown = [];
        if (!view || !relElems.length) { syncPanel(); return; }
        var parts = {};
        PRIMS.forEach(function (p) { parts[p] = []; });
        if (visible) relElems.forEach(function (e) {
            if (!beamVisible(e)) return;
            shown.push(e);
            symbolParts(e).forEach(function (pt) { pt.owner = e; parts[pt.prim].push(pt); });
        });
        if (!material) material = makeMaterial();
        root = new THREE.Group();
        meshes = {};
        var m4 = new THREE.Matrix4(), col = new THREE.Color();
        PRIMS.forEach(function (p) {
            var list = parts[p];
            if (!list.length) return;
            var mesh = new THREE.InstancedMesh(primGeometry(p), material, list.length);
            var owner = new Int32Array(list.length), dofs = [], partial = [];
            list.forEach(function (pt, k) {
                mesh.setMatrixAt(k, m4.compose(pt.pos, pt.quat, pt.scale));
                var rgb = pt.partial ? RGB_PARTIAL : RGB_FREE;
                mesh.setColorAt(k, col.setRGB(rgb[0] / 255, rgb[1] / 255, rgb[2] / 255));
                owner[k] = pt.owner;
                dofs.push(NAMES[pt.dof]);
                partial.push(pt.partial);
            });
            mesh.instanceMatrix.needsUpdate = true;
            if (mesh.instanceColor) mesh.instanceColor.needsUpdate = true;
            mesh.frustumCulled = false;
            meshes[p] = { mesh: mesh, owner: owner, dof: dofs, partial: partial };
            root.add(mesh);
        });
        if (typeof scene !== 'undefined' && scene) scene.add(root);
        syncPanel();
    }

    function update() {
        var sig = visible + '|' + size + '|';
        for (var i = 0; i < relElems.length; i++) sig += beamVisible(relElems[i]) ? '1' : '0';
        if (sig !== lastSig || (!meshes && relElems.length)) { lastSig = sig; rebuild(); }
        if (typeof needsRender !== 'undefined') needsRender = true;
    }

    // Section-cut hook (after FEABeams.writeVis has written the beams' elemVis).
    function writeVis(keep) { nodeKeep = keep || null; update(); }

    // ---- panel / readout -------------------------------------------------------
    function syncPanel() {
        if (!elSection) return;
        elSection.style.display = relElems.length ? '' : 'none';
        if (elCount) elCount.textContent = shown.length + ' / ' + relElems.length + ' members shown';
    }
    function fillReadout(e) {
        if (!elRoRow || !elRo) return;
        var t = text(e);
        if (!t) { elRoRow.style.display = 'none'; return; }
        elRo.textContent = t;
        elRoRow.style.display = '';
    }
    function clearReadout() { if (elRoRow) elRoRow.style.display = 'none'; }

    // ---- UI ------------------------------------------------------------------
    if (elShow) elShow.addEventListener('change', function () {
        visible = this.checked;
        try { localStorage.setItem('pluto.relShow', visible ? '1' : '0'); } catch (e) {}
        update();
    });
    if (elSize) elSize.addEventListener('input', function () {
        var v = parseFloat(this.value);
        if (!(v > 0)) return;
        size = v;
        try { localStorage.setItem('pluto.relSize', String(v)); } catch (e) {}
        update();
    });
    // Beams > Show: viewerBeams.js (loaded first) sets the mesh visibility, then this follows it.
    if (elBeamShow) elBeamShow.addEventListener('change', function () { update(); });

    return {
        onModelLoaded: onModelLoaded,
        onModelCleared: onModelCleared,
        writeVis: writeVis,
        update: update,
        fillReadout: fillReadout,
        clearReadout: clearReadout,
        // "I: M2 M3; J: fixed" for beam index e ('' when nothing is released); ~ = partly fixed
        text: text,
        // {a, b, pa, pb} 6-bit masks (bit i = P V2 V3 T M2 M3) for beam index e, or null
        decode: function (e) { return decode(maskOf(e)); },
        count: function () { return shown.length; },
        // test hooks (viewer/tests/test_releases.js)
        _decode: decode,
        _textOfMask: textOfMask,
        _parts: symbolParts,
        _meshes: function () { return meshes; },
        _elems: function () { return relElems; },
        _set: function (o) {
            if (o.size !== undefined) size = o.size;
            if (o.visible !== undefined) visible = o.visible;
            update();
        }
    };
})();
