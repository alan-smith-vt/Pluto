// ================================================================
// v4Reader.js
// Reads a Pluto v4 binary: 32-byte header -> block directory ->
// blocks addressed by (tag, domain). Spec: vault/format/v4-schema.md.
//
// Produces the UNIFIED in-memory model (schema §10):
//   { version, file, nNodes, nodes(Float64Array), nodeIds, loadCases,
//     sections, meta, domains:[{ name, family, maxSlots, components,
//     constComponents, dispVector, nElem, elemRecordU32, elems,
//     elemIds, fields:{offset,nLC,planeStride}|null,
//     constFields:{offset,nComp}|null, beamProps|null }] }
//
// Offsets are u64 in the file; all arithmetic here is plain Number
// (exact to 2^53). NEVER use bitwise ops on offsets.
// ================================================================

var FEAv4 = (function () {

    var MAGIC = 0x46454156;            // 'FEAV'
    var VERSION = 4;
    var HEADER_BYTES = 32;
    var DIR_ENTRY_BYTES = 32;
    var GLOBAL_DOMAIN = 0xFFFFFFFF;
    var FLAG_APPEND = 1;

    var TAGS = ['META', 'NODE', 'NDID', 'ELEM', 'ELID', 'FLDS', 'FLDC', 'SECT', 'BPRP'];
    var ELEM_RECORD_U32 = 6;           // both families, schema §4.2
    var BPRP_F32 = 8;

    var SECTION_TYPES = {
        1: { name: 'RECT', params: ['b', 'h'] },
        2: { name: 'I',    params: ['d', 'bfTop', 'tfTop', 'bfBot', 'tfBot', 'tw'] },
        3: { name: 'BOX',  params: ['b', 'h', 't'] },
        4: { name: 'PIPE', params: ['od', 't'] },
        5: { name: 'L',    params: ['b', 'h', 't'] },
        6: { name: 'C',    params: ['d', 'bf', 'tf', 'tw'] },
        7: { name: 'T',    params: ['d', 'bf', 'tf', 'tw'] },
        100: { name: 'POLY', params: [] }
    };

    function tagToU32(s) {
        return (s.charCodeAt(0)) + (s.charCodeAt(1) * 256) +
               (s.charCodeAt(2) * 65536) + (s.charCodeAt(3) * 16777216);
    }
    function u32ToTag(v) {
        return String.fromCharCode(v % 256, Math.floor(v / 256) % 256,
            Math.floor(v / 65536) % 256, Math.floor(v / 16777216) % 256);
    }
    function getU64(dv, off) {
        return dv.getUint32(off, true) + dv.getUint32(off + 4, true) * 4294967296;
    }

    function readRange(file, byteStart, byteLength) {
        return file.slice(byteStart, byteStart + byteLength).arrayBuffer();
    }

    function requireWithin(file, name, offset, length) {
        if (offset < HEADER_BYTES || length < 0 || offset + length > file.size) {
            throw new Error('Block layout mismatch: the ' + name + ' block [' +
                offset + ', ' + (offset + length) + ') does not fit inside the ' +
                file.size + '-byte file. The writer does not match this reader.');
        }
    }

    // ---- header + directory ------------------------------------------
    function parseHeader(buf) {
        var dv = new DataView(buf);
        return {
            magic: dv.getUint32(0, true),
            version: dv.getUint32(4, true),
            headerSize: dv.getUint32(8, true),
            nBlocks: dv.getUint32(12, true),
            dirOffset: getU64(dv, 16)
        };
    }

    // Returns { entries:[...], byKey:{ 'TAG:domain' -> entry } } -- last wins.
    function parseDirectory(buf, nBlocks, log) {
        var dv = new DataView(buf);
        var entries = [], byKey = {};
        for (var i = 0; i < nBlocks; i++) {
            var o = i * DIR_ENTRY_BYTES;
            var en = {
                tag: u32ToTag(dv.getUint32(o, true)),
                domain: dv.getUint32(o + 4, true),
                offset: getU64(dv, o + 8),
                length: getU64(dv, o + 16),
                count: dv.getUint32(o + 24, true),
                flags: dv.getUint32(o + 28, true)
            };
            if (TAGS.indexOf(en.tag) < 0) {
                log('Skipping unknown block "' + en.tag + '" (' + en.length + ' bytes).');
            }
            entries.push(en);
            byKey[en.tag + ':' + en.domain] = en;
        }
        return { entries: entries, byKey: byKey };
    }

    // Start of the next block after `offset` (or EOF) -- for APPEND blocks.
    function nextBlockStart(entries, offset, fileSize) {
        var best = fileSize;
        for (var i = 0; i < entries.length; i++) {
            var o = entries[i].offset;
            if (o > offset && o < best) best = o;
        }
        return best;
    }

    // ---- META normalisation ------------------------------------------
    function normalizeComponents(list, fallbackPrefix, n) {
        var out = Array.isArray(list) ? list.slice() : [];
        var len = (typeof n === 'number') ? n : out.length;
        for (var i = 0; i < len; i++) {
            out[i] = out[i] || {};
            if (!out[i].name) out[i].name = fallbackPrefix + ' ' + i;
            if (!out[i].kind) out[i].kind = 'unknown';
        }
        out.length = len;
        return out;
    }

    function normalizeLoadCases(list, nLC) {
        var lcs = Array.isArray(list) ? list.slice() : [];
        for (var i = 0; i < nLC; i++) {
            if (!lcs[i]) lcs[i] = { name: 'LC ' + (i + 1), type: 'primary' };
            if (!lcs[i].type) lcs[i].type = 'primary';
        }
        lcs.length = nLC;
        return lcs;
    }

    // Same rules as v3 (explicit displacementVector, else name matching).
    function resolveDispVector(explicit, comps) {
        if (Array.isArray(explicit) && explicit.length === 3) {
            var ok = explicit.every(function (i, n) {
                return typeof i === 'number' && i >= 0 && i < comps.length &&
                       explicit.indexOf(i) === n;
            });
            if (ok) return explicit.slice(0, 3);
        }
        var axes = ['x', 'y', 'z'];
        var found = [-1, -1, -1];
        for (var c = 0; c < comps.length; c++) {
            if (comps[c].kind !== 'displacement') continue;
            var name = String(comps[c].name || '').toLowerCase();
            for (var a = 0; a < 3; a++) {
                var ax = axes[a];
                if (new RegExp('^(trans(lation)?[ _-]?' + ax + '|[udt]' + ax + ')$').test(name) ||
                    new RegExp('^trans(lation)?\\b.*\\b' + ax + '$').test(name)) {
                    if (found[a] === -1) found[a] = c;
                }
            }
        }
        return (found[0] >= 0 && found[1] >= 0 && found[2] >= 0) ? found : null;
    }

    // ---- SECT decode -------------------------------------------------
    function decodeSections(buf, names) {
        var dv = new DataView(buf);
        var n = dv.getUint32(0, true);
        var p = 4;
        var out = [];
        for (var i = 0; i < n; i++) {
            var type = dv.getUint32(p, true); p += 4;
            var nParams = dv.getUint32(p, true); p += 4;
            var def = SECTION_TYPES[type] || { name: 'UNKNOWN' + type, params: [] };
            var sec = { index: i, typeCode: type, type: def.name, params: {}, paramList: [] };
            for (var k = 0; k < nParams; k++) {
                var v = dv.getFloat32(p, true); p += 4;
                sec.paramList.push(v);
                if (def.params[k]) sec.params[def.params[k]] = v;
            }
            if (type === 100) {
                var nPts = dv.getUint32(p, true); p += 4;
                sec.points = [];
                for (var q = 0; q < nPts; q++) {
                    sec.points.push([dv.getFloat32(p, true), dv.getFloat32(p + 4, true)]);
                    p += 8;
                }
            }
            sec.name = (names && names[i] && names[i].name) || (def.name + ' ' + i);
            out.push(sec);
        }
        return out;
    }

    // ---- main --------------------------------------------------------
    async function loadModel(file, log) {
        log = log || function () {};
        if (file.size < HEADER_BYTES) {
            throw new Error('File is only ' + file.size + ' bytes -- too small for a v4 header.');
        }
        var header = parseHeader(await readRange(file, 0, HEADER_BYTES));
        if (header.magic !== MAGIC) {
            throw new Error('Not a FEAV file (magic 0x' + (header.magic >>> 0).toString(16) + ').');
        }
        if (header.version !== VERSION) {
            throw new Error('v4Reader given a v' + header.version + ' file.');
        }
        if (header.headerSize !== HEADER_BYTES) {
            log('Warning: headerSize is ' + header.headerSize + ' (expected ' + HEADER_BYTES + ').');
        }
        var dirLen = header.nBlocks * DIR_ENTRY_BYTES;
        requireWithin(file, 'directory', header.dirOffset, dirLen);
        var dir = parseDirectory(await readRange(file, header.dirOffset, dirLen), header.nBlocks, log);
        var K = dir.byKey;

        function need(tag, domain) {
            var en = K[tag + ':' + domain];
            if (!en) throw new Error('Required block ' + tag +
                (domain === GLOBAL_DOMAIN ? '' : ' (domain ' + domain + ')') + ' is missing.');
            requireWithin(file, tag, en.offset, en.length);
            return en;
        }
        function opt(tag, domain) {
            var en = K[tag + ':' + domain];
            if (!en) return null;
            requireWithin(file, tag, en.offset, en.length);
            return en;
        }

        // META first: it declares the domains that index every other block.
        var metaEn = need('META', GLOBAL_DOMAIN);
        var meta = {};
        try {
            meta = JSON.parse(new TextDecoder('utf-8').decode(
                new Uint8Array(await readRange(file, metaEn.offset, metaEn.length))));
        } catch (err) {
            log('Metadata parse failed (' + err.message + '); using defaults.');
        }
        var domainDescs = Array.isArray(meta.domains) ? meta.domains : [];
        if (domainDescs.length === 0) throw new Error('META declares no domains.');

        // Global geometry.
        var nodeEn = need('NODE', GLOBAL_DOMAIN);
        var nNodes = Math.floor(nodeEn.length / 24);
        var nodes = new Float64Array(await readRange(file, nodeEn.offset, nNodes * 24));
        var ndidEn = opt('NDID', GLOBAL_DOMAIN);
        var nodeIds;
        if (ndidEn) {
            nodeIds = new Uint32Array(await readRange(file, ndidEn.offset, nNodes * 4));
        } else {
            nodeIds = new Uint32Array(nNodes);
            for (var i = 0; i < nNodes; i++) nodeIds[i] = i + 1;
        }

        // Sections (global, optional).
        var sections = [];
        var sectEn = opt('SECT', GLOBAL_DOMAIN);
        if (sectEn) {
            sections = decodeSections(await readRange(file, sectEn.offset, sectEn.length), meta.sections);
        }

        // Load-case count: taken from the largest FLDS count seen (all
        // domains are supposed to agree; disagreement is logged).
        var nLC = 0;
        var domains = [];
        for (var d = 0; d < domainDescs.length; d++) {
            var desc = domainDescs[d] || {};
            var elemEn = need('ELEM', d);
            var nElem = elemEn.count > 0 ? elemEn.count : Math.floor(elemEn.length / (ELEM_RECORD_U32 * 4));
            var recU32 = nElem > 0 ? Math.round(elemEn.length / (nElem * 4)) : ELEM_RECORD_U32;
            var elems = new Uint32Array(await readRange(file, elemEn.offset, nElem * recU32 * 4));
            var elidEn = opt('ELID', d);
            var elemIds;
            if (elidEn) {
                elemIds = new Uint32Array(await readRange(file, elidEn.offset, nElem * 4));
            } else {
                elemIds = new Uint32Array(nElem);
                for (var e = 0; e < nElem; e++) elemIds[e] = e + 1;
            }

            var maxSlots = desc.maxSlots || (desc.family === 'beam' ? 2 : 4);
            var comps = normalizeComponents(desc.components, 'Component');
            var constComps = normalizeComponents(desc.constComponents, 'Const');

            var fields = null;
            var fldEn = opt('FLDS', d);
            if (fldEn && comps.length > 0) {
                var planeStride = nElem * maxSlots * comps.length * 4;
                var count = fldEn.count;
                var avail = ((fldEn.flags & FLAG_APPEND) || fldEn.length === 0)
                    ? nextBlockStart(dir.entries, fldEn.offset, file.size) - fldEn.offset
                    : fldEn.length;
                var derived = planeStride > 0 ? Math.floor(avail / planeStride) : 0;
                if ((fldEn.flags & FLAG_APPEND) || count === 0 || count > derived) {
                    if (count !== derived) {
                        log('Domain ' + d + ': FLDS count ' + count + ', file holds ' +
                            derived + ' plane(s). Using ' + derived + '.');
                    }
                    count = derived;
                }
                requireWithin(file, 'FLDS (domain ' + d + ')', fldEn.offset, count * planeStride);
                fields = { offset: fldEn.offset, nLC: count, planeStride: planeStride };
                if (count > nLC) nLC = count;
            }

            var constFields = null;
            var fldcEn = opt('FLDC', d);
            if (fldcEn && constComps.length > 0) {
                var cLen = nElem * maxSlots * constComps.length * 4;
                requireWithin(file, 'FLDC (domain ' + d + ')', fldcEn.offset, cLen);
                constFields = { offset: fldcEn.offset, nComp: constComps.length, byteLength: cLen };
            }

            var beamProps = null;
            var bprpEn = opt('BPRP', d);
            if (bprpEn) {
                beamProps = new Float32Array(await readRange(file, bprpEn.offset, nElem * BPRP_F32 * 4));
            }

            domains.push({
                index: d,
                name: desc.name || (desc.family || 'domain') + ' ' + d,
                family: desc.family || 'shell',
                maxSlots: maxSlots,
                components: comps,
                constComponents: constComps,
                dispVector: resolveDispVector(desc.displacementVector, comps),
                nElem: nElem,
                elemRecordU32: recU32,
                elems: elems,
                elemIds: elemIds,
                fields: fields,
                constFields: constFields,
                beamProps: beamProps,
                constData: null                 // cache, filled by readConst
            });
        }

        var loadCases = normalizeLoadCases(meta.loadCases, nLC);

        log('v4: ' + nNodes + ' nodes, ' + domains.map(function (dm) {
            return dm.nElem + ' ' + dm.family + (dm.nElem === 1 ? '' : 's');
        }).join(' + ') + ', ' + nLC + ' LC(s)' +
            (sections.length ? ', ' + sections.length + ' section(s)' : '') + '.');

        return {
            version: 4,
            file: file,
            nNodes: nNodes,
            nodes: nodes,
            nodeIds: nodeIds,
            loadCases: loadCases,
            sections: sections,
            meta: meta,
            modelId: meta.modelId || null,
            geometryHash: meta.geometryHash || null,
            units: meta.units || null,
            domains: domains,
            directory: dir.entries
        };
    }

    return {
        MAGIC: MAGIC,
        VERSION: VERSION,
        HEADER_BYTES: HEADER_BYTES,
        DIR_ENTRY_BYTES: DIR_ENTRY_BYTES,
        GLOBAL_DOMAIN: GLOBAL_DOMAIN,
        FLAG_APPEND: FLAG_APPEND,
        ELEM_RECORD_U32: ELEM_RECORD_U32,
        BPRP_F32: BPRP_F32,
        SECTION_TYPES: SECTION_TYPES,
        tagToU32: tagToU32,
        u32ToTag: u32ToTag,
        readRange: readRange,
        loadModel: loadModel,
        resolveDispVector: resolveDispVector
    };
})();
