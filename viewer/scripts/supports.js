// ================================================================
// supports.js  --  support symbols from the sidecar `supports` section.
// Spec: vault/format/features-sidecar.md (supports), decision
// vault/decisions/2026-10-03-imported-restraints.md.
//
// Each sidecar support item ({id, name, nodeIds, dof, tags, ...}) draws a
// symbol at each of its nodes in CAESAR II's language (2026-10-04; the first
// set of cubes, collars, hoops and plates crowded busy nodes): a plate for an
// anchor, slim arrows pointing at the pipe along the line of action for the rest:
//   anchor        a square plate through the pipe, square to it (an imposed
//                 displacement too: it sits on an anchor point)
//   translation   two arrows, one from each side
//   oneway        one arrow, from the side it pushes from (+Y: below the pipe)
//   guide         two arrows across the pipe (four on a riser)
//   limit         two arrows along the pipe pointing at the node; an axial
//                 translation the same, and an axial one-way one of them --
//                 along the pipe they would hide inside it, so they run on its
//                 top surface
//   rotation      a double-headed arrow (moment vector) at 3/4 size on one side,
//                 beyond where a translation arrow on that line ends
//   hanger        one arrow below the pipe, holding it up
//   other         a small ball on top of the pipe
// A restraint with a gap stops short of the pipe by a visible space. Items
// written by the CAESAR export carry `restraints` (kind, direction, axis, gap,
// cnode ...); an item with only `dof` (a hand-made one) draws from its dof letters.
//
// Sizes follow the pipe: the outer radius of the drawn beams at the node
// (a flange or valve at the node counts), with a floor from the model span,
// times the Size slider; the arrow tips stay on the pipe surface. One
// InstancedMesh per primitive (cone, cylinder, box, ball), headlight shaded,
// frustumCulled = false.
//
// Colour (Supports panel): the support's node group (the LAST enabled node
// group listing the node, as for the markers -- the CAESAR export writes one
// group per restraint combination), the restraint kind, or a restraint-load
// component (kind "restraint" on the beam domain, a node total on every beam
// end at the node) in the current load case, through the active colormap
// over the visible supports' own range. Visibility: the item's `hidden`
// flag, the node groups (a node listed only by unticked groups hides), the
// section-cut isolate / crop box, and the panel's Show.
//
// Deformed shape: the symbols stay where the supports are; a line runs from
// each support to its displaced node (same scale and animation as the beams),
// so lift-off and gap closure show. CNODE restraints get a thin line to the
// connected node when the two are apart.
//
// Hooks (all guarded by window.FEASupports): viewer.js load / clear,
// syncAuxLayers -> recolor, setDispUniforms -> setDispScale, refreshDispVecs
// -> refreshDisp, feaPick / showReadout / pin; features.js sync ->
// onFeatures; sectionCut.js writeVis.
// ================================================================

