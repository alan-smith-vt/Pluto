// ================================================================
// files.js  --  the ONE way files come in and the ONE way features go out.
//
// Standalone-HTML viewer: no server writes anything, so the model (.bin,
// big, write-once) is read through a file picker and the features sidecar
// (.json, small, the user's layer) is read the same way and SAVED back by
// the user. Chrome / Edge are the targets: the File System Access API
// (showOpenFilePicker / showSaveFilePicker) keeps a handle to the picked
// sidecar so "Save features" writes straight over it, and a first save
// opens the picker in the folder the inputs came from with the model's
// name filled in. Anything else falls back to a plain <input type=file>
// and a download.
//
// "Choose files" accepts .bin and .json together (Ctrl-click). JSON is
// classified by shape:
//   pluto-features sidecar        -> FEAFeatures.setEnvelope (bound by name to the model)
//   legacy section_cuts.json      -> FEASectionCut.importLegacy   ({cuts:[...]})
//   legacy predicates.json        -> FEAPredicates.importLegacy   ({expressions:[...]} or {groups:[{predicate}]})
// Legacy files merge into the current sidecar (a fresh one is created if
// none is loaded) and mark it unsaved.
//
// Depends on viewer.js: loadModels(entries), log(); FEAFeatures for the
// envelope + dirty state; the ids in index.html's Model section.
// ================================================================

