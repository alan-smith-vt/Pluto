// ================================================================
// modules/bridgePicker.js  --  module (modules.js, core): hand-picked bridges between clean-model duct fragments.
//
// Self-contained: its REGISTRY entry in modules.js loads it; remove that entry (and this file) to drop it.
// The converter side is scripts/duct/model/ManualBridges.cs (DuctBridges in duct-config.psd1).
//
// Its tab's Arm button arms it (arming it disarms the Cut picker and the other way round). While armed, on any loaded
// clean-model file (main model or overlay; labels "clean | system ... | parts <ids>"):
//   Shift+click a duct     first pick = side A (green), second = side B (blue): one bridge, in the mode chosen
//                          in the tab (rigid link / extend A). Shift+click the first pick again to let it go.
// Each bridge is listed with its mode (changeable), Swap (A <-> B: which duct extends) and a delete button. The
// panel downloads bridges.json; Load reads one back to edit it. The list survives a reload (localStorage).
//
// bridges.json: { "kind": "pluto-duct-bridges", "version": 1, "bridges": [
//     { "mode": "rigid" | "extend", "a": { "parts": ["400012", ...], "label": "<clean label>" }, "b": { ... } } ] }
// parts = the clean member's source parts (Revit ElementId, N:<NavisId> for Navisworks-only members), as in drop.txt.
// The converter picks the two nearest open ends of the two ducts itself: no positions are stored.
// ================================================================

