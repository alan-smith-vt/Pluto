// ================================================================
// predicates.js  --  geometric predicate module (FEAPredicates).
//
// Port of the old viewer's predicate.js onto the v4 viewer. A
// predicate is a tree of plane / finite-plane leaves combined with
// AND / OR ops; any node can be negated. A leaf matches an element
// when its centroid lies within `tol` of the plane (slab test, both
// sides), its normal is within `normal_tol_deg` of the plane normal
// (shell elements only; beams and nodes skip the normal test), and,
// for finite leaves, its in-plane (u,v) offset is inside the
// width x length rectangle (u axes from the world axis least aligned
// with the normal, rotated by angle_deg).
//
// Trees are stored in the features sidecar (envelope.predicates.items)
// in the PRODUCTION dialect the C# Groups.cs parser already reads:
// kind: "plane"|"finitePlane"|"and"|"or", negated, point, normal, tol,
// normal_tol_deg, width/length/angle_deg. Points are WORLD (plant)
// coordinates; the engine subtracts the exporter recenter
// (FEAFeatures.worldOffset) and the viewer recenter (viewRecenter)
// before testing against scene-space geometry.
//
// FEAFeatures calls resolveMembers(predicateId) to expand groups with
// runtime predicate members into explicit per-domain REAL-ID lists.
//
// Depends on viewer.js globals: feaModel, feaBuild, scene, needsRender,
// viewRecenter; optional FEABeams, FEAFeatures. All DOM access is
// guarded so the module also loads headless (Node vm) for tests.
// ================================================================

