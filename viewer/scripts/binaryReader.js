// ================================================================
// binaryReader.js
// Reads the FEA field binary: header + geometry + ID tables +
// metadata up front, individual load-case (LC) field blocks on
// demand.
//
// CRITICAL: all offset/length arithmetic is plain JS Number
// (IEEE-754 double, exact to 2^53). NEVER use bitwise ops on offsets
// (| << >>> &) -- they truncate to 32 bits and silently wrap >4GB.
//
// We never call file.arrayBuffer() on the whole file; the File handle
// is lazy and the whole-file materialization is what hits the ~2GB
// single-ArrayBuffer ceiling. We slice exactly the bytes we need.
//
// ---- Header (v3): 15 little-endian uint32, 60 bytes --------------
//   off  field
//   0    magic              0x46454156
//   4    version            3
//   8    headerSize         60
//   12   nNodes
//   16   nElements
//   20   nFieldLC           load cases, envelopes included
//   24   cornerComponents   scalar components per corner (e.g. 14)
//   28   maxCorners         4  (fixed stride; tris NaN-pad slot 4)
//   32   metaOffset         -> UTF-8 JSON metadata
//   36   metaLength
//   40   nodesOffset        -> float32[nNodes][3]  x,y,z
//   44   elemsOffset        -> uint32[nElements][5]  ncount,id1..id4
//   48   nodeIdOffset       -> uint32[nNodes]   real (sparse) node IDs
//   52   elemIdOffset       -> uint32[nElements] real (sparse) elem IDs
//   56   cornerFieldOffset  -> float32[nFieldLC][nElements][maxCorners][cornerComponents]
// ================================================================

