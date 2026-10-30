using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

// Clean centreline model (2026-10-12; approach agreed with the user, vault handoff Pluto 2026-10-12 "Next:
// geometry simplification"). From the part-level pieces (each element's Segs, sized / oriented as drawn):
//   1. joints merged: near-link pairs (gap / overlap facing) and any joints within MergeIn
//   2. side links (a branch end butting into a duct's side) = rigid link from the branch end (the duct's face) to
//      the main's centreline, the main split there; tap stubs / eccentric offsets stay rigid links
//   3. overlapping collinear pieces (axes within 2 deg, 1 in) merged into one span (overlap length reported);
//      different sizes: both kept, flagged OVERLAP_SIZE_CONFLICT
//   4. straight chains collapsed: a joint where exactly two collinear same-size duct edges meet is removed
//   5. transitions keep their two end sizes (drawn at the larger; SAP: non-prismatic, later)
// Attachments (flanges / supports) have no pieces and drop out. Each clean edge lists its source Revit ids.
// Writes <out>_clean.bin / .features.json, <out>.frames.csv (the SAP input; the part-level one is
// <out>.parts_frames.csv) and returns the checks for match.txt.
public partial class DuctsToPluto
{

    static string BuildClean(string outBase, string modelId, string lengthUnit, List<RvEl> scope, Dictionary<int, RawViewerWriter.BeamMember> beams,
        Dictionary<int, double[]> beamDim, Dictionary<int, Node> nodes, double[] off, double scale, List<RvConn[]> nearPairs, List<int[]> sideNodes,
        Dictionary<int, int> navBeamRow, List<DuctRow> rows, Func<RvConn, int> nodeOf, Dictionary<RvConn, RvConn> attach)
    {
        Func<int, double[]> pos0 = id => DuctGeom.NodeFt(nodes[id], off, scale);
        var cm = CleanModel.FromPieces(scope, beams, beamDim, pos0, navBeamRow, rows, sideNodes, nodeOf);
        cm.MergeJoints(pos0, nearPairs, attach, nodeOf);
        cm.CloseGaps();
        cm.SplitAtLinks();
        cm.MergeOverlaps();
        cm.CollapseChains();
        var P = cm.P;
        var clean = cm.Clean();
        string cutNote = CleanModel.ApplyCuts(clean, P, Cfg);
        string dropNote = CleanModel.ApplyManualDrop(clean, P, Cfg);   // add-on, the viewer's Cut picker
        if (dropNote != "") cutNote += (cutNote != "" ? "\n" : "") + dropNote;
        string orientNote = ApplyOrientation(clean, P);   // DuctsCleanOrient.cs: before the supports (rotated depth)
        SteelModel sm = Cfg.Steel.Count > 0 ? SteelModel.Build(Cfg.Steel, Cfg.SteelSettings) : null;   // SteelViewWriter.cs draws it; split by supports from here on
        SpanTotal = 0;
        SupportRun sr = sm != null ? ApplySupports(clean, P, sm, outBase, rows) : null;   // DuctsCleanSupports.cs: splits clean frames + steel
        SupportNewLine = sr != null ? SupportsNewLine(sr) + (sr.FloatNewLine != "" ? "\n" + sr.FloatNewLine : "") + (sr.Hangers.NewLine != "" ? "\n" + sr.Hangers.NewLine : "") : "";

        // checks: length, deviation of the original connectors from the clean centreline
        double cleanLen = clean.Where(x => !x.Rigid && x.Source != "navisworks-only").Sum(x => DuctMath.Len(P[x.A], P[x.B]));
        var dev = new List<KeyValuePair<double, string>>();
        foreach (RvEl e in scope)
            foreach (RvConn k in e.Conns)
            {
                if (attach.ContainsKey(k) || DuctGeom.IsTapPoint(k)) continue;
                double best = double.MaxValue;
                foreach (CEdge ce in clean) { double t, lat; DuctMath.Project(P[ce.A], P[ce.B], k.P, out t, out lat); double[] q = DuctMath.Lerp(P[ce.A], P[ce.B], Math.Max(0, Math.Min(1, t))); best = Math.Min(best, Math.Sqrt(DuctMath.Dist2(q, k.P))); }
                if (best * 12 > 1) dev.Add(new KeyValuePair<double, string>(best * 12, e.Id + ":" + k.Key.Substring(k.Key.IndexOf(':') + 1)));
            }
        dev.Sort((x, y) => y.Key.CompareTo(x.Key));

        if (sm != null) sm.WriteCsv(outBase);
        WriteCleanViewer(outBase + "_clean", modelId + "/clean", lengthUnit, clean, P, off, scale, sm, sr);
        WriteCleanFrames(outBase + ".frames.csv", clean, P);

        return string.Format(Inv, "clean model (<out>_clean.bin, <out>.frames.csv): {0} elements ({1} rigid links) from {2} pieces; duct length {3:0.#} ft -> {4:0.#} ft, overlaps merged {5} ({6:0.#} ft), different-size overlaps {7} (OVERLAP_SIZE_CONFLICT), gaps closed {10}; connectors > 1 in from the clean line {8}{9}",
            clean.Count, clean.Count(x => x.Rigid), cm.Edges.Count(x => x.Src.Count > 0 || x.Source != "revit"), cm.RawLen, cleanLen, cm.OverlapMerged, cm.OverlapFt, cm.OverlapConflict, dev.Count,
            dev.Count > 0 ? " (worst: " + string.Join(", ", dev.Take(5).Select(x => x.Value + " " + x.Key.ToString("0.#", Inv) + " in").ToArray()) + ")" : "", cm.GapsClosed)
            + (cutNote != "" ? "\n" + cutNote : "")
            + (sm != null ? "\n" + sm.Summary() + " (<out>.steel_joints.csv, <out>.steel_frames.csv)" : "")
            + "\n" + orientNote
            + (sr != null ? "\n" + sr.Note : "");
    }



