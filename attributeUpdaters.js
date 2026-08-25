// ================================================================
// attributeUpdaters.js
// The ONLY per-update paths: rewrite the `cornerVals` attribute when
// the user changes component or LC, and the `dispVec` attribute when
// the deformed-shape view needs a new LC's displacements. Positions
// are built once and never touched again (deformation is GPU-side).
//
// Displacement-as-color is just a component selection -- it uses this
// exact same path, no special case.
// ================================================================

var FEAAttributes = (function () {

    // ---- shared corner -> vertex fan-out ---------------------------
    // Every cornerVals updater ends the same way: write the element's 4
    // corner values identically onto each of its vertices (6 for quads,
    // 3 for tris; a triangle's missing 4th corner mirrors corner 0 so
    // the bilinear eval reads it as a flat-ish patch -- the 4th corner
    // has zero area and never actually shows). fill(e, tmp) writes the
    // element's corner values into tmp[0..3]; iteration order matches
    // geometryBuilder so vertex ranges line up.
    var tmp4 = new Float32Array(4);

    function fanOutCornerVals(buildResult, fill) {
        var elemNCount = buildResult.elemNCount;
        var nElem = elemNCount.length;
        var attr = buildResult.geometry.getAttribute('cornerVals');
        var cv = attr.array;
        var vptr = 0;
        for (var e = 0; e < nElem; e++) {
            var nc = elemNCount[e];
            fill(e, nc, tmp4);
            var f0 = tmp4[0], f1 = tmp4[1], f2 = tmp4[2];
            var f3 = nc === 4 ? tmp4[3] : f0;
            var nv = nc === 4 ? 6 : 3;
            for (var i = 0; i < nv; i++) {
                cv[vptr * 4]     = f0;
                cv[vptr * 4 + 1] = f1;
                cv[vptr * 4 + 2] = f2;
                cv[vptr * 4 + 3] = f3;
                vptr++;
            }
        }
        attr.needsUpdate = true;
    }

    // Raw per-corner view of one component: direct strided data read.
    // `stride` is components-per-corner; defaults to the LC field block's
    // cornerComponents, but the strength block passes its own (its rows
    // are float32[maxCorners][nCapComponents]).
    function updateCornerVals(buildResult, model, lcData, compIndex, stride) {
        var h = model.header;
        var cc = stride || h.cornerComponents;
        var mc = h.maxCorners;
        fanOutCornerVals(buildResult, function (e, nc, out) {
            var base = e * mc * cc + compIndex;
            out[0] = lcData[base];
            out[1] = lcData[base + cc];
            out[2] = lcData[base + 2 * cc];
            out[3] = lcData[base + 3 * cc];
        });
    }

    // Min/max of the selected component across all real (non-NaN) corner
    // values -- used for auto vMin/vMax. Triangles only contribute their
    // 3 real corners. `stride` as in updateCornerVals.
    function computeRange(model, lcData, compIndex, stride) {
        var h = model.header;
        var cc = stride || h.cornerComponents;
        var mc = h.maxCorners;
        var nElem = h.nElements;
        var min = Infinity, max = -Infinity, count = 0;

        for (var e = 0; e < nElem; e++) {
            var base = e * mc * cc + compIndex;
            for (var k = 0; k < mc; k++) {
                var v = lcData[base + k * cc];
                if (v === v && v !== Infinity && v !== -Infinity) {   // skip NaN/Inf
                    if (v < min) min = v;
                    if (v > max) max = v;
                    count++;
                }
            }
        }
        if (count === 0) return { min: 0, max: 1, empty: true };
        if (min === max) { min -= 0.5; max += 0.5; }
        return { min: min, max: max, empty: false };
    }

    // Single pass per primary, updates all three envelope buffers
    // (min / max / abs(max)) with NaN-skipping semantics, and tracks
    // WHICH LC produced each winning slot value (Uint16 per slot value,
    // for the "controlling LC" readout / calc review).
    //
    // Pass bufs = null and the primary's LC index to seed; returns the
    // (new or mutated) envelope buffer set.
    //
    // NaN-skip means: a slot only stays NaN if EVERY primary was NaN
    // at that slot. A single missing-data LC can't destroy the result.
    //
    // Abs(max) is UNSIGNED: it stores the largest magnitude (a slot with
    // -50, 30, 40 reports 50). Its LC tracker still records which LC
    // supplied that magnitude.
    //
    // The three LC trackers cost 1.5x one LC block in bytes; if that
    // allocation fails on a huge model the fold degrades gracefully to
    // values-only (minLC/maxLC/absLC stay null).
    function foldEnvelopesThreeWay(bufs, primaryBuf, lcIndex) {
        if (!bufs) {
            var n = primaryBuf.length;
            var mnLC = null, mxLC = null, axLC = null;
            try {
                mnLC = new Uint16Array(n);
                mxLC = new Uint16Array(n);
                axLC = new Uint16Array(n);
                if (lcIndex) { mnLC.fill(lcIndex); mxLC.fill(lcIndex); axLC.fill(lcIndex); }
            } catch (err) {
                mnLC = mxLC = axLC = null;
            }
            return {
                min: new Float32Array(primaryBuf),
                max: new Float32Array(primaryBuf),
                abs: Float32Array.from(primaryBuf, function (v) { return Math.abs(v); }),
                minLC: mnLC, maxLC: mxLC, absLC: axLC
            };
        }
        var mn = bufs.min, mx = bufs.max, ax = bufs.abs;
        var track = bufs.minLC !== null;
        for (var i = 0; i < primaryBuf.length; i++) {
            var b = primaryBuf[i];
            if (b !== b) continue;                     // primary NaN: skip
            var a;
            a = mn[i]; if (a !== a || b < a) { mn[i] = b; if (track) bufs.minLC[i] = lcIndex; }
            a = mx[i]; if (a !== a || b > a) { mx[i] = b; if (track) bufs.maxLC[i] = lcIndex; }
            a = ax[i]; if (a !== a || Math.abs(b) > Math.abs(a)) {
                ax[i] = Math.abs(b);
                if (track) bufs.absLC[i] = lcIndex;
            }
        }
        return bufs;
    }

    // Coincident-node averaging -- the opposite of the per-corner default.
    // For each node, compute the mean of the corner values that touch it
    // across all elements, then write that mean to every corner of every
    // element at that node. Produces a smoothed field that's continuous
    // across element boundaries (at the cost of hiding the discontinuity
    // that is the per-corner viewer's whole point).
    //
    // NaN-skipping: NaN corner values are excluded from each node's mean.
    // A node's mean is NaN only if every contributing corner was NaN.
    //
    // Caches the per-node averages on buildResult so computeRangeAveraged
    // can reuse them without redoing the fold.
    function updateCornerValsNodeAveraged(buildResult, model, lcData, compIndex, stride) {
        var h = model.header;
        var cc = stride || h.cornerComponents;
        var mc = h.maxCorners;
        var nElem = h.nElements;
        var nNodes = h.nNodes;
        var normalGroups = buildResult.normalGroups;

        // Per-(element, corner) averaged value, keyed by flat index e*4+k.
        var cornerAvg = new Float32Array(nElem * 4);
        for (var i = 0; i < cornerAvg.length; i++) cornerAvg[i] = NaN;

        // Also build per-node average for computeRangeAveraged (use first
        // group's average as representative -- range is approximate anyway).
        var nodeAvg = new Float32Array(nNodes);
        for (var n0 = 0; n0 < nNodes; n0++) nodeAvg[n0] = NaN;

        for (var n = 0; n < nNodes; n++) {
            var groups = normalGroups[n];
            var firstGroupAvg = NaN;
            for (var g = 0; g < groups.length; g++) {
                var grp = groups[g];
                var sum = 0, cnt = 0;
                for (var j = 0; j < grp.length; j++) {
                    var entry = grp[j];
                    var v = lcData[entry.e * mc * cc + entry.k * cc + compIndex];
                    if (v === v) { sum += v; cnt++; }
                }
                var avg = cnt > 0 ? sum / cnt : NaN;
                if (g === 0) firstGroupAvg = avg;
                for (var j2 = 0; j2 < grp.length; j2++) {
                    var entry2 = grp[j2];
                    cornerAvg[entry2.e * 4 + entry2.k] = avg;
                }
            }
            nodeAvg[n] = firstGroupAvg;
        }
        buildResult.nodeAvgCache = nodeAvg;
        // Group-correct per-(element,corner) averages for point queries:
        // at a wall/slab junction the node has one average per normal
        // group, and a query on a wall element must read the wall
        // group's value, not whichever group happened to fold first.
        buildResult.cornerAvgCache = cornerAvg;

        fanOutCornerVals(buildResult, function (e, nc, out) {
            out[0] = cornerAvg[e * 4];
            out[1] = cornerAvg[e * 4 + 1];
            out[2] = cornerAvg[e * 4 + 2];
            out[3] = cornerAvg[e * 4 + 3];
        });
    }

    // Min/max of the per-node averages cached by the smoothed updater.
    function computeRangeAveraged(buildResult) {
        var avg = buildResult.nodeAvgCache;
        if (!avg) return { min: 0, max: 1, empty: true };
        var min = Infinity, max = -Infinity, count = 0;
        for (var i = 0; i < avg.length; i++) {
            var v = avg[i];
            if (v === v && v !== Infinity && v !== -Infinity) {
                if (v < min) min = v;
                if (v > max) max = v;
                count++;
            }
        }
        if (count === 0) return { min: 0, max: 1, empty: true };
        if (min === max) { min -= 0.5; max += 0.5; }
        return { min: min, max: max, empty: false };
    }

    // Fold one primary's DSR-component values into a per-slot running
    // max ACROSS all LCs AND across all DSR components, tracking which
    // DSR component AND which LC produced the winning value at each
    // (element,corner) slot. NaN-skipping. Pass valBuf = null to seed.
    //
    //   valBuf : Float32Array[nElements * maxCorners]  -- winning value
    //   srcBuf : Uint16Array [nElements * maxCorners]  -- winning DSR comp index
    //   lcBuf  : Uint16Array [nElements * maxCorners]  -- winning LC index
    //
    // dsrIndices is the metadata indices of every kind:'dsr' component;
    // lcIndex is the LC currently being folded in.
    function foldGlobalDsr(valBuf, srcBuf, lcBuf, primaryBuf, dsrIndices, header, lcIndex) {
        var cc = header.cornerComponents;
        var mc = header.maxCorners;
        var nE = header.nElements;
        var slots = nE * mc;
        if (!valBuf) {
            valBuf = new Float32Array(slots);
            srcBuf = new Uint16Array(slots);
            lcBuf  = new Uint16Array(slots);
            for (var i = 0; i < slots; i++) valBuf[i] = NaN;
        }
        var nDsr = dsrIndices.length;
        for (var e = 0; e < nE; e++) {
            for (var k = 0; k < mc; k++) {
                var slotBase = e * mc * cc + k * cc;
                var slotOut  = e * mc + k;
                var cur = valBuf[slotOut];
                for (var d = 0; d < nDsr; d++) {
                    var v = primaryBuf[slotBase + dsrIndices[d]];
                    if (v !== v) continue;
                    if (cur !== cur || v > cur) {
                        cur = v;
                        valBuf[slotOut] = v;
                        srcBuf[slotOut] = dsrIndices[d];
                        lcBuf[slotOut]  = lcIndex;
                    }
                }
            }
        }
        return { value: valBuf, source: srcBuf, lc: lcBuf };
    }

    // Write a per-(element,corner) slot value array (no component dim)
    // into the cornerVals attribute. Used for the global DSR envelope
    // views (value or category) -- there is no component to extract,
    // the slots ARE the displayed values.
    function updateCornerValsFromSlotArray(buildResult, slotValues, header) {
        var mc = header.maxCorners;
        fanOutCornerVals(buildResult, function (e, nc, out) {
            var b = e * mc;
            out[0] = slotValues[b];
            out[1] = slotValues[b + 1];
            out[2] = slotValues[b + 2];
            out[3] = slotValues[b + 3];
        });
    }

    // Same, but the source is a Uint16 per-slot CATEGORY array (e.g. the
    // controlling-check map). remap translates raw stored values (meta
    // component indices) to dense category ordinals 0..n-1; slots whose
    // value array says "no data" (NaN in valueArray) become NaN so the
    // shader's no-data path still works.
    function updateCornerValsFromSlotCategories(buildResult, slotCats, valueArray, remap, header) {
        var mc = header.maxCorners;
        fanOutCornerVals(buildResult, function (e, nc, out) {
            var b = e * mc;
            for (var k = 0; k < 4; k++) {
                var v = valueArray[b + k];
                out[k] = (v === v) ? remap[slotCats[b + k]] : NaN;
            }
        });
    }

    // Smoothed global DSR: for each node, find the contributing corner
    // with the largest DSR value, and take the value AND the controlling
    // source AND the controlling LC from that corner. This preserves the
    // controlling semantic across the smoothed view (you see the
    // worst-case check at each node, not an average over averages).
    function smoothGlobalDsr(buildResult, dsrValue, dsrSource, dsrLC, nNodes) {
        var nElem = buildResult.elemNCount.length;
        var normalGroups = buildResult.normalGroups;

        // Per-(element, corner) smoothed value/source/LC, keyed by e*4+k.
        var cornerVal = new Float32Array(nElem * 4);
        var cornerSrc = new Uint16Array(nElem * 4);
        var cornerLC  = new Uint16Array(nElem * 4);
        for (var i = 0; i < cornerVal.length; i++) cornerVal[i] = NaN;

        var nodeMaxVal = new Float32Array(nNodes);
        var nodeWinSrc = new Uint16Array(nNodes);
        for (var n0 = 0; n0 < nNodes; n0++) nodeMaxVal[n0] = NaN;

        for (var n = 0; n < nNodes; n++) {
            var groups = normalGroups[n];
            for (var g = 0; g < groups.length; g++) {
                var grp = groups[g];
                var bestVal = NaN, bestSrc = 0, bestLC = 0;
                for (var j = 0; j < grp.length; j++) {
                    var entry = grp[j];
                    var slot = entry.e * 4 + entry.k;
                    var v = dsrValue[slot];
                    if (v !== v) continue;
                    if (bestVal !== bestVal || v > bestVal) {
                        bestVal = v;
                        bestSrc = dsrSource[slot];
                        bestLC = dsrLC ? dsrLC[slot] : 0;
                    }
                }
                for (var j2 = 0; j2 < grp.length; j2++) {
                    var entry2 = grp[j2];
                    var slot2 = entry2.e * 4 + entry2.k;
                    cornerVal[slot2] = bestVal;
                    cornerSrc[slot2] = bestSrc;
                    cornerLC[slot2]  = bestLC;
                }
                if (g === 0) { nodeMaxVal[n] = bestVal; nodeWinSrc[n] = bestSrc; }
            }
        }
        buildResult.dsrNodeSrcCache = nodeWinSrc;
        buildResult.dsrCornerSmoothed = cornerVal;
        buildResult.dsrCornerSmoothedSrc = cornerSrc;
        buildResult.dsrCornerSmoothedLC = cornerLC;
        return nodeMaxVal;
    }

    // ---- displacement vector attribute ------------------------------
    // Write each vertex's OWN corner displacement (dx,dy,dz) into the
    // mesh dispVec attribute, and each edge endpoint's into the edge
    // overlay's dispVec attribute (via buildResult.edgeCorners). Unlike
    // cornerVals this differs per vertex: the offset is positional, and
    // the interior deforms through the GPU's plain linear interpolation.
    //
    // dispIdx is [ix, iy, iz] component indices (model.meta.dispVector).
    // NaN displacements (no-data elements, tri pad slots) become 0 so
    // partial data can't fling vertices to NaN-land.
    //
    // Returns the largest displacement magnitude seen (for auto-scale).
    function updateDispVecs(buildResult, model, lcData, dispIdx, edgeDispAttr) {
        var h = model.header;
        var cc = h.cornerComponents;
        var mc = h.maxCorners;
        var nElem = h.nElements;
        var elemNCount = buildResult.elemNCount;
        var ix = dispIdx[0], iy = dispIdx[1], iz = dispIdx[2];

        // Per-(element,corner) displacement, reused for mesh and edges.
        var cd = new Float32Array(nElem * 4 * 3);
        var maxLen2 = 0;
        for (var e = 0; e < nElem; e++) {
            var base = e * mc * cc;
            for (var k = 0; k < 4; k++) {
                var cb = base + k * cc;
                var dx = lcData[cb + ix], dy = lcData[cb + iy], dz = lcData[cb + iz];
                if (dx !== dx) dx = 0;
                if (dy !== dy) dy = 0;
                if (dz !== dz) dz = 0;
                var o = (e * 4 + k) * 3;
                cd[o] = dx; cd[o + 1] = dy; cd[o + 2] = dz;
                var l2 = dx * dx + dy * dy + dz * dz;
                if (l2 > maxLen2) maxLen2 = l2;
            }
        }

        // Mesh vertices: per-element corner order matches geometryBuilder
        // (quad tris (0,1,2),(0,2,3) -> corners [0,1,2,0,2,3]; tri [0,1,2]).
        var QUAD_ORDER = [0, 1, 2, 0, 2, 3];
        var attr = buildResult.geometry.getAttribute('dispVec');
        var dv = attr.array;
        var vptr = 0;
        for (var e2 = 0; e2 < nElem; e2++) {
            var nc = elemNCount[e2];
            var nv = nc === 4 ? 6 : 3;
            for (var i = 0; i < nv; i++) {
                var corner = nc === 4 ? QUAD_ORDER[i] : i;
                var src = (e2 * 4 + corner) * 3;
                dv[vptr * 3]     = cd[src];
                dv[vptr * 3 + 1] = cd[src + 1];
                dv[vptr * 3 + 2] = cd[src + 2];
                vptr++;
            }
        }
        attr.needsUpdate = true;

        // Edge overlay vertices.
        if (edgeDispAttr) {
            var ec = buildResult.edgeCorners;
            var ed = edgeDispAttr.array;
            for (var v = 0; v < ec.length; v++) {
                var src2 = ec[v] * 3;
                ed[v * 3]     = cd[src2];
                ed[v * 3 + 1] = cd[src2 + 1];
                ed[v * 3 + 2] = cd[src2 + 2];
            }
            edgeDispAttr.needsUpdate = true;
        }

        return Math.sqrt(maxLen2);
    }

    return {
        updateCornerVals: updateCornerVals,
        updateCornerValsNodeAveraged: updateCornerValsNodeAveraged,
        updateCornerValsFromSlotArray: updateCornerValsFromSlotArray,
        updateCornerValsFromSlotCategories: updateCornerValsFromSlotCategories,
        updateDispVecs: updateDispVecs,
        computeRange: computeRange,
        computeRangeAveraged: computeRangeAveraged,
        foldEnvelopesThreeWay: foldEnvelopesThreeWay,
        foldGlobalDsr: foldGlobalDsr,
        smoothGlobalDsr: smoothGlobalDsr
    };
})();
