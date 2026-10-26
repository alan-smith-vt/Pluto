// ================================================================
// modules/cutPicker.js  --  module (modules.js, core): hand-pick clean-model members to drop.
//
// Self-contained: its REGISTRY entry in modules.js loads it; remove that entry (and this file) to drop it.
// The converter side is scripts/arms/DuctsManualDrop.cs (DuctDrop in duct-config.psd1).
//
// Its tab's Arm button arms it. While armed, on any overlay whose members carry
// clean-model labels ("clean | system ... | parts <ids>"):
//   Shift+click         toggle one member
//   Shift+drag          box: add every visible clean member whose centre is inside
//   Shift+Alt+drag      box: remove
// Picked members get a red marker. The panel downloads drop.txt: one part per line (Revit
// ElementId, or N:<NavisId> for Navisworks-only members) with the member label as a comment.
// The pick list survives a reload (localStorage, by overlay name + member index).
// ================================================================

var FEACutPicker = (function () {

    // "clean | system S | 24x12 | 3.5 ft | parts 123 456 | FLAGS X" -> ['123', '456'];
    // "parts Navisworks #1234ABCD" -> ['N:1234ABCD']; not a clean label -> null
    function parseParts(label) {
        if (!label || label.indexOf('clean |') !== 0) return null;
        var m = /\|\s*parts\s+([^|]*)/.exec(label);
        if (!m) return [];
        var s = m[1].trim();
        var nv = /^Navisworks\s+#(\S+)/.exec(s);
        if (nv) return ['N:' + nv[1]];
        return s.split(/\s+/).filter(function (x) { return x && x !== '-'; });
    }
    function lengthFt(label) { var m = /\|\s*([\d.]+)\s*ft\s*\|/.exec(label || ''); return m ? parseFloat(m[1]) : 0; }

    if (typeof document === 'undefined' || typeof THREE === 'undefined') return { parseParts: parseParts, lengthFt: lengthFt };

    var KEY = 'pluto.cutPicker';
    var armed = false;
    var picked = {};          // key "<overlay name>|<elem>" -> { ov, elem, label, parts }
    var undo = [];
    var markers = null;
    var centres = new WeakMap();   // drawable -> { local: Float32Array (3 per elem), tris: per elem first tri }

    // ---- data ----------------------------------------------------------
    function overlays() { return window.FEAOverlays ? FEAOverlays.list() : []; }
    function beamPart(ov) { return ov.drawable && ov.drawable.beam; }
    function key(ov, e) { return ov.name + '|' + e; }

    // per element: centre (mesh-local) and one triangle (for the visibility flag)
    function centresOf(ov) {
        var p = beamPart(ov); if (!p) return null;
        var c = centres.get(ov.drawable); if (c) return c;
        var pos = p.build.geometry.getAttribute('position').array, t2e = p.build.triToElem;
        var n = p.view.header.nElements, sum = new Float64Array(n * 3), cnt = new Uint32Array(n), tri = new Int32Array(n).fill(-1);
        for (var t = 0; t < t2e.length; t++) {
            var e = t2e[t]; if (tri[e] < 0) tri[e] = t;
            for (var v = 0; v < 3; v++) for (var k = 0; k < 3; k++) sum[e * 3 + k] += pos[(t * 3 + v) * 3 + k];
            cnt[e] += 3;
        }
        var local = new Float32Array(n * 3);
        for (var i = 0; i < n; i++) for (var j = 0; j < 3; j++) local[i * 3 + j] = cnt[i] ? sum[i * 3 + j] / cnt[i] : NaN;
        c = { local: local, tri: tri };
        centres.set(ov.drawable, c);
        return c;
    }
    function visible(ov, e) {
        if (!ov.ctx.look.visible || !ov.drawable.root.visible) return false;
        var p = beamPart(ov), c = centresOf(ov), va = p.build.geometry.getAttribute('elemVis');
        return !va || c.tri[e] < 0 || va.array[c.tri[e] * 3] >= 0.5;   // elemVis: 1 shown, 0 hidden (Groups eyes)
    }
    function worldCentre(ov, e) {
        var c = centresOf(ov), p = beamPart(ov);
        if (isNaN(c.local[e * 3])) return null;
        return new THREE.Vector3(c.local[e * 3], c.local[e * 3 + 1], c.local[e * 3 + 2]).applyMatrix4(p.mesh.matrixWorld);
    }
    function entry(ov, e) {
        var label = ov.drawable.label('beam', e), parts = parseParts(label);
        return parts ? { ov: ov, elem: e, label: label, parts: parts } : null;
    }

    function snapshot() { return Object.keys(picked); }
    function pushUndo() { undo.push(snapshot()); if (undo.length > 50) undo.shift(); }
    function restore(keys) {
        picked = {};
        keys.forEach(function (k) {
            var i = k.lastIndexOf('|'), name = k.substring(0, i), e = parseInt(k.substring(i + 1), 10);
            var ov = overlays().find(function (o) { return o.name === name; });
            var en = ov && beamPart(ov) && e < beamPart(ov).view.header.nElements ? entry(ov, e) : null;
            if (en) picked[k] = en;
        });
    }
    function save() { try { localStorage.setItem(KEY, JSON.stringify(snapshot())); } catch (x) {} }
    function saved() { try { return JSON.parse(localStorage.getItem(KEY) || '[]'); } catch (x) { return []; } }

    // ---- markers -------------------------------------------------------
    function redraw() {
        if (!window.scene) return;
        if (markers) { scene.remove(markers); markers.geometry.dispose(); markers.material.dispose(); markers = null; }
        // the picked members' own triangles, red and translucent, drawn on top
        var pts = [];
        Object.keys(picked).forEach(function (k) {
            var en = picked[k], p = beamPart(en.ov), pos = p.build.geometry.getAttribute('position').array, t2e = p.build.triToElem;
            p.mesh.updateMatrixWorld();
            var v = new THREE.Vector3();
            for (var t = 0; t < t2e.length; t++) {
                if (t2e[t] !== en.elem) continue;
                for (var j = 0; j < 3; j++) {
                    var o = (t * 3 + j) * 3;
                    v.set(pos[o], pos[o + 1], pos[o + 2]).applyMatrix4(p.mesh.matrixWorld);
                    pts.push(v.x, v.y, v.z);
                }
            }
        });
        if (pts.length) {
            var g = new THREE.BufferGeometry();
            g.setAttribute('position', new THREE.Float32BufferAttribute(pts, 3));
            markers = new THREE.Mesh(g, new THREE.MeshBasicMaterial({ color: 0xff3b30, transparent: true, opacity: 0.6,
                depthTest: false, side: THREE.DoubleSide }));
            markers.renderOrder = 10;
            scene.add(markers);
        }
        updatePanel();
        if (typeof requestRender === 'function') requestRender(); else window.needsRender = true;
    }

    // ---- picking -------------------------------------------------------
    function toggleAt(x, y) {
        var hit = typeof feaPick === 'function' ? feaPick(x, y) : null;
        if (!hit || !hit.overlay || hit.fam !== 'beam') { status('no clean member under the cursor'); return; }
        var en = entry(hit.overlay, hit.elem);
        if (!en) { status('not a clean-model member (label does not start "clean |")'); return; }
        pushUndo();
        var k = key(hit.overlay, hit.elem);
        if (picked[k]) { delete picked[k]; status('removed 1'); } else { picked[k] = en; status('added 1'); }
        save(); redraw();
    }
    function boxSelect(x0, y0, x1, y1, remove) {
        var rect = renderer.domElement.getBoundingClientRect();
        var lx = Math.min(x0, x1), hx = Math.max(x0, x1), ly = Math.min(y0, y1), hy = Math.max(y0, y1);
        pushUndo();
        var n = 0;
        overlays().forEach(function (ov) {
            var p = beamPart(ov); if (!p) return;
            for (var e = 0; e < p.view.header.nElements; e++) {
                if (!visible(ov, e)) continue;
                var w = worldCentre(ov, e); if (!w) continue;
                var s = w.clone().project(camera);
                if (s.z < -1 || s.z > 1) continue;
                var sx = rect.left + (s.x + 1) / 2 * rect.width, sy = rect.top + (1 - s.y) / 2 * rect.height;
                if (sx < lx || sx > hx || sy < ly || sy > hy) continue;
                var k = key(ov, e);
                if (remove) { if (picked[k]) { delete picked[k]; n++; } }
                else if (!picked[k]) { var en = entry(ov, e); if (en) { picked[k] = en; n++; } }
            }
        });
        status((remove ? 'removed ' : 'added ') + n);
        save(); redraw();
    }

    var drag = null, elBox = null;
    function onDown(e) {
        if (!armed || !e.shiftKey || e.button !== 0 || e.target !== renderer.domElement) return;
        e.stopImmediatePropagation(); e.preventDefault();
        if (window.controls) controls.enabled = false;
        drag = { x: e.clientX, y: e.clientY, alt: e.altKey };
    }
    function onMove(e) {
        if (!drag) return;
        e.stopImmediatePropagation();
        if (!elBox) { elBox = document.createElement('div'); elBox.className = 'cutpick-box'; document.body.appendChild(elBox); }
        elBox.style.left = Math.min(drag.x, e.clientX) + 'px'; elBox.style.top = Math.min(drag.y, e.clientY) + 'px';
        elBox.style.width = Math.abs(e.clientX - drag.x) + 'px'; elBox.style.height = Math.abs(e.clientY - drag.y) + 'px';
        elBox.style.borderColor = drag.alt ? '#5ac8fa' : '#ff3b30';
    }
    function onUp(e) {
        if (!drag) return;
        e.stopImmediatePropagation();
        var d = drag; drag = null;
        if (elBox) { elBox.remove(); elBox = null; }
        if (window.controls) controls.enabled = true;
        if (Math.hypot(e.clientX - d.x, e.clientY - d.y) <= 5) toggleAt(e.clientX, e.clientY);
        else boxSelect(d.x, d.y, e.clientX, e.clientY, d.alt || e.altKey);
    }
    window.addEventListener('pointerdown', onDown, true);
    window.addEventListener('pointermove', onMove, true);
    window.addEventListener('pointerup', onUp, true);

    // ---- output --------------------------------------------------------
    function listText() {
        var seen = {}, lines = ['# Pluto Cut picker drop list ' + new Date().toISOString().slice(0, 10) +
            ' -- DuctDrop in duct-config.psd1 (one part per line; # comments)'];
        Object.keys(picked).sort().forEach(function (k) {
            var en = picked[k];
            en.parts.forEach(function (p, i) {
                if (seen[p]) return; seen[p] = 1;
                lines.push(p + (i === 0 ? '    # ' + en.label : ''));
            });
        });
        return lines.join('\r\n') + '\r\n';
    }
    function download() {
        var a = document.createElement('a');
        a.href = URL.createObjectURL(new Blob([listText()], { type: 'text/plain' }));
        a.download = 'drop.txt'; document.body.appendChild(a); a.click(); a.remove();
        setTimeout(function () { URL.revokeObjectURL(a.href); }, 1000);
    }

    // ---- tab (modules.js) -----------------------------------------------
    var css = document.createElement('style');
    css.textContent =
        '.cp-arm.on{background:#ff3b30;border-color:#ff3b30;color:#fff}' +
        '.cp-count{margin:6px 0 2px}.cp-status{color:#ffcc00;min-height:1em;margin-top:4px}' +
        '.cutpick-box{position:fixed;border:1px dashed #ff3b30;background:#ff3b3014;pointer-events:none;z-index:60}';
    document.head.appendChild(css);
    var el = FEAModules.tab('cutPicker');
    var box = document.createElement('div');
    box.innerHTML = '<div class="fea-row"><button class="fea-btn cp-arm" title="While armed, Shift+click / Shift+drag on clean-model overlays pick members">Arm</button></div>' +
        '<div class="cp-count"></div>' +
        '<div class="fea-hint">Arm, then Shift+click to toggle a member, Shift+drag to add a box, Shift+Alt+drag to remove. ' +
        'Hidden groups are skipped. Save the list as the DuctDrop file and rerun the export.</div>' +
        '<div class="fea-row" style="margin-top:6px;"><button class="fea-btn cp-dl">Download drop.txt</button>' +
        '<button class="fea-btn cp-undo">Undo</button><button class="fea-btn cp-clear">Clear</button></div>' +
        '<div class="cp-status"></div>';
    el.appendChild(box);
    var q = function (s) { return el.querySelector(s); };
    function status(t) { q('.cp-status').textContent = t || ''; }
    function updatePanel() {
        var ks = Object.keys(picked), parts = {}, ft = 0;
        ks.forEach(function (k) { picked[k].parts.forEach(function (p) { parts[p] = 1; }); ft += lengthFt(picked[k].label); });
        q('.cp-count').textContent = ks.length + ' members, ' + Object.keys(parts).length + ' parts, ' + ft.toFixed(1) + ' ft';
    }
    q('.cp-arm').addEventListener('click', function () {
        armed = !armed;
        q('.cp-arm').classList.toggle('on', armed); q('.cp-arm').textContent = armed ? 'Armed' : 'Arm';
        if (armed && !Object.keys(picked).length) restore(saved());
        status(''); redraw();
    });
    updatePanel();
    q('.cp-dl').addEventListener('click', download);
    q('.cp-undo').addEventListener('click', function () { if (undo.length) { restore(undo.pop()); save(); redraw(); } });
    q('.cp-clear').addEventListener('click', function () { pushUndo(); picked = {}; save(); redraw(); });

    return { parseParts: parseParts, lengthFt: lengthFt, listText: listText };
})();