    static void WriteCleanViewer(string outBase, string modelId, string lengthUnit, List<CEdge> clean, Dictionary<int, double[]> P, double[] off, double scale, SteelModel sm, SupportRun sr)
    {
        var nodes = new Dictionary<int, Node>(); int next = 1; var map = new Dictionary<int, int>();
        Func<int, int> nd = n => { int id; if (!map.TryGetValue(n, out id)) { id = DuctGeom.MakeNode(nodes, ref next, P[n], off, scale); map[n] = id; } return id; };
        var sections = new List<RawViewerWriter.SectionDef>();
        sections.Add(RawViewerWriter.SectionDef.Pipe("rigid link", (float)(1.5 * scale / 12), (float)(0.375 * scale / 12)));
        var secIx = new Dictionary<string, int>(StringComparer.Ordinal);
        var beams = new Dictionary<int, RawViewerWriter.BeamMember>(); var labels = new Dictionary<int, string>();
        var bySys = new SortedDictionary<string, List<uint>>(StringComparer.Ordinal); var gRigid = new List<uint>(); var gTrans = new List<uint>(); var gConf = new List<uint>(); var gCut = new List<uint>(); var gSpan = new List<uint>(); var beamOf = new Dictionary<CEdge, int>();
        int nb = 1; double ft = scale / 12;
        foreach (CEdge ce in clean)
        {
            int s = 0;
            if (!ce.Rigid && ce.Dim != null)
            {
                double[] d = ce.Dim;
                if (ce.DimB != null && ce.DimB[0] == d[0] && (d[0] == 1 ? ce.DimB[3] > d[3] : ce.DimB[1] * ce.DimB[2] > d[1] * d[2])) d = ce.DimB;   // larger end
                bool round = d[0] == 1 && !double.IsNaN(d[3]), rect = d[0] == 0 && !double.IsNaN(d[1]) && !double.IsNaN(d[2]);
                if (round || rect)
                {
                    double t = double.IsNaN(ce.Thk) ? 0.06 : ce.Thk;
                    string name = CleanModel.DimText(d) + string.Format(Inv, " t{0:0.###}", t);
                    if (!secIx.TryGetValue(name, out s))
                    {
                        sections.Add(round ? RawViewerWriter.SectionDef.Pipe(name, (float)(d[3] * ft), (float)(t * ft))
                                           : RawViewerWriter.SectionDef.Box(name, (float)(d[1] * ft), (float)(d[2] * ft), (float)(t * ft)));
                        s = sections.Count - 1; secIx[name] = s;
                    }
                }
            }
            int a = nd(ce.A), b = nd(ce.B); if (a == b) continue;
            var m = DuctGeom.Beam(nb, a, b, s, nodes);
            m.LocalY = WidthDir(ce, P);
            beams[nb] = m;
            string sys = ce.Src.Count > 0 ? ce.Src[0].SysName : (ce.Source == "navisworks-only" ? "navisworks-only" : "?");
            labels[nb] = string.Format(Inv, "clean | system {0} | {1}{2} | {3:0.##} ft | parts {4}{5}",
                sys, ce.Rigid ? "rigid link" : CleanModel.DimText(ce.Dim), ce.DimB != null ? " -> " + CleanModel.DimText(ce.DimB) : "", DuctMath.Len(P[ce.A], P[ce.B]),
                ce.Src.Count > 0 ? string.Join(" ", ce.Src.Select(x => x.Id).ToArray()) : (ce.NavisId != "" ? "Navisworks #" + ce.NavisId : "-"),
                ce.Flags.Count > 0 ? " | FLAGS " + string.Join(" ", ce.Flags.OrderBy(x => x, StringComparer.Ordinal).ToArray()) : "");
            List<uint> l; if (!bySys.TryGetValue(sys, out l)) { l = new List<uint>(); bySys[sys] = l; } l.Add((uint)nb);
            if (ce.Rigid) gRigid.Add((uint)nb);
            if (ce.DimB != null) gTrans.Add((uint)nb);
            if (ce.Flags.Contains("OVERLAP_SIZE_CONFLICT")) gConf.Add((uint)nb);
            if (ce.Flags.Contains("CUT_END")) gCut.Add((uint)nb);
            if (ce.Flags.Contains("UNSUPPORTED_SPAN")) gSpan.Add((uint)nb);
            beamOf[ce] = nb;
            nb++;
        }
        SteelViewWriter.View sv = sm != null ? SteelViewWriter.Add(sm, nodes, ref next, sections, beams, labels, ref nb, off, scale) : null;
        SupportView pv = AddSupports(sr, P, nodes, ref next, sections, beams, labels, ref nb, off, scale);
        if (beams.Count == 0) return;
        var comps = new List<RawViewerWriter.Component>(); comps.Add(new RawViewerWriter.Component("Axial N", "force", "kip"));
        var units = new Dictionary<string, string>(); units["length"] = lengthUnit;
        var w = new RawViewerWriter(outBase + ".bin", nodes, null, null, null, beams, sections, comps, modelId, units);
        w.SetBeamLabels(labels); w.Write(false);
        var sc = new FeaturesSidecar(); sc.ModelId = modelId; sc.GeometryHash = w.GeometryHash;
        sc.Units["length"] = lengthUnit; sc.Units["worldOffset"] = string.Format(Inv, "{0} {1} {2}", off[0] * scale, off[1] * scale, off[2] * scale);
        string[] pal = { "#4f8cff", "#ff8a3d", "#3fe0e0", "#b07cff", "#8ae04f", "#ffd23f", "#ff5fa2", "#f0f0f0" }; int pi = 0;
        foreach (KeyValuePair<string, List<uint>> kv in bySys) sc.AddGroup("Clean: " + kv.Key, pal[pi++ % pal.Length], "beams", kv.Value, new[] { "duct", "clean", "system" }, null);
        if (gRigid.Count > 0) sc.AddGroup("Clean: rigid links", "#ff3b3b", "beams", gRigid, new[] { "duct", "clean", "link" }, "RIGID");
        if (gTrans.Count > 0) sc.AddGroup("Clean: transitions", "#ffd23f", "beams", gTrans, new[] { "duct", "clean", "transition" }, null);
        if (gConf.Count > 0) sc.AddGroup("Flag: OVERLAP_SIZE_CONFLICT", "#ff5fa2", "beams", gConf, new[] { "duct", "clean", "flag" }, "FLAG_OVERLAP_SIZE_CONFLICT");
        if (gCut.Count > 0) sc.AddGroup("Clean: cut ends", "#ffffff", "beams", gCut, new[] { "duct", "clean", "cut" }, "FLAG_CUT_END");
        new DuctSystems(Cfg).ApplySystemView(sc);
        SteelViewWriter.AddGroups(sc, sv);
        AddSupportGroups(sc, pv);
        AddSpanGroup(sc, sr, clean, P, beamOf, gSpan);
        AddFloatingGroup(sc, sr, clean, P, beamOf);   // DuctsCleanSupports.cs: stability rule 1
        AddHangerGroups(sc, sr != null ? sr.Hangers : null, sr, clean, beamOf, HangerRowsForView, pv);   // DuctsCleanHangers.cs   // DuctsCleanSupports.cs: warn glyph per span
        File.WriteAllText(outBase + ".features.json", sc.ToJson(), new UTF8Encoding(false));
    }

