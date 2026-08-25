// ================================================================
// geometryBuilder.js
// Builds the duplicate-vertex (non-indexed) colored mesh geometry.
//
// Vertices are duplicated PER ELEMENT so corners are never shared and
// each can carry independent field data. Quad -> 2 triangles
// (0,1,2),(0,2,3). Triangle -> 1 triangle.
//
// Per-vertex attributes:
//   position   vec3  world xyz, static (deformation is GPU-side via dispVec)
//   quadUV     vec2  corner UV: c0=(0,0) c1=(1,0) c2=(1,1) c3=(0,1)
//   cornerVals vec4  the 4 corner values for the selected component,
//                    written IDENTICALLY on every vertex of an element
//                    (filled later by attributeUpdaters.js).
//   dispVec    vec3  THIS vertex's own corner displacement (unlike
//                    cornerVals it differs per vertex; zero until
//                    attributeUpdaters fills it). Vertex shader adds
//                    dispScale * dispVec to position.
// ================================================================

var FEAGeometry = (function () {

    // Corner -> quad-local UV.
    var CORNER_U = [0, 1, 1, 0];
    var CORNER_V = [0, 0, 1, 1];
    // Triangulation: which corners form each render triangle.
    var QUAD_TRIS = [[0, 1, 2], [0, 2, 3]];
    var TRI_TRIS  = [[0, 1, 2]];

    function vertsForElement(ncount) { return ncount === 4 ? 6 : 3; }
    function trisForElement(ncount)  { return ncount === 4 ? 2 : 1; }

    function build(model) {
        var h = model.header;
        var nodes = model.nodes;
        var elems = model.elems;
        var nElem = h.nElements;
        var REC = model.elemRecordU32 || 6;   // u32 per element record (v4: 6)

        // First pass: count vertices/triangles, record per-element layout.
        var elemNCount = new Uint8Array(nElem);
        var elemCorners = new Int32Array(nElem * 4);   // node indices, -1 if absent
        var totalVerts = 0, totalTris = 0;
        for (var e = 0; e < nElem; e++) {
            var nc = elems[e * REC];
            if (nc !== 3 && nc !== 4) nc = nc >= 4 ? 4 : 3;   // defensive
            elemNCount[e] = nc;
            for (var k = 0; k < 4; k++) {
                elemCorners[e * 4 + k] = (k < nc) ? elems[e * REC + 1 + k] : -1;
            }
            totalVerts += vertsForElement(nc);
            totalTris  += trisForElement(nc);
        }

        // Second pass: fill attribute arrays.
        var positions  = new Float32Array(totalVerts * 3);
        var quadUV     = new Float32Array(totalVerts * 2);
        var cornerVals = new Float32Array(totalVerts * 4);   // filled by updater
        var dispVecs   = new Float32Array(totalVerts * 3);   // filled by updater
        var elemVis    = new Float32Array(totalVerts);       // 1 = drawn (see shaders)
        elemVis.fill(1);
        var catIdx     = new Float32Array(totalVerts);       // group category, -1 = none
        catIdx.fill(-1);
        var triToElem  = new Int32Array(totalTris);          // render triangle -> element

        var vptr = 0, tptr = 0;
        for (var e2 = 0; e2 < nElem; e2++) {
            var nc2 = elemNCount[e2];
            var tris = nc2 === 4 ? QUAD_TRIS : TRI_TRIS;
            for (var t = 0; t < tris.length; t++) {
                triToElem[tptr++] = e2;
                for (var c = 0; c < 3; c++) {
                    var corner = tris[t][c];
                    var nodeIdx = elemCorners[e2 * 4 + corner];
                    positions[vptr * 3]     = nodes[nodeIdx * 3];
                    positions[vptr * 3 + 1] = nodes[nodeIdx * 3 + 1];
                    positions[vptr * 3 + 2] = nodes[nodeIdx * 3 + 2];
                    quadUV[vptr * 2]     = CORNER_U[corner];
                    quadUV[vptr * 2 + 1] = CORNER_V[corner];
                    vptr++;
                }
            }
        }

        var geo = new THREE.BufferGeometry();
        geo.setAttribute('position',   new THREE.BufferAttribute(positions, 3));
        geo.setAttribute('quadUV',     new THREE.BufferAttribute(quadUV, 2));
        geo.setAttribute('cornerVals', new THREE.BufferAttribute(cornerVals, 4));
        geo.setAttribute('dispVec',    new THREE.BufferAttribute(dispVecs, 3));
        geo.setAttribute('elemVis',    new THREE.BufferAttribute(elemVis, 1));
        geo.setAttribute('catIdx',     new THREE.BufferAttribute(catIdx, 1));
        if (totalVerts > 0) {
            geo.computeBoundingBox();
            geo.computeBoundingSphere();
        } else {
            geo.boundingBox = new THREE.Box3();          // empty
            geo.boundingSphere = new THREE.Sphere(new THREE.Vector3(), 0);
        }

        // Element-perimeter edges (for the optional wireframe overlay).
        var edges = buildEdgePositions(nElem, elemNCount, elemCorners, nodes);

        var elemNormals = computeElemNormals(nElem, elemNCount, elemCorners, nodes);
        var normalGroups = buildNormalGroups(nElem, elemNCount, elemCorners, elemNormals, h.nNodes);

        return {
            geometry: geo,
            triToElem: triToElem,        // Int32Array[totalTris]
            elemNCount: elemNCount,      // Uint8Array[nElem]
            elemCorners: elemCorners,    // Int32Array[nElem*4]
            elemNormals: elemNormals,    // Float32Array[nElem*3]
            normalGroups: normalGroups,  // per-node groups of (elem,corner) with similar normals
            totalVerts: totalVerts,
            totalTris: totalTris,
            edgePositions: edges.positions,
            edgeCorners: edges.corners     // Int32Array[2 per edge vertex] e*4+k
        };
    }

    // Element perimeter line segments (each edge as a vertex pair).
    // Also records, for each edge VERTEX, the (element, corner) it came
    // from (packed e*4+k) so the displacement updater can deform edges
    // in lockstep with the mesh.
    function buildEdgePositions(nElem, elemNCount, elemCorners, nodes) {
        var totalEdges = 0;
        for (var e = 0; e < nElem; e++) totalEdges += elemNCount[e];
        var pos = new Float32Array(totalEdges * 2 * 3);
        var cor = new Int32Array(totalEdges * 2);
        var p = 0, q = 0;
        for (var e2 = 0; e2 < nElem; e2++) {
            var nc = elemNCount[e2];
            for (var k = 0; k < nc; k++) {
                var k2 = (k + 1) % nc;
                var a = elemCorners[e2 * 4 + k];
                var b = elemCorners[e2 * 4 + k2];
                pos[p++] = nodes[a * 3];     pos[p++] = nodes[a * 3 + 1]; pos[p++] = nodes[a * 3 + 2];
                pos[p++] = nodes[b * 3];     pos[p++] = nodes[b * 3 + 1]; pos[p++] = nodes[b * 3 + 2];
                cor[q++] = e2 * 4 + k;
                cor[q++] = e2 * 4 + k2;
            }
        }
        return { positions: pos, corners: cor };
    }

    // Unit normal per element from cross product of first two edges.
    function computeElemNormals(nElem, elemNCount, elemCorners, nodes) {
        var out = new Float32Array(nElem * 3);
        for (var e = 0; e < nElem; e++) {
            var i0 = elemCorners[e * 4], i1 = elemCorners[e * 4 + 1], i2 = elemCorners[e * 4 + 2];
            var ax = nodes[i1 * 3]     - nodes[i0 * 3],
                ay = nodes[i1 * 3 + 1] - nodes[i0 * 3 + 1],
                az = nodes[i1 * 3 + 2] - nodes[i0 * 3 + 2];
            var bx = nodes[i2 * 3]     - nodes[i0 * 3],
                by = nodes[i2 * 3 + 1] - nodes[i0 * 3 + 1],
                bz = nodes[i2 * 3 + 2] - nodes[i0 * 3 + 2];
            var nx = ay * bz - az * by, ny = az * bx - ax * bz, nz = ax * by - ay * bx;
            var len = Math.sqrt(nx * nx + ny * ny + nz * nz);
            if (len > 1e-12) { nx /= len; ny /= len; nz /= len; }
            out[e * 3] = nx; out[e * 3 + 1] = ny; out[e * 3 + 2] = nz;
        }
        return out;
    }

    // For each node, group the (element, corner) pairs that touch it by
    // similar element normal. Two normals are "similar" if their dot
    // product exceeds NORMAL_DOT_THRESHOLD (≈15°). Each group will be
    // averaged independently during smoothing, so a wall and slab sharing
    // a node won't bleed into each other.
    //
    // Returns an array of length nNodes. Each entry is an array of groups,
    // where each group is an array of {e, k} objects (element index, corner).
    var NORMAL_DOT_THRESHOLD = 0.966;   // cos(15°)

    function buildNormalGroups(nElem, elemNCount, elemCorners, elemNormals, nNodes) {
        var nodeEntries = new Array(nNodes);
        for (var n = 0; n < nNodes; n++) nodeEntries[n] = [];

        for (var e = 0; e < nElem; e++) {
            var nc = elemNCount[e];
            for (var k = 0; k < nc; k++) {
                var ni = elemCorners[e * 4 + k];
                var groups = nodeEntries[ni];
                var ex = elemNormals[e * 3], ey = elemNormals[e * 3 + 1], ez = elemNormals[e * 3 + 2];
                var placed = false;
                for (var g = 0; g < groups.length; g++) {
                    var rep = groups[g][0];
                    var rx = elemNormals[rep.e * 3], ry = elemNormals[rep.e * 3 + 1], rz = elemNormals[rep.e * 3 + 2];
                    var dot = ex * rx + ey * ry + ez * rz;
                    if (dot > NORMAL_DOT_THRESHOLD || dot < -NORMAL_DOT_THRESHOLD) {
                        groups[g].push({ e: e, k: k });
                        placed = true;
                        break;
                    }
                }
                if (!placed) groups.push([{ e: e, k: k }]);
            }
        }
        return nodeEntries;
    }

    return {
        build: build,
        QUAD_TRIS: QUAD_TRIS,
        TRI_TRIS: TRI_TRIS,
        vertsForElement: vertsForElement
    };
})();
