// ================================================================
// beamGeometry.js
// Builds the solid (extruded cross-section) mesh for a beam domain.
//
// Each beam: section outline in local (y,z) -> ring at end A and ring
// at end B -> side quads + end caps. Vertices are duplicated PER
// ELEMENT (non-indexed) so render triangle -> element is a table
// lookup and every vertex carries its own attributes:
//   position  vec3   undeformed world xyz
//   normal    vec3   flat face normal (mild shading so solids read as 3D)
//   beamT     float  0 at end A, 1 at end B
//   endVals   vec2   (valueA, valueB) of the selected component, written
//                    identically on every vertex of the element
//                    (filled by attributeUpdaters.updateBeamEndVals)
//   dispVec   vec3   this vertex's end displacement (A or B)
//   elemVis   float  1 = drawn
//
// Local frame: X = n0 -> n1, Y = BPRP local-y re-orthogonalised
// against X, Z = X x Y. Section offsets (BPRP idx 3..6) shift each
// end's ring in (Y, Z). Hollow shapes (BOX, PIPE) render their OUTER
// outline only for now.
// ================================================================

var FEABeamGeometry = (function () {

    var PIPE_SEGS = 24;           // adaptive: see segsFor()

    // Fewer facets for big models so 150k pipes stay renderable.
    function segsFor(nElem) { return nElem > 60000 ? 6 : nElem > 20000 ? 8 : nElem > 5000 ? 12 : 24; }

    // Section outline as a CCW list of [y, z] points centred on the
    // section's bounding-box centre (centroid for symmetric shapes).
    function outline(sec) {
        var p = sec ? sec.params : null;
        switch (sec ? sec.type : '') {
            case 'RECT': return rect(p.b, p.h);
            case 'BOX':  return rect(p.b, p.h);
            case 'PIPE': return circle(p.od / 2);
            case 'I':    return iShape(p.d, p.bfTop, p.tfTop, p.bfBot, p.tfBot, p.tw);
            case 'C':    return cShape(p.d, p.bf, p.tf, p.tw);
            case 'T':    return tShape(p.d, p.bf, p.tf, p.tw);
            case 'L':    return lShape(p.b, p.h, p.t);
            case 'POLY': return sec.points && sec.points.length >= 3 ? sec.points.slice() : rect(1, 1);
            default:     return rect(1, 1);
        }
    }
    function rect(b, h) {
        return [[-b / 2, -h / 2], [b / 2, -h / 2], [b / 2, h / 2], [-b / 2, h / 2]];
    }
    function circle(r) {
        var pts = [];
        for (var i = 0; i < PIPE_SEGS; i++) {
            var a = 2 * Math.PI * i / PIPE_SEGS;   // PIPE_SEGS set per build
            pts.push([r * Math.cos(a), r * Math.sin(a)]);
        }
        return pts;
    }
    function iShape(d, bft, tft, bfb, tfb, tw) {
        var h2 = d / 2;
        return [
            [-bfb / 2, -h2], [bfb / 2, -h2], [bfb / 2, -h2 + tfb], [tw / 2, -h2 + tfb],
            [tw / 2, h2 - tft], [bft / 2, h2 - tft], [bft / 2, h2], [-bft / 2, h2],
            [-bft / 2, h2 - tft], [-tw / 2, h2 - tft], [-tw / 2, -h2 + tfb], [-bfb / 2, -h2 + tfb]
        ];
    }
    function cShape(d, bf, tf, tw) {
        var h2 = d / 2, y0 = -bf / 2;
        return [
            [y0, -h2], [y0 + bf, -h2], [y0 + bf, -h2 + tf], [y0 + tw, -h2 + tf],
            [y0 + tw, h2 - tf], [y0 + bf, h2 - tf], [y0 + bf, h2], [y0, h2]
        ];
    }
    function tShape(d, bf, tf, tw) {
        var h2 = d / 2;
        return [
            [-tw / 2, -h2], [tw / 2, -h2], [tw / 2, h2 - tf], [bf / 2, h2 - tf],
            [bf / 2, h2], [-bf / 2, h2], [-bf / 2, h2 - tf], [-tw / 2, h2 - tf]
        ];
    }
    function lShape(b, h, t) {
        var y0 = -b / 2, z0 = -h / 2;
        return [[y0, z0], [y0 + b, z0], [y0 + b, z0 + t], [y0 + t, z0 + t], [y0 + t, z0 + h], [y0, z0 + h]];
    }

    function capTriangles(pts) {
        var v2 = pts.map(function (p) { return new THREE.Vector2(p[0], p[1]); });
        try {
            return THREE.ShapeUtils.triangulateShape(v2, []);
        } catch (e) {
            var out = [];
            for (var i = 1; i + 1 < pts.length; i++) out.push([0, i, i + 1]);
            return out;
        }
    }

    function build(view) {
        var dom = view.domain;
        var nElem = dom.nElem;
        var REC = view.elemRecordU32;
        var elems = view.elems;
        var nodes = view.nodes;
        var props = view.beamProps;
        var taper = view.beamTaper || null;
        var sections = view.sections || [];
        var BP = FEAv4.BPRP_F32;
        PIPE_SEGS = segsFor(nElem);

        // Pre-triangulate one outline per section index.
        var secCache = {};
        function secGeom(si) {
            if (!secCache[si]) {
                var pts = outline(sections[si]);
                secCache[si] = { pts: pts, caps: capTriangles(pts) };
            }
            return secCache[si];
        }

        // First pass: count.
        var vertStart = new Int32Array(nElem), vertCount = new Int32Array(nElem);
        var sectionOf = new Int32Array(nElem);
        var totalVerts = 0;
        for (var e = 0; e < nElem; e++) {
            var si = elems[e * REC + 3];
            if (!(si < sections.length)) si = -1;
            sectionOf[e] = si;
            var sg = secGeom(si);
            var m = sg.pts.length;
            var nv = m * 6 + sg.caps.length * 3 * 2;
            vertStart[e] = totalVerts;
            vertCount[e] = nv;
            totalVerts += nv;
        }
        var totalTris = totalVerts / 3;

        var positions = new Float32Array(totalVerts * 3);
        var normals   = new Float32Array(totalVerts * 3);
        var beamT     = new Float32Array(totalVerts);
        var endVals   = new Float32Array(totalVerts * 2);
        var dispVecs  = new Float32Array(totalVerts * 3);
        var elemVis   = new Float32Array(totalVerts); elemVis.fill(1);
        var catIdx    = new Float32Array(totalVerts); catIdx.fill(-1);
        var triToElem = new Int32Array(totalTris);
        var axisA = new Float32Array(nElem * 3), axisB = new Float32Array(nElem * 3);
        var frames = new Float32Array(nElem * 9);   // X,Y,Z unit vectors per element

        var X = new THREE.Vector3(), Y = new THREE.Vector3(), Z = new THREE.Vector3();
        var A = new THREE.Vector3(), B = new THREE.Vector3(), tmp = new THREE.Vector3();
        var ringA = [], ringB = [];
        var vptr = 0, tptr = 0;

        function emit(p, n, t) {
            positions[vptr * 3] = p.x; positions[vptr * 3 + 1] = p.y; positions[vptr * 3 + 2] = p.z;
            normals[vptr * 3] = n.x;   normals[vptr * 3 + 1] = n.y;   normals[vptr * 3 + 2] = n.z;
            beamT[vptr] = t;
            vptr++;
        }
        function tri(p0, p1, p2, t0, t1, t2, e) {
            var n = tmp.copy(p1).sub(p0).cross(new THREE.Vector3().copy(p2).sub(p0));
            if (n.lengthSq() > 1e-20) n.normalize(); else n.set(0, 0, 1);
            var nn = n.clone();
            emit(p0, nn, t0); emit(p1, nn, t1); emit(p2, nn, t2);
            triToElem[tptr++] = e;
        }

        for (var e2 = 0; e2 < nElem; e2++) {
            var n0 = elems[e2 * REC + 1], n1 = elems[e2 * REC + 2];
            A.set(nodes[n0 * 3], nodes[n0 * 3 + 1], nodes[n0 * 3 + 2]);
            B.set(nodes[n1 * 3], nodes[n1 * 3 + 1], nodes[n1 * 3 + 2]);
            X.copy(B).sub(A);
            if (X.lengthSq() < 1e-20) X.set(1, 0, 0); else X.normalize();
            if (props) Y.set(props[e2 * BP], props[e2 * BP + 1], props[e2 * BP + 2]);
            else Y.set(0, 0, 1);
            // re-orthogonalise Y against X; fall back if parallel
            Y.sub(tmp.copy(X).multiplyScalar(Y.dot(X)));
            if (Y.lengthSq() < 1e-8) {
                Y.set(0, 0, 1).sub(tmp.copy(X).multiplyScalar(X.z));
                if (Y.lengthSq() < 1e-8) Y.set(0, 1, 0).sub(tmp.copy(X).multiplyScalar(X.y));
            }
            Y.normalize();
            Z.copy(X).cross(Y).normalize();
            frames.set([X.x, X.y, X.z, Y.x, Y.y, Y.z, Z.x, Z.y, Z.z], e2 * 9);

            var oAy = props ? props[e2 * BP + 3] : 0, oAz = props ? props[e2 * BP + 4] : 0;
            var oBy = props ? props[e2 * BP + 5] : 0, oBz = props ? props[e2 * BP + 6] : 0;
            var cA = A.clone().addScaledVector(Y, oAy).addScaledVector(Z, oAz);
            var cB = B.clone().addScaledVector(Y, oBy).addScaledVector(Z, oBz);
            axisA.set([cA.x, cA.y, cA.z], e2 * 3);
            axisB.set([cB.x, cB.y, cB.z], e2 * 3);

            var sg = secGeom(sectionOf[e2]);
            var pts = sg.pts, m = pts.length;
            // Taper (BTAP): scale the outline per end; 1/1 when the block is absent.
            var sA = taper ? taper[e2 * 2] : 1, sB = taper ? taper[e2 * 2 + 1] : 1;
            if (!(sA > 0)) sA = 1; if (!(sB > 0)) sB = 1;
            ringA.length = 0; ringB.length = 0;
            for (var i = 0; i < m; i++) {
                ringA.push(cA.clone().addScaledVector(Y, pts[i][0] * sA).addScaledVector(Z, pts[i][1] * sA));
                ringB.push(cB.clone().addScaledVector(Y, pts[i][0] * sB).addScaledVector(Z, pts[i][1] * sB));
            }
            for (var k = 0; k < m; k++) {
                var k2 = (k + 1) % m;
                tri(ringA[k], ringB[k], ringB[k2], 0, 1, 1, e2);
                tri(ringA[k], ringB[k2], ringA[k2], 0, 1, 0, e2);
            }
            for (var c = 0; c < sg.caps.length; c++) {
                var f = sg.caps[c];
                tri(ringA[f[0]], ringA[f[2]], ringA[f[1]], 0, 0, 0, e2);   // A cap faces -X
                tri(ringB[f[0]], ringB[f[1]], ringB[f[2]], 1, 1, 1, e2);   // B cap faces +X
            }
        }

        var geo = new THREE.BufferGeometry();
        geo.setAttribute('position', new THREE.BufferAttribute(positions, 3));
        geo.setAttribute('normal',   new THREE.BufferAttribute(normals, 3));
        geo.setAttribute('beamT',    new THREE.BufferAttribute(beamT, 1));
        geo.setAttribute('endVals',  new THREE.BufferAttribute(endVals, 2));
        geo.setAttribute('dispVec',  new THREE.BufferAttribute(dispVecs, 3));
        geo.setAttribute('elemVis',  new THREE.BufferAttribute(elemVis, 1));
        geo.setAttribute('catIdx',   new THREE.BufferAttribute(catIdx, 1));
        geo.computeBoundingBox();
        geo.computeBoundingSphere();

        return {
            geometry: geo,
            nElem: nElem,
            vertStart: vertStart,
            vertCount: vertCount,
            triToElem: triToElem,
            sectionOf: sectionOf,
            axisA: axisA,
            axisB: axisB,
            frames: frames,
            totalVerts: totalVerts
        };
    }

    // Parameter t in [0,1] of the point on the beam axis closest to p.
    function axisParam(build, e, p) {
        var ax = build.axisA[e * 3], ay = build.axisA[e * 3 + 1], az = build.axisA[e * 3 + 2];
        var dx = build.axisB[e * 3] - ax, dy = build.axisB[e * 3 + 1] - ay, dz = build.axisB[e * 3 + 2] - az;
        var L2 = dx * dx + dy * dy + dz * dz;
        if (L2 < 1e-20) return 0;
        var t = ((p.x - ax) * dx + (p.y - ay) * dy + (p.z - az) * dz) / L2;
        return Math.max(0, Math.min(1, t));
    }

    return { build: build, outline: outline, axisParam: axisParam };
})();
