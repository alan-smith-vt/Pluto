// ================================================================
// features.js  --  features sidecar (*.features.json) in the viewer.
// Spec: vault/format/features-sidecar.md
//
// First consumer: `groups`. Each group has a color and a member list
// ({domain, ids}); the viewer resolves real IDs -> element indices per
// domain, writes a per-element category index into the `catIdx`
// vertex attribute of the shell and beam meshes, and (when the single
// "Color by groups" switch is on) the shaders paint by palette instead
// of by field. Unknown sections of the envelope are kept verbatim so
// a later export round-trips them.
//
// Precedence: an element in several groups takes the LAST enabled group
// that lists it (envelope order) -- so "all W shapes grey" first, then
// "pipes by size" refine on top. The Groups tab (2026-09-03) exposes that:
// per-group enable (persisted as `hidden: true` on the item), colour edit,
// and drag reorder (mutates envelope.groups.items order, so Export keeps it).
// ================================================================

var FEAFeatures = (function () {

    var envelope = null;      // parsed sidecar (or null)
    var fileName = '';
    var enabled = false;      // the single switch
    var resolved = null;      // { shells: Float32Array|null, beams: ..., counts, unmatched }
    var palette = null;       // THREE.DataTexture of group colors
    var groupList = [];       // [{name,color,rgb,count}] in envelope order

    var elFile   = document.getElementById('featFile');
    var elToggle = document.getElementById('featGroups');
    var elName   = document.getElementById('featName');
    var elHint   = document.getElementById('featHint');
    var elPanel  = document.getElementById('grPanel');
    var elTab    = document.getElementById('grTab');
    var elList   = document.getElementById('grList');
    var elAll    = document.getElementById('grAll');
    var elNone   = document.getElementById('grNone');
    var elInvert = document.getElementById('grInvert');
    var elExport = document.getElementById('grExport');

    // ---- helpers --------------------------------------------------------
    function hexToRgb(hex) {
        var m = /^#?([0-9a-f]{6})$/i.exec(String(hex || ''));
        if (!m) return [200, 200, 200];
        var v = parseInt(m[1], 16);
        return [(v >> 16) & 255, (v >> 8) & 255, v & 255];
    }
    function autoColor(i) {
        var c = FEAShaders.categoryColor(i);
        return [c[0], c[1], c[2]];
    }

    function idMap(view) {
        if (view._featIdMap) return view._featIdMap;
        var m = new Map();
        for (var i = 0; i < view.elemIds.length; i++) m.set(view.elemIds[i], i);
        view._featIdMap = m;
        return m;
    }

    // ---- load / validate ------------------------------------------------
    async function loadFile(file) {
        var text = await file.text();
        var obj;
        try { obj = JSON.parse(text); }
        catch (err) { log('Features: ' + file.name + ' is not valid JSON (' + err.message + ').'); return; }
        setEnvelope(obj, file.name);
    }

    function setEnvelope(obj, name) {
        if (!obj || obj.format !== 'pluto-features') {
            log('Features: not a pluto-features file (format="' + (obj && obj.format) + '").');
            return;
        }
        envelope = obj;
        fileName = name || 'features.json';
        if (elName) elName.textContent = fileName;
        checkBinding();
        resolve();
        if (elToggle) elToggle.disabled = !resolved;
        sync();
        var g = (obj.groups && obj.groups.items) ? obj.groups.items.length : 0;
        log('Features: ' + fileName + ' loaded (' + g + ' group(s)' +
            (resolved && resolved.unmatched ? ', ' + resolved.unmatched + ' unmatched ID(s)' : '') +
            (resolved && resolved.unresolvedPredicates ? ', ' + resolved.unresolvedPredicates +
                ' predicate member(s) unresolved (predicate module not loaded)' : '') + ').');
        // Hand the predicate section to the predicate module; it calls back
        // into refresh() so predicate-member groups resolve on second pass.
        if (window.FEAPredicates && FEAPredicates.onEnvelope) FEAPredicates.onEnvelope(envelope);
    }

    // Minimal empty envelope bound to the loaded model, so features can be
    // authored in-viewer without importing a sidecar first.
    function ensureEnvelope() {
        if (envelope) return envelope;
        var u = feaModel && feaModel.unified;
        var obj = {
            format: 'pluto-features',
            version: 1,
            model: {
                modelId: (u && u.meta && u.meta.modelId) || '',
                geometryHash: (u && u.geometryHash) || ''
            },
            groups: { version: 1, items: [] },
            predicates: { version: 1, items: [] },
            sectionCuts: { version: 1, items: [] }
        };
        envelope = obj;
        fileName = 'untitled.features.json';
        if (elName) elName.textContent = fileName + ' (unsaved)';
        checkBinding();
        return envelope;
    }

    // Re-resolve groups + repaint (public: predicate edits call this).
    function refresh() {
        resolve();
        if (elToggle) elToggle.disabled = !resolved;
        sync();
    }

    function checkBinding() {
        if (!envelope || !feaModel || !elHint) return;
        var u = feaModel.unified;
        var want = envelope.model && envelope.model.geometryHash;
        var have = u && u.geometryHash;
        if (want && have && want !== have) {
            elHint.textContent = '⚠ geometryHash differs from the loaded model — members may not resolve.';
            elHint.style.color = '#ffb347';
        } else if (want && !have) {
            elHint.textContent = 'model has no geometryHash (v3 file) — binding unverified.';
            elHint.style.color = '';
        } else {
            elHint.textContent = want ? 'bound to loaded model (hash match).' : 'no model binding in file.';
            elHint.style.color = '';
        }
    }

    // Predicate -> [{domain, ids}] or null when no evaluator is available.
    // Replaced by the predicate module when it lands.
    function resolvePredicateMembers(predicateId) {
        if (window.FEAPredicates && FEAPredicates.resolveMembers) {
            return FEAPredicates.resolveMembers(predicateId, envelope);
        }
        return null;
    }

    // ---- resolve groups -> per-element category per domain --------------
    function resolve() {
        resolved = null;
        groupList = [];
        if (palette) { palette.dispose(); palette = null; }
        if (!envelope || !feaModel) return;
        var items = (envelope.groups && Array.isArray(envelope.groups.items)) ? envelope.groups.items : [];
        if (items.length === 0) return;

        var views = { shell: feaModel, beam: window.FEABeams ? FEABeams.view() : null };
        var out = { shell: null, beam: null, unmatched: 0 };
        Object.keys(views).forEach(function (fam) {
            var v = views[fam];
            if (!v) return;
            var arr = new Float32Array(v.header.nElements);
            arr.fill(-1);
            out[fam] = arr;
        });
        var domainByName = {};
        if (feaModel.unified) feaModel.unified.domains.forEach(function (d) { domainByName[d.name] = d.family; });

        var rgb = [];
        items.forEach(function (g, gi) {
            var color = g.color ? hexToRgb(g.color) : autoColor(gi);
            rgb.push(color);
            var count = 0, nodeCount = 0;
            var hidden = !!g.hidden;
            // Work on a COPY: predicate members expand into explicit entries
            // appended to the queue; the envelope itself is never mutated.
            var members = (Array.isArray(g.members) ? g.members : (g.members ? [g.members] : [])).slice();
            for (var mi = 0; mi < members.length; mi++) (function (m) {
                if (m.predicateId) {
                    // Runtime members: resolved from the predicate tree once the
                    // predicate module is ported (hook: resolvePredicateMembers).
                    var ids = resolvePredicateMembers(m.predicateId);
                    if (!ids) { out.unresolvedPredicates = (out.unresolvedPredicates || 0) + 1; return; }
                    ids.forEach(function (pm) { members.push(pm); });   // queued, visited by the for loop
                    return;
                }
                var fam = domainByName[m.domain] || m.domain || 'shell';
                if (fam === 'shells') fam = 'shell';
                if (fam === 'beams') fam = 'beam';
                if (fam === 'nodes' || (m.nodeIds && m.nodeIds.length)) {
                    nodeCount += (m.nodeIds || []).length;    // node groups: listed, not painted
                    return;
                }
                var v = views[fam], arr = out[fam];
                if (!v || !arr) { out.unmatched += (m.ids || []).length; return; }
                var map = idMap(v);
                (m.ids || []).forEach(function (id) {
                    var idx = map.get(id);
                    if (idx === undefined) { out.unmatched++; return; }
                    count++;
                    if (!hidden) arr[idx] = gi;
                });
            })(members[mi]);
            groupList.push({ name: g.name || ('Group ' + (gi + 1)), color: g.color, rgb: color,
                             count: count, nodeCount: nodeCount, hidden: hidden, painted: 0,
                             tags: Array.isArray(g.tags) ? g.tags : [] });
        });
        // painted = elements whose final category is this group (after precedence)
        ['shell', 'beam'].forEach(function (fam) {
            var arr = out[fam];
            if (!arr) return;
            for (var i = 0; i < arr.length; i++) if (arr[i] >= 0) groupList[arr[i]].painted++;
        });
        resolved = out;
        palette = FEAShaders.makePaletteTexture(rgb);
    }

    // ---- apply to materials -----------------------------------------
    function applyUniforms(mat, on) {
        if (!mat || !mat.uniforms.uGroupMode) return;
        mat.uniforms.uGroupMode.value = on ? 1 : 0;
        if (on) {
            mat.uniforms.groupPalette.value = palette;
            mat.uniforms.uGroupCount.value = groupList.length;
        }
    }

    function sync() {
        var on = enabled && !!resolved;
        if (feaBuild && resolved) {
            FEAAttributes.updateCatIdx(feaBuild.geometry, feaBuild.elemNCount, null, resolved.shell);
        }
        if (window.FEABeams && FEABeams.build() && resolved) {
            var bb = FEABeams.build();
            FEAAttributes.updateCatIdx(bb.geometry, null, bb, resolved.beam);
        }
        applyUniforms(feaMaterial, on);
        if (window.FEABeams && FEABeams.mesh()) applyUniforms(FEABeams.mesh().material, on);
        if (elToggle) elToggle.checked = enabled;
        drawLegend(on);
        if (typeof updateViewCaption === 'function') updateViewCaption();
        needsRender = true;
    }

    function drawLegend(on) { renderList(on); }

    // ---- Groups tab list --------------------------------------------------
    var dragFrom = -1;
    function items() { return (envelope && envelope.groups && Array.isArray(envelope.groups.items)) ? envelope.groups.items : []; }
    function hex2(c) { return ('0' + c.toString(16)).slice(-2); }

    function renderList(on) {
        if (!elList) return;
        elList.innerHTML = '';
        var list = items();
        var can = list.length > 0 && !!feaModel;
        [elAll, elNone, elInvert].forEach(function (b) { if (b) b.disabled = !can; });
        if (!list.length) {
            elList.innerHTML = '<div class="gr-empty">' + (envelope ? 'No groups in file' : 'No features file') + '</div>';
            return;
        }
        list.forEach(function (g, gi) {
            var info = groupList[gi] || { name: g.name, rgb: [200, 200, 200], count: 0, nodeCount: 0, painted: 0, hidden: !!g.hidden, tags: [] };
            var isNodes = info.count === 0 && info.nodeCount > 0;
            var row = document.createElement('div');
            row.className = 'gr-row' + (info.hidden ? ' off' : '') + (isNodes ? ' nodes' : '');
            row.draggable = true;

            var cb = document.createElement('input');
            cb.type = 'checkbox'; cb.className = 'gr-check'; cb.checked = !info.hidden;
            cb.title = isNodes ? 'Node group: listed for reference, nothing to paint' : 'Paint this group';
            cb.disabled = isNodes;
            cb.addEventListener('change', function () { setHidden(gi, !this.checked); });

            var sw = document.createElement('input');
            sw.type = 'color'; sw.className = 'gr-swatch';
            sw.value = '#' + hex2(info.rgb[0]) + hex2(info.rgb[1]) + hex2(info.rgb[2]);
            sw.title = 'Group colour (saved on Export)';
            sw.addEventListener('input', function () { g.color = this.value; refresh(); });
            sw.addEventListener('mousedown', function (e) { e.stopPropagation(); });

            var name = document.createElement('span');
            name.className = 'gr-name'; name.textContent = info.name;
            name.title = info.name + (info.tags.length ? '  [' + info.tags.join(', ') + ']' : '');

            var cnt = document.createElement('span');
            cnt.className = 'gr-count';
            if (isNodes) cnt.textContent = info.nodeCount + ' nodes';
            else {
                cnt.textContent = info.painted + '/' + info.count;
                if (!info.hidden && info.count > 0 && info.painted < info.count) {
                    cnt.classList.add('shadowed');
                    cnt.title = (info.count - info.painted) + ' member(s) painted by a group lower in the list';
                }
            }
            row.appendChild(cb); row.appendChild(sw); row.appendChild(name); row.appendChild(cnt);

            row.addEventListener('dragstart', function (e) {
                dragFrom = gi; row.classList.add('dragging');
                e.dataTransfer.effectAllowed = 'move';
                try { e.dataTransfer.setData('text/plain', String(gi)); } catch (err) {}
            });
            row.addEventListener('dragend', function () { dragFrom = -1; clearDrop(); row.classList.remove('dragging'); });
            row.addEventListener('dragover', function (e) {
                if (dragFrom < 0) return;
                e.preventDefault();
                var r = row.getBoundingClientRect(), after = (e.clientY - r.top) > r.height / 2;
                clearDrop(); row.classList.add(after ? 'drop-after' : 'drop-before');
            });
            row.addEventListener('drop', function (e) {
                if (dragFrom < 0) return;
                e.preventDefault();
                var r = row.getBoundingClientRect(), after = (e.clientY - r.top) > r.height / 2;
                moveGroup(dragFrom, gi + (after ? 1 : 0));
            });
            elList.appendChild(row);
        });
        var rest = document.createElement('div');
        rest.className = 'gr-ungrouped';
        rest.innerHTML = '<span class="sw"></span><span>ungrouped' + (on ? '' : ' (colouring off)') + '</span>';
        elList.appendChild(rest);
    }
    function clearDrop() {
        if (!elList) return;
        Array.prototype.forEach.call(elList.querySelectorAll('.gr-row'), function (r) { r.classList.remove('drop-before', 'drop-after'); });
    }
    function setHidden(gi, hidden) {
        var g = items()[gi]; if (!g) return;
        if (hidden) g.hidden = true; else delete g.hidden;
        refresh();
    }
    function setAllHidden(fn) {
        items().forEach(function (g, gi) {
            var info = groupList[gi];
            if (info && info.count === 0 && info.nodeCount > 0) return;   // node groups untouched
            var h = fn(!!g.hidden);
            if (h) g.hidden = true; else delete g.hidden;
        });
        refresh();
    }
    // Move item `from` so it lands at index `to` (index in the ORIGINAL list, before removal).
    function moveGroup(from, to) {
        var list = items();
        if (from < 0 || from >= list.length) return;
        if (to > from) to--;
        if (to === from) return;
        var g = list.splice(from, 1)[0];
        list.splice(to, 0, g);
        refresh();
    }

    // Group name for a (family, element index), or null.
    function groupOf(family, e) {
        if (!resolved || !enabled) return null;
        var arr = resolved[family];
        if (!arr || e < 0 || e >= arr.length) return null;
        var gi = arr[e];
        return gi >= 0 ? groupList[gi].name : null;
    }

    // ---- hooks from viewer.js -------------------------------------------
    function onModelLoaded() {
        if (feaModel) feaModel._featIdMap = null;
        checkBinding();
        resolve();
        if (elToggle) elToggle.disabled = !resolved;
        sync();
    }
    function onModelCleared() {
        resolved = null;
        if (palette) { palette.dispose(); palette = null; }
    }

    // ---- export ---------------------------------------------------------
    function exportJson() {
        if (!envelope) return null;
        return JSON.stringify(envelope, null, 2);
    }

    // ---- UI -------------------------------------------------------------
    if (elFile) elFile.addEventListener('change', function (e) {
        var f = e.target.files && e.target.files[0];
        if (f) loadFile(f);
        e.target.value = '';
    });
    if (elToggle) elToggle.addEventListener('change', function () {
        enabled = this.checked;
        sync();
    });
    if (elTab && elPanel) elTab.addEventListener('click', function () { elPanel.classList.toggle('collapsed'); });
    if (elAll) elAll.addEventListener('click', function () { setAllHidden(function () { return false; }); });
    if (elNone) elNone.addEventListener('click', function () { setAllHidden(function () { return true; }); });
    if (elInvert) elInvert.addEventListener('click', function () { setAllHidden(function (h) { return !h; }); });
    if (elExport) elExport.addEventListener('click', function () {
        var json = exportJson();
        if (!json) { log('Features: nothing to export.'); return; }
        var a = document.createElement('a');
        a.href = URL.createObjectURL(new Blob([json], { type: 'application/json' }));
        a.download = fileName.replace(/ \(unsaved\)$/, '') || 'features.json';
        document.body.appendChild(a); a.click(); document.body.removeChild(a);
        setTimeout(function () { URL.revokeObjectURL(a.href); }, 1000);
    });

    return {
        loadFile: loadFile,
        setEnvelope: setEnvelope,
        ensureEnvelope: ensureEnvelope,
        refresh: refresh,
        fileName: function () { return fileName; },
        onModelLoaded: onModelLoaded,
        onModelCleared: onModelCleared,
        sync: sync,
        groupOf: groupOf,
        isOn: function () { return enabled && !!resolved; },
        envelope: function () { return envelope; },
        // Recenter offset written by the exporter (units.worldOffset, "x y z" in
        // FILE units); add to picked coordinates to recover world/plant position.
        worldOffset: function () {
            var s = envelope && envelope.units && envelope.units.worldOffset;
            if (!s) return null;
            var p = String(s).trim().split(/\s+/).map(Number);
            return (p.length === 3 && p.every(isFinite)) ? p : null;
        },
        exportJson: exportJson,
        // test hooks (viewer/tests/test_groups.js)
        _groupList: function () { return groupList; },
        _resolved: function () { return resolved; },
        _moveGroup: moveGroup,
        _setEnabled: function (on) { enabled = !!on; sync(); }
    };
})();
