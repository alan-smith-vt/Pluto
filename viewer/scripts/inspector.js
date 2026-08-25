// ================================================================
// inspector.js
// Standalone structured inspector for FEAV v3 stress-viewer binaries.
//
// v3-ONLY for now (FEABinary is aliased to the frozen FEAv3 reader in
// inspector.html). Reuses it for ALL format logic -- header
// parse, offset/stride math, readRange, readLC -- so the inspector
// can never disagree with the viewer about the file layout.
//
// What it surfaces:
//   - Header: all 15 uint32 fields + a raw hex dump.
//   - Block layout: offset/length/end of every block, range-checked
//     against the file size (the same check the reader fails on).
//   - Metadata: load-case + component tables, plus raw JSON.
//   - Nodes / Elements / ID tables: paged browsing.
//   - Field data: load one LC block on demand, per-component stats,
//     and a per-element corner x component matrix.
// ================================================================

var FEAInspector = (function () {
    'use strict';

    var resultsEl, logEl, fileNameEl;

    // ---- tiny DOM helpers ----------------------------------------
    function el(tag, attrs, kids) {
        var n = document.createElement(tag);
        if (attrs) {
            for (var k in attrs) {
                if (k === 'class') n.className = attrs[k];
                else n.setAttribute(k, attrs[k]);
            }
        }
        if (kids != null) {
            if (!Array.isArray(kids)) kids = [kids];
            for (var i = 0; i < kids.length; i++) {
                var c = kids[i];
                if (c == null) continue;
                n.appendChild(typeof c === 'object' ? c : document.createTextNode(String(c)));
            }
        }
        return n;
    }

    function section(title, open) {
        var d = el('details', { class: 'section' });
        if (open) d.open = true;
        d.appendChild(el('summary', null, title));
        var body = el('div', { class: 'sbody' });
        d.appendChild(body);
        d._body = body;
        return d;
    }

    // Build a <table>. Each cell is a string/number, or an object
    // { text, cls } for a styled cell.
    function table(headers, rows) {
        var thead = el('thead');
        var hr = el('tr');
        for (var i = 0; i < headers.length; i++) hr.appendChild(el('th', null, headers[i]));
        thead.appendChild(hr);
        var tbody = el('tbody');
        for (var r = 0; r < rows.length; r++) {
            var tr = el('tr');
            var row = rows[r];
            for (var c = 0; c < row.length; c++) {
                var cell = row[c];
                if (cell && typeof cell === 'object') {
                    tr.appendChild(el('td', { class: cell.cls || '' }, cell.text));
                } else {
                    tr.appendChild(el('td', null, cell));
                }
            }
            tbody.appendChild(tr);
        }
        return el('table', { class: 'grid' }, [thead, tbody]);
    }

    // ---- formatting ----------------------------------------------
    function fmtNum(v) {
        if (typeof v !== 'number') return String(v);
        if (Number.isNaN(v)) return 'NaN';
        if (!Number.isFinite(v)) return v > 0 ? '+Inf' : '-Inf';
        if (v === 0) return '0';
        var a = Math.abs(v);
        if (a >= 1e-3 && a < 1e7) return v.toFixed(4);
        return v.toExponential(4);
    }

    function fmtBytes(n) {
        if (n < 1024) return n + ' B';
        if (n < 1048576) return (n / 1024).toFixed(1) + ' KB';
        if (n < 1073741824) return (n / 1048576).toFixed(2) + ' MB';
        return (n / 1073741824).toFixed(2) + ' GB';
    }

    function hex8(n) {
        var s = (n >>> 0).toString(16);
        while (s.length < 8) s = '0' + s;
        return s;
    }

    // ASCII of a uint32's bytes in little-endian (file) order.
    function leAscii(u32) {
        var s = '';
        for (var i = 0; i < 4; i++) {
            var b = (u32 >>> (i * 8)) & 0xff;
            s += (b >= 32 && b < 127) ? String.fromCharCode(b) : '.';
        }
        return s;
    }

    function hexDump(bytes, base) {
        var lines = [];
        for (var off = 0; off < bytes.length; off += 16) {
            var hex = '', asc = '';
            for (var i = 0; i < 16; i++) {
                if (off + i < bytes.length) {
                    var b = bytes[off + i];
                    hex += (b < 16 ? '0' : '') + b.toString(16) + ' ';
                    asc += (b >= 32 && b < 127) ? String.fromCharCode(b) : '.';
                } else {
                    hex += '   ';
                }
                if (i === 7) hex += ' ';
            }
            lines.push(hex8(base + off) + '  ' + hex + ' ' + asc);
        }
        return lines.join('\n');
    }

    // ---- log ------------------------------------------------------
    function clearLog() { logEl.textContent = ''; }
    function log(msg) { logEl.appendChild(document.createTextNode(msg + '\n')); }

    // ---- a reusable paged table ----------------------------------
    // rowFn(i) -> array of cells for absolute row i.
    function pagedTable(headers, total, rowFn, pageSize) {
        pageSize = pageSize || 100;
        var nPages = Math.max(1, Math.ceil(total / pageSize));
        var page = 0;

        var prev = el('button', null, '◀ Prev');
        var next = el('button', null, 'Next ▶');
        var jump = el('input', { type: 'number', class: 'pgjump', min: '0',
            max: String(Math.max(0, total - 1)), placeholder: 'row #' });
        var jumpBtn = el('button', null, 'Go');
        var info = el('span', { class: 'pginfo' });
        var bar = el('div', { class: 'pgbar' }, [prev, next, jump, jumpBtn, info]);
        var host = el('div', { class: 'tablewrap' });

        function render() {
            var start = page * pageSize;
            var end = Math.min(total, start + pageSize);
            var rows = [];
            for (var i = start; i < end; i++) rows.push(rowFn(i));
            host.innerHTML = '';
            host.appendChild(table(headers, rows));
            info.textContent = total === 0 ? '(empty)'
                : 'rows ' + start + '–' + (end - 1) + ' of ' + total +
                  '  ·  page ' + (page + 1) + ' / ' + nPages;
            prev.disabled = page <= 0;
            next.disabled = page >= nPages - 1;
        }
        prev.onclick = function () { if (page > 0) { page--; render(); } };
        next.onclick = function () { if (page < nPages - 1) { page++; render(); } };
        jumpBtn.onclick = function () {
            var r = parseInt(jump.value, 10);
            if (!isNaN(r) && r >= 0 && r < total) { page = Math.floor(r / pageSize); render(); }
        };
        render();
        return el('div', { class: 'paged' }, [bar, host]);
    }

    // ---- section renderers ---------------------------------------
    function renderHeader(model, headerBytes) {
        var h = model.header;
        var d = section('Header  —  ' + FEABinary.HEADER_FIELDS.length +
            ' uint32 fields, ' + FEABinary.HEADER_BYTES + ' bytes', true);
        var rows = FEABinary.HEADER_FIELDS.map(function (name, i) {
            var v = h[name];
            var note = '';
            if (name === 'magic') {
                note = '0x' + (v >>> 0).toString(16).toUpperCase() + '  "' +
                    leAscii(v) + '"  ' +
                    (v === FEABinary.MAGIC ? '(valid)' : '(WRONG — not a FEAV file)');
            } else if (name === 'version') {
                note = v === FEABinary.SUPPORTED_VERSION
                    ? 'supported' : 'reader expects v' + FEABinary.SUPPORTED_VERSION;
            } else if (/Offset$/.test(name)) {
                note = 'byte offset into file';
            } else if (/Length$/.test(name)) {
                note = 'byte length';
            }
            return [i * 4, name, v, note];
        });
        d._body.appendChild(table(['Offset', 'Field', 'Value (uint32 LE)', 'Notes'], rows));
        d._body.appendChild(el('div', { class: 'subhdr' }, 'Raw header bytes'));
        d._body.appendChild(el('pre', { class: 'hex' }, hexDump(headerBytes, 0)));
        return d;
    }

    function renderLayout(model) {
        var h = model.header;
        var d = section('Block layout', true);
        var blocks = [
            ['header', 0, h.headerSize],
            ['metadata (JSON)', h.metaOffset, h.metaLength],
            ['node coords', h.nodesOffset, h.nNodes * 3 * 4],
            ['element table', h.elemsOffset, h.nElements * FEABinary.ELEM_RECORD_U32 * 4],
            ['node IDs', h.nodeIdOffset, h.nNodes * 4],
            ['element IDs', h.elemIdOffset, h.nElements * 4],
            ['corner field', h.cornerFieldOffset, h.nFieldLC * model.lcByteStride]
        ];
        if (model.meta.strengths) {
            blocks.push(['design-strength block (meta-declared)', model.meta.strengths.offset,
                FEABinary.strengthByteLength(h, model.meta.strengths)]);
        }
        var rows = blocks.map(function (b) {
            var off = b[1], len = b[2], end = off + len;
            var ok = off >= 0 && len >= 0 && end <= model.file.size;
            return [b[0], off, len + '  (' + fmtBytes(len) + ')', end,
                { text: ok ? 'OK' : 'OUT OF RANGE', cls: ok ? 'ok' : 'bad' }];
        });
        rows.push([{ text: 'file size', cls: '' }, '', '',
            model.file.size + '  (' + fmtBytes(model.file.size) + ')', '']);
        d._body.appendChild(table(['Block', 'Offset', 'Length', 'End', 'Within file'], rows));
        d._body.appendChild(el('div', { class: 'note' },
            'Per-load-case field stride: ' + model.lcByteStride + ' bytes (' +
            fmtBytes(model.lcByteStride) + ') = nElements × maxCorners × ' +
            'cornerComponents × 4.'));
        return d;
    }

    // Tag a kind name as a styled table cell so stress/displacement/dsr
    // pop visually -- DSR especially since it's design-critical.
    function kindCell(k) {
        return { text: k, cls: 'kind-' + (k || 'other') };
    }

    function kindCounts(components) {
        var counts = {};
        components.forEach(function (c) {
            var k = c.kind || 'other';
            counts[k] = (counts[k] || 0) + 1;
        });
        return counts;
    }

    function kindSummary(components) {
        var counts = kindCounts(components);
        var known = ['stress', 'displacement', 'dsr', 'other'];
        var parts = [];
        known.forEach(function (k) {
            if (counts[k]) parts.push(counts[k] + ' ' + k);
        });
        // generic kinds (e.g. preDSR) after the known ones
        Object.keys(counts).forEach(function (k) {
            if (known.indexOf(k) < 0) parts.push(counts[k] + ' ' + k);
        });
        return parts.join(' · ');
    }

    function renderMeta(model) {
        var m = model.meta;
        var d = section('Metadata  —  ' + m.loadCases.length + ' load cases, ' +
            m.components.length + ' components  (' + kindSummary(m.components) + ')',
            true);
        d._body.appendChild(el('div', { class: 'subhdr' }, 'Load cases'));
        d._body.appendChild(table(['Index', 'Name', 'Type'],
            m.loadCases.map(function (lc, i) { return [i, lc.name, lc.type]; })));
        d._body.appendChild(el('div', { class: 'subhdr' }, 'Components'));
        d._body.appendChild(table(['Index', 'Name', 'Kind', 'Unit'],
            m.components.map(function (c, i) { return [i, c.name, kindCell(c.kind), c.unit || '']; })));
        if (m.strengths) {
            d._body.appendChild(el('div', { class: 'subhdr' },
                'Design-strength components (LC-independent block @ byte ' + m.strengths.offset + ')'));
            d._body.appendChild(table(['Index', 'Name', 'Kind', 'Unit'],
                m.strengths.components.map(function (c, i) {
                    return [i, c.name, kindCell('str'), c.unit || ''];
                })));
        }
        d._body.appendChild(el('div', { class: 'subhdr' }, 'Raw metadata JSON'));
        var raw;
        try { raw = JSON.stringify(m.raw, null, 2); } catch (e) { raw = String(m.raw); }
        d._body.appendChild(el('pre', { class: 'hex' }, raw));
        return d;
    }

    function renderNodes(model) {
        var n = model.header.nNodes;
        var d = section('Nodes  —  ' + n, false);
        d._body.appendChild(pagedTable(
            ['Index', 'Real ID', 'X', 'Y', 'Z'], n,
            function (i) {
                return [i, model.nodeIds[i],
                    fmtNum(model.nodes[i * 3]),
                    fmtNum(model.nodes[i * 3 + 1]),
                    fmtNum(model.nodes[i * 3 + 2])];
            }, 100));
        return d;
    }

    function renderElements(model) {
        var n = model.header.nElements;
        var REC = FEABinary.ELEM_RECORD_U32;
        var d = section('Elements  —  ' + n, false);
        d._body.appendChild(pagedTable(
            ['Index', 'Real ID', 'Corners', 'Node 0', 'Node 1', 'Node 2', 'Node 3'], n,
            function (i) {
                var ncount = model.elems[i * REC];
                var cells = [i, model.elemIds[i], ncount];
                for (var k = 0; k < 4; k++) {
                    var nv = model.elems[i * REC + 1 + k];
                    if (k >= ncount) {
                        cells.push({ text: nv === 0xFFFFFFFF ? 'pad' : nv + ' (pad)',
                            cls: 'pad' });
                    } else {
                        cells.push(nv);
                    }
                }
                return cells;
            }, 100));
        return d;
    }

    // Per-component min/max/mean over one resident LC block.
    function renderStats(model, lcData) {
        var h = model.header, cc = h.cornerComponents;
        var comps = model.meta.components;
        var st = [];
        for (var c = 0; c < cc; c++) {
            st.push({ min: Infinity, max: -Infinity, sum: 0, valid: 0, nan: 0 });
        }
        for (var e = 0; e < h.nElements; e++) {
            for (var k = 0; k < h.maxCorners; k++) {
                var base = e * h.maxCorners * cc + k * cc;
                for (var ci = 0; ci < cc; ci++) {
                    var v = lcData[base + ci];
                    var s = st[ci];
                    if (Number.isNaN(v)) {
                        s.nan++;
                    } else {
                        s.valid++;
                        s.sum += v;
                        if (v < s.min) s.min = v;
                        if (v > s.max) s.max = v;
                    }
                }
            }
        }
        var rows = st.map(function (s, i) {
            return [i, comps[i].name, kindCell(comps[i].kind),
                s.valid ? fmtNum(s.min) : '—',
                s.valid ? fmtNum(s.max) : '—',
                s.valid ? fmtNum(s.sum / s.valid) : '—',
                s.valid, s.nan];
        });
        var wrap = el('div');
        wrap.appendChild(el('div', { class: 'subhdr' },
            'Per-component statistics (this load case)'));
        wrap.appendChild(table(
            ['#', 'Component', 'Kind', 'Min', 'Max', 'Mean', 'Valid', 'NaN'], rows));
        return wrap;
    }

    // Per-DSR-component worst case for this load case: where is each
    // demand-to-strength ratio peaking? Returns null if there are no DSR
    // components in the file.
    function renderDsrWorst(model, lcData) {
        var h = model.header, cc = h.cornerComponents;
        var comps = model.meta.components;
        var dsrIdx = [];
        for (var c = 0; c < cc; c++) if (comps[c].kind === 'dsr') dsrIdx.push(c);
        if (dsrIdx.length === 0) return null;

        var REC = FEABinary.ELEM_RECORD_U32;
        var rows = dsrIdx.map(function (c) {
            var bestV = -Infinity, bestE = -1, bestK = -1;
            for (var e = 0; e < h.nElements; e++) {
                for (var k = 0; k < h.maxCorners; k++) {
                    var v = lcData[FEABinary.fieldIndex(h, e, k, c)];
                    if (v === v && v > bestV) { bestV = v; bestE = e; bestK = k; }
                }
            }
            if (bestE < 0) {
                return [c, comps[c].name, '—', '—', '—', '—', '—'];
            }
            var nodeIdx = model.elems[bestE * REC + 1 + bestK];
            var realNode = (bestK < model.elems[bestE * REC]) ? model.nodeIds[nodeIdx] : '—';
            var valCell = {
                text: fmtNum(bestV),
                cls: 'num' + (bestV >= 1.0 ? ' bad' : '')
            };
            return [c, comps[c].name, valCell,
                bestE, model.elemIds[bestE], bestK, realNode];
        });
        var wrap = el('div');
        wrap.appendChild(el('div', { class: 'subhdr' },
            'DSR worst case per check (this load case) — ≥1.0 highlighted'));
        wrap.appendChild(table(
            ['#', 'DSR check', 'Worst DSR', 'Elem idx', 'Elem ID',
             'Corner', 'Node ID'], rows));
        return wrap;
    }

    // Element navigator: corner x component matrix for one element.
    function renderElemNav(model, lcData) {
        var h = model.header, REC = FEABinary.ELEM_RECORD_U32;
        var comps = model.meta.components, cc = h.cornerComponents;

        var idx = el('input', { type: 'number', class: 'pgjump', min: '0',
            max: String(h.nElements - 1), value: '0' });
        var prev = el('button', null, '◀');
        var next = el('button', null, '▶');
        var meta = el('span', { class: 'pginfo' });
        var bar = el('div', { class: 'pgbar' },
            ['Element index:', prev, idx, next, meta]);
        var host = el('div', { class: 'tablewrap' });

        function show() {
            var e = parseInt(idx.value, 10);
            if (isNaN(e) || e < 0) e = 0;
            if (e > h.nElements - 1) e = h.nElements - 1;
            idx.value = e;

            var ncount = model.elems[e * REC];
            var nodeIdx = [];
            for (var k = 0; k < 4; k++) nodeIdx.push(model.elems[e * REC + 1 + k]);
            meta.textContent = 'Real ID ' + model.elemIds[e] + '  ·  ' +
                ncount + ' corners  ·  nodes [' +
                nodeIdx.slice(0, ncount).join(', ') + ']';

            var headers = ['#', 'Component', 'Kind'];
            for (var hk = 0; hk < h.maxCorners; hk++) {
                headers.push('Corner ' + hk + (hk >= ncount ? ' (pad)' : ''));
            }
            var rows = [];
            for (var c = 0; c < cc; c++) {
                var row = [c, comps[c].name, kindCell(comps[c].kind)];
                for (var ck = 0; ck < h.maxCorners; ck++) {
                    var v = lcData[FEABinary.fieldIndex(h, e, ck, c)];
                    row.push({ text: fmtNum(v), cls: Number.isNaN(v) ? 'nan' : 'num' });
                }
                rows.push(row);
            }
            host.innerHTML = '';
            host.appendChild(table(headers, rows));
        }
        prev.onclick = function () {
            idx.value = Math.max(0, (parseInt(idx.value, 10) || 0) - 1); show();
        };
        next.onclick = function () {
            idx.value = Math.min(h.nElements - 1, (parseInt(idx.value, 10) || 0) + 1); show();
        };
        idx.onchange = show;
        show();

        var wrap = el('div');
        wrap.appendChild(el('div', { class: 'subhdr' }, 'Per-element corner field'));
        wrap.appendChild(bar);
        wrap.appendChild(host);
        return wrap;
    }

    function renderField(model) {
        var d = section('Field data  —  ' + model.header.nFieldLC +
            ' load-case block(s)', true);
        var body = d._body;

        var lcSel = el('select', { class: 'sel' });
        model.meta.loadCases.forEach(function (lc, i) {
            lcSel.appendChild(el('option', { value: i },
                i + ': ' + lc.name + '  [' + lc.type + ']'));
        });
        var loadBtn = el('button', { class: 'primary' }, 'Load load-case block');
        body.appendChild(el('div', { class: 'fctl' }, ['Load case:', lcSel, loadBtn]));

        var statusEl = el('div', { class: 'note' },
            'Pick a load case and click "Load load-case block" — ' +
            'field blocks are sliced from the file on demand.');
        var statsHost = el('div');
        var dsrHost = el('div');
        var elemHost = el('div');
        body.appendChild(statusEl);
        body.appendChild(statsHost);
        body.appendChild(dsrHost);
        body.appendChild(elemHost);

        loadBtn.onclick = async function () {
            var lc = parseInt(lcSel.value, 10);
            statsHost.innerHTML = '';
            dsrHost.innerHTML = '';
            elemHost.innerHTML = '';
            statusEl.textContent = 'Reading load-case ' + lc + ' field block…';
            loadBtn.disabled = true;
            var lcData;
            try {
                lcData = await FEABinary.readLC(model, lc);
            } catch (err) {
                statusEl.textContent = 'Error reading LC ' + lc + ': ' + err.message;
                loadBtn.disabled = false;
                return;
            }
            loadBtn.disabled = false;
            statusEl.textContent = 'Loaded LC ' + lc + ' — ' + lcData.length +
                ' float32 values (' + fmtBytes(lcData.length * 4) + ').';
            statsHost.appendChild(renderStats(model, lcData));
            var dsr = renderDsrWorst(model, lcData);
            if (dsr) dsrHost.appendChild(dsr);
            elemHost.appendChild(renderElemNav(model, lcData));
        };
        return d;
    }

    // Design-strength block: per-component stats, loaded on demand (the block
    // is small next to the field data but not always trivial).
    function renderStrengths(model) {
        var str = model.meta.strengths;
        if (!str) return null;
        var h = model.header;
        var nStr = str.components.length;
        var d = section('Design Strengths  —  ' + nStr + ' LC-independent component(s)', false);
        var loadBtn = el('button', { class: 'primary' }, 'Load design-strength block');
        var statusEl = el('div', { class: 'note' },
            'float32[nElements][maxCorners][' + nStr + '] @ byte ' + str.offset +
            ' — same slot conventions as the corner field.');
        var host = el('div');
        d._body.appendChild(el('div', { class: 'fctl' }, [loadBtn]));
        d._body.appendChild(statusEl);
        d._body.appendChild(host);

        loadBtn.onclick = async function () {
            loadBtn.disabled = true;
            var data;
            try {
                data = await FEABinary.readStrengths(model);
            } catch (err) {
                statusEl.textContent = 'Error reading design-strength block: ' + err.message;
                loadBtn.disabled = false;
                return;
            }
            var st = [];
            for (var c = 0; c < nStr; c++) {
                st.push({ min: Infinity, max: -Infinity, sum: 0, valid: 0, nan: 0 });
            }
            for (var e = 0; e < h.nElements; e++) {
                for (var k = 0; k < h.maxCorners; k++) {
                    var base = (e * h.maxCorners + k) * nStr;
                    for (var ci = 0; ci < nStr; ci++) {
                        var v = data[base + ci];
                        var s = st[ci];
                        if (Number.isNaN(v)) {
                            s.nan++;
                        } else {
                            s.valid++;
                            s.sum += v;
                            if (v < s.min) s.min = v;
                            if (v > s.max) s.max = v;
                        }
                    }
                }
            }
            host.innerHTML = '';
            host.appendChild(table(
                ['#', 'Component', 'Unit', 'Min', 'Max', 'Mean', 'Valid', 'NaN'],
                st.map(function (s, i) {
                    return [i, str.components[i].name, str.components[i].unit || '',
                        s.valid ? fmtNum(s.min) : '—',
                        s.valid ? fmtNum(s.max) : '—',
                        s.valid ? fmtNum(s.sum / s.valid) : '—',
                        s.valid, s.nan];
                })));
            statusEl.textContent = 'Loaded ' + data.length + ' float32 values (' +
                fmtBytes(data.length * 4) + ').';
        };
        return d;
    }

    // ---- top-level ------------------------------------------------
    async function inspect(file, displayName) {
        clearLog();
        resultsEl.innerHTML = '';
        fileNameEl.textContent = displayName + '  ·  ' + fmtBytes(file.size);

        var model;
        try {
            model = await FEABinary.loadModel(file, log);
        } catch (err) {
            resultsEl.appendChild(el('div', { class: 'errbox' },
                'Failed to parse file: ' + err.message));
            log('ERROR: ' + err.message);
            return;
        }

        var headerBytes;
        try {
            var buf = await FEABinary.readRange(file, 0, FEABinary.HEADER_BYTES);
            headerBytes = new Uint8Array(buf);
        } catch (e) {
            headerBytes = new Uint8Array(0);
        }

        resultsEl.appendChild(renderHeader(model, headerBytes));
        resultsEl.appendChild(renderLayout(model));
        resultsEl.appendChild(renderMeta(model));
        resultsEl.appendChild(renderNodes(model));
        resultsEl.appendChild(renderElements(model));
        var capSection = renderStrengths(model);
        if (capSection) resultsEl.appendChild(capSection);
        resultsEl.appendChild(renderField(model));
        log('Inspection complete.');
    }

    function init() {
        resultsEl = document.getElementById('results');
        logEl = document.getElementById('log');
        fileNameEl = document.getElementById('fileName');

        document.getElementById('insFile').addEventListener('change', function (ev) {
            var f = ev.target.files && ev.target.files[0];
            if (f) inspect(f, f.name);
        });
        document.getElementById('btnSample').addEventListener('click', function () {
            if (typeof FEASample === 'undefined') {
                alert('sampleModel.js is not loaded.'); return;
            }
            inspect(FEASample.buildSampleBlob(), 'sampleModel.js (in-memory)');
        });
        document.getElementById('btnDownload').addEventListener('click', function () {
            if (typeof FEASample === 'undefined' || !FEASample.downloadSampleBin) {
                alert('FEASample.downloadSampleBin is not available.'); return;
            }
            FEASample.downloadSampleBin('sampleModel.bin');
        });
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }

    return { inspect: inspect };
})();
