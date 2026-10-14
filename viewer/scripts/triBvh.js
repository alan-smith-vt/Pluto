// ================================================================
// triBvh.js  --  bounding-volume hierarchy over a non-indexed triangle soup, for picking.
// three.js raycasts test every triangle; an overlay mesh of a few million triangles made every
// hover stall. Built once per mesh (first pick), ray tests then touch only the boxes the ray crosses.
//
//   var bvh = FEATriBvh.build(positions)     // Float32Array, 9 floats per triangle (local frame)
//   var hit = FEATriBvh.raycast(bvh, origin, dir, skip)
//             origin / dir: [x, y, z] (local frame, dir need not be unit); skip(tri) -> true to ignore
//             -> { tri, t } (t along dir) or null
// ================================================================

var FEATriBvh = (function () {
    var LEAF = 8;

    function build(P) {
        var n = (P.length / 9) | 0;
        var tri = new Uint32Array(n), cen = new Float32Array(n * 3);
        for (var i = 0; i < n; i++) {
            tri[i] = i;
            var o = i * 9;
            cen[i * 3] = (P[o] + P[o + 3] + P[o + 6]) / 3;
            cen[i * 3 + 1] = (P[o + 1] + P[o + 4] + P[o + 7]) / 3;
            cen[i * 3 + 2] = (P[o + 2] + P[o + 5] + P[o + 8]) / 3;
        }
        var cap = Math.max(16, ((2 * n / LEAF) | 0) + 16);
        var box = new Float32Array(cap * 6), a = new Int32Array(cap), b = new Int32Array(cap), leaf = new Uint8Array(cap);
        var count = 0;
        function grow() {
            cap *= 2;
            var nb = new Float32Array(cap * 6); nb.set(box); box = nb;
            var na = new Int32Array(cap); na.set(a); a = na;
            var nbb = new Int32Array(cap); nbb.set(b); b = nbb;
            var nl = new Uint8Array(cap); nl.set(leaf); leaf = nl;
        }
        // node: leaf -> a = first, b = end (into tri); inner -> a = left child, b = right child
        var stack = [[0, n, -1, 0]];   // start, end, parent, side (0 left / 1 right)
        if (n === 0) return { tri: tri, box: box, a: a, b: b, leaf: leaf, count: 0, P: P };
        while (stack.length) {
            var s = stack.pop(), st = s[0], en = s[1];
            if (count >= cap) grow();
            var id = count++;
            if (s[2] >= 0) { if (s[3] === 0) a[s[2]] = id; else b[s[2]] = id; }
            var lo0 = Infinity, lo1 = Infinity, lo2 = Infinity, hi0 = -Infinity, hi1 = -Infinity, hi2 = -Infinity;
            var c0 = Infinity, c1 = Infinity, c2 = Infinity, d0 = -Infinity, d1 = -Infinity, d2 = -Infinity;
            for (var k = st; k < en; k++) {
                var t = tri[k], o9 = t * 9;
                for (var v = 0; v < 9; v += 3) {
                    var x = P[o9 + v], y = P[o9 + v + 1], z = P[o9 + v + 2];
                    if (x < lo0) lo0 = x; if (x > hi0) hi0 = x;
                    if (y < lo1) lo1 = y; if (y > hi1) hi1 = y;
                    if (z < lo2) lo2 = z; if (z > hi2) hi2 = z;
                }
                var cx = cen[t * 3], cy = cen[t * 3 + 1], cz = cen[t * 3 + 2];
                if (cx < c0) c0 = cx; if (cx > d0) d0 = cx;
                if (cy < c1) c1 = cy; if (cy > d1) d1 = cy;
                if (cz < c2) c2 = cz; if (cz > d2) d2 = cz;
            }
            var bo = id * 6;
            box[bo] = lo0; box[bo + 1] = lo1; box[bo + 2] = lo2; box[bo + 3] = hi0; box[bo + 4] = hi1; box[bo + 5] = hi2;
            if (en - st <= LEAF) { leaf[id] = 1; a[id] = st; b[id] = en; continue; }
            // split the centroid box at the middle of its longest side; one side empty -> halve by count
            var ax = 0, ext = d0 - c0;
            if (d1 - c1 > ext) { ax = 1; ext = d1 - c1; }
            if (d2 - c2 > ext) { ax = 2; ext = d2 - c2; }
            var mid = st + ((en - st) >> 1);
            if (ext > 0) {
                var cut = ax === 0 ? (c0 + d0) / 2 : ax === 1 ? (c1 + d1) / 2 : (c2 + d2) / 2;
                var i0 = st, i1 = en - 1;
                while (i0 <= i1) {
                    if (cen[tri[i0] * 3 + ax] < cut) i0++;
                    else { var sw = tri[i0]; tri[i0] = tri[i1]; tri[i1] = sw; i1--; }
                }
                if (i0 > st && i0 < en) mid = i0;
            }
            leaf[id] = 0;
            stack.push([mid, en, id, 1]);
            stack.push([st, mid, id, 0]);
        }
        return { tri: tri, box: box, a: a, b: b, leaf: leaf, count: count, P: P };
    }

    function raycast(bvh, O, D, skip) {
        if (!bvh || bvh.count === 0) return null;
        var P = bvh.P, box = bvh.box, A = bvh.a, B = bvh.b, L = bvh.leaf, T = bvh.tri;
        var ox = O[0], oy = O[1], oz = O[2], dx = D[0], dy = D[1], dz = D[2];
        var ix = 1 / dx, iy = 1 / dy, iz = 1 / dz;
        var best = Infinity, bestTri = -1;
        var stack = [0];
        while (stack.length) {
            var id = stack.pop(), bo = id * 6;
            // slab test
            var t1 = (box[bo] - ox) * ix, t2 = (box[bo + 3] - ox) * ix;
            var tmin = Math.min(t1, t2), tmax = Math.max(t1, t2);
            t1 = (box[bo + 1] - oy) * iy; t2 = (box[bo + 4] - oy) * iy;
            tmin = Math.max(tmin, Math.min(t1, t2)); tmax = Math.min(tmax, Math.max(t1, t2));
            t1 = (box[bo + 2] - oz) * iz; t2 = (box[bo + 5] - oz) * iz;
            tmin = Math.max(tmin, Math.min(t1, t2)); tmax = Math.min(tmax, Math.max(t1, t2));
            if (!(tmax >= Math.max(tmin, 0)) || tmin > best) continue;   // NaN (axis-parallel on a face) -> miss
            if (!L[id]) { stack.push(B[id]); stack.push(A[id]); continue; }
            for (var k = A[id]; k < B[id]; k++) {
                var tr = T[k];
                if (skip && skip(tr)) continue;
                var o = tr * 9;
                // Moller-Trumbore, both faces
                var e1x = P[o + 3] - P[o], e1y = P[o + 4] - P[o + 1], e1z = P[o + 5] - P[o + 2];
                var e2x = P[o + 6] - P[o], e2y = P[o + 7] - P[o + 1], e2z = P[o + 8] - P[o + 2];
                var px = dy * e2z - dz * e2y, py = dz * e2x - dx * e2z, pz = dx * e2y - dy * e2x;
                var det = e1x * px + e1y * py + e1z * pz;
                if (det === 0) continue;
                var inv = 1 / det;
                var sx = ox - P[o], sy = oy - P[o + 1], sz = oz - P[o + 2];
                var u = (sx * px + sy * py + sz * pz) * inv;
                if (u < 0 || u > 1) continue;
                var qx = sy * e1z - sz * e1y, qy = sz * e1x - sx * e1z, qz = sx * e1y - sy * e1x;
                var w = (dx * qx + dy * qy + dz * qz) * inv;
                if (w < 0 || u + w > 1) continue;
                var t = (e2x * qx + e2y * qy + e2z * qz) * inv;
                if (t > 0 && t < best) { best = t; bestTri = tr; }
            }
        }
        return bestTri < 0 ? null : { tri: bestTri, t: best };
    }

    return { build: build, raycast: raycast };
})();
if (typeof module !== 'undefined') module.exports = FEATriBvh;
