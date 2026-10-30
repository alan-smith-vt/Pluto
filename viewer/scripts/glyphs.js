// ================================================================
// glyphs.js  --  glyph layer: groups that carry a `look` are drawn as
// screen-space symbols instead of painting their members.
// Spec: vault/format/features-sidecar.md ("look" / "anchors").
//
// Encoding (approved 2026-10-30): colour = identity of one chosen dimension,
// SHAPE = meaning, words in the legend. A look group never repaints its
// members; its glyphs sit at its `anchors` (element + t along a beam, or a
// node), or at each member's centroid when it has none.
//
// Rendering: per model ONE THREE.Points over every glyph, sampling a canvas
// atlas (one cell per glyph name, white shape + dark halo, tinted by the
// group colour). Constant pixel size. Exception glyphs draw over everything;
// detail glyphs are depth-tested after a pull of PULL_FRAC x span toward the
// camera, so an anchor inside a solid (a duct centreline) shows on its surface.
// Support glyphs (support-fixed / support-pinned) hang from their node with
// the base along `dir` (default: out of the member at a beam end, else world -Z). Per point: an inset along the beam
// toward its other end (t = 0 / 1 anchors of one joint don't stack), an
// optional world `dir` the sprite turns to, and a tier: `exception` always
// shows, `detail` only once the camera is nearer than DETAIL_FRAC x the
// model span. 10k glyphs measured at no visible frame cost (RTX laptop).
//
// Hover: a tooltip next to the cursor ("<glyph> label · anchor label"),
// picked on the CPU in screen space; a glyph the scene hides is skipped. Legend: built from the same groups and
// the same atlas pixels, bottom-left of the viewport.
//
// Pure part (collect) runs headless: node viewer/tests/test_glyphs.js
// ================================================================

