using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

// Viewer output of the supports (<out>_clean.bin; split stage 4: out of the DuctsToPluto partial): the stub as a thin
// beam ("Support: TAP_STUB"), a glyph per support type on the stub's duct end ("Support: guide" = ring-arrows,
// anchor = anchor), and the checks as warn glyphs that never repaint: "Flag: UNSUPPORTED_SPAN", "Flag: FLOATING",
// "Flag: HANGER_NO_STEEL_SUPPORT", "Flag: SUPPORT_NO_HANGER". Hover label = support id, type, gap.
public class SupportView
{
    public List<uint> Stubs = new List<uint>();
    public SortedDictionary<string, List<FeaturesSidecar.Anchor>> ByType = new SortedDictionary<string, List<FeaturesSidecar.Anchor>>(StringComparer.Ordinal);
    public SortedDictionary<string, List<uint>> StubsByType = new SortedDictionary<string, List<uint>>(StringComparer.Ordinal);
}

public static class SupportViewWriter
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // stubs as beams (duct joint -> steel joint); the support glyph sits on the stub's duct end (t = 0), turned to the duct
    public static SupportView AddSupports(SupportRun run, Dictionary<int, double[]> P, Dictionary<int, Node> nodes, ref int next, List<RawViewerWriter.SectionDef> sections,
        Dictionary<int, RawViewerWriter.BeamMember> beams, Dictionary<int, string> labels, ref int nb, double[] off, double scale)
    {
        var v = new SupportView();
        if (run == null || run.Ds.Supports.Count == 0) return v;
        sections.Add(RawViewerWriter.SectionDef.Pipe("TAP_STUB", (float)(1.0 * scale / 12), (float)(0.25 * scale / 12)));
        int sec = sections.Count - 1;
        for (int i = 0; i < run.Ds.Supports.Count; i++)
        {
            DuctSupports.Support sp = run.Ds.Supports[i]; SteelPoint j = run.SteelJoint[i];
            double[] pd = P[run.DuctJoint[i]], ps = { j.X / 12, j.Y / 12, j.Z / 12 };
            string label = string.Format(Inv, "support {0} | {1} | gap {2:0.##} in | duct {3:0.#}x{4:0.#} {5} at N{6} | steel {7} at {8}{9}",
                sp.Id, sp.Type, sp.Gap, run.Dim[i][0], run.Dim[i][1], sp.System, run.DuctJoint[i], sp.Section, j.Name,
                sp.Flags.Count > 0 ? " | FLAGS " + string.Join(" ", sp.Flags.ToArray()) : "");
            int a = DuctGeom.MakeNode(nodes, ref next, pd, off, scale), b = DuctGeom.MakeNode(nodes, ref next, ps, off, scale);
            var m = DuctGeom.Beam(nb, a, b, sec, nodes); beams[nb] = m; labels[nb] = label; v.Stubs.Add((uint)nb);
            List<FeaturesSidecar.Anchor> l; if (!v.ByType.TryGetValue(sp.Type, out l)) { l = new List<FeaturesSidecar.Anchor>(); v.ByType[sp.Type] = l; v.StubsByType[sp.Type] = new List<uint>(); }
            l.Add(new FeaturesSidecar.Anchor { Domain = "beams", Id = (uint)nb, T = 0, Dir = sp.Axis,
                Label = string.Format(Inv, "{0} gap {1:0.##} in", sp.Id, sp.Gap) });
            v.StubsByType[sp.Type].Add((uint)nb);
            nb++;
        }
        return v;
    }

    // glyph per support type (shape = meaning; registered viewer glyphs), unknown types a plain square
    static string SupportGlyph(string type) { return type == "guide" ? "ring-arrows" : type == "anchor" ? "anchor" : "square"; }

    public static void AddSupportGroups(FeaturesSidecar sc, SupportView v)
    {
        if (v == null) return;
        if (v.Stubs.Count > 0) sc.AddGroup("Support: TAP_STUB", "#5a5f68", "beams", v.Stubs, new[] { "duct", "support", "link" }, "TAP_STUBS");
        foreach (KeyValuePair<string, List<FeaturesSidecar.Anchor>> kv in v.ByType)
        {
            string name = kv.Key.Length > 0 ? char.ToUpperInvariant(kv.Key[0]) + kv.Key.Substring(1) : kv.Key;
            FeaturesSidecar.Group g = sc.AddLookGroup("Support: " + kv.Key, null,
                new FeaturesSidecar.Look { Glyph = SupportGlyph(kv.Key), Tier = "detail", Label = name, Section = "Supports" },
                kv.Value, new[] { "duct", "support" }, "beams", v.StubsByType[kv.Key]);
            g.StaadName = "SUPPORT_" + kv.Key.ToUpperInvariant();
        }
    }

    // unsupported spans: one warn glyph per span at its length-weighted centre (the frame nearest to it, t there);
    // members = the flagged frames (selection, SAP group); never repaints them
    public static void AddSpanGroup(FeaturesSidecar sc, SupportRun run, List<CEdge> clean, Dictionary<int, double[]> P, Dictionary<CEdge, int> beamOf, List<uint> flagged)
    {
        if (flagged.Count == 0 || run == null) return;
        var anchors = new List<FeaturesSidecar.Anchor>();
        foreach (DuctSupports.Span s in run.Spans)
        {
            double[] c = { 0, 0, 0 }; double w = 0;
            foreach (int i in s.Ducts)
            {
                CEdge ce = clean[i]; if (ce.Rigid) continue;
                double L = DuctMath.Len(P[ce.A], P[ce.B]); double[] m = DuctMath.Lerp(P[ce.A], P[ce.B], 0.5);
                for (int k = 0; k < 3; k++) c[k] += m[k] * L; w += L;
            }
            if (w <= 0) continue;
            for (int k = 0; k < 3; k++) c[k] /= w;
            CEdge best = null; double bt = 0.5, bd = double.MaxValue;
            foreach (int i in s.Ducts)
            {
                CEdge ce = clean[i]; int id; if (ce.Rigid || !beamOf.TryGetValue(ce, out id)) continue;
                double t, lat; DuctMath.Project(P[ce.A], P[ce.B], c, out t, out lat); t = Math.Max(0, Math.Min(1, t));
                double d = Math.Sqrt(DuctMath.Dist2(DuctMath.Lerp(P[ce.A], P[ce.B], t), c));
                if (d < bd) { bd = d; best = ce; bt = t; }
            }
            if (best != null)
                anchors.Add(new FeaturesSidecar.Anchor { Domain = "beams", Id = (uint)beamOf[best], T = Math.Round(bt, 4),
                    Label = string.Format(Inv, "{0} {1} {2:0.#} ft, {3} support(s)", s.Id, s.System, s.LengthIn / 12, s.Supports) });
        }
        FeaturesSidecar.Group g = sc.AddLookGroup("Flag: UNSUPPORTED_SPAN", null,
            new FeaturesSidecar.Look { Glyph = "warn", Tier = "exception", Label = string.Format(Inv, "Unsupported spans > {0:0.#} ft", run.Ds.Set.SpanFt), Section = "Checks" },
            anchors, new[] { "duct", "clean", "flag" }, "beams", flagged);
        g.StaadName = "FLAG_UNSUPPORTED_SPAN";
    }

    // a warn glyph per floating piece, on its longest frame; members = the piece's frames
    public static void AddFloatingGroup(FeaturesSidecar sc, SupportRun run, List<CEdge> clean, Dictionary<int, double[]> P, Dictionary<CEdge, int> beamOf)
    {
        if (run == null || run.Floating.Count == 0) return;
        var anchors = new List<FeaturesSidecar.Anchor>(); var members = new List<uint>();
        foreach (List<int> l in run.Floating)
        {
            int best = -1; double bl = -1;
            foreach (int i in l)
            {
                int id; if (!beamOf.TryGetValue(clean[i], out id)) continue;
                members.Add((uint)id);
                double L = clean[i].Rigid ? 0 : DuctMath.Len(P[clean[i].A], P[clean[i].B]);
                if (L > bl) { bl = L; best = id; }
            }
            if (best >= 0) anchors.Add(new FeaturesSidecar.Anchor { Domain = "beams", Id = (uint)best, T = 0.5, Label = SupportPlacer.PieceText(l, clean, P) });
        }
        if (members.Count == 0) return;
        sc.AddLookGroup("Flag: FLOATING", null, new FeaturesSidecar.Look { Glyph = "warn", Tier = "exception", Label = "Floating duct (no support, not in SAP)", Section = "Checks" },
            anchors, new[] { "duct", "clean", "flag" }, "beams", members).StaadName = "FLAG_FLOATING";
    }

    // warn glyphs: a hanger without a support on its duct frame (t at the hanger), a support without a hanger on its stub
    public static void AddHangerGroups(FeaturesSidecar sc, SupportRun run, List<CEdge> clean, Dictionary<CEdge, int> beamOf, List<DuctRow> rows, SupportView pv)
    {
        HangerRun hr = run != null ? run.Hangers : null;
        if (hr == null) return;
        var a1 = new List<FeaturesSidecar.Anchor>(); var m1 = new List<uint>();
        foreach (HangerHit h in hr.Hits)
        {
            int id; if (h.Edge < 0 || h.Support >= 0 || !beamOf.TryGetValue(clean[h.Edge], out id)) continue;
            a1.Add(new FeaturesSidecar.Anchor { Domain = "beams", Id = (uint)id, T = Math.Round(h.T, 4), Label = "hanger #" + rows[h.Row].NavisId });
            if (!m1.Contains((uint)id)) m1.Add((uint)id);
        }
        if (a1.Count > 0)
            sc.AddLookGroup("Flag: HANGER_NO_STEEL_SUPPORT", null, new FeaturesSidecar.Look { Glyph = "warn", Tier = "exception", Label = "Hanger without a steel support", Section = "Checks" },
                a1, new[] { "duct", "hanger", "flag" }, "beams", m1).StaadName = "FLAG_HANGER_NO_STEEL_SUPPORT";
        if (pv == null) return;
        var a2 = new List<FeaturesSidecar.Anchor>(); var m2 = new List<uint>();
        for (int k = 0; k < run.Ds.Supports.Count && k < pv.Stubs.Count; k++)
        {
            DuctSupports.Support sp = run.Ds.Supports[k];
            if (!sp.Flags.Contains("SUPPORT_NO_HANGER")) continue;
            a2.Add(new FeaturesSidecar.Anchor { Domain = "beams", Id = pv.Stubs[k], T = 0, Label = sp.Id });
            m2.Add(pv.Stubs[k]);
        }
        if (a2.Count > 0)
            sc.AddLookGroup("Flag: SUPPORT_NO_HANGER", null, new FeaturesSidecar.Look { Glyph = "warn", Tier = "exception", Label = "Support without a hanger", Section = "Checks" },
                a2, new[] { "duct", "support", "flag" }, "beams", m2).StaadName = "FLAG_SUPPORT_NO_HANGER";
    }
}
