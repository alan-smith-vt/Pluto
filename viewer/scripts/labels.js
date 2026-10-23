// ================================================================
// labels.js  --  screen-space text labels over the 3D view (2026-10-23):
// node numbers (pipe.js) and load values (loads.js). DOM spans in one
// fixed layer, placed after every render (viewer.js animate -> update).
//
// Layers: set(name, [{pos: THREE.Vector3 (scene coordinates), text, cls,
// priority}]). Each update projects every label, drops the ones behind
// the camera or off the canvas, then places them by priority (higher
// first, then nearer the camera) on a coarse screen grid: a label whose
// box overlaps one already placed is skipped, so a dense model shows a
// readable subset that fills in as you zoom. At most MAX on screen.
// ================================================================

var FEALabels = (function () {

    var layers = {};          // name -> list
    var host = null;
    var pool = [];            // spans, reused
    var MAX = 600;
    var CELL_W = 8, CELL_H = 6;
    var v = null;

    function ensureHost() {
        if (host || typeof document === 'undefined' || !document.body || !document.createElement) return host;
        host = document.createElement('div');
        host.id = 'labelLayer';
        document.body.appendChild(host);
        return host;
    }

    function set(name, list) {
        layers[name] = Array.isArray(list) ? list : [];
        if (typeof needsRender !== 'undefined') needsRender = true;
    }

    function count() {
        var n = 0;
        Object.keys(layers).forEach(function (k) { n += layers[k].length; });
        return n;
    }

    // Placement only (no DOM): [{label, x, y}] for a camera and a canvas size. Exposed for the tests.
    function place(cam, w, h) {
        if (!v) v = new THREE.Vector3();
        var cand = [];
        Object.keys(layers).forEach(function (k) {
            layers[k].forEach(function (l) {
                if (!l || !l.pos) return;
                v.copy(l.pos).project(cam);
                if (!(v.z > -1 && v.z < 1)) return;
                var x = (v.x + 1) / 2 * w, y = (1 - v.y) / 2 * h;
                if (x < -40 || x > w + 4 || y < 4 || y > h + 14) return;
                cand.push({ label: l, x: x, y: y, z: v.z, p: l.priority || 0 });
            });
        });
        cand.sort(function (a, b) { return (b.p - a.p) || (a.z - b.z); });
        var cols = Math.ceil(w / CELL_W) + 2, rows = Math.ceil(h / CELL_H) + 2;
        var grid = new Uint8Array(cols * rows), out = [];
        for (var i = 0; i < cand.length && out.length < MAX; i++) {
            var c = cand[i];
            var tw = 6.2 * String(c.label.text).length + 8, th = 13;
            var x0 = Math.floor((c.x + 4) / CELL_W), x1 = Math.floor((c.x + 4 + tw) / CELL_W);
            var y0 = Math.floor((c.y - th) / CELL_H), y1 = Math.floor(c.y / CELL_H);
            var free = true;
            for (var gy = Math.max(0, y0); gy <= Math.min(rows - 1, y1) && free; gy++)
                for (var gx = Math.max(0, x0); gx <= Math.min(cols - 1, x1); gx++)
                    if (grid[gy * cols + gx]) { free = false; break; }
            if (!free) continue;
            for (var gy2 = Math.max(0, y0); gy2 <= Math.min(rows - 1, y1); gy2++)
                for (var gx2 = Math.max(0, x0); gx2 <= Math.min(cols - 1, x1); gx2++) grid[gy2 * cols + gx2] = 1;
            out.push(c);
        }
        return out;
    }

    function update() {
        if (typeof camera === 'undefined' || !camera || typeof renderer === 'undefined' || !renderer) return;
        if (!count()) { hideFrom(0); return; }
        if (!ensureHost()) return;
        var rect = renderer.domElement.getBoundingClientRect();
        var placed = place(camera, rect.width, rect.height);
        for (var i = 0; i < placed.length; i++) {
            var s = pool[i];
            if (!s) { s = document.createElement('span'); pool.push(s); host.appendChild(s); }
            var l = placed[i].label;
            if (s._t !== l.text) { s.textContent = l.text; s._t = l.text; }
            var cls = 'lbl ' + (l.cls || '');
            if (s.className !== cls) s.className = cls;
            s.style.transform = 'translate(' + Math.round(rect.left + placed[i].x + 4) + 'px,' + Math.round(rect.top + placed[i].y - 13) + 'px)';
            s.style.display = '';
        }
        hideFrom(placed.length);
    }
    function hideFrom(k) {
        for (var i = k; i < pool.length; i++) if (pool[i].style.display !== 'none') pool[i].style.display = 'none';
    }

    return { set: set, update: update, place: place, count: count,
             _layers: function () { return layers; } };
})();