var FEABinary = (function () {

    var HEADER_FIELDS = [
        'magic', 'version', 'headerSize', 'nNodes', 'nElements', 'nFieldLC',
        'cornerComponents', 'maxCorners', 'metaOffset', 'metaLength',
        'nodesOffset', 'elemsOffset', 'nodeIdOffset', 'elemIdOffset',
        'cornerFieldOffset'
    ];
    var HEADER_BYTES = HEADER_FIELDS.length * 4;   // 60
    var MAGIC = 0x46454156;                        // 'FEAV'
    var SUPPORTED_VERSION = 3;
    var ELEM_RECORD_U32 = 5;                       // ncount + 4 node indices

    // Slice [byteStart, byteStart+byteLength) -- Number math only, no |0.
    function readRange(file, byteStart, byteLength) {
        var blob = file.slice(byteStart, byteStart + byteLength);
        return blob.arrayBuffer();
    }

    function parseHeaderBuffer(buf) {
        var dv = new DataView(buf);
        var h = {};
        for (var i = 0; i < HEADER_FIELDS.length; i++) {
            h[HEADER_FIELDS[i]] = dv.getUint32(i * 4, true);
        }
        return h;
    }

    function decodeMeta(buf, header, log) {
        var meta = null;
        if (buf && buf.byteLength > 0) {
            try {
                var text = new TextDecoder('utf-8').decode(new Uint8Array(buf));
                meta = JSON.parse(text);
            } catch (err) {
                if (log) log('Metadata parse failed (' + err.message + '); using default labels.');
                meta = null;
            }
        }
        return normalizeMeta(meta, header);
    }

    // Guarantee meta.loadCases / meta.components are arrays of the exact
    // length the header declares, with usable fallback labels. Components
    // may carry an optional `unit` string (passed through untouched).
    function normalizeMeta(meta, header) {
        meta = meta || {};
        var lcs = Array.isArray(meta.loadCases) ? meta.loadCases.slice() : [];
        for (var i = 0; i < header.nFieldLC; i++) {
            if (!lcs[i]) lcs[i] = { name: 'LC ' + (i + 1), type: 'primary' };
            if (!lcs[i].type) lcs[i].type = 'primary';
        }
        lcs.length = header.nFieldLC;

        var comps = Array.isArray(meta.components) ? meta.components.slice() : [];
        for (var j = 0; j < header.cornerComponents; j++) {
            if (!comps[j]) comps[j] = { name: 'Component ' + j, kind: 'unknown' };
            if (!comps[j].kind) comps[j].kind = 'unknown';
        }
        comps.length = header.cornerComponents;

        return {
            loadCases: lcs,
            components: comps,
            dispVector: resolveDispVector(meta, comps),
            strengths: normalizeStrengths(meta),
            raw: meta
        };
    }

    // Optional LC-independent design-strength block, declared purely in the
    // metadata (no header change, no version bump -- viewers that don't
    // know the key ignore it, files without it read as before):
    //   meta.strengths = { offset: <byte offset>,
    //                       components: [ {name, unit?}, ... ] }
    // Block layout: float32[nElements][maxCorners][nStrComponents],
    // same slot conventions as the corner field (tri pad slot NaN).
    // Returns { offset, components:[{name, kind:'str', unit?}] }
    // or null.
    function normalizeStrengths(meta) {
        var s = meta && meta.strengths;
        if (!s || typeof s.offset !== 'number' || s.offset < 0 ||
            !Array.isArray(s.components) || s.components.length === 0) {
            return null;
        }
        var comps = s.components.map(function (c, i) {
            c = c || {};
            return {
                name: c.name || ('Strength ' + i),
                kind: 'str',
                unit: c.unit
            };
        });
        return { offset: s.offset, components: comps };
    }

    // Which 3 component indices form the translation vector (for the
    // deformed-shape view). Preferred: explicit meta.displacementVector
    // = [ix, iy, iz]. Fallback: match displacement-kind component names
    // ("Translation X", "UX", "DX", "TX", ...). Returns [ix,iy,iz] or
    // null if no unambiguous triple exists.
    function resolveDispVector(meta, comps) {
        var dv = meta && meta.displacementVector;
        if (Array.isArray(dv) && dv.length === 3) {
            var ok = dv.every(function (i, n) {
                return typeof i === 'number' && i >= 0 && i < comps.length &&
                       dv.indexOf(i) === n;   // integer-ish, in range, distinct
            });
            if (ok) return dv.slice(0, 3);
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

    // Byte stride of a single LC field block.
    function lcByteStride(header) {
        return header.nElements * header.maxCorners * header.cornerComponents * 4;
    }

    // Throw a precise error if a block falls outside the file -- this
    // catches a writer whose header layout does not match this reader.
    function requireWithin(file, name, offset, length) {
        if (offset < HEADER_BYTES || length < 0 || offset + length > file.size) {
            throw new Error('Header layout mismatch: the ' + name + ' block [' +
                offset + ', ' + (offset + length) + ') does not fit inside the ' +
                file.size + '-byte file. The writer header does not match this viewer.');
        }
    }

    // Parse header + load all resident (small) blocks. Returns a model.
    // `file` may be a File or a Blob -- both expose slice()/arrayBuffer().
    async function loadModel(file, log) {
        log = log || function () {};

        if (file.size < HEADER_BYTES) {
            throw new Error('File is only ' + file.size + ' bytes -- too small for a ' +
                HEADER_BYTES + '-byte header.');
        }

        var headBuf = await readRange(file, 0, Math.min(256, file.size));
        var header = parseHeaderBuffer(headBuf);

        if (header.magic !== MAGIC) {
            throw new Error('Not a recognized FEA field file: header magic is 0x' +
                (header.magic >>> 0).toString(16) + ', expected 0x' +
                MAGIC.toString(16) + '.');
        }
        if (header.version !== SUPPORTED_VERSION) {
            log('Warning: file format version ' + header.version +
                ' (viewer expects v' + SUPPORTED_VERSION + ').');
        }
        if (header.headerSize !== HEADER_BYTES) {
            log('Warning: headerSize is ' + header.headerSize +
                ' (expected ' + HEADER_BYTES + ').');
        }

        var stride = lcByteStride(header);
        if (stride > 2.0e9) {
            log('Warning: single-LC field block is ' + (stride / 1e9).toFixed(2) +
                ' GB -- near the ~2GB per-slice ceiling.');
        }

        // Metadata first: the design-strength block (if any) is declared there
        // and affects how the LC count is derived below.
        requireWithin(file, 'metadata', header.metaOffset, header.metaLength);
        var metaBuf = await readRange(file, header.metaOffset, header.metaLength);
        var meta = decodeMeta(metaBuf, header, log);

        // Append-mode: derive LC count from file size so the writer can
        // stream-append LCs without knowing nFieldLC upfront or patching
        // the header. The header value is treated as a hint. A strength
        // block placed AFTER the LC planes caps the LC region instead of
        // being miscounted as field data (the recommended layout puts it
        // before cornerFieldOffset, where this doesn't matter).
        var lcRegionEnd = file.size;
        if (meta.strengths && meta.strengths.offset > header.cornerFieldOffset) {
            lcRegionEnd = meta.strengths.offset;
        }
        var availableLCs = Math.max(0,
            Math.floor((lcRegionEnd - header.cornerFieldOffset) / stride));
        if (availableLCs !== header.nFieldLC) {
            log('Header nFieldLC=' + header.nFieldLC + '; file contains ' +
                availableLCs + ' LC(s). Using ' + availableLCs + '.');
            header.nFieldLC = availableLCs;
            meta = normalizeMeta(meta.raw, header);   // re-trim load-case list
        }

        // Validate every block before reading -- fail clearly, not cryptically.
        requireWithin(file, 'node coords', header.nodesOffset, header.nNodes * 3 * 4);
        requireWithin(file, 'element table', header.elemsOffset, header.nElements * ELEM_RECORD_U32 * 4);
        requireWithin(file, 'node IDs', header.nodeIdOffset, header.nNodes * 4);
        requireWithin(file, 'element IDs', header.elemIdOffset, header.nElements * 4);
        requireWithin(file, 'corner field', header.cornerFieldOffset, header.nFieldLC * stride);
        if (meta.strengths) {
            var strLen = strengthByteLength(header, meta.strengths);
            try {
                requireWithin(file, 'design-strength block', meta.strengths.offset, strLen);
            } catch (err) {
                log('Design-strength block invalid (' + err.message + '); ignoring strengths.');
                meta.strengths = null;
            }
        }

        log('Header: ' + header.nNodes + ' nodes, ' + header.nElements +
            ' elements, ' + header.nFieldLC + ' field LCs, ' +
            header.cornerComponents + ' components/corner' +
            (meta.strengths ? ', ' + meta.strengths.components.length + ' design-strength component(s)' : '') +
            '.');

        // Geometry + ID tables -- small, kept resident.
        var nodesBuf = await readRange(file, header.nodesOffset, header.nNodes * 3 * 4);
        var elemsBuf = await readRange(file, header.elemsOffset, header.nElements * ELEM_RECORD_U32 * 4);
        var nIdBuf   = await readRange(file, header.nodeIdOffset, header.nNodes * 4);
        var eIdBuf   = await readRange(file, header.elemIdOffset, header.nElements * 4);

        return {
            file: file,
            header: header,
            meta: meta,
            nodes: new Float32Array(nodesBuf),     // [nNodes][3] x,y,z
            elems: new Uint32Array(elemsBuf),      // [nElements][ncount,id1..id4]
            nodeIds: new Uint32Array(nIdBuf),      // real (sparse) node IDs
            elemIds: new Uint32Array(eIdBuf),      // real (sparse) element IDs
            lcByteStride: stride,
            strData: null                          // lazily filled by readStrengths
        };
    }

    function strengthByteLength(header, strengths) {
        return header.nElements * header.maxCorners * strengths.components.length * 4;
    }

    // Read the (LC-independent) design-strength block, cached on the model.
    // Returns Float32Array[nElements][maxCorners][nStrComponents],
    // or null if the file has no design strengths.
    async function readStrengths(model) {
        if (!model.meta.strengths) return null;
        if (model.strData) return model.strData;
        var s = model.meta.strengths;
        var buf = await readRange(model.file, s.offset,
            strengthByteLength(model.header, s));
        model.strData = new Float32Array(buf);
        return model.strData;
    }

    // Slice exactly one LC's per-corner field block (stress + displacement
    // components interleaved). Caller keeps at most the current LC resident.
    async function readLC(model, lc) {
        var h = model.header;
        if (lc < 0 || lc >= h.nFieldLC) throw new Error('LC index out of range: ' + lc);
        // Number math -- lc*stride can exceed 4GB and must not be truncated.
        var offset = h.cornerFieldOffset + lc * model.lcByteStride;
        var buf = await readRange(model.file, offset, model.lcByteStride);
        return new Float32Array(buf);   // [nElements][maxCorners][cornerComponents]
    }

    // Strided index into a resident LC Float32Array.
    function fieldIndex(header, elem, corner, comp) {
        var cc = header.cornerComponents;
        return elem * header.maxCorners * cc + corner * cc + comp;
    }

    // Slice ONE element's record from ONE LC: a few-hundred-byte read at
    // a directly computable offset. This is what makes the calc-review
    // card possible without keeping any non-current LC resident.
    // Returns Float32Array[maxCorners][cornerComponents].
    async function readElementRecord(model, lc, elem) {
        var h = model.header;
        if (lc < 0 || lc >= h.nFieldLC) throw new Error('LC index out of range: ' + lc);
        if (elem < 0 || elem >= h.nElements) throw new Error('Element index out of range: ' + elem);
        var recFloats = h.maxCorners * h.cornerComponents;
        var offset = h.cornerFieldOffset + lc * model.lcByteStride + elem * recFloats * 4;
        var buf = await readRange(model.file, offset, recFloats * 4);
        return new Float32Array(buf);
    }

    return {
        MAGIC: MAGIC,
        HEADER_BYTES: HEADER_BYTES,
        HEADER_FIELDS: HEADER_FIELDS,
        ELEM_RECORD_U32: ELEM_RECORD_U32,
        SUPPORTED_VERSION: SUPPORTED_VERSION,
        readRange: readRange,
        loadModel: loadModel,
        readLC: readLC,
        readElementRecord: readElementRecord,
        readStrengths: readStrengths,
        strengthByteLength: strengthByteLength,
        lcByteStride: lcByteStride,
        fieldIndex: fieldIndex,
        normalizeMeta: normalizeMeta
    };
})();
