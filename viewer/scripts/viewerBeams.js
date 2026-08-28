// ================================================================
// viewerBeams.js  --  beam-domain display, layered on viewer.js.
//
// Loaded after viewer.js; viewer.js calls the hooks below (guarded by
// `window.FEABeams`) at model load / dispose, LC change, recolor,
// deform-scale change, and picking. Beams keep their OWN component
// selection and auto range (their quantities are not the shell
// quantities), share the active colormap / abs / alarm settings, and
// draw neutral grey in views that have no beam data yet (envelopes,
// design strengths, Global DSR).
// ================================================================

var FEABeams = (function () {

    var view = null;          // PlutoFormat beam domain view (primary file)
    var build = null;         // FEABeamGeometry.build result
    var beamMesh = null;
    var material = null;
    var lcData = null;        // resident beam LC plane
    var lcLoaded = -1;
    var comp = 0;
    var range = { min: 0, max: 1 };
    var visible = true;
    var xray = false;
    var XRAY_ALPHA = 0.10;    // per-wall brightness; 2 pipes coincident = 4 walls = obvious glow
    var dispLoaded = -1;

    var elSection = document.getElementById('beamSection');
    var elShow    = document.getElementById('beamShow');
    var elXray    = document.getElementById('beamXray');
    var elComp    = document.getElementById('beamComp');
    var elRange   = document.getElementById('beamRange');
    var elCount   = document.getElementById('beamCount');

    function neutralMode() {
        return !!currentEnvelope || (typeof inStrMode === 'function' && inStrMode());
    }

    // ---- lifecycle ------------------------------------------------------
    function onModelLoaded(shellView) {
        onModelCleared();
        var u = shellView && shellView.unified;
        if (!u) return;
        view = PlutoFormat.beamView(u);
        if (!view || view.header.nElements === 0) { view = null; return; }

        build = FEABeamGeometry.build(view);
        material = new THREE.ShaderMaterial({
            uniforms: {
                colormap: { value: texScalar },
                vMin: { value: 0 },
                vMax: { value: 1 },
                alarmThreshold: { value: 0 },
                alarmColor: { value: new THREE.Vector3(1, 0, 1) },
                uAbs: { value: 0 },
                uNeutral: { value: 1 },
                neutralColor: { value: new THREE.Vector3(0.62, 0.64, 0.68) },
                dispScale: { value: 0 },
                uGroupMode: { value: 0 },
                groupPalette: { value: FEAShaders.makePaletteTexture([[200, 200, 200]]) },
                uGroupCount: { value: 1 },
                uXray: { value: 0 }
            },
            vertexShader: FEAShaders.beamVertex,
            fragmentShader: FEAShaders.beamFragment,
            side: THREE.DoubleSide
        });
        applyXray();
        beamMesh = new THREE.Mesh(build.geometry, material);
        beamMesh.visible = visible;
        scene.add(beamMesh);

        // UI
        elSection.style.display = '';
        elCount.textContent = view.header.nElements + ' beams · ' +
            (view.sections ? view.sections.length : 0) + ' sections';
        elComp.innerHTML = '';
        view.meta.components.forEach(function (c, i) {
            var o = document.createElement('option');
            o.value = String(i);
            o.textContent = c.name + (c.unit ? ' [' + c.unit + ']' : '');
            elComp.appendChild(o);
        });
        if (comp >= view.meta.components.length) comp = 0;
        elComp.value = String(comp);
        elComp.disabled = !view.domain.fields;
        log('Beams: ' + view.header.nElements + ' elements, ' +
            view.meta.components.length + ' components' +
            (view.domain.fields ? '' : ' (geometry only)') + '.');
    }

    function onModelCleared() {
        if (beamMesh) {
            scene.remove(beamMesh);
            beamMesh.geometry.dispose();
            beamMesh.material.dispose();
        }
        beamMesh = null; material = null; build = null; view = null;
        lcData = null; lcLoaded = -1; dispLoaded = -1;
        if (elSection) elSection.style.display = 'none';
    }

    async function onLCChanged(lc) {
        if (!view || !view.domain.fields) return;
        try {
            lcData = await feaSet.readLCDomain(lc, 'beam');
            lcLoaded = lc;
        } catch (err) {
            lcData = null;
            log('Beam LC read failed: ' + err.message);
        }
    }

    // Recolor + range; called at the end of every applyComponentAndRange.
    function sync() {
        if (!material) return;
        var neutral = neutralMode() || !lcData;
        material.uniforms.uNeutral.value = neutral ? 1 : 0;
        material.uniforms.colormap.value = texScalar;
        material.uniforms.uAbs.value = absValue ? 1 : 0;
        material.uniforms.alarmThreshold.value = alarmEnabled ? alarmThreshold : 0;
        material.uniforms.alarmColor.value.set(
            alarmColor[0] / 255, alarmColor[1] / 255, alarmColor[2] / 255);
        if (!neutral) {
            FEAAttributes.updateBeamEndVals(build, view, lcData, comp);
            var r = FEAAttributes.computeBeamRange(view, lcData, comp);
            if (absValue) r = absTransformRange(r);
            range = r;
            material.uniforms.vMin.value = r.min;
            material.uniforms.vMax.value = r.max;
            var c = view.meta.components[comp];
            elRange.textContent = c.name + ': ' + fmt(r.min, 4) + ' … ' + fmt(r.max, 4) +
                (c.unit ? ' ' + c.unit : '');
        } else {
            elRange.textContent = lcData ? 'neutral (no beam data in this view)' : 'no beam results';
        }
        needsRender = true;
    }

    // Additive x-ray: depth-write off + additive blending, so brightness counts
    // overlapping walls (order-independent, no transparency sorting artifacts).
    // Coincident pipes glow at double the brightness of a single pipe.
    function applyXray() {
        if (!material) return;
        material.uniforms.uXray.value = xray ? XRAY_ALPHA : 0;
        material.transparent = xray;
        material.depthWrite = !xray;
        material.blending = xray ? THREE.AdditiveBlending : THREE.NormalBlending;
        needsRender = true;
    }

    function setDispScale(v) {
        if (material) material.uniforms.dispScale.value = v;
    }

    // Returns peak |disp| among beam ends (0 when unavailable).
    function refreshDispVecs() {
        if (!build || !lcData || !view.meta.dispVector) return 0;
        var m = FEAAttributes.updateBeamDispVecs(build, view, lcData, view.meta.dispVector);
        dispLoaded = lcLoaded;
        return m;
    }

    // ---- picking --------------------------------------------------------
    function pick(raycaster) {
        if (!beamMesh || !beamMesh.visible) return null;
        var hits = raycaster.intersectObject(beamMesh);
        for (var i = 0; i < hits.length; i++) {
            var fi = hits[i].faceIndex;
            if (fi == null) continue;
            var e = build.triToElem[fi];
            return { beam: true, elem: e, point: hits[i].point, distance: hits[i].distance, faceIndex: fi };
        }
        return null;
    }

    function fillReadout(hit) {
        var e = hit.elem;
        var REC = view.elemRecordU32;
        var n0 = view.elems[e * REC + 1], n1 = view.elems[e * REC + 2];
        var t = FEABeamGeometry.axisParam(build, e, hit.point);
        var sec = view.sections[build.sectionOf[e]];
        var c = view.meta.components[comp];
        var neutral = neutralMode() || !lcData;
        var v = NaN;
        if (!neutral) {
            var cc = view.header.cornerComponents, ms = view.header.maxCorners;
            var a = lcData[e * ms * cc + comp], b = ms > 1 ? lcData[e * ms * cc + cc + comp] : a;
            v = a + (b - a) * t;
            if (absValue) v = Math.abs(v);
        }
        lastQuery = null;                       // shell calc card does not apply
        elRoValue.textContent = neutral ? '—' : (v === v ? fmt(v, 6) + (c.unit ? ' ' + c.unit : '') : 'no data');
        elRoValue.className = 'ro-value' + (v === v ? '' : ' ro-nodata');
        elRoComp.textContent = neutral ? 'beam (neutral view)' :
            c.name + (c.unit ? ' [' + c.unit + ']' : '') + ' (beam ' + c.kind + ')';
        var grp = window.FEAFeatures ? FEAFeatures.groupOf('beam', e) : null;
        var lbl = view.labels ? view.labels.get(e) : '';
        elRoElem.textContent = view.elemIds[e] + (lbl ? ' [' + lbl + ']' : '') + '  (beam idx ' + e + ', ' +
            (sec ? sec.name + ' ' + sec.type : 'no section') + (grp ? ', group: ' + grp : '') + ')';
        var nn = t < 0.5 ? n0 : n1;
        var nlbl = view.nodeLabels ? view.nodeLabels.get(nn) : '';
        elRoNode.textContent = view.nodeIds[nn] + (nlbl ? ' [' + nlbl + ']' : '');
        elRoCorners.textContent = view.nodeIds[n0] + ' → ' + view.nodeIds[n1];
        elRoUV.textContent = 't = ' + t.toFixed(4);
        elRoPos.textContent = hit.point.x.toFixed(2) + ', ' +
            hit.point.y.toFixed(2) + ', ' + hit.point.z.toFixed(2);
        if (elRoControllingRow) elRoControllingRow.style.display = 'none';
    }

    // ---- UI -------------------------------------------------------------
    if (elShow) elShow.addEventListener('change', function () {
        visible = this.checked;
        if (beamMesh) beamMesh.visible = visible;
        needsRender = true;
    });
    if (elXray) elXray.addEventListener('change', function () {
        xray = this.checked;
        applyXray();
    });
    if (elComp) elComp.addEventListener('change', function () {
        comp = parseInt(this.value, 10) || 0;
        sync();
        if (view) log('Beam component: ' + view.meta.components[comp].name);
    });

    return {
        onModelLoaded: onModelLoaded,
        onModelCleared: onModelCleared,
        onLCChanged: onLCChanged,
        sync: sync,
        setDispScale: setDispScale,
        refreshDispVecs: refreshDispVecs,
        pick: pick,
        fillReadout: fillReadout,
        mesh: function () { return beamMesh; },
        view: function () { return view; },
        build: function () { return build; }
    };
})();
