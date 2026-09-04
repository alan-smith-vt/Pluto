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
// Node groups (2026-09-04): `nodeIds` members resolve to a per-node
// category the same way and are drawn as a point layer (nodeMarkers, one
// THREE.Points, square screen-space markers, vertex colours from the
// palette) while the switch is on; the section-cut isolate hides markers
// off the kept panel via writeVis. Markers sit on the undeformed node
// positions (no displacement scale). Unlike elements, node groups do NOT
// shadow each other: every enabled group draws all its nodes, and where
// two markers would land on the same spot (a node in two groups, or
// coincident joints such as a plate joint over its ground joint) the
// later group's marker is nudged aside by a small world-space step per
// occupant, so both show. Rows report how many were nudged. groupOf()
// for nodes still answers last-enabled-wins. The Groups tab lists element groups
// and node groups on two sub-tabs (Elements | Nodes). EVERY group starts
// unticked unless the sidecar item says `hidden: false`; ticking writes the
// flag so Export remembers it. (Orbs on top were tried and rejected the
// same day -- too much clutter.)
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
    var nodeMarkers = null;   // THREE.Points for painted node-group members (or null)
    var nodeKeepMask = null;  // Uint8Array from the section-cut isolate (null = all)
    var nodeMembers = [];     // per group: Int32Array of node indices (resolved members, in order)
    var NODE_POINT_PX = 7;
    var NUDGE_SPAN_FRACTION = 0.004;   // coincident-marker step = model span x this
    var listTab = 'elements';        // 'elements' | 'nodes' -- which sub-tab is showing
    try { listTab = localStorage.getItem('pluto.groupsTab') === 'nodes' ? 'nodes' : 'elements'; } catch (e0) {}

    var elFile   = document.getElementById('featFile');
    var elToggle = document.getElementById('featGroups');
    var elName   = document.getElementById('featName');
    var elHint   = document.getElementById('featHint');
    var elPanel  = document.getElementById('grPanel');
    var elTab    = document.getElementById('grTab');
    var elList   = document.getElementById('grList');
    var elNodeList = document.getElementById('grNodeList');
    var elTabE   = document.getElementById('grTabElems');
    var elTabN   = document.getElementById('grTabNodes');
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

    function nodeIdMap(model) {
        if (model._featNodeIdMap) return model._featNodeIdMap;
        var m = new Map();
        var ids = model.nodeIds || [];
        for (var i = 0; i < ids.length; i++) m.set(ids[i], i);
        model._featNodeIdMap = m;
        return m;
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
        if (window.FEASectionCut && FEASectionCut.onEnvelope) FEASectionCut.onEnvelope(envelope);
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
        var mid = (u && u.meta && u.meta.modelId) ? String(u.meta.modelId).split('/').pop() : '';
        fileName = (mid || 'untitled') + '.features.json';
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
        var out = { shell: null, beam: null, nodes: null, unmatched: 0 };
        nodeMembers = [];
        var nNodes = (feaModel.header && feaModel.header.nNodes) || (feaModel.nodeIds ? feaModel.nodeIds.length : 0);
        if (nNodes > 0) { out.nodes = new Float32Array(nNodes); out.nodes.fill(-1); }
        var nmap = nodeIdMap(feaModel);
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
            var count = 0, nodeCount = 0, nodeIdx = [];
            var hidden = groupHidden(g);
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
                    (m.nodeIds || []).forEach(function (id) {
                        var ni = nmap.get(id);
                        if (ni === undefined) { out.unmatched++; return; }
                        nodeCount++;
                        nodeIdx.push(ni);
                        if (!hidden && out.nodes) out.nodes[ni] = gi;
                    });
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
                             count: count, nodeCount: nodeCount, hidden: hidden, painted: 0, nodePainted: 0, nodeNudged: 0,
                             tags: Array.isArray(g.tags) ? g.tags : [] });
            nodeMembers.push(Int32Array.from(nodeIdx));
        });
        // painted = elements whose final category is this group (after precedence)
        ['shell', 'beam'].forEach(function (fam) {
            var arr = out[fam];
            if (!arr) return;
            for (var i = 0; i < arr.length; i++) if (arr[i] >= 0) groupList[arr[i]].painted++;
        });
        markerData = buildMarkerData();     // also fills nodePainted / nodeNudged
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

    // ---- node markers ---------------------------------------------------
    function disposeMarkers() {
        if (!nodeMarkers) return;
        if (typeof scene !== 'undefined' && scene) scene.remove(nodeMarkers);
        nodeMarkers.geometry.dispose();
        nodeMarkers.material.dispose();
        nodeMarkers = null;
    }
    var markerData = null;    // { n, pos: Float32Array, col: Float32Array } from buildMarkerData
    // Model span (largest bbox side) from the node table, cached on the model.
    function modelSpan(model) {
        if (model._featSpan) return model._featSpan;
        var nd = model.nodes || [], lo = [Infinity, Infinity, Infinity], hi = [-Infinity, -Infinity, -Infinity];
        for (var i = 0; i < nd.length; i += 3) for (var a = 0; a < 3; a++) {
            var v = nd[i + a];
            if (v < lo[a]) lo[a] = v;
            if (v > hi[a]) hi[a] = v;
        }
        var span = Math.max(hi[0] - lo[0], hi[1] - lo[1], hi[2] - lo[2]);
        model._featSpan = (isFinite(span) && span > 0) ? span : 1;
        return model._featSpan;
    }
    // Nudge directions for the 1st, 2nd, 3rd... extra occupant of a spot: steps
    // along +x, +y, +z, then the diagonals, scaled by the occupant number.
    var NUDGE_DIRS = [[1, 0, 0], [0, 1, 0], [0, 0, 1], [1, 1, 0], [0, 1, 1], [1, 0, 1], [1, 1, 1]];
    // Marker list: every enabled node group draws all its (kept) nodes in list
    // order; a marker landing on an occupied spot (same rounded position) is
    // nudged by step x occupant count. Fills nodePainted / nodeNudged per group.
    function buildMarkerData() {
        for (var g = 0; g < groupList.length; g++) { groupList[g].nodePainted = 0; groupList[g].nodeNudged = 0; }
        if (!feaModel || !feaModel.nodes) return null;
        var nodes = feaModel.nodes, step = modelSpan(feaModel) * NUDGE_SPAN_FRACTION;
        var scale = 1e4, occupied = {};
        var px = [], py = [], pz = [], cr = [], cg = [], cb = [];
        for (var gi = 0; gi < groupList.length; gi++) {
            var info = groupList[gi];
            if (info.hidden || !nodeMembers[gi] || !nodeMembers[gi].length) continue;
            var c = info.rgb, mem = nodeMembers[gi];
            for (var k = 0; k < mem.length; k++) {
                var ni = mem[k];
                if (nodeKeepMask && !nodeKeepMask[ni]) continue;
                var x = nodes[ni * 3], y = nodes[ni * 3 + 1], z = nodes[ni * 3 + 2];
                var key = Math.round(x * scale) + ',' + Math.round(y * scale) + ',' + Math.round(z * scale);
                var occ = occupied[key] || 0;
                occupied[key] = occ + 1;
                if (occ > 0) {
                    var d = NUDGE_DIRS[(occ - 1) % NUDGE_DIRS.length], m = step * (1 + Math.floor((occ - 1) / NUDGE_DIRS.length));
                    x += d[0] * m; y += d[1] * m; z += d[2] * m;
                    info.nodeNudged++;
                }
                info.nodePainted++;
                px.push(x); py.push(y); pz.push(z);
                cr.push(c[0] / 255); cg.push(c[1] / 255); cb.push(c[2] / 255);
            }
        }
        var n = px.length;
        if (n === 0) return null;
        var pos = new Float32Array(n * 3), col = new Float32Array(n * 3);
        for (var i = 0; i < n; i++) {
            pos[i * 3] = px[i]; pos[i * 3 + 1] = py[i]; pos[i * 3 + 2] = pz[i];
            col[i * 3] = cr[i]; col[i * 3 + 1] = cg[i]; col[i * 3 + 2] = cb[i];
        }
        return { n: n, pos: pos, col: col };
    }
    // One THREE.Points over the marker list, vertex-coloured by group. Depth-tested,
    // so a node inside solid geometry (the ring wall) is hidden by it.
    function rebuildMarkers(on) {
        disposeMarkers();
        if (!on || !resolved || !feaModel || !feaModel.nodes) return;
        markerData = buildMarkerData();
        if (typeof THREE === 'undefined' || typeof scene === 'undefined' || !scene) return;
        if (!markerData) return;
        var geom = new THREE.BufferGeometry();
        geom.setAttribute('position', new THREE.BufferAttribute(markerData.pos, 3));
        geom.setAttribute('color', new THREE.BufferAttribute(markerData.col, 3));
        var mat = new THREE.PointsMaterial({ size: NODE_POINT_PX, sizeAttenuation: false, vertexColors: true,
                                             depthTest: true, depthWrite: false });
        nodeMarkers = new THREE.Points(geom, mat);
        nodeMarkers.renderOrder = 5;
        nodeMarkers.frustumCulled = false;
        scene.add(nodeMarkers);
    }
    // Section-cut isolate hook (sectionCut.js): null = show every painted node.
    function writeVis(nodeKeep) {
        nodeKeepMask = nodeKeep || null;
        rebuildMarkers(enabled && !!resolved);
        renderList(enabled && !!resolved);      // painted / nudged counts follow the isolate
        needsRender = true;
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
        rebuildMarkers(on);
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

    function isNodeGroup(info) { return !!info && info.count === 0 && info.nodeCount > 0; }
    // A group with only nodeIds members, from the envelope item itself.
    function itemIsNodes(g) {
        var ms = Array.isArray(g.members) ? g.members : (g.members ? [g.members] : []);
        if (!ms.length) return false;
        for (var i = 0; i < ms.length; i++) {
            var m = ms[i];
            if (m.predicateId) return false;
            if (!(m.domain === 'nodes' || (m.nodeIds && m.nodeIds.length))) return false;
        }
        return true;
    }
    // Enabled state: a group paints only when the item says `hidden: false`;
    // absent or true = off. Ticking always writes the flag explicitly.
    function groupHidden(g) {
        return g.hidden === undefined ? true : !!g.hidden;
    }
    function writeHidden(g, hidden) {
        g.hidden = !!hidden;
    }
    function showTab(tab) {
        listTab = tab === 'nodes' ? 'nodes' : 'elements';
        try { localStorage.setItem('pluto.groupsTab', listTab); } catch (e0) {}
        if (elList) elList.hidden = listTab !== 'elements';
        if (elNodeList) elNodeList.hidden = listTab !== 'nodes';
        if (elTabE) elTabE.classList.toggle('active', listTab === 'elements');
        if (elTabN) elTabN.classList.toggle('active', listTab === 'nodes');
    }

    function renderList(on) {
        if (!elList) return;
        elList.innerHTML = '';
        if (elNodeList) elNodeList.innerHTML = '';
        showTab(listTab);
        var list = items();
        var can = list.length > 0 && !!feaModel;
        [elAll, elNone, elInvert].forEach(function (b) { if (b) b.disabled = !can; });
        if (!list.length) {
            var empty = '<div class="gr-empty">' + (envelope ? 'No groups in file' : 'No features file') + '</div>';
            elList.innerHTML = empty;
            if (elNodeList) elNodeList.innerHTML = empty;
            return;
        }
        var nElemRows = 0, nNodeRows = 0;
        list.forEach(function (g, gi) {
            var info = groupList[gi] || { name: g.name, rgb: [200, 200, 200], count: 0, nodeCount: 0, painted: 0, nodePainted: 0, hidden: groupHidden(g), tags: [] };
            var isNodes = isNodeGroup(info);
            var target = isNodes ? (elNodeList || elList) : elList;
            if (isNodes) nNodeRows++; else nElemRows++;
            var row = document.createElement('div');
            row.className = 'gr-row' + (info.hidden ? ' off' : '') + (isNodes ? ' nodes' : '');
            row.draggable = true;

            var cb = document.createElement('input');
            cb.type = 'checkbox'; cb.className = 'gr-check'; cb.checked = !info.hidden;
            cb.title = isNodes ? 'Node group: draw its nodes as points in this colour' : 'Paint this group';
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
            if (isNodes) {
                cnt.textContent = info.nodePainted + '/' + info.nodeCount + ' nodes';
                if (!info.hidden && info.nodeNudged > 0) {
                    cnt.textContent += ' \u00b7 ' + info.nodeNudged + ' nudged';
                    cnt.classList.add('shadowed');
                    cnt.title = info.nodeNudged + ' marker(s) sit on a spot already taken by a group higher in the list (or a coincident joint) and are drawn nudged aside';
                }
            } else {
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
            target.appendChild(row);
        });
        if (nElemRows === 0) elList.innerHTML = '<div class="gr-empty">No element groups</div>';
        var rest = document.createElement('div');
        rest.className = 'gr-ungrouped';
        rest.innerHTML = '<span class="sw"></span><span>ungrouped' + (on ? '' : ' (colouring off)') + '</span>';
        elList.appendChild(rest);
        if (elNodeList) {
            if (nNodeRows === 0) elNodeList.innerHTML = '<div class="gr-empty">No node groups</div>';
            var nh = document.createElement('div');
            nh.className = 'gr-ungrouped';
            nh.innerHTML = '<span>points at the nodes' + (on ? '' : ' (colouring off)') + '</span>';
            elNodeList.appendChild(nh);
        }
    }
    function clearDrop() {
        [elList, elNodeList].forEach(function (el) {
            if (!el) return;
            Array.prototype.forEach.call(el.querySelectorAll('.gr-row'), function (r) { r.classList.remove('drop-before', 'drop-after'); });
        });
    }
    function setHidden(gi, hidden) {
        var g = items()[gi]; if (!g) return;
        writeHidden(g, hidden);
        refresh();
    }
    // All / None / Invert act on the sub-tab that is showing.
    function setAllHidden(fn) {
        items().forEach(function (g, gi) {
            var nodes = isNodeGroup(groupList[gi]);
            if (nodes !== (listTab === 'nodes')) return;
            writeHidden(g, fn(groupHidden(g)));
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
        var arr = resolved[family === 'node' ? 'nodes' : family];
        if (!arr || e < 0 || e >= arr.length) return null;
        var gi = arr[e];
        return gi >= 0 ? groupList[gi].name : null;
    }

    // ---- hooks from viewer.js -------------------------------------------
    function onModelLoaded() {
        if (feaModel) { feaModel._featIdMap = null; feaModel._featNodeIdMap = null; }
        nodeKeepMask = null;
        checkBinding();
        resolve();
        if (elToggle) elToggle.disabled = !resolved;
        sync();
    }
    var DEMO_NAME = 'PlateDemo.features.json';
    function onModelCleared() {
        resolved = null;
        nodeKeepMask = null;
        disposeMarkers();
        if (palette) { palette.dispose(); palette = null; }
        // The demo's sample sidecar must not outlive the demo: a real model
        // loaded over it would otherwise inherit demo groups and export as
        // "PlateDemo.features.json" (2026-09-04).
        if (envelope && fileName === DEMO_NAME) {
            envelope = null; fileName = '';
            if (elName) elName.textContent = 'no features file';
            if (elHint) elHint.textContent = '';
            if (window.FEAPredicates && FEAPredicates.onEnvelope) FEAPredicates.onEnvelope(null);
            if (window.FEASectionCut && FEASectionCut.onEnvelope) FEASectionCut.onEnvelope(null);
            renderList(false);
        }
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
    if (elTabE) elTabE.addEventListener('click', function () { showTab('elements'); });
    if (elTabN) elTabN.addEventListener('click', function () { showTab('nodes'); });
    if (elAll) elAll.addEventListener('click', function () { setAllHidden(function () { return false; }); });
    if (elNone) elNone.addEventListener('click', function () { setAllHidden(function () { return true; }); });
    if (elInvert) elInvert.addEventListener('click', function () { setAllHidden(function (h) { return !h; }); });
    // Download the whole sidecar (groups, predicates, section cuts); the
    // section-cut panel's Export button calls this too.
    function download() {
        var json = exportJson();
        if (!json) { log('Features: nothing to export.'); return; }
        var a = document.createElement('a');
        a.href = URL.createObjectURL(new Blob([json], { type: 'application/json' }));
        a.download = fileName.replace(/ \(unsaved\)$/, '') || 'features.json';
        document.body.appendChild(a); a.click(); document.body.removeChild(a);
        setTimeout(function () { URL.revokeObjectURL(a.href); }, 1000);
    }
    if (elExport) elExport.addEventListener('click', download);

    return {
        loadFile: loadFile,
        setEnvelope: setEnvelope,
        ensureEnvelope: ensureEnvelope,
        refresh: refresh,
        fileName: function () { return fileName; },
        onModelLoaded: onModelLoaded,
        onModelCleared: onModelCleared,
        sync: sync,
        writeVis: writeVis,
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
        download: download,
        // test hooks (viewer/tests/test_groups.js)
        _groupList: function () { return groupList; },
        _resolved: function () { return resolved; },
        _markers: function () { return markerData; },
        _moveGroup: moveGroup,
        _setEnabled: function (on) { enabled = !!on; sync(); }
    };
})();
