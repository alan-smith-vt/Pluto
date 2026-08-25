// ================================================================
// pointQuery.js
// Raycast -> triangle->element map -> inverse-bilinear -> exact eval.
//
// The readout value comes from the resident float corner DATA, never
// from reading back pixel color (colormaps aren't injective, are 8-bit,
// and a GPU readback stalls). Evaluating bilinear from the resident
// float values is exact and full-precision.
// ================================================================

var FEAQuery = (function () {

    // ---- core math -------------------------------------------------

    function bilinear(u, v, f0, f1, f2, f3) {
        return (1 - u) * (1 - v) * f0 + u * (1 - v) * f1
             + u * v * f2 + (1 - u) * v * f3;
    }

    // Closed-form inverse of the bilinear map. p, p0..p3 are THREE.Vector2
    // already projected into the quad's plane, with corners ordered
    // p0=(0,0) p1=(1,0) p2=(1,1) p3=(0,1).
    //
    //   pos(u,v) = p0 + u*E + v*F + u*v*G
    //     E = p1-p0   F = p3-p0   G = p0-p1+p2-p3 (twist)
    // Eliminating u gives a quadratic in v:
    //     cross(G,F) v^2 + (cross(E,F)+cross(h,G)) v + cross(h,E) = 0
    function cross2(m, n) { return m.x * n.y - m.y * n.x; }

    function solveU(h, E, F, G, v) {
        var dx = E.x + G.x * v, dy = E.y + G.y * v;
        return Math.abs(dx) >= Math.abs(dy)
            ? (h.x - F.x * v) / dx
            : (h.y - F.y * v) / dy;
    }

    // Penalty for a parameter outside [0,1] -- 0 when valid.
    function outOfRange(t) {
        return (t < 0 ? -t : 0) + (t > 1 ? t - 1 : 0);
    }

    function inverseBilinear(p, p0, p1, p2, p3) {
        var E = p1.clone().sub(p0);
        var F = p3.clone().sub(p0);
        var G = p0.clone().sub(p1).add(p2).sub(p3);   // twist term
        var h = p.clone().sub(p0);

        var k2 = cross2(G, F);
        var k1 = cross2(E, F) + cross2(h, G);
        var k0 = cross2(h, E);

        var candidates;
        if (Math.abs(k2) < 1e-12) {                   // parallelogram / linear
            candidates = [Math.abs(k1) < 1e-20 ? 0 : -k0 / k1];
        } else {
            var disc = k1 * k1 - 4 * k2 * k0;
            var w = disc < 0 ? 0 : Math.sqrt(disc);
            candidates = [(-k1 - w) / (2 * k2), (-k1 + w) / (2 * k2)];
        }

        // Pick the (u,v) pair closest to the unit square.
        var best = null, bestScore = Infinity;
        for (var i = 0; i < candidates.length; i++) {
            var v = candidates[i];
            var u = solveU(h, E, F, G, v);
            var score = outOfRange(u) + outOfRange(v);
            if (score < bestScore) { bestScore = score; best = { u: u, v: v }; }
        }
        return best;
    }

    // ---- helpers ---------------------------------------------------

    function cornerPos(model, nodeIdx) {
        return new THREE.Vector3(
            model.nodes[nodeIdx * 3],
            model.nodes[nodeIdx * 3 + 1],
            model.nodes[nodeIdx * 3 + 2]
        );
    }

    // Project world points into the element's best-fit plane (2D).
    function planeProjector(corners) {
        var c0 = corners[0];
        var eU = corners[1].clone().sub(c0);
        var eV = corners[corners.length - 1].clone().sub(c0);
        var nrm = eU.clone().cross(eV);
        if (nrm.lengthSq() < 1e-20) nrm.set(0, 0, 1);
        nrm.normalize();
        var basisU = eU.clone();
        if (basisU.lengthSq() < 1e-20) basisU.set(1, 0, 0);
        basisU.normalize();
        var basisV = nrm.clone().cross(basisU).normalize();
        return function (p) {
            var d = p.clone().sub(c0);
            return new THREE.Vector2(d.dot(basisU), d.dot(basisV));
        };
    }

    // Barycentric interpolation for a triangle element (3 real corners).
    function triEval(corners, p, f0, f1, f2) {
        var v0 = corners[1].clone().sub(corners[0]);
        var v1 = corners[2].clone().sub(corners[0]);
        var v2 = p.clone().sub(corners[0]);
        var d00 = v0.dot(v0), d01 = v0.dot(v1), d11 = v1.dot(v1);
        var d20 = v2.dot(v0), d21 = v2.dot(v1);
        var denom = d00 * d11 - d01 * d01;
        if (Math.abs(denom) < 1e-20) return { value: f0, u: 0, v: 0 };
        var b1 = (d11 * d20 - d01 * d21) / denom;
        var b2 = (d00 * d21 - d01 * d20) / denom;
        var b0 = 1 - b1 - b2;
        return { value: b0 * f0 + b1 * f1 + b2 * f2, u: b1, v: b2 };
    }

    // ---- public ----------------------------------------------------

    // state: { model, buildResult, lcData, compIndex, compStride?,
    //          smoothing, dsrMode }
    // compStride (optional): components-per-corner of lcData; defaults
    // to header.cornerComponents. The strength block passes its own.
    // dsrMode (optional): { value:  Float32Array[nElem*maxCorners],
    //                       source: Uint16Array[nElem*maxCorners],
    //                       lc:     Uint16Array[nElem*maxCorners] }
    // When dsrMode is set, the corner values come from `value` (per-slot
    // global DSR max) and we report the controlling source (which DSR
    // check) and controlling LC per corner.
    function query(state, faceIndex, worldPoint) {
        var model = state.model, build = state.buildResult;
        var lcData = state.lcData;
        var comp = state.compIndex;
        var h = model.header;

        var elem = build.triToElem[faceIndex];
        var nc = build.elemNCount[elem];
        var dsr = state.dsrMode;
        var smoothed = state.smoothing && (dsr ? build.dsrNodeSrcCache : build.nodeAvgCache);

        // Corner world positions + node IDs + corner field values. The
        // value source depends on the mode:
        //   - DSR + smoothed   -> per-node max from dsrNodeSrcCache lookup
        //   - DSR + raw        -> per-slot value from dsr.value
        //   - regular smoothed -> per-node mean from nodeAvgCache
        //   - regular raw      -> direct lcData read
        var stride = state.compStride || h.cornerComponents;
        var corners = [], nodeIds = [], f = [], cornerSources = [], cornerLCs = [];
        var base = elem * h.maxCorners * stride + comp;
        for (var k = 0; k < nc; k++) {
            var ni = build.elemCorners[elem * 4 + k];
            var slot = elem * 4 + k;
            corners.push(cornerPos(model, ni));
            nodeIds.push(model.nodeIds[ni]);
            if (dsr) {
                if (smoothed && build.dsrCornerSmoothed) {
                    f.push(build.dsrCornerSmoothed[slot]);
                    cornerSources.push(build.dsrCornerSmoothedSrc
                        ? build.dsrCornerSmoothedSrc[slot]
                        : dsr.source[slot]);
                    cornerLCs.push(build.dsrCornerSmoothedLC
                        ? build.dsrCornerSmoothedLC[slot]
                        : (dsr.lc ? dsr.lc[slot] : 0));
                } else {
                    f.push(dsr.value[slot]);
                    cornerSources.push(dsr.source[slot]);
                    cornerLCs.push(dsr.lc ? dsr.lc[slot] : 0);
                }
            } else {
                // Smoothed: prefer the per-(element,corner) group-correct
                // average (matches the displayed colors exactly); the
                // per-node cache is a first-group fallback only.
                f.push(smoothed
                    ? (build.cornerAvgCache ? build.cornerAvgCache[slot]
                                            : build.nodeAvgCache[ni])
                    : lcData[base + k * stride]);
            }
        }

        var result;
        if (nc === 4) {
            var proj = planeProjector(corners);
            var uv = inverseBilinear(
                proj(worldPoint), proj(corners[0]), proj(corners[1]),
                proj(corners[2]), proj(corners[3])
            );
            result = {
                value: bilinear(uv.u, uv.v, f[0], f[1], f[2], f[3]),
                u: uv.u, v: uv.v
            };
        } else {
            result = triEval(corners, worldPoint, f[0], f[1], f[2]);
        }

        // Nearest corner node by 3D distance to the hit point.
        var nearestK = 0, nearestD = Infinity;
        for (var j = 0; j < nc; j++) {
            var dist = corners[j].distanceToSquared(worldPoint);
            if (dist < nearestD) { nearestD = dist; nearestK = j; }
        }

        var v = result.value;
        return {
            ok: true,
            element: elem,
            elementId: model.elemIds[elem],
            ncount: nc,
            value: v,
            noData: !(v === v),                       // NaN -> "no data"
            u: result.u, v: result.v,
            cornerNodeIds: nodeIds,
            cornerValues: f,
            cornerSources: dsr ? cornerSources : null,
            cornerLCs: dsr ? cornerLCs : null,
            nearestCorner: nearestK,
            nearestNodeId: nodeIds[nearestK],
            point: worldPoint.clone(),
            isDsr: !!dsr
        };
    }

    return {
        query: query,
        bilinear: bilinear,
        inverseBilinear: inverseBilinear
    };
})();
