// ================================================================
// overlays.js  --  extra models drawn beside the loaded one, and the model rail.
// Design: vault/viewer/model-rail.md · module map: vault/viewer/Viewer modules.md
//
// Owns: the rail (one row per model), "Add overlay...", file pairing, placement.
// Each overlay is a FEAModelMesh drawable (shells + beams under one group) wrapped
// as a model context (models.js) and registered with FEAModels:
//   - click a row to make that model ACTIVE: the Groups tab, its filter / eyes /
//     colours and Save features then act on it (features.js through the context)
//   - colours: a model is painted by its own groups (Groups tab state; an overlay's
//     groups paint unless they say hidden). With its group painting off, an overlay
//     is drawn in its MODEL COLOUR (swatch on its row); the primary keeps its field.
//   - x-ray per row (every model): additive translucency, overlaps read brighter
//   - placed at true world coordinates: each file's exporter recenter
//     (sidecar model.units.worldOffset) and the viewer recenter are added back,
//     lengths converted to the primary's unit; plan rotation 0/90/180/270 per row
//   - pickable: the readout names the overlay, the member label and group
// Stays primary-only (stage 3): load cases / legend, section cuts, predicates.
// ================================================================

var FEAOverlays = (function () {

    var list = [];            // overlay records, in load order
    var OVERLAY_COLORS = ['#9ea3ad', '#c9a46b', '#7fb3ff', '#8ae0a5', '#d08ad0', '#e0c25a'];
    var nextColor = 0;
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
        ov.root.scale.setScalar(s);
        ov.root.rotation.set(0, 0, ov.rotZ * Math.PI / 180);
        // world(ov) = local + localOrigin + worldOffset; scene = R (world * s) - primary origin
        var w = toPrimaryWorld(ov, [ov.localOrigin[0] + ov.offset[0], ov.localOrigin[1] + ov.offset[1], ov.localOrigin[2] + ov.offset[2]], s);
        ov.root.position.set(w[0] - po[0], w[1] - po[1], w[2] - po[2]);
    }

    function reposition() {
        var po = primaryOrigin();
        list.forEach(function (ov) { placeOne(ov, po); });
        render();
        if (typeof needsRender !== 'undefined') needsRender = true;
    }

    // The overlay as a model context (models.js interface).
    function makeContext(ov) {
        var d = ov.drawable, sv = PlutoFormat.shellView(ov.unified);
        var ctx = {
            id: 'ov:' + ov.name + ':' + Date.now(), kind: 'overlay', feat: {}, ov: ov,
            look: { visible: true, xray: false, color: ov.color },
            name: function () { return ov.name; },
            shellModel: function () { return sv; },
            beamView: function () { return d.beam ? d.beam.view : null; },
            paint: d.paint,
            setGroupLook: d.setGroupLook,
            applyVisibility: function () {
                d.setElemOff('shell', FEAFeatures.elemOffFor(ctx, 'shell'));
                d.setElemOff('beam', FEAFeatures.elemOffFor(ctx, 'beam'));
                redraw();
            },
            markerParent: function () { return d.root; },
            setVisible: function (on) { ctx.look.visible = on; d.setVisible(on); redraw(); },
            setXray: function (on) { ctx.look.xray = on; d.setXray(on); redraw(); },
            setColor: function (hex) { ctx.look.color = hex; d.setColor(hex); redraw(); }
        };
        return ctx;
    }
    function redraw() { if (typeof needsRender !== 'undefined') needsRender = true; }
    function counts(ov) {
        var d = ov.drawable, a = [];
        if (d.shell) a.push(d.shell.view.header.nElements + ' shells');
        if (d.beam) a.push(d.beam.view.header.nElements + ' beams');
        return a.join(' + ');
    }

    // binFile: Blob/File of the .bin; jsonFile: its features sidecar (optional).
    async function add(binFile, jsonFile, name) {
        if (typeof feaModel === 'undefined' || !feaModel) { log('Load a model first; overlays are placed relative to it.'); return null; }
        var unified = await PlutoFormat.load(binFile, log);
        var sv = PlutoFormat.shellView(unified), bv = PlutoFormat.beamView(unified);
        if (!(sv && sv.header.nElements > 0) && !(bv && bv.header.nElements > 0)) { log('Overlay ' + name + ': no shells or beams in the file.'); return null; }
        var envelope = null;
        if (jsonFile) {
            try { envelope = JSON.parse(await jsonFile.text()); }
            catch (err) { log('Overlay ' + name + ': features file unreadable (' + err.message + ').'); }
        }
        var ov = {
            name: name, unified: unified, envelope: envelope,
            unit: unitOf(unified), offset: parseOffset(envelope), rotZ: 0,
            color: OVERLAY_COLORS[nextColor++ % OVERLAY_COLORS.length]
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

        ov.drawable = FEAModelMesh.create(unified, ov.color);
        ov.root = ov.drawable.root;
        scene.add(ov.root);
        list.push(ov);
        ov.ctx = makeContext(ov);
        FEAModels.register(ov.ctx);
        FEAFeatures.attach(ov.ctx, envelope, name + '.features.json');
        reposition();
        var ng = envelope && envelope.groups && envelope.groups.items ? envelope.groups.items.length : 0;
        log('Overlay ' + name + ': ' + counts(ov) + ', ' + ng + ' groups, units ' + (ov.unit || '?') +
            (ov.unitNote ? ' (' + ov.unitNote + ')' : '') + '.');
        reportPlacement(ov);                 // last, so it is the line the status shows
        return ov;
    }

    function remove(ov) {
        var i = list.indexOf(ov);
        if (i < 0) return;
        FEAFeatures.detach(ov.ctx);
        FEAModels.unregister(ov.ctx);
        scene.remove(ov.root);
        ov.drawable.dispose();
        list.splice(i, 1);
        render();
        redraw();
    }

    // Nearest visible overlay member under the ray: { overlay, fam, elem, point, distance }.
    function pick(raycaster) {
        var best = null;
        list.forEach(function (ov) {
            if (!ov.ctx.look.visible) return;
            var h = ov.drawable.pick(raycaster);
            if (h && (!best || h.distance < best.distance)) { h.overlay = ov; best = h; }
        });
        return best;
    }

    function fillReadout(hit) {
        var ov = hit.overlay, d = ov.drawable, e = hit.elem, fam = hit.fam;
        var lbl = d.label(fam, e);
        var g = FEAFeatures.groupOfIn(ov.ctx, fam, e) || 'no group';
        var sec = fam === 'beam' && d.beam.view.sections && d.beam.build.sectionOf ? d.beam.view.sections[d.beam.build.sectionOf[e]] : null;
        elRoValue.textContent = g;
        elRoValue.className = 'ro-value';
        elRoComp.textContent = 'overlay ' + ov.name + (sec && sec.name ? ' · ' + sec.name : '');
        elRoElem.textContent = d.elemId(fam, e) + (lbl ? ' [' + lbl + ']' : '') + '  (overlay ' + fam + ')';
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
        if (!obj) return b;
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
        if (FEAModels.primary().look.visible) b.union(primaryBox());
        list.forEach(function (ov) { if (ov.ctx.look.visible) b.union(sceneBox(ov.root)); });
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
            var oc = sceneBox(ov.root).getCenter(new THREE.Vector3());
            var d = oc.distanceTo(pc);
            if (!best || d < best.d) best = { rot: a, d: d };
        });
        ov.rotZ = keep; placeOne(ov, po);
        return best;
    }

    // One log line per overlay: where it is, where the main model is, how far apart.
    function reportPlacement(ov) {
        var pb = primaryBox(), ob = sceneBox(ov.root);
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
    // One row per model: [show] name / info [x-ray] [colour] [rot] [zoom] [x]. Clicking the
    // name makes the model active (the Groups tab follows it).
    function render() {
        if (!elRail) return;
        elRail.innerHTML = '';
        var hasPrimary = typeof feaModel !== 'undefined' && !!feaModel;
        if (!hasPrimary) { elRail.innerHTML = '<div class="fea-hint">no model loaded</div>'; return; }
        var u = primaryUnit(), act = FEAModels.active(), pc = FEAModels.primary();
        elRail.appendChild(row(pc, window.feaPrimaryName || 'model',
            'main model · centre ' + fmtXYZ(worldCentre(primaryBox())) + ' ' + u,
            null, function () { zoomToBox(primaryBox()); }, null, act === pc));
        list.forEach(function (ov) {
            var off = ov.offset.some(function (x) { return x !== 0; }) ? 'offset ' + fmtXYZ(ov.offset) + ' ' + (ov.unit || '?') : 'NO offset';
            var info = counts(ov) + ' · centre ' + fmtXYZ(worldCentre(sceneBox(ov.root))) + ' ' + u + ' · ' + off + (ov.unitNote ? ' · ' + ov.unitNote : '');
            elRail.appendChild(row(ov.ctx, ov.name, info, function () { remove(ov); },
                function () { zoomToBox(sceneBox(ov.root)); },
                { value: ov.rotZ, set: function (a) {
                    ov.rotZ = a;
                    try { localStorage.setItem('pluto.ovRot.' + ov.name, String(a)); } catch (e1) {}
                    reposition(); reportPlacement(ov);
                } }, act === ov.ctx));
        });
        if (list.length) {
            var fa = document.createElement('button'); fa.className = 'fea-btn'; fa.textContent = 'Fit all models';
            fa.title = 'Frame every visible model (sets the camera range to all of them)';
            fa.addEventListener('click', fitAll);
            var hint = document.createElement('span'); hint.className = 'fea-hint'; hint.style.marginLeft = '6px';
            hint.textContent = 'click a name: the Groups tab shows that model';
            var wrap = document.createElement('div'); wrap.className = 'fea-row'; wrap.style.marginTop = '4px';
            wrap.appendChild(fa); wrap.appendChild(hint); elRail.appendChild(wrap);
        }
    }

    function row(ctx, name, info, onRemove, onZoom, rot, active) {
        var r = document.createElement('div');
        r.className = 'rail-row' + (active ? ' active' : '');
        var eye = document.createElement('input');
        eye.type = 'checkbox'; eye.className = 'fea-check'; eye.checked = ctx.look.visible; eye.title = 'Show / hide';
        eye.addEventListener('change', function () { ctx.setVisible(eye.checked); });
        r.appendChild(eye);
        var txt = document.createElement('div');
        txt.className = 'rail-text';
        txt.title = active ? 'Active: the Groups tab shows this model' : 'Click to make active: the Groups tab shows this model';
        var nm = document.createElement('div'); nm.className = 'rail-name'; nm.textContent = name;
        var sub = document.createElement('div'); sub.className = 'rail-info'; sub.textContent = info;
        txt.appendChild(nm); txt.appendChild(sub);
        txt.addEventListener('click', function () { FEAModels.setActive(ctx); });
        r.appendChild(txt);
        var xr = document.createElement('label'); xr.className = 'rail-ghost';
        xr.title = 'X-ray: translucent, overlaps read brighter';
        var xc = document.createElement('input'); xc.type = 'checkbox'; xc.className = 'fea-check'; xc.checked = !!ctx.look.xray;
        xc.addEventListener('change', function () { ctx.setXray(xc.checked); });
        xr.appendChild(xc); xr.appendChild(document.createTextNode('x-ray'));
        r.appendChild(xr);
        if (ctx.setColor) {
            var sw = document.createElement('input'); sw.type = 'color'; sw.className = 'rail-swatch'; sw.value = ctx.look.color;
            sw.title = 'Model colour: used where this model is not painted by its groups';
            sw.addEventListener('input', function () { ctx.setColor(sw.value); });
            r.appendChild(sw);
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
    FEAModels.onChange(function () { render(); });

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
        // a new primary is shown; overlays stay and are re-placed
        onPrimaryLoaded: function () { FEAModels.primary().look.visible = true; reposition(); }
    };
})();
