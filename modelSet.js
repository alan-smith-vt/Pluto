// ================================================================
// modelSet.js
// Wraps N loaded models (identical geometry, different load cases --
// e.g. the same structure re-run with different soil springs) as one
// logical model with a virtually concatenated, globally indexed
// load-case list. The viewer keeps working on a single LC integer;
// this layer routes reads to the right file.
//
// The first accepted file is the PRIMARY: geometry, component layout,
// design strengths and displacementVector all come from it. Secondary
// files are validated against it:
//   - geometry (counts, node coords, element table, ID tables) must be
//     byte-identical or the file is rejected;
//   - components should match; same names in a different order (or a
//     subset) are remapped into the primary's layout during each read,
//     with NaN for anything missing -- so a model rerun after a
//     component was added still loads;
//   - strengths are taken from the primary; a name mismatch in a
//     secondary is a warning, not a rejection.
//
// Since files carry no canonical model name, the file NAME identifies
// each model: the LC dropdown groups by it, and full LC labels are
// "file · LC name" whenever more than one file is loaded (LC
// names/numbers commonly collide across model variants).
// ================================================================

var FEAModelSet = (function () {

    function arraysEqual(a, b) {
        if (a.length !== b.length) return false;
        for (var i = 0; i < a.length; i++) if (a[i] !== b[i]) return false;
        return true;
    }

    // Compare a candidate model against the primary.
    // Returns { ok:false, reason } on geometry mismatch, else
    // { ok:true, remap, warn } where remap is null when the component
    // layout is identical, or Int32Array[primary cc] (primary index ->
    // candidate slot, -1 = missing -> NaN) when it differs by name.
    function validate(primary, m) {
        var hp = primary.header, hm = m.header;
        if (hm.nNodes !== hp.nNodes || hm.nElements !== hp.nElements ||
            hm.maxCorners !== hp.maxCorners) {
            return { ok: false, reason: 'geometry differs (' +
                hm.nNodes + ' nodes / ' + hm.nElements + ' elements vs ' +
                hp.nNodes + ' / ' + hp.nElements + ')' };
        }
        if (!arraysEqual(primary.nodes, m.nodes))
            return { ok: false, reason: 'node coordinates differ' };
        if (!arraysEqual(primary.elems, m.elems))
            return { ok: false, reason: 'element table differs' };
        if (!arraysEqual(primary.nodeIds, m.nodeIds) ||
            !arraysEqual(primary.elemIds, m.elemIds))
            return { ok: false, reason: 'ID tables differ' };

        var pc = primary.meta.components, cc = m.meta.components;
        var identical = pc.length === cc.length && pc.every(function (c, i) {
            return c.name === cc[i].name && c.kind === cc[i].kind;
        });
        var remap = null;
        var warn = [];
        if (!identical) {
            var slotByName = {};
            cc.forEach(function (c, i) {
                if (!(c.name in slotByName)) slotByName[c.name] = i;
            });
            remap = new Int32Array(pc.length);
            var missing = [];
            pc.forEach(function (c, i) {
                remap[i] = (c.name in slotByName) ? slotByName[c.name] : -1;
                if (remap[i] < 0) missing.push(c.name);
            });
            warn.push(missing.length
                ? 'components remapped by name; missing: ' + missing.join(', ')
                : 'components remapped by name (order differs)');
        }

        var ps = primary.meta.strengths, ms = m.meta.strengths;
        var psn = ps ? ps.components.map(function (c) { return c.name; }).join('|') : '';
        var msn = ms ? ms.components.map(function (c) { return c.name; }).join('|') : '';
        if (psn !== msn) warn.push("design strengths differ (primary's are used)");

        return { ok: true, remap: remap, warn: warn };
    }

    // Short per-model aliases for inline citations (file names are often
    // long): strip the longest common prefix and suffix across the set
    // and keep the distinguishing middle ("...SoilSoft" / "...SoilStiff"
    // -> "Soft" / "Stiff"), trimmed of separators and capped at 16 chars.
    // Degenerate results (empty / duplicate) fall back to M1, M2, ...
    function shortLabels(names) {
        var n = names.length;
        if (n <= 1) return names.slice();
        var SEP = /[\s_\-.]/;
        var minLen = Math.min.apply(null, names.map(function (s) { return s.length; }));
        var pre = 0;
        while (pre < minLen && names.every(function (s) { return s[pre] === names[0][pre]; })) pre++;
        // snap back to a token boundary so "..._Soft"/"..._Stiff" yields
        // "Soft"/"Stiff", not "oft"/"tiff" (they share the leading S)
        while (pre > 0 && !SEP.test(names[0][pre - 1])) pre--;
        var suf = 0;
        while (suf < minLen - pre && names.every(function (s) {
            return s[s.length - 1 - suf] === names[0][names[0].length - 1 - suf];
        })) suf++;
        while (suf > 0 && !SEP.test(names[0][names[0].length - suf])) suf--;
        var seen = {};
        return names.map(function (s, i) {
            var core = s.slice(pre, s.length - suf).replace(/^[\s_\-.]+|[\s_\-.]+$/g, '');
            if (core.length > 16) core = core.slice(0, 15) + '…';
            if (!core || seen[core]) core = 'M' + (i + 1);
            seen[core] = true;
            return core;
        });
    }

    // entries: [{ model, name, remap }] -- first entry is the primary
    // (remap must be null there). Secondary geometry buffers may be
    // freed by the caller after validation; only file/header/meta are
    // touched here.
    function build(entries) {
        var primary = entries[0].model;
        var hp = primary.header;
        var multi = entries.length > 1;
        var shorts = shortLabels(entries.map(function (e) { return e.name; }));

        var lcs = [];
        entries.forEach(function (en, f) {
            en.model.meta.loadCases.forEach(function (lc, j) {
                lcs.push({ file: f, lc: j, name: lc.name, type: lc.type });
            });
        });

        // Gather a whole LC block into the primary's component layout.
        function remapBlock(raw, m, remap) {
            var ccF = m.header.cornerComponents;
            var ccP = hp.cornerComponents;
            var mc = hp.maxCorners, nE = hp.nElements;
            var out = new Float32Array(nE * mc * ccP);
            var p = 0;
            for (var e = 0; e < nE; e++) {
                for (var k = 0; k < mc; k++) {
                    var base = (e * mc + k) * ccF;
                    for (var i = 0; i < ccP; i++) {
                        var s = remap[i];
                        out[p++] = s >= 0 ? raw[base + s] : NaN;
                    }
                }
            }
            return out;
        }

        function remapRecord(rec, m, remap) {
            var ccF = m.header.cornerComponents;
            var ccP = hp.cornerComponents;
            var out = new Float32Array(hp.maxCorners * ccP);
            var p = 0;
            for (var k = 0; k < hp.maxCorners; k++) {
                for (var i = 0; i < ccP; i++) {
                    var s = remap[i];
                    out[p++] = s >= 0 ? rec[k * ccF + s] : NaN;
                }
            }
            return out;
        }

        return {
            primary: primary,
            count: entries.length,
            nLC: lcs.length,
            lcs: lcs,                                  // [{file, lc, name, type}]
            entryName: function (f) { return entries[f].name; },
            entryShort: function (f) { return shorts[f]; },
            fileName: function (g) { return entries[lcs[g].file].name; },
            lcName: function (g) { return lcs[g] ? lcs[g].name : ('LC ' + (g + 1)); },
            // Unambiguous INLINE label: prefixed with the model's SHORT
            // alias whenever more than one model is loaded (full file
            // names are long; they live in the LC dropdown headers and
            // in lcLongName for tooltips).
            lcFullName: function (g) {
                if (!lcs[g]) return 'LC ' + (g + 1);
                return multi ? shorts[lcs[g].file] + ' · ' + lcs[g].name
                             : lcs[g].name;
            },
            // Full-file-name variant for tooltips / logs.
            lcLongName: function (g) {
                if (!lcs[g]) return 'LC ' + (g + 1);
                return multi ? entries[lcs[g].file].name + ' · ' + lcs[g].name
                             : lcs[g].name;
            },
            readLC: async function (g) {
                var en = entries[lcs[g].file];
                var raw = await FEABinary.readLC(en.model, lcs[g].lc);
                return en.remap ? remapBlock(raw, en.model, en.remap) : raw;
            },
            readElementRecord: async function (g, elem) {
                var en = entries[lcs[g].file];
                var rec = await FEABinary.readElementRecord(en.model, lcs[g].lc, elem);
                return en.remap ? remapRecord(rec, en.model, en.remap) : rec;
            }
        };
    }

    return { validate: validate, build: build };
})();
