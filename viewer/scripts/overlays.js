// ================================================================
// overlays.js  --  extra models drawn beside the loaded one (model rail, stage 1).
// Design: vault/viewer/model-rail.md
//
// The loaded model (viewer.js feaModel) is the PRIMARY: it owns the field,
// load cases, legend, groups tab, section cuts and predicates. Overlays are
// other files (e.g. plant steel + pipes over a SAP duct model) drawn as
// reference geometry:
//   - beams only for now (a file with shells draws its beams and says so)
//   - coloured by the overlay's OWN sidecar groups (every element group,
//     envelope order, later wins; no group = neutral grey), or ghosted
//   - placed at true world coordinates: each file's exporter recenter
//     (sidecar units.worldOffset) and the viewer recenter are added back,
//     lengths converted to the primary's unit
//   - pickable: the readout names the overlay, the member label and group
// Each overlay keeps its own sidecar (bound by its own geometryHash); nothing
// is merged. Not yet (stage 2/3): making an overlay the active model (its
// groups in the Groups tab, Save, field), section cuts / predicates across
// models.
// ================================================================

var FEAOverlays = (function () {

    var list = [];            // overlay records, in load order
    var primaryVisible = true;
    var GHOST_ALPHA = 0.22;
    var UNIT_M = { m: 1, mm: 0.001, cm: 0.01, in: 0.0254, inch: 0.0254, ft: 0.3048, feet: 0.3048 };

    var elRail = document.getElementById('modelRail');
    var elAdd = document.getElementById('ovAdd');
    var elFile = document.getElementById('ovFile');

    function unitOf(unified) {
        var u = unified && unified.units && unified.units.length;
        return u ? String(u).toLowerCase() : '';
    }
    // The exporter recenter: model.units.worldOffset per the sidecar spec (vault/format/
    // features-sidecar); top-level units.worldOffset accepted too.
    function parseOffset(env) {
        var u = env && ((env.model && env.model.units) || env.units);
        var s = u && u.worldOffset;
        if (!s) return [0, 0, 0];
        var p = String(s).trim().split(/\s+/).map(Number);
        return (p.length === 3 && p.every(isFinite)) ? p : [0, 0, 0];
    }

    // World position of the primary scene origin, in primary file units.
    function primaryOrigin() {
        var o = parseOffset(window.FEAFeatures && FEAFeatures.envelope ? FEAFeatures.envelope() : null);
        var r = (typeof viewRecenter !== 'undefined') ? viewRecenter : null;
        return [(o ? o[0] : 0) + (r ? r[0] : 0), (o ? o[1] : 0) + (r ? r[1] : 0), (o ? o[2] : 0) + (r ? r[2] : 0)];
    }

    // Overlay -> primary length factor (1 with a warning when a unit is unknown).
    function unitScale(ov) {
        var pu = (typeof feaModel !== 'undefined' && feaModel && feaModel.unified) ? unitOf(feaModel.unified) : '';
        var a = UNIT_M[ov.unit], b = UNIT_M[pu];
        ov.unitNote = '';
        if (!a || !b) {
            ov.unitNote = 'units ' + (ov.unit || '?') + ' / ' + (pu || '?') + ': not converted';
            return 1;
        }
        return a / b;
    }

    // Overlay world point (overlay units, its own axes) -> primary world (primary units, primary
    // axes): scale, then rotate rotZ degrees about the vertical (Z) axis through the origin.
    // Used when the two files' plan axes differ (e.g. a SAP model with X = plant north).
    function toPrimaryWorld(ov, w, s) {
        var t = ov.rotZ * Math.PI / 180, c = Math.round(Math.cos(t) * 1e12) / 1e12, sn = Math.round(Math.sin(t) * 1e12) / 1e12;
        var x = w[0] * s, y = w[1] * s;
        return [c * x - sn * y, sn * x + c * y, w[2] * s];
    }

    function placeOne(ov, po) {
        var s = unitScale(ov);
        ov.mesh.scale.setScalar(s);
        ov.mesh.rotation.set(0, 0, ov.rotZ * Math.PI / 180);
        // world(ov) = local + localOrigin + worldOffset; scene = R (world * s) - primary origin
        var w = toPrimaryWorld(ov, [ov.localOrigin[0] + ov.offset[0], ov.localOrigin[1] + ov.offset[1], ov.localOrigin[2] + ov.offset[2]], s);
        ov.mesh.position.set(w[0] - po[0], w[1] - po[1], w[2] - po[2]);
    }

    function reposition() {
        var po = primaryOrigin();
        list.forEach(function (ov) { placeOne(ov, po); });
        render();
        if (typeof needsRender !== 'undefined') needsRender = true;
    }

    // Element groups of the overlay's sidecar -> per-element category + palette.
    function resolveGroups(ov) {
        var n = ov.view.header.nElements;
        var cat = new Float32Array(n); cat.fill(-1);
        ov.groupOfElem = new Int32Array(n); ov.groupOfElem.fill(-1);
        ov.groups = [];
        var items = (ov.envelope && ov.envelope.groups && Array.isArray(ov.envelope.groups.items)) ? ov.envelope.groups.items : [];
        if (!items.length) return { cat: cat, rgb: [[200, 200, 200]] };
        var idx = new Map();
        var ids = ov.view.elemIds;
        for (var e = 0; e < n; e++) idx.set(ids[e], e);
        var beamDomains = {};
        ov.unified.domains.forEach(function (d) { if (d.family === 'beam') beamDomains[d.name] = 1; });
        var rgb = [];
        items.forEach(function (g, gi) {
            rgb.push(g.color ? hexToRgb(g.color) : FEAShaders.categoryColor(gi));
            var count = 0;
            (Array.isArray(g.members) ? g.members : []).forEach(function (m) {
                var d = m.domain || '';
                if (!(d === 'beams' || d === 'beam' || beamDomains[d])) return;
                (m.ids || []).forEach(function (id) {
                    var i = idx.get(id);
                    if (i === undefined) return;
                    cat[i] = gi; ov.groupOfElem[i] = gi; count++;
                });
            });
            ov.groups.push({ name: g.name || ('Group ' + (gi + 1)), count: count });
        });
        return { cat: cat, rgb: rgb };
    }
    function hexToRgb(h) {
        var m = /^#?([0-9a-f]{6})$/i.exec(String(h || ''));
        if (!m) return [200, 200, 200];
        var v = parseInt(m[1], 16);
        return [(v >> 16) & 255, (v >> 8) & 255, v & 255];
    }

    function applyLook(ov) {
        var u = ov.material.uniforms;
        u.uGroupMode.value = ov.hasGroups ? 1 : 0;
        u.uXray.value = ov.ghost ? GHOST_ALPHA : 0;
        ov.material.transparent = ov.ghost;
        ov.material.depthWrite = !ov.ghost;
        ov.mesh.visible = ov.visible;
        if (typeof needsRender !== 'undefined') needsRender = true;
    }

    // binFile: Blob/File of the .bin; jsonFile: its features sidecar (optional).
    async function add(binFile, jsonFile, name) {
        if (typeof feaModel === 'undefined' || !feaModel) { log('Load a model first; overlays are placed relative to it.'); return null; }
        var unified = await PlutoFormat.load(binFile, log);
        var view = PlutoFormat.beamView(unified);
        if (!view || view.header.nElements === 0) { log('Overlay ' + name + ': no beams (overlays draw beams only for now).'); return null; }
        var envelope = null;
        if (jsonFile) {
            try { envelope = JSON.parse(await jsonFile.text()); }
            catch (err) { log('Overlay ' + name + ': features file unreadable (' + err.message + ').'); }
        }
        var ov = {
            name: name, unified: unified, view: view, envelope: envelope,
            unit: unitOf(unified), offset: parseOffset(envelope),
            visible: true, ghost: false, hasGroups: false, rotZ: 0
        };
        try { ov.rotZ = Number(localStorage.getItem('pluto.ovRot.' + name)) || 0; } catch (e0) {}   // remembered per overlay name
        var want = envelope && envelope.model && envelope.model.geometryHash;
        if (want && unified.geometryHash && want !== unified.geometryHash) log('Overlay ' + name + ': ⚠ features geometryHash differs from the file.');

        // Local frame: subtract the bbox centre (whole units) so float32 stays small.
        var xyz = unified.nodes, nn = unified.nNodes;
        var mn = [Infinity, Infinity, Infinity], mx = [-Infinity, -Infinity, -Infinity];
        for (var i = 0; i < nn * 3; i++) { var a = i % 3; if (xyz[i] < mn[a]) mn[a] = xyz[i]; if (xyz[i] > mx[a]) mx[a] = xyz[i]; }
        ov.localOrigin = [Math.round((mn[0] + mx[0]) / 2), Math.round((mn[1] + mx[1]) / 2), Math.round((mn[2] + mx[2]) / 2)];
        for (var j = 0; j < nn * 3; j++) xyz[j] -= ov.localOrigin[j % 3];

        ov.build = FEABeamGeometry.build(view);
        var gr = resolveGroups(ov);
        ov.hasGroups = ov.groups.length > 0;
        FEAAttributes.updateCatIdx(ov.build.geometry, null, ov.build, gr.cat);
        ov.palette = FEAShaders.makePaletteTexture(gr.rgb);
        ov.material = new THREE.ShaderMaterial({
            uniforms: {
                colormap: { value: ov.palette },
                vMin: { value: 0 }, vMax: { value: 1 },
                alarmThreshold: { value: 0 }, alarmColor: { value: new THREE.Vector3(1, 0, 1) },
                uAbs: { value: 0 }, uNeutral: { value: 1 },
                neutralColor: { value: new THREE.Vector3(0.62, 0.64, 0.68) },   // grey: reads as "not the model"
                dispScale: { value: 0 },
                uGroupMode: { value: 0 }, groupPalette: { value: ov.palette }, uGroupCount: { value: Math.max(gr.rgb.length, 1) },
                uXray: { value: 0 }
            },
            vertexShader: FEAShaders.beamVertex,
            fragmentShader: FEAShaders.beamFragment,
            side: THREE.DoubleSide
        });
        ov.mesh = new THREE.Mesh(ov.build.geometry, ov.material);
        ov.mesh.renderOrder = 2;          // after the primary, so a ghost blends over it
        scene.add(ov.mesh);
        list.push(ov);
        applyLook(ov);
        reposition();
        log('Overlay ' + name + ': ' + view.header.nElements + ' beams, ' + ov.groups.length + ' groups, units ' + (ov.unit || '?') +
            (ov.unitNote ? ' (' + ov.unitNote + ')' : '') + '.');
        reportPlacement(ov);                 // last, so it is the line the status shows
        return ov;
    }

    function remove(ov) {
        var i = list.indexOf(ov);
        if (i < 0) return;
        scene.remove(ov.mesh);
        ov.build.geometry.dispose(); ov.material.dispose(); ov.palette.dispose();
        list.splice(i, 1);
        render();
        if (typeof needsRender !== 'undefined') needsRender = true;
    }

    function setPrimaryVisible(on) {
        primaryVisible = on;
        if (typeof mesh !== 'undefined' && mesh) mesh.visible = on;
        if (typeof feaEdges !== 'undefined' && feaEdges) feaEdges.visible = on;
        var bm = window.FEABeams && FEABeams.mesh();
        if (bm) bm.visible = on && (document.getElementById('beamShow') ? document.getElementById('beamShow').checked : true);
        if (typeof needsRender !== 'undefined') needsRender = true;
    }

    // Nearest visible overlay member under the ray: { overlay, elem, point, distance }.
    function pick(raycaster) {
        var best = null;
        list.forEach(function (ov) {
            if (!ov.visible) return;
            var hits = raycaster.intersectObject(ov.mesh);
            for (var i = 0; i < hits.length; i++) {
                if (hits[i].faceIndex == null) continue;
                if (!best || hits[i].distance < best.distance)
                    best = { overlay: ov, elem: ov.build.triToElem[hits[i].faceIndex], point: hits[i].point, distance: hits[i].distance };
                break;
            }
        });
        return best;
    }

    function fillReadout(hit) {
        var ov = hit.overlay, e = hit.elem, v = ov.view;
        var lbl = v.labels ? v.labels.get(e) : '';
        var g = ov.groupOfElem[e] >= 0 ? ov.groups[ov.groupOfElem[e]].name : 'no group';
        var sec = v.sections && ov.build.sectionOf ? v.sections[ov.build.sectionOf[e]] : null;
        elRoValue.textContent = g;
        elRoValue.className = 'ro-value';
        elRoComp.textContent = 'overlay ' + ov.name + (sec && sec.name ? ' · ' + sec.name : '');
        elRoElem.textContent = v.elemIds[e] + (lbl ? ' [' + lbl + ']' : '') + '  (overlay beam)';
        elRoNode.textContent = '—';
        elRoCorners.textContent = '—';
        elRoUV.textContent = '—';
        elRoPos.textContent = roPosText(hit.point) + nativeText(ov, hit.point);
        if (elRoControllingRow) elRoControllingRow.style.display = 'none';
        lastQuery = null;
    }

    // ---- where things are ------------------------------------------------
    // Scene-space box of an object (empty Box3 when nothing to measure).
    function sceneBox(obj) {
        var b = new THREE.Box3();
        if (!obj || !obj.geometry) return b;
        obj.updateMatrixWorld(true);
        b.setFromObject(obj);
        return b;
    }
    function primaryBox() {
        var b = new THREE.Box3();
        if (typeof mesh !== 'undefined' && mesh && feaModel && feaModel.header && feaModel.header.nElements > 0) b.union(sceneBox(mesh));
        var bm = window.FEABeams && FEABeams.mesh();
        if (bm) b.union(sceneBox(bm));
        return b;
    }
    // Box centre in WORLD coordinates (primary units): scene + primary origin.
    function worldCentre(box) {
        if (box.isEmpty()) return null;
        var c = box.getCenter(new THREE.Vector3()), po = primaryOrigin();
        return [c.x + po[0], c.y + po[1], c.z + po[2]];
    }
    function fmtXYZ(v) { return v ? v.map(function (x) { return Math.round(x).toLocaleString('en-US'); }).join(', ') : '—'; }
    function primaryUnit() { return (typeof feaModel !== 'undefined' && feaModel && feaModel.unified) ? (unitOf(feaModel.unified) || '?') : '?'; }

    // Frame the camera on a scene box (also resets near/far to it, so a far-away
    // overlay is not clipped by the main model's far plane).
    function zoomToBox(box) {
        if (box.isEmpty()) return;
        var sph = box.getBoundingSphere(new THREE.Sphere()), rr = sph.radius || 1;
        controls.target.copy(sph.center);
        var dir = camera.position.clone().sub(sph.center);
        if (dir.lengthSq() < 1e-12) dir.set(0.5, 0.45, 0.9);
        camera.position.copy(sph.center).add(dir.normalize().multiplyScalar(rr * 2.6));
        frameCamera(rr);
        controls.update();
        if (typeof needsRender !== 'undefined') needsRender = true;
    }
    function fitAll() {
        var b = new THREE.Box3();
        if (primaryVisible) b.union(primaryBox());
        list.forEach(function (ov) { if (ov.visible) b.union(sceneBox(ov.mesh)); });
        zoomToBox(b);
    }

    // Which plan rotation (0/90/180/270) puts this overlay's centre nearest the main model's.
    function bestRotation(ov) {
        var pb = primaryBox();
        if (pb.isEmpty()) return null;
        var keep = ov.rotZ, po = primaryOrigin(), best = null;
        var pc = pb.getCenter(new THREE.Vector3());
        [0, 90, 180, 270].forEach(function (a) {
            ov.rotZ = a; placeOne(ov, po);
            var oc = sceneBox(ov.mesh).getCenter(new THREE.Vector3());
            var d = oc.distanceTo(pc);
            if (!best || d < best.d) best = { rot: a, d: d };
        });
        ov.rotZ = keep; placeOne(ov, po);
        return best;
    }

    // One log line per overlay: where it is, where the main model is, how far apart.
    function reportPlacement(ov) {
        var pb = primaryBox(), ob = sceneBox(ov.mesh);
        var pc = worldCentre(pb), oc = worldCentre(ob), u = primaryUnit();
        var off = ov.offset.some(function (x) { return x !== 0; }) ? fmtXYZ(ov.offset) + ' ' + (ov.unit || '?')
            : (ov.envelope ? 'none in its features file' : 'none (no features file loaded)');
        var msg = 'Overlay ' + ov.name + ': centre ' + fmtXYZ(oc) + ' ' + u + ' (exporter offset ' + off + '); main model centre ' + fmtXYZ(pc) + ' ' + u;
        if (pc && oc && !pb.isEmpty()) {
            var d = Math.sqrt(Math.pow(oc[0] - pc[0], 2) + Math.pow(oc[1] - pc[1], 2) + Math.pow(oc[2] - pc[2], 2));
            var size = pb.getSize(new THREE.Vector3()).length() || 1;
            msg += '; ' + Math.round(d).toLocaleString('en-US') + ' ' + u + ' apart (' + (d / size).toFixed(1) + ' x the main model size)';
            if (d > 10 * size) {
                var b = bestRotation(ov);
                if (b && b.rot !== ov.rotZ && b.d < 10 * size)
                    msg += ' -- far apart; rotated ' + b.rot + '\u00b0 about Z it lands ' + Math.round(b.d).toLocaleString('en-US') + ' ' + u +
                        ' from it: set "rot" on its row to ' + b.rot;
                else msg += ' -- far apart: check that both files use the same coordinate system and units';
            }
        }
        log(msg + '.');
    }

    // The picked point in the overlay's OWN coordinates (its units and axes), when rotated or scaled.
    function nativeText(ov, p) {
        if (!ov.rotZ && unitScale(ov) === 1) return '';
        var po = primaryOrigin(), s = unitScale(ov);
        var x = p.x + po[0], y = p.y + po[1], z = p.z + po[2];
        var t = -ov.rotZ * Math.PI / 180, c = Math.cos(t), sn = Math.sin(t);
        var nx = (c * x - sn * y) / s, ny = (sn * x + c * y) / s, nz = z / s;
        return '  (' + ov.name + ': ' + nx.toFixed(2) + ', ' + ny.toFixed(2) + ', ' + nz.toFixed(2) + ' ' + (ov.unit || '') + ')';
    }

    // ---- rail ----------------------------------------------------------
    function render() {
        if (!elRail) return;
        elRail.innerHTML = '';
        var hasPrimary = typeof feaModel !== 'undefined' && !!feaModel;
        if (!hasPrimary) { elRail.innerHTML = '<div class="fea-hint">no model loaded</div>'; return; }
        var u = primaryUnit();
        var pBox = primaryBox();
        elRail.appendChild(row(window.feaPrimaryName || 'model',
            'active · centre ' + fmtXYZ(worldCentre(pBox)) + ' ' + u, primaryVisible,
            function (on) { setPrimaryVisible(on); }, null, null, true, false, function () { zoomToBox(primaryBox()); }));
        list.forEach(function (ov) {
            var off = ov.offset.some(function (x) { return x !== 0; }) ? 'offset ' + fmtXYZ(ov.offset) + ' ' + (ov.unit || '?') : 'NO offset';
            var info = ov.view.header.nElements + ' beams · ' + (ov.groups.length ? ov.groups.length + ' groups' : 'no groups') +
                ' · centre ' + fmtXYZ(worldCentre(sceneBox(ov.mesh))) + ' ' + u + ' · ' + off + (ov.unitNote ? ' · ' + ov.unitNote : '');
            elRail.appendChild(row(ov.name, info, ov.visible,
                function (on) { ov.visible = on; applyLook(ov); },
                function (on) { ov.ghost = on; applyLook(ov); }, function () { remove(ov); }, false, ov.ghost,
                function () { zoomToBox(sceneBox(ov.mesh)); },
                { value: ov.rotZ, set: function (a) {
                    ov.rotZ = a;
                    try { localStorage.setItem('pluto.ovRot.' + ov.name, String(a)); } catch (e1) {}
                    reposition(); reportPlacement(ov);
                } }));
        });
        if (list.length) {
            var fa = document.createElement('button'); fa.className = 'fea-btn'; fa.textContent = 'Fit all models';
            fa.title = 'Frame every visible model (sets the camera range to all of them)';
            fa.addEventListener('click', fitAll);
            var wrap = document.createElement('div'); wrap.className = 'fea-row'; wrap.style.marginTop = '4px';
            wrap.appendChild(fa); elRail.appendChild(wrap);
        }
    }

    function row(name, info, visible, onVis, onGhost, onRemove, active, ghost, onZoom, rot) {
        var r = document.createElement('div');
        r.className = 'rail-row' + (active ? ' active' : '');
        var eye = document.createElement('input');
        eye.type = 'checkbox'; eye.className = 'fea-check'; eye.checked = visible; eye.title = 'Show / hide';
        eye.addEventListener('change', function () { onVis(eye.checked); });
        r.appendChild(eye);
        var txt = document.createElement('div');
        txt.className = 'rail-text';
        var nm = document.createElement('div'); nm.className = 'rail-name'; nm.textContent = name; nm.title = name;
        var sub = document.createElement('div'); sub.className = 'rail-info'; sub.textContent = info; sub.title = info;
        txt.appendChild(nm); txt.appendChild(sub);
        r.appendChild(txt);
        if (onGhost) {
            var gh = document.createElement('label'); gh.className = 'rail-ghost'; gh.title = 'Translucent';
            var gc = document.createElement('input'); gc.type = 'checkbox'; gc.className = 'fea-check'; gc.checked = !!ghost;
            gc.addEventListener('change', function () { onGhost(gc.checked); });
            gh.appendChild(gc); gh.appendChild(document.createTextNode('ghost'));
            r.appendChild(gh);
        }
        if (rot) {
            var sel = document.createElement('select'); sel.className = 'fea-select rail-rot';
            sel.title = 'Rotate this overlay about the vertical (Z) axis, degrees counter-clockwise, when its plan axes differ from the main model\'s';
            [0, 90, 180, 270].forEach(function (a) {
                var o = document.createElement('option'); o.value = String(a); o.textContent = 'rot ' + a + '\u00b0'; sel.appendChild(o);
            });
            sel.value = String(rot.value);
            sel.addEventListener('change', function () { rot.set(Number(sel.value)); });
            r.appendChild(sel);
        }
        if (onZoom) {
            var z = document.createElement('button'); z.className = 'fea-btn rail-x'; z.innerHTML = '&#8982;'; z.title = 'Zoom to this model';
            z.addEventListener('click', onZoom);
            r.appendChild(z);
        }
        if (onRemove) {
            var x = document.createElement('button'); x.className = 'fea-btn rail-x'; x.textContent = '×'; x.title = 'Remove overlay';
            x.addEventListener('click', onRemove);
            r.appendChild(x);
        }
        return r;
    }

    // ---- file picking: .bin files + their sidecars, paired by base name ----
    function baseName(n) { return String(n).replace(/\.features\.json$/i, '').replace(/\.(bin|json)$/i, ''); }
    async function addFiles(files) {
        var bins = [], jsons = {};
        Array.prototype.forEach.call(files, function (f) {
            if (/\.json$/i.test(f.name)) jsons[baseName(f.name)] = f; else bins.push(f);
        });
        for (var i = 0; i < bins.length; i++) {
            try { await add(bins[i], jsons[baseName(bins[i].name)] || null, baseName(bins[i].name)); }
            catch (err) { log('Overlay ' + bins[i].name + ' failed: ' + err.message); }
        }
        render();
    }
    if (elAdd && elFile) {
        elAdd.addEventListener('click', function () { elFile.value = ''; elFile.click(); });
        elFile.addEventListener('change', function () { addFiles(elFile.files); });
    }

    return {
        add: add,
        addFiles: addFiles,
        pick: pick,
        fillReadout: fillReadout,
        reposition: reposition,
        render: render,
        fitAll: fitAll,
        list: function () { return list; },
        // a new primary keeps its own visibility; overlays stay and are re-placed
        onPrimaryLoaded: function () { primaryVisible = true; reposition(); }
    };
})();
