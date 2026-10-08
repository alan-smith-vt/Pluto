// ================================================================
// modelMesh.js  --  one file drawn as reference geometry: its shells AND beams
// under one THREE.Group (the placement), with group-capable materials.
// Used by overlays.js; knows nothing about the rail, the Groups tab or globals.
//
//   var d = FEAModelMesh.create(unified)     // nodes already in the local frame
//   d.root                    THREE.Group to place (position / rotation / scale)
//   d.shell / d.beam          { view, build, material, mesh } or null
//   d.paint(fam, cat)         per-element group index -> catIdx attribute
//   d.setGroupLook(on, palette, count)
//   d.setElemOff(fam, mask)   Uint8Array (1 = hidden) or null -> elemVis
//   d.setVisible(on) / d.setXray(on) / d.setColor('#rrggbb')
//   d.pick(raycaster)         { fam, elem, point, distance } or null
//   d.label(fam, e)           the element label from the file
//   d.dispose()
// ================================================================

var FEAModelMesh = (function () {

    function hexVec(h) {
        var m = /^#?([0-9a-f]{6})$/i.exec(String(h || ''));
        var v = m ? parseInt(m[1], 16) : 0x9ea3ad;
        return new THREE.Vector3(((v >> 16) & 255) / 255, ((v >> 8) & 255) / 255, (v & 255) / 255);
    }

    // Uniforms both shaders read; field uniforms are inert (overlays draw no results).
    function uniforms(color) {
        var pal = FEAShaders.makePaletteTexture([[200, 200, 200]]);
        return {
            colormap: { value: pal }, vMin: { value: 0 }, vMax: { value: 1 },
            alarmThreshold: { value: 0 }, alarmColor: { value: new THREE.Vector3(1, 0, 1) },
            uAbs: { value: 0 }, uCategorical: { value: 0 }, dispScale: { value: 0 },
            uNeutral: { value: 1 }, neutralColor: { value: hexVec(color) },
            uGroupMode: { value: 0 }, groupPalette: { value: pal }, uGroupCount: { value: 1 },
            uXray: { value: 0 }
        };
    }

    function create(unified, color) {
        var d = { root: new THREE.Group(), shell: null, beam: null };
        var sv = PlutoFormat.shellView(unified);
        if (sv && sv.header.nElements > 0) {
            var sb = FEAGeometry.build(sv);
            var sm = new THREE.ShaderMaterial({ uniforms: uniforms(color), vertexShader: FEAShaders.vertex,
                                                fragmentShader: FEAShaders.fragment, side: THREE.DoubleSide });
            sm.polygonOffset = true; sm.polygonOffsetFactor = 2; sm.polygonOffsetUnits = 4;
            d.shell = { view: sv, build: sb, material: sm, mesh: new THREE.Mesh(sb.geometry, sm) };
            d.root.add(d.shell.mesh);
        }
        var bv = PlutoFormat.beamView(unified);
        if (bv && bv.header.nElements > 0) {
            var bb = FEABeamGeometry.build(bv);
            var bm = new THREE.ShaderMaterial({ uniforms: uniforms(color), vertexShader: FEAShaders.beamVertex,
                                                fragmentShader: FEAShaders.beamFragment, side: THREE.DoubleSide });
            d.beam = { view: bv, build: bb, material: bm, mesh: new THREE.Mesh(bb.geometry, bm) };
            d.root.add(d.beam.mesh);
        }
        d.root.renderOrder = 2;
        parts(d).forEach(function (p) { p.mesh.renderOrder = 2; });

        d.paint = function (fam, cat) {
            var p = d[fam]; if (!p) return;
            if (fam === 'shell') FEAAttributes.updateCatIdx(p.build.geometry, p.build.elemNCount, null, cat);
            else FEAAttributes.updateCatIdx(p.build.geometry, null, p.build, cat);
        };
        d.setGroupLook = function (on, palette, count) {
            parts(d).forEach(function (p) {
                var u = p.material.uniforms;
                u.uGroupMode.value = on ? 1 : 0;
                if (on && palette) { u.groupPalette.value = palette; u.uGroupCount.value = count; }
            });
        };
        d.setElemOff = function (fam, mask) {
            var p = d[fam]; if (!p) return;
            var attr = p.build.geometry.getAttribute('elemVis'), a = attr.array;
            var n = p.view.header.nElements;
            for (var e = 0; e < n; e++) { var r = vertRange(fam, p.build, e); a.fill(mask && mask[e] ? 0 : 1, r[0], r[1]); }
            attr.needsUpdate = true;
        };
        d.setVisible = function (on) { d.root.visible = on; };
        d.setXray = function (on) { parts(d).forEach(function (p) { FEAModels.xrayMaterial(p.material, on); }); };
        d.setColor = function (hex) { var c = hexVec(hex); parts(d).forEach(function (p) { p.material.uniforms.neutralColor.value = c; }); };
        d.pick = function (raycaster) {
            var best = null;
            parts(d).forEach(function (p) {
                if (!d.root.visible) return;
                var hits = raycaster.intersectObject(p.mesh);
                for (var i = 0; i < hits.length; i++) {
                    var fi = hits[i].faceIndex;
                    if (fi == null) continue;
                    var e = p.build.triToElem[fi], fam = p === d.shell ? 'shell' : 'beam';
                    if (!elemShown(fam, p, e)) continue;     // eye-hidden members are not pickable
                    if (!best || hits[i].distance < best.distance) best = { fam: fam, elem: e, point: hits[i].point, distance: hits[i].distance };
                    break;
                }
            });
            return best;
        };
        d.label = function (fam, e) { var v = d[fam] && d[fam].view; return v && v.labels ? (v.labels.get(e) || '') : ''; };
        d.elemId = function (fam, e) { var v = d[fam] && d[fam].view; return v ? v.elemIds[e] : e; };
        d.dispose = function () {
            parts(d).forEach(function (p) { p.build.geometry.dispose(); p.material.dispose(); });
        };
        return d;
    }

    function parts(d) { return [d.shell, d.beam].filter(Boolean); }
    // [first, end) render vertices of element e. Beam builds carry vertStart / vertCount; shell builds
    // lay elements out in order, 6 vertices per quad and 3 per triangle (geometryBuilder.js).
    function vertRange(fam, build, e) {
        if (fam === 'beam') return [build.vertStart[e], build.vertStart[e] + build.vertCount[e]];
        if (!build._vs) {
            var vs = new Int32Array(build.elemNCount.length), acc = 0;
            for (var i = 0; i < vs.length; i++) { vs[i] = acc; acc += build.elemNCount[i] === 4 ? 6 : 3; }
            build._vs = vs;
        }
        return [build._vs[e], build._vs[e] + (build.elemNCount[e] === 4 ? 6 : 3)];
    }
    function elemShown(fam, p, e) {
        var attr = p.build.geometry.getAttribute('elemVis');
        return !attr || attr.array[vertRange(fam, p.build, e)[0]] > 0.5;
    }

    return { create: create };
})();
