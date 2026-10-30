using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

// Duct-to-steel supports in the clean duct model (2026-10-30; rules: vault handoff Pluto 2026-10-30 "Duct-to-steel
// supports"). BuildClean runs it after the SteelModel is built: DuctSupports.Find on the clean frames and the steel
// frames, then per support a duct joint (the clean frame is split there), a steel joint (SteelModel.SplitAt, flag
// SUPPORT) and a TAP_STUB link between them (SAP side: <out>.supports.csv). Then the span check: clean frames of a
// horizontal stretch longer than SpanFt between supports get UNSUPPORTED_SPAN (frames.csv Flags, the parts'
// flags.csv rows, SAP group FLAG_UNSUPPORTED_SPAN).
// Viewer (<out>_clean.bin): the stub as a thin beam ("Support: TAP_STUB"), the support as a glyph on the stub (viewer look group,
// one per type: guide = ring-arrows, anchor = anchor) ("Support: guide"), "Flag: UNSUPPORTED_SPAN" on the flagged frames. Hover label = support id, type, gap.
// Settings from duct-config.psd1 (Export-DuctsViewer.ps1): DuctSupportGapIn, DuctSpanFt.
public partial class DuctsToPluto
{
    public static DuctSupports.Settings SupportSettings = new DuctSupports.Settings();

    class SupportRun
    {
        public DuctSupports Ds;
        public List<int> DuctJoint = new List<int>();             // per support: the clean joint (P key)
        public List<SteelModel.Joint> SteelJoint = new List<SteelModel.Joint>();
        public List<string> SteelMember = new List<string>();
        public List<double[]> Dim = new List<double[]>();          // per support: { W, H } across / up, inches
        public List<double[]> Across = new List<double[]>();       // per support: horizontal width direction
        public List<DuctSupports.Span> Spans = new List<DuctSupports.Span>();
        public string Note = "";
        public HangerRun Hangers;   // DuctsCleanHangers.cs
        public HashSet<int> Rigid = new HashSet<int>();   // clean indices of rigid links (span frame counts leave them out)
    }

