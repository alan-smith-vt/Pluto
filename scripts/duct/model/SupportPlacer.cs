using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

// Duct-to-steel supports in the clean duct model (2026-10-30; rules: vault handoff Pluto 2026-10-30 "Duct-to-steel
// supports"; split stage 4: out of the DuctsToPluto partial). Place runs DuctSupports.Find on the clean frames and
// plain steel records (no steel model types here), splits each clean frame at its supports, then the span check
// (UNSUPPORTED_SPAN), the floating pieces (FLOATING, stability rule 1) and the Navisworks hanger check. The steel
// side of each support (SteelIx, T: a split request) is applied by the caller, which fills SteelJoint / SteelMember
// before WriteSupportsCsv. Viewer output: SupportViewWriter.cs.
public class SteelPoint { public string Name = ""; public double X, Y, Z; }   // a steel joint, inches

public class SupportRun
{
    public DuctSupports Ds;
    public List<int> DuctJoint = new List<int>();             // per support: the clean joint (P key)
    public List<SteelPoint> SteelJoint = new List<SteelPoint>();   // per support, filled by the caller after the steel splits
    public List<string> SteelSource = new List<string>();      // per support, filled by the caller (source member id, supports.csv SteelMember)
    public List<double[]> Dim = new List<double[]>();          // per support: { W, H } across / up, inches
    public List<double[]> Across = new List<double[]>();       // per support: horizontal width direction
    public List<DuctSupports.Span> Spans = new List<DuctSupports.Span>();
    public HashSet<int> Rigid = new HashSet<int>();   // clean indices of rigid links (span frame counts leave them out)
    public List<List<int>> Floating = new List<List<int>>();   // pieces with no support (clean indices), longest first
    public HangerRun Hangers;
    public string Note = "";      // match.txt clean-model block
    public string NewLine = "";   // match.txt NEW block lines (supports, floating, hangers)
}

