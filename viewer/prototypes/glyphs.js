// PROTOTYPE (branch viewer-design, not for main): screen-space glyph layer.
// One THREE.Points draw call for every marker; each point samples one cell
// of a canvas-drawn glyph atlas. Constant pixel size, dark halo for contrast
// on any background, depth-tested so glyphs hide behind solid geometry.
// Driven only by generic per-group data: { glyph, color, at } — no domain
// names in here. GlyphProto.run(spec) where spec maps a group name to that.
var GlyphProto = (function () {
    var CELL = 64, GLYPHS = ['circle', 'disc', 'half', 'diamond', 'square',
        'ring-arrows', 'anchor', 'warn', 'tick', 'cross'];

    // Every glyph: white fill + dark outline drawn into its own cell. The
    // shader tints the white by the point colour, so shape = meaning and
    // colour = group, independently.
    function draw(g, name) {
        var c = CELL / 2, r = CELL * 0.34;
        g.lineJoin = 'round'; g.lineWidth = 7; g.strokeStyle = '#0b0c10'; g.fillStyle = '#fff';
        function both(path) { g.beginPath(); path(); g.stroke(); g.lineWidth = 3; g.strokeStyle = '#fff'; g.beginPath(); path(); g.stroke(); g.lineWidth = 7; g.strokeStyle = '#0b0c10'; }
        function fs(path) { g.beginPath(); path(); g.stroke(); g.fill(); }
        switch (name) {
            case 'circle': both(function () { g.arc(c, c, r * 0.8, 0, 7); }); break;          // pinned: hollow
            case 'disc': fs(function () { g.arc(c, c, r * 0.8, 0, 7); }); break;              // fixed: solid
            case 'half':                                                                       // hinge: half
                g.beginPath(); g.arc(c, c, r * 0.8, 0, 7); g.stroke(); g.fill();
                g.fillStyle = '#0b0c10'; g.beginPath(); g.arc(c, c, r * 0.8 - 2, -Math.PI / 2, Math.PI / 2); g.fill(); g.fillStyle = '#fff'; break;
            case 'diamond': fs(function () { g.moveTo(c, c - r); g.lineTo(c + r, c); g.lineTo(c, c + r); g.lineTo(c - r, c); g.closePath(); }); break;
            case 'square': fs(function () { g.rect(c - r * 0.75, c - r * 0.75, r * 1.5, r * 1.5); }); break;
            case 'ring-arrows':                                                                // guide
                both(function () { g.arc(c, c, r * 0.45, 0, 7); });
                both(function () { g.moveTo(c - r * 1.3, c); g.lineTo(c + r * 1.3, c); });
                fs(function () { g.moveTo(c - r * 1.4, c); g.lineTo(c - r * 0.95, c - r * 0.35); g.lineTo(c - r * 0.95, c + r * 0.35); g.closePath(); });
                fs(function () { g.moveTo(c + r * 1.4, c); g.lineTo(c + r * 0.95, c - r * 0.35); g.lineTo(c + r * 0.95, c + r * 0.35); g.closePath(); });
                break;
            case 'anchor':                                                                     // cube on pyramid
                fs(function () { g.moveTo(c, c - r * 0.1); g.lineTo(c + r, c + r); g.lineTo(c - r, c + r); g.closePath(); });
                fs(function () { g.rect(c - r * 0.5, c - r * 1.0, r, r * 0.9); }); break;
            case 'warn':
                fs(function () { g.moveTo(c, c - r * 1.1); g.lineTo(c + r * 1.1, c + r * 0.8); g.lineTo(c - r * 1.1, c + r * 0.8); g.closePath(); });
                g.fillStyle = '#0b0c10'; g.font = 'bold ' + (CELL * 0.38) + 'px sans-serif'; g.textAlign = 'center'; g.fillText('!', c, c + r * 0.65); g.fillStyle = '#fff'; break;
            case 'tick': both(function () { g.moveTo(c - r, c); g.lineTo(c - r * 0.2, c + r * 0.7); g.lineTo(c + r, c - r * 0.7); }); break;
            case 'cross': both(function () { g.moveTo(c - r, c - r); g.lineTo(c + r, c + r); g.moveTo(c + r, c - r); g.lineTo(c - r, c + r); }); break;
        }
    }

    var atlasCanvas = null;
    function atlas() {
        if (atlasCanvas) return atlasCanvas;
        var cv = document.createElement('canvas'); cv.width = CELL * GLYPHS.length; cv.height = CELL;
        var g = cv.getContext('2d');
        GLYPHS.forEach(function (n, i) { g.save(); g.translate(i * CELL, 0); draw(g, n); g.restore(); });
        return (atlasCanvas = cv);
    }
    // a glyph cell as a data URL, for the legend (same pixels as the 3D layer)
    function glyphImg(name, color) {
        var cv = document.createElement('canvas'); cv.width = cv.height = CELL;
        var g = cv.getContext('2d'); g.drawImage(atlas(), GLYPHS.indexOf(name) * CELL, 0, CELL, CELL, 0, 0, CELL, CELL);
        g.globalCompositeOperation = 'multiply'; g.fillStyle = color; g.fillRect(0, 0, CELL, CELL);
        g.globalCompositeOperation = 'destination-in'; g.drawImage(atlas(), GLYPHS.indexOf(name) * CELL, 0, CELL, CELL, 0, 0, CELL, CELL);
        return cv.toDataURL();
    }

    var VS = 'attribute float glyph; attribute float far; attribute vec3 color; uniform float size; varying float vG; varying vec3 vC;' +
        'void main(){ vG=glyph; vC=color; vec4 mv=modelViewMatrix*vec4(position,1.0); gl_Position=projectionMatrix*mv;' +
        ' gl_Position.z -= 0.002*gl_Position.w; gl_PointSize=(-mv.z>far)?0.0:size; }';
    var FS = 'uniform sampler2D atlas; uniform float n; varying float vG; varying vec3 vC;' +
        'void main(){ vec2 uv=vec2((vG+gl_PointCoord.x)/n, 1.0-gl_PointCoord.y); vec4 t=texture2D(atlas,uv);' +
        ' if(t.a<0.3) discard; gl_FragColor=vec4(t.rgb*vC,1.0); }';

    // element centroid per record: [count, n0, n1, ...] node indices
    function centroids(view, ids) {
        var out = [], stride = view.elems.length / view.elemIds.length, idx = {};
        for (var i = 0; i < view.elemIds.length; i++) idx[view.elemIds[i]] = i;
        ids.forEach(function (id) {
            var e = idx[id]; if (e === undefined) return;
            var k = view.elems[e * stride], x = 0, y = 0, z = 0;
            for (var j = 1; j <= k; j++) { var n = view.elems[e * stride + j]; x += view.nodes[n * 3]; y += view.nodes[n * 3 + 1]; z += view.nodes[n * 3 + 2]; }
            out.push([x / k, y / k, z / k]);
        });
        return out;
    }
    // shell icons: one glyph per connected cluster (cluster = within `tol`)
    function clusters(pts, tol) {
        var out = [];
        pts.forEach(function (p) {
            for (var i = 0; i < out.length; i++) { var q = out[i]; if (Math.abs(q.s[0] / q.n - p[0]) < tol && Math.abs(q.s[1] / q.n - p[1]) < tol && Math.abs(q.s[2] / q.n - p[2]) < tol) { q.s[0] += p[0]; q.s[1] += p[1]; q.s[2] += p[2]; q.n++; return; } }
            out.push({ s: p.slice(), n: 1 });
        });
        return out.map(function (q) { return [q.s[0] / q.n, q.s[1] / q.n, q.s[2] / q.n]; });
    }

    var layer = null;
    // spec: [{ group, glyph, color, label, cluster? }]; features: parsed json
    function run(features, spec, opts) {
        opts = opts || {};
        var byName = {}; features.groups.items.forEach(function (g) { byName[g.name] = g; });
        var pos = [], col = [], gl = [], fr = [], legend = [];
        spec.forEach(function (s) {
            var g = byName[s.group]; if (!g) return;
            var pts = [];
            g.members.forEach(function (m) {
                var v = m.domain === 'beams' ? FEABeams.view() : feaModel;
                pts = pts.concat(centroids(v, m.ids));
            });
            if (s.cluster) pts = clusters(pts, s.cluster);
            var c = new THREE.Color(s.color);
            pts.forEach(function (p) { pos.push(p[0], p[1], p[2]); col.push(c.r, c.g, c.b); gl.push(GLYPHS.indexOf(s.glyph)); fr.push(s.far || 1e9); });
            legend.push({ s: s, n: pts.length });
        });
        var geo = new THREE.BufferGeometry();
        geo.setAttribute('position', new THREE.Float32BufferAttribute(pos, 3));
        geo.setAttribute('color', new THREE.Float32BufferAttribute(col, 3));
        geo.setAttribute('glyph', new THREE.Float32BufferAttribute(gl, 1));
        geo.setAttribute('far', new THREE.Float32BufferAttribute(fr, 1));
        var tex = new THREE.CanvasTexture(atlas());
        var mat = new THREE.ShaderMaterial({ vertexShader: VS, fragmentShader: FS, transparent: false,
            uniforms: { atlas: { value: tex }, n: { value: GLYPHS.length }, size: { value: (opts.px || 22) * (window.devicePixelRatio || 1) } } });
        if (layer) layer.parent.remove(layer);
        layer = new THREE.Points(geo, mat); layer.renderOrder = 10;
        FEAModels.active().markerParent().add(layer);
        drawLegend(legend, opts);
        return { markers: pos.length / 3 };
    }

    // Legend: grouped by meaning, glyph + swatch + words + count. Lives in the
    // viewport corner, replacing the empty colour bar when no field is shown.
    function drawLegend(rows, opts) {
        var el = document.getElementById('glyphLegend');
        if (!el) { el = document.createElement('div'); el.id = 'glyphLegend'; document.body.appendChild(el); }
        el.style.cssText = 'position:fixed;left:430px;bottom:14px;background:rgba(14,15,20,.92);border:1px solid #2a2d36;border-radius:6px;padding:8px 12px;font:12px/1.5 system-ui,sans-serif;color:#d8dae0;z-index:50;min-width:230px';
        var html = '', sec = null;
        (opts.context || []).forEach(function (c, i) {
            if (i === 0) html += '<div style="color:#8a8f9a;font-size:11px;text-transform:uppercase;letter-spacing:.06em;margin-top:2px">Colour = ' + (opts.colorBy || 'group') + '</div>';
            html += '<div><span style="display:inline-block;width:18px;height:8px;background:' + c.color + ';margin-right:8px;border-radius:2px"></span>' + c.label + '</div>';
        });
        rows.forEach(function (r) {
            if (r.s.section !== sec) { sec = r.s.section; html += '<div style="color:#8a8f9a;font-size:11px;text-transform:uppercase;letter-spacing:.06em;margin-top:6px">' + sec + '</div>'; }
            html += '<div style="display:flex;align-items:center;gap:8px"><img src="' + glyphImg(r.s.glyph, r.s.color) + '" width="20" height="20"><span style="flex:1">' + r.s.label + '</span><span style="color:#8a8f9a">' + r.n + '</span></div>';
        });
        el.innerHTML = html;
    }

    // perf probe: n random markers in the model box, returns ms/frame over 60 frames
    function bench(n) {
        var b = new THREE.Box3().setFromObject(scene), pos = new Float32Array(n * 3), col = new Float32Array(n * 3).fill(1), gl = new Float32Array(n);
        for (var i = 0; i < n; i++) { pos[i * 3] = b.min.x + Math.random() * (b.max.x - b.min.x); pos[i * 3 + 1] = b.min.y + Math.random() * (b.max.y - b.min.y); pos[i * 3 + 2] = b.min.z + Math.random() * (b.max.z - b.min.z); gl[i] = i % GLYPHS.length; }
        layer.geometry.setAttribute('position', new THREE.BufferAttribute(pos, 3));
        layer.geometry.setAttribute('color', new THREE.BufferAttribute(col, 3));
        layer.geometry.setAttribute('glyph', new THREE.BufferAttribute(gl, 1));
        layer.geometry.setAttribute('far', new THREE.BufferAttribute(new Float32Array(n).fill(1e9), 1));
        layer.parent.remove(layer); scene.add(layer);
    }

    return { run: run, bench: bench, glyphs: GLYPHS, glyphImg: glyphImg };
})();