    static SupportRun ApplySupports(List<CEdge> clean, Dictionary<int, double[]> P, SteelModel sm, string outBase, List<Row> rows)
    {
        var run = new SupportRun(); var ds = new DuctSupports(SupportSettings); run.Ds = ds;
        var ducts = DuctRecords(clean, P);
        var steel = new List<DuctSupports.Steel>(); var frames = new List<SteelModel.Frame>();
        if (sm != null)
            foreach (SteelModel.Frame f in sm.Frames)
            {
                SteelMember b = sm.Members[f.Source];
                var st = new DuctSupports.Steel { I = sm.JointPos(f.I), J = sm.JointPos(f.J), TopI = sm.TopAt(f, 0)[2], TopJ = sm.TopAt(f, 1)[2],
                    Name = f.Name, Section = string.IsNullOrEmpty(b.SectionName) ? "UNNAMED" : b.SectionName };
                steel.Add(st); frames.Add(f);
            }
        ds.Find(ducts, steel);

        // steel joints: per frame, highest t first (SplitAt keeps the I part in f, so lower t rescale by the cut)
        var steelJ = new SteelModel.Joint[ds.Supports.Count];
        foreach (IGrouping<int, int> g in Enumerable.Range(0, ds.Supports.Count).GroupBy(i => ds.Supports[i].SteelIx))
        {
            SteelModel.Frame f = frames[g.Key]; double end = 1;
            foreach (int i in g.OrderByDescending(i => ds.Supports[i].T))
            {
                double t = ds.Supports[i].T;
                if (end <= 1e-9) { steelJ[i] = sm.Joints[f.I - 1]; continue; }
                int i0 = f.I, j0 = f.J;
                SteelModel.Joint j = sm.SplitAt(f, t / end, "SUPPORT");
                steelJ[i] = j;
                if (j.Id != i0 && j.Id != j0) end = t;   // a new joint: f is now the part up to it
            }
        }
        // duct joints: split each clean frame at its supports (within 1 in of an end: that end)
        var ductJ = new int[ds.Supports.Count];
        int nextKey = P.Count > 0 ? P.Keys.Max() + 1 : 1;
        var bySeg = Enumerable.Range(0, ds.Supports.Count).GroupBy(i => ds.Supports[i].DuctIx).ToDictionary(g => g.Key, g => g.OrderBy(i => ds.Supports[i].S).ToList());
        var outEdges = new List<CEdge>();
        for (int k = 0; k < clean.Count; k++)
        {
            CEdge ce = clean[k]; List<int> sps;
            if (!bySeg.TryGetValue(k, out sps)) { outEdges.Add(ce); continue; }
            double L = Len(P[ce.A], P[ce.B]) * 12; int prev = ce.A;
            foreach (int i in sps)
            {
                double s = ds.Supports[i].S;
                if (s * L <= 1) { ductJ[i] = ce.A; continue; }
                if ((1 - s) * L <= 1) { ductJ[i] = ce.B; continue; }
                if (prev != ce.A && Len(P[prev], Lerp(P[ce.A], P[ce.B], s)) * 12 <= 1) { ductJ[i] = prev; continue; }
                int nk = nextKey++; P[nk] = Lerp(P[ce.A], P[ce.B], s);
                outEdges.Add(CopyEdge(ce, prev, nk)); prev = nk; ductJ[i] = nk;
            }
            outEdges.Add(prev == ce.A ? ce : CopyEdge(ce, prev, ce.B));
        }
        clean.Clear(); clean.AddRange(outEdges);

        for (int i = 0; i < ds.Supports.Count; i++)
        {
            DuctSupports.Support sp = ds.Supports[i]; DuctSupports.Duct d = ducts[sp.DuctIx];
            run.DuctJoint.Add(ductJ[i]); run.SteelJoint.Add(steelJ[i]);
            SteelMember b = sm.Members[frames[sp.SteelIx].Source];
            run.SteelMember.Add(b.MemberOid ?? "");
            double[] u = d.WidthDir ?? new[] { -sp.Axis[1], sp.Axis[0], 0 }; double w = d.Round ? d.Dia : d.W, h = d.Round ? d.Dia : d.H;
            if (Math.Abs(u[2]) > 0.7) { u = Unit(new[] { -sp.Axis[1], sp.Axis[0], 0 }); double x = w; w = h; h = x; }   // width vertical: across = H
            run.Across.Add(u); run.Dim.Add(new[] { w, h });
        }

        // span check on the split frames
        var after = DuctRecords(clean, P);
        var sj = new HashSet<int>(ductJ);
        run.Spans = ds.Spans(after, sj);
        for (int k = 0; k < run.Spans.Count; k++) run.Spans[k].Id = "US" + (k + 1);   // longest first
        SpanTotal = run.Spans.Count;
        for (int k = 0; k < clean.Count; k++) if (clean[k].Rigid) run.Rigid.Add(k);
        foreach (DuctSupports.Span s in run.Spans)
            foreach (int i in s.Ducts)
            {
                CEdge ce = clean[i]; if (ce.Rigid) continue;   // every duct frame of the stretch is flagged; rigid links are not
                ce.Flags.Add("UNSUPPORTED_SPAN");
                foreach (RvEl e in ce.Src)
                {
                    if (!e.Flags.Contains("UNSUPPORTED_SPAN")) e.Flags.Add("UNSUPPORTED_SPAN");
                    string det = string.Format(Inv, "unsupported span {0}: {1:0.#} ft, {2} support(s) at its ends", s.Id, s.LengthIn / 12, s.Supports);
                    if (!e.Detail.Contains(det)) e.Detail.Add(det);
                }
            }
        run.Hangers = CheckHangers(clean, P, rows, run);   // flags only (SUPPORT_NO_HANGER into supports.csv)
        WriteSupportsCsv(outBase + ".supports.csv", run, P);
        run.Note = SupportsNote(run) + "\n" + run.Hangers.Note;
        return run;
    }

    static List<DuctSupports.Duct> DuctRecords(List<CEdge> clean, Dictionary<int, double[]> P)
    {
        var l = new List<DuctSupports.Duct>();
        foreach (CEdge ce in clean)
        {
            double[] a = P[ce.A], b = P[ce.B];
            var d = new DuctSupports.Duct { JointA = ce.A, JointB = ce.B, A = new[] { a[0] * 12, a[1] * 12, a[2] * 12 }, B = new[] { b[0] * 12, b[1] * 12, b[2] * 12 },
                Rigid = ce.Rigid, WidthDir = WidthDir(ce, P), System = ce.Src.Count > 0 ? ce.Src[0].SysName : ce.Source };
            if (!ce.Rigid && ce.Dim != null)
            {
                double[] dm = ce.Dim;
                if (ce.DimB != null && ce.DimB[0] == dm[0] && (dm[0] == 1 ? ce.DimB[3] > dm[3] : ce.DimB[1] * ce.DimB[2] > dm[1] * dm[2])) dm = ce.DimB;   // larger end
                d.Round = dm[0] == 1; d.W = dm[1]; d.H = dm[2]; d.Dia = dm[3];
                if (d.Round && double.IsNaN(d.Dia)) d.Dia = 0;
                if (!d.Round && (double.IsNaN(d.W) || double.IsNaN(d.H))) { d.W = 0; d.H = 0; }
            }
            l.Add(d);
        }
        return l;
    }

