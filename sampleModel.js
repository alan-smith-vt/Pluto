// ================================================================
// sampleModel.js
// Synthetic FEA binary generator -- a curved plate of mixed quad and
// triangle elements with a known field. Produces a Blob in the exact
// v3 format, so the viewer can be exercised end-to-end through the
// SAME slice/readRange path a real file uses (Blob.slice behaves
// identically to File.slice).
//
// This is a demo/verification aid, not the production writer.
//
// Field components are designed to make the architecture visible:
//   - stress components carry a per-element jump  -> shared-node
//     corners differ -> inter-element discontinuity (the signal).
//   - "Smooth Scalar" and the displacement components are sampled
//     purely at node position -> shared-node corners match -> the
//     field reads continuous across element boundaries for free.
// ================================================================

var FEASample = (function () {

    var MAGIC = 0x46454156;
    var VERSION = 3;
    var HEADER_FIELDS = 15;

    // variant (optional, default 0): produces a model with IDENTICAL
    // geometry, strengths, components and load-case NAMES but different
    // field values -- the "same structure, different soil springs"
    // scenario, for exercising the multi-model viewer.
    function buildSampleBlob(variant) {
        variant = variant || 0;
        var vScale = 1 + 0.18 * variant;       // field magnitude shift per model
        // ---- grid -------------------------------------------------
        var NX = 24, NY = 16;                  // cells
        var SX = 120, SY = 80;                 // plate extent (inches)
        var nodesX = NX + 1, nodesY = NY + 1;
        var nNodes = nodesX * nodesY;

        // Node coordinates: plate in XY, gentle Z curvature.
        var coords = new Float32Array(nNodes * 3);
        for (var j = 0; j <= NY; j++) {
            for (var i = 0; i <= NX; i++) {
                var idx = j * nodesX + i;
                var x = i / NX * SX;
                var y = j / NY * SY;
                var z = 7.0 * Math.sin(Math.PI * x / SX) * Math.sin(Math.PI * y / SY);
                coords[idx * 3] = x;
                coords[idx * 3 + 1] = y;
                coords[idx * 3 + 2] = z;
            }
        }

        // ---- elements (quads + a triangle corner region) ----------
        // Cells in the i<6 && j<6 corner are split into 2 triangles
        // each, so the file exercises mixed tri/quad handling.
        var elements = [];   // { ncount, nodes:[4] }
        for (var jc = 0; jc < NY; jc++) {
            for (var ic = 0; ic < NX; ic++) {
                var n00 = jc * nodesX + ic;
                var n10 = jc * nodesX + ic + 1;
                var n11 = (jc + 1) * nodesX + ic + 1;
                var n01 = (jc + 1) * nodesX + ic;
                if (ic < 6 && jc < 6) {
                    elements.push({ ncount: 3, nodes: [n00, n10, n11, 0xFFFFFFFF] });
                    elements.push({ ncount: 3, nodes: [n00, n11, n01, 0xFFFFFFFF] });
                } else {
                    elements.push({ ncount: 4, nodes: [n00, n10, n11, n01] });
                }
            }
        }
        var nElements = elements.length;

        // One quad element forced to all-NaN to exercise the no-data path.
        var nanElem = Math.floor(nElements * 0.62);
        if (elements[nanElem].ncount !== 4) nanElem = nElements - 1;

        // ---- field generation -------------------------------------
        var maxCorners = 4;
        var cornerComponents = 19;              // 8 stress + 6 displacement + 3 DSR + 2 preDSR
        var nFieldLC = 3;                       // LC1, LC2, ENV(max)
        var fieldCount = nFieldLC * nElements * maxCorners * cornerComponents;
        var field = new Float32Array(fieldCount);

        function nodePos(ni) {
            return [coords[ni * 3], coords[ni * 3 + 1], coords[ni * 3 + 2]];
        }
        // Per-element constant jump -> creates the stress discontinuity.
        function jump(e, salt) { return (((e * salt) % 100) - 50) * 4.0; }

        // Compute all corner components for one LC.
        // Stress components carry a per-element jump -> shared-node
        // corners differ -> inter-element discontinuity (the signal).
        // Displacement components are sampled at node position only ->
        // shared-node corners match -> continuous across elements.
        function corner(lc, e, pos) {
            var x = pos[0], y = pos[1];
            var cx = lc === 1 ? 90 : 60, cy = lc === 1 ? 22 : 40;
            var r2 = (x - cx) * (x - cx) + (y - cy) * (y - cy);
            var sc = (lc === 1 ? 0.85 : 1.0) * vScale;
            var base = vScale * ((lc === 1 ? 700 : 900) * Math.exp(-r2 / (lc === 1 ? 1200 : 1600)) + 50);
            var jmp = jump(e, (lc === 1 ? 53 : 37) + variant * 7);

            // DSR fields. Each peaks in a different region so the
            // controlling check varies spatially: P-M near the center,
            // Shear-V2 near vertical edges, Compression near horizontal
            // edges. Each LC reweights them so controlling also varies
            // with LC -- a non-trivial Global DSR envelope.
            var distLR = Math.min(x, SX - x);
            var distTB = Math.min(y, SY - y);
            var pmCheck    = 1.7 * Math.exp(-r2 / 1800) + 0.15;
            var shearCheck = 1.4 * Math.exp(-distLR * 0.05) + 0.10;
            var compCheck  = 1.5 * Math.exp(-distTB * 0.07) + 0.10;
            if (lc === 1) { pmCheck *= 0.70; shearCheck *= 1.25; compCheck *= 0.80; }
            // Variant models reweight the checks so the controlling MODEL
            // (not just the controlling LC/check) varies spatially.
            if (variant) { pmCheck *= 1.18; shearCheck *= 0.82; compCheck *= 1.06; }
            // Constituent (kind:"preDSR") checks: the parts of the
            // combined P-M check, stored so the controlling combined
            // ratio can be traced back to which demand dominates.
            // Plottable reference fields; excluded from the Global DSR
            // envelope by their kind.
            var pmMomentPart = 0.72 * pmCheck + 0.04 * Math.sin(0.08 * x);
            var pmAxialPart  = pmCheck - pmMomentPart;

            return [
                // 8 stress components (discontinuous: per-element jump)
                base * Math.cos(0.05 * x) + jmp,                      // Shear X
                base * Math.sin(0.06 * y) + jmp,                      // Shear Y
                base * Math.sin(0.04 * (x + y)) + jmp,                // Shear IP
                0.6 * base + 0.4 * x + jmp,                           // Axial X
                0.6 * base + 0.4 * y + jmp,                           // Axial Y
                base * Math.cos(0.03 * x) * Math.cos(0.03 * y) + jmp, // Moment X
                base * Math.sin(0.03 * x) * Math.sin(0.03 * y) + jmp, // Moment Y
                0.5 * base * Math.sin(0.05 * (x - y)) + jmp,          // Moment IP
                // 6 displacement components (continuous: node-sampled)
                sc * 0.45 * Math.sin(Math.PI * x / SX) * Math.cos(Math.PI * y / (2 * SY)), // Translation X
                sc * 0.32 * Math.sin(Math.PI * y / SY),                                    // Translation Y
                sc * 1.30 * Math.sin(Math.PI * x / SX) * Math.sin(Math.PI * y / SY),       // Translation Z
                sc * 0.012 * Math.cos(Math.PI * x / SX),                                   // Rotation X
                sc * 0.012 * Math.cos(Math.PI * y / SY),                                   // Rotation Y
                sc * 0.006 * Math.sin(Math.PI * (x + y) / (SX + SY)),                      // Rotation Z
                // 3 DSR components
                pmCheck, shearCheck, compCheck,
                // 2 preDSR constituents of the P-M check
                pmMomentPart, pmAxialPart
            ];
        }

        for (var e = 0; e < nElements; e++) {
            var el = elements[e];
            var isNaNElem = (e === nanElem);
            for (var lc = 0; lc < 2; lc++) {            // LC0, LC1 primary
                for (var k = 0; k < maxCorners; k++) {
                    var b = lc * nElements * maxCorners * cornerComponents
                          + e * maxCorners * cornerComponents
                          + k * cornerComponents;
                    var realCorner = k < el.ncount;
                    if (!realCorner || isNaNElem) {
                        // triangle 4th slot OR forced no-data element.
                        for (var cc = 0; cc < cornerComponents; cc++) field[b + cc] = NaN;
                    } else {
                        var vals = corner(lc, e, nodePos(el.nodes[k]));
                        for (var c2 = 0; c2 < cornerComponents; c2++) field[b + c2] = vals[c2];
                    }
                }
            }
            // Envelope LC (index 2): per-slot max of LC0 & LC1.
            for (var k2 = 0; k2 < maxCorners; k2++) {
                var b0 = 0 * nElements * maxCorners * cornerComponents
                       + e * maxCorners * cornerComponents + k2 * cornerComponents;
                var b1 = 1 * nElements * maxCorners * cornerComponents
                       + e * maxCorners * cornerComponents + k2 * cornerComponents;
                var b2 = 2 * nElements * maxCorners * cornerComponents
                       + e * maxCorners * cornerComponents + k2 * cornerComponents;
                for (var c3 = 0; c3 < cornerComponents; c3++) {
                    field[b2 + c3] = Math.max(field[b0 + c3], field[b1 + c3]);
                }
            }
        }

        // ---- design-strength block (LC-independent) ----------------------
        // float32[nElements][maxCorners][nStr], same slot conventions
        // as the corner field (tri pad slot NaN). Placed immediately
        // after the header so its offset is known before the metadata
        // (which references it) is built.
        var nStr = 3;
        var strength = new Float32Array(nElements * maxCorners * nStr);
        for (var ce = 0; ce < nElements; ce++) {
            var cel = elements[ce];
            for (var ck = 0; ck < maxCorners; ck++) {
                var cb = (ce * maxCorners + ck) * nStr;
                if (ck >= cel.ncount) {
                    for (var ci = 0; ci < nStr; ci++) strength[cb + ci] = NaN;
                } else {
                    var cp = nodePos(cel.nodes[ck]);
                    // Node-sampled (continuous), spatially varying so the
                    // strength views are visually non-trivial.
                    strength[cb]     = 800 + 400 * (1 - cp[0] / SX) + 100 * cp[1] / SY;  // shear
                    strength[cb + 1] = 1500 - 500 * cp[1] / SY;                          // moment
                    strength[cb + 2] = 1200 + 300 * Math.sin(Math.PI * cp[0] / SX);      // axial
                }
            }
        }

        // ---- metadata (UTF-8 JSON) --------------------------------
        var meta = {
            format: 'FEAV per-corner field',
            generator: 'sampleModel.js',
            lengthUnit: 'in',           // geometry unit (optional key)
            loadCases: [
                { name: 'LC1 Gravity', type: 'primary' },
                { name: 'LC2 Wind', type: 'primary' },
                { name: 'ENV Max', type: 'envelope' }
            ],
            components: [
                { name: 'Shear X', kind: 'stress', unit: 'psi' },
                { name: 'Shear Y', kind: 'stress', unit: 'psi' },
                { name: 'Shear IP', kind: 'stress', unit: 'psi' },
                { name: 'Axial X', kind: 'stress', unit: 'psi' },
                { name: 'Axial Y', kind: 'stress', unit: 'psi' },
                { name: 'Moment X', kind: 'stress', unit: 'lb-in/in' },
                { name: 'Moment Y', kind: 'stress', unit: 'lb-in/in' },
                { name: 'Moment IP', kind: 'stress', unit: 'lb-in/in' },
                { name: 'Translation X', kind: 'displacement', unit: 'in' },
                { name: 'Translation Y', kind: 'displacement', unit: 'in' },
                { name: 'Translation Z', kind: 'displacement', unit: 'in' },
                { name: 'Rotation X', kind: 'displacement', unit: 'rad' },
                { name: 'Rotation Y', kind: 'displacement', unit: 'rad' },
                { name: 'Rotation Z', kind: 'displacement', unit: 'rad' },
                { name: 'P-M check',  kind: 'dsr' },
                { name: 'Shear-V2',   kind: 'dsr' },
                { name: 'Compression', kind: 'dsr' },
                // Generic kind: plottable with default behavior, no DSR
                // machinery. Constituents of the P-M check for trace-back.
                { name: 'PM moment part', kind: 'preDSR' },
                { name: 'PM axial part',  kind: 'preDSR' }
            ],
            // Which components form the translation vector for the
            // deformed-shape view (indices into components[]).
            displacementVector: [8, 9, 10],
            // LC-independent design-strength block (offset filled in below --
            // it sits right after the header, so it's already known).
            strengths: {
                offset: HEADER_FIELDS * 4,
                components: [
                    { name: 'Shear strength',  unit: 'psi' },
                    { name: 'Moment strength', unit: 'lb-in/in' },
                    { name: 'Axial strength',  unit: 'psi' }
                ]
            }
        };
        var metaStr = JSON.stringify(meta);
        while (metaStr.length % 4 !== 0) metaStr += ' ';      // pad to 4-byte boundary
        var metaBytes = new TextEncoder().encode(metaStr);

        // ---- layout & write ---------------------------------------
        var headerSize = HEADER_FIELDS * 4;                   // 60
        var strOffset = headerSize;
        var strLength = strength.length * 4;
        var metaOffset = strOffset + strLength;
        var metaLength = metaBytes.length;
        var nodesOffset = metaOffset + metaLength;
        var nodesLen = nNodes * 3 * 4;
        var elemsOffset = nodesOffset + nodesLen;
        var elemsLen = nElements * 5 * 4;
        var nodeIdOffset = elemsOffset + elemsLen;
        var nodeIdLen = nNodes * 4;
        var elemIdOffset = nodeIdOffset + nodeIdLen;
        var elemIdLen = nElements * 4;
        var cornerFieldOffset = elemIdOffset + elemIdLen;
        var fieldLen = fieldCount * 4;
        var total = cornerFieldOffset + fieldLen;

        var buf = new ArrayBuffer(total);
        var dv = new DataView(buf);
        var hv = [
            MAGIC, VERSION, headerSize, nNodes, nElements, nFieldLC,
            cornerComponents, maxCorners, metaOffset, metaLength,
            nodesOffset, elemsOffset, nodeIdOffset, elemIdOffset, cornerFieldOffset
        ];
        for (var hi = 0; hi < hv.length; hi++) dv.setUint32(hi * 4, hv[hi] >>> 0, true);

        new Float32Array(buf, strOffset, strength.length).set(strength);
        new Uint8Array(buf, metaOffset, metaLength).set(metaBytes);
        new Float32Array(buf, nodesOffset, nNodes * 3).set(coords);

        var elemU32 = new Uint32Array(buf, elemsOffset, nElements * 5);
        for (var ei = 0; ei < nElements; ei++) {
            elemU32[ei * 5] = elements[ei].ncount;
            for (var en = 0; en < 4; en++) elemU32[ei * 5 + 1 + en] = elements[ei].nodes[en] >>> 0;
        }

        var nodeIdU32 = new Uint32Array(buf, nodeIdOffset, nNodes);
        for (var ni = 0; ni < nNodes; ni++) nodeIdU32[ni] = 1000 + ni * 7;   // sparse IDs
        var elemIdU32 = new Uint32Array(buf, elemIdOffset, nElements);
        for (var eii = 0; eii < nElements; eii++) elemIdU32[eii] = 5000 + eii * 3;

        new Float32Array(buf, cornerFieldOffset, fieldCount).set(field);

        return new Blob([buf], { type: 'application/octet-stream' });
    }

    // Emit the sample binary as a real .bin download, so it can be fed
    // back through the file picker -- the viewer and the inspector then
    // exercise the exact File.slice path a production file would use.
    function downloadSampleBin(filename) {
        filename = filename || 'sampleModel.bin';
        var blob = buildSampleBlob();
        var url = URL.createObjectURL(blob);
        var a = document.createElement('a');
        a.href = url;
        a.download = filename;
        document.body.appendChild(a);
        a.click();
        document.body.removeChild(a);
        // Revoke after the click has been dispatched.
        setTimeout(function () { URL.revokeObjectURL(url); }, 0);
        return blob;
    }

    return {
        buildSampleBlob: buildSampleBlob,
        downloadSampleBin: downloadSampleBin
    };
})();