var FEAPredicates = (function () {

    // ---- state ----------------------------------------------------------
    var items = [];             // runtime expressions
    var selectedIds = [];       // runtime node ids ('n#')
    var armedMode = null;       // 'place' | 'adjust' | null
    var showExtents = true;
    var ridCounter = 0;
    var lastLeafDefaults = {
        tol: 0.1,
        normal_tol_deg: 5.0,
        finite: false,
        width: 240,
        length: 240,
        angle_deg: 0
    };

    // geometry caches (scene/local coordinates), rebuilt per model
    var shellCg = null;         // Float32Array[nElem*3] element centroids
    var elemVertStart = null;   // Int32Array[nElem] first vertex of element in feaBuild geometry
    var beamCg = null;          // Float32Array[nBeam*3] beam midpoints
    var beamAxis = null;        // Float32Array[nBeam*3] unit axis P0->P1

    // visuals
    var highlightObj = null;    // matched-set overlay (Group)
    var extentsObj = null;      // finite-leaf ghost rectangle
    var tooltipEl = null;
    var dragIds = null;

    // ---- DOM (all guarded: absent when running headless) ----------------
    var elPanel   = document.getElementById('pdPanel');
    var elTab     = document.getElementById('pdTab');
    var elNew     = document.getElementById('pdNew');
    var elAdjust  = document.getElementById('pdAdjust');
    var elDelete  = document.getElementById('pdDelete');
    var elAnd     = document.getElementById('pdAnd');
    var elOr      = document.getElementById('pdOr');
    var elNot     = document.getElementById('pdNot');
    var elInspector = document.getElementById('pdInspector');
    var elTree    = document.getElementById('pdTree');
    var elGroup   = document.getElementById('pdGroup');
    var elExport  = document.getElementById('pdExport');
    var elImport  = document.getElementById('pdImport');
    var elImportFile = document.getElementById('pdImportFile');

    // ---- small helpers --------------------------------------------------
    function round6(v) { return parseFloat(v.toFixed(6)); }
    function newRid() { return 'n' + (ridCounter++); }
    function newGuid(prefix) {
        var s = '';
        for (var i = 0; i < 16; i++) s += (Math.floor(Math.random() * 16)).toString(16);
        return prefix + s;
    }
    function say(msg) { if (typeof log === 'function') log(msg); }
    function render() { needsRender = true; }

    // world = scene/local + totalOffset (exporter recenter + viewer recenter)
    function totalOffset() {
        var o = (window.FEAFeatures && FEAFeatures.worldOffset) ? FEAFeatures.worldOffset() : null;
        var r = (typeof viewRecenter !== 'undefined') ? viewRecenter : null;
        return [
            (o ? o[0] : 0) + (r ? r[0] : 0),
            (o ? o[1] : 0) + (r ? r[1] : 0),
            (o ? o[2] : 0) + (r ? r[2] : 0)
        ];
    }

    // ---- geometry caches ------------------------------------------------
    function clearCaches() {
        shellCg = null; elemVertStart = null; beamCg = null; beamAxis = null;
    }

    function beamViewOrNull() {
        return (window.FEABeams && FEABeams.view()) ? FEABeams.view() : null;
    }

    function ensureCaches() {
        if (feaModel && feaBuild && !shellCg) {
            var nElem = feaModel.header.nElements;
            var nodes = feaModel.nodes;
            shellCg = new Float32Array(nElem * 3);
            elemVertStart = new Int32Array(nElem);
            var vptr = 0;
            for (var e = 0; e < nElem; e++) {
                var nc = feaBuild.elemNCount[e];
                var cx = 0, cy = 0, cz = 0;
                for (var k = 0; k < nc; k++) {
                    var ni = feaBuild.elemCorners[e * 4 + k];
                    cx += nodes[ni * 3]; cy += nodes[ni * 3 + 1]; cz += nodes[ni * 3 + 2];
                }
                shellCg[e * 3] = cx / nc; shellCg[e * 3 + 1] = cy / nc; shellCg[e * 3 + 2] = cz / nc;
                elemVertStart[e] = vptr;
                vptr += (nc === 4 ? 6 : 3);
            }
        }
        var bv = beamViewOrNull();
        if (bv && !beamCg) {
            var nB = bv.header.nElements;
            var REC = bv.elemRecordU32 || 6;
            beamCg = new Float32Array(nB * 3);
            beamAxis = new Float32Array(nB * 3);
            for (var b = 0; b < nB; b++) {
                var n0 = bv.elems[b * REC + 1], n1 = bv.elems[b * REC + 2];
                var x0 = bv.nodes[n0 * 3], y0 = bv.nodes[n0 * 3 + 1], z0 = bv.nodes[n0 * 3 + 2];
                var x1 = bv.nodes[n1 * 3], y1 = bv.nodes[n1 * 3 + 1], z1 = bv.nodes[n1 * 3 + 2];
                beamCg[b * 3] = (x0 + x1) / 2; beamCg[b * 3 + 1] = (y0 + y1) / 2; beamCg[b * 3 + 2] = (z0 + z1) / 2;
                var ax = x1 - x0, ay = y1 - y0, az = z1 - z0;
                var len = Math.sqrt(ax * ax + ay * ay + az * az);
                if (len > 1e-12) { ax /= len; ay /= len; az /= len; }
                beamAxis[b * 3] = ax; beamAxis[b * 3 + 1] = ay; beamAxis[b * 3 + 2] = az;
            }
        }
    }

    // ---- node factories -------------------------------------------------
    // In-plane axes: reference = world axis least aligned with the normal,
    // projected into the plane, rotated about the normal by angle_deg.
    function buildPredAxes(leaf) {
        var nx = leaf.normal[0], ny = leaf.normal[1], nz = leaf.normal[2];
        var ax = Math.abs(nx), ay = Math.abs(ny), az = Math.abs(nz);
        var rx, ry, rz;
        if (ax <= ay && ax <= az)   { rx = 1; ry = 0; rz = 0; }
        else if (ay <= az)          { rx = 0; ry = 1; rz = 0; }
        else                        { rx = 0; ry = 0; rz = 1; }
        var d = rx * nx + ry * ny + rz * nz;
        var u0x = rx - d * nx, u0y = ry - d * ny, u0z = rz - d * nz;
        var ulen = Math.sqrt(u0x * u0x + u0y * u0y + u0z * u0z);
        u0x /= ulen; u0y /= ulen; u0z /= ulen;
        var theta = (leaf.angle_deg || 0) * Math.PI / 180;
        var c = Math.cos(theta), s = Math.sin(theta);
        var cx = ny * u0z - nz * u0y;
        var cy = nz * u0x - nx * u0z;
        var cz = nx * u0y - ny * u0x;
        var ux = u0x * c + cx * s, uy = u0y * c + cy * s, uz = u0z * c + cz * s;
        leaf.uAxis = [ux, uy, uz];
        leaf.vAxis = [ny * uz - nz * uy, nz * ux - nx * uz, nx * uy - ny * ux];
    }

    function makeLeaf(worldPoint, normal) {
        var node = {
            rid: newRid(),
            kind: 'leaf',
            negated: false,
            point: [worldPoint[0], worldPoint[1], worldPoint[2]],
            normal: [round6(normal[0]), round6(normal[1]), round6(normal[2])],
            tol: lastLeafDefaults.tol,
            normal_tol_deg: lastLeafDefaults.normal_tol_deg,
            finite: lastLeafDefaults.finite,
            width: lastLeafDefaults.width,
            length: lastLeafDefaults.length,
            angle_deg: lastLeafDefaults.angle_deg,
            matches: null,
            count: 0
        };
        node.cosNormTol = Math.cos(node.normal_tol_deg * Math.PI / 180);
        buildPredAxes(node);
        return node;
    }

    function makeOp(opKind, children) {
        return {
            rid: newRid(), kind: 'op', op: opKind, negated: false,
            children: children || [], collapsed: false, matches: null, count: 0
        };
    }

    function makeItem(rootNode, name, target) {
        return {
            id: newGuid('p-'),
            name: name || ('Predicate ' + (items.length + 1)),
            target: target || 'elements',
            root: rootNode,
            _raw: null            // envelope item this mirrors (unknown keys preserved)
        };
    }

    // ---- tree traversal -------------------------------------------------
    function walkFind(node, rid) {
        if (node.rid === rid) return node;
        if (node.kind === 'op') {
            for (var i = 0; i < node.children.length; i++) {
                var h = walkFind(node.children[i], rid);
                if (h) return h;
            }
        }
        return null;
    }
    function findNode(rid) {
        for (var i = 0; i < items.length; i++) {
            var n = walkFind(items[i].root, rid);
            if (n) return { node: n, item: items[i] };
        }
        return null;
    }
    function walkParent(node, parent, rid) {
        if (node.rid === rid) return { parent: parent };
        if (node.kind === 'op') {
            for (var i = 0; i < node.children.length; i++) {
                var h = walkParent(node.children[i], node, rid);
                if (h) return h;
            }
        }
        return null;
    }
    function findParent(rid) {
        for (var i = 0; i < items.length; i++) {
            var h = walkParent(items[i].root, null, rid);
            if (h) return h;      // parent === null when rid is the root
        }
        return null;
    }
    function itemOf(rid) { var h = findNode(rid); return h ? h.item : null; }
    function findItem(predicateId) {
        for (var i = 0; i < items.length; i++) if (items[i].id === predicateId) return items[i];
        return null;
    }

    // ---- evaluation -----------------------------------------------------
    function familiesFor(item) {
        if (item.target === 'nodes') return feaModel ? ['node'] : [];
        var fams = [];
        var doms = item.domains;   // optional ['shells'] / ['beams'] filter
        var wantShell = !doms || doms.indexOf('shells') !== -1;
        var wantBeam = !doms || doms.indexOf('beams') !== -1;
        if (wantShell && feaModel && feaBuild && feaModel.header.nElements > 0) fams.push('shell');
        if (wantBeam && beamViewOrNull()) fams.push('beam');
        return fams;
    }

    function universeSize(fam) {
        if (fam === 'shell') return feaModel.header.nElements;
        if (fam === 'beam') { var bv = beamViewOrNull(); return bv ? bv.header.nElements : 0; }
        return feaModel.header.nNodes;
    }

    function emptyMatches(fams) {
        var m = {};
        fams.forEach(function (f) { m[f] = []; });
        return m;
    }

    function evaluateItem(item) {
        ensureCaches();
        evalNode(item.root, item, familiesFor(item));
    }

    function evalNode(node, item, fams) {
        if (node.kind === 'leaf') { evalLeaf(node, fams); return; }
        node.children.forEach(function (c) { evalNode(c, item, fams); });
        var m = emptyMatches(fams);
        fams.forEach(function (fam) {
            m[fam] = (node.op === 'and')
                ? intersectChildren(node.children, fam)
                : unionChildren(node.children, fam);
        });
        node.matches = m;
        node.count = countOf(m);
    }

    function countOf(m) {
        var c = 0;
        for (var f in m) c += m[f].length;
        return c;
    }

    // negated -> universe minus matches (per family)
    function effectiveSet(node, fam) {
        var arr = (node.matches && node.matches[fam]) || [];
        if (!node.negated) return new Set(arr);
        var matched = new Set(arr);
        var out = new Set();
        var n = universeSize(fam);
        for (var i = 0; i < n; i++) if (!matched.has(i)) out.add(i);
        return out;
    }

    function intersectChildren(children, fam) {
        if (children.length === 0) return [];
        var sets = children.map(function (c) { return effectiveSet(c, fam); });
        sets.sort(function (a, b) { return a.size - b.size; });
        var out = [];
        sets[0].forEach(function (idx) {
            for (var i = 1; i < sets.length; i++) if (!sets[i].has(idx)) return;
            out.push(idx);
        });
        return out;
    }

    function unionChildren(children, fam) {
        var combined = new Set();
        children.forEach(function (c) {
            effectiveSet(c, fam).forEach(function (idx) { combined.add(idx); });
        });
        return Array.from(combined);
    }

    function evalLeaf(leaf, fams) {
        var off = totalOffset();
        var px = leaf.point[0] - off[0], py = leaf.point[1] - off[1], pz = leaf.point[2] - off[2];
        var nx = leaf.normal[0], ny = leaf.normal[1], nz = leaf.normal[2];
        var tol = leaf.tol, cosTol = leaf.cosNormTol;

        var finite = leaf.finite;
        var halfW = 0, halfL = 0, ux = 0, uy = 0, uz = 0, vx = 0, vy = 0, vz = 0;
        if (finite) {
            halfW = leaf.width / 2;
            halfL = leaf.length / 2;
            if (!isFinite(halfW) || !isFinite(halfL) || halfW <= 0 || halfL <= 0) {
                finite = false;
            } else {
                ux = leaf.uAxis[0]; uy = leaf.uAxis[1]; uz = leaf.uAxis[2];
                vx = leaf.vAxis[0]; vy = leaf.vAxis[1]; vz = leaf.vAxis[2];
            }
        }

        // slab + (optional) finite test against a packed xyz array; the
        // normal test only applies to shells (norms non-null).
        function scan(cg, norms, out) {
            var N = cg.length;
            for (var k = 0; k < N; k += 3) {
                var dx = cg[k] - px, dy = cg[k + 1] - py, dz = cg[k + 2] - pz;
                var dist = dx * nx + dy * ny + dz * nz;
                if (dist < 0) dist = -dist;
                if (dist > tol) continue;
                if (norms) {
                    var dot = norms[k] * nx + norms[k + 1] * ny + norms[k + 2] * nz;
                    if (dot < 0) dot = -dot;
                    if (dot < cosTol) continue;
                }
                if (finite) {
                    var u = dx * ux + dy * uy + dz * uz;
                    if (u < -halfW || u > halfW) continue;
                    var v = dx * vx + dy * vy + dz * vz;
                    if (v < -halfL || v > halfL) continue;
                }
                out.push(k / 3);
            }
        }

        var m = emptyMatches(fams);
        fams.forEach(function (fam) {
            if (fam === 'shell' && shellCg) scan(shellCg, feaBuild.elemNormals, m.shell);
            else if (fam === 'beam' && beamCg) scan(beamCg, null, m.beam);
            else if (fam === 'node') scan(feaModel.nodes, null, m.node);
        });
        leaf.matches = m;
        leaf.count = countOf(m);
    }

    function evaluateAll() {
        if (!feaModel) return;
        items.forEach(evaluateItem);
    }

    // ---- envelope (sidecar) serialization -------------------------------
    // Production dialect: what scripts/lib/readers/Groups.cs parses.
    function exportNode(node) {
        if (node.kind === 'leaf') {
            var p = {
                kind: node.finite ? 'finitePlane' : 'plane',
                negated: node.negated,
                point: node.point.slice(),
                normal: node.normal.slice(),
                tol: node.tol,
                normal_tol_deg: node.normal_tol_deg
            };
            if (node.finite) {
                p.width = node.width;
                p.length = node.length;
                p.angle_deg = node.angle_deg;
            }
            return p;
        }
        return {
            kind: node.op,
            negated: node.negated,
            children: node.children.map(exportNode)
        };
    }

    function importNode(data) {
        if (!data || !data.kind) return null;
        if (data.kind === 'plane' || data.kind === 'finitePlane') {
            var leaf = {
                rid: newRid(),
                kind: 'leaf',
                negated: !!data.negated,
                point: data.point ? data.point.slice() : [0, 0, 0],
                normal: data.normal ? data.normal.slice() : [0, 1, 0],
                tol: typeof data.tol === 'number' ? data.tol : 0.1,
                normal_tol_deg: typeof data.normal_tol_deg === 'number' ? data.normal_tol_deg : 5.0,
                finite: data.kind === 'finitePlane',
                width: typeof data.width === 'number' ? data.width : 240,
                length: typeof data.length === 'number' ? data.length : 240,
                angle_deg: typeof data.angle_deg === 'number' ? data.angle_deg : 0,
                matches: null,
                count: 0
            };
            leaf.cosNormTol = Math.cos(leaf.normal_tol_deg * Math.PI / 180);
            buildPredAxes(leaf);
            return leaf;
        }
        if (data.kind === 'and' || data.kind === 'or') {
            return {
                rid: newRid(), kind: 'op', op: data.kind, negated: !!data.negated,
                children: (data.children || []).map(importNode).filter(Boolean),
                collapsed: true, matches: null, count: 0
            };
        }
        say('Predicates: unknown node kind "' + data.kind + '" skipped.');
        return null;
    }

    // Write runtime state back into the envelope (preserving unknown keys
    // on items we loaded, per the sidecar round-trip rule).
    function syncEnvelope() {
        if (!window.FEAFeatures) return;
        var env = FEAFeatures.envelope();
        if (!env) {
            if (items.length === 0) return;
            env = FEAFeatures.ensureEnvelope ? FEAFeatures.ensureEnvelope() : null;
            if (!env) return;
        }
        if (!env.predicates) env.predicates = { version: 1, items: [] };
        env.predicates.items = items.map(function (it) {
            var raw = it._raw || {};
            raw.id = it.id;
            raw.name = it.name;
            raw.target = it.target;
            if (it.domains) raw.domains = it.domains; else delete raw.domains;
            raw.tree = exportNode(it.root);
            it._raw = raw;
            return raw;
        });
    }

    // Groups referencing a predicate re-resolve after any predicate change.
    function refreshGroupsIfReferenced() {
        if (!window.FEAFeatures) return;
        var env = FEAFeatures.envelope();
        if (!env || !env.groups || !Array.isArray(env.groups.items)) return;
        var referenced = env.groups.items.some(function (g) {
            var ms = Array.isArray(g.members) ? g.members : (g.members ? [g.members] : []);
            return ms.some(function (m) { return m && m.predicateId; });
        });
        if (referenced && FEAFeatures.refresh) FEAFeatures.refresh();
    }

    function afterMutation(item) {
        if (item && feaModel) evaluateItem(item);
        syncEnvelope();
        refreshGroupsIfReferenced();
        refreshAll();
    }

    // ---- public hooks ---------------------------------------------------
    function onEnvelope(env) {
        items = [];
        selectedIds = [];
        var list = (env && env.predicates && Array.isArray(env.predicates.items))
            ? env.predicates.items : [];
        list.forEach(function (raw) {
            var root = importNode(raw.tree);
            if (!root) return;
            var it = {
                id: raw.id || newGuid('p-'),
                name: raw.name || ('Predicate ' + (items.length + 1)),
                target: raw.target === 'nodes' ? 'nodes' : 'elements',
                domains: Array.isArray(raw.domains) ? raw.domains.slice() : null,
                root: root,
                _raw: raw
            };
            if (!it.domains) delete it.domains;
            items.push(it);
        });
        if (feaModel) evaluateAll();
        if (list.length) say('Predicates: ' + items.length + ' loaded from sidecar.');
        refreshGroupsIfReferenced();
        refreshAll();
    }

    function onModelLoaded() {
        clearCaches();
        evaluateAll();
        refreshAll();
    }

    function onModelCleared() {
        clearCaches();
        items.forEach(function (it) { strip(it.root); });
        function strip(n) {
            n.matches = null; n.count = 0;
            if (n.kind === 'op') n.children.forEach(strip);
        }
        teardownVisuals();
    }

    // resolveMembers(predicateId) -> explicit member entries with REAL ids
    // ([{domain:'shells', ids}, {domain:'beams', ids}, {domain:'nodes', nodeIds}]),
    // or null when the predicate is unknown / no model is loaded.
    function resolveMembers(predicateId) {
        var item = findItem(predicateId);
        if (!item || !feaModel) return null;
        evaluateItem(item);
        var root = item.root;
        var out = [];
        var fams = familiesFor(item);
        fams.forEach(function (fam) {
            var set = effectiveSet(root, fam);
            if (set.size === 0) return;
            var idx = Array.from(set);
            if (fam === 'shell') {
                out.push({ domain: 'shells', ids: idx.map(function (i) { return feaModel.elemIds[i]; }) });
            } else if (fam === 'beam') {
                var bv = beamViewOrNull();
                out.push({ domain: 'beams', ids: idx.map(function (i) { return bv.elemIds[i]; }) });
            } else {
                out.push({ domain: 'nodes', nodeIds: idx.map(function (i) { return feaModel.nodeIds[i]; }) });
            }
        });
        return out;
    }

    // ---- mutations ------------------------------------------------------
    function createFromPick(worldPoint, normal) {
        var leaf = makeLeaf(worldPoint, normal);
        if (selectedIds.length === 1) {
            var sel = findNode(selectedIds[0]);
            if (sel && sel.node.kind === 'op') {
                sel.node.children.push(leaf);
                selectedIds = [leaf.rid];
                afterMutation(sel.item);
                return;
            }
        }
        var it = makeItem(leaf);
        items.push(it);
        selectedIds = [leaf.rid];
        afterMutation(it);
    }

    function moveLeaf(rid, worldPoint, normal) {
        var hit = findNode(rid);
        if (!hit || hit.node.kind !== 'leaf') return;
        var leaf = hit.node;
        leaf.point = [worldPoint[0], worldPoint[1], worldPoint[2]];
        leaf.normal = [round6(normal[0]), round6(normal[1]), round6(normal[2])];
        buildPredAxes(leaf);
        afterMutation(hit.item);
    }

    function deleteSelected() {
        if (selectedIds.length === 0) return;
        var ids = selectedIds.slice();
        var touched = [];
        ids.forEach(function (rid) {
            var hit = findNode(rid);
            if (!hit) return;
            if (hit.item.root.rid === rid) {
                items = items.filter(function (it) { return it !== hit.item; });
            } else {
                var p = findParent(rid);
                if (p && p.parent) {
                    p.parent.children = p.parent.children.filter(function (c) { return c.rid !== rid; });
                    if (touched.indexOf(hit.item) === -1) touched.push(hit.item);
                }
            }
        });
        selectedIds = [];
        touched.forEach(function (it) { if (feaModel) evaluateItem(it); });
        afterMutation(null);
    }

    function toggleNegateSelected() {
        if (selectedIds.length === 0) return;
        var touched = [];
        selectedIds.forEach(function (rid) {
            var hit = findNode(rid);
            if (hit) {
                hit.node.negated = !hit.node.negated;
                if (touched.indexOf(hit.item) === -1) touched.push(hit.item);
            }
        });
        touched.forEach(function (it) { if (feaModel) evaluateItem(it); });
        afterMutation(null);
    }

    function canGroupSelection() {
        if (selectedIds.length === 0) return false;
        var parents = selectedIds.map(function (rid) {
            var p = findParent(rid); return p ? p.parent : null;
        });
        if (selectedIds.length === 1 && parents[0] === null) return true;
        if (parents.indexOf(null) !== -1) return false;
        for (var i = 1; i < parents.length; i++) if (parents[i] !== parents[0]) return false;
        return true;
    }

    function groupSelectionInto(opKind) {
        if (!canGroupSelection()) return;
        var parents = selectedIds.map(function (rid) {
            var p = findParent(rid); return p ? p.parent : null;
        });
        if (selectedIds.length === 1 && parents[0] === null) {
            var hit = findNode(selectedIds[0]);
            if (!hit) return;
            var rootOp = makeOp(opKind, [hit.node]);
            hit.item.root = rootOp;
            selectedIds = [rootOp.rid];
            afterMutation(hit.item);
            return;
        }
        var first = parents[0];
        var ordered = first.children.filter(function (c) { return selectedIds.indexOf(c.rid) !== -1; });
        var insertIdx = first.children.indexOf(ordered[0]);
        var newOp = makeOp(opKind, ordered);
        first.children = first.children.filter(function (c) { return selectedIds.indexOf(c.rid) === -1; });
        first.children.splice(insertIdx, 0, newOp);
        selectedIds = [newOp.rid];
        afterMutation(itemOf(newOp.rid));
    }

    function dissolveSelectedOp() {
        if (selectedIds.length !== 1) return;
        var hit = findNode(selectedIds[0]);
        if (!hit || hit.node.kind !== 'op') return;
        var op = hit.node, item = hit.item;
        if (item.root.rid === op.rid) {
            var idx = items.indexOf(item);
            items.splice(idx, 1);
            if (op.children.length === 0) {
                selectedIds = [];
            } else if (op.children.length === 1) {
                var it1 = makeItem(op.children[0], item.name, item.target);
                items.splice(idx, 0, it1);
                selectedIds = [op.children[0].rid];
            } else {
                op.children.forEach(function (child, i) {
                    var itN = makeItem(child, item.name + ' (' + (i + 1) + ')', item.target);
                    items.splice(idx + i, 0, itN);
                });
                selectedIds = [op.children[0].rid];
            }
        } else {
            var p = findParent(op.rid);
            if (!p || !p.parent) return;
            var opIdx = p.parent.children.indexOf(op);
            Array.prototype.splice.apply(p.parent.children, [opIdx, 1].concat(op.children));
            selectedIds = op.children.length ? [op.children[0].rid] : [];
        }
        items.forEach(function (it) { if (feaModel) evaluateItem(it); });
        afterMutation(null);
    }

    // "Group from predicate": append a sidecar group whose members resolve
    // from the selected predicate at load / on demand.
    function groupFromSelected() {
        var item = null;
        if (selectedIds.length >= 1) item = itemOf(selectedIds[0]);
        if (!item && items.length === 1) item = items[0];
        if (!item) { say('Predicates: select a predicate to make a group from.'); return; }
        if (!window.FEAFeatures || !FEAFeatures.ensureEnvelope) return;
        syncEnvelope();                               // make sure the item exists in the envelope
        var env = FEAFeatures.ensureEnvelope();
        if (!env.groups) env.groups = { version: 1, items: [] };
        env.groups.items.push({
            id: newGuid('g-'),
            name: item.name,
            source: { predicateId: item.id },
            members: [{ predicateId: item.id }]
        });
        if (FEAFeatures.refresh) FEAFeatures.refresh();
        say('Predicates: group "' + item.name + '" added (color by groups to see it).');
    }

    // ---- arming / picking (viewer.js pointerup routes here) --------------
    function wantsPick() { return armedMode !== null && !!feaModel; }

    // hit: feaPick result -- shell {faceIndex, point} or beam {beam, elem, point}
    function placePick(hit) {
        ensureCaches();
        var off = totalOffset();
        var world = [hit.point.x + off[0], hit.point.y + off[1], hit.point.z + off[2]];
        var normal;
        if (hit.beam) {
            var e = hit.elem;
            normal = beamAxis ? [beamAxis[e * 3], beamAxis[e * 3 + 1], beamAxis[e * 3 + 2]] : [0, 0, 1];
        } else {
            var elem = feaBuild.triToElem[hit.faceIndex];
            normal = [feaBuild.elemNormals[elem * 3], feaBuild.elemNormals[elem * 3 + 1],
                      feaBuild.elemNormals[elem * 3 + 2]];
        }
        if (armedMode === 'place') {
            createFromPick(world, normal);
            setArmed(null);
        } else if (armedMode === 'adjust' && selectedIds.length === 1) {
            moveLeaf(selectedIds[0], world, normal);
        }
    }

    function setArmed(mode) {
        armedMode = mode;
        if (mode && window.FEASectionCut && FEASectionCut.disarm) FEASectionCut.disarm();
        if (elNew) elNew.classList.toggle('active', mode === 'place');
        if (elAdjust) elAdjust.classList.toggle('active', mode === 'adjust');
        if (elNew) elNew.textContent = mode === 'place' ? 'Ctrl+click the model…' : 'New';
    }
    function disarm() { setArmed(null); }

    // ---- visuals --------------------------------------------------------
    function haveScene() { return (typeof scene !== 'undefined') && scene && (typeof THREE !== 'undefined'); }

    function teardownVisuals() {
        if (!haveScene()) return;
        [highlightObj, extentsObj].forEach(function (o) {
            if (!o) return;
            scene.remove(o);
            o.traverse(function (c) {
                if (c.geometry) c.geometry.dispose();
                if (c.material) c.material.dispose();
            });
        });
        highlightObj = null;
        extentsObj = null;
        render();
    }

    function updateVisuals() {
        if (!haveScene()) return;
        teardownVisuals();
        if (selectedIds.length !== 1 || !feaModel) return;
        var hit = findNode(selectedIds[0]);
        if (!hit || !hit.node.matches) return;
        ensureCaches();
        var fams = familiesFor(hit.item);   // only query evaluated families:
                                            // a negated node's effectiveSet is the
                                            // whole universe for any other family

        var group = new THREE.Group();

        // shell matches: translucent overlay of the matched elements
        var shellSet = fams.indexOf('shell') !== -1 ? effectiveSet(hit.node, 'shell') : new Set();
        if (shellSet.size > 0 && feaBuild && elemVertStart) {
            var src = feaBuild.geometry.attributes.position.array;
            var total = 0;
            shellSet.forEach(function (e) { total += (feaBuild.elemNCount[e] === 4 ? 6 : 3); });
            var pos = new Float32Array(total * 3);
            var w = 0;
            shellSet.forEach(function (e) {
                var start = elemVertStart[e] * 3;
                var len = (feaBuild.elemNCount[e] === 4 ? 6 : 3) * 3;
                pos.set(src.subarray(start, start + len), w);
                w += len;
            });
            var geo = new THREE.BufferGeometry();
            geo.setAttribute('position', new THREE.BufferAttribute(pos, 3));
            group.add(new THREE.Mesh(geo, new THREE.MeshBasicMaterial({
                color: 0x2e7d32, transparent: true, opacity: 0.55, side: THREE.DoubleSide,
                depthWrite: false, polygonOffset: true, polygonOffsetFactor: -1, polygonOffsetUnits: -1
            })));
        }

        // beam + node matches: constant-size points
        function pointCloud(set, sourceXyz, color) {
            if (set.size === 0 || !sourceXyz) return;
            var pos = new Float32Array(set.size * 3);
            var i = 0;
            set.forEach(function (idx) {
                pos[i * 3] = sourceXyz[idx * 3];
                pos[i * 3 + 1] = sourceXyz[idx * 3 + 1];
                pos[i * 3 + 2] = sourceXyz[idx * 3 + 2];
                i++;
            });
            var geo = new THREE.BufferGeometry();
            geo.setAttribute('position', new THREE.BufferAttribute(pos, 3));
            group.add(new THREE.Points(geo, new THREE.PointsMaterial({
                color: color, size: 8, sizeAttenuation: false, transparent: true, opacity: 0.9
            })));
        }
        if (fams.indexOf('beam') !== -1) pointCloud(effectiveSet(hit.node, 'beam'), beamCg, 0x2e7d32);
        if (fams.indexOf('node') !== -1) pointCloud(effectiveSet(hit.node, 'node'), feaModel.nodes, 0x2e7d32);

        if (group.children.length > 0) {
            highlightObj = group;
            scene.add(group);
        }

        // finite-leaf extents ghost
        if (showExtents && hit.node.kind === 'leaf' && hit.node.finite) {
            var pred = hit.node;
            var geoP = new THREE.PlaneGeometry(pred.width, pred.length);
            var meshP = new THREE.Mesh(geoP, new THREE.MeshBasicMaterial({
                color: 0x88cc88, transparent: true, opacity: 0.15,
                side: THREE.DoubleSide, depthWrite: false
            }));
            meshP.add(new THREE.LineSegments(
                new THREE.WireframeGeometry(geoP),
                new THREE.LineBasicMaterial({ color: 0x88cc88 })));
            var off = totalOffset();
            meshP.position.set(pred.point[0] - off[0], pred.point[1] - off[1], pred.point[2] - off[2]);
            var u = new THREE.Vector3(pred.uAxis[0], pred.uAxis[1], pred.uAxis[2]);
            var v = new THREE.Vector3(pred.vAxis[0], pred.vAxis[1], pred.vAxis[2]);
            var n = new THREE.Vector3(pred.normal[0], pred.normal[1], pred.normal[2]);
            meshP.quaternion.setFromRotationMatrix(new THREE.Matrix4().makeBasis(u, v, n));
            extentsObj = meshP;
            scene.add(meshP);
        }
        render();
    }

    // ---- UI: refresh ----------------------------------------------------
    function refreshAll() {
        refreshActionBar();
        renderInspector();
        rebuildTree();
        updateVisuals();
        render();
    }

    function refreshActionBar() {
        if (!elDelete) return;
        var n = selectedIds.length;
        elDelete.disabled = n === 0;
        elNot.disabled = n === 0;
        var canAdjust = false;
        if (n === 1) {
            var hit = findNode(selectedIds[0]);
            canAdjust = !!(hit && hit.node.kind === 'leaf');
        }
        elAdjust.disabled = !canAdjust;
        if (!canAdjust && armedMode === 'adjust') setArmed(null);
        var canGroup = canGroupSelection();
        elAnd.disabled = !canGroup;
        elOr.disabled = !canGroup;
        if (elGroup) elGroup.disabled = !(n >= 1 || items.length === 1);
    }

    function onRowClick(rid, event) {
        var multi = event.ctrlKey || event.shiftKey || event.metaKey;
        if (multi) {
            var idx = selectedIds.indexOf(rid);
            if (idx >= 0) selectedIds.splice(idx, 1);
            else selectedIds.push(rid);
        } else {
            selectedIds = [rid];
        }
        refreshAll();
    }

    function clearSelection() { selectedIds = []; refreshAll(); }

    // ---- UI: inspector --------------------------------------------------
    function fieldRow(labelText) {
        var row = document.createElement('div');
        row.className = 'fea-row';
        var lbl = document.createElement('span');
        lbl.className = 'fea-label';
        lbl.textContent = labelText;
        row.appendChild(lbl);
        return row;
    }

    function numberRow(labelText, value, unit, step, onChange) {
        var row = fieldRow(labelText);
        var inp = document.createElement('input');
        inp.type = 'number';
        inp.className = 'fea-input num';
        inp.style.cssText = 'flex:0 0 64px; text-align:center;';
        inp.value = value;
        inp.step = step;
        inp.addEventListener('change', function () {
            var v = parseFloat(inp.value);
            if (!isNaN(v)) onChange(v);
        });
        row.appendChild(inp);
        if (unit) {
            var u = document.createElement('span');
            u.className = 'fea-hint';
            u.textContent = unit;
            row.appendChild(u);
        }
        return row;
    }

    function checkboxInline(labelText, checked, onChange) {
        var lab = document.createElement('label');
        lab.className = 'pd-checkbox';
        var cb = document.createElement('input');
        cb.type = 'checkbox';
        cb.className = 'fea-check';
        cb.checked = checked;
        cb.addEventListener('change', function () { onChange(cb.checked); });
        lab.appendChild(cb);
        var t = document.createElement('span');
        t.textContent = labelText;
        lab.appendChild(t);
        return lab;
    }

    function renderInspector() {
        if (!elInspector) return;
        elInspector.innerHTML = '';
        var n = selectedIds.length;
        if (n === 0) {
            elInspector.innerHTML = '<div class="pd-empty">Nothing selected</div>';
            return;
        }
        var itemSet = [];
        selectedIds.forEach(function (rid) {
            var it = itemOf(rid);
            if (it && itemSet.indexOf(it) === -1) itemSet.push(it);
        });

        if (itemSet.length === 1) renderItemHeader(itemSet[0]);
        else {
            var hdr = document.createElement('div');
            hdr.className = 'pd-summary';
            hdr.textContent = 'Selection spans ' + itemSet.length + ' predicates.';
            elInspector.appendChild(hdr);
        }

        if (n === 1) {
            var hit = findNode(selectedIds[0]);
            if (!hit) return;
            if (hit.node.kind === 'leaf') renderLeafEditor(hit.node, hit.item);
            else renderOpEditor(hit.node, hit.item);
        } else {
            var counts = { leaf: 0, op: 0 };
            selectedIds.forEach(function (rid) {
                var h = findNode(rid);
                if (h) counts[h.node.kind]++;
            });
            var s = document.createElement('div');
            s.className = 'pd-summary';
            s.innerHTML = '<strong>' + n + ' nodes selected</strong><br>' +
                counts.leaf + ' leaf, ' + counts.op + ' operator<br>' +
                (canGroupSelection()
                    ? '<span style="color:#88aa88">Eligible to group (same parent)</span>'
                    : '<span style="color:#aa6644">Cannot group (mixed parents or includes a root)</span>');
            elInspector.appendChild(s);
        }
    }

    function renderItemHeader(item) {
        var nameRow = fieldRow('Name');
        var nameInp = document.createElement('input');
        nameInp.type = 'text';
        nameInp.className = 'fea-input';
        nameInp.value = item.name;
        nameInp.addEventListener('change', function () {
            item.name = nameInp.value || '(unnamed)';
            syncEnvelope();
            rebuildTree();
        });
        nameRow.appendChild(nameInp);
        elInspector.appendChild(nameRow);

        var typeRow = fieldRow('Target');
        var wrap = document.createElement('div');
        wrap.style.cssText = 'display:flex; gap:4px; flex:1;';
        [['elements', 'Elements'], ['nodes', 'Nodes']].forEach(function (t) {
            var b = document.createElement('button');
            b.className = 'fea-btn' + (item.target === t[0] ? ' active' : '');
            b.textContent = t[1];
            b.addEventListener('click', function () {
                item.target = t[0];
                afterMutation(item);
            });
            wrap.appendChild(b);
        });
        typeRow.appendChild(wrap);
        elInspector.appendChild(typeRow);
    }

    function renderOpEditor(node, item) {
        var opRow = fieldRow('Operator');
        var wrap = document.createElement('div');
        wrap.style.cssText = 'display:flex; gap:4px; flex:1;';
        ['and', 'or'].forEach(function (o) {
            var b = document.createElement('button');
            b.className = 'fea-btn' + (node.op === o ? ' active' : '');
            b.textContent = o.toUpperCase();
            b.addEventListener('click', function () {
                node.op = o;
                afterMutation(item);
            });
            wrap.appendChild(b);
        });
        opRow.appendChild(wrap);
        elInspector.appendChild(opRow);

        var negRow = document.createElement('div');
        negRow.className = 'fea-row';
        negRow.appendChild(checkboxInline('Negate (¬)', node.negated, function (v) {
            node.negated = v;
            afterMutation(item);
        }));
        elInspector.appendChild(negRow);

        var info = document.createElement('div');
        info.className = 'pd-summary';
        info.innerHTML = node.children.length + ' child node(s)' +
            (node.children.length === 0 ? ' — empty container' : '') +
            '<br>Matches: <strong>' + node.count + '</strong>';
        if (node.children.length === 0) info.style.color = '#d4a045';
        elInspector.appendChild(info);

        var dissolve = document.createElement('button');
        dissolve.className = 'fea-btn pd-danger';
        dissolve.style.marginTop = '6px';
        dissolve.textContent = 'Dissolve';
        dissolve.title = 'Remove this operator; its children take its place';
        dissolve.addEventListener('click', dissolveSelectedOp);
        elInspector.appendChild(dissolve);
    }

    function renderLeafEditor(node, item) {
        var toggles = document.createElement('div');
        toggles.className = 'pd-toggles';
        toggles.appendChild(checkboxInline('Negate (¬)', node.negated, function (v) {
            node.negated = v;
            afterMutation(item);
        }));
        toggles.appendChild(checkboxInline('Finite', node.finite, function (v) {
            node.finite = v;
            lastLeafDefaults.finite = v;
            if (v) buildPredAxes(node);
            afterMutation(item);
        }));
        if (node.finite) {
            toggles.appendChild(checkboxInline('Show', showExtents, function (v) {
                showExtents = v;
                updateVisuals();
            }));
        }
        elInspector.appendChild(toggles);

        // Position (world coordinates, matching the pick readout)
        var pos = document.createElement('div');
        pos.className = 'pd-posblock';
        pos.innerHTML = '<div class="pd-poslabel">Position (world)</div>';
        pos.title = 'Scroll over a field to nudge by ±10. Shift = ×10, Alt = ÷10.';
        ['X', 'Y', 'Z'].forEach(function (axis, i) {
            var row = document.createElement('div');
            row.className = 'pd-posrow';
            var lbl = document.createElement('span');
            lbl.className = 'pd-poschan';
            lbl.style.color = ['#ff5252', '#6ee06e', '#5f8cff'][i];
            lbl.textContent = axis;
            var inp = document.createElement('input');
            inp.type = 'text';
            inp.className = 'fea-input num';
            inp.value = node.point[i].toLocaleString(undefined, { maximumFractionDigits: 1 });
            inp.addEventListener('change', function () {
                var v = parseFloat(inp.value.replace(/,/g, ''));
                if (!isNaN(v)) {
                    node.point[i] = v;
                    afterMutation(item);
                } else {
                    inp.value = node.point[i].toLocaleString(undefined, { maximumFractionDigits: 1 });
                }
            });
            inp.addEventListener('wheel', function (e) {
                e.preventDefault();
                var dir = e.deltaY < 0 ? 1 : -1;
                var step = e.shiftKey ? 100 : (e.altKey ? 1 : 10);
                node.point[i] += dir * step;
                afterMutation(item);
            }, { passive: false });
            row.appendChild(lbl);
            row.appendChild(inp);
            pos.appendChild(row);
        });
        elInspector.appendChild(pos);

        var normRow = fieldRow('Normal');
        var ns = document.createElement('span');
        ns.className = 'pd-norm';
        ns.textContent = '[ ' + node.normal[0].toFixed(4) + ', ' +
            node.normal[1].toFixed(4) + ', ' + node.normal[2].toFixed(4) + ' ]';
        normRow.appendChild(ns);
        elInspector.appendChild(normRow);

        if (node.finite) {
            elInspector.appendChild(numberRow('Width', node.width, 'in', 5, function (v) {
                node.width = v;
                lastLeafDefaults.width = v;
                afterMutation(item);
            }));
            elInspector.appendChild(numberRow('Length', node.length, 'in', 5, function (v) {
                node.length = v;
                lastLeafDefaults.length = v;
                afterMutation(item);
            }));
            elInspector.appendChild(numberRow('Angle', node.angle_deg, 'deg', 5, function (v) {
                node.angle_deg = v;
                lastLeafDefaults.angle_deg = v;
                buildPredAxes(node);
                afterMutation(item);
            }));
        }

        elInspector.appendChild(numberRow('Dist tol', node.tol, 'in', 0.1, function (v) {
            node.tol = v;
            lastLeafDefaults.tol = v;
            afterMutation(item);
        }));
        elInspector.appendChild(numberRow('Ang tol', node.normal_tol_deg, 'deg', 0.5, function (v) {
            node.normal_tol_deg = v;
            lastLeafDefaults.normal_tol_deg = v;
            node.cosNormTol = Math.cos(v * Math.PI / 180);   // (old viewer bug: wrote the wrong field)
            afterMutation(item);
        }));

        var mi = document.createElement('div');
        mi.className = 'pd-summary';
        mi.innerHTML = 'Matches: <strong>' + node.count + '</strong>' +
            (item.target === 'nodes' ? ' nodes' : ' elements');
        elInspector.appendChild(mi);
    }

    // ---- UI: tree -------------------------------------------------------
    function rebuildTree() {
        if (!elTree) return;
        elTree.innerHTML = '';
        if (items.length === 0) {
            var empty = document.createElement('div');
            empty.className = 'pd-empty';
            empty.innerHTML = 'No predicates yet.<br>Click <em>New</em>, then Ctrl+click the model.';
            elTree.appendChild(empty);
            return;
        }
        items.forEach(function (item) { renderNode(item.root, 0, item, true); });
    }

    function renderNode(node, depth, item, isRoot) {
        var row = document.createElement('div');
        row.className = 'pd-row' + (isRoot ? ' root' : '');
        if (selectedIds.indexOf(node.rid) !== -1) row.classList.add('selected');
        row.draggable = true;
        row.dataset.rid = node.rid;
        row.addEventListener('dragstart', function (e) { onDragStart(node, e); });
        row.addEventListener('dragover', function (e) { onDragOver(node, e); });
        row.addEventListener('dragleave', function (e) {
            e.currentTarget.classList.remove('drop-into', 'drop-before', 'drop-after');
        });
        row.addEventListener('drop', function (e) { onDrop(node, e); });
        row.addEventListener('dragend', cleanupDrag);

        var indent = document.createElement('span');
        indent.style.cssText = 'display:inline-block;width:' + (depth * 13) + 'px;';
        row.appendChild(indent);

        var arrow = document.createElement('span');
        arrow.className = 'pd-arrow';
        arrow.innerHTML = '&#9654;';
        if (node.kind === 'op') {
            if (!node.collapsed) arrow.classList.add('open');
            arrow.addEventListener('click', function (e) {
                e.stopPropagation();
                node.collapsed = !node.collapsed;
                rebuildTree();
            });
        } else {
            arrow.classList.add('spacer');
        }
        row.appendChild(arrow);

        var pill = document.createElement('span');
        pill.className = 'pd-pill';
        if (node.kind === 'op') {
            pill.classList.add(node.op === 'and' ? 'op-and' : 'op-or');
            pill.textContent = node.op.toUpperCase();
        } else {
            pill.classList.add('leaf');
            pill.textContent = node.finite ? 'RECT' : 'PLANE';
        }
        row.appendChild(pill);

        if (node.negated) {
            var neg = document.createElement('span');
            neg.className = 'pd-neg';
            neg.textContent = '¬';
            row.appendChild(neg);
        }

        var name = document.createElement('span');
        name.className = 'pd-name';
        if (isRoot) {
            name.textContent = item.name;
        } else if (node.kind === 'op') {
            name.textContent = '(' + node.children.length + ' children)';
            name.classList.add('dim');
        } else {
            var nn = node.normal;
            name.textContent = 'n=[' + nn[0].toFixed(2) + ',' + nn[1].toFixed(2) + ',' + nn[2].toFixed(2) + ']';
            name.classList.add('mono');
        }
        row.appendChild(name);

        var info = document.createElement('span');
        info.className = 'pd-count';
        info.textContent = node.count;
        row.appendChild(info);
        if (isRoot) {
            var t = document.createElement('span');
            t.className = 'pd-count';
            t.textContent = item.target === 'nodes' ? 'nodes' : 'elems';
            row.appendChild(t);
        }
        if (node.kind === 'op' && node.children.length === 0) {
            var warn = document.createElement('span');
            warn.className = 'pd-warn';
            warn.textContent = '⚠';
            warn.title = 'Empty operator';
            row.appendChild(warn);
        }

        row.addEventListener('click', function (e) {
            e.stopPropagation();
            onRowClick(node.rid, e);
        });
        elTree.appendChild(row);

        if (node.kind === 'op' && !node.collapsed) {
            node.children.forEach(function (c) { renderNode(c, depth + 1, item, false); });
        }
    }

    // ---- drag and drop --------------------------------------------------
    function containsRid(node, rid) {
        if (node.rid === rid) return true;
        if (node.kind === 'op') {
            for (var i = 0; i < node.children.length; i++) {
                if (containsRid(node.children[i], rid)) return true;
            }
        }
        return false;
    }

    function anyAncestor(draggedIds, targetRid) {
        for (var i = 0; i < draggedIds.length; i++) {
            var h = findNode(draggedIds[i]);
            if (h && containsRid(h.node, targetRid)) return true;
        }
        return false;
    }

    function pruneDescendants(ids) {
        return ids.filter(function (rid) {
            var p = findParent(rid);
            while (p && p.parent) {
                if (ids.indexOf(p.parent.rid) !== -1) return false;
                p = findParent(p.parent.rid);
            }
            return true;
        });
    }

    function onDragStart(node, e) {
        var ids;
        if (selectedIds.indexOf(node.rid) !== -1 && selectedIds.length > 1) {
            ids = selectedIds.slice();
        } else {
            ids = [node.rid];
        }
        dragIds = pruneDescendants(ids);
        e.dataTransfer.effectAllowed = 'move';
        try { e.dataTransfer.setData('text/plain', dragIds.join(',')); } catch (err) {}
    }

    function dropZone(e, row, node) {
        var rect = row.getBoundingClientRect();
        var y = e.clientY - rect.top, h = rect.height;
        if (node.kind === 'op') return (y < h * 0.25) ? 'before' : 'into';
        return (y < h * 0.5) ? 'before' : 'after';
    }

    function onDragOver(node, e) {
        if (!dragIds) return;
        if (dragIds.indexOf(node.rid) !== -1) return;
        if (anyAncestor(dragIds, node.rid)) return;
        e.preventDefault();
        e.dataTransfer.dropEffect = 'move';
        clearDropMarks();
        var zone = dropZone(e, e.currentTarget, node);
        e.currentTarget.classList.add(zone === 'into' ? 'drop-into' : (zone === 'before' ? 'drop-before' : 'drop-after'));
    }

    function onDrop(node, e) {
        e.preventDefault();
        if (!dragIds) return;
        moveNodesTo(dragIds, node, dropZone(e, e.currentTarget, node));
        cleanupDrag();
    }

    function clearDropMarks() {
        if (!elTree) return;
        Array.prototype.forEach.call(elTree.querySelectorAll('.pd-row'), function (r) {
            r.classList.remove('drop-into', 'drop-before', 'drop-after');
        });
        elTree.classList.remove('drop-new');
    }

    function cleanupDrag() {
        clearDropMarks();
        dragIds = null;
    }

    function moveNodesTo(ids, targetNode, position) {
        if (targetNode && anyAncestor(ids, targetNode.rid)) return;
        var dragged = [];
        ids.forEach(function (rid) {
            var h = findNode(rid);
            if (h) dragged.push(h.node);
        });
        if (dragged.length === 0) return;

        // detach: whole expressions removed from the list, inner nodes from parents
        var removedItems = [];
        dragged.forEach(function (n) {
            var src = itemOf(n.rid);
            if (src && src.root.rid === n.rid) {
                removedItems.push(src);
            } else if (src) {
                var p = findParent(n.rid);
                if (p && p.parent) {
                    p.parent.children = p.parent.children.filter(function (c) { return c.rid !== n.rid; });
                }
            }
        });
        if (removedItems.length) {
            items = items.filter(function (it) { return removedItems.indexOf(it) === -1; });
        }

        if (position === 'new-expression') {
            var rootNode = dragged.length === 1 ? dragged[0] : makeOp('and', dragged);
            items.push(makeItem(rootNode));
            selectedIds = [rootNode.rid];
        } else if (position === 'into') {
            Array.prototype.push.apply(targetNode.children, dragged);
            selectedIds = dragged.map(function (n) { return n.rid; });
        } else {
            var p2 = findParent(targetNode.rid);
            if (p2 && p2.parent) {
                var idx = p2.parent.children.indexOf(targetNode);
                if (position === 'after') idx++;
                Array.prototype.splice.apply(p2.parent.children, [idx, 0].concat(dragged));
                selectedIds = dragged.map(function (n) { return n.rid; });
            } else {
                var targetItem = itemOf(targetNode.rid);
                var itemIdx = items.indexOf(targetItem);
                if (position === 'after') itemIdx++;
                var rootNode2 = dragged.length === 1 ? dragged[0] : makeOp('and', dragged);
                items.splice(itemIdx, 0, makeItem(rootNode2));
                selectedIds = [rootNode2.rid];
            }
        }
        items.forEach(function (it) { if (feaModel) evaluateItem(it); });
        afterMutation(null);
    }

    // ---- legacy predicates.json import ----------------------------------
    // v3: { expressions: [{name, type, root}] }  v1: { groups: [{name, predicate}] }
    function importLegacy(data) {
        var added = 0;
        function legacyItem(rootData, name, type) {
            var root = importNode(rootData);
            if (!root) return;
            var it = makeItem(root, name, type === 'nodes' ? 'nodes' : 'elements');
            if (type === 'plates') it.domains = ['shells'];
            items.push(it);
            added++;
        }
        if (Array.isArray(data.expressions)) {
            data.expressions.forEach(function (g) { legacyItem(g.root, g.name, g.type); });
        } else if (Array.isArray(data.groups)) {
            data.groups.forEach(function (g) { legacyItem(g.predicate || {}, g.name, g.type); });
        } else {
            say('Predicates: unrecognized JSON (not a features sidecar or legacy predicates file).');
            return;
        }
        say('Predicates: imported ' + added + ' legacy predicate(s).');
        if (feaModel) evaluateAll();
        afterMutation(null);
    }

    // ---- UI wiring ------------------------------------------------------
    if (elTab) elTab.addEventListener('click', function () {
        elPanel.classList.toggle('collapsed');
    });

    if (elNew) elNew.addEventListener('click', function () {
        setArmed(armedMode === 'place' ? null : 'place');
    });

    if (elAdjust) elAdjust.addEventListener('click', function () {
        if (selectedIds.length !== 1) return;
        var hit = findNode(selectedIds[0]);
        if (!hit || hit.node.kind !== 'leaf') return;
        setArmed(armedMode === 'adjust' ? null : 'adjust');
    });

    if (elDelete) elDelete.addEventListener('click', deleteSelected);
    if (elNot) elNot.addEventListener('click', toggleNegateSelected);
    if (elAnd) elAnd.addEventListener('click', function () { groupSelectionInto('and'); });
    if (elOr) elOr.addEventListener('click', function () { groupSelectionInto('or'); });
    if (elGroup) elGroup.addEventListener('click', groupFromSelected);

    if (elExport) elExport.addEventListener('click', function () {
        syncEnvelope();
        if (!window.FEAFeatures) return;
        if (!FEAFeatures.envelope() && items.length > 0 && FEAFeatures.ensureEnvelope) {
            FEAFeatures.ensureEnvelope();
            syncEnvelope();
        }
        var json = FEAFeatures.exportJson();
        if (!json) { say('Predicates: nothing to export (no features envelope).'); return; }
        var blob = new Blob([json], { type: 'application/json' });
        var a = document.createElement('a');
        a.href = URL.createObjectURL(blob);
        a.download = (FEAFeatures.fileName && FEAFeatures.fileName()) || 'model.features.json';
        a.click();
        URL.revokeObjectURL(a.href);
    });

    if (elImport) elImport.addEventListener('click', function () {
        if (elImportFile) elImportFile.click();
    });

    if (elImportFile) elImportFile.addEventListener('change', function (e) {
        var file = e.target.files && e.target.files[0];
        this.value = '';
        if (!file) return;
        var reader = new FileReader();
        reader.onload = function (ev) {
            var data;
            try { data = JSON.parse(ev.target.result); }
            catch (err) { say('Predicates: invalid JSON (' + err.message + ').'); return; }
            if (data && data.format === 'pluto-features') {
                FEAFeatures.setEnvelope(data, file.name);   // full sidecar -> normal path
            } else {
                importLegacy(data);
            }
        };
        reader.readAsText(file);
    });

    if (elTree) {
        elTree.addEventListener('click', function (e) {
            if (e.target === elTree) clearSelection();
        });
        elTree.addEventListener('dragover', function (e) {
            if (!dragIds || e.target !== elTree) return;
            e.preventDefault();
            e.dataTransfer.dropEffect = 'move';
            elTree.classList.add('drop-new');
        });
        elTree.addEventListener('dragleave', function (e) {
            if (e.target === elTree) elTree.classList.remove('drop-new');
        });
        elTree.addEventListener('drop', function (e) {
            if (!dragIds || e.target !== elTree) return;
            e.preventDefault();
            moveNodesTo(dragIds, null, 'new-expression');
            cleanupDrag();
        });
    }

    if (typeof window !== 'undefined' && window.addEventListener) {
        window.addEventListener('keydown', function (e) {
            if (e.key === 'Escape' && armedMode) setArmed(null);
        });
    }

    if (elInspector) refreshAll();

    // ---- exports --------------------------------------------------------
    return {
        // viewer.js hooks
        onModelLoaded: onModelLoaded,
        onModelCleared: onModelCleared,
        wantsPick: wantsPick,
        placePick: placePick,
        disarm: disarm,
        // features.js hooks
        onEnvelope: onEnvelope,
        resolveMembers: resolveMembers,
        // introspection / tests
        items: function () { return items; },
        evaluateAll: evaluateAll,
        exportNode: exportNode,
        importNode: importNode,
        importLegacy: importLegacy,
        createFromPick: createFromPick,
        select: function (rid) { selectedIds = [rid]; refreshAll(); },
        _test: {
            buildPredAxes: buildPredAxes,
            makeLeaf: makeLeaf,
            makeOp: makeOp,
            makeItem: makeItem,
            evaluateItem: evaluateItem,
            effectiveSet: effectiveSet,
            addItem: function (it) { items.push(it); return it; },
            reset: function () { items = []; selectedIds = []; clearCaches(); }
        }
    };
})();
