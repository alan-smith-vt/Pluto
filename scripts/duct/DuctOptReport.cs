// HTML report of an expansion-joint optimizer run (DuctOptimize outputs): verdicts, convergence and
// trial-landscape plots, DCR envelope curves before / after, pivot and timing distributions, and plan /
// elevation maps of the duct coloured by DCR with the candidates tried and the joints chosen.
// Inline SVG with native <title> tooltips, no scripts. C# 5, no dependencies.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

public static class DuctOptReport
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    const string Blue = "#1f77b4", Orange = "#ff7f0e", Green = "#2ca02c", Red = "#d62728", Grey = "#999", Purple = "#9467bd";

    class Row { public Dictionary<string, string> C; public double Num(string k) { double v; return double.TryParse(C[k], NumberStyles.Float, Inv, out v) ? v : double.NaN; } }
    static List<Row> Tsv(string path)
    {
        List<Row> rows = new List<Row>();
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return rows;
        foreach (Dictionary<string, string> d in DuctTables.ReadCsv(path)) { Row r = new Row(); r.C = d; rows.Add(r); }
        return rows;
    }

    // dcr*: paths to dcr-frames.tsv files (DuctEvaluate) or null. optDir holds DuctOptimize's outputs.
    public static void Write(string path, string title, string[] facts, string optDir, string connectedDir,
        string dcrConnected, string dcrBase, string dcrFinal, string dcrDirect, double limit)
    {
        List<Row> steps = Tsv(Path.Combine(optDir, "steps.tsv"));
        List<Row> evals = Tsv(Path.Combine(optDir, "evaluations.tsv"));
        List<Row> cands = Tsv(Path.Combine(optDir, "candidates-used.tsv"));
        List<Row> chosen = Tsv(Path.Combine(optDir, "chosen.tsv"));
        List<Row> selected = Tsv(Path.Combine(optDir, "candidates-selected.tsv"));
        Dictionary<string, double> envBase = DuctReport.DcrEnvelope(Path.Combine(optDir, "envelope-base.tsv"));
        Dictionary<string, double> envFinal = DuctReport.DcrEnvelope(Path.Combine(optDir, "envelope-final.tsv"));
        Dictionary<string, double> dCon = DuctReport.DcrEnvelope(dcrConnected), dBase = DuctReport.DcrEnvelope(dcrBase), dFin = DuctReport.DcrEnvelope(dcrFinal), dDir = DuctReport.DcrEnvelope(dcrDirect);
        string optText = File.Exists(Path.Combine(optDir, "optimize.txt")) ? File.ReadAllText(Path.Combine(optDir, "optimize.txt")) : "";

        StringBuilder sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>").Append(H(title)).Append("</title><style>")
          .Append("body{font:14px/1.45 Segoe UI,Arial,sans-serif;margin:24px;color:#222;max-width:1400px}h1{font-size:22px}h2{font-size:17px;margin-top:28px;border-bottom:1px solid #ccc}h3{font-size:14px;margin:12px 0 4px}")
          .Append("table{border-collapse:collapse;margin:8px 0}td,th{border:1px solid #ddd;padding:3px 8px;text-align:right;font-variant-numeric:tabular-nums}th{background:#f3f3f3}td:first-child,th:first-child{text-align:left}")
          .Append(".ok{color:#0a7a2f;font-weight:bold}.bad{color:#b00020;font-weight:bold}.warn{color:#b26a00;font-weight:bold}pre{background:#f7f7f7;padding:8px;overflow-x:auto;font-size:12px}")
          .Append(".row{display:flex;flex-wrap:wrap;gap:16px;align-items:flex-start}.card{border:1px solid #ddd;padding:8px;background:#fff}small{color:#666}svg text{font-family:Segoe UI,Arial}")
          .Append("</style></head><body>");
        sb.Append("<h1>").Append(H(title)).Append("</h1><p><small>").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm", Inv)).Append("</small></p><ul>");
        foreach (string f in facts) sb.Append("<li>").Append(H(f)).Append("</li>");
        sb.Append("</ul>");

        // ---- outcome
        Row last = steps.Count > 0 ? steps[steps.Count - 1] : null;
        Row first = steps.Count > 0 ? steps[0] : null;
        sb.Append("<h2>Outcome</h2>");
        if (last != null)
        {
            bool done = last.Num("Excess") <= 0;
            sb.Append("<p class=\"").Append(done ? "ok" : "warn").Append("\">").Append(done ? "Every frame within the limit: " : "Frames still over the limit: ")
              .Append(H(last.C["Joints"])).Append(" joint(s) ").Append(H(last.C["Set"])).Append(F(" &rarr; sum of excess {0} (from {1}), frames over {2} (from {3}), max {4} (from {5}); {6} sets evaluated.",
                G(last.Num("Excess")), G(first.Num("Excess")), last.C["Over"], first.C["Over"], G(last.Num("Max")), G(first.Num("Max")), evals.Count)).Append("</p>");
        }
        sb.Append("<table><tr><th>DCR envelopes</th><th>frames</th><th>over ").Append(G(limit)).Append("</th><th>sum of excess</th><th>max</th></tr>");
        EnvRow(sb, "connected model (DuctEvaluate)", dCon, limit);
        EnvRow(sb, "link model, nothing released (optimizer)", envBase, limit);
        EnvRow(sb, "link model, nothing released (DuctEvaluate)", dBase, limit);
        EnvRow(sb, "chosen set released (optimizer)", envFinal, limit);
        EnvRow(sb, "chosen set released (DuctEvaluate on the superposed forces)", dFin, limit);
        EnvRow(sb, "chosen set, direct SAP disconnect (DuctEvaluate)", dDir, limit);
        sb.Append("</table>");
        sb.Append("<table><tr><th>check</th><th>frames</th><th>max |diff| of the envelope</th><th>tolerance (relative, per frame)</th><th>result</th></tr>");
        Verdict(sb, "optimizer scoring vs DuctEvaluate, nothing released (dcr-frames.tsv carries 6 digits)", envBase, dBase, 1e-5);
        Verdict(sb, "optimizer scoring vs DuctEvaluate, chosen set released", envFinal, dFin, 1e-5);
        Verdict(sb, "released by superposition vs direct SAP disconnect", dFin, dDir, 1e-4);
        Verdict(sb, "link model (nothing released) vs connected model", envBase, dCon, 0.02);
        sb.Append("</table>");

        // chosen joints
        if (chosen.Count > 0)
        {
            sb.Append("<h3>Chosen joints</h3><table><tr><th>joint</th><th>span</th><th>X</th><th>Y</th><th>Z</th><th>greedy step</th><th>excess before</th><th>excess after</th><th>gain</th></tr>");
            foreach (Row r in chosen)
            {
                double b = r.Num("ExcessBefore"), a = r.Num("ExcessAfter");
                sb.Append("<tr><td>").Append(H(r.C["Joint"])).Append("</td><td>").Append(H(r.C["Span"])).Append("</td><td>").Append(G(r.Num("X_in"))).Append("</td><td>").Append(G(r.Num("Y_in"))).Append("</td><td>").Append(G(r.Num("Z_in")))
                  .Append("</td><td>").Append(H(r.C["AddedAtStep"])).Append("</td><td>").Append(G(b)).Append("</td><td>").Append(G(a)).Append("</td><td>").Append(double.IsNaN(b) ? "" : G(b - a)).Append("</td></tr>");
            }
            sb.Append("</table>");
        }

        // ---- search
        sb.Append("<h2>Search</h2><div class=\"row\">");
        sb.Append(Convergence(steps, "Excess", "sum of excess over the limit", limit, false));
        sb.Append(Convergence(steps, "Over", "frames over the limit", limit, true));
        sb.Append(Landscape(evals));
        sb.Append(Gains(steps));
        sb.Append("</div>");
        sb.Append("<h2>DCR envelopes</h2><div class=\"row\">");
        sb.Append(SortedCurve(dCon, envBase, envFinal, dDir, limit));
        sb.Append(Histogram(dCon.Count > 0 ? dCon : envBase, envFinal, limit));
        sb.Append("</div>");
        sb.Append(DuctReport.DcrBars(dCon.Count > 0 ? dCon : envBase, envFinal, dDir, limit, 25));

        // ---- shear (not in the score: reported beside it)
        Dictionary<string, double> sCon = DuctReport.DcrColumn(dcrConnected, "DCR_shear"), sFin = DuctReport.DcrColumn(dcrFinal, "DCR_shear"), sDir = DuctReport.DcrColumn(dcrDirect, "DCR_shear");
        List<Row> shearCap = new List<Row>();   // written by DuctEvaluate beside dcr-frames.tsv
        foreach (string dp in new string[] { dcrFinal, dcrConnected })
            if (shearCap.Count == 0 && !string.IsNullOrEmpty(dp)) shearCap = Tsv(Path.Combine(Path.GetDirectoryName(dp), "shear-capacity.tsv"));
        if (sCon.Count > 0 || sFin.Count > 0)
        {
            sb.Append("<h2>Shear</h2><p><small>Shear DCR per frame = max over the scored stations and cases of V2 and V3 DCRs, with the same 1.5 stress increase. ")
              .Append("Not part of the optimizer score (config Dcr.NoShear); shown to compare with the shear calc. ")
              .Append("With a stiffener spacing: webs of V2 are the two walls of depth a = t3, of V3 the two walls of depth b = t2; k<sub>v</sub> from spacing / depth (4 + 5.34/(s/h)&sup2; if s/h &le; 1, else 5.34 + 4/(s/h)&sup2;); F<sub>cr</sub> = &pi;&sup2;Ek<sub>v</sub>/(12(1&minus;&mu;&sup2;)(h/t)&sup2;); V<sub>n</sub> per web from the sheet's rule; allowable = 2V<sub>n</sub>/&Omega;<sub>v</sub>. ")
              .Append("\"sheet\" = the Mathcad sheet as written (h = h_a / h_b, one web).</small></p>");
            sb.Append("<table><tr><th>shear DCR</th><th>frames</th><th>over ").Append(G(limit)).Append("</th><th>sum of excess</th><th>max</th></tr>");
            EnvRow(sb, "connected model", sCon, limit);
            EnvRow(sb, "chosen set released", sFin, limit);
            EnvRow(sb, "chosen set, direct SAP disconnect", sDir, limit);
            sb.Append("</table>");
            if (shearCap.Count > 0)
            {
                string[] cols = { "Section", "Material", "a_in", "b_in", "t_in", "StiffSpacing_in", "Omega_v", "h_a_over_t", "kv_a", "Fcr_a_ksi", "lambda_va", "Vn_a_web_kip", "Vall_a_kip", "h_b_over_t", "kv_b", "Fcr_b_ksi", "lambda_vb", "Vn_b_web_kip", "Vall_b_kip" };
                string[] heads = { "section", "material", "a (in)", "b (in)", "t (in)", "stiffeners (in)", "&Omega;<sub>v</sub>", "V2: h/t", "k<sub>v</sub>", "F<sub>cr</sub> (ksi)", "&lambda;<sub>v</sub>", "V<sub>n</sub> per web (kip)", "V2 allowable (kip)", "V3: h/t", "k<sub>v</sub>", "F<sub>cr</sub> (ksi)", "&lambda;<sub>v</sub>", "V<sub>n</sub> per web (kip)", "V3 allowable (kip)" };
                sb.Append("<h3>Shear capacity per section (allowable before the 1.5 increase)</h3><table><tr>");
                foreach (string hd in heads) sb.Append("<th>").Append(hd).Append("</th>");
                sb.Append("</tr>");
                foreach (Row r in shearCap)
                {
                    sb.Append("<tr>");
                    for (int i = 0; i < cols.Length; i++)
                    {
                        string v = r.C.ContainsKey(cols[i]) ? r.C[cols[i]] : "";
                        double x; bool num = i >= 2 && double.TryParse(v, NumberStyles.Float, Inv, out x);
                        sb.Append("<td>").Append(num ? G(double.Parse(v, NumberStyles.Float, Inv)) : H(v)).Append("</td>");
                    }
                    sb.Append("</tr>");
                }
                sb.Append("</table>");
            }
            sb.Append(DuctReport.DcrBars(sCon.Count > 0 ? sCon : sFin, sFin, sDir, limit, 25, "shear DCR"));
        }

        // ---- maps
        Dictionary<string, double[]> xyz; List<string[]> segs; List<Row> supports;
        LoadGeometry(connectedDir, out xyz, out segs, out supports);
        if (segs.Count > 0)
        {
            int legendStart = sb.Length;
            sb.Append("<h2>Maps</h2><p><small>Frames coloured by DCR envelope: green &le; 0.5, yellow 1.0, red 1.5, dark red 3+; grey = not scored. ")
              .Append("Candidates: hollow grey = named but not in the link model (excluded or not sampled), blue = tried, red ring = chosen. Supports, piping-drawing symbols in black: triangle under the joint = rest (vertical held), bars across the duct either side = line stop (held along the duct), bars along the duct either side = guide (held across it), arcs around the joint = rotation held; an anchor shows all of them. Supports on a duct running into / out of the view are hover only. Purple bar across the duct (ring on a riser) on the released map = expansion joint. A held direction pointing out of the view is not drawn; hover a support for its exact restraints. A frame running into / out of the view is a thick dot in its DCR colour, open grey square = support not sub-classed (survey older than the support classes). Hover for names and values. Maps: wheel to zoom, drag to pan, double-click to reset.</small></p>");
            string legend = sb.ToString(legendStart + "<h2>Maps</h2>".Length, sb.Length - legendStart - "<h2>Maps</h2>".Length);
            WriteReleasedMap(Path.Combine(Path.GetDirectoryName(path), "released-map.html"), title, legend, xyz, segs, envFinal, chosen, supports);
            foreach (int proj in new int[] { 0, 1 })
            {
                sb.Append("<h3>").Append(proj == 0 ? "Plan (X right, Y up)" : "Elevation (X right, Z up)").Append("</h3><div class=\"row\">");
                sb.Append(Map(proj == 0 ? "connected: DCR envelope" : "connected: DCR envelope", proj, xyz, segs, dCon.Count > 0 ? dCon : envBase, null, null, null, supports));
                sb.Append(Map("chosen set released: DCR envelope", proj, xyz, segs, envFinal, null, null, chosen, supports));
                sb.Append(Map("candidates: named / tried / chosen", proj, xyz, segs, null, selected, cands, chosen, supports));
                Dictionary<string, double> shearMap = DuctReport.DcrColumn(dcrFinal, "DCR_shear");
                if (shearMap.Count > 0) sb.Append(Map("chosen set released: shear DCR", proj, xyz, segs, shearMap, null, null, null, supports));
                sb.Append("</div>");
            }
        }
        else sb.Append("<h2>Maps</h2><p>No geometry: the connected survey's frames.tsv / duct-joints.tsv were not found.</p>");

        // ---- cost
        sb.Append("<h2>Cost</h2><div class=\"row\">");
        sb.Append(PivotHist(evals));
        sb.Append(Timing(evals));
        sb.Append("</div>");
        sb.Append("<h2>Log</h2><pre>").Append(H(optText)).Append("</pre>");
        sb.Append(ZoomScript).Append("</body></html>");
        File.WriteAllText(path, sb.ToString());
    }

    static void EnvRow(StringBuilder sb, string name, Dictionary<string, double> d, double limit)
    {
        if (d.Count == 0) return;
        double ex = 0, mx = 0; int over = 0;
        foreach (double v in d.Values) { if (v > limit) { ex += v - limit; over++; } mx = Math.Max(mx, v); }
        sb.Append("<tr><td>").Append(H(name)).Append("</td><td>").Append(d.Count).Append("</td><td>").Append(over).Append("</td><td>").Append(G(ex)).Append("</td><td>").Append(G(mx)).Append("</td></tr>");
    }
    static void Verdict(StringBuilder sb, string name, Dictionary<string, double> a, Dictionary<string, double> b, double tol)
    {
        if (a.Count == 0 || b.Count == 0) return;
        double mx = 0, rel = 0; int n = 0; string at = "";
        foreach (KeyValuePair<string, double> kv in a) { double vb; if (!b.TryGetValue(kv.Key, out vb)) continue; n++; double d = Math.Abs(kv.Value - vb); if (d > mx) { mx = d; at = kv.Key; } rel = Math.Max(rel, d / Math.Max(1.0, Math.Abs(kv.Value))); }
        bool ok = rel <= tol;
        sb.Append("<tr><td>").Append(H(name)).Append("</td><td>").Append(n).Append("</td><td>").Append(G(mx)).Append(at.Length > 0 ? " (frame " + H(at) + ")" : "").Append("</td><td>").Append(G(tol)).Append("</td><td class=\"").Append(ok ? "ok\">OK" : "bad\">DIFFERENT").Append("</td></tr>");
    }

    // ------------------------------------------------------------------ plots

    class Plot
    {
        public StringBuilder S = new StringBuilder();
        public int W, Hh, ML = 48, MB = 34, MT = 22, MR = 12;
        public double XLo, XHi, YLo, YHi;
        public Plot(string title, int w, int h, double xlo, double xhi, double ylo, double yhi, string xlabel, string ylabel) : this(title, w, h, xlo, xhi, ylo, yhi, xlabel, ylabel, false) { }
        public Plot(string title, int w, int h, double xlo, double xhi, double ylo, double yhi, string xlabel, string ylabel, bool intX)
        {
            W = w; Hh = h; XLo = xlo; XHi = xhi; YLo = ylo; YHi = yhi;
            if (XHi - XLo < 1e-12) { XHi = XLo + 1; }
            if (YHi - YLo < 1e-12) { YHi = YLo + 1; }
            S.Append("<div class=\"card\"><svg width=\"").Append(W + ML + MR).Append("\" height=\"").Append(Hh + MT + MB).Append("\" font-size=\"10\">");
            S.Append(F("<text x=\"{0}\" y=\"14\" font-size=\"12\" font-weight=\"bold\">{1}</text>", ML, H(title)));
            S.Append(F("<rect x=\"{0}\" y=\"{1}\" width=\"{2}\" height=\"{3}\" fill=\"#fff\" stroke=\"#999\"/>", ML, MT, W, Hh));
            foreach (double t in Ticks(YLo, YHi, false)) S.Append(F("<line x1=\"{0}\" y1=\"{1:0.#}\" x2=\"{2}\" y2=\"{1:0.#}\" stroke=\"#eee\"/><text x=\"{3}\" y=\"{4:0.#}\" text-anchor=\"end\" fill=\"#666\">{5}</text>", ML, Y(t), ML + W, ML - 4, Y(t) + 3, G(t)));
            foreach (double t in Ticks(XLo, XHi, intX)) S.Append(F("<line x1=\"{0:0.#}\" y1=\"{1}\" x2=\"{0:0.#}\" y2=\"{2}\" stroke=\"#eee\"/><text x=\"{0:0.#}\" y=\"{3}\" text-anchor=\"middle\" fill=\"#666\">{4}</text>", X(t), MT, MT + Hh, MT + Hh + 12, G(t)));
            S.Append(F("<text x=\"{0}\" y=\"{1}\" text-anchor=\"middle\">{2}</text>", ML + W / 2, MT + Hh + MB - 4, H(xlabel)));
            S.Append(F("<text x=\"10\" y=\"{0}\" transform=\"rotate(-90 10,{0})\" text-anchor=\"middle\">{1}</text>", MT + Hh / 2, H(ylabel)));
        }
        public double X(double x) { return ML + (x - XLo) / (XHi - XLo) * W; }
        public double Y(double y) { return MT + Hh - (y - YLo) / (YHi - YLo) * Hh; }
        public void Dot(double x, double y, double r, string fill, string stroke, string title)
        {
            S.Append(F("<circle cx=\"{0:0.#}\" cy=\"{1:0.#}\" r=\"{2}\" fill=\"{3}\" stroke=\"{4}\" fill-opacity=\"0.75\">", X(x), Y(y), r, fill, stroke));
            if (title != null) S.Append("<title>").Append(H(title)).Append("</title>");
            S.Append("</circle>");
        }
        public void Poly(List<double[]> pts, string color, double width)
        {
            if (pts.Count == 0) return;
            StringBuilder p = new StringBuilder();
            foreach (double[] q in pts) p.Append(F("{0:0.#},{1:0.#} ", X(q[0]), Y(q[1])));
            S.Append(F("<polyline points=\"{0}\" fill=\"none\" stroke=\"{1}\" stroke-width=\"{2}\"/>", p.ToString().Trim(), color, width));
        }
        public void HLine(double y, string color) { S.Append(F("<line x1=\"{0}\" y1=\"{1:0.#}\" x2=\"{2}\" y2=\"{1:0.#}\" stroke=\"{3}\" stroke-dasharray=\"4 3\"/>", ML, Y(y), ML + W, color)); }
        public void Legend(int i, string color, string text) { S.Append(F("<rect x=\"{0}\" y=\"{1}\" width=\"10\" height=\"10\" fill=\"{2}\"/><text x=\"{3}\" y=\"{4}\">{5}</text>", ML + W - 150, MT + 6 + 14 * i, color, ML + W - 136, MT + 15 + 14 * i, H(text))); }
        public string End() { S.Append("</svg></div>"); return S.ToString(); }
    }

    static List<double> Ticks(double lo, double hi, bool integer)
    {
        List<double> t = new List<double>();
        double range = hi - lo; if (range <= 0) return t;
        double raw = range / 5, mag = Math.Pow(10, Math.Floor(Math.Log10(raw))), step = mag;
        foreach (double m in new double[] { 1, 2, 5, 10 }) { if (raw <= m * mag) { step = m * mag; break; } }
        if (integer) step = Math.Max(1, Math.Ceiling(step));
        for (double v = Math.Ceiling(lo / step) * step; v <= hi + 1e-9 * range; v += step) t.Add(v);
        return t;
    }
    static string PhaseColor(string phase)
    {
        if (phase == "greedy") return Blue; if (phase == "swap") return Orange; if (phase == "removal") return Purple; if (phase == "base") return Grey;
        return Green;
    }

    static string Convergence(List<Row> steps, string col, string ylabel, double limit, bool isCount)
    {
        if (steps.Count == 0) return "";
        double hi = 0; foreach (Row r in steps) hi = Math.Max(hi, r.Num(col));
        Plot p = new Plot("accepted sets: " + ylabel, 360, 220, 0, Math.Max(1, steps.Count - 1), 0, hi * 1.08 + (hi == 0 ? 1 : 0), "accepted step (base, greedy, swap, removal)", ylabel, true);
        List<double[]> pts = new List<double[]>();
        for (int i = 0; i < steps.Count; i++) pts.Add(new double[] { i, steps[i].Num(col) });
        p.Poly(pts, "#bbb", 1.2);
        for (int i = 0; i < steps.Count; i++)
        {
            Row r = steps[i];
            p.Dot(i, r.Num(col), 4, PhaseColor(r.C["Phase"]), "none", F("{0} {1}: {2} joint(s) [{3}]  excess {4}, over {5}, max {6}", r.C["Phase"], r.C["Trial"], r.C["Joints"], r.C["Set"], G(r.Num("Excess")), r.C["Over"], G(r.Num("Max"))));
        }
        p.Legend(0, Blue, "greedy"); p.Legend(1, Orange, "swap"); p.Legend(2, Purple, "removal");
        return p.End();
    }

    // Every trial of every greedy step (and swap pass): how peaked the landscape is at each step.
    static string Landscape(List<Row> evals)
    {
        List<Row> tr = new List<Row>(); int singular = 0, maxStep = 0; double hi = 0;
        foreach (Row e in evals)
        {
            if (e.C["Phase"] != "greedy") continue;
            if (string.Equals(e.C["Singular"], "True", StringComparison.OrdinalIgnoreCase)) { singular++; continue; }
            tr.Add(e); maxStep = Math.Max(maxStep, (int)e.Num("Step")); hi = Math.Max(hi, e.Num("Excess"));
        }
        if (tr.Count == 0) return "";
        Plot p = new Plot(F("greedy trials: {0} sets, {1} singular (mechanism, not drawn)", tr.Count, singular), 360, 220, 0.5, maxStep + 0.5, 0, hi * 1.08 + (hi == 0 ? 1 : 0), "greedy step", "sum of excess with the trial joint added", true);
        Dictionary<int, int> perStep = new Dictionary<int, int>();
        foreach (Row e in tr) { int s = (int)e.Num("Step"); perStep[s] = (perStep.ContainsKey(s) ? perStep[s] : 0) + 1; }
        Dictionary<int, int> k = new Dictionary<int, int>();
        foreach (Row e in tr)
        {
            int s = (int)e.Num("Step"); int i = k.ContainsKey(s) ? k[s] : 0; k[s] = i + 1;
            double jitter = perStep[s] > 1 ? -0.3 + 0.6 * i / (perStep[s] - 1) : 0;
            bool acc = string.Equals(e.C["Accepted"], "True", StringComparison.OrdinalIgnoreCase);
            p.Dot(s + jitter, e.Num("Excess"), acc ? 5 : 2.5, acc ? Red : Blue, acc ? Red : "none", F("+ joint {0}: excess {1}, over {2}, max {3}, pivot {4}", e.C["Trial"], G(e.Num("Excess")), e.C["Over"], G(e.Num("Max")), G(e.Num("Pivot"))));
        }
        return p.End();
    }

    static string Gains(List<Row> steps)
    {
        List<string> lab = new List<string>(); List<double> gain = new List<double>();
        for (int i = 1; i < steps.Count; i++)
        {
            Row r = steps[i];
            double g = steps[i - 1].Num("Excess") - r.Num("Excess");
            lab.Add(r.C["Phase"] == "greedy" ? "+ " + r.C["Trial"] : r.C["Phase"] == "swap" ? "swap " + r.C["Trial"] : "- " + r.C["Trial"]);
            gain.Add(g);
        }
        if (lab.Count == 0) return "";
        double hi = 0; foreach (double g in gain) hi = Math.Max(hi, Math.Abs(g));
        int rowH = 16, W = 260, m = 110, Hh = lab.Count * rowH + 10;
        StringBuilder s = new StringBuilder();
        s.Append("<div class=\"card\"><svg width=\"").Append(W + m + 70).Append("\" height=\"").Append(Hh + 30).Append("\" font-size=\"10\">");
        s.Append(F("<text x=\"{0}\" y=\"14\" font-size=\"12\" font-weight=\"bold\">gain in excess per accepted step</text>", m));
        for (int i = 0; i < lab.Count; i++)
        {
            double y = 22 + i * rowH, wdt = hi > 0 ? Math.Abs(gain[i]) / hi * W : 0;
            s.Append(F("<text x=\"{0}\" y=\"{1}\" text-anchor=\"end\">{2}</text>", m - 4, y + 11, H(lab[i])));
            s.Append(F("<rect x=\"{0}\" y=\"{1}\" width=\"{2:0.#}\" height=\"11\" fill=\"{3}\"/>", m, y + 2, wdt, gain[i] >= 0 ? Blue : Red));
            s.Append(F("<text x=\"{0:0.#}\" y=\"{1}\" fill=\"#333\">{2}</text>", m + wdt + 3, y + 11, G(gain[i])));
        }
        s.Append("</svg></div>");
        return s.ToString();
    }

    static string SortedCurve(Dictionary<string, double> con, Dictionary<string, double> bas, Dictionary<string, double> fin, Dictionary<string, double> dir, double limit)
    {
        double hi = limit; int n = 0;
        foreach (Dictionary<string, double> d in new Dictionary<string, double>[] { con, bas, fin, dir }) { foreach (double v in d.Values) hi = Math.Max(hi, v); n = Math.Max(n, d.Count); }
        if (n == 0) return "";
        Plot p = new Plot("DCR envelope, frames sorted from worst", 420, 240, 1, n, 0, hi * 1.05, "frame rank", "DCR envelope", true);
        p.HLine(limit, Red);
        string[] names = { "connected", "link model, nothing released", "chosen set released", "direct SAP disconnect" };
        string[] cols = { Grey, "#555", Blue, Red };
        Dictionary<string, double>[] ds = { con, bas, fin, dir };
        int li = 0;
        for (int i = 0; i < 4; i++)
        {
            if (ds[i].Count == 0) continue;
            List<double> v = new List<double>(ds[i].Values); v.Sort(); v.Reverse();
            List<double[]> pts = new List<double[]>();
            for (int k = 0; k < v.Count; k++) pts.Add(new double[] { k + 1, v[k] });
            p.Poly(pts, cols[i], i == 3 ? 1 : 1.8);
            p.Legend(li++, cols[i], names[i]);
        }
        return p.End();
    }

    static string Histogram(Dictionary<string, double> before, Dictionary<string, double> after, double limit)
    {
        if (before.Count == 0 && after.Count == 0) return "";
        double hi = limit; foreach (double v in before.Values) hi = Math.Max(hi, v); foreach (double v in after.Values) hi = Math.Max(hi, v);
        double bw = 0.1; int nb = (int)Math.Ceiling(hi / bw) + 1;
        int[] cb = new int[nb], ca = new int[nb];
        foreach (double v in before.Values) cb[Math.Min(nb - 1, (int)Math.Floor(v / bw))]++;
        foreach (double v in after.Values) ca[Math.Min(nb - 1, (int)Math.Floor(v / bw))]++;
        int top = 0; for (int i = 0; i < nb; i++) top = Math.Max(top, Math.Max(cb[i], ca[i]));
        Plot p = new Plot("DCR envelope histogram (bin 0.1)", 420, 240, 0, nb * bw, 0, top * 1.08, "DCR envelope", "frames");
        double half = p.W / (double)nb / 2;
        for (int i = 0; i < nb; i++)
        {
            double x0 = p.X(i * bw);
            if (cb[i] > 0) p.S.Append(F("<rect x=\"{0:0.#}\" y=\"{1:0.#}\" width=\"{2:0.#}\" height=\"{3:0.#}\" fill=\"{4}\"><title>{5}-{6}: {7} frames before</title></rect>", x0, p.Y(cb[i]), half, p.Y(0) - p.Y(cb[i]), Grey, G(i * bw), G((i + 1) * bw), cb[i]));
            if (ca[i] > 0) p.S.Append(F("<rect x=\"{0:0.#}\" y=\"{1:0.#}\" width=\"{2:0.#}\" height=\"{3:0.#}\" fill=\"{4}\"><title>{5}-{6}: {7} frames after</title></rect>", x0 + half, p.Y(ca[i]), half, p.Y(0) - p.Y(ca[i]), Blue, G(i * bw), G((i + 1) * bw), ca[i]));
        }
        p.S.Append(F("<line x1=\"{0:0.#}\" y1=\"{1}\" x2=\"{0:0.#}\" y2=\"{2}\" stroke=\"{3}\" stroke-dasharray=\"4 3\"/>", p.X(limit), p.MT, p.MT + p.Hh, Red));
        p.Legend(0, Grey, "before"); p.Legend(1, Blue, "chosen set released");
        return p.End();
    }

    static string PivotHist(List<Row> evals)
    {
        List<double> lp = new List<double>(); int singular = 0;
        foreach (Row e in evals)
        {
            if (e.C["Phase"] == "base") continue;
            if (string.Equals(e.C["Singular"], "True", StringComparison.OrdinalIgnoreCase)) { singular++; continue; }
            double pv = e.Num("Pivot"); if (pv > 0) lp.Add(Math.Log10(pv));
        }
        if (lp.Count == 0) return "";
        int lo = (int)Math.Floor(Min(lp)), hi = (int)Math.Ceiling(Max(lp)); if (hi == lo) hi = lo + 1;
        int[] c = new int[hi - lo];
        foreach (double v in lp) c[Math.Min(c.Length - 1, (int)Math.Floor(v) - lo)]++;
        int top = 0; foreach (int k in c) top = Math.Max(top, k);
        Plot p = new Plot(F("release-system pivot ratio: {0} sets, {1} singular (mechanisms)", lp.Count, singular), 360, 200, lo, hi, 0, top * 1.08, "log10(min pivot / max pivot); singular = below -13", "sets");
        for (int i = 0; i < c.Length; i++)
            if (c[i] > 0) p.S.Append(F("<rect x=\"{0:0.#}\" y=\"{1:0.#}\" width=\"{2:0.#}\" height=\"{3:0.#}\" fill=\"{4}\"><title>1e{5}..1e{6}: {7} sets</title></rect>", p.X(lo + i) + 1, p.Y(c[i]), p.X(lo + i + 1) - p.X(lo + i) - 2, p.Y(0) - p.Y(c[i]), Blue, lo + i, lo + i + 1, c[i]));
        return p.End();
    }

    static string Timing(List<Row> evals)
    {
        double maxJ = 0, maxMs = 0; int n = 0;
        foreach (Row e in evals) { maxJ = Math.Max(maxJ, e.Num("Joints")); maxMs = Math.Max(maxMs, e.Num("Ms")); n++; }
        if (n == 0) return "";
        Plot p = new Plot(F("cost per evaluation ({0} sets)", n), 360, 200, 0, maxJ + 1, 0, maxMs * 1.08 + (maxMs == 0 ? 1 : 0), "joints in the set", "ms", true);
        foreach (Row e in evals) p.Dot(e.Num("Joints"), e.Num("Ms"), 2.5, PhaseColor(e.C["Phase"]), "none", null);
        return p.End();
    }
    static double Min(List<double> l) { double m = double.MaxValue; foreach (double v in l) m = Math.Min(m, v); return m; }
    static double Max(List<double> l) { double m = double.MinValue; foreach (double v in l) m = Math.Max(m, v); return m; }

    // ------------------------------------------------------------------ maps

    // supports: support joints of the connected survey (Class = support); Support / Restrains are empty for a survey
    // made before supports were sub-classed.
    static void LoadGeometry(string connectedDir, out Dictionary<string, double[]> xyz, out List<string[]> segs, out List<Row> supports)
    {
        xyz = new Dictionary<string, double[]>(StringComparer.Ordinal); segs = new List<string[]>(); supports = new List<Row>();
        if (string.IsNullOrEmpty(connectedDir)) return;
        string jp = Path.Combine(connectedDir, "duct-joints.tsv"), fp = Path.Combine(connectedDir, "frames.tsv");
        if (!File.Exists(jp) || !File.Exists(fp)) return;
        foreach (Row r in Tsv(jp))
        {
            xyz[r.C["Joint"]] = new double[] { r.Num("X_in"), r.Num("Y_in"), r.Num("Z_in") };
            if (r.C["Class"] == "support")
            {
                if (!r.C.ContainsKey("Support")) { r.C["Support"] = ""; r.C["Restrains"] = ""; }
                supports.Add(r);
            }
        }
        foreach (Row r in Tsv(fp)) if (xyz.ContainsKey(r.C["JointI"]) && xyz.ContainsKey(r.C["JointJ"])) segs.Add(new string[] { r.C["Frame"], r.C["JointI"], r.C["JointJ"] });
    }

    static string Ramp(double v)
    {
        if (double.IsNaN(v)) return "#cfcfcf";
        int[] g = { 44, 160, 44 }, y = { 255, 191, 0 }, r = { 214, 39, 40 }, k = { 107, 0, 16 };
        if (v <= 0.5) return Rgb(g);
        if (v <= 1.0) return Lerp(g, y, (v - 0.5) / 0.5);
        if (v <= 1.5) return Lerp(y, r, (v - 1.0) / 0.5);
        if (v <= 3.0) return Lerp(r, k, (v - 1.5) / 1.5);
        return Rgb(k);
    }
    static string Lerp(int[] a, int[] b, double t) { int[] c = new int[3]; for (int i = 0; i < 3; i++) c[i] = (int)Math.Round(a[i] + (b[i] - a[i]) * t); return Rgb(c); }
    static string Rgb(int[] c) { return F("rgb({0},{1},{2})", c[0], c[1], c[2]); }

    // proj 0 = plan (X, Y), 1 = elevation (X, Z). values: frame -> DCR (null = geometry only).
    // selected: candidates-selected.tsv (named), cands: candidates-used.tsv (in the link model), chosen: chosen.tsv.
    // Wheel = zoom about the cursor, drag = pan, double-click = reset; everything inside the map scales with it.
    const string ZoomScript = "<script>document.querySelectorAll('svg.zoommap').forEach(function(svg){"
        + "var v0=svg.getAttribute('viewBox').split(' ').map(Number),v=v0.slice(),drag=null;"
        + "function set(){svg.setAttribute('viewBox',v.join(' '));}"
        + "function pt(e){var r=svg.getBoundingClientRect();return [v[0]+(e.clientX-r.left)/r.width*v[2],v[1]+(e.clientY-r.top)/r.height*v[3]];}"
        + "svg.addEventListener('wheel',function(e){e.preventDefault();var p=pt(e),f=e.deltaY<0?0.8:1.25;if(v[2]*f>v0[2]*2)f=v0[2]*2/v[2];v=[p[0]-(p[0]-v[0])*f,p[1]-(p[1]-v[1])*f,v[2]*f,v[3]*f];set();},{passive:false});"
        + "svg.addEventListener('mousedown',function(e){drag=[e.clientX,e.clientY,v[0],v[1]];});"
        + "window.addEventListener('mousemove',function(e){if(!drag)return;var r=svg.getBoundingClientRect();v[0]=drag[2]-(e.clientX-drag[0])/r.width*v[2];v[1]=drag[3]-(e.clientY-drag[1])/r.height*v[3];set();});"
        + "window.addEventListener('mouseup',function(){drag=null;});"
        + "svg.addEventListener('dblclick',function(){v=v0.slice();set();});"
        + "});</script>";

    // Standalone page: the released DCR map, plan and elevation, full width.
    static void WriteReleasedMap(string path, string title, string legend, Dictionary<string, double[]> xyz, List<string[]> segs, Dictionary<string, double> envFinal, List<Row> chosen, List<Row> supports)
    {
        StringBuilder sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>").Append(H(title)).Append(": released map</title><style>")
          .Append("body{font:14px/1.45 Segoe UI,Arial,sans-serif;margin:24px;color:#222}h1{font-size:20px}h3{font-size:14px;margin:16px 0 4px}.card{border:1px solid #ddd;padding:8px;background:#fff;display:inline-block}small{color:#666}svg text{font-family:Segoe UI,Arial}svg.zoommap{max-width:100%;height:auto}")
          .Append("</style></head><body><h1>").Append(H(title)).Append(": chosen set released</h1><p><small>").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm", Inv)).Append("</small></p>")
          .Append(legend);
        foreach (int proj in new int[] { 0, 1 })
        {
            sb.Append("<h3>").Append(proj == 0 ? "Plan (X right, Y up)" : "Elevation (X right, Z up)").Append("</h3>");
            sb.Append(Map("chosen set released: DCR envelope", proj, xyz, segs, envFinal, null, null, chosen, supports, 1300));
        }
        sb.Append("<p><small>Wheel to zoom, drag to pan, double-click to reset.</small></p>").Append(ZoomScript).Append("</body></html>");
        File.WriteAllText(path, sb.ToString());
    }

    static string Map(string title, int proj, Dictionary<string, double[]> xyz, List<string[]> segs, Dictionary<string, double> values, List<Row> selected, List<Row> cands, List<Row> chosen, List<Row> supports)
    {
        return Map(title, proj, xyz, segs, values, selected, cands, chosen, supports, 440);
    }
    static string Map(string title, int proj, Dictionary<string, double[]> xyz, List<string[]> segs, Dictionary<string, double> values, List<Row> selected, List<Row> cands, List<Row> chosen, List<Row> supports, int width)
    {
        int iu = 0, iv = proj == 0 ? 1 : 2;
        double ulo = double.MaxValue, uhi = double.MinValue, vlo = double.MaxValue, vhi = double.MinValue;
        foreach (double[] p in xyz.Values) { ulo = Math.Min(ulo, p[iu]); uhi = Math.Max(uhi, p[iu]); vlo = Math.Min(vlo, p[iv]); vhi = Math.Max(vhi, p[iv]); }
        double du = Math.Max(uhi - ulo, 1), dv = Math.Max(vhi - vlo, 1);
        int W = width, m = 14; int Hh = (int)Math.Max(140, Math.Min(480 * width / 440, W * dv / du));
        double sc = Math.Min((W - 2 * m) / du, (Hh - 2 * m) / dv);
        double ox = m + ((W - 2 * m) - du * sc) / 2, oy = m + ((Hh - 2 * m) - dv * sc) / 2;
        StringBuilder s = new StringBuilder();
        double k = W / 440.0;   // symbol and line scale: the same look at any map width
        s.Append(F("<div class=\"card\"><svg class=\"zoommap\" width=\"{0}\" height=\"{1}\" viewBox=\"0 0 {0} {1}\" font-size=\"10\">", W, Hh + 20));
        s.Append(F("<text x=\"4\" y=\"12\" font-size=\"12\" font-weight=\"bold\">{0}</text>", H(title)));
        s.Append(F("<g transform=\"translate(0,20)\"><rect x=\"0\" y=\"0\" width=\"{0}\" height=\"{1}\" fill=\"#fafafa\" stroke=\"#ddd\"/>", W, Hh));
        foreach (string[] sg in segs)
        {
            double[] a = xyz[sg[1]], b = xyz[sg[2]];
            double v = double.NaN; if (values != null) values.TryGetValue(sg[0], out v); if (values != null && !values.ContainsKey(sg[0])) v = double.NaN;
            string col = values == null ? "#b0b0b0" : Ramp(v);
            double x1 = ox + (a[iu] - ulo) * sc, y1 = Hh - oy - (a[iv] - vlo) * sc, x2 = ox + (b[iu] - ulo) * sc, y2 = Hh - oy - (b[iv] - vlo) * sc;
            if (Math.Abs(x2 - x1) + Math.Abs(y2 - y1) < 0.5)   // runs into / out of the view: a thick dot in the frame's colour
            {
                s.Append(F("<circle cx=\"{0:0.#}\" cy=\"{1:0.#}\" r=\"{2}\" fill=\"{3}\"><title>frame {4} (out of plane){5}</title></circle>", x1, y1, (values == null ? 2.5 : 3.5) * k, col, H(sg[0]), double.IsNaN(v) ? "" : ": DCR " + G(v)));
                continue;
            }
            s.Append(F("<line x1=\"{0:0.#}\" y1=\"{1:0.#}\" x2=\"{2:0.#}\" y2=\"{3:0.#}\" stroke=\"{4}\" stroke-width=\"{5}\" stroke-linecap=\"round\"><title>frame {6}{7}</title></line>",
                ox + (a[iu] - ulo) * sc, Hh - oy - (a[iv] - vlo) * sc, ox + (b[iu] - ulo) * sc, Hh - oy - (b[iv] - vlo) * sc, col, (values == null ? 1.5 : 2.5) * k, H(sg[0]), double.IsNaN(v) ? "" : ": DCR " + G(v)));
        }
        HashSet<string> drawnAt = new HashSet<string>();
        if (supports != null)
            foreach (Row r in supports)
            {
                double[] p; if (!xyz.TryGetValue(r.C["Joint"], out p)) continue;
                double cx = ox + (p[iu] - ulo) * sc, cy = Hh - oy - (p[iv] - vlo) * sc;
                if (!drawnAt.Add(F("{0:0}|{1:0}", cx, cy))) continue;   // supports stacked along a riser: the first one only
                string kind = r.C["Support"], tip = F("<title>joint {0}: {1}{2}</title>", H(r.C["Joint"]), kind.Length == 0 ? "support" : kind, r.C["Restrains"].Length == 0 ? "" : " (" + H(r.C["Restrains"]) + ")");
                s.Append(F("<g transform=\"translate({0:0.##},{1:0.##}) scale({2:0.###}) translate({3:0.##},{4:0.##})\">", cx, cy, k, -cx, -cy))
                 .Append(SupportSymbol(r.C["Joint"], p, cx, cy, iu, iv, xyz, segs, kind, r.C["Restrains"], tip)).Append("</g>");
            }
        if (selected != null)
        {
            HashSet<string> inModel = new HashSet<string>(StringComparer.Ordinal);
            if (cands != null) foreach (Row r in cands) inModel.Add(r.C["Joint"]);
            foreach (Row r in selected)
            {
                double[] p; if (inModel.Contains(r.C["Joint"]) || !xyz.TryGetValue(r.C["Joint"], out p)) continue;
                s.Append(F("<circle cx=\"{0:0.#}\" cy=\"{1:0.#}\" r=\"3\" fill=\"none\" stroke=\"#888\"><title>joint {2}: not in the link model ({3})</title></circle>", ox + (p[iu] - ulo) * sc, Hh - oy - (p[iv] - vlo) * sc, H(r.C["Joint"]), H(r.C["Reason"])));
            }
        }
        if (cands != null)
            foreach (Row r in cands)
            {
                double[] p; if (!xyz.TryGetValue(r.C["Joint"], out p)) continue;
                bool ex = r.C["Excluded"].Length > 0;
                s.Append(F("<circle cx=\"{0:0.#}\" cy=\"{1:0.#}\" r=\"3\" fill=\"{2}\" stroke=\"none\"><title>joint {3} (span {4}){5}</title></circle>", ox + (p[iu] - ulo) * sc, Hh - oy - (p[iv] - vlo) * sc, ex ? "#bbb" : Blue, H(r.C["Joint"]), H(r.C["Span"]), ex ? ": excluded, " + H(r.C["Excluded"]) : ": tried"));
            }
        if (chosen != null)
            foreach (Row r in chosen)
            {
                double[] p; if (!xyz.TryGetValue(r.C["Joint"], out p)) continue;
                double cx = ox + (p[iu] - ulo) * sc, cy = Hh - oy - (p[iv] - vlo) * sc;
                if (values != null)
                {
                    string jt = F("<title>expansion joint {0} (greedy step {1})</title>", H(r.C["Joint"]), H(r.C["AddedAtStep"]));
                    double[] sd = ScreenDir(r.C["Joint"], p, iu, iv, xyz, segs);
                    if (sd == null)   // duct runs out of the view: a ring around its dot
                        s.Append(F("<circle cx=\"{0:0.#}\" cy=\"{1:0.#}\" r=\"{4:0.#}\" fill=\"none\" stroke=\"{2}\" stroke-width=\"{5:0.#}\">{3}</circle>", cx, cy, JointColour, jt, 5 * k, 2 * k));
                    else
                        s.Append(F("<line x1=\"{0:0.#}\" y1=\"{1:0.#}\" x2=\"{2:0.#}\" y2=\"{3:0.#}\" stroke=\"{4}\" stroke-width=\"{6:0.#}\">{5}</line>", cx - sd[1] * 6 * k, cy + sd[0] * 6 * k, cx + sd[1] * 6 * k, cy - sd[0] * 6 * k, JointColour, jt, 2.2 * k));
                    continue;
                }
                s.Append(F("<circle cx=\"{0:0.#}\" cy=\"{1:0.#}\" r=\"7\" fill=\"none\" stroke=\"{2}\" stroke-width=\"2.5\"><title>chosen: joint {3} (greedy step {4})</title></circle><text x=\"{5:0.#}\" y=\"{6:0.#}\" fill=\"{2}\" font-weight=\"bold\">{3}</text>", cx, cy, Red, H(r.C["Joint"]), H(r.C["AddedAtStep"]), cx + 8, cy - 6));
            }
        s.Append("</g></svg></div>");
        return s.ToString();
    }

    // ------------------------------------------------------------------ support symbols (piping-drawing convention)

    // Restrains text from the survey ("U all but X; R none", "U Y", "U (0.71,0.71,0); R all") -> held(direction) tests.
    class Held
    {
        public string Mode = "none";   // all | none | one (held along D) | but (held except along D)
        public double[] D;
        public bool Holds(double[] d)
        {
            if (Mode == "all") return true;
            if (Mode == "none" || D == null) return false;
            double dot = Math.Abs(d[0] * D[0] + d[1] * D[1] + d[2] * D[2]);
            return Mode == "one" ? dot > 0.7 : Math.Sqrt(Math.Max(0, 1 - dot * dot)) > 0.7;
        }
    }
    static Held ParseHeld(string restrains, string prefix)
    {
        Held h = new Held();
        foreach (string part in restrains.Split(';'))
        {
            string t = part.Trim();
            if (!t.StartsWith(prefix + " ", StringComparison.Ordinal)) continue;
            t = t.Substring(prefix.Length + 1).Trim();
            if (t == "all" || t == "none") { h.Mode = t; return h; }
            if (t.StartsWith("all but ", StringComparison.Ordinal)) { h.Mode = "but"; t = t.Substring(8).Trim(); } else h.Mode = "one";
            h.D = Dir(t);
            if (h.D == null) h.Mode = "none";
        }
        return h;
    }
    static double[] Dir(string t)
    {
        if (t == "X") return new double[] { 1, 0, 0 };
        if (t == "Y") return new double[] { 0, 1, 0 };
        if (t == "Z") return new double[] { 0, 0, 1 };
        string[] c = t.Trim('(', ')').Split(',');
        double a, b, e;
        if (c.Length == 3 && double.TryParse(c[0], NumberStyles.Float, Inv, out a) && double.TryParse(c[1], NumberStyles.Float, Inv, out b) && double.TryParse(c[2], NumberStyles.Float, Inv, out e))
        {
            double l = Math.Sqrt(a * a + b * b + e * e);
            if (l > 0) return new double[] { a / l, b / l, e / l };
        }
        return null;
    }
    const string SymbolColour = "#000";
    const string JointColour = "#7b2cbf";   // expansion joints: purple, clear of the DCR ramp and the black supports

    // Unit screen direction of the first frame at the joint that shows in this view; null if every frame there runs out of it.
    static double[] ScreenDir(string joint, double[] p, int iu, int iv, Dictionary<string, double[]> xyz, List<string[]> segs)
    {
        foreach (string[] sg in segs)
        {
            if (sg[1] != joint && sg[2] != joint) continue;
            double[] q = xyz[sg[1] == joint ? sg[2] : sg[1]];
            double ex = q[iu] - p[iu], ey = -(q[iv] - p[iv]), el = Math.Sqrt(ex * ex + ey * ey);
            double tl = Math.Sqrt((q[0] - p[0]) * (q[0] - p[0]) + (q[1] - p[1]) * (q[1] - p[1]) + (q[2] - p[2]) * (q[2] - p[2]));
            if (tl > 0 && el / tl > 0.3) return new double[] { ex / el, ey / el };
        }
        return null;
    }

    // Piping-drawing support symbol at a joint, in the view's screen space:
    //   vertical translation held (rest)     -> triangle under the joint, blue
    //   held along the duct (line stop)      -> two bars across the duct, either side of the joint
    //   held across the duct, horizontal     -> two bars along the duct, either side (guide); a direction that points
    //                                           out of the view is not drawn
    //   rotation held about X / Y / Z        -> arc segments around the joint in the axis colour
    // All black: shape and position carry the state. Unclassed supports: grey square. An anchor on a duct running into /
    // out of the view draws nothing but its hover target (those stack at every riser in plan).
    static string SupportSymbol(string joint, double[] p, double cx, double cy, int iu, int iv, Dictionary<string, double[]> xyz, List<string[]> segs, string kind, string restrains, string tip)
    {
        StringBuilder s = new StringBuilder();
        s.Append("<g>").Append(tip);
        if (kind.Length == 0 || restrains.Length == 0)
        {
            s.Append(F("<rect x=\"{0:0.#}\" y=\"{1:0.#}\" width=\"8\" height=\"8\" fill=\"#999\" fill-opacity=\"0.15\" stroke=\"#999\" stroke-width=\"1.3\"/>", cx - 4, cy - 4));
            return s.Append("</g>").ToString();
        }
        Held U = ParseHeld(restrains, "U"), R = ParseHeld(restrains, "R");
        // duct axis in 3D: the first frame at the joint
        double[] a = null;
        foreach (string[] sg in segs)
        {
            if (sg[1] != joint && sg[2] != joint) continue;
            double[] q = xyz[sg[1] == joint ? sg[2] : sg[1]];
            double ex = q[0] - p[0], ey = q[1] - p[1], ez = q[2] - p[2], l = Math.Sqrt(ex * ex + ey * ey + ez * ez);
            if (l > 1e-9) { a = new double[] { ex / l, ey / l, ez / l }; break; }
        }
        if (a == null) a = new double[] { 1, 0, 0 };
        if (Math.Sqrt(a[iu] * a[iu] + a[iv] * a[iv]) < 0.3)   // duct runs into / out of the view: its dot only, stacked supports hide
        {
            s.Append(F("<circle cx=\"{0:0.#}\" cy=\"{1:0.#}\" r=\"5\" fill=\"#000\" fill-opacity=\"0\"/>", cx, cy));
            return s.Append("</g>").ToString();
        }
        // three test directions: along the duct, horizontal across it, and the third
        double[] h = Math.Abs(a[2]) > 0.9 ? new double[] { 1, 0, 0 } : Unit(new double[] { -a[1], a[0], 0 });
        double[] v = Unit(new double[] { a[1] * h[2] - a[2] * h[1], a[2] * h[0] - a[0] * h[2], a[0] * h[1] - a[1] * h[0] });
        const double g = 1.9, half = 1.7, w = 0.65, axialOff = 2.4;
        bool rest = false;
        foreach (double[] d in new double[][] { a, h, v })
        {
            if (!U.Holds(d)) continue;
            if (Math.Abs(d[2]) > 0.7) { rest = true; continue; }
            double px = d[iu], py = -d[iv], pl = Math.Sqrt(px * px + py * py);
            if (pl < 0.3) continue;   // points out of the view
            px /= pl; py /= pl;
            bool axial = d == a;
            double off = axial ? axialOff : g;
            string col = SymbolColour;
            foreach (int sgn in new int[] { -1, 1 })
            {
                double bx = cx + sgn * px * off, by = cy + sgn * py * off;   // bar centre, bar runs perpendicular to (px, py)
                s.Append(F("<line x1=\"{0:0.#}\" y1=\"{1:0.#}\" x2=\"{2:0.#}\" y2=\"{3:0.#}\" stroke=\"{4}\" stroke-width=\"{5}\"/>", bx - py * half, by + px * half, bx + py * half, by - px * half, col, w));
            }
        }
        if (rest)
            s.Append(F("<path d=\"M{0:0.#},{1:0.#} l1.4,2.1 l-2.8,0 z\" fill=\"{2}\"/>", cx, cy + g + 0.5, SymbolColour));
        double[][] axes = { new double[] { 1, 0, 0 }, new double[] { 0, 1, 0 }, new double[] { 0, 0, 1 } };
        const double rr = 3.6;
        for (int k = 0; k < 3; k++)
        {
            if (!R.Holds(axes[k])) continue;
            double a0 = (200 + k * 55) * Math.PI / 180, a1 = (240 + k * 55) * Math.PI / 180;
            s.Append(F("<path d=\"M{0:0.#},{1:0.#} A{2},{2} 0 0 1 {3:0.#},{4:0.#}\" fill=\"none\" stroke=\"{5}\" stroke-width=\"{6}\"/>", cx + rr * Math.Cos(a0), cy + rr * Math.Sin(a0), rr, cx + rr * Math.Cos(a1), cy + rr * Math.Sin(a1), SymbolColour, w));
        }
        s.Append(F("<circle cx=\"{0:0.#}\" cy=\"{1:0.#}\" r=\"6\" fill=\"#000\" fill-opacity=\"0\"/>", cx, cy));   // hover target
        return s.Append("</g>").ToString();
    }
    static double[] Unit(double[] d) { double l = Math.Sqrt(d[0] * d[0] + d[1] * d[1] + d[2] * d[2]); return l > 0 ? new double[] { d[0] / l, d[1] / l, d[2] / l } : d; }

    static string H(string s) { return (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;"); }
    static string G(double v) { return double.IsNaN(v) ? "" : v.ToString("G4", Inv); }
    static string F(string fmt, params object[] args) { return string.Format(Inv, fmt, args); }
}
