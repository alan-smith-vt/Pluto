// ================================================================
// sectionCut.js  --  section cuts: a named, saved list of probe lines.
//
// A cut is a line through a center point on a shell panel, along a
// direction (X, Y or Z today; any unit vector round-trips), with total
// length L (L/2 each way). "New section cut" arms placement; Ctrl+left-
// click raycasts the center onto the mesh. "Adjust" re-places the
// SELECTED cut the same way. At ~200 stations along the line a short
// probe ray (along the placement panel's normal) re-hits the mesh and
// evaluates the currently displayed field there via FEAQuery. Stations
// that miss (door openings, past the extents) or read NaN are gaps.
// Trapezoidal integration over contiguous hit segments.
//
// Many cuts live at once (2026-09-04, ported from the archived viewer's
// sectionCut.js management layer): each has a name, an optional group,
// a visibility flag and its own 3D marker; the SELECTED cut owns the
// probe, the plot and the isolate. Cuts persist in the features sidecar
// under `sectionCuts` (spec: vault/format/features-sidecar.md):
//
//   { id, name, group?, visible, axis?,
//     plane:  { point: [x,y,z], normal: [nx,ny,nz] },   // the SECTION plane
//     bounds: { up: [ux,uy,uz], halfWidth: L/2 },         // up = panel normal
//     domains: ["shells"] }
//
// The probe direction is not stored twice: dir = normal x up. `axis`
// ("x" | "y" | "z") is the axis the user chose and colours the list;
// `sloped: true` means that axis is bent onto the panel (dir = axis minus
// its component along up, normalised: Z on a roof runs up the slope, X on
// a curved wall follows the tangent). An item with neither key, or whose
// direction matches no axis, reads as "sloped" (amber).
// Unknown keys on an item survive a round trip (kept on item._raw).
// The archived viewer's standalone section_cuts.json ({version, groups,
// cuts:[{point, axis, length(in)}]}) imports through a shim.
//
// Depends on viewer.js globals: scene, camera, mesh, feaEdges, feaModel,
// feaBuild, feaLCData, currentComp, currentStr, smoothing, absValue,
// envGlobalDsr*, inDsrMode(), inStrMode(), strengthComponents(),
// nodeVec(), fmt(), requestRender(), log(); FEAFeatures for the sidecar.
// ================================================================