var FEABridgePicker = (function () {

    var parseParts = typeof FEACutPicker !== 'undefined' && FEACutPicker.parseParts ? FEACutPicker.parseParts : function (label) {
        if (!label || label.indexOf('clean |') !== 0) return null;
        var m = /\|\s*parts\s+([^|]*)/.exec(label);
        if (!m) return [];
        var s = m[1].trim(), nv = /^Navisworks\s+#(\S+)/.exec(s);
        if (nv) return ['N:' + nv[1]];
        return s.split(/\s+/).filter(function (x) { return x && x !== '-'; });
    };
    function makeDoc(list) {
        return { kind: 'pluto-duct-bridges', version: 1, bridges: list.map(function (b) {
            return { mode: b.mode, a: { parts: b.a.parts.slice(), label: b.a.label }, b: { parts: b.b.parts.slice(), label: b.b.label } };
        }) };
    }
    function readDoc(doc) {
        if (!doc || doc.kind !== 'pluto-duct-bridges' || !Array.isArray(doc.bridges)) throw new Error('not a bridges.json (kind pluto-duct-bridges)');
        return doc.bridges.filter(function (b) { return b && b.a && b.b && Array.isArray(b.a.parts) && Array.isArray(b.b.parts) && b.a.parts.length && b.b.parts.length; })
            .map(function (b) { return { mode: b.mode === 'extend' ? 'extend' : 'rigid', a: { parts: b.a.parts.map(String), label: String(b.a.label || '') }, b: { parts: b.b.parts.map(String), label: String(b.b.label || '') } }; });
    }

    if (typeof document === 'undefined' || typeof THREE === 'undefined') return { makeDoc: makeDoc, readDoc: readDoc };

    var KEY = 'pluto.bridgePicker';
    var armed = false, mode = 'rigid';
    var bridges = [];          // { mode, a: { parts, label }, b: { parts, label } }
    var first = null;          // the pending side A: { src, elem, parts, label }
    var undo = [];
    var markers = null;
    var MAIN = '(main model)';

    // ---- data (same sources as the Cut picker) ---------------------------
    function sources() {
        var out = [];
        if (window.FEABeams && FEABeams.view() && FEABeams.build() && FEABeams.mesh()) {
            var v = FEABeams.view();
            out.push({ name: MAIN, part: { view: v, build: FEABeams.build(), mesh: FEABeams.mesh() },
                       label: function (e) { return v.labels ? (v.labels.get(e) || '') : ''; } });
        }
        (window.FEAOverlays ? FEAOverlays.list() : []).forEach(function (ov) {
            var p = ov.drawable && ov.drawable.beam; if (!p) return;
            out.push({ name: ov.name, part: p, label: function (e) { return ov.drawable.label('beam', e); } });
        });
        return out;
    }
    function source(name) { return sources().find(function (s) { return s.name === name; }); }
    function partsKey(parts) { return parts.slice().sort().join(' '); }
    // the members of every loaded clean file holding any of these parts (to draw a saved bridge after a reload; a member
    // merged with a neighbour or split upstream is still found, as the converter finds it)
    function membersOf(parts) {
        var want = {}, out = []; parts.forEach(function (x) { want[x] = 1; });
        sources().forEach(function (src) {
            var n = src.part.view.header.nElements;
            for (var e = 0; e < n; e++) { var p = parseParts(src.label(e)); if (p && p.some(function (x) { return want[x]; })) out.push({ src: src, elem: e }); }
        });
        return out;
    }

    function save() { try { localStorage.setItem(KEY, JSON.stringify(makeDoc(bridges))); } catch (x) {} }
    function load() { try { var s = localStorage.getItem(KEY); if (s) bridges = readDoc(JSON.parse(s)); } catch (x) { bridges = []; } }
    function pushUndo() { undo.push(JSON.stringify(makeDoc(bridges))); if (undo.length > 50) undo.shift(); }

    // ---- markers: A green, B blue, a line between the two centres ----------
    function trisOf(m, out) {
        var p = m.src.part, pos = p.build.geometry.getAttribute('position').array, t2e = p.build.triToElem, v = new THREE.Vector3();
        p.mesh.updateMatrixWorld();
        var c = new THREE.Vector3(), n = 0;
        for (var t = 0; t < t2e.length; t++) {
            if (t2e[t] !== m.elem) continue;
            for (var j = 0; j < 3; j++) {
                var o = (t * 3 + j) * 3;
                v.set(pos[o], pos[o + 1], pos[o + 2]).applyMatrix4(p.mesh.matrixWorld);
                out.push(v.x, v.y, v.z); c.add(v); n++;
            }
        }
        return n ? c.multiplyScalar(1 / n) : null;
    }
    function redraw() {
        if (!window.scene) return;
        if (markers) { scene.remove(markers); markers.traverse(function (o) { if (o.geometry) o.geometry.dispose(); if (o.material) o.material.dispose(); }); markers = null; }
        markers = new THREE.Group(); markers.renderOrder = 10;
        var ga = [], gb = [], gl = [];
        function side(parts, arr) { var c = null; membersOf(parts).forEach(function (m) { var x = trisOf(m, arr); if (x && !c) c = x; }); return c; }
        bridges.forEach(function (b) {
            var ca = side(b.a.parts, ga), cb = side(b.b.parts, gb);
            if (ca && cb) gl.push(ca.x, ca.y, ca.z, cb.x, cb.y, cb.z);
        });
        if (first) trisOf(first, ga);
        function mesh(arr, color) {
            if (!arr.length) return;
            var g = new THREE.BufferGeometry(); g.setAttribute('position', new THREE.Float32BufferAttribute(arr, 3));
            var m = new THREE.Mesh(g, new THREE.MeshBasicMaterial({ color: color, transparent: true, opacity: 0.6, depthTest: false, side: THREE.DoubleSide }));
            m.renderOrder = 10; markers.add(m);
        }
        mesh(ga, 0x34c759); mesh(gb, 0x0a84ff);
        if (gl.length) {
            var g = new THREE.BufferGeometry(); g.setAttribute('position', new THREE.Float32BufferAttribute(gl, 3));
            var l = new THREE.LineSegments(g, new THREE.LineBasicMaterial({ color: 0xffcc00, depthTest: false }));
            l.renderOrder = 11; markers.add(l);
        }
        scene.add(markers);
        updatePanel();
        if (typeof requestRender === 'function') requestRender(); else window.needsRender = true;
    }

    // ---- picking ---------------------------------------------------------
    function pickAt(x, y) {
        var hit = typeof feaPick === 'function' ? feaPick(x, y) : null;
        var src = !hit ? null : hit.overlay ? (hit.fam === 'beam' ? source(hit.overlay.name) : null) : hit.beam ? source(MAIN) : null;
        if (!src) { status('no clean-model duct under the cursor'); return; }
        var label = src.label(hit.elem), parts = parseParts(label);
        if (!parts) { status('that is not a clean-model member: its label does not start "clean |"'); return; }
        if (!parts.length) { status('that member has no source parts (a rigid link?): pick a duct'); return; }
        if (!first) { first = { src: src, elem: hit.elem, parts: parts, label: label }; status('side A picked: Shift+click the duct to bridge to'); redraw(); return; }
        if (first.src === src && first.elem === hit.elem) { first = null; status('side A let go'); redraw(); return; }
        if (partsKey(first.parts) === partsKey(parts)) { status('the same parts on both sides: pick another duct'); return; }
        pushUndo();
        bridges.push({ mode: mode, a: { parts: first.parts, label: first.label }, b: { parts: parts, label: label } });
        first = null;
        status('bridge ' + bridges.length + ' added (' + mode + ')');
        save(); redraw();
    }
    var down = null;
    window.addEventListener('pointerdown', function (e) {
        if (!armed || !e.shiftKey || e.button !== 0 || !window.renderer || e.target !== renderer.domElement) return;
        e.stopImmediatePropagation(); e.preventDefault();
        if (window.controls) controls.enabled = false;
        down = { x: e.clientX, y: e.clientY };
    }, true);
    window.addEventListener('pointerup', function (e) {
        if (!down) return;
        e.stopImmediatePropagation();
        var d = down; down = null;
        if (window.controls) controls.enabled = true;
        if (Math.hypot(e.clientX - d.x, e.clientY - d.y) <= 5) pickAt(e.clientX, e.clientY);
    }, true);

    // ---- output ----------------------------------------------------------
    function download() {
        var a = document.createElement('a');
        a.href = URL.createObjectURL(new Blob([JSON.stringify(makeDoc(bridges), null, 2) + '\n'], { type: 'application/json' }));
        a.download = 'bridges.json'; document.body.appendChild(a); a.click(); a.remove();
        setTimeout(function () { URL.revokeObjectURL(a.href); }, 1000);
    }

    // ---- tab -------------------------------------------------------------
    var css = document.createElement('style');
    css.textContent =
        '.bp-arm.on{background:#34c759;border-color:#34c759;color:#000}' +
        '.bp-mode button.on{background:#ffcc00;border-color:#ffcc00;color:#000}' +
        '.bp-status{color:#ffcc00;min-height:1em;margin-top:4px}' +
        '.bp-list{margin-top:6px;max-height:40vh;overflow:auto}' +
        '.bp-item{display:flex;gap:4px;align-items:center;padding:3px 0;border-top:1px solid #ffffff14;font-size:11px}' +
        '.bp-item .bp-txt{flex:1;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}' +
        '.bp-item select{font-size:11px}.bp-a{color:#34c759}.bp-b{color:#0a84ff}';
    document.head.appendChild(css);
    var el = FEAModules.tab('bridgePicker');
    var box = document.createElement('div');
    box.innerHTML =
        '<div class="fea-row"><button class="fea-btn bp-arm" title="While armed, Shift+click two clean-model ducts to bridge them">Arm</button>' +
        '<span class="bp-mode" style="margin-left:6px"><button class="fea-btn on" data-m="rigid" title="a rigid link between the two nearest open ends">Rigid link</button>' +
        '<button class="fea-btn" data-m="extend" title="duct A extends along its axis to duct B">Extend A</button></span></div>' +
        '<div class="fea-hint">Arm, then Shift+click duct A (green) and duct B (blue). Rigid link: a rigid link between their nearest open ends. ' +
        'Extend A: duct A runs on along its own axis to B (a short rigid link closes any offset). Save the list as the DuctBridges file and rerun the export.</div>' +
        '<div class="fea-row" style="margin-top:6px;"><button class="fea-btn bp-dl">Download bridges.json</button>' +
        '<button class="fea-btn bp-load">Load</button><button class="fea-btn bp-undo">Undo</button><button class="fea-btn bp-clear">Clear</button>' +
        '<input type="file" accept=".json,application/json" class="bp-file" style="display:none"></div>' +
        '<div class="bp-status"></div><div class="bp-count"></div><div class="bp-list"></div>';
    el.appendChild(box);
    var q = function (s) { return el.querySelector(s); };
    function status(t) { q('.bp-status').textContent = t || ''; }
    function short(label) {   // "clean | system S | 24x12 | 3.5 ft | parts ..." -> "S 24x12 3.5 ft"
        var f = (label || '').split('|').map(function (x) { return x.trim(); });
        return f.length > 3 ? f[1].replace(/^system\s+/, '') + ' ' + f[2] + ' ' + f[3] : label;
    }
    function updatePanel() {
        q('.bp-count').textContent = bridges.length + ' bridge' + (bridges.length === 1 ? '' : 's') + (first ? ' (side A picked)' : '');
        var list = q('.bp-list'); list.innerHTML = '';
        bridges.forEach(function (b, i) {
            var row = document.createElement('div'); row.className = 'bp-item';
            row.innerHTML = '<span class="bp-txt" title=""><span class="bp-a"></span> → <span class="bp-b"></span></span>' +
                '<select><option value="rigid">rigid</option><option value="extend">extend A</option></select>' +
                '<button class="fea-btn" title="swap A and B (which duct extends)">⇄</button><button class="fea-btn" title="delete">✕</button>';
            row.querySelector('.bp-a').textContent = (i + 1) + '. ' + short(b.a.label);
            row.querySelector('.bp-b').textContent = short(b.b.label);
            row.querySelector('.bp-txt').title = 'A: ' + b.a.label + '\nB: ' + b.b.label;
            var sel = row.querySelector('select'); sel.value = b.mode;
            sel.addEventListener('change', function () { pushUndo(); b.mode = sel.value; save(); updatePanel(); });
            var bt = row.querySelectorAll('button');
            bt[0].addEventListener('click', function () { pushUndo(); var t = b.a; b.a = b.b; b.b = t; save(); redraw(); });
            bt[1].addEventListener('click', function () { pushUndo(); bridges.splice(i, 1); save(); redraw(); });
            list.appendChild(row);
        });
    }
    function setArmed(on) {
        armed = on;
        q('.bp-arm').classList.toggle('on', armed); q('.bp-arm').textContent = armed ? 'Armed' : 'Arm';
        if (!armed) first = null;
        if (armed) window.dispatchEvent(new CustomEvent('pluto-picker-armed', { detail: 'bridgePicker' }));
        status(''); redraw();
    }
    window.addEventListener('pluto-picker-armed', function (e) { if (e.detail !== 'bridgePicker' && armed) setArmed(false); });
    q('.bp-arm').addEventListener('click', function () { setArmed(!armed); });
    Array.prototype.forEach.call(el.querySelectorAll('.bp-mode button'), function (b) {
        b.addEventListener('click', function () {
            mode = b.getAttribute('data-m');
            Array.prototype.forEach.call(el.querySelectorAll('.bp-mode button'), function (x) { x.classList.toggle('on', x === b); });
        });
    });
    q('.bp-dl').addEventListener('click', download);
    q('.bp-load').addEventListener('click', function () { q('.bp-file').click(); });
    q('.bp-file').addEventListener('change', function () {
        var f = q('.bp-file').files[0]; if (!f) return;
        f.text().then(function (t) {
            pushUndo(); bridges = readDoc(JSON.parse(t)); save(); redraw(); status('loaded ' + bridges.length + ' from ' + f.name);
        }).catch(function (x) { status('not loaded: ' + x.message); });
        q('.bp-file').value = '';
    });
    q('.bp-undo').addEventListener('click', function () { if (undo.length) { bridges = readDoc(JSON.parse(undo.pop())); save(); redraw(); } });
    q('.bp-clear').addEventListener('click', function () { pushUndo(); bridges = []; first = null; save(); redraw(); });
    load(); updatePanel();

    return { makeDoc: makeDoc, readDoc: readDoc };
})();