    // a rectangular edge's width direction (W_in lies along it), unit and square to the edge; null = not known (rigid,
    // round, or the run had no connector width directions)
    static double[] WidthDir(CEdge ce, Dictionary<int, double[]> P)
    {
        if (ce.Y == null || ce.Rigid || (ce.Dim != null && ce.Dim[0] == 1)) return null;
        double[] y = ce.Y, x = DuctMath.Unit(DuctMath.Sub3(P[ce.B], P[ce.A])); double dt = DuctMath.Dot3(y, x);
        double[] yy = DuctMath.Unit(new[] { y[0] - dt * x[0], y[1] - dt * x[1], y[2] - dt * x[2] });
        return DuctMath.Dot3(yy, yy) > 0.5 ? yy : null;
    }

    // the SAP input: one frame per clean element, inches, Navisworks frame; W2 / H2 / Dia2 = a transition's other end;
    // WX / WY / WZ = the width direction (SapDucts sets the local axis angle from it); Thk_in = the parts' Navisworks
    // wall thickness, else 0.06 (ThkSource "default")
    static void WriteCleanFrames(string path, List<CEdge> clean, Dictionary<int, double[]> P)
    {
        var s = new StringBuilder();
        s.AppendLine("Frame,JointI,JointJ,XI_in,YI_in,ZI_in,XJ_in,YJ_in,ZJ_in,Shape,W_in,H_in,Dia_in,W2_in,H2_in,Dia2_in,Thk_in,ThkSource,SizeSource,Source,RevitElementId,NavisId,Flags,System,WX,WY,WZ,SystemColor,SystemRole,Span");
        int f = 0;
        foreach (CEdge ce in clean)
        {
            double[] a = P[ce.A], b = P[ce.B]; double[] d = ce.Dim ?? new[] { 0.0, double.NaN, double.NaN, double.NaN }, d2 = ce.DimB;
            string shape = ce.Rigid ? "rigid" : d[0] == 1 ? "round" : "rect";
            double[] wd = WidthDir(ce, P);
            s.AppendLine(string.Join(",", new[] { "F" + (++f), "N" + ce.A, "N" + ce.B,
                R3(a[0] * 12), R3(a[1] * 12), R3(a[2] * 12), R3(b[0] * 12), R3(b[1] * 12), R3(b[2] * 12), shape,
                ce.Rigid ? "" : R3(d[1]), ce.Rigid ? "" : R3(d[2]), ce.Rigid ? "" : R3(d[3]),
                d2 != null ? R3(d2[1]) : "", d2 != null ? R3(d2[2]) : "", d2 != null ? R3(d2[3]) : "",
                ce.Rigid ? "" : double.IsNaN(ce.Thk) ? "0.06" : R3(ce.Thk), ce.Rigid ? "" : double.IsNaN(ce.Thk) ? "default" : "navisworks",
                ce.Rigid ? "rigid" : d2 != null ? "transition" : "revit", ce.Source,
                string.Join(";", ce.Src.Select(x => x.Id).ToArray()), ce.NavisId,
                string.Join(" ", ce.Flags.OrderBy(x => x, StringComparer.Ordinal).ToArray()),
                ce.Src.Count > 0 ? ce.Src[0].SysName : ce.Source,
                wd != null ? wd[0].ToString("0.######", Inv) : "", wd != null ? wd[1].ToString("0.######", Inv) : "", wd != null ? wd[2].ToString("0.######", Inv) : "",
                new DuctSystems(Cfg).SystemColor(ce.Src.Count > 0 ? ce.Src[0].SysName : ""),
                ce.Src.Count > 0 ? new DuctSystems(Cfg).SystemRole(ce.Src[0].SysName) : ce.Source == "navisworks-only" ? "navisworks-only" : "", ce.Span }.Select(DuctGeom.CsvCell).ToArray()));
        }
        File.WriteAllText(path, s.ToString(), new UTF8Encoding(false));
    }
}
