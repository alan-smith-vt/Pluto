// Headless test for files.js Reload / Auto-reload: a URL source read again with the view kept, a change
// reloaded only after it holds still for one poll, a wait while the features have unsaved edits, and files
// from the plain file input (snapshots) refused.
//   node viewer/tests/test_reload.js
const fs = require('fs'), path = require('path'), vm = require('vm');
global.window = global; global.document = { getElementById: () => null };
const logs = [];
global.log = (m) => logs.push(m);
const calls = [];
global.loadModels = async (entries, opts) => { calls.push({ names: entries.map(e => e.name), keepView: !!(opts && opts.keepView) }); };
let dirty = false, envelopes = [];
global.FEAFeatures = { isDirty: () => dirty, setEnvelope: (o, n) => envelopes.push(n), envelope: () => null, fileName: () => '', markSaved: () => {} };
// a served folder: two files whose state the test changes
const files = {
    '/out/line.bin': { body: 'BIN1', modified: 'Wed, 21 Oct 2026 10:00:00 GMT' },
    '/out/line.features.json': { body: JSON.stringify({ format: 'pluto-features', version: 1 }), modified: 'Wed, 21 Oct 2026 10:00:00 GMT' }
};
let fetches = 0;
global.fetch = async (url, opts) => {
    fetches++;
    const f = files[url.split('?')[0]];
    if (!f) return { ok: false, status: 404 };
    const headers = new Map([['last-modified', f.modified], ['content-length', String(f.body.length)]]);
    return { ok: true, status: 200, headers: { get: (k) => headers.get(k.toLowerCase()) || null }, blob: async () => new Blob([f.body]) };
};
if (typeof File === 'undefined') global.File = class extends Blob { constructor(parts, name) { super(parts); this.name = name; } };
vm.runInThisContext(fs.readFileSync(path.join(__dirname, '..', 'scripts', 'files.js'), 'utf8'), { filename: 'files.js' });

let fails = 0;
const assert = (c, m) => { if (!c) { fails++; console.error('FAIL ' + m); } else console.log('ok   ' + m); };

(async () => {
    assert(!FEAFiles.canReload(), 'nothing loaded: nothing to reload');
    FEAFiles.setUrlSource('/out/line.bin', '/out/line.features.json');
    assert(FEAFiles.canReload(), 'a URL source can be read again');

    assert(await FEAFiles.reload(false) && calls.length === 1 && calls[0].keepView && calls[0].names[0] === 'line' && envelopes[0] === 'line.features.json',
        'Reload: the model and its sidecar read again, the view kept');

    // Auto-reload: the first poll learns the state; a change reloads only once it holds still
    calls.length = 0;
    await FEAFiles._poll();
    files['/out/line.bin'].modified = 'Wed, 21 Oct 2026 10:05:00 GMT'; files['/out/line.bin'].body = 'BIN2-longer';
    await FEAFiles._poll();
    assert(calls.length === 0, 'a change seen once: not reloaded yet (it may still be being written)');
    await FEAFiles._poll();
    assert(calls.length === 1 && calls[0].keepView, 'the same change seen again: reloaded, the view kept');
    await FEAFiles._poll();
    assert(calls.length === 1, 'no further change: no further reload');

    // unsaved features: Auto waits and says so once; a manual reload asks (here: declined)
    dirty = true;
    files['/out/line.features.json'].modified = 'Wed, 21 Oct 2026 10:06:00 GMT';
    await FEAFiles._poll(); await FEAFiles._poll(); await FEAFiles._poll();
    assert(calls.length === 1 && logs.filter(m => /unsaved edits/.test(m)).length === 1, 'unsaved features: Auto-reload waits, said once');
    global.confirm = () => false;
    assert(!(await FEAFiles.reload(false)) && calls.length === 1, 'unsaved features: a manual Reload asks first (declined: nothing read)');
    dirty = false;
    await FEAFiles._poll(); await FEAFiles._poll();
    assert(calls.length === 2, 'features saved: the waiting change reloads');

    // a missing file: reported, nothing loaded
    FEAFiles.setUrlSource('/out/gone.bin', null);
    assert(!(await FEAFiles.reload(false)) && logs.some(m => /Reload failed: 404/.test(m)), 'a file gone: Reload failed, said so');

    // files from the plain file input: snapshots, refused with the reason
    FEAFiles._setSource({ kind: 'snapshot' });
    assert(!FEAFiles.canReload() && !(await FEAFiles.reload(false)) && logs.some(m => /plain file input/.test(m)), 'snapshot files: no reload, the reason given');

    console.log(fails ? fails + ' FAILED' : 'ALL RELOAD TESTS PASSED');
    process.exitCode = fails ? 1 : 0;
})();