public static class SupportPlacer
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static SupportRun Place(List<CEdge> clean, Dictionary<int, double[]> P, List<DuctSupports.Steel> steel, DuctSettings set,
        List<DuctRow> rows, List<int> hangerScope)
    {
        var run = new SupportRun(); var ds = new DuctSupports(set.Supports); run.Ds = ds;
        var ducts = DuctRecords(clean, P);
        ds.Find(ducts, steel);

        // duct joints: split each clean frame at its supports (within 1 in of an end: that end)
        var ductJ = new int[ds.Supports.Count];
        int nextKey = P.Count > 0 ? P.Keys.Max() + 1 : 1;
        var bySeg = Enumerable.Range(0, ds.Supports.Count).GroupBy(i => ds.Supports[i].DuctIx).ToDictionary(g => g.Key, g => g.OrderBy(i => ds.Supports[i].S).ToList());
        var outEdges = new List<CEdge>();
        for (int k = 0; k < clean.Count; k++)
        {
            CEdge ce = clean[k]; List<int> sps;
            if (!bySeg.TryGetValue(k, out sps)) { outEdges.Add(ce); continue; }
            double L = DuctMath.Len(P[ce.A], P[ce.B]) * 12; int prev = ce.A;
            foreach (int i in sps)
            {
                double s = ds.Supports[i].S;
                if (s * L <= 1) { ductJ[i] = ce.A; continue; }
                if ((1 - s) * L <= 1) { ductJ[i] = ce.B; continue; }
                if (prev != ce.A && DuctMath.Len(P[prev], DuctMath.Lerp(P[ce.A], P[ce.B], s)) * 12 <= 1) { ductJ[i] = prev; continue; }
                int nk = nextKey++; P[nk] = DuctMath.Lerp(P[ce.A], P[ce.B], s);
                outEdges.Add(CleanModel.CopyEdge(ce, prev, nk)); prev = nk; ductJ[i] = nk;
            }
            outEdges.Add(prev == ce.A ? ce : CleanModel.CopyEdge(ce, prev, ce.B));
        }
        clean.Clear(); clean.AddRange(outEdges);

        for (int i = 0; i < ds.Supports.Count; i++)
        {
            DuctSupports.Support sp = ds.Supports[i]; DuctSupports.Duct d = ducts[sp.DuctIx];
            run.DuctJoint.Add(ductJ[i]);
            double[] u = d.WidthDir ?? new[] { -sp.Axis[1], sp.Axis[0], 0 }; double w = d.Round ? d.Dia : d.W, h = d.Round ? d.Dia : d.H;
            if (Math.Abs(u[2]) > 0.7) { u = DuctMath.Unit(new[] { -sp.Axis[1], sp.Axis[0], 0 }); double x = w; w = h; h = x; }   // width vertical: across = H
            run.Across.Add(u); run.Dim.Add(new[] { w, h });
        }

        // span check on the split frames
        var after = DuctRecords(clean, P);
        var sj = new HashSet<int>(ductJ);
        run.Spans = ds.Spans(after, sj);
        for (int k = 0; k < run.Spans.Count; k++) run.Spans[k].Id = "US" + (k + 1);   // longest first
        for (int k = 0; k < clean.Count; k++) if (clean[k].Rigid) run.Rigid.Add(k);
        foreach (DuctSupports.Span s in run.Spans)
            foreach (int i in s.Ducts)
            {
                CEdge ce = clean[i]; if (ce.Rigid) continue;   // every duct frame of the stretch is flagged; rigid links are not
                ce.Flags.Add("UNSUPPORTED_SPAN"); ce.Span = s.Id;
                foreach (RvEl e in ce.Src)
                {
                    if (!e.Flags.Contains("UNSUPPORTED_SPAN")) e.Flags.Add("UNSUPPORTED_SPAN");
                    string det = string.Format(Inv, "unsupported span {0}: {1:0.#} ft, {2} support(s) at its ends", s.Id, s.LengthIn / 12, s.Supports);
                    if (!e.Detail.Contains(det)) e.Detail.Add(det);
                }
            }
        run.Floating = FloatingPieces(clean, P, sj);   // stability rule 1: FLOATING, left out of SAP by SapDucts
        run.Hangers = HangerCheck.Check(clean, P, rows, hangerScope, run, set.HangerMatchIn);   // flags only (SUPPORT_NO_HANGER into supports.csv)
        string floatNew;
        run.Note = SupportsNote(run) + "\n" + FloatingNote(run, clean, P, out floatNew) + "\n" + run.Hangers.Note;
        run.NewLine = SupportsNewLine(run) + (floatNew != "" ? "\n" + floatNew : "") + (run.Hangers.NewLine != "" ? "\n" + run.Hangers.NewLine : "");
        return run;
    }

    static List<DuctSupports.Duct> DuctRecords(List<CEdge> clean, Dictionary<int, double[]> P)
    {
        var l = new List<DuctSupports.Duct>();
        foreach (CEdge ce in clean)
        {
            double[] a = P[ce.A], b = P[ce.B];
            var d = new DuctSupports.Duct { JointA = ce.A, JointB = ce.B, A = new[] { a[0] * 12, a[1] * 12, a[2] * 12 }, B = new[] { b[0] * 12, b[1] * 12, b[2] * 12 },
                Rigid = ce.Rigid, WidthDir = DuctGeom.WidthDir(ce, P), System = ce.Src.Count > 0 ? ce.Src[0].SysName : ce.Source };
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
    public static void WriteSupportsCsv(string path, SupportRun run, Dictionary<int, double[]> P)
    {
        var s = new StringBuilder();
        s.AppendLine("SupportId,Type,DuctJoint,DX,DY,DZ,SteelJoint,SX,SY,SZ,SteelMember,AxisX,AxisY,AxisZ,Gap_in,System,Section,Flags");
        for (int i = 0; i < run.Ds.Supports.Count; i++)
        {
            DuctSupports.Support sp = run.Ds.Supports[i]; double[] d = P[run.DuctJoint[i]]; SteelPoint j = run.SteelJoint[i];
            s.AppendLine(string.Join(",", new[] { sp.Id, sp.Type, "N" + run.DuctJoint[i], DuctGeom.R3(d[0] * 12), DuctGeom.R3(d[1] * 12), DuctGeom.R3(d[2] * 12),
                j.Name, DuctGeom.R3(j.X), DuctGeom.R3(j.Y), DuctGeom.R3(j.Z), run.SteelSource[i],
                sp.Axis[0].ToString("0.######", Inv), sp.Axis[1].ToString("0.######", Inv), sp.Axis[2].ToString("0.######", Inv),
                DuctGeom.R3(sp.Gap), sp.System, sp.Section, string.Join(" ", sp.Flags.ToArray()) }.Select(DuctGeom.CsvCell).ToArray()));
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

    // stability rule 1 (2026-10-30, user): a connected duct piece (clean frames joined at shared joints, rigid links
    // included) with no support joint at all is FLOATING on every frame and part. It has rigid-body modes in SAP
    // (the MODAL hang), so SapDucts leaves it out; frames.csv keeps it, flagged
    static List<List<int>> FloatingPieces(List<CEdge> clean, Dictionary<int, double[]> P, HashSet<int> supportJoints)
    {
        var parent = new int[clean.Count]; for (int i = 0; i < parent.Length; i++) parent[i] = i;
        Func<int, int> root = null; root = i => parent[i] == i ? i : (parent[i] = root(parent[i]));
        var byJoint = new Dictionary<int, int>();
        for (int i = 0; i < clean.Count; i++)
            foreach (int j in new[] { clean[i].A, clean[i].B })
            {
                int o; if (byJoint.TryGetValue(j, out o)) parent[root(i)] = root(o); else byJoint[j] = i;
            }
        var pieces = new Dictionary<int, List<int>>();
        for (int i = 0; i < clean.Count; i++) { List<int> l; int r = root(i); if (!pieces.TryGetValue(r, out l)) { l = new List<int>(); pieces[r] = l; } l.Add(i); }
        var res = pieces.Values.Where(l => !l.Any(i => supportJoints.Contains(clean[i].A) || supportJoints.Contains(clean[i].B)))
            .OrderByDescending(l => l.Sum(i => clean[i].Rigid ? 0 : DuctMath.Len(P[clean[i].A], P[clean[i].B]))).ToList();
        foreach (List<int> l in res)
        {
            double ft = l.Sum(i => clean[i].Rigid ? 0 : DuctMath.Len(P[clean[i].A], P[clean[i].B]));
            foreach (int i in l)
            {
                CEdge ce = clean[i]; ce.Flags.Add("FLOATING");
                foreach (RvEl e in ce.Src)
                {
                    if (!e.Flags.Contains("FLOATING")) e.Flags.Add("FLOATING");
                    string det = string.Format(Inv, "floating piece: {0:0.#} ft, {1} frame(s), no support; left out of SAP", ft, l.Count);
                    if (!e.Detail.Contains(det)) e.Detail.Add(det);
                }
            }
        }
        return res;
    }

    public static string PieceText(List<int> l, List<CEdge> clean, Dictionary<int, double[]> P)
    {
        CEdge first = l.Select(i => clean[i]).FirstOrDefault(c => c.Src.Count > 0) ?? clean[l[0]];
        string sys = first.Src.Count > 0 ? first.Src[0].SysName : first.Source;
        return string.Format(Inv, "{0} {1:0.#} ft ({2})", sys, l.Sum(i => clean[i].Rigid ? 0 : DuctMath.Len(P[clean[i].A], P[clean[i].B])),
            string.Join(" ", l.Select(i => "F" + (i + 1)).ToArray()));   // frames.csv names
    }

    static string FloatingNote(SupportRun run, List<CEdge> clean, Dictionary<int, double[]> P, out string newLine)
    {
        int n = run.Floating.Count, f = run.Floating.Sum(l => l.Count);
        newLine = n == 0 ? "" : string.Format(Inv, "NEW floating: {0} piece{1} ({2} frame{3}) with no support, left out of SAP (FLOATING): {4}",
            n, n == 1 ? "" : "s", f, f == 1 ? "" : "s", string.Join(", ", run.Floating.Select(l => PieceText(l, clean, P)).ToArray()));
        return string.Format(Inv, "floating pieces (FLOATING, no support, left out of SAP): {0}{1}", n,
            n > 0 ? "\n" + string.Join("\n", run.Floating.Select(l => "  " + PieceText(l, clean, P)).ToArray()) : "");
    }

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
}
