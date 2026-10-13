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
//   d.setEdges(on)            wireframe: shell element edges, beam section outlines at both ends + lengthwise
//                             corner lines; built on first use; follows elemVis
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
            d._off = d._off || {}; d._off[fam] = mask;
            if (d.edges && d.edges[fam]) edgeVis(d, fam);
        };
        // Wireframe, per family, drawn as lines that hide with their element. Built on first use only (a large
        // mesh pays nothing until asked). Shells: the element perimeters (geometryBuilder edgePositions).
        // Beams: each end's section outline + lengthwise lines (every corner, or 4 on a faceted round).
        d.setEdges = function (on) {
            d.edges = d.edges || {};
            ['shell', 'beam'].forEach(function (fam) {
                if (!d[fam]) return;
                if (on && !d.edges[fam]) {
                    var e = fam === 'shell' ? { pos: d.shell.build.edgePositions, elem: null } : beamEdges(d.beam.build);
                    var geo = new THREE.BufferGeometry();
                    geo.setAttribute('position', new THREE.BufferAttribute(e.pos, 3));
                    geo.setAttribute('elemVis', new THREE.BufferAttribute(new Float32Array(e.pos.length / 3), 1));
                    var mat = new THREE.ShaderMaterial({
                        uniforms: { uColor: { value: new THREE.Vector3(0, 0, 0) }, uOpacity: { value: 0.45 } },
                        vertexShader: EDGE_VERTEX, fragmentShader: FEAShaders.edgeFragment, transparent: true, depthWrite: false });
                    var ls = new THREE.LineSegments(geo, mat);
                    ls.renderOrder = 3; ls.userData.elem = e.elem;
                    d.edges[fam] = ls;
                    d.root.add(ls);
                    edgeVis(d, fam);
                }
                if (d.edges[fam]) d.edges[fam].visible = !!on;
            });
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
            if (d.edges) Object.keys(d.edges).forEach(function (k) { d.edges[k].geometry.dispose(); d.edges[k].material.dispose(); });
        };
        return d;
    }

    function parts(d) { return [d.shell, d.beam].filter(Boolean); }
    // edges without displacement (overlays draw no results)
    var EDGE_VERTEX = [
        'attribute float elemVis;',
        'varying float vVis;',
        'void main() {',
        '  vVis = elemVis;',
        '  gl_Position = projectionMatrix * modelViewMatrix * vec4(position, 1.0);',
        '}'
    ].join('\n');
    // edge vertex -> its element (shells: edgeCorners holds e*4+k per edge vertex; beams: userData.elem), hidden with it
    function edgeVis(d, fam) {
        var ls = d.edges[fam], attr = ls.geometry.getAttribute('elemVis'), a = attr.array, off = d._off ? d._off[fam] : null;
        var cor = fam === 'shell' ? d.shell.build.edgeCorners : null, el = ls.userData.elem;
        for (var i = 0; i < a.length; i++) { var e = cor ? cor[i] >> 2 : el[i]; a[i] = off && off[e] ? 0 : 1; }
        attr.needsUpdate = true;
    }
    // Beam outline segments from the extruded mesh (beamGeometry.js): per element, the side quads come first,
    // 6 vertices per outline point k: ringA[k], ringB[k], ringB[k+1], ringA[k], ringB[k+1], ringA[k+1].
    function beamEdges(b) {
        var P = b.geometry.getAttribute('position').array, pos = [], elem = [];
        function seg(v0, v1, e) {
            pos.push(P[v0 * 3], P[v0 * 3 + 1], P[v0 * 3 + 2], P[v1 * 3], P[v1 * 3 + 1], P[v1 * 3 + 2]);
            elem.push(e, e);
        }
        for (var e = 0; e < b.nElem; e++) {
            var vs = b.vertStart[e], m = b.outlineN[e];
            if (!(m >= 2) || 6 * m > b.vertCount[e]) continue;
            var step = m > 8 ? Math.max(1, Math.floor(m / 4)) : 1;
            for (var k = 0; k < m; k++) {
                var v = vs + 6 * k;
                seg(v, v + 5, e);              // end A outline: ringA[k] - ringA[k+1]
                seg(v + 1, v + 2, e);          // end B outline: ringB[k] - ringB[k+1]
                if (k % step === 0) seg(v, v + 1, e);   // lengthwise: ringA[k] - ringB[k]
            }
        }
        return { pos: new Float32Array(pos), elem: new Int32Array(elem) };
    }
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

    return { create: create, beamEdges: beamEdges };
})();
