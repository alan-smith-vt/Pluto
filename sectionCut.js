// ================================================================
// sectionCut.js  --  axis-aligned section cuts.
//
// "New section cut" arms placement; Ctrl+left-click then raycasts
// the center onto the mesh. The cut is a line through that center
// along X, Y or Z with total length L (L/2 each way). At ~200
// stations along the line a short probe ray (along the picked
// face's normal) re-hits the mesh and evaluates the currently
// displayed field there via FEAQuery. Stations that miss (door
// openings, past the building extents) or read NaN are gaps: they
// contribute neither to the area integral nor to the effective
// length. Trapezoidal integration over contiguous hit segments.
//
// Depends on viewer.js globals: scene, mesh, feaModel, feaBuild,
// feaLCData, currentComp, currentStr, smoothing, absValue,
// envGlobalDsr*, inDsrMode(), inStrMode(), strengthComponents(),
// nodeVec(), fmt(), requestRender().
// ================================================================

var FEASectionCut = (function () {

    var N_SAMPLES = 200;

    var AXIS_DIR = {
        x: new THREE.Vector3(1, 0, 0),
        y: new THREE.Vector3(0, 1, 0),
        z: new THREE.Vector3(0, 0, 1)
    };
    var AXIS_CSS = { x: '#ff5252', y: '#6ee06e', z: '#5f8cff' };

    var armed = false;
    var axis = 'x';
    var cutLength = 0;          // 0 -> auto default on first placement
    var cut = null;             // { center: Vector3, normal: Vector3 }
    var visual = null;          // THREE.Group holding the marker parts
    // Parts whose size tracks camera distance. mode 'uniform' scales all
    // three axes; 'radial' scales only X/Z so a bar keeps its true length.
    var visParts = [];
    var lastScale = -1;
    var samples = null;         // { t:[], v:[], hit:[], area, effLen }
    var isolated = null;        // Uint8Array[nElem] keep-flags, or null
    var probeRay = new THREE.Raycaster();

    // ---- DOM ---------------------------------------------------
    var elPanel = document.getElementById('scPanel');
    var elTab   = document.getElementById('scTab');
    var elNew   = document.getElementById('scNew');
    var elAxisX = document.getElementById('scAxisX');
    var elAxisY = document.getElementById('scAxisY');
    var elAxisZ = document.getElementById('scAxisZ');
    var elLen   = document.getElementById('scLen');
    var elUnits = document.getElementById('scUnits');
    var elIso   = document.getElementById('scIsolate');
    var elMarker    = document.getElementById('scMarker');
    var elColorMode = document.getElementById('scColorMode');
    var elColor     = document.getElementById('scColor');
    var elThick     = document.getElementById('scThick');
    var elPlot  = document.getElementById('scPlot');
    var elStats = document.getElementById('scStats');

    elTab.addEventListener('click', function () {
        elPanel.classList.toggle('collapsed');
    });

    elNew.addEventListener('click', function () {
        setArmed(!armed);
    });

    elAxisX.addEventListener('click', function () { setAxis('x'); });
    elAxisY.addEventListener('click', function () { setAxis('y'); });
    elAxisZ.addEventListener('click', function () { setAxis('z'); });

    elLen.addEventListener('change', function () {
        var v = parseFloat(this.value.replace(/,/g, ''));
        if (isNaN(v) || v <= 0) { this.value = cutLength ? String(cutLength) : ''; return; }
        cutLength = v;
        rebuild();
    });

    elUnits.addEventListener('change', function () { drawPlot(); });

    elMarker.addEventListener('change', function () {
        updateVisual();
    });

    elColorMode.addEventListener('change', function () {
        syncColorSwatch();
        updateVisual();
        drawPlot();
    });

    // Touching the swatch means you want that color -- flip to Custom
    // rather than silently discarding the pick.
    elColor.addEventListener('input', function () {
        elColorMode.value = 'custom';
        elColor.disabled = false;
        updateVisual();
        drawPlot();
    });

    elThick.addEventListener('change', function () {
        var v = parseFloat(this.value);
        if (!(isFinite(v) && v > 0)) { this.value = '1'; }
        else if (v > 20) { this.value = '20'; }
        lastScale = -1;           // force updateScale past its no-op guard
        updateScale();
    });

    elIso.addEventListener('change', function () {
        applyIsolation();
        resample();          // isolation also scopes what the probe samples
        drawPlot();
    });

    function setArmed(on) {
        armed = on;
        elNew.classList.toggle('active', on);
        elNew.textContent = on ? 'Ctrl+click the model…' : 'New section cut';
    }

    function setAxis(a) {
        axis = a;
        elAxisX.classList.toggle('active', a === 'x');
        elAxisY.classList.toggle('active', a === 'y');
        elAxisZ.classList.toggle('active', a === 'z');
        syncColorSwatch();
        rebuild();
    }

    // ---- marker color ------------------------------------------
    // 'By axis' follows the direction buttons; 'Custom' takes the
    // swatch. Both the 3D marker and the plot curve use it, so the
    // two always read as the same cut.
    // Mode is 'axis', 'custom', or a literal '#rrggbb' preset.
    function markerCss() {
        var m = elColorMode.value;
        if (m === 'custom') return elColor.value;
        if (m === 'axis') return AXIS_CSS[axis];
        return m;
    }

    function markerHex() {
        return parseInt(markerCss().slice(1), 16);
    }

    // Outside 'Custom' the swatch is a read-only preview of the
    // color the current mode resolves to.
    function syncColorSwatch() {
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
    // Value unit of the displayed field ('' for DSR: dimensionless);
    // model length unit from the dropdown (initialized from the
    // optional `lengthUnit` metadata key when the file declares one).
    function valueUnit() {
        if (!feaModel || inDsrMode()) return '';
        var c = activeComponent();
        return (c && c.unit) ? c.unit : '';
    }

    function lengthUnit() { return elUnits.value; }   // '' | 'in' | 'ft'

    // Recognize a length-unit token; returns 'in' / 'ft' or null.
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
        if (mu) elUnits.value = mu;
    }

    // Displayed area value + unit. The raw integral is value x model
    // length. When the value unit divides by a length (e.g. kip/ft)
    // and the model unit is known, the cut length converts into that
    // denominator so it cancels: kip/ft over inches -> kip. Otherwise
    // the units are shown side by side ('len' when the model unit is
    // undeclared).
    function areaDisplay(rawArea) {
        var vu = valueUnit(), mu = normLen(lengthUnit());
        var slash = vu.lastIndexOf('/');
        if (mu && slash > 0) {
            var den = normLen(vu.slice(slash + 1));
            if (den) {
                return {
                    value: rawArea * FT_PER[mu] / FT_PER[den],
                    unit: vu.slice(0, slash)
                };
            }
        }
        var lu = lengthUnit();
        return {
            value: rawArea,
            unit: (vu || lu) ? (vu || '1') + '·' + (lu || 'len') : ''
        };
    }

    function withUnit(text, unit) {
        return unit ? text + ' ' + unit : text;
    }

    // Units come from file metadata -- escape before innerHTML use.
    function esc(s) {
        return String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;');
    }

    // ---- helpers -----------------------------------------------
    function modelSpan() {
        if (!mesh) return 1;
        var d = new THREE.Box3().setFromObject(mesh).getSize(new THREE.Vector3());
        return Math.max(d.x, d.y, d.z) || 1;
    }

    // Same tolerance geometryBuilder uses to keep wall and slab normal
    // groups from bleeding into each other (cos 15°, sign-insensitive).
    var NORMAL_DOT_THRESHOLD = 0.966;

    function normalMatchesCut(e) {
        var nrm = feaBuild.elemNormals;
        var dot = nrm[e * 3] * cut.normal.x +
                  nrm[e * 3 + 1] * cut.normal.y +
                  nrm[e * 3 + 2] * cut.normal.z;
        return Math.abs(dot) > NORMAL_DOT_THRESHOLD;
    }

    // A probe hit is usable when its element lies in the cut's plane --
    // and, while isolating, when it belongs to the isolated panel too,
    // so the plot measures exactly what stays on screen.
    function faceUsable(faceIndex) {
        var e = feaBuild.triToElem[faceIndex];
        if (isolated) return !!isolated[e];
        return normalMatchesCut(e);
    }

    // ---- isolation: the connected panel the cut sits on ---------
    // Flood fill from the placement element through shared nodes,
    // admitting only elements whose normal still matches the cut's.
    // A wall and the slab it lands on break apart at the joint;
    // openings are walked around, so a wall with doors stays whole.
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

    function computeIsolatedElems() {
        var nElem = feaBuild.elemNCount.length;
        var keep = new Uint8Array(nElem);
        var map = nodeToElems();
        var queue = [cut.elem];
        keep[cut.elem] = 1;
        for (var qi = 0; qi < queue.length; qi++) {
            var e = queue[qi];
            var nc = feaBuild.elemNCount[e];
            for (var k = 0; k < nc; k++) {
                var nbrs = map[feaBuild.elemCorners[e * 4 + k]];
                if (!nbrs) continue;
                for (var j = 0; j < nbrs.length; j++) {
                    var n2 = nbrs[j];
                    if (keep[n2] || !normalMatchesCut(n2)) continue;
                    keep[n2] = 1;
                    queue.push(n2);
                }
            }
        }
        return keep;
    }

    // Write the keep-set into the mesh + edge elemVis attributes; a
    // null set means "show everything".
    function writeVis(keep) {
        if (!mesh || !feaBuild) return;
        var nElem = feaBuild.elemNCount.length;
        var attr = mesh.geometry.getAttribute('elemVis');
        var arr = attr.array;
        var edgeAttr = feaEdges ? feaEdges.geometry.getAttribute('elemVis') : null;
        var eArr = edgeAttr ? edgeAttr.array : null;
        var vptr = 0, eptr = 0;
        for (var e = 0; e < nElem; e++) {
            var nc = feaBuild.elemNCount[e];
            var vis = keep ? keep[e] : 1;
            var nv = nc === 4 ? 6 : 3;
            for (var i = 0; i < nv; i++) arr[vptr++] = vis;
            if (eArr) for (var q = 0; q < nc * 2; q++) eArr[eptr++] = vis;
        }
        attr.needsUpdate = true;
        if (edgeAttr) edgeAttr.needsUpdate = true;
        requestRender();
    }

    function applyIsolation() {
        if (!mesh || !feaBuild) return;
        isolated = (elIso.checked && cut) ? computeIsolatedElems() : null;
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
            dsrMode: inDsr ? {
                value: envGlobalDsrValue,
                source: envGlobalDsrSource,
                lc: envGlobalDsrLC
            } : null
        }, faceIndex, point);
        var v = q.value;
        if (!inDsr && absValue && v === v) v = Math.abs(v);
        return v;
    }

    // ---- placement (called from viewer.js Ctrl+click) ----------
    function wantsPick() { return armed && !!feaModel; }

    function placeCenter(hit) {
        cut = {
            center: hit.point.clone(),
            normal: faceNormal(hit.faceIndex),
            elem: feaBuild.triToElem[hit.faceIndex]
        };
        if (!(cutLength > 0)) {
            // default: half the model span, rounded to 2 sig figs
            var s = modelSpan() * 0.5;
            var mag = Math.pow(10, Math.floor(Math.log10(s)) - 1);
            cutLength = Math.round(s / mag) * mag;
            elLen.value = String(cutLength);
        }
        setArmed(false);
        elPanel.classList.remove('collapsed');
        rebuild();
    }

    // ---- visual: target-style sphere + L/2 cylinder each way ---
    function removeVisual() {
        if (!visual) return;
        scene.remove(visual);
        visual.traverse(function (o) {
            if (o.geometry) o.geometry.dispose();
            if (o.material) o.material.dispose();
        });
        visual = null;
        visParts = [];
    }

    // Every marker part is a cylinder or sphere rather than a THREE
    // line: WebGL clamps line width to 1px on essentially every
    // platform, so a line-based marker could not honor the thickness
    // input at all.
    var THIN_R = 0.08;          // hairline radius vs the solid bar's 0.3

    // Aim a cylinder's local +Y along a world axis.
    function orientAlong(obj, ax) {
        if (ax === 'x') obj.rotation.z = Math.PI / 2;
        else if (ax === 'z') obj.rotation.x = Math.PI / 2;
    }

    function perpAxes() {
        return axis === 'x' ? ['y', 'z'] : axis === 'y' ? ['x', 'z'] : ['x', 'y'];
    }

    // Solid: an orb like the focus target with a bar running the full
    // cut length. Reads clearly but covers the field it sits on.
    function buildSolidMarker(group) {
        var mat = new THREE.MeshPhongMaterial({
            color: markerHex(), shininess: 60 });
        // Unit-radius geometry; updateScale() sizes it per frame by
        // camera distance, exactly like the focus orb.
        var sph = new THREE.Mesh(new THREE.SphereGeometry(1, 24, 16), mat);
        group.add(sph);
        visParts.push({ obj: sph, axial: 'iso' });

        var cyl = new THREE.Mesh(
            new THREE.CylinderGeometry(0.3, 0.3, cutLength, 16), mat);
        orientAlong(cyl, axis);
        group.add(cyl);
        visParts.push({ obj: cyl, axial: 'model' });
    }

    // Thin / dotted: a hairline spine down the cut (solid, or broken
    // into dashes) plus a crosshair at the center. Drawn depth-test
    // free -- a hairline lying exactly on the surface it measures
    // would z-fight, and staying visible is the point of the style.
    function buildThinMarker(group, dashed) {
        var mat = new THREE.MeshBasicMaterial({
            color: markerHex(), depthTest: false });
        var half = cutLength / 2;

        if (dashed) {
            // One shared geometry for every dash; the marker's length
            // stays in model units, so the dash pattern is fixed to
            // the cut rather than to the zoom.
            var nDash = 24;
            var period = cutLength / nDash;
            var geo = new THREE.CylinderGeometry(THIN_R, THIN_R, period * 0.45, 8);
            var d = AXIS_DIR[axis];
            for (var i = 0; i < nDash; i++) {
                var dash = new THREE.Mesh(geo, mat);
                orientAlong(dash, axis);
                dash.position.copy(d).multiplyScalar(-half + period * (i + 0.5));
                dash.renderOrder = 8;
                group.add(dash);
                visParts.push({ obj: dash, axial: 'model' });
            }
        } else {
            var spine = new THREE.Mesh(
                new THREE.CylinderGeometry(THIN_R, THIN_R, cutLength, 8), mat);
            orientAlong(spine, axis);
            spine.renderOrder = 8;
            group.add(spine);
            visParts.push({ obj: spine, axial: 'model' });
        }

        // Two arms across the cut. Their LENGTH is screen-sized, so the
        // crosshair holds its apparent size at any zoom.
        var armGeo = new THREE.CylinderGeometry(THIN_R, THIN_R, 1, 8);
        perpAxes().forEach(function (p) {
            var arm = new THREE.Mesh(armGeo, mat);
            orientAlong(arm, p);
            arm.renderOrder = 8;
            group.add(arm);
            visParts.push({ obj: arm, axial: 2.5 });
        });
    }

    function updateVisual() {
        removeVisual();
        if (!cut || !mesh) return;
        var style = elMarker.value;
        if (style === 'none') { requestRender(); return; }

        var group = new THREE.Group();
        if (style === 'thin' || style === 'dotted')
            buildThinMarker(group, style === 'dotted');
        else buildSolidMarker(group);
        group.position.copy(cut.center);
        scene.add(group);
        visual = group;
        lastScale = -1;
        updateScale();
        requestRender();
    }

    // Distance-based marker size, same law as viewer.js orbScale() but
    // measured to the cut center. Radial dimensions also take the
    // thickness multiplier; each part declares what to do with its
    // local Y:
    //   'iso'    -- a sphere: scale all three axes alike
    //   'model'  -- length is real geometry (bar, spine, dash): leave it
    //   <number> -- length is screen-sized (crosshair arms): scale by s*n
    function updateScale() {
        if (!visual || !cut || !visParts.length) return;
        var dist = camera.position.distanceTo(cut.center);
        var s = dist < 0.03 ? 0
            : dist > 20 ? 0.01 * dist
            : Math.max(0.1, 0.074 - 0.048 * Math.log(dist));
        if (Math.abs(s - lastScale) < Math.abs(lastScale) * 1e-3) return;
        lastScale = s;
        visual.visible = s > 0;
        if (s > 0) {
            var r = s * thickness();
            for (var i = 0; i < visParts.length; i++) {
                var p = visParts[i];
                if (p.axial === 'iso') p.obj.scale.setScalar(r);
                else if (p.axial === 'model') p.obj.scale.set(r, 1, r);
                else p.obj.scale.set(r, s * p.axial, r);
            }
        }
        requestRender();
    }

    // ---- sampling + integration --------------------------------
    function resample() {
        samples = null;
        if (!cut || !mesh || !feaModel || !(cutLength > 0)) return;
        var dir = AXIS_DIR[axis];
        var eps = modelSpan() * 0.01;
        var back = cut.normal.clone().negate();
        var origin = new THREE.Vector3();
        probeRay.near = 0;
        probeRay.far = eps * 2;
        var t = [], v = [], hit = [];
        for (var i = 0; i <= N_SAMPLES; i++) {
            var s = -cutLength / 2 + cutLength * i / N_SAMPLES;
            origin.copy(cut.center)
                .addScaledVector(dir, s)
                .addScaledVector(cut.normal, eps);
            probeRay.set(origin, back);
            var hs = probeRay.intersectObject(mesh);
            var val = NaN;
            // Take the nearest hit whose element is coplanar-ish with the
            // placement element (same normal-similarity rule the smoothing
            // groups use), so a cut along a wall base never reads the
            // perpendicular slab elements that share its nodes.
            for (var hI = 0; hI < hs.length; hI++) {
                var fi = hs[hI].faceIndex;
                if (fi == null) continue;
                if (!faceUsable(fi)) continue;
                val = queryValue(fi, hs[hI].point);
                break;
            }
            t.push(s);
            v.push(val);
            hit.push(val === val);
        }
        var dx = cutLength / N_SAMPLES, area = 0, eff = 0;
        for (var k = 0; k < N_SAMPLES; k++) {
            if (hit[k] && hit[k + 1]) {
                area += 0.5 * (v[k] + v[k + 1]) * dx;
                eff += dx;
            }
        }
        samples = { t: t, v: v, hit: hit, area: area, effLen: eff };
    }

    // ---- plot --------------------------------------------------
    function drawPlot() {
        var ctx = elPlot.getContext('2d');
        var W = elPlot.width, H = elPlot.height;
        ctx.clearRect(0, 0, W, H);
        ctx.fillStyle = '#1a1a1f';
        ctx.fillRect(0, 0, W, H);
        ctx.font = '10px Consolas, monospace';

        if (!samples) {
            ctx.fillStyle = '#555';
            ctx.textAlign = 'center';
            ctx.fillText(cut ? 'no data' : 'no section cut', W / 2, H / 2);
            elStats.textContent = '—';
            return;
        }

        // Data extremes and where along the cut they occur -- these get
        // named on the plot. The AXIS ends are padded past them below,
        // so the curve never runs into the frame.
        var vmin = Infinity, vmax = -Infinity, any = false;
        var iMin = 0, iMax = 0;
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
            elStats.textContent =
                'Area: —   Average: —   Effective length: 0 / ' + fmt(cutLength, 3);
            return;
        }
        vmin = Math.min(vmin, 0);
        vmax = Math.max(vmax, 0);
        var pad = (vmax - vmin) * 0.06 || 1;
        vmin -= pad; vmax += pad;

        // The axis spans only the captured run -- from the first station
        // that hit an element to the last. Length asked for past the
        // building edge is dead space, so it isn't given plot width.
        var iFirst = 0, iLast = N_SAMPLES;
        while (iFirst < N_SAMPLES && !samples.hit[iFirst]) iFirst++;
        while (iLast > iFirst && !samples.hit[iLast]) iLast--;

        var padL = 4, padR = 4, padT = 14, padB = 13;
        var pw = W - padL - padR, ph = H - padT - padB;
        function px(i) {
            return iLast === iFirst ? padL + pw / 2
                 : padL + pw * (i - iFirst) / (iLast - iFirst);
        }
        function py(val) { return padT + ph * (1 - (val - vmin) / (vmax - vmin)); }

        // zero line
        var y0 = py(0);
        ctx.strokeStyle = 'rgba(255,255,255,0.18)';
        ctx.beginPath();
        ctx.moveTo(padL, y0);
        ctx.lineTo(W - padR, y0);
        ctx.stroke();

        // fill to zero + stroke, broken at gaps
        var col = markerCss();
        ctx.fillStyle = hexA(col, 0.18);
        for (var a = 0; a <= N_SAMPLES;) {
            if (!samples.hit[a]) { a++; continue; }
            var b = a;
            while (b + 1 <= N_SAMPLES && samples.hit[b + 1]) b++;
            // fill
            ctx.beginPath();
            ctx.moveTo(px(a), y0);
            for (var j = a; j <= b; j++) ctx.lineTo(px(j), py(samples.v[j]));
            ctx.lineTo(px(b), y0);
            ctx.closePath();
            ctx.fill();
            // line
            ctx.strokeStyle = col;
            ctx.lineWidth = 1.4;
            ctx.beginPath();
            ctx.moveTo(px(a), py(samples.v[a]));
            for (var j2 = a; j2 <= b; j2++) ctx.lineTo(px(j2), py(samples.v[j2]));
            ctx.stroke();
            a = b + 1;
        }
        ctx.lineWidth = 1;

        // Extreme guides: a dashed rule at each peak value, a dot where
        // it occurs along the cut, and the value named at the rule --
        // the frame edges are padded and carry no meaning of their own.
        var vu = valueUnit(), lu = lengthUnit();
        function guide(val, idx, label, above) {
            var gy = py(val);
            ctx.save();
            ctx.setLineDash([3, 3]);
            ctx.strokeStyle = 'rgba(255,255,255,0.30)';
            ctx.beginPath();
            ctx.moveTo(padL, gy);
            ctx.lineTo(W - padR, gy);
            ctx.stroke();
            ctx.restore();
            ctx.fillStyle = col;
            ctx.beginPath();
            ctx.arc(px(idx), gy, 2.2, 0, Math.PI * 2);
            ctx.fill();
            // Keep the text inside the canvas when a peak sits hard
            // against the top or bottom of the plot area.
            var ty = above ? Math.max(9, gy - 4) : Math.min(H - 3, gy + 11);
            ctx.fillStyle = '#ddd';
            ctx.textAlign = 'left';
            ctx.fillText(label + ' ' + withUnit(fmt(val, 3), vu), padL + 2, ty);
        }

        if (dMax === dMin) {
            guide(dMax, iMax, 'const', true);
        } else {
            // Both labels sit above their rule; if the two rules are
            // nearly on top of each other, drop the lower one's label
            // underneath so they can't overlap.
            guide(dMax, iMax, 'max', true);
            guide(dMin, iMin, 'min', (py(dMin) - py(dMax)) >= 14);
        }

        // Station values at the ends of the captured run, so a trimmed
        // axis still says where along the cut it starts and stops.
        ctx.fillStyle = '#999';
        ctx.textAlign = 'left';
        ctx.fillText(fmt(samples.t[iFirst], 3), padL + 2, H - padB + 10);
        ctx.textAlign = 'right';
        ctx.fillText(fmt(samples.t[iLast], 3) + '  ' + axis.toUpperCase() +
            (lu ? ' [' + lu + ']' : ''),
            W - padR - 2, H - padB + 10);

        var avg = samples.effLen > 0 ? samples.area / samples.effLen : NaN;
        var area = areaDisplay(samples.area);
        elStats.innerHTML =
            'Area ∫v·ds: <b style="color:#fff">' +
                withUnit(fmt(area.value, 4), esc(area.unit)) + '</b><br>' +
            'Average (area / eff. len): <b style="color:#fff">' +
                withUnit(fmt(avg, 4), esc(vu)) + '</b><br>' +
            'Effective length: <b style="color:#fff">' + fmt(samples.effLen, 3) + '</b>' +
            ' / ' + withUnit(fmt(cutLength, 3), esc(lu));
    }

    function hexA(hex, a) {
        var r = parseInt(hex.slice(1, 3), 16),
            g = parseInt(hex.slice(3, 5), 16),
            b = parseInt(hex.slice(5, 7), 16);
        return 'rgba(' + r + ',' + g + ',' + b + ',' + a + ')';
    }

    // ---- orchestration -----------------------------------------
    function rebuild() {          // params changed: visual + data
        updateVisual();
        applyIsolation();         // before resample: it scopes the probe
        resample();
        drawPlot();
    }

    function refresh() {          // displayed field changed: data only
        syncModelUnits();
        if (!cut) return;
        resample();
        drawPlot();
    }

    function onModelCleared() {   // model disposed / replaced
        cut = null;
        samples = null;
        isolated = null;          // fresh geometry draws fully visible
        lastModel = null;         // next model re-syncs the units dropdown
        removeVisual();
        setArmed(false);
        drawPlot();
    }

    syncModelUnits();
    syncColorSwatch();
    drawPlot();

    return {
        wantsPick: wantsPick,
        placeCenter: placeCenter,
        refresh: refresh,
        updateScale: updateScale,
        onModelCleared: onModelCleared
    };
})();
