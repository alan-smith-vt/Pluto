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
// Reload / Auto-reload (a model a runner rebuilds, e.g. Build-Pipe.ps1
// -Watch): the model and its sidecar are read again from where they came
// from -- the picked files' handles, or the ?bin= / &features= URLs -- with
// the camera and the load case kept. Auto polls every 2 s and reloads once a
// change has held still for one poll; it waits while the features have
// unsaved edits (a reload would drop them). Files given through the plain
// file input are snapshots the browser cannot read again: pick them with
// "Choose files" in Chrome / Edge to reload.
//
// Depends on viewer.js: loadModels(entries), log(); FEAFeatures for the
// envelope + dirty state; the ids in index.html's Model section.
// ================================================================

var FEAFiles = (function () {

    var elPick = document.getElementById('feaPick');
    var elInput = document.getElementById('feaFile');
    var elSave = document.getElementById('feaSave');
    var elName = document.getElementById('featName');

    var elReload = document.getElementById('feaReload');
    var elAuto = document.getElementById('feaAuto');

    var binHandle = null;       // FileSystemFileHandle of the primary model, if picked with the API
    var featHandle = null;      // handle of the loaded / last-saved sidecar
    var primaryName = '';       // base name of the primary model ("TANK-A")
    // where the loaded model came from, to read it again:
    //   { kind: 'handles', bins: [{ handle, name }], feat: handle | null }
    //   { kind: 'urls', bin: url, feat: url | null }
    //   { kind: 'snapshot' } (the plain file input: cannot be read again)
    var source = null;
    var autoTimer = null, lastSig = null, pendingSig = null, busy = false, waitNoted = false;

    var hasFS = typeof window !== 'undefined' && typeof window.showOpenFilePicker === 'function';

    // Chrome's per-picker-id folder memory does not survive a reload on a
    // file:// page, so the last picked handle is kept in IndexedDB (handles
    // are storable) and used as the picker's startIn on the next load.
    var DB = 'pluto-files', STORE = 'handles', KEY = 'last';
    function idb() {
        return new Promise(function (res, rej) {
            if (typeof indexedDB === 'undefined') { rej(new Error('no IndexedDB')); return; }
            var rq = indexedDB.open(DB, 1);
            rq.onupgradeneeded = function () { rq.result.createObjectStore(STORE); };
            rq.onsuccess = function () { res(rq.result); };
            rq.onerror = function () { rej(rq.error); };
        });
    }
    async function rememberHandle(h) {
        try {
            var db = await idb();
            await new Promise(function (res, rej) {
                var tx = db.transaction(STORE, 'readwrite');
                tx.objectStore(STORE).put(h, KEY);
                tx.oncomplete = res; tx.onerror = function () { rej(tx.error); };
            });
        } catch (e) { /* private mode, blocked storage: no memory, nothing else lost */ }
    }
    async function recallHandle() {
        try {
            var db = await idb();
            return await new Promise(function (res, rej) {
                var rq = db.transaction(STORE, 'readonly').objectStore(STORE).get(KEY);
                rq.onsuccess = function () { res(rq.result || null); };
                rq.onerror = function () { rej(rq.error); };
            });
        } catch (e) { return null; }
    }

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
        // `id` lets Chrome remember this picker's last folder across sessions; a
        // startIn would override that, so it is only given once a model has been
        // picked in this session (then the pickers stay beside that model).
        var opts = {
            id: 'pluto-model', multiple: true,
            types: [{ description: 'Pluto model + features', accept: { 'application/octet-stream': ['.bin'], 'application/json': ['.json'] } }]
        };
        var start = binHandle || featHandle || await recallHandle();
        if (start) opts.startIn = start;
        try { handles = await window.showOpenFilePicker(opts); }
        catch (err) {
            if (err && err.name === 'AbortError') return;   // cancelled
            delete opts.startIn;                            // a stale remembered handle: pick without it
            try { handles = await window.showOpenFilePicker(opts); } catch (err2) { return; }
        }
        var files = [];
        for (var i = 0; i < handles.length; i++) files.push({ file: await handles[i].getFile(), handle: handles[i] });
        if (handles.length) rememberHandle(handles[0]);
        await ingest(files);
    }

    // ---- reloading -------------------------------------------------------
    function setSource(s) { source = s; lastSig = null; pendingSig = null; syncUI(); }

    function setUrlSource(bin, feat) { setSource({ kind: 'urls', bin: bin, feat: feat || null }); }

    function canReload() { return !!source && (source.kind === 'handles' || source.kind === 'urls'); }

    function bust(u) { return u + (u.indexOf('?') >= 0 ? '&' : '?') + '_t=' + Date.now(); }

    // a fingerprint of the source files' state (time + size), to see a rebuild
    async function signature() {
        if (!canReload()) return null;
        var parts = [];
        if (source.kind === 'handles') {
            var hs = source.bins.map(function (b) { return b.handle; });
            if (source.feat) hs.push(source.feat);
            for (var i = 0; i < hs.length; i++) { var f = await hs[i].getFile(); parts.push(f.lastModified + ':' + f.size); }
        } else {
            var us = [source.bin];
            if (source.feat) us.push(source.feat);
            for (var j = 0; j < us.length; j++) {
                var r = await fetch(bust(us[j]), { method: 'HEAD', cache: 'no-store' });
                if (!r.ok) throw new Error(r.status + ' for ' + us[j]);
                parts.push((r.headers.get('last-modified') || '') + ':' + (r.headers.get('content-length') || '') + ':' + (r.headers.get('etag') || ''));
            }
        }
        return parts.join('|');
    }

    // read the model and its sidecar again; the camera and load case stay
    async function reload(auto) {
        if (!canReload()) {
            say(source && source.kind === 'snapshot' ? 'Reload: these files came through the plain file input, which the browser cannot read again; pick them with "Choose files" (Chrome / Edge) to reload.' : 'Reload: no model to reload.');
            return false;
        }
        if (window.FEAFeatures && FEAFeatures.isDirty()) {
            if (auto) {
                if (!waitNoted) say('Auto-reload: the model changed, but the features have unsaved edits; save them (Ctrl+S) and it reloads.');
                waitNoted = true;
                return false;
            }
            if (typeof confirm === 'function' && !confirm('The features have unsaved edits; reloading drops them. Reload anyway?')) return false;
        }
        waitNoted = false;
        busy = true;
        try {
            var entries = [], featFile = null;
            if (source.kind === 'handles') {
                for (var i = 0; i < source.bins.length; i++) entries.push({ file: await source.bins[i].handle.getFile(), name: source.bins[i].name });
                if (source.feat) featFile = await source.feat.getFile();
            } else {
                var base = function (u) { return decodeURIComponent(u.split('?')[0].split('/').pop()); };
                var rb = await fetch(bust(source.bin), { cache: 'no-store' });
                if (!rb.ok) throw new Error(rb.status + ' for ' + source.bin);
                entries.push({ file: await rb.blob(), name: baseName(base(source.bin)) });
                if (source.feat) {
                    var rf = await fetch(bust(source.feat), { cache: 'no-store' });
                    if (!rf.ok) throw new Error(rf.status + ' for ' + source.feat);
                    featFile = new File([await rf.blob()], base(source.feat), { type: 'application/json' });
                }
            }
            await loadModels(entries, { keepView: true });
            if (featFile && window.FEAFeatures) {
                var obj = JSON.parse(await featFile.text());
                if (classify(obj) === 'features') FEAFeatures.setEnvelope(obj, featFile.name);
            }
            lastSig = await signature();
            say((auto ? 'Auto-reload: ' : 'Reload: ') + entries.map(function (e) { return e.name; }).join(' + ') + ' read again ' + new Date().toLocaleTimeString() + '.');
            syncUI();
            return true;
        } catch (err) {
            say('Reload failed: ' + err.message);
            return false;
        } finally { busy = false; }
    }

    // one Auto-reload poll: reload once a new state has held still for a poll
    async function poll() {
        if (busy || !canReload()) return;
        var sig;
        try { sig = await signature(); } catch (err) { return; }   // a file mid-replace: next poll
        if (lastSig === null) { lastSig = sig; return; }
        if (sig === lastSig) { pendingSig = null; return; }
        if (sig !== pendingSig) { pendingSig = sig; return; }
        pendingSig = null;
        if (await reload(true)) lastSig = await signature();
    }

    function setAuto(on) {
        if (autoTimer) { clearInterval(autoTimer); autoTimer = null; }
        if (on) { lastSig = null; pendingSig = null; autoTimer = setInterval(poll, 2000); poll(); }
        if (elAuto) elAuto.checked = !!on;
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
            source = bins.every(function (f) { return !!f.handle; })
                ? { kind: 'handles', bins: bins.map(function (f) { return { handle: f.handle, name: baseName(f.file.name) }; }), feat: null }
                : { kind: 'snapshot' };
            lastSig = null; pendingSig = null;
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
            if (source && source.kind === 'handles') source.feat = featHandle;
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
                    var sopts = {
                        id: 'pluto-model', suggestedName: name,
                        types: [{ description: 'Pluto features sidecar', accept: { 'application/json': ['.json'] } }]
                    };
                    var sstart = binHandle || await recallHandle();
                    if (sstart) sopts.startIn = sstart;
                    try { featHandle = await window.showSaveFilePicker(sopts); }
                    catch (err0) {
                        if (err0 && err0.name === 'AbortError') throw err0;
                        delete sopts.startIn;
                        featHandle = await window.showSaveFilePicker(sopts);
                    }
                    rememberHandle(featHandle);
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
        var can = canReload();
        if (elReload) {
            elReload.disabled = !can;
            elReload.title = can ? 'Read the model and its features again (the view stays)' :
                source && source.kind === 'snapshot' ? 'These files came through the plain file input; pick them with Choose files (Chrome / Edge) to reload' : 'No model to reload';
        }
        if (elAuto) {
            elAuto.disabled = !can;
            if (!can && autoTimer) setAuto(false);
        }
    }

    if (elPick) elPick.addEventListener('click', function () { pick(); });
    if (elReload) elReload.addEventListener('click', function () { reload(false); });
    if (elAuto) elAuto.addEventListener('change', function () { setAuto(elAuto.checked); });
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
        reload: reload,
        setAuto: setAuto,
        setUrlSource: setUrlSource,
        canReload: canReload,
        classify: classify,
        hasFileSystemAccess: function () { return hasFS; },
        // test hooks
        _sidecarModel: sidecarModel,
        _setSource: setSource,
        _poll: poll,
        _source: function () { return source; }
    };
})();