var FEASupports = (function () {

    var view = null;          // beam domain view (primary file)
    var nodeEnds = null;      // node index -> [elem, end, elem, end, ...] (beam ends at the node)
    var nodeR = null;         // node index -> outer radius of the drawn beams there (0 = none)
    var span = 1;             // model span (largest bbox side)
    var entries = [];         // one per (item, node): {item, itemIdx, ni, rs: [restraint...], name}
    var itemsRef = null;      // the envelope's items array the entries came from
    var nodeKeep = null;      // section-cut isolate mask (null = all)
    var visible = true;       // panel Show
    var size = 1;             // panel Size
    var colorMode = 'group';  // 'group' | 'kind' | 'c<component index>'
    var lastSig = '';         // visibility signature of the built instances
    var meshes = null;        // {prim: {mesh, owner: Int32Array, kind: []}}
    var root = null;          // THREE.Group holding the meshes
    var lines = null;         // deflection lines (LineSegments, edge shader)
    var cnodeLines = null;    // CNODE lines
    var lineOwner = [];       // deflection line i -> entry index
    var material = null;
    var loadRange = null;     // {min, max, name, unit} of the load colouring, or null
    var shown = [];           // entry indices drawn

    var PRIMS = ['cone', 'cyl', 'box', 'ball'];
    var KIND_RGB = {
        anchor: [229, 57, 53], translation: [3, 155, 229], oneway: [124, 179, 66], guide: [0, 137, 123],
        limit: [251, 140, 0], rotation: [142, 36, 170], hanger: [253, 216, 53], imposed: [216, 27, 96],
        other: [176, 176, 186]
    };
    var NEUTRAL = [0.35, 0.35, 0.38];
    var MIN_UNIT_FRACTION = 0.0025;   // symbol unit floor = span x this (tiny pipes in a big model)

    var elSection = document.getElementById('supSection');
    var elShow    = document.getElementById('supShow');
    var elSize    = document.getElementById('supSize');
    var elColor   = document.getElementById('supColor');
    var elCount   = document.getElementById('supCount');
    var elRange   = document.getElementById('supRange');

    try {
        var sz = parseFloat(localStorage.getItem('pluto.supSize'));
        if (sz > 0) size = sz;
    } catch (e0) {}
    if (elSize) elSize.value = String(size);

    // ---- vectors ---------------------------------------------------------
    function v3(a) { return new THREE.Vector3(a[0], a[1], a[2]); }
    function unit(a) {
        if (!a || a.length < 3) return null;
        var v = v3(a);
        var l = v.length();
        return (l > 1e-12 && isFinite(l)) ? v.multiplyScalar(1 / l) : null;
    }
    function perp(a) {
        var t = Math.abs(a.x) < 0.9 ? new THREE.Vector3(1, 0, 0) : new THREE.Vector3(0, 1, 0);
        return t.cross(a).normalize();
    }
    var Y = new THREE.Vector3(0, 1, 0), Z = new THREE.Vector3(0, 0, 1);
    function quatFrom(from, to) { return new THREE.Quaternion().setFromUnitVectors(from, to); }
    // Rotation taking the primitive's x / y / z to n / (ax x n) / ax, with n made square
    // to ax first (falls back to any normal of ax when the two are parallel).
    function quatFrame(n, ax) {
        var h = new THREE.Vector3().crossVectors(ax, n);
        if (h.lengthSq() < 1e-12) h = perp(ax); else h.normalize();
        var x = new THREE.Vector3().crossVectors(h, ax).normalize();
        return new THREE.Quaternion().setFromRotationMatrix(new THREE.Matrix4().makeBasis(x, h, ax));
    }

    // ---- model -----------------------------------------------------------
    function onModelLoaded() {
        onModelCleared();
        view = window.FEABeams ? FEABeams.view() : null;
        if (!view) return;
        var nNodes = view.nodes.length / 3;
        var REC = view.elemRecordU32, elems = view.elems, nE = view.header.nElements;
        var lists = new Array(nNodes);
        for (var e = 0; e < nE; e++) {
            for (var k = 0; k < 2; k++) {
                var n = elems[e * REC + 1 + k];
                (lists[n] || (lists[n] = [])).push(e, k);
            }
        }
        nodeEnds = lists;
        // outer radius of what is drawn at each node: the largest section at an end there
        var build = FEABeams.build(), secs = view.sections || [];
        nodeR = new Float64Array(nNodes);
        for (var e2 = 0; e2 < nE; e2++) {
            for (var k2 = 0; k2 < 2; k2++) {
                var si = k2 === 0 ? build.sectionOf[e2] : build.sectionOfB[e2];
                var r = sectionRadius(secs[si]);
                var n2 = elems[e2 * REC + 1 + k2];
                if (r > nodeR[n2]) nodeR[n2] = r;
            }
        }
        var lo = [Infinity, Infinity, Infinity], hi = [-Infinity, -Infinity, -Infinity], nd = view.nodes;
        for (var i = 0; i < nd.length; i += 3) for (var a = 0; a < 3; a++) {
            if (nd[i + a] < lo[a]) lo[a] = nd[i + a];
            if (nd[i + a] > hi[a]) hi[a] = nd[i + a];
        }
        span = Math.max(hi[0] - lo[0], hi[1] - lo[1], hi[2] - lo[2]);
        if (!(span > 0) || !isFinite(span)) span = 1;
        populateColorSelect();
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
        view = null; nodeEnds = null; nodeR = null; span = 1;
        entries = []; itemsRef = null; nodeKeep = null; lastSig = ''; loadRange = null; shown = [];
        if (elSection) elSection.style.display = 'none';
    }

    function disposeMeshes() {
        if (root && typeof scene !== 'undefined' && scene) scene.remove(root);
        if (meshes) PRIMS.forEach(function (p) {
            var m = meshes[p] && meshes[p].mesh;
            if (!m) return;
            m.geometry.dispose();               // own geometry; the material is shared and kept
            if (m.dispose) m.dispose();
        });
        if (lines) { lines.geometry.dispose(); lines.material.dispose(); }
        if (cnodeLines) { cnodeLines.geometry.dispose(); cnodeLines.material.dispose(); }
        meshes = null; root = null; lines = null; cnodeLines = null; lineOwner = [];
    }

    // ---- sidecar items -> entries -----------------------------------------
    var DOF_AXIS = { tx: [1, 0, 0], ty: [0, 1, 0], tz: [0, 0, 1], rx: [1, 0, 0], ry: [0, 1, 0], rz: [0, 0, 1] };

    // The restraints an item draws: its `restraints` list, or ones made from its dof letters.
    function restraintsOf(item) {
        if (Array.isArray(item.restraints) && item.restraints.length) {
            return item.restraints.map(function (r) {
                return { type: String(r.type || r.kind || '?'), kind: String(r.kind || 'other'),
                         dir: unit(r.direction), axis: unit(r.axis), gap: +r.gap || 0, vertical: !!r.vertical,
                         friction: +r.friction || 0, stiffness: +r.stiffness || 0, cnode: r.cnode, raw: r };
            });
        }
        var dof = item.dof || {}, keys = Object.keys(dof).filter(function (k) { return DOF_AXIS[k]; });
        if (keys.length === 6 && keys.every(function (k) { return dof[k] === 'fixed'; }))
            return [{ type: 'ANC', kind: 'anchor', dir: null, axis: null, gap: 0 }];
        return keys.map(function (k) {
            var v = String(dof[k]), d = unit(DOF_AXIS[k]);
            if (k.charAt(0) === 'r') return { type: k.toUpperCase(), kind: 'rotation', dir: d, axis: null, gap: v === 'gap' ? 1 : 0 };
            if (v === '+' || v === '-') {
                if (v === '-') d.negate();
                return { type: v + k.charAt(1).toUpperCase(), kind: 'oneway', dir: d, axis: null, gap: 0 };
            }
            return { type: k.charAt(1).toUpperCase(), kind: 'translation', dir: d, axis: null, gap: v === 'gap' ? 1 : 0 };
        });
    }

    function parseItems(items) {
        entries = [];
        itemsRef = items;
        if (!view || !items) return;
        var nmap = new Map(), ids = view.nodeIds;
        for (var i = 0; i < ids.length; i++) nmap.set(ids[i], i);
        items.forEach(function (item, idx) {
            if (!item || !Array.isArray(item.nodeIds)) return;
            var rs = restraintsOf(item);
            item.nodeIds.forEach(function (id) {
                var ni = nmap.get(id);
                if (ni === undefined) return;
                entries.push({ item: item, itemIdx: idx, ni: ni, rs: rs, name: item.name || ('support ' + (idx + 1)) });
            });
        });
    }

    function envelopeItems() {
        var env = window.FEAFeatures ? FEAFeatures.envelope() : null;
        var s = env && env.supports;
        return (s && Array.isArray(s.items)) ? s.items : null;
    }

    // ---- visibility --------------------------------------------------------
    function nodeGroup(ni) {
        return (window.FEAFeatures && FEAFeatures.nodeGroupAt) ? FEAFeatures.nodeGroupAt(ni) : null;
    }
    function entryVisible(en) {
        if (en.item.hidden === true) return false;
        if (nodeKeep && !nodeKeep[en.ni]) return false;
        var g = nodeGroup(en.ni);
        return !(g && g.listed && !g.name);       // listed only by unticked groups
    }

    // ---- symbols -----------------------------------------------------------
    // Pipe axis at a node: the restraint's own, else the first beam there.
    function nodeAxis(ni) {
        var l = nodeEnds && nodeEnds[ni];
        if (!l) return null;
        var REC = view.elemRecordU32, e = l[0], nd = view.nodes;
        var a = view.elems[e * REC + 1], b = view.elems[e * REC + 2];
        return unit([nd[b * 3] - nd[a * 3], nd[b * 3 + 1] - nd[a * 3 + 1], nd[b * 3 + 2] - nd[a * 3 + 2]]);
    }

    // The model's up direction: the file's META upAxis, else the view's.
    function modelUp() {
        var a = view && view.unified ? view.unified.upAxis : null;
        if (a === 'Z') return Z;
        if (a === 'Y') return Y;
        return (typeof zUp !== 'undefined' && zUp) ? Z : Y;
    }

    // Parts of one entry: [{prim, pos, quat, scale: Vector3, kind}]. CAESAR's symbol language
    // (2026-10-04, after the first set -- cubes, collars, hoops, plates -- crowded busy nodes):
    // an anchor is a square plate through the pipe, every other restraint is a slim arrow
    // pointing at the pipe along the line it acts on.
    function symbolParts(en) {
        var nd = view.nodes, ni = en.ni;
        var P = new THREE.Vector3(nd[ni * 3], nd[ni * 3 + 1], nd[ni * 3 + 2]);
        var R = nodeR && nodeR[ni] > 0 ? nodeR[ni] : span * MIN_UNIT_FRACTION;
        var u = Math.max(R, span * MIN_UNIT_FRACTION) * size;
        var L = 2.4 * u;                       // arrow length
        var up = modelUp();
        var out = [];
        function put(prim, pos, quat, sx, sy, sz, kind) {
            out.push({ prim: prim, pos: pos, quat: quat || new THREE.Quaternion(), scale: new THREE.Vector3(sx, sy, sz), kind: kind });
        }
        // An arrow along unit a (pointing at the pipe), tip at `tip`; two heads for a rotation
        // (the moment-vector convention), drawn at s x the size.
        function arrow(tip, a, kind, heads, s) {
            s = s || 1;
            var len = L * s, hl = 0.7 * u * s, hr = 0.3 * u * s, sr = 0.1 * u * s, q = quatFrom(Y, a);
            var nh = heads || 1, back = 0;
            for (var k = 0; k < nh; k++) {
                put('cone', tip.clone().addScaledVector(a, -hl / 2 - back), q, hr, hl, hr, kind);
                back += 0.8 * hl;
            }
            var headLen = hl + (nh - 1) * 0.8 * hl;
            put('cyl', tip.clone().addScaledVector(a, -headLen - (len - headLen) / 2), q, sr, len - headLen, sr, kind);
        }
        // The side of the pipe the arrows along it sit on: up, square to the pipe (any normal on a riser).
        function topOf(ax) {
            var t = up.clone().addScaledVector(ax, -up.dot(ax));
            return t.lengthSq() > 1e-6 ? t.normalize() : perp(ax);
        }
        // Arrows ALONG the pipe (limit stops, axial restraints) would hide inside it, so they run on its
        // top surface and point back at the node: sides +1 / -1 along ax, tips `from` off the node.
        function alongPipe(ax, sides, kind, from, heads, s) {
            var base = P.clone().addScaledVector(topOf(ax), R + 0.32 * u * (s || 1));   // heads rest on the pipe
            sides.forEach(function (sgn) {
                arrow(base.clone().addScaledVector(ax, sgn * from), ax.clone().multiplyScalar(-sgn), kind, heads, s);
            });
        }
        // Arrows ACROSS the pipe along d, tips on its surface (off it by the gap): both sides, or the
        // one side a one-way pushes from.
        function across(d, both, kind, gapOff) {
            arrow(P.clone().addScaledVector(d, -(R + gapOff)), d, kind);
            if (both) arrow(P.clone().addScaledVector(d, R + gapOff), d.clone().negate(), kind);
        }
        function ball(kind) {
            var t = up.clone();
            put('ball', P.clone().addScaledVector(t, R + 0.4 * u), null, 0.4 * u, 0.4 * u, 0.4 * u, kind);
        }
        en.rs.forEach(function (r) {
            var ax = r.axis || nodeAxis(ni) || perp(r.dir || up);
            var d = r.dir;
            var gapOff = r.gap > 0 ? 0.35 * u : 0;
            var axial = !!d && Math.abs(d.dot(ax)) > 0.9;
            switch (r.kind) {
                case 'anchor':
                case 'imposed': {                   // an imposed displacement sits on an anchor point
                    var side = 2 * R + 1.6 * u;
                    put('box', P.clone(), quatFrame(perp(ax), ax), side, side, 0.14 * u, r.kind);
                    break;
                }
                case 'translation':
                    if (!d) { ball('other'); break; }
                    if (axial) alongPipe(ax, [1, -1], r.kind, 0.45 * u + gapOff);
                    else across(d, true, r.kind, gapOff);
                    break;
                case 'oneway':
                    if (!d) { ball('other'); break; }
                    if (axial) alongPipe(ax, [d.dot(ax) > 0 ? -1 : 1], r.kind, 0.45 * u + gapOff);
                    else across(d, false, r.kind, gapOff);
                    break;
                case 'guide':
                    if (r.vertical || !d) {               // a riser: both horizontal directions
                        var g1 = perp(ax), g2 = new THREE.Vector3().crossVectors(ax, g1);
                        across(g1, true, r.kind, gapOff);
                        across(g2, true, r.kind, gapOff);
                    } else across(d, true, r.kind, gapOff);
                    break;
                case 'limit':
                    alongPipe(ax, [1, -1], r.kind, 0.45 * u + gapOff);
                    break;
                case 'rotation': {
                    // a double-headed arrow on one side, beyond where a translation arrow would end
                    var a = d || ax;
                    if (Math.abs(a.dot(ax)) > 0.9) alongPipe(ax, [a.dot(ax) > 0 ? 1 : -1], r.kind, 0.45 * u + L + 0.3 * u, 2, 0.75);
                    else arrow(P.clone().addScaledVector(a, R + L + 0.3 * u), a.clone().negate(), r.kind, 2, 0.75);
                    break;
                }
                case 'hanger':                            // an arrow below the pipe, holding it up
                    across(d || up, false, r.kind, 0);
                    break;
                default:
                    ball('other');
            }
        });
        return out;
    }

    function primGeometry(p) {
        switch (p) {
            case 'cone':   return new THREE.ConeGeometry(1, 1, 14);
            case 'cyl':    return new THREE.CylinderGeometry(1, 1, 1, 10);
            case 'box':    return new THREE.BoxGeometry(1, 1, 1);
            default:       return new THREE.SphereGeometry(1, 14, 8);
        }
    }

    // Headlight shading on the instance colour: the symbols read as solids
    // whatever the scene lights are (the field meshes are unlit).
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

    // (Re)build the instances when what is drawn changed; colours follow in recolor().
    function rebuild() {
        disposeMeshes();
        shown = [];
        if (!view || !entries.length) { syncPanel(); return; }
        var parts = {};
        PRIMS.forEach(function (p) { parts[p] = []; });
        if (visible) {
            entries.forEach(function (en, i) {
                if (!entryVisible(en)) return;
                shown.push(i);
                symbolParts(en).forEach(function (pt) { pt.owner = i; parts[pt.prim].push(pt); });
            });
        }
        if (!material) material = makeMaterial();
        root = new THREE.Group();
        meshes = {};
        var m4 = new THREE.Matrix4();
        PRIMS.forEach(function (p) {
            var list = parts[p];
            if (!list.length) return;
            var mesh = new THREE.InstancedMesh(primGeometry(p), material, list.length);
            var owner = new Int32Array(list.length), kinds = [];
            list.forEach(function (pt, k) {
                mesh.setMatrixAt(k, m4.compose(pt.pos, pt.quat, pt.scale));
                mesh.setColorAt(k, new THREE.Color(1, 1, 1));
                owner[k] = pt.owner;
                kinds.push(pt.kind);
            });
            mesh.instanceMatrix.needsUpdate = true;
            mesh.frustumCulled = false;
            meshes[p] = { mesh: mesh, owner: owner, kind: kinds };
            root.add(mesh);
        });
        buildLines();
        if (typeof scene !== 'undefined' && scene) scene.add(root);
        syncPanel();
    }

    // Deflection lines (one per drawn support: node -> displaced node, scaled in the
    // shader) and CNODE lines (static, only where the connected node is apart).
    function buildLines() {
        var nd = view.nodes, pos = [], dv = [];
        lineOwner = [];
        shown.forEach(function (i) {
            var ni = entries[i].ni;
            for (var t = 0; t < 2; t++) pos.push(nd[ni * 3], nd[ni * 3 + 1], nd[ni * 3 + 2]);
            dv.push(0, 0, 0, 0, 0, 0);
            lineOwner.push(i);
        });
        if (pos.length) {
            var g = new THREE.BufferGeometry();
            g.setAttribute('position', new THREE.BufferAttribute(new Float32Array(pos), 3));
            g.setAttribute('dispVec', new THREE.BufferAttribute(new Float32Array(dv), 3));
            var vis = new Float32Array(pos.length / 3); vis.fill(1);
            g.setAttribute('elemVis', new THREE.BufferAttribute(vis, 1));
            lines = new THREE.LineSegments(g, new THREE.ShaderMaterial({
                uniforms: { uColor: { value: new THREE.Vector3(1.0, 0.82, 0.30) }, uOpacity: { value: 0.95 }, dispScale: { value: 0 } },
                vertexShader: FEAShaders.edgeVertex, fragmentShader: FEAShaders.edgeFragment, transparent: true
            }));
            lines.frustumCulled = false;
            lines.visible = false;
            root.add(lines);
            refreshDisp();
        }
        var cpos = [], nmap = null;
        shown.forEach(function (i) {
            var en = entries[i], seen = {};
            en.rs.forEach(function (r) {
                var c = +r.cnode;
                if (!(c > 0) || seen[c]) return;
                seen[c] = 1;
                if (!nmap) { nmap = new Map(); for (var k = 0; k < view.nodeIds.length; k++) nmap.set(view.nodeIds[k], k); }
                var cj = nmap.get(c);
                if (cj === undefined || cj === en.ni) return;
                var a = en.ni;
                var dx = nd[cj * 3] - nd[a * 3], dy = nd[cj * 3 + 1] - nd[a * 3 + 1], dz = nd[cj * 3 + 2] - nd[a * 3 + 2];
                if (Math.sqrt(dx * dx + dy * dy + dz * dz) < span * 1e-5) return;
                cpos.push(nd[a * 3], nd[a * 3 + 1], nd[a * 3 + 2], nd[cj * 3], nd[cj * 3 + 1], nd[cj * 3 + 2]);
            });
        });
        if (cpos.length) {
            var cg = new THREE.BufferGeometry();
            cg.setAttribute('position', new THREE.BufferAttribute(new Float32Array(cpos), 3));
            cnodeLines = new THREE.LineSegments(cg, new THREE.LineBasicMaterial({ color: 0x9fb4ff, transparent: true, opacity: 0.8 }));
            cnodeLines.frustumCulled = false;
            root.add(cnodeLines);
        }
    }

    // ---- values from the beam plane ---------------------------------------
    function lcData() { return window.FEABeams && FEABeams.lcData ? FEABeams.lcData() : null; }
    // Value of component c at node ni: the first beam end there with a finite value.
    function nodeValue(data, ni, c) {
        var l = nodeEnds && nodeEnds[ni];
        if (!data || !l || c < 0) return NaN;
        var cc = view.header.cornerComponents, ms = view.header.maxCorners;
        for (var i = 0; i < l.length; i += 2) {
            var v = data[l[i] * ms * cc + (ms > 1 ? l[i + 1] : 0) * cc + c];
            if (v === v) return v;
        }
        return NaN;
    }
    function nodeEnd(ni) {
        var l = nodeEnds && nodeEnds[ni];
        return l ? { elem: l[0], end: l[1] } : null;
    }
    function restraintComps() {
        var out = [];
        if (view) view.meta.components.forEach(function (c, i) { if (c.kind === 'restraint') out.push(i); });
        return out;
    }
    function neutralView() {
        return (typeof currentEnvelope !== 'undefined' && !!currentEnvelope) ||
               (typeof inStrMode === 'function' && inStrMode());
    }

    // ---- colour --------------------------------------------------------------
    function recolor() {
        loadRange = null;
        if (!meshes) { syncRange(); return; }
        var comp = colorMode.charAt(0) === 'c' ? parseInt(colorMode.slice(1), 10) : -1;
        var byLoad = comp >= 0 && view && comp < view.meta.components.length;
        var vals = null, lo = Infinity, hi = -Infinity;
        var data = byLoad && !neutralView() ? lcData() : null;
        var abs = typeof absValue !== 'undefined' && absValue;
        if (byLoad) {
            vals = {};
            shown.forEach(function (i) {
                var v = nodeValue(data, entries[i].ni, comp);
                if (abs && v === v) v = Math.abs(v);
                vals[i] = v;
                if (v === v) { if (v < lo) lo = v; if (v > hi) hi = v; }
            });
            var c = view.meta.components[comp];
            if (lo === Infinity) loadRange = { empty: true, name: c.name, unit: c.unit };
            else {
                if (lo === hi) { lo -= 0.5; hi += 0.5; }
                loadRange = { min: lo, max: hi, name: c.name, unit: c.unit, empty: false };
            }
        }
        var anchors = (typeof FEAShaders !== 'undefined' && FEAShaders.colormaps)
            ? (FEAShaders.colormaps[typeof colormapName !== 'undefined' ? colormapName : 'turbo'] || FEAShaders.colormaps.viridis) : null;
        var alarmOn = typeof alarmEnabled !== 'undefined' && alarmEnabled;
        var col = new THREE.Color();
        PRIMS.forEach(function (p) {
            var m = meshes[p];
            if (!m) return;
            for (var k = 0; k < m.owner.length; k++) {
                var en = entries[m.owner[k]], rgb;
                if (byLoad) {
                    var v = vals[m.owner[k]];
                    if (!(v === v) || !loadRange || loadRange.empty) rgb = NEUTRAL;
                    else if (alarmOn && v >= alarmThreshold) rgb = [alarmColor[0] / 255, alarmColor[1] / 255, alarmColor[2] / 255];
                    else {
                        var s = FEAShaders.sampleAnchors(anchors, (v - loadRange.min) / (loadRange.max - loadRange.min));
                        rgb = [s[0] / 255, s[1] / 255, s[2] / 255];
                    }
                } else if (colorMode === 'kind') {
                    rgb = scale255(KIND_RGB[m.kind[k]] || KIND_RGB.other);
                } else {
                    var g = nodeGroup(en.ni);
                    rgb = (g && g.rgb) ? scale255(g.rgb) : scale255(KIND_RGB[m.kind[k]] || KIND_RGB.other);
                }
                m.mesh.setColorAt(k, col.setRGB(rgb[0], rgb[1], rgb[2]));
            }
            if (m.mesh.instanceColor) m.mesh.instanceColor.needsUpdate = true;
        });
        syncRange();
        if (typeof needsRender !== 'undefined') needsRender = true;
    }
    function scale255(c) { return [c[0] / 255, c[1] / 255, c[2] / 255]; }

    // ---- deformed shape ------------------------------------------------------
    function refreshDisp() {
        if (!lines || !view) return;
        var dvec = view.meta.dispVector, data = lcData();
        var attr = lines.geometry.getAttribute('dispVec'), a = attr.array;
        for (var i = 0; i < lineOwner.length; i++) {
            var ni = entries[lineOwner[i]].ni;
            for (var q = 0; q < 3; q++) {
                var v = dvec && data ? nodeValue(data, ni, dvec[q]) : 0;
                a[i * 6 + 3 + q] = v === v ? v : 0;
            }
        }
        attr.needsUpdate = true;
    }
    function setDispScale(v) {
        if (!lines) return;
        lines.material.uniforms.dispScale.value = v;
        lines.visible = typeof deform !== 'undefined' && deform.enabled;
    }

    // ---- section-cut isolate -------------------------------------------------
    function writeVis(keep) {
        nodeKeep = keep || null;
        update();
    }

    // ---- features hook -------------------------------------------------------
    // Called by features.js sync(): a new sidecar, group ticks / colours, or a model.
    function onFeatures() {
        var items = envelopeItems();
        if (items !== itemsRef) { parseItems(items); lastSig = ''; }
        update();
    }
    function update() {
        var sig = visible + '|' + size + '|';
        for (var i = 0; i < entries.length; i++) sig += entryVisible(entries[i]) ? '1' : '0';
        if (sig !== lastSig || (!meshes && entries.length)) { lastSig = sig; rebuild(); }
        recolor();
        if (typeof deform !== 'undefined' && lines) setDispScale(typeof currentDispScale === 'function' ? currentDispScale(performance.now()) : 0);
    }

    // ---- panel ---------------------------------------------------------------
    function populateColorSelect() {
        if (!elColor) return;
        var want = colorMode;
        try { want = localStorage.getItem('pluto.supColor') || want; } catch (e) {}
        elColor.innerHTML = '';
        function opt(value, text) {
            var o = document.createElement('option');
            o.value = value; o.textContent = text;
            elColor.appendChild(o);
        }
        opt('group', 'Support group colour');
        opt('kind', 'Restraint kind');
        var pick = want === 'kind' ? 'kind' : 'group';
        restraintComps().forEach(function (i) {
            var c = view.meta.components[i];
            opt('c' + i, c.name + (c.unit ? ' [' + c.unit + ']' : ''));
            if (want === 'n:' + c.name) pick = 'c' + i;
        });
        colorMode = pick;
        elColor.value = pick;
    }
    function syncPanel() {
        if (!elSection) return;
        elSection.style.display = entries.length ? '' : 'none';
        if (elCount) {
            var items = {};
            entries.forEach(function (en) { items[en.itemIdx] = 1; });
            elCount.textContent = shown.length + ' / ' + entries.length + ' shown' +
                (Object.keys(items).length !== entries.length ? ' (' + Object.keys(items).length + ' items)' : '');
        }
    }
    function syncRange() {
        if (!elRange) return;
        if (!loadRange) {
            elRange.textContent = colorMode === 'kind' ? 'colour = restraint kind' : 'colour = the node group (Groups tab, Nodes)';
        } else if (loadRange.empty) {
            elRange.textContent = loadRange.name + ': ' + (neutralView() ? 'no load data in this view' : 'no restraint loads in this LC');
        } else {
            var f = typeof fmt === 'function' ? fmt : function (v) { return String(v); };
            elRange.textContent = loadRange.name + ': ' + f(loadRange.min, 4) + ' … ' + f(loadRange.max, 4) +
                (loadRange.unit ? ' ' + loadRange.unit : '');
        }
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
                if (!best || hits[i].distance < best.distance)
                    best = { support: true, entry: m.owner[hits[i].instanceId], point: hits[i].point, distance: hits[i].distance };
                break;
            }
        });
        return best;
    }

    // "gap 0.1, mu 0.3, k 1e5, cnode 2021" (what the item says beyond its type)
    function restraintExtras(r) {
        var raw = r.raw || {}, out = [];
        if (raw.gap !== undefined && +raw.gap > 0) out.push('gap ' + raw.gap);
        if (raw.friction) out.push('μ ' + raw.friction);
        if (raw.stiffness) out.push('k ' + raw.stiffness);
        if (raw.cnode) out.push('cnode ' + raw.cnode);
        if (raw.tag) out.push(String(raw.tag));
        return out.join(', ');
    }
    function restraintText(r) {
        var x = restraintExtras(r);
        return r.type + (x ? ' (' + x + ')' : '');
    }
    // A load for the readout and the card: whole numbers from 100 up.
    function fmtLoad(v) { return fmt(v, Math.abs(v) >= 100 ? 0 : 2); }
    // |F| and |M| (or the first two restraint components) at the node in the current LC: [{c, v}]
    function loadPair(ni) {
        var data = neutralView() ? null : lcData(), out = [];
        if (!data) return out;
        var rc = restraintComps(), comps = view.meta.components;
        var pickC = rc.filter(function (i) { return /\|[FM]\|/.test(comps[i].name); });
        if (!pickC.length) pickC = rc.slice(0, 2);
        pickC.forEach(function (i) {
            var v = nodeValue(data, ni, i);
            if (v === v) out.push({ c: comps[i], v: v });
        });
        return out;
    }

    // Value line: the colouring component, else |F| in the current LC, else the support's name.
    function fillReadout(hit) {
        var en = entries[hit.entry], ni = en.ni;
        var comp = colorMode.charAt(0) === 'c' ? parseInt(colorMode.slice(1), 10) : -1;
        var data = neutralView() ? null : lcData();
        var pair = loadPair(ni);
        var c = comp >= 0 ? view.meta.components[comp] : (pair.length ? pair[0].c : null);
        var v = comp >= 0 ? nodeValue(data, ni, comp) : (pair.length ? pair[0].v : NaN);
        var g = nodeGroup(ni);
        if (c) {
            elRoValue.textContent = v === v ? fmtLoad(v) + (c.unit ? ' ' + c.unit : '') : 'no data';
            elRoValue.className = 'ro-value' + (v === v ? '' : ' ro-nodata');
            elRoComp.textContent = c.name + (c.unit ? ' [' + c.unit + ']' : '') + ' (support)';
        } else {
            elRoValue.textContent = en.name;
            elRoValue.className = 'ro-value';
            elRoComp.textContent = data ? 'support: no restraint loads in this LC' : 'support';
        }
        elRoElem.textContent = en.name + '  (support' + (g && g.name ? ', group: ' + g.name : '') + ')';
        var nlbl = view.nodeLabels ? view.nodeLabels.get(ni) : '';
        elRoNode.textContent = view.nodeIds[ni] + (nlbl ? ' [' + nlbl + ']' : '');
        elRoCorners.textContent = en.rs.map(restraintText).join(', ');
        elRoUV.textContent = pair.length ? pair.map(function (p) {
            return p.c.name.replace(/^Restraint\s+/, '') + ' ' + fmtLoad(p.v) + (p.c.unit ? ' ' + p.c.unit : '');
        }).join(' · ') : '—';
        var nd = view.nodes;
        elRoPos.textContent = roPosText(new THREE.Vector3(nd[ni * 3], nd[ni * 3 + 1], nd[ni * 3 + 2]));
        if (elRoControllingRow) elRoControllingRow.style.display = 'none';
        lastQuery = null;
    }

    // Short load-case label for the card: "L1 (OPE)" from "L1 (OPE) W+T1+P1".
    function shortLC(name) {
        var m = /^(\S+\s*\([^)]*\))/.exec(name || '');
        return m ? m[1] : String(name || '').slice(0, 12);
    }

    // Pin card: the restraints, then the restraint loads in every load case of the
    // primary file (one record read per LC), and the displacement in the current LC.
    async function showPinCard(hit) {
        if (!view || typeof elCalcCard === 'undefined' || !elCalcCard) return;
        var en = entries[hit.entry], ni = en.ni;
        var token = ++calcCardToken;
        elCalcCard.style.display = '';
        elCalcCard.innerHTML = '';
        var head = document.createElement('div');
        head.className = 'cc-head';
        head.textContent = 'Support ' + en.name + '  (node ' + view.nodeIds[ni] + ')';
        elCalcCard.appendChild(head);
        ccSub(elCalcCard, 'Restraints');
        var t0 = document.createElement('table');
        en.rs.forEach(function (r) {
            var x = restraintExtras(r);
            ccRow(t0, r.type, r.kind + (x ? ', ' + x : ''));
        });
        elCalcCard.appendChild(t0);

        var rc = restraintComps(), comps = view.meta.components;
        var ne = nodeEnd(ni);
        if (!rc.length || !ne || !view.domain.fields || !feaSet) return;
        var rows = [];
        for (var g = 0; g < feaSet.lcs.length; g++) {
            var lc = feaSet.lcs[g];
            if (lc.file !== 0) continue;
            var rec;
            try { rec = await view.readElementRecord(lc.lc, ne.elem); }
            catch (err) { rec = null; }
            if (token !== calcCardToken) return;          // pin changed while reading
            rows.push({ g: g, name: lcName(g), rec: rec });
        }
        var cc = view.header.cornerComponents, base = (view.header.maxCorners > 1 ? ne.end : 0) * cc;
        function table(idx, title) {
            if (!idx.length) return;
            var unitTxt = comps[idx[0]].unit;
            ccSub(elCalcCard, title + (unitTxt ? ' [' + unitTxt + ']' : ''));
            var t = document.createElement('table');
            var hr = document.createElement('tr');
            ['LC'].concat(idx.map(function (i) { return comps[i].name.replace(/^Restraint\s+/, ''); })).forEach(function (h, k) {
                var th = document.createElement('td');
                th.textContent = h;
                if (k) th.className = 'cc-num';
                hr.appendChild(th);
            });
            t.appendChild(hr);
            rows.forEach(function (row) {
                var tr = document.createElement('tr');
                if (row.g === currentLC) tr.className = 'cc-win';
                var td = document.createElement('td');
                td.textContent = shortLC(row.name);
                td.title = row.name;
                tr.appendChild(td);
                idx.forEach(function (i) {
                    var v = row.rec ? row.rec[base + i] : NaN;
                    var tdv = document.createElement('td');
                    tdv.className = 'cc-num';
                    tdv.textContent = v === v ? fmtLoad(v) : '—';
                    tr.appendChild(tdv);
                });
                t.appendChild(tr);
            });
            elCalcCard.appendChild(t);
        }
        var forces = rc.filter(function (i) { return /F[XYZ]$/.test(comps[i].name); });
        var moments = rc.filter(function (i) { return /M[XYZ]$/.test(comps[i].name); });
        if (!forces.length && !moments.length) forces = rc.slice(0, 3);
        table(forces, 'Loads on the restraint');
        table(moments, 'Moments on the restraint');
        var dv = view.meta.dispVector;
        var data = neutralView() ? null : lcData();
        if (dv && data) {
            ccSub(elCalcCard, 'Displacement @ ' + lcFullName(currentLC));
            var t2 = document.createElement('table');
            comps.forEach(function (c, i) {
                if (c.kind !== 'displacement') return;
                ccRow(t2, c.name + (c.unit ? ' [' + c.unit + ']' : ''), fmt(nodeValue(data, ni, i), 4));
            });
            elCalcCard.appendChild(t2);
        }
    }

    // ---- UI ------------------------------------------------------------------
    if (elShow) elShow.addEventListener('change', function () { visible = this.checked; update(); });
    if (elSize) elSize.addEventListener('input', function () {
        var v = parseFloat(this.value);
        if (!(v > 0)) return;
        size = v;
        try { localStorage.setItem('pluto.supSize', String(v)); } catch (e) {}
        update();
    });
    if (elColor) elColor.addEventListener('change', function () {
        colorMode = this.value;
        var keep = colorMode;
        if (colorMode.charAt(0) === 'c' && view) keep = 'n:' + view.meta.components[parseInt(colorMode.slice(1), 10)].name;
        try { localStorage.setItem('pluto.supColor', keep); } catch (e) {}
        recolor();
    });

    return {
        onModelLoaded: onModelLoaded,
        onModelCleared: onModelCleared,
        onFeatures: onFeatures,
        recolor: recolor,
        refreshDisp: refreshDisp,
        setDispScale: setDispScale,
        writeVis: writeVis,
        pick: pick,
        fillReadout: fillReadout,
        showPinCard: showPinCard,
        count: function () { return shown.length; },
        // test hooks (viewer/tests/test_supports.js)
        _entries: function () { return entries; },
        _meshes: function () { return meshes; },
        _lines: function () { return lines; },
        _cnodeLines: function () { return cnodeLines; },
        _loadRange: function () { return loadRange; },
        _parts: symbolParts,
        _set: function (o) {
            if (o.size !== undefined) size = o.size;
            if (o.visible !== undefined) visible = o.visible;
            if (o.colorMode !== undefined) colorMode = o.colorMode;
            update();
        }
    };
})();
