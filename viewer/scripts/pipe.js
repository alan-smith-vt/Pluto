// ================================================================
// pipe.js  --  the pipe model in the viewer (2026-10-23): the sidecar's
// `review`, `properties` and `model.source`, written by the CAESAR export
// (PipeSidecar.cs) from the model a workbook, a TOML or a .cii holds. The
// viewer is the confirmation step before CAESAR imports the model.
// Spec: vault/format/features-sidecar.md (review, properties, model.source).
//
//   Checks flyout   the build's checks by level (error, warning, notice,
//                   info), each with its nodes / elements / beams; a click
//                   flies to them and flashes them. The tab shows the count
//                   of errors and warnings.
//   Source line     where the model came from: file, kind, saved time,
//                   when the preview was written (Model panel, and the
//                   Checks header)
//   Properties      hovering a pipe beam adds its element's fields to the
//                   readout (size, T1 / P1, material ...); pinning it gives
//                   a card with every field, the loads on it and the checks
//                   about it
//   Node numbers    the Display panel's "Node numbers": every real node's
//                   number on screen (FEALabels; bend / valve points and
//                   other synthetic vertices, ids from 10,000,000, skipped)
//
// Hooks (guarded by window.FEAPipe): viewer.js load / clear, showReadout,
// pin; features.js sync -> onFeatures.
// ================================================================

