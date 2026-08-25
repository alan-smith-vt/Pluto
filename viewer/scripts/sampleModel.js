// ================================================================
// sampleModel.js
// Synthetic FEA binary generator -- a curved plate of mixed quad and
// triangle elements with a known field, plus (v4) a steel frame of
// beam elements with parametric sections under it.
//
//   buildSampleBlob(variant)    -> legacy v3 file (plate only)
//   buildSampleBlobV4(variant)  -> Promise<Blob>, v4 file: shell domain
//                                  + beam domain + SECT + geometryHash
//
// Both go through the SAME slice/readRange path a real file uses
// (Blob.slice behaves identically to File.slice). Demo/verification
// aid, not the production writer.
//
// Field design (why the numbers look the way they do):
//   - shell stress components carry a per-element jump -> shared-node
//     corners differ -> inter-element discontinuity (the signal).
//   - displacement components are sampled purely at node position ->
//     shared-node corners match -> continuous across elements; the
//     beam domain samples the SAME displacement function at its nodes,
//     so plate and frame deform together.
//   - beam force components vary linearly end A -> end B.
// ================================================================

var FEASample = (function () {

    var MAGIC = 0x46454156;
    var HEADER_FIELDS = 15;

    var NX = 24, NY = 16;                  // plate cells
    var SX = 120, SY = 80;                 // plate extent (inches)
    var COL_H = 60;                        // column height below the plate

    // ---- shared plate generator ---------------------------------------
    // variant (optional, default 0): IDENTICAL geometry, strengths,
    // components and load-case NAMES but different field values -- the
    // "same structure, different soil springs" scenario.
    function buildPlate(variant) {
        variant = variant || 0;
        var vScale = 1 + 0.18 * variant;
        var nodesX = NX + 1, nodesY = NY + 1;
        var nNodes = nodesX * nodesY;

        var coords = new Float64Array(nNodes * 3);
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

        // Cells in the i<6 && j<6 corner are split into 2 triangles.
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
        var nanElem = Math.floor(nElements * 0.62);
        if (elements[nanElem].ncount !== 4) nanElem = nElements - 1;

        var maxCorners = 4;
        var cornerComponents = 19;              // 8 stress + 6 disp + 3 DSR + 2 preDSR
        var nFieldLC = 3;                       // LC1, LC2, ENV(max)
        var fieldCount = nFieldLC * nElements * maxCorners * cornerComponents;
        var field = new Float32Array(fieldCount);

        function nodePos(ni) {
            return [coords[ni * 3], coords[ni * 3 + 1], coords[ni * 3 + 2]];
        }
        function jump(e, salt) { return (((e * salt) % 100) - 50) * 4.0; }

        // Node displacement (continuous) -- shared with the beam domain.
        function disp(lc, x, y, z) {
            var sc = (lc === 1 ? 0.85 : 1.0) * vScale;
            var zf = z < 0 ? Math.max(0, 1 + z / COL_H) : 1;   // columns taper to 0 at the base
            return [
                zf * sc * 0.45 * Math.sin(Math.PI * x / SX) * Math.cos(Math.PI * y / (2 * SY)),
                zf * sc * 0.32 * Math.sin(Math.PI * y / SY),
                zf * sc * 1.30 * Math.sin(Math.PI * x / SX) * Math.sin(Math.PI * y / SY),
                sc * 0.012 * Math.cos(Math.PI * x / SX),
                sc * 0.012 * Math.cos(Math.PI * y / SY),
                sc * 0.006 * Math.sin(Math.PI * (x + y) / (SX + SY))
            ];
        }

        function corner(lc, e, pos) {
            var x = pos[0], y = pos[1];
            var cx = lc === 1 ? 90 : 60, cy = lc === 1 ? 22 : 40;
            var r2 = (x - cx) * (x - cx) + (y - cy) * (y - cy);
            var base = vScale * ((lc === 1 ? 700 : 900) * Math.exp(-r2 / (lc === 1 ? 1200 : 1600)) + 50);
            var jmp = jump(e, (lc === 1 ? 53 : 37) + variant * 7);
            var distLR = Math.min(x, SX - x);
            var distTB = Math.min(y, SY - y);
            var pmCheck    = 1.7 * Math.exp(-r2 / 1800) + 0.15;
            var shearCheck = 1.4 * Math.exp(-distLR * 0.05) + 0.10;
            var compCheck  = 1.5 * Math.exp(-distTB * 0.07) + 0.10;
            if (lc === 1) { pmCheck *= 0.70; shearCheck *= 1.25; compCheck *= 0.80; }
            if (variant) { pmCheck *= 1.18; shearCheck *= 0.82; compCheck *= 1.06; }
            var pmMomentPart = 0.72 * pmCheck + 0.04 * Math.sin(0.08 * x);
            var pmAxialPart  = pmCheck - pmMomentPart;
            var d = disp(lc, x, y, pos[2]);
            return [
                base * Math.cos(0.05 * x) + jmp,
                base * Math.sin(0.06 * y) + jmp,
                base * Math.sin(0.04 * (x + y)) + jmp,
                0.6 * base + 0.4 * x + jmp,
                0.6 * base + 0.4 * y + jmp,
                base * Math.cos(0.03 * x) * Math.cos(0.03 * y) + jmp,
                base * Math.sin(0.03 * x) * Math.sin(0.03 * y) + jmp,
                0.5 * base * Math.sin(0.05 * (x - y)) + jmp,
                d[0], d[1], d[2], d[3], d[4], d[5],
                pmCheck, shearCheck, compCheck,
                pmMomentPart, pmAxialPart
            ];
        }

        var plane = nElements * maxCorners * cornerComponents;
        for (var e = 0; e < nElements; e++) {
            var el = elements[e];
            for (var lc = 0; lc < 2; lc++) {
                for (var k = 0; k < maxCorners; k++) {
                    var b = lc * plane + e * maxCorners * cornerComponents + k * cornerComponents;
                    if (k >= el.ncount || e === nanElem) {
                        for (var cc = 0; cc < cornerComponents; cc++) field[b + cc] = NaN;
                    } else {
                        var vals = corner(lc, e, nodePos(el.nodes[k]));
                        for (var c2 = 0; c2 < cornerComponents; c2++) field[b + c2] = vals[c2];
                    }
                }
            }
            for (var k2 = 0; k2 < maxCorners; k2++) {
                var rec = e * maxCorners * cornerComponents + k2 * cornerComponents;
                for (var c3 = 0; c3 < cornerComponents; c3++) {
                    field[2 * plane + rec + c3] = Math.max(field[rec + c3], field[plane + rec + c3]);
                }
            }
        }

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
                    strength[cb]     = 800 + 400 * (1 - cp[0] / SX) + 100 * cp[1] / SY;
                    strength[cb + 1] = 1500 - 500 * cp[1] / SY;
                    strength[cb + 2] = 1200 + 300 * Math.sin(Math.PI * cp[0] / SX);
                }
            }
        }

        return {
            variant: variant, vScale: vScale,
            nodesX: nodesX, nodesY: nodesY, nNodes: nNodes, coords: coords,
            elements: elements, nElements: nElements,
            maxCorners: maxCorners, cornerComponents: cornerComponents, nFieldLC: nFieldLC,
            field: field, strength: strength, nStr: nStr,
            disp: disp,
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
                { name: 'PM moment part', kind: 'preDSR' },
                { name: 'PM axial part',  kind: 'preDSR' }
            ],
            displacementVector: [8, 9, 10],
            strengthComponents: [
                { name: 'Shear strength',  unit: 'psi' },
                { name: 'Moment strength', unit: 'lb-in/in' },
                { name: 'Axial strength',  unit: 'psi' }
            ]
        };
    }

    // ---- v3 (legacy) ---------------------------------------------------
    function buildSampleBlob(variant) {
        var P = buildPlate(variant);
        var nNodes = P.nNodes, nElements = P.nElements;
        var meta = {
            format: 'FEAV per-corner field',
            generator: 'sampleModel.js',
            lengthUnit: 'in',
            loadCases: P.loadCases,
            components: P.components,
            displacementVector: P.displacementVector,
            strengths: { offset: HEADER_FIELDS * 4, components: P.strengthComponents }
        };
        var metaStr = JSON.stringify(meta);
        while (metaStr.length % 4 !== 0) metaStr += ' ';
        var metaBytes = new TextEncoder().encode(metaStr);

        var headerSize = HEADER_FIELDS * 4;
        var strOffset = headerSize;
        var strLength = P.strength.length * 4;
        var metaOffset = strOffset + strLength;
        var metaLength = metaBytes.length;
        var nodesOffset = metaOffset + metaLength;
        var elemsOffset = nodesOffset + nNodes * 12;
        var nodeIdOffset = elemsOffset + nElements * 20;
        var elemIdOffset = nodeIdOffset + nNodes * 4;
        var cornerFieldOffset = elemIdOffset + nElements * 4;
        var total = cornerFieldOffset + P.field.length * 4;

        var buf = new ArrayBuffer(total);
        var dv = new DataView(buf);
        var hv = [
            MAGIC, 3, headerSize, nNodes, nElements, P.nFieldLC,
            P.cornerComponents, P.maxCorners, metaOffset, metaLength,
            nodesOffset, elemsOffset, nodeIdOffset, elemIdOffset, cornerFieldOffset
        ];
        for (var hi = 0; hi < hv.length; hi++) dv.setUint32(hi * 4, hv[hi] >>> 0, true);

        new Float32Array(buf, strOffset, P.strength.length).set(P.strength);
        new Uint8Array(buf, metaOffset, metaLength).set(metaBytes);
        var f32nodes = new Float32Array(buf, nodesOffset, nNodes * 3);
        for (var n = 0; n < nNodes * 3; n++) f32nodes[n] = P.coords[n];
        var elemU32 = new Uint32Array(buf, elemsOffset, nElements * 5);
        for (var ei = 0; ei < nElements; ei++) {
            elemU32[ei * 5] = P.elements[ei].ncount;
            for (var en = 0; en < 4; en++) elemU32[ei * 5 + 1 + en] = P.elements[ei].nodes[en] >>> 0;
        }
        var nodeIdU32 = new Uint32Array(buf, nodeIdOffset, nNodes);
        for (var ni = 0; ni < nNodes; ni++) nodeIdU32[ni] = 1000 + ni * 7;
        var elemIdU32 = new Uint32Array(buf, elemIdOffset, nElements);
        for (var eii = 0; eii < nElements; eii++) elemIdU32[eii] = 5000 + eii * 3;
        new Float32Array(buf, cornerFieldOffset, P.field.length).set(P.field);
        return new Blob([buf], { type: 'application/octet-stream' });
    }

    // ---- v4: plate + beam frame ----------------------------------------
    // Beam frame: I-section edge beams along all four plate edges (one
    // element per plate cell edge), pipe columns from the plate corners
    // down COL_H, and a RECT mid-span girder under the plate centreline.
    async function buildSampleBlobV4(variant) {
        var P = buildPlate(variant);
        var nodesX = P.nodesX;
        var vScale = P.vScale;

        // Extra nodes: 4 column bases.
        var cornerNodes = [0, NX, NY * nodesX, NY * nodesX + NX];
        var nNodes = P.nNodes + 4;
        var coords = new Float64Array(nNodes * 3);
        coords.set(P.coords);
        cornerNodes.forEach(function (cn, i) {
            var b = (P.nNodes + i) * 3;
            coords[b] = P.coords[cn * 3];
            coords[b + 1] = P.coords[cn * 3 + 1];
            coords[b + 2] = P.coords[cn * 3 + 2] - COL_H;
        });

        var sections = [
            { name: 'W12x26',   typeCode: 2, params: [12.2, 6.5, 0.38, 6.5, 0.38, 0.23] },
            { name: 'HSS8 pipe', typeCode: 4, params: [8.6, 0.5] },
            { name: 'Girder 6x10', typeCode: 1, params: [6, 10] }
        ];

        // beams: { n0, n1, sec, yAxis:[3] }
        var beams = [];
        function edge(a, b, sec, yAxis) { beams.push({ n0: a, n1: b, sec: sec, yAxis: yAxis }); }
        var UP = [0, 0, 1];
        for (var i = 0; i < NX; i++) {
            edge(i, i + 1, 0, UP);                                          // y = 0
            edge(NY * nodesX + i, NY * nodesX + i + 1, 0, UP);              // y = SY
        }
        for (var j = 0; j < NY; j++) {
            edge(j * nodesX, (j + 1) * nodesX, 0, UP);                      // x = 0
            edge(j * nodesX + NX, (j + 1) * nodesX + NX, 0, UP);            // x = SX
        }
        cornerNodes.forEach(function (cn, k) {                              // columns (top -> base)
            edge(cn, P.nNodes + k, 1, [1, 0, 0]);
        });
        var midRow = (NY / 2) * nodesX;
        for (var g = 0; g < NX; g++) edge(midRow + g, midRow + g + 1, 2, UP); // girder
        var nBeams = beams.length;

        var beamComps = [
            { name: 'Axial N',    kind: 'force', unit: 'kip' },
            { name: 'Shear Vy',   kind: 'force', unit: 'kip' },
            { name: 'Shear Vz',   kind: 'force', unit: 'kip' },
            { name: 'Torsion T',  kind: 'force', unit: 'kip-in' },
            { name: 'Moment My',  kind: 'force', unit: 'kip-in' },
            { name: 'Moment Mz',  kind: 'force', unit: 'kip-in' },
            { name: 'Translation X', kind: 'displacement', unit: 'in' },
            { name: 'Translation Y', kind: 'displacement', unit: 'in' },
            { name: 'Translation Z', kind: 'displacement', unit: 'in' }
        ];
        var nbc = beamComps.length, slots = 2, nLC = P.nFieldLC;
        var bplane = nBeams * slots * nbc;
        var bfield = new Float32Array(nLC * bplane);
        function beamVals(lc, bm, end) {
            var ni = end === 0 ? bm.n0 : bm.n1;
            var x = coords[ni * 3], y = coords[ni * 3 + 1], z = coords[ni * 3 + 2];
            var s = (lc === 1 ? 0.8 : 1.0) * vScale;
            var t = end;                                                    // 0 at A, 1 at B
            var d = P.disp(lc, x, y, z);
            var col = bm.sec === 1;
            return [
                col ? -s * (40 + 20 * t) : s * 6 * Math.cos(Math.PI * x / SX),
                s * 3 * Math.sin(Math.PI * y / SY) * (1 - 2 * t),
                s * (col ? 4 : 8) * Math.sin(Math.PI * x / SX) * (0.5 - t),
                s * 2 * Math.sin(Math.PI * (x + y) / (SX + SY)),
                s * (col ? 300 * (1 - t) : 120 * Math.sin(Math.PI * x / SX) * Math.sin(Math.PI * y / SY)) + s * 30 * t,
                s * (col ? 90 * t : 60 * Math.cos(Math.PI * y / SY)) - s * 15 * t,
                d[0], d[1], d[2]
            ];
        }
        for (var b = 0; b < nBeams; b++) {
            for (var lc = 0; lc < 2; lc++) {
                for (var e2 = 0; e2 < slots; e2++) {
                    var vals = beamVals(lc, beams[b], e2);
                    var base = lc * bplane + (b * slots + e2) * nbc;
                    for (var c = 0; c < nbc; c++) bfield[base + c] = vals[c];
                }
            }
            for (var e3 = 0; e3 < slots; e3++) {
                var r = (b * slots + e3) * nbc;
                for (var c4 = 0; c4 < nbc; c4++) {
                    bfield[2 * bplane + r + c4] = Math.max(bfield[r + c4], bfield[bplane + r + c4]);
                }
            }
        }

        // ---- pack --------------------------------------------------------
        var w = FEAv4Writer.create();
        var G = FEAv4.GLOBAL_DOMAIN;
        w.addBlock('NODE', G, coords.buffer, { count: nNodes });
        var nodeIds = new Uint32Array(nNodes);
        for (var ni2 = 0; ni2 < nNodes; ni2++) nodeIds[ni2] = 1000 + ni2 * 7;
        w.addBlock('NDID', G, nodeIds.buffer, { count: nNodes });

        var REC = FEAv4.ELEM_RECORD_U32;
        var selems = new Uint32Array(P.nElements * REC);
        for (var ei = 0; ei < P.nElements; ei++) {
            selems[ei * REC] = P.elements[ei].ncount;
            for (var k = 0; k < 4; k++) selems[ei * REC + 1 + k] = P.elements[ei].nodes[k] >>> 0;
            selems[ei * REC + 5] = 0;
        }
        var selemIds = new Uint32Array(P.nElements);
        for (var si = 0; si < P.nElements; si++) selemIds[si] = 5000 + si * 3;
        w.addBlock('ELEM', 0, selems.buffer, { count: P.nElements });
        w.addBlock('ELID', 0, selemIds.buffer, { count: P.nElements });
        w.addBlock('FLDS', 0, P.field.buffer, { count: nLC });
        w.addBlock('FLDC', 0, P.strength.buffer, { count: 1 });

        var belems = new Uint32Array(nBeams * REC);
        var belemIds = new Uint32Array(nBeams);
        var bprp = new Float32Array(nBeams * FEAv4.BPRP_F32);
        beams.forEach(function (bm, bi) {
            belems[bi * REC] = 2;
            belems[bi * REC + 1] = bm.n0;
            belems[bi * REC + 2] = bm.n1;
            belems[bi * REC + 3] = bm.sec;
            belemIds[bi] = 20000 + bi;
            var o = bi * FEAv4.BPRP_F32;
            bprp[o] = bm.yAxis[0]; bprp[o + 1] = bm.yAxis[1]; bprp[o + 2] = bm.yAxis[2];
        });
        w.addBlock('ELEM', 1, belems.buffer, { count: nBeams });
        w.addBlock('ELID', 1, belemIds.buffer, { count: nBeams });
        w.addBlock('FLDS', 1, bfield.buffer, { count: nLC });
        w.addBlock('BPRP', 1, bprp.buffer, { count: nBeams });
        w.addBlock('SECT', G, FEAv4Writer.encodeSections(sections), { count: sections.length });

        w.setMeta({
            generator: 'sampleModel.js v4',
            modelId: 'sample/plate-frame/variant' + (variant || 0),
            units: { length: 'in', force: 'kip' },
            loadCases: P.loadCases,
            domains: [
                { name: 'shells', family: 'shell', maxSlots: 4,
                  components: P.components,
                  constComponents: P.strengthComponents.map(function (c) {
                      return { name: c.name, kind: 'str', unit: c.unit };
                  }),
                  displacementVector: P.displacementVector },
                { name: 'beams', family: 'beam', maxSlots: 2,
                  components: beamComps, displacementVector: [6, 7, 8] }
            ],
            sections: sections.map(function (s) { return { name: s.name, type: FEAv4.SECTION_TYPES[s.typeCode].name }; })
        });
        return w.toBlobAsync();
    }

    function downloadBlob(blob, filename) {
        var url = URL.createObjectURL(blob);
        var a = document.createElement('a');
        a.href = url;
        a.download = filename;
        document.body.appendChild(a);
        a.click();
        document.body.removeChild(a);
        setTimeout(function () { URL.revokeObjectURL(url); }, 0);
    }

    function downloadSampleBin(filename) {
        var blob = buildSampleBlob();
        downloadBlob(blob, filename || 'sampleModel.bin');
        return blob;
    }

    async function downloadSampleBinV4(filename) {
        var blob = await buildSampleBlobV4();
        downloadBlob(blob, filename || 'sampleModel_v4.bin');
        return blob;
    }

    return {
        buildPlate: buildPlate,
        buildSampleBlob: buildSampleBlob,
        buildSampleBlobV4: buildSampleBlobV4,
        downloadSampleBin: downloadSampleBin,
        downloadSampleBinV4: downloadSampleBinV4
    };
})();