var FEAFiles = (function () {

    var elPick = document.getElementById('feaPick');
    var elInput = document.getElementById('feaFile');
    var elSave = document.getElementById('feaSave');
    var elName = document.getElementById('featName');

    var binHandle = null;       // FileSystemFileHandle of the primary model, if picked with the API
    var featHandle = null;      // handle of the loaded / last-saved sidecar
    var primaryName = '';       // base name of the primary model ("TANK-A")

    var hasFS = typeof window !== 'undefined' && typeof window.showOpenFilePicker === 'function';

    function say(m) { if (typeof log === 'function') log(m); }
    function baseName(n) { return String(n || '').replace(/\.[^.]+$/, ''); }
    function isJson(n) { return /\.json$/i.test(n); }
    // "TANK-A.features.json" and "TANK-A.json" both belong to model "TANK-A"
    function sidecarModel(n) { return baseName(n).replace(/\.features$/i, ''); }

    // ---- classification (pure; tested headlessly) ------------------------
    function classify(obj) {
        if (!obj || typeof obj !== 'object') return 'unknown';
        if (obj.format === 'pluto-features') return 'features';
        if (Array.isArray(obj.cuts)) return 'legacy-cuts';
        if (Array.isArray(obj.expressions)) return 'legacy-predicates';
        if (Array.isArray(obj.groups) && obj.groups.some(function (g) { return g && g.predicate; })) return 'legacy-predicates';
        return 'unknown';
    }

    // ---- picking ---------------------------------------------------------
    async function pick() {
        if (!hasFS) { if (elInput) elInput.click(); return; }
        var handles;
        try {
            handles = await window.showOpenFilePicker({
                multiple: true,
                startIn: binHandle || featHandle || 'documents',
                types: [{ description: 'Pluto model + features', accept: { 'application/octet-stream': ['.bin'], 'application/json': ['.json'] } }]
            });
        } catch (err) { return; }                       // cancelled
        var files = [];
        for (var i = 0; i < handles.length; i++) files.push({ file: await handles[i].getFile(), handle: handles[i] });
        await ingest(files);
    }

    // files: [{ file: File, handle?: FileSystemFileHandle }]
    async function ingest(files) {
        var bins = files.filter(function (f) { return !isJson(f.file.name); });
        var jsons = files.filter(function (f) { return isJson(f.file.name); });
        if (bins.length) {
            var entries = bins.map(function (f) { return { file: f.file, name: baseName(f.file.name) }; });
            await loadModels(entries);
            primaryName = entries[0].name;
            binHandle = bins[0].handle || null;
            featHandle = null;
        }
        // a sidecar first (so legacy files merge into it), matched to the model by name
        var sidecars = jsons.filter(function (f) { return f._kind === undefined; });
        for (var j = 0; j < sidecars.length; j++) {
            var text = await sidecars[j].file.text(), obj;
            try { obj = JSON.parse(text); } catch (err) { say('Files: ' + sidecars[j].file.name + ' is not valid JSON.'); sidecars[j]._kind = 'bad'; continue; }
            sidecars[j]._kind = classify(obj);
            sidecars[j]._obj = obj;
        }
        var feats = sidecars.filter(function (f) { return f._kind === 'features'; });
        if (feats.length) {
            var pickF = feats[0];
            for (var k = 0; k < feats.length; k++) if (primaryName && sidecarModel(feats[k].file.name).toLowerCase() === primaryName.toLowerCase()) pickF = feats[k];
            if (feats.length > 1) say('Files: ' + feats.length + ' sidecars picked; using ' + pickF.file.name + '.');
            FEAFeatures.setEnvelope(pickF._obj, pickF.file.name);
            featHandle = pickF.handle || null;
        }
        sidecars.forEach(function (f) {
            if (f._kind === 'legacy-cuts') {
                if (window.FEASectionCut && FEASectionCut.importLegacy) FEASectionCut.importLegacy(f._obj);
            } else if (f._kind === 'legacy-predicates') {
                if (window.FEAPredicates && FEAPredicates.importLegacy) FEAPredicates.importLegacy(f._obj);
            } else if (f._kind === 'unknown') {
                say('Files: ' + f.file.name + ' is neither a features sidecar nor a legacy cuts / predicates file; ignored.');
            }
        });
        syncUI();
    }

    // ---- saving ----------------------------------------------------------
    function targetName() {
        var n = window.FEAFeatures ? FEAFeatures.fileName() : '';
        n = String(n || '').replace(/ \(unsaved\)$/, '');
        if (!n || n === 'untitled.features.json') n = (primaryName || 'model') + '.features.json';
        return n;
    }

    async function save() {
        if (!window.FEAFeatures) return;
        var json = FEAFeatures.exportJson();
        if (!json) { say('Features: nothing to save.'); return; }
        var name = targetName();
        if (hasFS) {
            try {
                if (!featHandle) {
                    featHandle = await window.showSaveFilePicker({
                        suggestedName: name,
                        startIn: binHandle || 'documents',
                        types: [{ description: 'Pluto features sidecar', accept: { 'application/json': ['.json'] } }]
                    });
                }
                var w = await featHandle.createWritable();
                await w.write(json);
                await w.close();
                FEAFeatures.markSaved(featHandle.name);
                say('Features: saved ' + featHandle.name + '.');
                syncUI();
                return;
            } catch (err) {
                if (err && err.name === 'AbortError') return;      // cancelled
                say('Features: save through the file picker failed (' + err.message + '); downloading instead.');
            }
        }
        var a = document.createElement('a');
        a.href = URL.createObjectURL(new Blob([json], { type: 'application/json' }));
        a.download = name;
        document.body.appendChild(a); a.click(); document.body.removeChild(a);
        setTimeout(function () { URL.revokeObjectURL(a.href); }, 1000);
        FEAFeatures.markSaved(name);
        syncUI();
    }

    // ---- UI --------------------------------------------------------------
    function syncUI() {
        if (!window.FEAFeatures) return;
        var dirty = FEAFeatures.isDirty();
        var has = !!FEAFeatures.envelope();
        if (elSave) {
            elSave.disabled = !has;
            elSave.classList.toggle('dirty', dirty);
            elSave.title = !has ? 'No features to save yet' :
                featHandle ? 'Write the features sidecar back to ' + featHandle.name :
                'Save the features sidecar as ' + targetName() + (hasFS ? ' (picker opens beside the model)' : ' (download)');
        }
        if (elName) {
            var n = has ? FEAFeatures.fileName().replace(/ \(unsaved\)$/, '') : 'no features file';
            elName.textContent = n + (dirty ? '  *' : '');
            elName.classList.toggle('dirty', dirty);
        }
    }

    if (elPick) elPick.addEventListener('click', function () { pick(); });
    if (elSave) elSave.addEventListener('click', function () { save(); });
    if (elInput) elInput.addEventListener('change', function (e) {
        var files = Array.prototype.slice.call(e.target.files).map(function (f) { return { file: f }; });
        e.target.value = '';
        if (files.length) ingest(files);
    });
    // Ctrl+S saves the features, the same as the button.
    if (typeof window.addEventListener === 'function') window.addEventListener('keydown', function (e) {
        if ((e.ctrlKey || e.metaKey) && !e.shiftKey && (e.key === 's' || e.key === 'S')) { e.preventDefault(); save(); }
    });

    return {
        pick: pick,
        ingest: ingest,
        save: save,
        syncUI: syncUI,
        classify: classify,
        hasFileSystemAccess: function () { return hasFS; },
        // test hooks
        _sidecarModel: sidecarModel
    };
})();