    // SAP input: one row per support (inches, the frames.csv frame). SteelJoint = a steel_joints.csv joint (the steel
    // frame is split there); DuctJoint = a frames.csv joint. Axis = duct direction (link local 1).
    static void WriteSupportsCsv(string path, SupportRun run, Dictionary<int, double[]> P)
    {
        var s = new StringBuilder();
        s.AppendLine("SupportId,Type,DuctJoint,DX,DY,DZ,SteelJoint,SX,SY,SZ,SteelMember,AxisX,AxisY,AxisZ,Gap_in,System,Section,Flags");
        for (int i = 0; i < run.Ds.Supports.Count; i++)
        {
            DuctSupports.Support sp = run.Ds.Supports[i]; double[] d = P[run.DuctJoint[i]]; SteelModel.Joint j = run.SteelJoint[i];
            s.AppendLine(string.Join(",", new[] { sp.Id, sp.Type, "N" + run.DuctJoint[i], R3(d[0] * 12), R3(d[1] * 12), R3(d[2] * 12),
                j.Name, R3(j.X), R3(j.Y), R3(j.Z), run.SteelMember[i],
                sp.Axis[0].ToString("0.######", Inv), sp.Axis[1].ToString("0.######", Inv), sp.Axis[2].ToString("0.######", Inv),
                R3(sp.Gap), sp.System, sp.Section, string.Join(" ", sp.Flags.ToArray()) }.Select(CsvCell).ToArray()));
        }
        File.WriteAllText(path, s.ToString(), new UTF8Encoding(false));
    }

