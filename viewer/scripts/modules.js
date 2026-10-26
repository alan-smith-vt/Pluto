// ================================================================
// modules.js  --  viewer modules: self-contained features with their own tab on the right edge.
//
// REGISTRY below lists every module. core: true = loaded at startup; core: false = optional,
// loaded on demand from the "+" tab (the last tab) and remembered for the next visit
// (localStorage). Moving a module between core and optional is that one flag.
//
// A module is one script in scripts/modules/ (+ optional CSS). It builds its UI with
//   var body = FEAModules.tab(id)      // the flyout body element of its tab (created on first call)
// and must not touch other modules. Tabs stack under the groups tab in load order, then "+".
// ================================================================

var FEAModules = (function () {

    var REGISTRY = [
        { id: 'cutPicker', title: 'Cut picker', icon: '⊘', core: true, src: 'scripts/modules/cutPicker.js',
          desc: 'Pick clean-model members to drop; downloads drop.txt (DuctDrop).' }
    ];

    var KEY = 'pluto.modules.optional';
    var loaded = {};           // id -> 'loading' | 'loaded' | 'failed'
    var tabs = [];             // module tab panels in order; the "+" panel stays last
    var plusPanel = null, plusBody = null;

    function byId(id) { return REGISTRY.find(function (m) { return m.id === id; }); }
    function remembered() { try { return JSON.parse(localStorage.getItem(KEY) || '[]'); } catch (e) { return []; } }
    function remember() {
        var ids = REGISTRY.filter(function (m) { return !m.core && loaded[m.id] === 'loaded'; }).map(function (m) { return m.id; });
        try { localStorage.setItem(KEY, JSON.stringify(ids)); } catch (e) {}
    }

    // ---- tabs ------------------------------------------------------------
    // Stacked under the groups tab (--gr-top + 50px per tab).
    function layout() {
        tabs.concat(plusPanel ? [plusPanel] : []).forEach(function (p, i) {
            var top = 'calc(var(--gr-top) + ' + (50 * (i + 1)) + 'px)';
            p.style.top = top;
            p.querySelector('.md-body').style.maxHeight = 'calc(100vh - ' + top + ' - 12px)';
        });
    }
    function makePanel(title, icon) {
        var p = document.createElement('div'); p.className = 'md-panel collapsed';
        var body = document.createElement('div'); body.className = 'md-body';
        var tab = document.createElement('div'); tab.className = 'md-tab'; tab.title = title; tab.textContent = icon;
        tab.addEventListener('click', function () { p.classList.toggle('collapsed'); });
        p.appendChild(body); p.appendChild(tab);
        document.body.appendChild(p);
        return p;
    }
    function tab(id) {
        var m = byId(id) || { id: id, title: id, icon: '?' };
        var p = tabs.find(function (t) { return t.dataset.module === id; });
        if (!p) {
            p = makePanel(m.title, m.icon); p.dataset.module = id;
            var sub = document.createElement('div'); sub.className = 'fea-sub'; sub.textContent = m.title;
            p.querySelector('.md-body').appendChild(sub);
            tabs.push(p); layout();
        }
        return p.querySelector('.md-body');
    }

    // ---- loading ---------------------------------------------------------
    function load(id) {
        var m = byId(id);
        if (!m || loaded[id]) return;
        loaded[id] = 'loading'; renderPlus();
        var s = document.createElement('script');
        s.src = m.src;
        s.onload = function () { loaded[id] = 'loaded'; remember(); renderPlus(); };
        s.onerror = function () { loaded[id] = 'failed'; renderPlus(); console.warn('[modules] failed to load ' + m.src); };
        document.body.appendChild(s);
    }

    // ---- "+" tab: the optional modules -----------------------------------
    function renderPlus() {
        if (!plusBody) return;
        plusBody.innerHTML = '<div class="fea-sub">Modules</div>';
        REGISTRY.forEach(function (m) {
            var row = document.createElement('div'); row.className = 'md-item';
            row.innerHTML = '<span class="md-item-icon"></span><span class="md-item-text"><div></div><div class="md-item-desc"></div></span>';
            row.querySelector('.md-item-icon').textContent = m.icon;
            row.querySelector('.md-item-text div').textContent = m.title;
            row.querySelector('.md-item-desc').textContent = m.desc || '';
            var st = loaded[m.id];
            if (m.core) {
                var c = document.createElement('span'); c.className = 'md-core'; c.textContent = 'core'; row.appendChild(c);
            } else {
                var b = document.createElement('button'); b.className = 'fea-btn';
                b.textContent = st === 'loaded' ? 'Loaded' : st === 'loading' ? '...' : st === 'failed' ? 'Retry' : 'Load';
                b.disabled = st === 'loaded' || st === 'loading';
                b.addEventListener('click', function () { if (st === 'failed') delete loaded[m.id]; load(m.id); });
                row.appendChild(b);
            }
            plusBody.appendChild(row);
        });
        if (!REGISTRY.some(function (m) { return !m.core; })) {
            var h = document.createElement('div'); h.className = 'fea-hint'; h.textContent = 'No optional modules yet: every module is core.';
            plusBody.appendChild(h);
        }
        if (remembered().length) {
            var f = document.createElement('div'); f.className = 'fea-hint';
            f.textContent = 'Loaded optional modules come back on the next visit.';
            plusBody.appendChild(f);
        }
    }

    plusPanel = makePanel('Modules: load optional modules', '+');
    plusBody = plusPanel.querySelector('.md-body');
    layout();
    renderPlus();
    REGISTRY.forEach(function (m) { if (m.core) load(m.id); });
    remembered().forEach(function (id) { var m = byId(id); if (m && !m.core) load(id); });

    return { tab: tab, load: load, registry: function () { return REGISTRY.slice(); } };
})();