var FEAGlyphs = (function () {

    var NAMES = ['circle', 'disc', 'half', 'diamond', 'square', 'triangle', 'warn', 'tick', 'cross', 'ring-arrows', 'anchor',
                 'support-fixed', 'support-pinned'];
    // Support glyphs (SAP/STAAD style) hang off the node: the node is the cell
    // centre and the base lies toward +x, which `dir` turns to point away from
    // the member (default: out of the member for a beam-end anchor, else world -Z). Drawn larger so the half-cell reads.
    var BASE_GLYPH = { 'support-fixed': 1, 'support-pinned': 1 };
    var BASE_SCALE = 1.6;
    var CELL = 64;
    var PX = 22;                 // glyph size, CSS px
    var PICK_PX = 12;            // hover radius, CSS px
    var DETAIL_FRAC = 0.45;      // detail tier shows below this camera distance / model span
    var PULL_FRAC = 0.01;        // detail glyphs draw this x model span toward the camera, so an anchor
                                 // inside a solid (a duct centreline) still shows on its surface;
                                 // exception glyphs draw over everything
    var NEUTRAL = '#e8e8ee';
    var ALARM = '#ff4d6d';       // reserved: `warn` default; nothing else should use it

    function glyphIndex(name) { var i = NAMES.indexOf(name); return i < 0 ? NAMES.indexOf('square') : i; }
    function hexRgb(h) {
        var m = /^#?([0-9a-f]{6})$/i.exec(h || '');
        if (!m) return null;
        var v = parseInt(m[1], 16);
        return [(v >> 16 & 255) / 255, (v >> 8 & 255) / 255, (v & 255) / 255];
    }
    function domainFamily(d) { return d === 'beams' || d === 'beam' ? 'beam' : d === 'nodes' ? 'nodes' : 'shell'; }
    function idIndex(ids) { var m = new Map(); for (var i = 0; i < ids.length; i++) m.set(ids[i], i); return m; }

    // ---- collect: groups -> flat glyph arrays (no THREE, no DOM) -------
    // items: features groups; views: { shell, beam } (elems records are
    // [count, n0, n1, ...] into that view's nodes); hiddenOf(g) -> bool.
    function collect(items, views, hiddenOf) {
        var pos = [], along = [], dir = [], col = [], gly = [], tier = [], inset = [], scale = [], meta = [], legend = [];
        var maps = {};
        function mapFor(fam) {
            if (maps[fam]) return maps[fam];
            var v = fam === 'nodes' ? views.shell : views[fam];
            if (!v) return null;
            return (maps[fam] = fam === 'nodes' ? idIndex(v.nodeIds) : idIndex(v.elemIds));
        }
        function nodeXYZ(v, n) { return [v.nodes[n * 3], v.nodes[n * 3 + 1], v.nodes[n * 3 + 2]]; }
        // -> { p, a } world point and unit vector toward the far end (or null)
        function place(fam, id, t) {
            var map = mapFor(fam);
            if (!map) return null;
            var i = map.get(id);
            if (i === undefined) return null;
            if (fam === 'nodes') return { p: nodeXYZ(views.shell, i), a: null };
            var v = views[fam], stride = v.elems.length / v.elemIds.length, k = v.elems[i * stride];
            if (fam === 'beam' && t !== undefined && t !== null && k >= 2) {
                var A = nodeXYZ(v, v.elems[i * stride + 1]), B = nodeXYZ(v, v.elems[i * stride + 2]);
                var d = [B[0] - A[0], B[1] - A[1], B[2] - A[2]], L = Math.hypot(d[0], d[1], d[2]) || 1;
                var s = t < 0.5 ? 1 : -1;      // inset points from the anchored end into the member
                return { p: [A[0] + d[0] * t, A[1] + d[1] * t, A[2] + d[2] * t],
                         a: (t <= 0 || t >= 1) ? [s * d[0] / L, s * d[1] / L, s * d[2] / L] : null };
            }
            var c = [0, 0, 0];
            for (var j = 1; j <= k; j++) { var q = nodeXYZ(v, v.elems[i * stride + j]); c[0] += q[0]; c[1] += q[1]; c[2] += q[2]; }
            return { p: [c[0] / k, c[1] / k, c[2] / k], a: null };
        }
        (items || []).forEach(function (g, gi) {
            var look = g && g.look;
            if (!look || typeof look !== 'object') return;
            var gIdx = glyphIndex(look.glyph);
            var rgb = hexRgb(g.color) || hexRgb(look.glyph === 'warn' ? ALARM : NEUTRAL);
            var isExc = look.tier === 'exception';
            var ins = typeof look.insetPx === 'number' ? look.insetPx : 10;
            var row = { gi: gi, glyph: NAMES[gIdx], color: g.color || (look.glyph === 'warn' ? ALARM : NEUTRAL),
                        label: look.label || g.name || '', section: look.section || '', tier: isExc ? 'exception' : 'detail',
                        n: 0, missing: 0, hidden: !!hiddenOf(g) };
            legend.push(row);
            if (row.hidden) return;
            var anchors = Array.isArray(g.anchors) ? g.anchors : null;
            if (!anchors) {                    // one glyph per member, at its centroid / node
                anchors = [];
                (Array.isArray(g.members) ? g.members : []).forEach(function (m) {
                    (m.nodeIds || []).forEach(function (id) { anchors.push({ domain: 'nodes', id: id }); });
                    (m.ids || []).forEach(function (id) { anchors.push({ domain: m.domain, id: id }); });
                });
            }
            anchors.forEach(function (an, ai) {
                var fam = domainFamily(an.domain);
                var at = place(fam, an.id, fam === 'beam' ? an.t : null);
                if (!at) { row.missing++; return; }
                pos.push(at.p[0], at.p[1], at.p[2]);
                var a = at.a || [0, 0, 0];
                along.push(a[0], a[1], a[2]);
                var isBase = !!BASE_GLYPH[NAMES[gIdx]];
                // support base: explicit dir, else out of the member at a beam end, else world -Z
                var d = Array.isArray(an.dir) && an.dir.length === 3 ? an.dir
                      : !isBase ? [0, 0, 0] : at.a ? [-at.a[0], -at.a[1], -at.a[2]] : [0, 0, -1];
                dir.push(+d[0] || 0, +d[1] || 0, +d[2] || 0);
                col.push(rgb[0], rgb[1], rgb[2]);
                gly.push(gIdx);
                tier.push(isExc ? 1 : 0);
                inset.push(at.a && !isBase ? ins : 0);   // a support sits on its node, not inset
                scale.push(isBase ? BASE_SCALE : 1);
                meta.push({ gi: gi, ai: ai, label: an.label || '' });
                row.n++;
            });
        });
        return { n: meta.length, pos: Float32Array.from(pos), along: Float32Array.from(along), dir: Float32Array.from(dir),
                 col: Float32Array.from(col), glyph: Float32Array.from(gly), tier: Float32Array.from(tier),
                 inset: Float32Array.from(inset), scale: Float32Array.from(scale), meta: meta, legend: legend };
    }

    // ---- atlas (canvas, drawn once) -------------------------------------
    // White shape with a dark halo; the shader multiplies white by the tint.
    // Shapes face +x where they have a direction (ring-arrows), so `dir`
    // turns them.
    function drawGlyph(g, name) {
        var c = CELL / 2, r = CELL * 0.30, INK = '#0b0c10';
        g.lineJoin = 'round'; g.lineCap = 'round';
        function halo(path, w) { g.lineWidth = w + 4; g.strokeStyle = INK; g.beginPath(); path(); g.stroke(); g.lineWidth = w; g.strokeStyle = '#fff'; g.beginPath(); path(); g.stroke(); }
        function solid(path) { g.lineWidth = 4; g.strokeStyle = INK; g.fillStyle = '#fff'; g.beginPath(); path(); g.stroke(); g.fill(); }
        function circ(rr) { return function () { g.arc(c, c, rr, 0, Math.PI * 2); }; }
        switch (name) {
            case 'circle': halo(circ(r * 0.85), 4); break;
            case 'disc': solid(circ(r)); break;
            case 'half':
                solid(circ(r));
                g.fillStyle = INK; g.beginPath(); g.arc(c, c, r - 2, -Math.PI / 2, Math.PI / 2); g.closePath(); g.fill(); break;
            case 'diamond': solid(function () { g.moveTo(c, c - r * 1.15); g.lineTo(c + r * 1.15, c); g.lineTo(c, c + r * 1.15); g.lineTo(c - r * 1.15, c); g.closePath(); }); break;
            case 'square': solid(function () { g.rect(c - r * 0.85, c - r * 0.85, r * 1.7, r * 1.7); }); break;
            case 'triangle': solid(function () { g.moveTo(c, c - r * 1.1); g.lineTo(c + r * 1.1, c + r * 0.8); g.lineTo(c - r * 1.1, c + r * 0.8); g.closePath(); }); break;
            case 'warn':
                solid(function () { g.moveTo(c, c - r * 1.3); g.lineTo(c + r * 1.3, c + r * 0.95); g.lineTo(c - r * 1.3, c + r * 0.95); g.closePath(); });
                g.fillStyle = INK; g.fillRect(c - 2.5, c - r * 0.55, 5, r * 0.85); g.beginPath(); g.arc(c, c + r * 0.6, 3, 0, Math.PI * 2); g.fill(); break;
            case 'tick': halo(function () { g.moveTo(c - r, c); g.lineTo(c - r * 0.25, c + r * 0.75); g.lineTo(c + r, c - r * 0.75); }, 5); break;
            case 'cross': halo(function () { g.moveTo(c - r, c - r); g.lineTo(c + r, c + r); g.moveTo(c + r, c - r); g.lineTo(c - r, c + r); }, 5); break;
            case 'ring-arrows':
                halo(function () { g.moveTo(c - r * 1.35, c); g.lineTo(c + r * 1.35, c); }, 3);
                [-1, 1].forEach(function (s) { solid(function () { g.moveTo(c + s * r * 1.55, c); g.lineTo(c + s * r * 1.05, c - r * 0.4); g.lineTo(c + s * r * 1.05, c + r * 0.4); g.closePath(); }); });
                g.lineWidth = 4; g.strokeStyle = INK; g.fillStyle = INK; g.beginPath(); g.arc(c, c, r * 0.55, 0, Math.PI * 2); g.fill();
                halo(circ(r * 0.5), 3); break;
            // supports: node at the cell centre, base toward +x (dir turns it)
            case 'support-fixed': (function () {
                var x0 = c + 2, x1 = CELL - 9, h = r * 1.15;           // block from the node to the base plate
                solid(function () { g.rect(x0, c - h, x1 - x0, 2 * h); });
                g.strokeStyle = INK; g.lineWidth = 2;                    // hatch: SAP-style fixity
                for (var k = -2; k <= 2; k++) { g.beginPath(); g.moveTo(x0 + 4, c + k * h * 0.42 - 4); g.lineTo(x1 - 4, c + k * h * 0.42 + 4); g.stroke(); }
                halo(function () { g.moveTo(x1 + 3, c - h - 4); g.lineTo(x1 + 3, c + h + 4); }, 3);   // ground line
            })(); break;
            case 'support-pinned': (function () {
                var x1 = CELL - 10, h = r * 1.05;                        // triangle: apex on the node, base toward +x
                solid(function () { g.moveTo(c + 1, c); g.lineTo(x1, c - h); g.lineTo(x1, c + h); g.closePath(); });
                halo(function () { g.moveTo(x1 + 4, c - h - 4); g.lineTo(x1 + 4, c + h + 4); }, 3);   // ground line
                g.fillStyle = INK; g.beginPath(); g.arc(c + 1, c, 3.5, 0, Math.PI * 2); g.fill();      // the pin
            })(); break;
            case 'anchor':
                solid(function () { g.moveTo(c, c - r * 0.05); g.lineTo(c + r * 1.1, c + r * 1.1); g.lineTo(c - r * 1.1, c + r * 1.1); g.closePath(); });
                solid(function () { g.rect(c - r * 0.6, c - r * 1.15, r * 1.2, r * 1.1); }); break;
        }
    }
    var atlasCv = null;
    function atlas() {
        if (atlasCv) return atlasCv;
        var cv = document.createElement('canvas');
        cv.width = CELL * NAMES.length; cv.height = CELL;
        var g = cv.getContext('2d');
        NAMES.forEach(function (n, i) { g.save(); g.translate(i * CELL, 0); drawGlyph(g, n); g.restore(); });
        return (atlasCv = cv);
    }
    // one glyph, tinted, as an <img> src (legend + tooltip use the 3D pixels)
    var imgCache = {};
    function glyphSrc(name, color) {
        var key = name + color;
        if (imgCache[key]) return imgCache[key];
        var cv = document.createElement('canvas'); cv.width = cv.height = CELL;
        var g = cv.getContext('2d'), i = glyphIndex(name);
        if (BASE_GLYPH[NAMES[i]]) { g.translate(CELL / 2, CELL / 2); g.rotate(Math.PI / 2); g.translate(-CELL * 0.75, -CELL / 2); }   // base down, node near the top
        g.drawImage(atlas(), i * CELL, 0, CELL, CELL, 0, 0, CELL, CELL);
        g.setTransform(1, 0, 0, 1, 0, 0);
        g.globalCompositeOperation = 'multiply'; g.fillStyle = color; g.fillRect(0, 0, CELL, CELL);
        g.globalCompositeOperation = 'destination-in';
        if (BASE_GLYPH[NAMES[i]]) { g.translate(CELL / 2, CELL / 2); g.rotate(Math.PI / 2); g.translate(-CELL * 0.75, -CELL / 2); }
        g.drawImage(atlas(), i * CELL, 0, CELL, CELL, 0, 0, CELL, CELL);
        return (imgCache[key] = cv.toDataURL());
    }

    // ---- shaders ---------------------------------------------------------
    var VS = [
        'attribute vec3 along; attribute vec3 gdir; attribute vec3 color; attribute float glyph; attribute float tier; attribute float inset; attribute float gscale;',
        'uniform float size; uniform vec2 viewport; uniform float detailFar; uniform float orthoDepth; uniform float pull;',
        'varying vec3 vC; varying float vG; varying vec2 vRot;',
        'vec2 screenDir(vec4 c0, vec3 w) {',
        '  vec4 c1 = projectionMatrix * modelViewMatrix * vec4(position + w, 1.0);',
        '  vec2 d = (c1.xy / c1.w - c0.xy / c0.w) * viewport;',
        '  float l = length(d); return l > 1e-6 ? d / l : vec2(0.0); }',
        'void main() {',
        '  vC = color; vG = glyph;',
        '  vec4 mv = modelViewMatrix * vec4(position, 1.0);',
        '  float depth = orthoDepth > 0.0 ? orthoDepth : -mv.z;',
        '  vec4 mvp = mv;',
        '  if (orthoDepth > 0.0) mvp.z += pull; else { float l = length(mv.xyz); mvp.xyz *= max(l - pull, 0.5 * l) / max(l, 1e-6); }',
        '  vec4 c0 = projectionMatrix * mvp;',
        '  float show = (tier > 0.5 || depth < detailFar) ? 1.0 : 0.0;',
        '  vec2 off = vec2(0.0); vRot = vec2(1.0, 0.0);',
        '  if (inset > 0.0) off = screenDir(c0, along * 1e-3 * abs(mv.z) + along * 1e-6) * inset;',
        '  if (length(gdir) > 0.0) { vec2 r = screenDir(c0, normalize(gdir) * 1e-3 * abs(mv.z) + gdir * 1e-6); if (length(r) > 0.0) vRot = r; }',
        '  c0.xy += off / viewport * c0.w;',
        '  if (tier > 0.5) c0.z = -0.999 * c0.w;',   // exceptions: over everything
        '  gl_Position = c0;',
        '  gl_PointSize = size * gscale * show; }'
    ].join('\n');
    var FS = [
        'uniform sampler2D atlas; uniform float count;',
        'varying vec3 vC; varying float vG; varying vec2 vRot;',
        'void main() {',
        '  vec2 p = gl_PointCoord - 0.5; p.y = -p.y;',
        '  p = vec2(vRot.x * p.x + vRot.y * p.y, -vRot.y * p.x + vRot.x * p.y) + 0.5;',
        '  if (p.x < 0.0 || p.x > 1.0 || p.y < 0.0 || p.y > 1.0) discard;',
        '  vec4 t = texture2D(atlas, vec2((vG + p.x) / count, p.y));',
        '  if (t.a < 0.35) discard;',
        '  gl_FragColor = vec4(t.rgb * vC, 1.0); }'
    ].join('\n');

    // ---- per-model layer ------------------------------------------------
    var texture = null;
    function material() {
        if (!texture) { texture = new THREE.CanvasTexture(atlas()); texture.flipY = true; }
        return new THREE.ShaderMaterial({
            vertexShader: VS, fragmentShader: FS,
            uniforms: { atlas: { value: texture }, count: { value: NAMES.length }, size: { value: PX },
                        viewport: { value: new THREE.Vector2(1, 1) }, detailFar: { value: 1e30 }, orthoDepth: { value: 0 }, pull: { value: 0 } }
        });
    }
    function spanOf(v) {
        if (!v || !v.nodes || !v.nodes.length) return 0;
        var lo = [Infinity, Infinity, Infinity], hi = [-Infinity, -Infinity, -Infinity];
        for (var i = 0; i < v.nodes.length; i += 3) for (var k = 0; k < 3; k++) { var x = v.nodes[i + k]; if (x < lo[k]) lo[k] = x; if (x > hi[k]) hi[k] = x; }
        return Math.hypot(hi[0] - lo[0], hi[1] - lo[1], hi[2] - lo[2]);
    }
    // frame state the shader and the picker share
    function frameUniforms(u, span) {
        var dpr = (renderer.getPixelRatio && renderer.getPixelRatio()) || 1;
        var sz = renderer.getSize(new THREE.Vector2());
        u.viewport.value.set(sz.x / 2, sz.y / 2);     // NDC -> CSS px half-extent
        u.size.value = PX * dpr;
        u.detailFar.value = span * DETAIL_FRAC;
        u.pull.value = span * PULL_FRAC;
        u.orthoDepth.value = camera.isOrthographicCamera ? (camera.top - camera.bottom) / camera.zoom * 1.07 : 0;
    }

    var layers = [];   // { ctx, points, data, items, span }

    // features.js calls this after every resolve of a model's groups
    function update(ctx, items, views, hiddenOf) {
        if (typeof THREE === 'undefined') return;
        var L = layers.filter(function (l) { return l.ctx === ctx; })[0];
        if (!L) { L = { ctx: ctx, points: null }; layers.push(L); }
        if (L.points) { if (L.points.parent) L.points.parent.remove(L.points); L.points.geometry.dispose(); L.points = null; }
        var data = collect(items, views, hiddenOf);
        L.data = data; L.items = items; L.span = spanOf(views.shell);
        if (data.n > 0) {
            var geo = new THREE.BufferGeometry();
            geo.setAttribute('position', new THREE.BufferAttribute(data.pos, 3));
            geo.setAttribute('along', new THREE.BufferAttribute(data.along, 3));
            geo.setAttribute('gdir', new THREE.BufferAttribute(data.dir, 3));
            geo.setAttribute('color', new THREE.BufferAttribute(data.col, 3));
            geo.setAttribute('glyph', new THREE.BufferAttribute(data.glyph, 1));
            geo.setAttribute('tier', new THREE.BufferAttribute(data.tier, 1));
            geo.setAttribute('inset', new THREE.BufferAttribute(data.inset, 1));
            geo.setAttribute('gscale', new THREE.BufferAttribute(data.scale, 1));
            var pts = new THREE.Points(geo, material());
            pts.frustumCulled = false;
            pts.renderOrder = 10;
            pts.onBeforeRender = function () { frameUniforms(pts.material.uniforms, L.span); };
            L.points = pts;
            var parent = ctx.markerParent && ctx.markerParent();
            if (parent) parent.add(pts);
        }
        drawLegend();
        bindHover();
        if (typeof needsRender !== 'undefined') needsRender = true;
    }
    function remove(ctx) {
        layers = layers.filter(function (l) {
            if (l.ctx !== ctx) return true;
            if (l.points && l.points.parent) l.points.parent.remove(l.points);
            return false;
        });
        drawLegend();
    }

    // ---- picking (CPU, screen space; mirrors the vertex shader) ---------
    // Candidates within PICK_PX of the cursor, nearest first; the first one
    // the scene does not hide wins. Hidden = what the shader hides: an
    // exception glyph never; a detail glyph when a ray through its centre
    // pixel hits geometry nearer than the glyph pulled PULL_FRAC x span
    // toward the camera.
    var occluder = function (cssX, cssY) {      // -> world point of the nearest visible surface, or null
        if (typeof feaPick !== 'function') return null;
        var r = renderer.domElement.getBoundingClientRect();
        var h = feaPick(r.left + cssX, r.top + cssY);
        return h && h.point ? h.point : null;
    };
    // view depth of a world point (perspective: distance from the eye, which the pull shortens)
    function eyeDepth(p) {
        return camera.isOrthographicCamera ? -p.clone().applyMatrix4(camera.matrixWorldInverse).z : p.distanceTo(camera.position);
    }
    var v0 = null, v1 = null, v2 = null;
    function pick(cssX, cssY) {
        if (typeof THREE === 'undefined' || !layers.length) return null;
        v0 = v0 || new THREE.Vector3(); v1 = v1 || new THREE.Vector3(); v2 = v2 || new THREE.Vector3();
        camera.updateMatrixWorld();
        var sz = renderer.getSize(new THREE.Vector2()), hw = sz.x / 2, hh = sz.y / 2;
        var cands = [], R2 = PICK_PX * PICK_PX;
        var orthoDepth = camera.isOrthographicCamera ? (camera.top - camera.bottom) / camera.zoom * 1.07 : 0;
        layers.forEach(function (L) {
            if (!L.points || !L.points.visible || !L.points.parent) return;
            if (L.ctx.look && L.ctx.look.visible === false) return;
            var d = L.data, mw = L.points.matrixWorld, far = L.span * DETAIL_FRAC;
            for (var i = 0; i < d.n; i++) {
                v0.set(d.pos[i * 3], d.pos[i * 3 + 1], d.pos[i * 3 + 2]).applyMatrix4(mw);
                if (d.tier[i] < 0.5) {     // same depth the shader uses: view-space z (or the ortho stand-in)
                    var depth = orthoDepth || -v2.copy(v0).applyMatrix4(camera.matrixWorldInverse).z;
                    if (depth >= far) continue;
                }
                v1.copy(v0).project(camera);
                if (v1.z > 1 || v1.z < -1) continue;
                var sx = (v1.x + 1) * hw, sy = (1 - v1.y) * hh;
                if (d.inset[i] > 0) {
                    v2.set(d.along[i * 3], d.along[i * 3 + 1], d.along[i * 3 + 2]).multiplyScalar(1e-3 * v0.distanceTo(camera.position) + 1e-6);
                    v2.add(v0).project(camera);
                    var ex = (v2.x + 1) * hw - sx, ey = (1 - v2.y) * hh - sy, el = Math.hypot(ex, ey);
                    if (el > 1e-6) { sx += ex / el * d.inset[i]; sy += ey / el * d.inset[i]; }
                }
                var dd = (sx - cssX) * (sx - cssX) + (sy - cssY) * (sy - cssY);
                if (dd < R2) cands.push({ L: L, i: i, dd: dd, sx: sx, sy: sy, depth: eyeDepth(v0) });
            }
        });
        cands.sort(function (a, b) { return a.dd - b.dd; });
        for (var c = 0; c < cands.length; c++) {
            var k = cands[c];
            if (k.L.data.tier[k.i] < 0.5) {
                var p = occluder(k.sx, k.sy);
                if (p && eyeDepth(p) < k.depth - k.L.span * PULL_FRAC) continue;
            }
            var m = k.L.data.meta[k.i], row = k.L.data.legend.filter(function (r) { return r.gi === m.gi; })[0];
            return { glyph: row.glyph, color: row.color, label: row.label, anchorLabel: m.label, group: k.L.items[m.gi] };
        }
        return null;
    }

    // ---- hover tooltip ----------------------------------------------------
    var tip = null, bound = false, pending = null;
    function esc(s) { return String(s).replace(/[&<>"]/g, function (c) { return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]; }); }
    function bindHover() {
        if (bound || typeof renderer === 'undefined' || !renderer || !renderer.domElement) return;
        bound = true;
        var el = renderer.domElement;
        tip = document.createElement('div');
        tip.className = 'glyph-tip';
        document.body.appendChild(tip);
        el.addEventListener('mousemove', function (e) {
            if (pending) return;
            pending = requestAnimationFrame(function () {
                pending = null;
                var r = el.getBoundingClientRect(), h = pick(e.clientX - r.left, e.clientY - r.top);
                if (!h) { tip.style.display = 'none'; return; }
                tip.innerHTML = '<img src="' + glyphSrc(h.glyph, h.color) + '" width="16" height="16"><span>' + esc(h.label) +
                    (h.anchorLabel ? ' <span class="glyph-tip-sub">· ' + esc(h.anchorLabel) + '</span>' : '') + '</span>';
                tip.style.left = (e.clientX + 14) + 'px';
                tip.style.top = (e.clientY + 14) + 'px';
                tip.style.display = 'flex';
            });
        });
        el.addEventListener('mouseleave', function () { tip.style.display = 'none'; });
    }

    // ---- legend --------------------------------------------------------------
    // Glyph rows by section (glyph + words + count), from every model with a
    // glyph layer. Hidden groups are left out; a group with no glyphs placed
    // still lists with 0 so a missing anchor set is visible.
    function drawLegend() {
        if (typeof document === 'undefined' || !document.body) return;
        var el = document.getElementById('glyphLegend');
        var rows = [];
        layers.forEach(function (L) { if (L.data) L.data.legend.forEach(function (r) { if (!r.hidden) rows.push(r); }); });
        if (!rows.length) { if (el) el.style.display = 'none'; return; }
        if (!el) { el = document.createElement('div'); el.id = 'glyphLegend'; document.body.appendChild(el); }
        // exceptions first, then by section, keeping file order inside
        rows = rows.map(function (r, i) { return { r: r, i: i }; }).sort(function (a, b) {
            var ta = a.r.tier === 'exception' ? 0 : 1, tb = b.r.tier === 'exception' ? 0 : 1;
            return ta - tb || (a.r.section < b.r.section ? -1 : a.r.section > b.r.section ? 1 : a.i - b.i);
        }).map(function (x) { return x.r; });
        var html = '', sec = null;
        rows.forEach(function (r) {
            var head = r.tier === 'exception' ? 'Checks' : (r.section || 'Symbols');
            if (r.tier === 'exception' && r.section) head = r.section;
            if (head !== sec) { sec = head; html += '<div class="gl-sec">' + esc(head) + '</div>'; }
            html += '<div class="gl-row' + (r.tier === 'detail' ? ' gl-detail' : '') + '" title="' +
                (r.tier === 'detail' ? 'shown when zoomed in' : 'always shown') + (r.missing ? ' · ' + r.missing + ' anchor(s) not in the model' : '') + '">' +
                '<img src="' + glyphSrc(r.glyph, r.color) + '" width="18" height="18"><span class="gl-name">' + esc(r.label) +
                '</span><span class="gl-n">' + r.n + '</span></div>';
        });
        el.innerHTML = html;
        el.style.display = '';
    }

    return {
        update: update, remove: remove, pick: pick, collect: collect,
        setOccluder: function (fn) { occluder = fn; },     // tests: (cssX, cssY) -> world point | null
        names: function () { return NAMES.slice(); }, glyphSrc: glyphSrc,
        _layers: function () { return layers; }
    };
})();