    static string SupportsNote(SupportRun run)
    {
        DuctSupports ds = run.Ds; DuctSupports.Settings st = ds.Set;
        var types = ds.Supports.GroupBy(x => x.Type).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => g.Key + " " + g.Count()).ToArray();
        string worst = string.Join(", ", run.Spans.Take(5).Select(x => string.Format(Inv, "{0} {1} {2:0.#} ft", x.Id, x.System, x.LengthIn / 12)).ToArray());
        return string.Format(Inv, "supports (<out>.supports.csv): {0} ({1}), gap <= {2:0.##} in, steel overlapping the duct bottom {3}, steel through the duct {4} (no support), steel along the duct under it {5} (not modelled)\n",
                ds.Supports.Count, types.Length > 0 ? string.Join(", ", types) : "-", st.GapMaxIn, ds.Supports.Count(x => x.Flags.Contains("STEEL_OVERLAP")), ds.Clashes, ds.Parallel)
            + string.Format(Inv, "unsupported spans > {0:0.#} ft (UNSUPPORTED_SPAN): {1}{2}\n", st.SpanFt, SpanCount(run), worst != "" ? "; longest: " + worst : "")
            + string.Join("", run.Spans.Select(x => string.Format(Inv, "  {0} {1} {2:0.#} ft, {3} support(s) at its ends, {4} frame(s): {5}\n", x.Id, x.System, x.LengthIn / 12, x.Supports,
                SpanFrames(run, x).Count, string.Join(" ", SpanFrames(run, x).Select(i => "F" + (i + 1)).ToArray()))).ToArray())   // frames.csv names (F = clean index + 1)
            + ds.Histogram().TrimEnd();
    }

    // match.txt NEW block line, set by BuildClean ("" = no steel)
    static string SupportNewLine = "";
    static int SpanTotal;   // span count for the match.txt parts summary, set by ApplySupports

    static string SupportsNewLine(SupportRun run)
    {
        return string.Format(Inv, "NEW supports: {0} ({1}), steel through a duct {2}; unsupported spans > {3:0.#} ft: {4}{5}",
            run.Ds.Supports.Count, string.Join(", ", run.Ds.Supports.GroupBy(x => x.Type).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => g.Key + " " + g.Count()).ToArray()),
            run.Ds.Clashes, run.Ds.Set.SpanFt, SpanCount(run),
            run.Spans.Count > 0 ? ": " + string.Join(", ", run.Spans.Take(8).Select(x => string.Format(Inv, "{0} {1} {2:0.#} ft", x.Id, x.System, x.LengthIn / 12)).ToArray()) + (run.Spans.Count > 8 ? " ..." : "") : "");
    }

    // the headline count is spans; frames only with their unit (user decision): "9 spans (25 frames)"
    static string SpanCount(SupportRun run)
    {
        int n = run.Spans.Count, f = run.Spans.Sum(x => SpanFrames(run, x).Count);
        return string.Format(Inv, "{0} span{1} ({2} frame{3})", n, n == 1 ? "" : "s", f, f == 1 ? "" : "s");
    }

    // the duct frames of a span (clean indices), rigid links left out as in the flag
    static List<int> SpanFrames(SupportRun run, DuctSupports.Span s) { return s.Ducts.Where(i => !run.Rigid.Contains(i)).ToList(); }

    class SupportView
    {
        public List<uint> Stubs = new List<uint>();
        public SortedDictionary<string, List<FeaturesSidecar.Anchor>> ByType = new SortedDictionary<string, List<FeaturesSidecar.Anchor>>(StringComparer.Ordinal);
        public SortedDictionary<string, List<uint>> StubsByType = new SortedDictionary<string, List<uint>>(StringComparer.Ordinal);
    }

    // stubs as beams (duct joint -> steel joint); the support glyph sits on the stub's duct end (t = 0), turned to the duct
    static SupportView AddSupports(SupportRun run, Dictionary<int, double[]> P, Dictionary<int, Node> nodes, ref int next, List<RawViewerWriter.SectionDef> sections,
        Dictionary<int, RawViewerWriter.BeamMember> beams, Dictionary<int, string> labels, ref int nb, double[] off, double scale)
    {
        var v = new SupportView();
        if (run == null || run.Ds.Supports.Count == 0) return v;
        sections.Add(RawViewerWriter.SectionDef.Pipe("TAP_STUB", (float)(1.0 * scale / 12), (float)(0.25 * scale / 12)));
        int sec = sections.Count - 1;
        for (int i = 0; i < run.Ds.Supports.Count; i++)
        {
            DuctSupports.Support sp = run.Ds.Supports[i]; SteelModel.Joint j = run.SteelJoint[i];
            double[] pd = P[run.DuctJoint[i]], ps = { j.X / 12, j.Y / 12, j.Z / 12 };
            string label = string.Format(Inv, "support {0} | {1} | gap {2:0.##} in | duct {3:0.#}x{4:0.#} {5} at N{6} | steel {7} at {8}{9}",
                sp.Id, sp.Type, sp.Gap, run.Dim[i][0], run.Dim[i][1], sp.System, run.DuctJoint[i], sp.Section, j.Name,
                sp.Flags.Count > 0 ? " | FLAGS " + string.Join(" ", sp.Flags.ToArray()) : "");
            int a = MakeNode(nodes, ref next, pd, off, scale), b = MakeNode(nodes, ref next, ps, off, scale);
            var m = Beam(nb, a, b, sec, nodes); beams[nb] = m; labels[nb] = label; v.Stubs.Add((uint)nb);
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

    static void AddSupportGroups(FeaturesSidecar sc, SupportView v)
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
    static void AddSpanGroup(FeaturesSidecar sc, SupportRun run, List<CEdge> clean, Dictionary<int, double[]> P, Dictionary<CEdge, int> beamOf, List<uint> flagged)
    {
        if (flagged.Count == 0) return;
        var anchors = new List<FeaturesSidecar.Anchor>();
        if (run != null)
            foreach (DuctSupports.Span s in run.Spans)
            {
                double[] c = { 0, 0, 0 }; double w = 0;
                foreach (int i in s.Ducts)
                {
                    CEdge ce = clean[i]; if (ce.Rigid) continue;
                    double L = Len(P[ce.A], P[ce.B]); double[] m = Lerp(P[ce.A], P[ce.B], 0.5);
                    for (int k = 0; k < 3; k++) c[k] += m[k] * L; w += L;
                }
                if (w <= 0) continue;
                for (int k = 0; k < 3; k++) c[k] /= w;
                CEdge best = null; double bt = 0.5, bd = double.MaxValue;
                foreach (int i in s.Ducts)
                {
                    CEdge ce = clean[i]; int id; if (ce.Rigid || !beamOf.TryGetValue(ce, out id)) continue;
                    double t, lat; Project(P[ce.A], P[ce.B], c, out t, out lat); t = Math.Max(0, Math.Min(1, t));
                    double d = Math.Sqrt(Dist2(Lerp(P[ce.A], P[ce.B], t), c));
                    if (d < bd) { bd = d; best = ce; bt = t; }
                }
                if (best != null)
                    anchors.Add(new FeaturesSidecar.Anchor { Domain = "beams", Id = (uint)beamOf[best], T = Math.Round(bt, 4),
                        Label = string.Format(Inv, "{0} {1} {2:0.#} ft, {3} support(s)", s.Id, s.System, s.LengthIn / 12, s.Supports) });
            }
        FeaturesSidecar.Group g = sc.AddLookGroup("Flag: UNSUPPORTED_SPAN", null,
            new FeaturesSidecar.Look { Glyph = "warn", Tier = "exception", Label = string.Format(Inv, "Unsupported spans > {0:0.#} ft", SupportSettings.SpanFt), Section = "Checks" },
            anchors, new[] { "duct", "clean", "flag" }, "beams", flagged);
        g.StaadName = "FLAG_UNSUPPORTED_SPAN";
    }
}
