// HTML report of a release check: verdicts, comparison tables, scatter plots (inline SVG, no scripts),
// and the DCR envelopes before / after the release next to the direct SAP run. C# 5, no dependencies.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

public static class DuctReport
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // Frame -> DCR_envelope from a DuctEvaluate dcr-frames.tsv (null path = absent).
    public static Dictionary<string, double> DcrEnvelope(string dcrFramesPath)
    {
        Dictionary<string, double> d = new Dictionary<string, double>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(dcrFramesPath) || !File.Exists(dcrFramesPath)) return d;
        using (StreamReader reader = new StreamReader(dcrFramesPath))
        {
            string[] h = reader.ReadLine().Split('\t');
            int iF = ForceTable.Col(h, "Frame"), iE = ForceTable.Col(h, "DCR_envelope");
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.Length == 0) continue;
                string[] c = line.Split('\t');
                d[c[iF]] = double.Parse(c[iE], NumberStyles.Float, Inv);
            }
        }
        return d;
    }

    public static void Write(string path, string title, string[] facts, string releaseText,
        DuctRelease.Compare baseVsConnected, DuctRelease.Compare releasedVsDirect,
        Dictionary<string, double> dcrConnected, Dictionary<string, double> dcrReleased, Dictionary<string, double> dcrDirect, double limit)
    {
        StringBuilder sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>").Append(H(title)).Append("</title><style>")
          .Append("body{font:14px/1.45 Segoe UI,Arial,sans-serif;margin:24px;color:#222;max-width:1200px}h1{font-size:22px}h2{font-size:17px;margin-top:28px;border-bottom:1px solid #ccc}")
          .Append("table{border-collapse:collapse;margin:8px 0}td,th{border:1px solid #ddd;padding:3px 8px;text-align:right;font-variant-numeric:tabular-nums}th{background:#f3f3f3}td:first-child,th:first-child{text-align:left}")
          .Append(".ok{color:#0a7a2f;font-weight:bold}.bad{color:#b00020;font-weight:bold}pre{background:#f7f7f7;padding:8px;overflow-x:auto;font-size:12px}")
          .Append(".row{display:flex;flex-wrap:wrap;gap:16px}.card{border:1px solid #ddd;padding:8px}small{color:#666}")
          .Append("</style></head><body>");
        sb.Append("<h1>").Append(H(title)).Append("</h1><p><small>").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm", Inv)).Append("</small></p><ul>");
        foreach (string f in facts) sb.Append("<li>").Append(H(f)).Append("</li>");
        sb.Append("</ul>");

        sb.Append("<h2>Verdicts</h2><table><tr><th>check</th><th>rows</th><th>worst comp</th><th>diff / peak</th><th>tolerance</th><th>result</th></tr>");
        Verdict(sb, baseVsConnected); Verdict(sb, releasedVsDirect);
        sb.Append("</table>");

        sb.Append("<h2>Release</h2><pre>").Append(H(releaseText)).Append("</pre>");

        foreach (DuctRelease.Compare c in new DuctRelease.Compare[] { baseVsConnected, releasedVsDirect })
        {
            if (c == null) continue;
            sb.Append("<h2>").Append(H(c.Title)).Append("</h2>");
            sb.Append("<table><tr><th>component</th><th>max |diff|</th><th>peak |A|</th><th>diff / peak</th><th>worst at</th></tr>");
            for (int k = 0; k < 6; k++)
                sb.Append("<tr><td>").Append(DuctRelease.Comp[k]).Append("</td><td>").Append(G(c.MaxAbs[k])).Append("</td><td>").Append(G(c.Peak[k]))
                  .Append("</td><td class=\"").Append(c.MaxRelPeak[k] > c.Tolerance ? "bad" : "").Append("\">").Append(G(c.MaxRelPeak[k])).Append("</td><td style=\"text-align:left\">").Append(H(c.WorstAt[k] ?? "")).Append("</td></tr>");
            sb.Append("</table><table><tr><th>case</th><th>step</th><th>rows</th><th>worst comp</th><th>max |diff|</th><th>diff / peak</th></tr>");
            foreach (string[] r in c.PerCase)
                sb.Append("<tr><td>").Append(H(r[0])).Append("</td><td>").Append(H(r[1])).Append("</td><td>").Append(r[2]).Append("</td><td>").Append(H(r[3])).Append("</td><td>").Append(r[4]).Append("</td><td>").Append(r[5]).Append("</td></tr>");
            sb.Append("</table><div class=\"row\">");
            foreach (int k in new int[] { 0, 4, 5, 1, 2, 3 }) sb.Append(Scatter(c, k));
            sb.Append("</div>");
        }

        if (dcrReleased.Count > 0)
        {
            sb.Append("<h2>DCR envelopes (max axial + max M2 + max M3, ends only)</h2>");
            int oc = Over(dcrConnected, limit), orl = Over(dcrReleased, limit), od = Over(dcrDirect, limit);
            sb.Append("<table><tr><th>model</th><th>frames</th><th>over ").Append(G(limit)).Append("</th><th>sum of excess</th><th>max</th></tr>");
            DcrRow(sb, "connected (no joint)", dcrConnected, limit);
            DcrRow(sb, "released by superposition", dcrReleased, limit);
            DcrRow(sb, "direct SAP disconnect", dcrDirect, limit);
            sb.Append("</table><div class=\"row\">");
            if (dcrDirect.Count > 0) sb.Append(DcrScatter("released vs direct", dcrDirect, dcrReleased, limit));
            if (dcrConnected.Count > 0) sb.Append(DcrScatter("released vs connected", dcrConnected, dcrReleased, limit));
            sb.Append("</div>");
            sb.Append(DcrBars(dcrConnected, dcrReleased, dcrDirect, limit, 25));
        }
        sb.Append("</body></html>");
        File.WriteAllText(path, sb.ToString());
    }

    static void Verdict(StringBuilder sb, DuctRelease.Compare c)
    {
        if (c == null) return;
        int w = 0; for (int k = 1; k < 6; k++) if (c.MaxRelPeak[k] > c.MaxRelPeak[w]) w = k;
        sb.Append("<tr><td>").Append(H(c.Title)).Append("</td><td>").Append(c.Rows).Append("</td><td>").Append(DuctRelease.Comp[w]).Append("</td><td>").Append(G(c.MaxRelPeak[w]))
          .Append("</td><td>").Append(G(c.Tolerance)).Append("</td><td class=\"").Append(c.Ok ? "ok\">OK" : "bad\">DIFFERENT").Append("</td></tr>");
    }
    static int Over(Dictionary<string, double> d, double limit) { int n = 0; foreach (double v in d.Values) if (v > limit) n++; return n; }
    static void DcrRow(StringBuilder sb, string name, Dictionary<string, double> d, double limit)
    {
        if (d.Count == 0) return;
        double ex = 0, mx = 0; foreach (double v in d.Values) { if (v > limit) ex += v - limit; mx = Math.Max(mx, v); }
        sb.Append("<tr><td>").Append(H(name)).Append("</td><td>").Append(d.Count).Append("</td><td>").Append(Over(d, limit)).Append("</td><td>").Append(G(ex)).Append("</td><td>").Append(G(mx)).Append("</td></tr>");
    }

    // Scatter of B (y) against A (x) for one component, with the diagonal.
    static string Scatter(DuctRelease.Compare c, int comp)
    {
        double lo = double.MaxValue, hi = double.MinValue; int n = 0;
        foreach (double[] p in c.Points) if ((int)p[0] == comp) { lo = Math.Min(lo, Math.Min(p[1], p[2])); hi = Math.Max(hi, Math.Max(p[1], p[2])); n++; }
        if (n == 0) return "";
        if (hi - lo < 1e-12) { hi = lo + 1; lo -= 1; }
        double pad = 0.05 * (hi - lo); lo -= pad; hi += pad;
        int W = 300, Hh = 300, m = 34;
        StringBuilder s = new StringBuilder();
        s.Append("<div class=\"card\"><svg width=\"").Append(W + m + 10).Append("\" height=\"").Append(Hh + m + 10).Append("\" font-size=\"10\" font-family=\"Segoe UI,Arial\">");
        s.Append(F("<rect x=\"{0}\" y=\"5\" width=\"{1}\" height=\"{2}\" fill=\"#fff\" stroke=\"#999\"/>", m, W, Hh));
        s.Append(F("<line x1=\"{0}\" y1=\"{1}\" x2=\"{2}\" y2=\"5\" stroke=\"#c33\" stroke-dasharray=\"4 3\"/>", m, Hh + 5, m + W));
        foreach (double[] p in c.Points)
        {
            if ((int)p[0] != comp) continue;
            double x = m + (p[1] - lo) / (hi - lo) * W, y = 5 + Hh - (p[2] - lo) / (hi - lo) * Hh;
            s.Append(F("<circle cx=\"{0:0.#}\" cy=\"{1:0.#}\" r=\"2.2\" fill=\"#1f77b4\" fill-opacity=\"0.55\"/>", x, y));
        }
        s.Append(F("<text x=\"{0}\" y=\"{1}\" text-anchor=\"middle\">A (reference) {2}</text>", m + W / 2, Hh + m + 4, DuctRelease.Comp[comp]));
        s.Append(F("<text x=\"10\" y=\"{0}\" transform=\"rotate(-90 10,{0})\" text-anchor=\"middle\">B {1}</text>", 5 + Hh / 2, DuctRelease.Comp[comp]));
        s.Append(F("<text x=\"{0}\" y=\"{1}\" fill=\"#666\">{2}</text>", m + 4, 16, G(hi))).Append(F("<text x=\"{0}\" y=\"{1}\" fill=\"#666\">{2}</text>", m + 4, Hh + 2, G(lo)));
        s.Append(F("<text x=\"{0}\" y=\"{1}\" text-anchor=\"end\" fill=\"#666\">{2} rows, diff/peak {3}</text>", m + W - 4, 16, n, G(c.MaxRelPeak[comp])));
        s.Append("</svg></div>");
        return s.ToString();
    }

    static string DcrScatter(string title, Dictionary<string, double> a, Dictionary<string, double> b, double limit)
    {
        double hi = limit; foreach (double v in a.Values) hi = Math.Max(hi, v); foreach (double v in b.Values) hi = Math.Max(hi, v);
        hi *= 1.05;
        int W = 300, Hh = 300, m = 34;
        StringBuilder s = new StringBuilder();
        s.Append("<div class=\"card\"><svg width=\"").Append(W + m + 10).Append("\" height=\"").Append(Hh + m + 10).Append("\" font-size=\"10\" font-family=\"Segoe UI,Arial\">");
        s.Append(F("<rect x=\"{0}\" y=\"5\" width=\"{1}\" height=\"{2}\" fill=\"#fff\" stroke=\"#999\"/>", m, W, Hh));
        s.Append(F("<line x1=\"{0}\" y1=\"{1}\" x2=\"{2}\" y2=\"5\" stroke=\"#c33\" stroke-dasharray=\"4 3\"/>", m, Hh + 5, m + W));
        double lx = m + limit / hi * W, ly = 5 + Hh - limit / hi * Hh;
        s.Append(F("<line x1=\"{0:0.#}\" y1=\"5\" x2=\"{0:0.#}\" y2=\"{1}\" stroke=\"#999\"/><line x1=\"{2}\" y1=\"{3:0.#}\" x2=\"{4}\" y2=\"{3:0.#}\" stroke=\"#999\"/>", lx, Hh + 5, m, ly, m + W));
        foreach (KeyValuePair<string, double> kv in a)
        {
            double vb; if (!b.TryGetValue(kv.Key, out vb)) continue;
            double x = m + kv.Value / hi * W, y = 5 + Hh - vb / hi * Hh;
            s.Append(F("<circle cx=\"{0:0.#}\" cy=\"{1:0.#}\" r=\"2.5\" fill=\"{2}\" fill-opacity=\"0.6\"/>", x, y, vb > limit ? "#d62728" : "#2ca02c"));
        }
        s.Append(F("<text x=\"{0}\" y=\"{1}\" text-anchor=\"middle\">{2}: x = {3}, y = released</text>", m + W / 2, Hh + m + 4, H(title), title.Contains("direct") ? "direct" : "connected"));
        s.Append(F("<text x=\"{0}\" y=\"{1}\" fill=\"#666\">{2}</text>", m + 4, 16, G(hi)));
        s.Append("</svg></div>");
        return s.ToString();
    }

    // Top frames by connected envelope: bars connected (grey) / released (blue) / direct (thin red mark).
    public static string DcrBars(Dictionary<string, double> con, Dictionary<string, double> rel, Dictionary<string, double> dir, double limit, int top)
    {
        Dictionary<string, double> order = con.Count > 0 ? con : rel;
        List<string> frames = new List<string>(order.Keys);
        frames.Sort(delegate(string x, string y) { return order[y].CompareTo(order[x]); });
        if (frames.Count > top) frames.RemoveRange(top, frames.Count - top);
        double hi = limit;
        foreach (string f in frames) { double v; if (con.TryGetValue(f, out v)) hi = Math.Max(hi, v); if (rel.TryGetValue(f, out v)) hi = Math.Max(hi, v); if (dir.TryGetValue(f, out v)) hi = Math.Max(hi, v); }
        hi *= 1.05;
        int rowH = 16, W = 600, m = 70, Hh = frames.Count * rowH + 10;
        StringBuilder s = new StringBuilder();
        s.Append("<h3 style=\"font-size:14px\">Top ").Append(frames.Count).Append(" frames by connected envelope: grey = connected, blue = released, red mark = direct</h3>");
        s.Append("<svg width=\"").Append(W + m + 60).Append("\" height=\"").Append(Hh + 20).Append("\" font-size=\"10\" font-family=\"Segoe UI,Arial\">");
        double lx = m + limit / hi * W;
        s.Append(F("<line x1=\"{0:0.#}\" y1=\"0\" x2=\"{0:0.#}\" y2=\"{1}\" stroke=\"#999\" stroke-dasharray=\"3 3\"/>", lx, Hh));
        int i = 0;
        foreach (string f in frames)
        {
            double y = 5 + i * rowH, v;
            s.Append(F("<text x=\"{0}\" y=\"{1}\" text-anchor=\"end\">{2}</text>", m - 4, y + 11, H(f)));
            if (con.TryGetValue(f, out v)) s.Append(F("<rect x=\"{0}\" y=\"{1}\" width=\"{2:0.#}\" height=\"5\" fill=\"#bbb\"/>", m, y + 1, v / hi * W));
            if (rel.TryGetValue(f, out v)) { s.Append(F("<rect x=\"{0}\" y=\"{1}\" width=\"{2:0.#}\" height=\"5\" fill=\"#1f77b4\"/>", m, y + 7, v / hi * W)); s.Append(F("<text x=\"{0:0.#}\" y=\"{1}\" fill=\"#333\">{2}</text>", m + v / hi * W + 3, y + 12, G(v))); }
            if (dir.TryGetValue(f, out v)) s.Append(F("<rect x=\"{0:0.#}\" y=\"{1}\" width=\"2\" height=\"13\" fill=\"#d62728\"/>", m + v / hi * W - 1, y));
            i++;
        }
        s.Append("</svg>");
        return s.ToString();
    }

    static string H(string s) { return (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;"); }
    static string G(double v) { return v.ToString("G4", Inv); }
    static string F(string fmt, params object[] args) { return string.Format(Inv, fmt, args); }
}
