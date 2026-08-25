// ================================================================
// v4Writer.js
// Minimal in-browser v4 block writer (vault/format/v4-schema.md).
// Used by the sample generator and by tests; the production writer is
// the C# RawViewerWriter. Builds the whole file in memory -- fine for
// demo sizes, not for multi-GB exports.
//
//   var w = FEAv4Writer.create();
//   w.addBlock('NODE', FEAv4.GLOBAL_DOMAIN, float64Array.buffer);
//   w.addBlock('ELEM', 0, u32.buffer, { count: nElem });
//   w.addBlock('FLDS', 0, f32.buffer, { count: nLC });
//   w.setMeta(obj);                 // written as the META block
//   var blob = w.toBlob();
//
// Layout produced: header | blocks in insertion order (8-byte aligned)
// | META | directory at EOF (header.dirOffset points at it).
// geometryHash is computed over NODE NDID ELEM ELID SECT BPRP bytes
// in schema order and injected into META if not already present
// (SHA-256 via crypto.subtle -> toBlobAsync; toBlob skips the hash).
// ================================================================

var FEAv4Writer = (function () {

    var HASH_ORDER = ['NODE', 'NDID', 'ELEM', 'ELID', 'SECT', 'BPRP'];

    function align8(n) { return Math.ceil(n / 8) * 8; }

    function setU64(dv, off, v) {
        dv.setUint32(off, v % 4294967296, true);
        dv.setUint32(off + 4, Math.floor(v / 4294967296), true);
    }

    function create() {
        var blocks = [];       // { tag, domain, bytes(Uint8Array), count, flags }
        var meta = {};

        function addBlock(tag, domain, buffer, opts) {
            opts = opts || {};
            var bytes = buffer instanceof Uint8Array ? buffer
                      : new Uint8Array(buffer.buffer || buffer, buffer.byteOffset || 0, buffer.byteLength);
            blocks.push({ tag: tag, domain: domain, bytes: bytes,
                          count: opts.count || 0, flags: opts.flags || 0 });
        }

        function setMeta(obj) { meta = obj || {}; }

        // Bytes hashed for geometryHash: schema order, domains ascending.
        function hashInput() {
            var parts = [];
            HASH_ORDER.forEach(function (tag) {
                blocks.filter(function (b) { return b.tag === tag; })
                      .sort(function (a, b) { return a.domain - b.domain; })
                      .forEach(function (b) { parts.push(b.bytes); });
            });
            var total = parts.reduce(function (n, p) { return n + p.byteLength; }, 0);
            var out = new Uint8Array(total), p = 0;
            parts.forEach(function (b) { out.set(b, p); p += b.byteLength; });
            return out;
        }

        function assemble() {
            var metaBytes = new TextEncoder().encode(JSON.stringify(meta));
            var all = blocks.concat([{ tag: 'META', domain: FEAv4.GLOBAL_DOMAIN,
                                       bytes: metaBytes, count: 0, flags: 0 }]);
            var off = FEAv4.HEADER_BYTES;
            var placed = all.map(function (b) {
                var entry = { tag: b.tag, domain: b.domain, offset: off,
                              length: b.bytes.byteLength, count: b.count, flags: b.flags, bytes: b.bytes };
                off = align8(off + b.bytes.byteLength);
                return entry;
            });
            var dirOffset = off;
            var total = dirOffset + placed.length * FEAv4.DIR_ENTRY_BYTES;
            var buf = new ArrayBuffer(total);
            var dv = new DataView(buf);
            var u8 = new Uint8Array(buf);
            dv.setUint32(0, FEAv4.MAGIC, true);
            dv.setUint32(4, FEAv4.VERSION, true);
            dv.setUint32(8, FEAv4.HEADER_BYTES, true);
            dv.setUint32(12, placed.length, true);
            setU64(dv, 16, dirOffset);
            setU64(dv, 24, 0);
            placed.forEach(function (b, i) {
                u8.set(b.bytes, b.offset);
                var o = dirOffset + i * FEAv4.DIR_ENTRY_BYTES;
                dv.setUint32(o, FEAv4.tagToU32(b.tag), true);
                dv.setUint32(o + 4, b.domain >>> 0, true);
                setU64(dv, o + 8, b.offset);
                setU64(dv, o + 16, b.length);
                dv.setUint32(o + 24, b.count >>> 0, true);
                dv.setUint32(o + 28, b.flags >>> 0, true);
            });
            return buf;
        }

        function toBlob() {
            return new Blob([assemble()], { type: 'application/octet-stream' });
        }

        async function toBlobAsync() {
            if (!meta.geometryHash && window.crypto && crypto.subtle) {
                var digest = await crypto.subtle.digest('SHA-256', hashInput());
                var hex = Array.prototype.map.call(new Uint8Array(digest), function (b) {
                    return ('0' + b.toString(16)).slice(-2);
                }).join('');
                meta.geometryHash = 'sha256:' + hex;
            }
            return toBlob();
        }

        return { addBlock: addBlock, setMeta: setMeta, toBlob: toBlob,
                 toBlobAsync: toBlobAsync, hashInput: hashInput };
    }

    // Encode a section list (schema §6) into a SECT block.
    // sections: [{ typeCode, params:[...], points?:[[y,z],...] }]
    function encodeSections(sections) {
        var floats = 1;
        sections.forEach(function (s) {
            floats += 2 + s.params.length;
            if (s.typeCode === 100) floats += 1 + 2 * (s.points || []).length;
        });
        var buf = new ArrayBuffer(floats * 4);
        var dv = new DataView(buf);
        var p = 0;
        dv.setUint32(p, sections.length, true); p += 4;
        sections.forEach(function (s) {
            dv.setUint32(p, s.typeCode, true); p += 4;
            dv.setUint32(p, s.params.length, true); p += 4;
            s.params.forEach(function (v) { dv.setFloat32(p, v, true); p += 4; });
            if (s.typeCode === 100) {
                var pts = s.points || [];
                dv.setUint32(p, pts.length, true); p += 4;
                pts.forEach(function (pt) {
                    dv.setFloat32(p, pt[0], true); dv.setFloat32(p + 4, pt[1], true); p += 8;
                });
            }
        });
        return buf;
    }

    return { create: create, encodeSections: encodeSections };
})();
