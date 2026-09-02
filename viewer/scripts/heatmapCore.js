// ================================================================
// heatmapCore.js
// Pure computation for the pipe-weight heatmap (heatmap.html).
// No DOM, no THREE -- loadable headlessly in Node for tests.
//
// Model contract (from PlutoFormat.load + beamView): beam elements with
// PIPE sections are pipes, I sections are steel. Coordinates stay in
// FILE units throughout; areas convert to ft^2 via unitToFeet() for the
// psf math (psf = lb/ft^2 of the pipe's PLAN footprint = planLength*OD).
//
// Weight of a pipe run = psf(OD) * planLength_ft * OD_ft, smeared over
// the cells its plan rectangle covers by supersampling. A vertical run
// has ~zero plan length and so ~zero weight (risers undercount -- noted
// in the page; switch to plf later fixes that properly).
// ================================================================

var FEAHeatmapCore = (function () {

    function unitToFeet(unit) {
        switch ((unit || 'in').toLowerCase()) {
            case 'm': return 3.280839895;
            case 'mm': return 0.003280839895;
            case 'in': return 1.0 / 12.0;
            case 'ft': return 1.0;
            default: return 1.0 / 12.0;
        }
    }

    // Split the beam domain into pipes and steel; derive the steel plan
    // extents and a default Z cutoff = lowest bottom flange of any
    // HORIZONTAL steel member (verticals -- column hangers -- excluded).
    function analyze(bv) {
        var REC = bv.elemRecordU32, elems = bv.elems, nodes = bv.nodes;
        var sections = bv.sections || [];
        var nElem = bv.header ? bv.header.nElements : (elems.length / REC);
        var pipes = [], steel = [];
        var sx0 = Infinity, sy0 = Infinity, sx1 = -Infinity, sy1 = -Infinity;
        var cutoff = Infinity, steelZTop = -Infinity;

        for (var e = 0; e < nElem; e++) {
            var n0 = elems[e * REC + 1], n1 = elems[e * REC + 2], si = elems[e * REC + 3];
            var sec = si < sections.length ? sections[si] : null;
            var ax = nodes[n0 * 3], ay = nodes[n0 * 3 + 1], az = nodes[n0 * 3 + 2];
            var bx = nodes[n1 * 3], by = nodes[n1 * 3 + 1], bz = nodes[n1 * 3 + 2];
            if (sec && sec.type === 'PIPE') {
                pipes.push({ ax: ax, ay: ay, az: az, bx: bx, by: by, bz: bz,
                             od: sec.params.od, elem: e });
            } else if (sec && sec.type === 'I') {
                steel.push({ ax: ax, ay: ay, bx: bx, by: by });
                sx0 = Math.min(sx0, ax, bx); sx1 = Math.max(sx1, ax, bx);
                sy0 = Math.min(sy0, ay, by); sy1 = Math.max(sy1, ay, by);
                var len = Math.sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay) + (bz - az) * (bz - az));
                var horizontal = len > 1e-9 && Math.abs(bz - az) / len < 0.2;
                if (horizontal) {
                    var zc = (az + bz) / 2, d = sec.params.d || 0;
                    cutoff = Math.min(cutoff, zc - d / 2);
                    steelZTop = Math.max(steelZTop, zc + d / 2);
                }
            }
        }
        return {
            pipes: pipes, steel: steel,
            extents: { x0: sx0, y0: sy0, x1: sx1, y1: sy1 },
            defaultCutoff: cutoff === Infinity ? null : cutoff,
            steelZTop: steelZTop === -Infinity ? null : steelZTop
        };
    }

    // psf table: { "<OD inches>": psf, ..., "default": psf }. Nearest key
    // within 5% of the OD wins; else "default"; else 0 (counted unmatched).
    function makePsfLookup(table) {
        var keys = [];
        for (var k in table) {
            if (k === 'default') continue;
            var v = parseFloat(k);
            if (v === v && v > 0) keys.push({ od: v, psf: table[k] });
        }
        var def = typeof table['default'] === 'number' ? table['default'] : null;
        return function (odInches) {
            var best = null, bestErr = Infinity;
            for (var i = 0; i < keys.length; i++) {
                var err = Math.abs(keys[i].od - odInches) / odInches;
                if (err < bestErr) { bestErr = err; best = keys[i]; }
            }
            if (best && bestErr <= 0.05) return { psf: best.psf, matched: true };
            if (def !== null) return { psf: def, matched: true };
            return { psf: 0, matched: false };
        };
    }

    // Rasterise pipes above cutoffZ into a base grid clipped to extents.
    // opts: { extents:{x0,y0,x1,y1}, cutoffZ, baseCell (file units), unit }
    // Returns { nx, ny, w: Float64Array (lb per base cell), baseCell,
    //           cellAreaFt2, totalW, nCounted, nBelow, nUnmatched }
    function rasterize(pipes, psfLookup, opts) {
        var ex = opts.extents;
        var cell = opts.baseCell;
        var toFt = unitToFeet(opts.unit);
        var nx = Math.max(1, Math.ceil((ex.x1 - ex.x0) / cell));
        var ny = Math.max(1, Math.ceil((ex.y1 - ex.y0) / cell));
        var w = new Float64Array(nx * ny);
        var totalW = 0, nCounted = 0, nBelow = 0, nUnmatched = 0;
        var inchPerUnit = toFt * 12;

        for (var i = 0; i < pipes.length; i++) {
            var p = pipes[i];
            var zc = (p.az + p.bz) / 2;
            if (zc < opts.cutoffZ) { nBelow++; continue; }
            var dx = p.bx - p.ax, dy = p.by - p.ay;
            var planLen = Math.sqrt(dx * dx + dy * dy);
            var odIn = p.od * inchPerUnit;
            var r = psfLookup(odIn);
            if (!r.matched) { nUnmatched++; }
            var W = r.psf * (planLen * toFt) * (p.od * toFt);   // lb
            if (W <= 0) { nCounted++; continue; }
            nCounted++;

            // supersample the plan rectangle: along-length x across-width
            var ux, uy;
            if (planLen > 1e-9) { ux = dx / planLen; uy = dy / planLen; }
            else { ux = 1; uy = 0; }
            var px = -uy, py = ux;                               // plan perpendicular
            var nL = Math.max(2, Math.ceil(planLen / (cell * 0.5)) + 1);
            var nW = Math.max(1, Math.ceil(p.od / (cell * 0.5)));
            var dW = W / (nL * nW);
            for (var a = 0; a < nL; a++) {
                var t = nL === 1 ? 0.5 : a / (nL - 1);
                var sx = p.ax + dx * t, sy = p.ay + dy * t;
                for (var b = 0; b < nW; b++) {
                    var s = nW === 1 ? 0 : (b / (nW - 1) - 0.5);
                    var qx = sx + px * s * p.od, qy = sy + py * s * p.od;
                    if (qx < ex.x0 || qx > ex.x1 || qy < ex.y0 || qy > ex.y1) continue;  // outside steel plan
                    // samples exactly on the max edge belong to the last cell
                    var cx = Math.min(nx - 1, Math.floor((qx - ex.x0) / cell));
                    var cy = Math.min(ny - 1, Math.floor((qy - ex.y0) / cell));
                    w[cy * nx + cx] += dW;
                    totalW += dW;
                }
            }
        }
        return { nx: nx, ny: ny, w: w, baseCell: cell,
                 cellAreaFt2: (cell * toFt) * (cell * toFt),
                 totalW: totalW, nCounted: nCounted, nBelow: nBelow, nUnmatched: nUnmatched };
    }

    // Aggregate the base grid into blocks of `factor` base cells (the
    // smear level). Returns { nx, ny, w (lb per block), psf (lb/ft^2),
    // maxPsf, cellFile } -- block psf = weight / covered block area.
    function aggregate(grid, factor) {
        var nx = Math.max(1, Math.ceil(grid.nx / factor));
        var ny = Math.max(1, Math.ceil(grid.ny / factor));
        var w = new Float64Array(nx * ny);
        for (var y = 0; y < grid.ny; y++) {
            var by = Math.floor(y / factor);
            for (var x = 0; x < grid.nx; x++) {
                w[by * nx + Math.floor(x / factor)] += grid.w[y * grid.nx + x];
            }
        }
        var psf = new Float64Array(nx * ny);
        var maxPsf = 0;
        for (var i = 0; i < w.length; i++) {
            // area of the block actually inside the extents (edge blocks are partial)
            var bx0 = (i % nx) * factor, by0 = Math.floor(i / nx) * factor;
            var cw = Math.min(factor, grid.nx - bx0), ch = Math.min(factor, grid.ny - by0);
            var area = cw * ch * grid.cellAreaFt2;
            psf[i] = area > 0 ? w[i] / area : 0;
            if (psf[i] > maxPsf) maxPsf = psf[i];
        }
        return { nx: nx, ny: ny, w: w, psf: psf, maxPsf: maxPsf, cellFile: grid.baseCell * factor };
    }

    return { unitToFeet: unitToFeet, analyze: analyze, makePsfLookup: makePsfLookup,
             rasterize: rasterize, aggregate: aggregate };
})();

if (typeof module !== 'undefined' && module.exports) module.exports = FEAHeatmapCore;