var FEAPipe = (function () {

    var view = null;
    var nodeIndex = null, beamIndex = null;
    var beamProp = null;        // beam index -> property item
    var propsRef = null, reviewRef = null, sourceRef = null;
    var units = {};
    var levelOn = { error: true, warning: true, notice: true, info: true };
    var selected = '';
    var nodeNums = false;
    var SYNTHETIC_FROM = 10000000;

    var elPanel   = document.getElementById('rvPanel');
    var elTab     = document.getElementById('rvTab');
    var elSummary = document.getElementById('rvSummary');
    var elSource  = document.getElementById('rvSource');
    var elList    = document.getElementById('rvList');
    var elFeatSrc = document.getElementById('featSource');
    var elPropRow = document.getElementById('roPropsRow');
    var elProps   = document.getElementById('roProps');
    var elNodeNums = document.getElementById('feaNodeNums');

    try { nodeNums = localStorage.getItem('pluto.nodeNums') === '1'; } catch (e0) {}
    if (elNodeNums) elNodeNums.checked = nodeNums;

    function f(v, d) { return typeof fmt === 'function' ? fmt(v, d) : String(v); }
    function env() { return window.FEAFeatures ? FEAFeatures.envelope() : null; }
    function primaryView() {
        if (window.FEABeams && FEABeams.view()) return FEABeams.view();
        return null;
    }

    // ---- model / features ------------------------------------------------
    function onModelLoaded() {
        onModelCleared();
        view = primaryView();
        if (view) {
            nodeIndex = new Map();
            for (var i = 0; i < view.nodeIds.length; i++) nodeIndex.set(view.nodeIds[i], i);
            beamIndex = new Map();
            for (var e = 0; e < view.elemIds.length; e++) beamIndex.set(view.elemIds[e], e);
        }
        syncNodeLabels();
    }
    function onModelCleared() {
        view = null; nodeIndex = null; beamIndex = null; beamProp = null;
        propsRef = null; reviewRef = null; sourceRef = null; selected = '';
        if (window.FEALabels) FEALabels.set('nodes', []);
        renderAll();
    }
    function onFeatures() {
        var e = env();
        var p = e && e.properties && Array.isArray(e.properties.items) ? e.properties : null;
        var r = e && e.review && Array.isArray(e.review.items) ? e.review : null;
        var s = e && e.model && e.model.source ? e.model.source : null;
        if (p !== propsRef) {
            propsRef = p;
            units = (p && p.units) || {};
            beamProp = null;
            if (p && beamIndex) {
                beamProp = new Map();
                p.items.forEach(function (it) {
                    (it.members || []).forEach(function (m) {
                        (m.ids || []).forEach(function (id) { var b = beamIndex.get(id); if (b !== undefined) beamProp.set(b, it); });
                    });
                });
            }
            syncNodeLabels();
        }
        if (r !== reviewRef || s !== sourceRef) { reviewRef = r; sourceRef = s; renderAll(); }
    }
    function pipeMode() { return !!propsRef; }

    // ---- source -----------------------------------------------------------
    function when(t) { return t ? String(t).replace('T', ' ').replace(/:\d\dZ$/, ' UTC').replace(/Z$/, ' UTC') : ''; }
    function sourceText() {
        var s = sourceRef;
        if (!s) return '';
        var parts = ['from ' + (s.file || '?') + (s.kind ? ' (' + s.kind + ')' : '')];
        if (s.saved) parts.push('saved ' + when(s.saved));
        if (s.written) parts.push('preview ' + when(s.written));
        return parts.join(' · ');
    }

    // ---- Checks flyout ------------------------------------------------------
    var LEVELS = ['error', 'warning', 'notice', 'info'];
    function items() { return reviewRef ? reviewRef.items : []; }
    function renderAll() {
        var src = sourceText();
        if (elFeatSrc) { elFeatSrc.textContent = src; elFeatSrc.style.display = src ? '' : 'none'; }
        if (elSource) elSource.textContent = src || 'no source recorded';
        var by = { error: 0, warning: 0, notice: 0, info: 0 };
        items().forEach(function (it) { if (by[it.level] !== undefined) by[it.level]++; });
        if (elTab) {
            var bad = by.error + by.warning;
            elTab.textContent = reviewRef ? (by.error ? '!' : '✓') : '✓';
            elTab.className = reviewRef ? (by.error ? 'rv-bad' : by.warning ? 'rv-warn' : 'rv-ok') : '';
            elTab.title = reviewRef ? 'Checks: ' + by.error + ' error(s), ' + by.warning + ' warning(s), ' + by.notice + ' notice(s), ' + by.info + ' info' : 'Checks (no review in the features file)';
            if (elPanel) elPanel.style.display = reviewRef || pipeMode() ? '' : 'none';
            elTab.setAttribute('data-count', bad ? String(bad) : '');
        }
        if (elSummary) {
            elSummary.innerHTML = '';
            LEVELS.forEach(function (lv) {
                var b = document.createElement('button');
                b.className = 'rv-chip rv-' + lv + (levelOn[lv] ? ' on' : '');
                b.textContent = by[lv] + ' ' + (lv === 'info' ? 'info' : lv + (by[lv] === 1 ? '' : 's'));
                b.title = 'Show / hide the ' + lv + ' checks';
                b.addEventListener('click', function () { levelOn[lv] = !levelOn[lv]; renderAll(); });
                elSummary.appendChild(b);
            });
        }
        if (!elList) return;
        elList.innerHTML = '';
        if (!reviewRef) { elList.innerHTML = '<div class="gr-empty">No checks in the features file (a CAESAR / pipe model export writes them)</div>'; return; }
        var list = items().filter(function (it) { return levelOn[it.level] !== false; });
        if (!list.length) { elList.innerHTML = '<div class="gr-empty">' + (items().length ? 'Nothing at the levels shown' : 'No findings: the model passed every check') + '</div>'; return; }
        list.forEach(function (it) {
            var row = document.createElement('div');
            row.className = 'rv-row rv-' + it.level + (it.id === selected ? ' sel' : '');
            var dot = document.createElement('span');
            dot.className = 'rv-dot';
            row.appendChild(dot);
            var body = document.createElement('div');
            body.className = 'rv-body';
            var txt = document.createElement('div');
            txt.className = 'rv-text';
            txt.textContent = it.text;
            body.appendChild(txt);
            var meta = [];
            if (it.code) meta.push(it.code);
            if (it.nodeIds && it.nodeIds.length) meta.push('node' + (it.nodeIds.length > 1 ? 's ' : ' ') + it.nodeIds.slice(0, 6).join(', ') + (it.nodeIds.length > 6 ? ' …' : ''));
            if (it.elements && it.elements.length) meta.push('element' + (it.elements.length > 1 ? 's ' : ' ') + it.elements.slice(0, 6).join(', ') + (it.elements.length > 6 ? ' …' : ''));
            var m = document.createElement('div');
            m.className = 'rv-meta';
            m.textContent = meta.join(' · ');
            body.appendChild(m);
            row.appendChild(body);
            if (locatable(it)) {
                row.title = 'Click: fly to it';
                row.addEventListener('click', function () { selected = it.id; locate(it); renderAll(); });
            } else row.classList.add('rv-static');
            elList.appendChild(row);
        });
    }

    function locatable(it) { return !!((it.nodeIds && it.nodeIds.length) || (it.members && it.members.length)); }
    function points(it) {
        var pts = [], segs = [];
        if (!view) return { pts: pts, segs: segs };
        var nd = view.nodes, REC = view.elemRecordU32;
        (it.nodeIds || []).forEach(function (id) {
            var ni = nodeIndex.get(id);
            if (ni !== undefined) pts.push(new THREE.Vector3(nd[ni * 3], nd[ni * 3 + 1], nd[ni * 3 + 2]));
        });
        (it.members || []).forEach(function (m) {
            (m.ids || []).forEach(function (id) {
                var b = beamIndex.get(id);
                if (b === undefined) return;
                var a = view.elems[b * REC + 1], c = view.elems[b * REC + 2];
                segs.push([new THREE.Vector3(nd[a * 3], nd[a * 3 + 1], nd[a * 3 + 2]), new THREE.Vector3(nd[c * 3], nd[c * 3 + 1], nd[c * 3 + 2])]);
            });
        });
        return { pts: pts, segs: segs };
    }
    function modelSpan() {
        if (!view) return 1;
        var box = new THREE.Box3(), nd = view.nodes, p = new THREE.Vector3();
        for (var i = 0; i < nd.length; i += 3) box.expandByPoint(p.set(nd[i], nd[i + 1], nd[i + 2]));
        var s = box.getSize(new THREE.Vector3());
        return Math.max(s.x, s.y, s.z) || 1;
    }
    // fly to an item's nodes and beams and flash them
    function locate(it) {
        var g = points(it);
        if (!g.pts.length && !g.segs.length) return false;
        var box = new THREE.Box3();
        g.pts.forEach(function (p) { box.expandByPoint(p); });
        g.segs.forEach(function (s) { box.expandByPoint(s[0]); box.expandByPoint(s[1]); });
        var c = box.getCenter(new THREE.Vector3()), span = modelSpan();
        var r = Math.max(box.getSize(new THREE.Vector3()).length() / 2, span * 0.08);
        if (typeof tweenFocusTo === 'function' && typeof camera !== 'undefined' && camera) {
            tweenFocusTo(c);
            if (camera.isPerspectiveCamera && typeof focusTween !== 'undefined' && focusTween) {
                var dir = camera.position.clone().sub(controls.target);
                if (dir.lengthSq() < 1e-12) dir.set(0.5, 0.45, 0.9);
                dir.normalize();
                focusTween.camFrom = camera.position.clone();
                focusTween.camTo = c.clone().addScaledVector(dir, 1.25 * r / Math.sin(camera.fov * Math.PI / 360));
                focusTween.duration = 600;
            }
        }
        if (typeof startHighlight === 'function') {
            var group = new THREE.Group(), mats = [];
            var rad = span * 0.006;
            g.pts.slice(0, 400).forEach(function (p) {
                var mat = new THREE.MeshBasicMaterial({ color: 0xffe14d, depthTest: false, depthWrite: false, transparent: true, opacity: 0 });
                var m = new THREE.Mesh(new THREE.SphereGeometry(rad, 16, 10), mat);
                m.position.copy(p); m.renderOrder = 6;
                group.add(m); mats.push({ material: mat, peak: 0.95 });
            });
            g.segs.slice(0, 600).forEach(function (s) {
                var d = s[1].clone().sub(s[0]), len = d.length();
                if (!(len > 0)) return;
                var mat = new THREE.MeshBasicMaterial({ color: 0xffe14d, depthTest: false, depthWrite: false, transparent: true, opacity: 0 });
                var m = new THREE.Mesh(new THREE.CylinderGeometry(rad * 0.6, rad * 0.6, len, 8), mat);
                m.position.copy(s[0]).lerp(s[1], 0.5);
                m.quaternion.setFromUnitVectors(new THREE.Vector3(0, 1, 0), d.normalize());
                m.renderOrder = 6;
                group.add(m); mats.push({ material: mat, peak: 0.8 });
            });
            startHighlight(group, mats);
        }
        if (typeof log === 'function' && it.text) log('Check: ' + it.text.slice(0, 160));
        return true;
    }

    // ---- properties ----------------------------------------------------------
    function u(k) { return units[k] ? ' ' + units[k] : ''; }
    function propOf(beam) { return beamProp ? beamProp.get(beam) || null : null; }
    function summaryOf(it) {
        var v = it.values || {}, parts = [];
        parts.push(it.kind + ' ' + it.from + '-' + it.to + (it.name ? ' ' + it.name : ''));
        if (v.od !== undefined) parts.push(f(v.od, 4) + ' x ' + f(v.wall, 4) + u('diameter'));
        if (v.bend) parts.push('R ' + (v.bend.named || f(v.bend.radius, 3)));
        if (v.T) parts.push('T1 ' + f(v.T[0], 2) + u('temperature'));
        if (v.P) parts.push('P1 ' + f(v.P[0], 2) + u('pressure'));
        if (v.material) parts.push('Mat ' + v.material);
        if (v.insul) parts.push('insul ' + f(v.insul, 3));
        if (v.uniform) parts.push('U' + v.uniform.map(function (x) { return x.v; }).join('/'));
        if (v.wind) parts.push('wind ' + f(v.wind.shape, 3));
        return parts.join(' · ');
    }
    function clearReadout() { if (elPropRow) elPropRow.style.display = 'none'; }
    function fillBeamReadout(beam) {
        var it = propOf(beam);
        if (!elPropRow || !elProps) return;
        if (!it) { elPropRow.style.display = 'none'; return; }
        elProps.textContent = summaryOf(it);
        elPropRow.style.display = '';
    }
    function showBeamCard(hit) {
        var it = propOf(hit.elem);
        if (!it || typeof elCalcCard === 'undefined' || !elCalcCard) return false;
        var v = it.values || {};
        elCalcCard.style.display = '';
        elCalcCard.innerHTML = '';
        var head = document.createElement('div');
        head.className = 'cc-head';
        head.textContent = 'Element ' + it.element + ': ' + it.from + '-' + it.to + '  ' + it.kind + (it.name ? ' (' + it.name + ')' : '') + (it.line ? ', line ' + it.line : '');
        elCalcCard.appendChild(head);
        var t;
        ccSub(elCalcCard, 'Geometry');
        t = document.createElement('table');
        ccRow(t, 'DX DY DZ' + u('length'), [v.dx, v.dy, v.dz].map(function (x) { return f(x, 4); }).join('  '));
        ccRow(t, 'length' + u('length'), f(v.length, 4));
        ccRow(t, 'OD x wall' + u('diameter'), f(v.od, 4) + ' x ' + f(v.wall, 4));
        if (v.insul) ccRow(t, 'insulation' + u('wall'), f(v.insul, 4));
        if (v.corr) ccRow(t, 'corrosion' + u('wall'), f(v.corr, 4));
        elCalcCard.appendChild(t);
        if (v.T || v.P) {
            ccSub(elCalcCard, 'Temperature' + u('temperature') + ' / pressure' + u('pressure'));
            t = document.createElement('table');
            var n = Math.max((v.T || []).length, (v.P || []).length);
            for (var k = 0; k < n; k++) {
                var tv = v.T && v.T[k] !== undefined ? v.T[k] : 0, pv = v.P && v.P[k] !== undefined ? v.P[k] : 0;
                if (!tv && !pv) continue;
                ccRow(t, 'T' + (k + 1) + ' / P' + (k + 1), f(tv, 3) + ' / ' + f(pv, 3));
            }
            elCalcCard.appendChild(t);
        }
        ccSub(elCalcCard, 'Material');
        t = document.createElement('table');
        ccRow(t, 'material', v.material ? String(v.material) : '—');
        if (v.emod) ccRow(t, 'E' + u('modulus'), f(v.emod, 4));
        if (v.poisson) ccRow(t, 'Poisson', f(v.poisson, 3));
        if (v.pipeDensity) ccRow(t, 'pipe density' + u('pipeDensity'), f(v.pipeDensity, 5));
        if (v.insulDensity) ccRow(t, 'insulation density' + u('insulDensity'), f(v.insulDensity, 5));
        if (v.fluidDensity) ccRow(t, 'fluid density' + u('fluidDensity'), f(v.fluidDensity, 5));
        elCalcCard.appendChild(t);
        if (v.bend || v.rigid || v.expJoint || v.reducer) {
            ccSub(elCalcCard, it.kind);
            t = document.createElement('table');
            if (v.bend) {
                ccRow(t, 'radius' + u('length'), (v.bend.named ? v.bend.named + ' (' + f(v.bend.radius, 4) + ')' : f(v.bend.radius, 4)) + (v.bend.noRadius ? ' -- CAESAR reads no radius' : ''));
                if (v.bend.type) ccRow(t, 'type', String(v.bend.type));
                if (v.bend.nodes && v.bend.nodes.length) ccRow(t, 'bend nodes', v.bend.nodes.map(function (a) { return a[1] + ' @ ' + a[0]; }).join(', '));
            }
            if (v.rigid) ccRow(t, 'weight' + u('force'), f(v.rigid.weight, 4));
            if (v.expJoint) {
                ccRow(t, 'axial K' + u('stiffness'), f(v.expJoint.axial, 4));
                ccRow(t, 'transverse K' + u('stiffness'), f(v.expJoint.transverse, 4));
                ccRow(t, 'bending K' + u('rotStiffness'), f(v.expJoint.bending, 4));
                ccRow(t, 'torsion K' + u('rotStiffness'), f(v.expJoint.torsion, 4));
                if (v.expJoint.effectiveId) ccRow(t, 'effective ID' + u('diameter'), f(v.expJoint.effectiveId, 4));
            }
            if (v.reducer) ccRow(t, 'to OD x wall' + u('diameter'), f(v.reducer.od2, 4) + ' x ' + f(v.reducer.wall2, 4));
            elCalcCard.appendChild(t);
        }
        // loads on the element and at its ends
        var loads = [];
        if (window.FEALoads && FEALoads.itemsAt) {
            loads = FEALoads.itemsAt(null, it.element).concat(FEALoads.itemsAt(it.from, 0)).concat(FEALoads.itemsAt(it.to, 0));
        }
        if (loads.length) {
            ccSub(elCalcCard, 'Loads');
            t = document.createElement('table');
            var seen = [];
            loads.forEach(function (ld) {
                if (seen.indexOf(ld) >= 0) return;
                seen.push(ld);
                if (ld.kind === 'wind') { ccRow(t, ld.name, (ld.wind && ld.wind.type) + ' shape ' + f(ld.wind && ld.wind.shape, 3)); return; }
                (ld.vectors || []).forEach(function (vc) {
                    var pre = ld.kind === 'force' ? 'F' : ld.kind === 'displacement' ? 'D' : 'U';
                    ccRow(t, ld.name + ' ' + pre + vc.v, FEALoads.vectorText(ld, vc) + (vc.g ? ' (g)' : ''));
                });
            });
            elCalcCard.appendChild(t);
        }
        // the checks about it
        var mine = items().filter(function (r) {
            return (r.elements || []).indexOf(it.element) >= 0 || (r.nodeIds || []).indexOf(it.from) >= 0 || (r.nodeIds || []).indexOf(it.to) >= 0;
        });
        if (mine.length) {
            ccSub(elCalcCard, 'Checks');
            mine.forEach(function (r) {
                var d = document.createElement('div');
                d.className = 'rv-card rv-' + r.level;
                d.textContent = r.level + ': ' + r.text;
                elCalcCard.appendChild(d);
            });
        }
        return true;
    }

    // ---- node numbers ----------------------------------------------------------
    function syncNodeLabels() {
        if (!window.FEALabels) return;
        if (!nodeNums || !view) { FEALabels.set('nodes', []); return; }
        var list = [], nd = view.nodes, skip = pipeMode();
        for (var i = 0; i < view.nodeIds.length; i++) {
            var id = view.nodeIds[i];
            if (skip && id >= SYNTHETIC_FROM) continue;
            list.push({ pos: new THREE.Vector3(nd[i * 3], nd[i * 3 + 1], nd[i * 3 + 2]), text: String(id), cls: 'lbl-node', priority: 1 });
        }
        FEALabels.set('nodes', list);
    }

    // ---- UI ----------------------------------------------------------------------
    if (elTab && elPanel) elTab.addEventListener('click', function () { elPanel.classList.toggle('collapsed'); });
    if (elNodeNums) elNodeNums.addEventListener('change', function () {
        nodeNums = this.checked;
        try { localStorage.setItem('pluto.nodeNums', nodeNums ? '1' : '0'); } catch (e) {}
        syncNodeLabels();
    });
    renderAll();

    return {
        onModelLoaded: onModelLoaded,
        onModelCleared: onModelCleared,
        onFeatures: onFeatures,
        hasProperties: function () { return !!beamProp; },
        fillBeamReadout: fillBeamReadout,
        clearReadout: clearReadout,
        showBeamCard: showBeamCard,
        locate: locate,
        sourceText: sourceText,
        // test hooks (viewer/tests/test_review.js)
        _items: items,
        _prop: propOf,
        _summary: summaryOf,
        _points: points,
        _setNodeNums: function (on) { nodeNums = !!on; syncNodeLabels(); },
        _setLevel: function (lv, on) { levelOn[lv] = !!on; renderAll(); }
    };
})();