var FEASectionCut = (function () {

    var N_SAMPLES = 200;

    var AXIS_DIR = {
        x: new THREE.Vector3(1, 0, 0),
        y: new THREE.Vector3(0, 1, 0),
        z: new THREE.Vector3(0, 0, 1)
    };
    var AXIS_CSS = { x: '#ff5252', y: '#6ee06e', z: '#5f8cff', sloped: '#f2b84b' };
    var AXIS_COS = 0.9998;          // within ~1 deg of a global axis counts as that axis

    var armed = false;              // placing a NEW cut
    var adjusting = false;          // re-placing the selected cut
    var defAxis = 'x';              // axis for the next new cut
    var defSloped = false;          // bend the axis onto the panel for the next new cut
    var defLength = 0;              // 0 -> auto default on first placement
    var cuts = [];                  // runtime cut objects (see makeCut)
    var groups = [];                // [{ name, visible }] from the sidecar
    var collapsed = {};             // group name -> collapsed (view state only)
    var activeId = null;
    var samples = null;             // { t:[], v:[], hit:[], area, effLen } for the active cut
    var isolated = null;            // Uint8Array[nElem] keep-flags, or null
    var probeRay = new THREE.Raycaster();
    var lastScale = -1;
    var suppressSync = false;       // while loading from the envelope

    // ---- DOM ---------------------------------------------------
    var elPanel = document.getElementById('scPanel');
    var elTab   = document.getElementById('scTab');
    var elNew   = document.getElementById('scNew');
    var elAdjust = document.getElementById('scAdjust');
    var elDelete = document.getElementById('scDelete');
    var elName  = document.getElementById('scName');
    var elGroup = document.getElementById('scGroup');
    var elGroupList = document.getElementById('scGroupList');
    var elAxisX = document.getElementById('scAxisX');
    var elAxisY = document.getElementById('scAxisY');
    var elAxisZ = document.getElementById('scAxisZ');
    var elSloped = document.getElementById('scSloped');
    var elLen   = document.getElementById('scLen');
    var elPosX  = document.getElementById('scPosX');
    var elPosY  = document.getElementById('scPosY');
    var elPosZ  = document.getElementById('scPosZ');
    var elUnits = document.getElementById('scUnits');
    var elIso   = document.getElementById('scIsolate');
    var elMarker    = document.getElementById('scMarker');
    var elColorMode = document.getElementById('scColorMode');
    var elColor     = document.getElementById('scColor');
    var elThick     = document.getElementById('scThick');
    var elPlot  = document.getElementById('scPlot');
    var elStats = document.getElementById('scStats');
    var elList  = document.getElementById('scList');
    var elCount = document.getElementById('scCount');

    function on(el, ev, fn) { if (el) el.addEventListener(ev, fn); }

    on(elTab, 'click', function () { elPanel.classList.toggle('collapsed'); });
    on(elNew, 'click', function () { setArmed(!armed); });
    on(elAdjust, 'click', function () { setAdjusting(!adjusting); });
    on(elDelete, 'click', function () { deleteCut(activeId); });

    on(elAxisX, 'click', function () { setAxis('x'); });
    on(elAxisY, 'click', function () { setAxis('y'); });
    on(elAxisZ, 'click', function () { setAxis('z'); });
    on(elSloped, 'click', function () { setSloped(!(active() ? active().sloped : defSloped)); });

    on(elLen, 'change', function () {
        var v = parseFloat(this.value.replace(/,/g, ''));
        var c = active();
        if (isNaN(v) || v <= 0) { this.value = c ? String(c.length) : (defLength ? String(defLength) : ''); return; }
        defLength = v;
        if (c) { c.length = v; afterMutation(c); }
    });
    on(elName, 'change', function () {
        var c = active(); if (!c) return;
        c.name = this.value.trim() || c.name;
        this.value = c.name;
        afterMutation(null);
    });
    on(elGroup, 'change', function () {
        var c = active(); if (!c) return;
        var g = this.value.trim();
        c.group = g || null;
        if (g && !groupByName(g)) groups.push({ name: g, visible: true });
        pruneGroups();
        afterMutation(null);
    });
    [elPosX, elPosY, elPosZ].forEach(function (el, k) {
        on(el, 'change', function () {
            var c = active(); if (!c) return;
            var v = parseFloat(this.value.replace(/,/g, ''));
            if (!isFinite(v)) { syncFields(); return; }
            c.center.setComponent(k, v);
            resolveElem(c);
            afterMutation(c);
        });
    });

    on(elUnits, 'change', function () { drawPlot(); });
    on(elMarker, 'change', function () { updateVisuals(); });
    on(elColorMode, 'change', function () { syncColorSwatch(); updateVisuals(); drawPlot(); });
    // Touching the swatch means you want that color -- flip to Custom
    // rather than silently discarding the pick.
    on(elColor, 'input', function () {
        elColorMode.value = 'custom';
        elColor.disabled = false;
        updateVisuals();
        drawPlot();
    });
    on(elThick, 'change', function () {
        var v = parseFloat(this.value);
        if (!(isFinite(v) && v > 0)) { this.value = '1'; }
        else if (v > 20) { this.value = '20'; }
        lastScale = -1;           // force updateScale past its no-op guard
        updateScale();
    });
    on(elIso, 'change', function () {
        applyIsolation();
        resample();          // isolation also scopes what the probe samples
        drawPlot();
    });

    function setArmed(onOff) {
        armed = onOff;
        if (onOff) {
            adjusting = false;
            if (window.FEAPredicates && FEAPredicates.disarm) FEAPredicates.disarm();
        }
        if (elNew) {
            elNew.classList.toggle('active', armed);
            elNew.textContent = armed ? 'Ctrl+click the model…' : 'New section cut';
        }
        if (elAdjust) elAdjust.classList.toggle('active', adjusting);
    }
    function setAdjusting(onOff) {
        adjusting = onOff && !!active();
        if (adjusting) {
            armed = false;
            if (window.FEAPredicates && FEAPredicates.disarm) FEAPredicates.disarm();
        }
        if (elNew) { elNew.classList.toggle('active', armed); elNew.textContent = 'New section cut'; }
        if (elAdjust) {
            elAdjust.classList.toggle('active', adjusting);
            elAdjust.textContent = adjusting ? 'Ctrl+click…' : 'Adjust';
        }
    }

    // Axis buttons: the default for new cuts, and the selected cut's direction.
    function setAxis(a) {
        defAxis = a;
        var c = active();
        if (c) { c.axis = a; computeDir(c); afterMutation(c); }
        else { syncAxisButtons(); syncColorSwatch(); }
    }
    function setSloped(onOff) {
        defSloped = !!onOff;
        var c = active();
        if (c) { c.sloped = defSloped; computeDir(c); afterMutation(c); }
        else syncAxisButtons();
    }
    // The probe direction from the chosen axis: the axis itself, or the axis
    // bent onto the panel (component along the panel normal removed). A cut
    // that came in with no axis keeps whatever direction it has.
    function computeDir(c) {
        if (!AXIS_DIR[c.axis]) return;
        var d = AXIS_DIR[c.axis].clone();
        if (c.sloped) {
            d.addScaledVector(c.normal, -d.dot(c.normal));
            if (d.lengthSq() < 1e-8) d = AXIS_DIR[c.axis].clone();     // axis is the panel normal: nothing to bend
        }
        c.dir = d.normalize();
    }
    function syncAxisButtons() {
        var c = active();
        var a = c ? c.axis : defAxis;
        var s = c ? !!c.sloped : defSloped;
        if (elAxisX) elAxisX.classList.toggle('active', a === 'x');
        if (elAxisY) elAxisY.classList.toggle('active', a === 'y');
        if (elAxisZ) elAxisZ.classList.toggle('active', a === 'z');
        if (elSloped) {
            elSloped.classList.toggle('active', s);
            elSloped.disabled = !!(c && !AXIS_DIR[c.axis]);
        }
    }

    // ---- cut objects --------------------------------------------
    function newGuid(prefix) {
        var s = '';
        for (var i = 0; i < 16; i++) s += (Math.floor(Math.random() * 16)).toString(16);
        return prefix + s;
    }
    function active() {
        for (var i = 0; i < cuts.length; i++) if (cuts[i].id === activeId) return cuts[i];
        return null;
    }
    function findCut(id) {
        for (var i = 0; i < cuts.length; i++) if (cuts[i].id === id) return cuts[i];
        return null;
    }
    function groupByName(name) {
        for (var i = 0; i < groups.length; i++) if (groups[i].name === name) return groups[i];
        return null;
    }
    function pruneGroups() {
        groups = groups.filter(function (g) {
            return cuts.some(function (c) { return c.group === g.name; });
        });
    }

    // Which global axis a unit direction is (or 'sloped').
    function axisOf(dir) {
        var ax = Math.abs(dir.x), ay = Math.abs(dir.y), az = Math.abs(dir.z);
        if (ax >= AXIS_COS) return 'x';
        if (ay >= AXIS_COS) return 'y';
        if (az >= AXIS_COS) return 'z';
        return 'sloped';
    }

    function makeCut(fields) {
        var c = {
            id: fields.id || newGuid('c-'),
            name: fields.name || nextName(),
            group: fields.group || null,
            visible: fields.visible !== false,
            center: fields.center.clone(),
            normal: fields.normal.clone().normalize(),      // panel normal = bounds.up
            dir: fields.dir.clone().normalize(),
            length: fields.length,
            sloped: !!fields.sloped,
            elem: -1,
            visual: null,
            visParts: [],
            _raw: fields._raw || null
        };
        c.axis = fields.axis || axisOf(c.dir);
        if (c.sloped && AXIS_DIR[c.axis]) computeDir(c);
        return c;
    }
    function nextName() {
        var n = cuts.length + 1, name;
        do { name = 'Cut ' + n++; } while (cuts.some(function (c) { return c.name === name; }));
        return name;
    }

    // ---- sidecar round trip -------------------------------------
    function vec3(a, fallback) {
        return (Array.isArray(a) && a.length === 3 && a.every(function (v) { return isFinite(v); }))
            ? new THREE.Vector3(a[0], a[1], a[2]) : (fallback ? fallback.clone() : null);
    }
    function arr(v) { return [v.x, v.y, v.z]; }

    // item -> cut. Section-plane normal n and panel normal up give the probe
    // direction dir = n x up (re-orthogonalised against up).
    function itemToCut(raw) {
        var plane = raw.plane || {}, bounds = raw.bounds || {};
        var point = vec3(plane.point, null);
        var n = vec3(plane.normal, null);
        var up = vec3(bounds.up, null);
        if (!point || !n) return null;
        if (!up || up.lengthSq() < 1e-12) up = new THREE.Vector3(0, 0, 1);
        n.normalize(); up.normalize();
        var hasAxis = !!(raw.axis && AXIS_DIR[raw.axis]);
        var dir = hasAxis && !raw.sloped ? AXIS_DIR[raw.axis].clone()
                : new THREE.Vector3().crossVectors(n, up);
        if (dir.lengthSq() < 1e-12) dir = hasAxis ? AXIS_DIR[raw.axis].clone() : new THREE.Vector3(1, 0, 0);
        dir.normalize();
        var half = isFinite(bounds.halfWidth) && bounds.halfWidth > 0 ? bounds.halfWidth : 0;
        return makeCut({
            id: raw.id, name: raw.name, group: raw.group, visible: raw.visible,
            center: point, normal: up, dir: dir, length: half * 2 || defLength || 1,
            axis: hasAxis ? raw.axis : null, sloped: hasAxis && !!raw.sloped, _raw: raw
        });
    }

    // cut -> item, preserving unknown keys from the loaded item.
    function cutToItem(c) {
        var raw = c._raw || {};
        raw.id = c.id;
        raw.name = c.name;
        if (c.group) raw.group = c.group; else delete raw.group;
        raw.visible = !!c.visible;
        var n = new THREE.Vector3().crossVectors(c.normal, c.dir);
        if (n.lengthSq() < 1e-12) n = new THREE.Vector3().crossVectors(c.normal, AXIS_DIR[c.axis === 'z' ? 'x' : 'z']);
        n.normalize();
        raw.plane = { point: arr(c.center), normal: arr(n) };
        var b = raw.bounds || {};
        b.up = arr(c.normal);
        b.halfWidth = c.length / 2;
        raw.bounds = b;
        if (!Array.isArray(raw.domains)) raw.domains = ['shells'];
        if (c.axis && AXIS_DIR[c.axis]) raw.axis = c.axis; else delete raw.axis;
        if (c.sloped && AXIS_DIR[c.axis]) raw.sloped = true; else delete raw.sloped;
        c._raw = raw;
        return raw;
    }

    function syncEnvelope() {
        if (suppressSync || !window.FEAFeatures) return;
        var env = FEAFeatures.envelope();
        if (!env) {
            if (cuts.length === 0) return;
            env = FEAFeatures.ensureEnvelope ? FEAFeatures.ensureEnvelope() : null;
            if (!env) return;
        }
        if (!env.sectionCuts) env.sectionCuts = { version: 1, items: [] };
        if (FEAFeatures.markDirty) FEAFeatures.markDirty();
        env.sectionCuts.items = cuts.map(cutToItem);
        if (groups.length) env.sectionCuts.groups = groups.map(function (g) { return { name: g.name, visible: g.visible !== false }; });
        else delete env.sectionCuts.groups;
    }

    // Sidecar loaded (or replaced): rebuild the cut list from it.
    function onEnvelope(env) {
        suppressSync = true;
        clearAll();
        var sc = env && env.sectionCuts;
        var list = (sc && Array.isArray(sc.items)) ? sc.items : [];
        var skipped = 0;
        list.forEach(function (raw) {
            var c = itemToCut(raw);
            if (c) cuts.push(c); else skipped++;
        });
        groups = (sc && Array.isArray(sc.groups) ? sc.groups : []).map(function (g) {
            return { name: String(g.name), visible: g.visible !== false };
        });
        cuts.forEach(function (c) { if (c.group && !groupByName(c.group)) groups.push({ name: c.group, visible: true }); });
        pruneGroups();
        suppressSync = false;
        if (feaModel && mesh) cuts.forEach(resolveElem);
        if (list.length && typeof log === 'function')
            log('Section cuts: ' + cuts.length + ' loaded from sidecar' + (skipped ? ' (' + skipped + ' skipped)' : '') + '.');
        activeId = cuts.length ? cuts[0].id : null;
        rebuild();
    }

    // Archived viewer's section_cuts.json: {version, groups:[{name,...}],
    // cuts:[{name, group, point:[x,y,z] (inches), axis:'X', length (inches), visible}]}.
    // Points convert to the model unit; the panel normal is found by probing.
    function importLegacy(obj) {
        var n = 0, scale = normLen(lengthUnit()) === 'ft' ? 1 / 12 : 1;
        (obj.cuts || []).forEach(function (lc) {
            var p = vec3(lc.point, null);
            if (!p) return;
            p.multiplyScalar(scale);
            var a = String(lc.axis || 'z').toLowerCase();
            if (!AXIS_DIR[a]) a = 'z';
            var c = makeCut({
                name: lc.name, group: lc.group, visible: lc.visible !== false,
                center: p, normal: guessNormal(p, a), dir: AXIS_DIR[a],
                length: (lc.length > 0 ? lc.length * scale : defLength || 1), axis: a
            });
            cuts.push(c); n++;
        });
        (obj.groups || []).forEach(function (g) {
            if (g && g.name && !groupByName(g.name)) groups.push({ name: String(g.name), visible: g.visible !== false });
        });
        pruneGroups();
        if (feaModel && mesh) cuts.forEach(resolveElem);
        if (n && !activeId) activeId = cuts[cuts.length - n].id;
        if (typeof log === 'function') log('Section cuts: ' + n + ' imported from legacy section_cuts.json.');
        afterMutation(active());
    }
    // Nearest shell surface to a point, looking along the two axes
    // perpendicular to the cut direction; its face normal becomes the panel.
    function guessNormal(p, a) {
        if (!mesh || !feaBuild) return new THREE.Vector3(0, 0, 1);
        var best = null, bestD = Infinity;
        var eps = modelSpan() * 0.05;
        ['x', 'y', 'z'].forEach(function (k) {
            if (k === a) return;
            [1, -1].forEach(function (sgn) {
                probeRay.near = 0; probeRay.far = eps;
                probeRay.set(p.clone().addScaledVector(AXIS_DIR[k], -sgn * eps * 0.5), AXIS_DIR[k].clone().multiplyScalar(sgn));
                var hs = probeRay.intersectObject(mesh);
                if (hs.length && hs[0].distance < bestD) { bestD = hs[0].distance; best = hs[0].faceIndex; }
            });
        });
        return best == null ? new THREE.Vector3(0, 0, 1) : faceNormal(best);
    }


    // ---- marker color ------------------------------------------
    // 'By axis' follows the cut's direction; 'Custom' takes the swatch.
    // Both the 3D marker and the plot curve use it, so the two always
    // read as the same cut. Mode is 'axis', 'custom', or a '#rrggbb' preset.
    function cutCss(c) {
        var m = elColorMode ? elColorMode.value : 'axis';
        if (m === 'custom') return elColor.value;
        if (m === 'axis') return AXIS_CSS[c ? c.axis : defAxis] || AXIS_CSS.sloped;
        return m;
    }
    function markerCss() { return cutCss(active()); }

    // Outside 'Custom' the swatch is a read-only preview of the
    // color the current mode resolves to.
    function syncColorSwatch() {
        if (!elColorMode) return;
        var custom = elColorMode.value === 'custom';
        elColor.disabled = !custom;
        if (!custom) elColor.value = markerCss();
    }

    // Thickness multiplier on every radial dimension. Blank/garbage
    // reads as 1 rather than collapsing the marker to nothing.
    function thickness() {
        var v = parseFloat(elThick.value);
        return (isFinite(v) && v > 0) ? Math.min(v, 20) : 1;
    }

    // ---- units -------------------------------------------------
    function valueUnit() {
        if (!feaModel || inDsrMode()) return '';
        var c = activeComponent();
        return (c && c.unit) ? c.unit : '';
    }
    function lengthUnit() { return elUnits ? elUnits.value : ''; }   // '' | 'in' | 'ft'
    function normLen(u) {
        u = String(u == null ? '' : u).toLowerCase().replace(/[.\s]/g, '');
        if (u === 'in' || u === 'inch' || u === 'inches') return 'in';
        if (u === 'ft' || u === 'foot' || u === 'feet') return 'ft';
        return null;
    }
    var FT_PER = { in: 1 / 12, ft: 1 };

    var lastModel = null;
    function syncModelUnits() {
        if (!feaModel || feaModel === lastModel) return;
        lastModel = feaModel;
        var raw = feaModel.meta.raw;
        var mu = normLen(raw && raw.lengthUnit);
        if (mu && elUnits) elUnits.value = mu;
    }

    // Displayed area value + unit (see areaDisplay in the previous
    // revision: kip/ft over inches -> kip when the model unit is known).
    function areaDisplay(rawArea) {
        var vu = valueUnit(), mu = normLen(lengthUnit());
        var slash = vu.lastIndexOf('/');
        if (mu && slash > 0) {
            var den = normLen(vu.slice(slash + 1));
            if (den) return { value: rawArea * FT_PER[mu] / FT_PER[den], unit: vu.slice(0, slash) };
        }
        var lu = lengthUnit();
        return { value: rawArea, unit: (vu || lu) ? (vu || '1') + '·' + (lu || 'len') : '' };
    }
    function withUnit(text, unit) { return unit ? text + ' ' + unit : text; }
    function esc(s) { return String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;'); }

    // ---- helpers -----------------------------------------------
    function modelSpan() {
        if (!mesh) return 1;
        var d = new THREE.Box3().setFromObject(mesh).getSize(new THREE.Vector3());
        return Math.max(d.x, d.y, d.z) || 1;
    }

    // Same tolerance geometryBuilder uses to keep wall and slab normal
    // groups from bleeding into each other (cos 15°, sign-insensitive).
    var NORMAL_DOT_THRESHOLD = 0.966;

    function normalMatches(c, e) {
        var nrm = feaBuild.elemNormals;
        var dot = nrm[e * 3] * c.normal.x + nrm[e * 3 + 1] * c.normal.y + nrm[e * 3 + 2] * c.normal.z;
        return Math.abs(dot) > NORMAL_DOT_THRESHOLD;
    }

    // A probe hit is usable when its element lies in the cut's plane --
    // and, while isolating, when it belongs to the isolated panel too.
    function faceUsable(c, faceIndex) {
        var e = feaBuild.triToElem[faceIndex];
        if (isolated) return !!isolated[e];
        return normalMatches(c, e);
    }

    // Element under a cut's center (after a load or a numeric move):
    // short ray down the panel normal, first coplanar hit wins.
    function resolveElem(c) {
        c.elem = -1;
        if (!mesh || !feaBuild) return;
        var eps = modelSpan() * 0.02;
        probeRay.near = 0; probeRay.far = eps * 2;
        probeRay.set(c.center.clone().addScaledVector(c.normal, eps), c.normal.clone().negate());
        var hs = probeRay.intersectObject(mesh);
        for (var i = 0; i < hs.length; i++) {
            var fi = hs[i].faceIndex;
            if (fi == null) continue;
            var e = feaBuild.triToElem[fi];
            if (normalMatches(c, e)) { c.elem = e; return; }
        }
    }

    // ---- isolation: the connected panel the cut sits on ---------
    function nodeToElems() {
        if (feaBuild.scNodeElems) return feaBuild.scNodeElems;
        var map = new Array(feaModel.header.nNodes);
        var nElem = feaBuild.elemNCount.length;
        for (var e = 0; e < nElem; e++) {
            var nc = feaBuild.elemNCount[e];
            for (var k = 0; k < nc; k++) {
                var ni = feaBuild.elemCorners[e * 4 + k];
                (map[ni] || (map[ni] = [])).push(e);
            }
        }
        feaBuild.scNodeElems = map;
        return map;
    }

    function computeIsolatedElems(c) {
        var nElem = feaBuild.elemNCount.length;
        var keep = new Uint8Array(nElem);
        if (c.elem < 0) return null;
        var map = nodeToElems();
        var queue = [c.elem];
        keep[c.elem] = 1;
        for (var qi = 0; qi < queue.length; qi++) {
            var e = queue[qi];
            var nc = feaBuild.elemNCount[e];
            for (var k = 0; k < nc; k++) {
                var nbrs = map[feaBuild.elemCorners[e * 4 + k]];
                if (!nbrs) continue;
                for (var j = 0; j < nbrs.length; j++) {
                    var n2 = nbrs[j];
                    if (keep[n2] || !normalMatches(c, n2)) continue;
                    keep[n2] = 1;
                    queue.push(n2);
                }
            }
        }
        return keep;
    }

    // Write the keep-set into the mesh + edge elemVis attributes; a
    // null set means "show everything". Beams and node markers follow.
    function writeVis(keep) {
        if (!mesh || !feaBuild) return;
        var nElem = feaBuild.elemNCount.length;
        var attr = mesh.geometry.getAttribute('elemVis');
        var arrv = attr.array;
        var edgeAttr = feaEdges ? feaEdges.geometry.getAttribute('elemVis') : null;
        var eArr = edgeAttr ? edgeAttr.array : null;
        var vptr = 0, eptr = 0;
        for (var e = 0; e < nElem; e++) {
            var nc = feaBuild.elemNCount[e];
            var vis = keep ? keep[e] : 1;
            var nv = nc === 4 ? 6 : 3;
            for (var i = 0; i < nv; i++) arrv[vptr++] = vis;
            if (eArr) for (var q = 0; q < nc * 2; q++) eArr[eptr++] = vis;
        }
        attr.needsUpdate = true;
        if (edgeAttr) edgeAttr.needsUpdate = true;
        var nodeKeep = null;
        if (keep) {
            nodeKeep = new Uint8Array(feaModel.header.nNodes);
            for (var e3 = 0; e3 < nElem; e3++) {
                if (!keep[e3]) continue;
                var nc3 = feaBuild.elemNCount[e3];
                for (var k3 = 0; k3 < nc3; k3++) nodeKeep[feaBuild.elemCorners[e3 * 4 + k3]] = 1;
            }
        }
        if (window.FEABeams && FEABeams.writeVis) FEABeams.writeVis(nodeKeep);
        if (window.FEAFeatures && FEAFeatures.writeVis) FEAFeatures.writeVis(nodeKeep);
        requestRender();
    }

    function applyIsolation() {
        if (!mesh || !feaBuild) return;
        var c = active();
        isolated = (elIso && elIso.checked && c) ? computeIsolatedElems(c) : null;
        writeVis(isolated);
    }

    // Element-plane normal of the picked face (orientation is
    // irrelevant -- the probe crosses the surface either way).
    function faceNormal(faceIndex) {
        var elem = feaBuild.triToElem[faceIndex];
        var a = nodeVec(feaBuild.elemCorners[elem * 4]);
        var b = nodeVec(feaBuild.elemCorners[elem * 4 + 1]);
        var c = nodeVec(feaBuild.elemCorners[elem * 4 + 2]);
        var n = b.sub(a).cross(c.sub(a));
        if (n.lengthSq() < 1e-20) n.set(0, 0, 1);
        return n.normalize();
    }

    // Evaluate the currently displayed field at a mesh hit -- same
    // state showReadout() feeds FEAQuery.
    function queryValue(faceIndex, point) {
        var inDsr = inDsrMode();
        var inStr = inStrMode();
        if (inDsr && !envGlobalDsrValue) return NaN;
        if (inStr && !feaModel.strData) return NaN;
        if (!inDsr && !inStr && !feaLCData) return NaN;
        var q = FEAQuery.query({
            model: feaModel, buildResult: feaBuild,
            lcData: inStr ? feaModel.strData : feaLCData,
            compIndex: inStr ? currentStr : currentComp,
            compStride: inStr ? strengthComponents().length : null,
            smoothing: smoothing,
            dsrMode: inDsr ? { value: envGlobalDsrValue, source: envGlobalDsrSource, lc: envGlobalDsrLC } : null
        }, faceIndex, point);
        var v = q.value;
        if (!inDsr && absValue && v === v) v = Math.abs(v);
        return v;
    }

    // ---- placement (called from viewer.js Ctrl+click) ----------
    function wantsPick() { return (armed || adjusting) && !!feaModel; }

    function defaultLength() {
        if (defLength > 0) return defLength;
        // default: half the model span, rounded to 2 sig figs
        var s = modelSpan() * 0.5;
        var mag = Math.pow(10, Math.floor(Math.log10(s)) - 1);
        defLength = Math.round(s / mag) * mag;
        if (elLen) elLen.value = String(defLength);
        return defLength;
    }

    function placeCenter(hit) {
        var c;
        if (adjusting && active()) {
            c = active();
            c.center = hit.point.clone();
            c.normal = faceNormal(hit.faceIndex);
            c.elem = feaBuild.triToElem[hit.faceIndex];
            computeDir(c);
            setAdjusting(false);
        } else {
            c = makeCut({
                center: hit.point, normal: faceNormal(hit.faceIndex), dir: AXIS_DIR[defAxis],
                length: defaultLength(), axis: defAxis, sloped: defSloped
            });
            c.elem = feaBuild.triToElem[hit.faceIndex];
            cuts.push(c);
            activeId = c.id;
            setArmed(false);
        }
        if (elPanel) elPanel.classList.remove('collapsed');
        afterMutation(c);
    }

    function selectCut(id) {
        if (activeId === id) return;
        activeId = id;
        setAdjusting(false);
        rebuild();
    }
    function deleteCut(id) {
        var i = cuts.map(function (c) { return c.id; }).indexOf(id);
        if (i < 0) return;
        removeVisual(cuts[i]);
        cuts.splice(i, 1);
        pruneGroups();
        if (activeId === id) activeId = cuts.length ? cuts[Math.min(i, cuts.length - 1)].id : null;
        afterMutation(null);
    }
    function toggleCutVis(id) {
        var c = findCut(id); if (!c) return;
        c.visible = !c.visible;
        afterMutation(null);
    }
    function toggleGroupVis(name) {
        var g = groupByName(name); if (!g) return;
        g.visible = !g.visible;
        afterMutation(null);
    }
    function cutShown(c) {
        if (!c.visible) return false;
        var g = c.group ? groupByName(c.group) : null;
        return !g || g.visible !== false;
    }

    // ---- visuals: one marker group per shown cut -----------------
    function removeVisual(c) {
        if (!c.visual) return;
        scene.remove(c.visual);
        c.visual.traverse(function (o) {
            if (o.geometry) o.geometry.dispose();
            if (o.material) o.material.dispose();
        });
        c.visual = null;
        c.visParts = [];
    }

    // Every marker part is a cylinder or sphere rather than a THREE
    // line: WebGL clamps line width to 1px, so a line-based marker
    // could not honor the thickness input at all.
    var THIN_R = 0.08;          // hairline radius vs the solid bar's 0.3
    var UP = new THREE.Vector3(0, 1, 0);

    // Aim a cylinder's local +Y along a world direction.
    function orientAlong(obj, dir) {
        obj.quaternion.setFromUnitVectors(UP, dir.clone().normalize());
    }
    // Two directions perpendicular to the cut (for the crosshair arms).
    function perpDirs(c) {
        var d = c.dir;
        var a = Math.abs(d.x) < 0.9 ? new THREE.Vector3(1, 0, 0) : new THREE.Vector3(0, 1, 0);
        var p1 = new THREE.Vector3().crossVectors(d, a).normalize();
        var p2 = new THREE.Vector3().crossVectors(d, p1).normalize();
        return [p1, p2];
    }

    function hexOf(c) { return parseInt(cutCss(c).slice(1), 16); }

    // Where along the cut the probe found data: contiguous runs of
    // [s0, s1, hit] from the selected cut's samples (a segment between two
    // stations counts as data only when both ends hit, matching the
    // integral). Any other cut, or no samples yet, is one "hit" run.
    var MISS_HEX = 0xffffff;
    function dataRuns(c) {
        var half = c.length / 2;
        if (c.id !== activeId || !samples || samples.hit.length !== N_SAMPLES + 1) return [[-half, half, true]];
        var runs = [], s0 = -half, cur = samples.hit[0] && samples.hit[1];
        for (var i = 1; i < N_SAMPLES; i++) {
            var h = samples.hit[i] && samples.hit[i + 1];
            if (h !== cur) { runs.push([s0, samples.t[i], cur]); s0 = samples.t[i]; cur = h; }
        }
        runs.push([s0, half, cur]);
        return runs;
    }
    // One cylinder per run along the cut direction, coloured by whether it
    // reached data (cut colour) or not (white).
    function addRunCylinders(c, group, runs, radius, segs, makeMat) {
        runs.forEach(function (r) {
            var len = r[1] - r[0];
            if (len <= 0) return;
            var cyl = new THREE.Mesh(new THREE.CylinderGeometry(radius, radius, len, segs), makeMat(r[2] ? hexOf(c) : MISS_HEX));
            orientAlong(cyl, c.dir);
            cyl.position.copy(c.dir).multiplyScalar((r[0] + r[1]) / 2);
            cyl.renderOrder = 8;
            group.add(cyl);
            c.visParts.push({ obj: cyl, axial: 'model' });
        });
    }

    // Solid: an orb like the focus target with a bar running the full
    // cut length, white where the probe found nothing. The selected cut's
    // orb also gets a wireframe halo.
    function buildSolidMarker(c, group) {
        var mat = new THREE.MeshPhongMaterial({ color: hexOf(c), shininess: 60 });
        var sph = new THREE.Mesh(new THREE.SphereGeometry(1, 24, 16), mat);
        group.add(sph);
        c.visParts.push({ obj: sph, axial: 'iso' });
        addRunCylinders(c, group, dataRuns(c), 0.3, 16, function (hex) {
            return new THREE.MeshPhongMaterial({ color: hex, shininess: 60 });
        });
    }

    // Thin / dotted: a hairline spine down the cut (solid, or broken
    // into dashes) plus a crosshair at the center, drawn depth-test free.
    function buildThinMarker(c, group, dashed) {
        var mat = new THREE.MeshBasicMaterial({ color: hexOf(c), depthTest: false });
        var missMat = new THREE.MeshBasicMaterial({ color: MISS_HEX, depthTest: false });
        var half = c.length / 2;
        var runs = dataRuns(c);
        function hitAt(s) {
            for (var k = 0; k < runs.length; k++) if (s >= runs[k][0] && s <= runs[k][1]) return runs[k][2];
            return true;
        }
        if (dashed) {
            var nDash = 24;
            var period = c.length / nDash;
            var geo = new THREE.CylinderGeometry(THIN_R, THIN_R, period * 0.45, 8);
            for (var i = 0; i < nDash; i++) {
                var s = -half + period * (i + 0.5);
                var dash = new THREE.Mesh(geo, hitAt(s) ? mat : missMat);
                orientAlong(dash, c.dir);
                dash.position.copy(c.dir).multiplyScalar(s);
                dash.renderOrder = 8;
                group.add(dash);
                c.visParts.push({ obj: dash, axial: 'model' });
            }
        } else {
            addRunCylinders(c, group, runs, THIN_R, 8, function (hex) {
                return hex === MISS_HEX ? missMat : mat;
            });
        }
        var armGeo = new THREE.CylinderGeometry(THIN_R, THIN_R, 1, 8);
        perpDirs(c).forEach(function (p) {
            var armM = new THREE.Mesh(armGeo, mat);
            orientAlong(armM, p);
            armM.renderOrder = 8;
            group.add(armM);
            c.visParts.push({ obj: armM, axial: 2.5 });
        });
    }

    function buildHalo(c, group) {
        var halo = new THREE.Mesh(
            new THREE.SphereGeometry(1, 16, 12),
            new THREE.MeshBasicMaterial({ color: 0xffffff, wireframe: true, transparent: true, opacity: 0.35 }));
        group.add(halo);
        c.visParts.push({ obj: halo, axial: 'halo' });
    }

    function updateVisuals() {
        cuts.forEach(removeVisual);
        if (!mesh) return;
        var style = elMarker ? elMarker.value : 'solid';
        cuts.forEach(function (c) {
            if (!cutShown(c)) return;
            var group = new THREE.Group();
            if (style !== 'none') {
                if (style === 'thin' || style === 'dotted') buildThinMarker(c, group, style === 'dotted');
                else buildSolidMarker(c, group);
            }
            if (c.id === activeId && cuts.length > 1) buildHalo(c, group);
            group.position.copy(c.center);
            scene.add(group);
            c.visual = group;
        });
        lastScale = -1;
        updateScale();
        requestRender();
    }

    // Distance-based marker size, same law as viewer.js orbScale() but
    // measured to each cut's center. Each part declares what to do with
    // its local Y: 'iso' sphere, 'model' real length, 'halo' (1.6x orb),
    // <number> screen-sized length.
    function updateScale() {
        var any = false;
        cuts.forEach(function (c) {
            if (!c.visual || !c.visParts.length) return;
            var dist = camera.position.distanceTo(c.center);
            var s = dist < 0.03 ? 0 : dist > 20 ? 0.01 * dist : Math.max(0.1, 0.074 - 0.048 * Math.log(dist));
            if (c._lastScale != null && Math.abs(s - c._lastScale) < Math.abs(c._lastScale) * 1e-3) return;
            c._lastScale = s;
            any = true;
            c.visual.visible = s > 0;
            if (s > 0) {
                var r = s * thickness();
                for (var i = 0; i < c.visParts.length; i++) {
                    var p = c.visParts[i];
                    if (p.axial === 'iso') p.obj.scale.setScalar(r);
                    else if (p.axial === 'halo') p.obj.scale.setScalar(r * 1.6);
                    else if (p.axial === 'model') p.obj.scale.set(r, 1, r);
                    else p.obj.scale.set(r, s * p.axial, r);
                }
            }
        });
        if (any) requestRender();
    }

    // ---- sampling + integration (active cut) --------------------
    function resample() {
        samples = null;
        var c = active();
        if (!c || !mesh || !feaModel || !(c.length > 0)) return;
        var dir = c.dir;
        var eps = modelSpan() * 0.01;
        var back = c.normal.clone().negate();
        var origin = new THREE.Vector3();
        probeRay.near = 0;
        probeRay.far = eps * 2;
        var t = [], v = [], hit = [];
        for (var i = 0; i <= N_SAMPLES; i++) {
            var s = -c.length / 2 + c.length * i / N_SAMPLES;
            origin.copy(c.center).addScaledVector(dir, s).addScaledVector(c.normal, eps);
            probeRay.set(origin, back);
            var hs = probeRay.intersectObject(mesh);
            var val = NaN;
            for (var hI = 0; hI < hs.length; hI++) {
                var fi = hs[hI].faceIndex;
                if (fi == null) continue;
                if (!faceUsable(c, fi)) continue;
                val = queryValue(fi, hs[hI].point);
                break;
            }
            t.push(s); v.push(val); hit.push(val === val);
        }
        var dx = c.length / N_SAMPLES, area = 0, eff = 0;
        for (var k = 0; k < N_SAMPLES; k++) {
            if (hit[k] && hit[k + 1]) { area += 0.5 * (v[k] + v[k + 1]) * dx; eff += dx; }
        }
        samples = { t: t, v: v, hit: hit, area: area, effLen: eff };
    }

    // ---- plot --------------------------------------------------
    function drawPlot() {
        if (!elPlot) return;
        var c = active();
        var ctx = elPlot.getContext('2d');
        var W = elPlot.width, H = elPlot.height;
        ctx.clearRect(0, 0, W, H);
        ctx.fillStyle = '#1a1a1f';
        ctx.fillRect(0, 0, W, H);
        ctx.font = '10px Consolas, monospace';

        if (!samples) {
            ctx.fillStyle = '#555';
            ctx.textAlign = 'center';
            ctx.fillText(c ? 'no data' : 'no section cut', W / 2, H / 2);
            if (elStats) elStats.textContent = '—';
            return;
        }
        var cutLength = c.length;
        var vmin = Infinity, vmax = -Infinity, any = false, iMin = 0, iMax = 0;
        for (var i = 0; i < samples.v.length; i++) {
            if (!samples.hit[i]) continue;
            any = true;
            if (samples.v[i] < vmin) { vmin = samples.v[i]; iMin = i; }
            if (samples.v[i] > vmax) { vmax = samples.v[i]; iMax = i; }
        }
        var dMin = vmin, dMax = vmax;
        if (!any) {
            ctx.fillStyle = '#555';
            ctx.textAlign = 'center';
            ctx.fillText('cut misses all elements', W / 2, H / 2);
            if (elStats) elStats.textContent = 'Area: —   Average: —   Effective length: 0 / ' + fmt(cutLength, 3);
            return;
        }
        vmin = Math.min(vmin, 0); vmax = Math.max(vmax, 0);
        var pad = (vmax - vmin) * 0.06 || 1;
        vmin -= pad; vmax += pad;
        var iFirst = 0, iLast = N_SAMPLES;
        while (iFirst < N_SAMPLES && !samples.hit[iFirst]) iFirst++;
        while (iLast > iFirst && !samples.hit[iLast]) iLast--;
        var padL = 4, padR = 4, padT = 14, padB = 13;
        var pw = W - padL - padR, ph = H - padT - padB;
        function px(i) { return iLast === iFirst ? padL + pw / 2 : padL + pw * (i - iFirst) / (iLast - iFirst); }
        function py(val) { return padT + ph * (1 - (val - vmin) / (vmax - vmin)); }
        var y0 = py(0);
        ctx.strokeStyle = 'rgba(255,255,255,0.18)';
        ctx.beginPath(); ctx.moveTo(padL, y0); ctx.lineTo(W - padR, y0); ctx.stroke();
        var col = markerCss();
        ctx.fillStyle = hexA(col, 0.18);
        for (var a = 0; a <= N_SAMPLES;) {
            if (!samples.hit[a]) { a++; continue; }
            var b = a;
            while (b + 1 <= N_SAMPLES && samples.hit[b + 1]) b++;
            ctx.beginPath(); ctx.moveTo(px(a), y0);
            for (var j = a; j <= b; j++) ctx.lineTo(px(j), py(samples.v[j]));
            ctx.lineTo(px(b), y0); ctx.closePath(); ctx.fill();
            ctx.strokeStyle = col; ctx.lineWidth = 1.4;
            ctx.beginPath(); ctx.moveTo(px(a), py(samples.v[a]));
            for (var j2 = a; j2 <= b; j2++) ctx.lineTo(px(j2), py(samples.v[j2]));
            ctx.stroke();
            a = b + 1;
        }
        ctx.lineWidth = 1;
        var vu = valueUnit(), lu = lengthUnit();
        function guide(val, idx, label, above) {
            var gy = py(val);
            ctx.save(); ctx.setLineDash([3, 3]); ctx.strokeStyle = 'rgba(255,255,255,0.30)';
            ctx.beginPath(); ctx.moveTo(padL, gy); ctx.lineTo(W - padR, gy); ctx.stroke(); ctx.restore();
            ctx.fillStyle = col; ctx.beginPath(); ctx.arc(px(idx), gy, 2.2, 0, Math.PI * 2); ctx.fill();
            var ty = above ? Math.max(9, gy - 4) : Math.min(H - 3, gy + 11);
            ctx.fillStyle = '#ddd'; ctx.textAlign = 'left';
            ctx.fillText(label + ' ' + withUnit(fmt(val, 3), vu), padL + 2, ty);
        }
        if (dMax === dMin) guide(dMax, iMax, 'const', true);
        else { guide(dMax, iMax, 'max', true); guide(dMin, iMin, 'min', (py(dMin) - py(dMax)) >= 14); }
        ctx.fillStyle = '#999'; ctx.textAlign = 'left';
        ctx.fillText(fmt(samples.t[iFirst], 3), padL + 2, H - padB + 10);
        ctx.textAlign = 'right';
        var axLabel = c.axis === 'sloped' ? 'sloped' : c.axis.toUpperCase() + (c.sloped ? ' (sloped)' : '');
        ctx.fillText(fmt(samples.t[iLast], 3) + '  ' + axLabel + (lu ? ' [' + lu + ']' : ''), W - padR - 2, H - padB + 10);
        var avg = samples.effLen > 0 ? samples.area / samples.effLen : NaN;
        var area = areaDisplay(samples.area);
        if (elStats) elStats.innerHTML =
            'Area ∫v·ds: <b style="color:#fff">' + withUnit(fmt(area.value, 4), esc(area.unit)) + '</b><br>' +
            'Average (area / eff. len): <b style="color:#fff">' + withUnit(fmt(avg, 4), esc(vu)) + '</b><br>' +
            'Effective length: <b style="color:#fff">' + fmt(samples.effLen, 3) + '</b>' +
            ' / ' + withUnit(fmt(cutLength, 3), esc(lu));
    }

    function hexA(hex, a) {
        var r = parseInt(hex.slice(1, 3), 16), g = parseInt(hex.slice(3, 5), 16), b = parseInt(hex.slice(5, 7), 16);
        return 'rgba(' + r + ',' + g + ',' + b + ',' + a + ')';
    }

    // ---- list + fields ------------------------------------------
    function fmtPos(v) { return (Math.round(v * 1000) / 1000).toString(); }

    function syncFields() {
        var c = active();
        var has = !!c;
        [elName, elGroup, elPosX, elPosY, elPosZ].forEach(function (el) { if (el) el.disabled = !has; });
        if (elAdjust) elAdjust.disabled = !has;
        if (elDelete) elDelete.disabled = !has;
        if (elName) elName.value = has ? c.name : '';
        if (elGroup) elGroup.value = has && c.group ? c.group : '';
        if (elGroupList) {
            elGroupList.innerHTML = '';
            groups.forEach(function (g) { var o = document.createElement('option'); o.value = g.name; elGroupList.appendChild(o); });
        }
        if (elPosX) elPosX.value = has ? fmtPos(c.center.x) : '';
        if (elPosY) elPosY.value = has ? fmtPos(c.center.y) : '';
        if (elPosZ) elPosZ.value = has ? fmtPos(c.center.z) : '';
        if (elLen) elLen.value = has ? String(c.length) : (defLength ? String(defLength) : '');
        syncAxisButtons();
        syncColorSwatch();
    }

    function renderList() {
        if (!elList) return;
        elList.innerHTML = '';
        if (elCount) elCount.textContent = cuts.length ? cuts.length + ' cut' + (cuts.length === 1 ? '' : 's') : '';
        if (!cuts.length) {
            elList.innerHTML = '<div class="sc-empty">No cuts. Arm "New section cut", then Ctrl+click.</div>';
            return;
        }
        groups.forEach(function (g) {
            var members = cuts.filter(function (c) { return c.group === g.name; });
            if (!members.length) return;
            var hdr = document.createElement('div');
            hdr.className = 'sc-group' + (collapsed[g.name] ? ' collapsed' : '') + (g.visible === false ? ' off' : '');
            var arrow = document.createElement('span'); arrow.className = 'sc-arrow'; arrow.innerHTML = '&#9654;';
            var name = document.createElement('span'); name.className = 'sc-gname'; name.textContent = g.name;
            var cnt = document.createElement('span'); cnt.className = 'sc-gcount'; cnt.textContent = members.length;
            var vis = document.createElement('span'); vis.className = 'sc-vis'; vis.innerHTML = g.visible === false ? '&#9676;' : '&#9673;';
            vis.title = 'Show / hide the whole group';
            vis.addEventListener('click', function (e) { e.stopPropagation(); toggleGroupVis(g.name); });
            hdr.appendChild(arrow); hdr.appendChild(name); hdr.appendChild(cnt); hdr.appendChild(vis);
            hdr.addEventListener('click', function () { collapsed[g.name] = !collapsed[g.name]; renderList(); });
            elList.appendChild(hdr);
            if (!collapsed[g.name]) members.forEach(function (c) { elList.appendChild(makeRow(c, true)); });
        });
        var loose = cuts.filter(function (c) { return !c.group; });
        if (loose.length && groups.length) {
            var sep = document.createElement('div'); sep.className = 'sc-ungrouped'; sep.textContent = 'Ungrouped';
            elList.appendChild(sep);
        }
        loose.forEach(function (c) { elList.appendChild(makeRow(c, false)); });
    }

    function makeRow(c, grouped) {
        var row = document.createElement('div');
        row.className = 'sc-row' + (grouped ? ' grouped' : '') + (c.id === activeId ? ' selected' : '') + (cutShown(c) ? '' : ' off');
        row.addEventListener('click', function () { selectCut(c.id); });
        var dot = document.createElement('span'); dot.className = 'sc-dot';
        dot.style.background = AXIS_CSS[c.axis] || AXIS_CSS.sloped;
        if (c.sloped) dot.classList.add('sloped');
        dot.title = c.axis === 'sloped' ? 'sloped direction' : 'along ' + c.axis.toUpperCase() + (c.sloped ? ', bent onto the panel' : '');
        var name = document.createElement('span'); name.className = 'sc-name'; name.textContent = c.name;
        var info = document.createElement('span'); info.className = 'sc-info';
        info.textContent = (c.axis === 'sloped' ? '∠' : c.axis.toUpperCase() + (c.sloped ? '∠' : '')) + ' ' + fmt(c.length, 3) + (lengthUnit() ? ' ' + lengthUnit() : '');
        var vis = document.createElement('span'); vis.className = 'sc-vis'; vis.innerHTML = c.visible ? '&#9673;' : '&#9676;';
        vis.title = 'Show / hide this cut';
        vis.addEventListener('click', function (e) { e.stopPropagation(); toggleCutVis(c.id); });
        row.appendChild(dot); row.appendChild(name); row.appendChild(info); row.appendChild(vis);
        return row;
    }

    // ---- orchestration -----------------------------------------
    function afterMutation(c) {
        syncEnvelope();
        rebuild();
    }
    function rebuild() {          // params changed: data, then visual + UI
        applyIsolation();         // before resample: it scopes the probe
        resample();
        updateVisuals();          // after resample: the marker shows where data was found
        drawPlot();
        syncFields();
        renderList();
    }

    function refresh() {          // displayed field changed: data (+ marker runs)
        syncModelUnits();
        if (!active()) return;
        resample();
        updateVisuals();
        drawPlot();
    }

    function clearAll() {
        cuts.forEach(removeVisual);
        cuts = [];
        groups = [];
        activeId = null;
        samples = null;
        isolated = null;
    }

    function onModelLoaded() {    // geometry available: re-find the elements under saved cuts
        syncModelUnits();
        cuts.forEach(resolveElem);
        rebuild();
    }

    function onModelCleared() {   // model disposed / replaced: cuts stay (they belong to the sidecar)
        cuts.forEach(removeVisual);
        cuts.forEach(function (c) { c.elem = -1; });
        samples = null;
        isolated = null;          // fresh geometry draws fully visible
        lastModel = null;         // next model re-syncs the units dropdown
        setArmed(false);
        setAdjusting(false);
        drawPlot();
        renderList();
    }

    syncModelUnits();
    syncFields();
    renderList();
    drawPlot();

    return {
        wantsPick: wantsPick,
        placeCenter: placeCenter,
        refresh: refresh,
        updateScale: updateScale,
        onModelLoaded: onModelLoaded,
        onModelCleared: onModelCleared,
        onEnvelope: onEnvelope,
        importLegacy: importLegacy,
        disarm: function () { setArmed(false); setAdjusting(false); },
        // test hooks (viewer/tests/test_sectioncuts.js)
        _itemToCut: itemToCut,
        _cutToItem: cutToItem,
        _axisOf: axisOf,
        _cuts: function () { return cuts; },
        _groups: function () { return groups; },
        _select: selectCut,
        _delete: deleteCut,
        _importLegacy: importLegacy,
        _makeCut: makeCut,
        _setSloped: setSloped,
        _dataRuns: dataRuns,
        _setSamples: function (s) { samples = s; },
        _push: function (c) { cuts.push(c); activeId = c.id; afterMutation(c); }
    };
})();
