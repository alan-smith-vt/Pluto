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
// Precedence: an element in several groups takes the LAST group that
// lists it (envelope order) -- so "all W shapes grey" first, then
// "pipes by size" refine on top.
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
    var elLegend = document.getElementById('featLegend');
    var elHint   = document.getElementById('featHint');

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
            var count = 0;
            var members = Array.isArray(g.members) ? g.members : (g.members ? [g.members] : []);
            members.forEach(function (m) {
                if (m.predicateId) {
                    // Runtime members: resolved from the predicate tree once the
                    // predicate module is ported (hook: resolvePredicateMembers).
                    var ids = resolvePredicateMembers(m.predicateId);
                    if (!ids) { out.unresolvedPredicates = (out.unresolvedPredicates || 0) + 1; return; }
                    ids.forEach(function (pm) { members.push(pm); });   // appended, processed in this loop
                    return;
                }
                var fam = domainByName[m.domain] || m.domain || 'shell';
                if (fam === 'shells') fam = 'shell';
                if (fam === 'beams') fam = 'beam';
                var v = views[fam], arr = out[fam];
                if (!v || !arr) { out.unmatched += (m.ids || []).length; return; }
                var map = idMap(v);
                (m.ids || []).forEach(function (id) {
                    var idx = map.get(id);
                    if (idx === undefined) { out.unmatched++; return; }
                    arr[idx] = gi;
                    count++;
                });
            });
            groupList.push({ name: g.name || ('Group ' + (gi + 1)), color: g.color, rgb: color, count: count });
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

    function drawLegend(on) {
        if (!elLegend) return;
        elLegend.innerHTML = '';
        elLegend.style.display = on ? '' : 'none';
        if (!on) return;
        groupList.forEach(function (g) {
            var row = document.createElement('div');
            row.className = 'cat-row';
            var sw = document.createElement('span');
            sw.className = 'cat-swatch';
            sw.style.background = 'rgb(' + g.rgb.join(',') + ')';
            var lab = document.createElement('span');
            lab.textContent = g.name + ' (' + g.count + ')';
            row.appendChild(sw); row.appendChild(lab);
            elLegend.appendChild(row);
        });
        var rest = document.createElement('div');
        rest.className = 'cat-row';
        var sw2 = document.createElement('span');
        sw2.className = 'cat-swatch';
        sw2.style.background = 'rgb(100,102,108)';
        var lab2 = document.createElement('span');
        lab2.textContent = 'ungrouped';
        rest.appendChild(sw2); rest.appendChild(lab2);
        elLegend.appendChild(rest);
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

    return {
        loadFile: loadFile,
        setEnvelope: setEnvelope,
        onModelLoaded: onModelLoaded,
        onModelCleared: onModelCleared,
        sync: sync,
        groupOf: groupOf,
        isOn: function () { return enabled && !!resolved; },
        envelope: function () { return envelope; },
        exportJson: exportJson
    };
})();
