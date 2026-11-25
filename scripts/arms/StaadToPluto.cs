using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

// StaadToPluto -- a STAAD.Pro frame model's output (.anl) -> a Pluto viewer model, <base>.bin (v4, beams only) +
// <base>.features.json, through AnlFrameReader (scripts/lib/readers/AnlFrameReader.cs, AnlFramePrints.cs). Runner:
// scripts/staad/Run-StaadFrame.ps1. vault/arms/staad-frame-to-viewer.md. C# 5 (prefix StaadFrame).
//
// What it draws and where each value comes from:
//   geometry    STAAD's axes kept (Y up: the file's upAxis), joints in inches; a model the reader marks with an Error
//               (SET Z UP, generated joints or members, a list it cannot expand) is refused, every problem named
//   members     the member's own local axes (BETA turned), the viewer's y = -STAAD's z and z = STAAD's y (the depth);
//               releases as release bits (STAAD's FX FY FZ MX MY MZ are SAP's P V2 V3 T M2 M3), a spring or partial
//               release as partly fixed; offsets across the member as the section's offset (along it: not drawn)
//   sections    a TABLE shape: SAP's section library entry of that name (FrameSapLibrary; STAAD's older AMERICAN
//               names L40404, HSST6X6X0.375, PIPX20 tried as L4X4X1/4, HSS6X6X3/8, Pipe2XS), drawn as SapToPluto draws
//               the SAP section; a single angle turned to STAAD's principal axes (FrameStd.AngleTurn, as StdToFrame
//               reads it into SAP). Else the shape of its family fitted to STAAD's PRINT MEMBER PROPERTIES (I, tube,
//               pipe; any other a rectangle of the same radii of gyration); PRIS YD / ZD as the rectangle or circle;
//               else a 4 in square, flagged
//   results     per load case, every one printed:
//                 force        Axial Fx, Shear Fy, Shear Fz, Shear |V| (PRINT MEMBER FORCES)
//                 moment       Torsion Mx, Bending My, Bending Mz, Bending |M|
//                              the force inside the member at each end: end A's printed force negated, end B's as
//                              printed, so a member's two ends read alike (tension positive; a fixed-ended beam's two
//                              end moments the same sign)
//                 displacement DX DY DZ |D| RX RY RZ, global (PRINT JOINT DISPLACEMENTS; the deformed shape); a case
//                              that leaves out a member's joint is not written
//                 restraint    Reaction FX FY FZ |F| MX MY MZ |M|, global, as printed (PRINT SUPPORT REACTIONS): on
//                              every member end at the support; they colour the support symbols
//                 code         Code ratio, Code case (CHECK CODE): one value per member, the same at both ends
//   code checks a CHECK CODE under a LOAD LIST of one case is that case's check: its ratios go into the case, beside
//               its forces (the first such check of a case; a later one of the same case is an entry of its own). Any
//               other CHECK CODE (several cases, or none listed: every case) is an entry of its own, "Check <n>: <code>,
//               worst of <list>", holding each member's governing ratio and case. Where one code was checked case by
//               case in two cases or more, one more entry holds each member's largest ratio over them, and its case
//   sidecar     one beam group per section (shown), one per member kind (TRUSS ...), one per code of the members that
//               FAIL and that were NOT DESIGNED (unticked, painting over the sections when ticked), one ticked node
//               group per support type, and the "supports" items the viewer draws as support symbols. A previous
//               <base>.features.json is merged (colour / visibility edits, hand-made groups, section cuts survive)

public sealed class StaadFrameExportResult
{
    public string AnlPath, BinPath, SidecarPath, SidecarMerge, ModelId, GeometryHash, Library;
    public string ForceUnit = "", MomentUnit = "", DispUnit = "", ReactionForceUnit = "", ReactionMomentUnit = "";
    public double[] WorldOffset;
    public int Nodes, Beams, Sections, SectionsFromLibrary, SectionsFitted, SectionsPris, SectionsGuessed, AnglesTurned;
    public int Released, Offset, Supports, SupportGroups, Groups, Components;
    public int ForceCases, DispCases, DispCasesDropped, ReactionCases, CodeChecks, CodeChecksInCases, CodeCheckEntries, CodeEnvelopes;
    public int ReaderErrors, ReaderWarnings;
    public readonly List<string> LoadCaseNames = new List<string>();
    public readonly List<string> Warnings = new List<string>();
    public readonly List<string> Notes = new List<string>();

    public string Summary()
    {
        var sb = new StringBuilder();
        sb.AppendLine(AnlPath + ": " + I(Nodes) + " joints, " + I(Beams) + " members, " + I(Sections) + " sections (" +
            I(SectionsFromLibrary) + " from SAP's library, " + I(SectionsFitted) + " fitted to STAAD's member properties, " +
            I(SectionsPris) + " PRIS, " + I(SectionsGuessed) + " drawn as a placeholder)" +
            (AnglesTurned > 0 ? ", " + I(AnglesTurned) + " single angle member(s) turned to STAAD's principal axes" : ""));
        sb.AppendLine("members: " + I(Released) + " with releases, " + I(Offset) + " with offsets; " + I(Supports) + " supports");
        sb.AppendLine("results: " + I(LoadCaseNames.Count) + " entries; forces in " + I(ForceCases) + " (" + ForceUnit + ", " + MomentUnit +
            "), displacements in " + I(DispCases) + (DispCasesDropped > 0 ? " (" + I(DispCasesDropped) + " left out: a member joint missing)" : "") +
            ", reactions in " + I(ReactionCases));
        sb.AppendLine("code checks: " + I(CodeChecks) + " CHECK CODE command(s) read; " + I(CodeChecksInCases) + " into their load case, " +
            I(CodeCheckEntries) + " an entry of their own, " + I(CodeEnvelopes) + " largest-over-cases entr" + (CodeEnvelopes == 1 ? "y" : "ies"));
        sb.AppendLine("sidecar: " + I(Groups) + " groups, " + I(Supports) + " support items" + (!string.IsNullOrEmpty(SidecarMerge) ? "; " + SidecarMerge : ""));
        sb.AppendLine("  bin      -> " + BinPath);
        sb.Append("  features -> " + SidecarPath);
        return sb.ToString();
    }

    public string ToJson()
    {
        var sb = new StringBuilder("{");
        Prop(sb, "anl", AnlPath); Prop(sb, "bin", BinPath); Prop(sb, "features", SidecarPath); Prop(sb, "modelId", ModelId);
        Prop(sb, "geometryHash", GeometryHash); Prop(sb, "library", Library);
        Key(sb, "loadCases"); List(sb, LoadCaseNames);
        Prop(sb, "nodes", Nodes); Prop(sb, "beams", Beams); Prop(sb, "sections", Sections); Prop(sb, "sectionsFromLibrary", SectionsFromLibrary);
        Prop(sb, "sectionsFitted", SectionsFitted); Prop(sb, "sectionsPris", SectionsPris); Prop(sb, "sectionsGuessed", SectionsGuessed);
        Prop(sb, "anglesTurned", AnglesTurned); Prop(sb, "released", Released); Prop(sb, "offset", Offset); Prop(sb, "supports", Supports);
        Prop(sb, "groups", Groups); Prop(sb, "components", Components); Prop(sb, "forceCases", ForceCases); Prop(sb, "dispCases", DispCases);
        Prop(sb, "dispCasesDropped", DispCasesDropped); Prop(sb, "reactionCases", ReactionCases); Prop(sb, "codeChecks", CodeChecks);
        Prop(sb, "codeChecksInCases", CodeChecksInCases); Prop(sb, "codeCheckEntries", CodeCheckEntries); Prop(sb, "codeEnvelopes", CodeEnvelopes);
        Prop(sb, "forceUnit", ForceUnit); Prop(sb, "momentUnit", MomentUnit); Prop(sb, "dispUnit", DispUnit);
        Prop(sb, "readerErrors", ReaderErrors); Prop(sb, "readerWarnings", ReaderWarnings);
        Key(sb, "warnings"); List(sb, Warnings);
        Key(sb, "notes"); List(sb, Notes);
        sb.Append("}");
        return sb.ToString();
    }

    static string I(int v) { return v.ToString(CultureInfo.InvariantCulture); }
    static void Key(StringBuilder sb, string k) { if (sb.Length > 1) sb.Append(","); Str(sb, k); sb.Append(":"); }
    static void Prop(StringBuilder sb, string k, string v) { Key(sb, k); Str(sb, v); }
    static void Prop(StringBuilder sb, string k, int v) { Key(sb, k); sb.Append(I(v)); }
    static void List(StringBuilder sb, List<string> l)
    {
        sb.Append("[");
        for (int i = 0; i < l.Count; i++) { if (i > 0) sb.Append(","); Str(sb, l[i]); }
        sb.Append("]");
    }
    static void Str(StringBuilder sb, string s)
    {
        if (s == null) { sb.Append("null"); return; }
        sb.Append('"');
        foreach (char c in s)
        {
            if (c == '"' || c == '\\') sb.Append('\\').Append(c);
            else if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
            else sb.Append(c);
        }
        sb.Append('"');
    }
}

// one CHECK CODE command's results, and where they go
sealed class StaadFrameCheck
{
    public AnlFramePrint Print;
    public string Code = "";
    public readonly List<AnlFrameCodeCheck> Rows = new List<AnlFrameCodeCheck>();
    public int Plane;           // the load case id its ratios go into
    public bool InCase;         // a LOAD LIST of one case: that case's own check
}

public static class StaadToPluto
{
    static readonly CultureInfo CI = CultureInfo.InvariantCulture;
    const int CheckIdBase = 1000000, EnvelopeIdBase = 2000000;     // entries of their own: above any STAAD case number
    static readonly string[] DofKeys = { "tx", "ty", "tz", "rx", "ry", "rz" };

    // anlPath: the .anl; outBase: <base> of <base>.bin + <base>.features.json; modelId: null = "staad/<name>";
    // sapLibrary: SAP's section library folder (null: the newest SAP installed, FrameSapLibrary.Folder; none: sections
    // from STAAD's member properties)
    public static StaadFrameExportResult Export(string anlPath, string outBase, string modelId, string sapLibrary)
    {
        var res = new StaadFrameExportResult();
        res.AnlPath = Path.GetFullPath(anlPath);
        if (string.IsNullOrEmpty(modelId)) modelId = "staad/" + Path.GetFileNameWithoutExtension(anlPath).Replace(' ', '_');
        res.ModelId = modelId;

        AnlFrameModel m = AnlFrameReader.Read(anlPath);
        res.ReaderErrors = m.ErrorCount; res.ReaderWarnings = m.WarningCount;
        if (m.ErrorCount > 0)
            throw new Exception("StaadToPluto: the .anl reader cannot hold " + Path.GetFileName(anlPath) + " exactly (" + m.ErrorCount.ToString(CI) + " error(s)): " +
                                string.Join(" | ", m.ProblemsOf("Error").Select(p => p.Code + ": " + p.Message).ToArray()));
        foreach (var p in m.ProblemsOf("Warning")) res.Warnings.Add("reader: " + p.Code + ": " + p.Message);
        if (m.Members.Count == 0) throw new Exception("StaadToPluto: no members in " + Path.GetFileName(anlPath) + " (a plate-only model is not a frame)");

        // ---- joints (only those the members use: the beam domain is the model) ----
        var used = new HashSet<int>();
        foreach (var mem in m.Members.Values) { used.Add(mem.NodeA); used.Add(mem.NodeB); }
        double maxAbs = 0;
        double[] mn = { double.MaxValue, double.MaxValue, double.MaxValue }, mx = { double.MinValue, double.MinValue, double.MinValue };
        foreach (int id in used)
        {
            AnlFrameNode n;
            if (!m.Nodes.TryGetValue(id, out n)) continue;
            double[] p = { n.X, n.Y, n.Z };
            for (int k = 0; k < 3; k++) { maxAbs = Math.Max(maxAbs, Math.Abs(p[k])); mn[k] = Math.Min(mn[k], p[k]); mx[k] = Math.Max(mx[k], p[k]); }
        }
        double[] off = { 0, 0, 0 };
        if (maxAbs > 1e6)
        {
            for (int k = 0; k < 3; k++) off[k] = Math.Round((mn[k] + mx[k]) / 2.0 / 1000.0) * 1000.0;
            res.WorldOffset = off;
            res.Warnings.Add("coordinates reach " + N(maxAbs) + " in: (" + N(off[0]) + " " + N(off[1]) + " " + N(off[2]) + ") subtracted from every joint; it is in the sidecar's model.units.worldOffset");
        }
        var nodes = new Dictionary<int, Node>();
        foreach (int id in used.OrderBy(x => x))
        {
            AnlFrameNode n;
            if (!m.Nodes.TryGetValue(id, out n)) continue;
            var nd = new Node();
            nd.id = id;
            nd.xyz.X = (float)(n.X - off[0]); nd.xyz.Y = (float)(n.Y - off[1]); nd.xyz.Z = (float)(n.Z - off[2]);
            nodes[id] = nd;
        }
        res.Nodes = nodes.Count;

        // ---- sections ----
        string library = sapLibrary;
        if (string.IsNullOrEmpty(library)) library = FrameSapLibrary.Folder("");
        if (!string.IsNullOrEmpty(library) && !Directory.Exists(library))
        {
            res.Warnings.Add("SAP's section library " + library + " is not a folder: table shapes are drawn from STAAD's member properties");
            library = null;
        }
        res.Library = library;
        if (string.IsNullOrEmpty(library)) res.Notes.Add("no SAP section library on this machine (name its folder with -SapLibrary): table shapes are drawn from STAAD's member properties where printed");
        var propOf = new Dictionary<int, AnlFrameMemberProperty>();
        foreach (var p in m.MemberProperties) propOf[p.Member] = p;
        double propInches = InchesPer(m.MemberPropertyLengthText);
        var secs = new List<RawViewerWriter.SectionDef>();
        var secIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var secDelta = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var bySection = new Dictionary<string, List<uint>>(StringComparer.OrdinalIgnoreCase);
        var sectionOrder = new List<string>();

        // ---- members ----
        var beams = new Dictionary<int, RawViewerWriter.BeamMember>();
        var byKind = new Dictionary<string, List<uint>>(StringComparer.Ordinal);
        var alongOffsets = new List<int>();
        var fromLibrary = new List<string>();
        var noAxes = new List<int>();
        foreach (var mem in m.Members.Values)
        {
            if (!nodes.ContainsKey(mem.NodeA) || !nodes.ContainsKey(mem.NodeB) || mem.NodeA == mem.NodeB) continue;
            string key = SectionKey(mem);
            int si;
            if (!secIndex.TryGetValue(key, out si))
            {
                AnlFrameMemberProperty mp = m.Members.Values.Where(x => SectionKey(x) == key).Select(x => { AnlFrameMemberProperty q; return propOf.TryGetValue(x.Id, out q) ? q : null; }).FirstOrDefault(q => q != null);
                double delta;
                string how;
                RawViewerWriter.SectionDef def = Section(mem, key, library, mp, propInches, out delta, out how, res);
                si = secs.Count;
                secs.Add(def);
                secIndex[key] = si;
                secDelta[key] = delta;
                sectionOrder.Add(key);
                bySection[key] = new List<uint>();
                if (how.StartsWith("SAP's library", StringComparison.Ordinal) && double.IsNaN(delta)) fromLibrary.Add(key);
                else res.Notes.Add("section " + key + ": " + how);
            }
            var bm = new RawViewerWriter.BeamMember();
            bm.Id = mem.Id; bm.NodeA = mem.NodeA; bm.NodeB = mem.NodeB; bm.SectionIndex = si;
            AnlFrameNode a = m.Nodes[mem.NodeA], b = m.Nodes[mem.NodeB];
            double[] lx, ly, lz;
            bool nearVertical;
            double d = secDelta[key];
            if (!mem.BetaKnown) noAxes.Add(mem.Id);
            if (StaadAxes.Compute(new[] { a.X, a.Y, a.Z }, new[] { b.X, b.Y, b.Z }, (mem.BetaKnown ? mem.Beta : 0) - (double.IsNaN(d) ? 0 : d), out lx, out ly, out lz, out nearVertical))
            {
                bm.LocalY = new[] { -lz[0], -lz[1], -lz[2] };      // viewer y = -STAAD z; viewer z = x cross y = STAAD y (the depth)
                if (!double.IsNaN(d)) res.AnglesTurned++;
                // offsets across the member: the section's offset in the viewer's (y, z); along it, not drawn
                double[] zv = ly;
                double[] oa = OffsetVector(mem.OffsetA, mem.OffsetALocal, lx, ly, lz), ob = OffsetVector(mem.OffsetB, mem.OffsetBLocal, lx, ly, lz);
                if (oa != null) { bm.OffsetAy = Dot(oa, bm.LocalY); bm.OffsetAz = Dot(oa, zv); if (Math.Abs(Dot(oa, lx)) > 1e-9) alongOffsets.Add(mem.Id); }
                if (ob != null) { bm.OffsetBy = Dot(ob, bm.LocalY); bm.OffsetBz = Dot(ob, zv); if (Math.Abs(Dot(ob, lx)) > 1e-9) alongOffsets.Add(mem.Id); }
                if (oa != null || ob != null) res.Offset++;
            }
            bm.ReleaseMask = ReleaseBits(mem.ReleaseA, 0) | ReleaseBits(mem.ReleaseB, 6);
            if (bm.ReleaseMask != 0) res.Released++;
            beams[mem.Id] = bm;
            bySection[key].Add((uint)mem.Id);
            if (mem.Kind.Length > 0)
            {
                List<uint> l;
                if (!byKind.TryGetValue(mem.Kind, out l)) byKind[mem.Kind] = l = new List<uint>();
                l.Add((uint)mem.Id);
            }
        }
        if (fromLibrary.Count > 0) res.Notes.Insert(0, "sections from SAP's library (" + library + "): " + string.Join(", ", fromLibrary.ToArray()));
        if (beams.Count == 0) throw new Exception("StaadToPluto: no member of " + Path.GetFileName(anlPath) + " has both its joints");
        res.Beams = beams.Count; res.Sections = secs.Count;
        if (alongOffsets.Count > 0)
            res.Notes.Add("member offsets along the member are not drawn (the member is drawn joint to joint; its offsets across it are): " + Ids(alongOffsets.Distinct().ToList()));
        if (noAxes.Count > 0)
            res.Warnings.Add("members whose BETA is not a number (ANGLE, RANGLE, REFERENCE) are drawn with BETA 0: " + Ids(noAxes));
        var beamOrder = beams.Keys.OrderBy(k => k).ToList();
        var nodeEnds = new Dictionary<int, List<int[]>>();
        foreach (int bid in beamOrder)
        {
            AddEnd(nodeEnds, beams[bid].NodeA, bid, 0);
            AddEnd(nodeEnds, beams[bid].NodeB, bid, 1);
        }

        // ---- results ----
        var comps = new List<RawViewerWriter.Component>();
        var records = new List<KeyValuePair<string, List<RawViewerWriter.BeamRecord>>>();
        var disps = new List<Disp>();
        var caseNames = new Dictionary<int, string>();
        var withData = new HashSet<int>();

        // forces: the force inside the member at each end
        res.ForceUnit = m.ForceUnitText; res.MomentUnit = m.MomentUnitText;
        if (m.EndForces.Count > 0)
        {
            var force = new List<RawViewerWriter.BeamRecord>();
            var moment = new List<RawViewerWriter.BeamRecord>();
            var fcases = new HashSet<int>();
            foreach (var f in m.EndForces)
            {
                if (!beams.ContainsKey(f.Member)) continue;
                double s = f.End == "B" ? 1 : -1;
                double fx = s * f.F[0], fy = s * f.F[1], fz = s * f.F[2], tx = s * f.F[3], my = s * f.F[4], mz = s * f.F[5];
                int end = f.End == "B" ? 1 : 0;
                force.Add(Rec(f.LC, f.Member, end, new[] { fx, fy, fz, Math.Sqrt(fy * fy + fz * fz) }));
                moment.Add(Rec(f.LC, f.Member, end, new[] { tx, my, mz, Math.Sqrt(my * my + mz * mz) }));
                fcases.Add(f.LC);
            }
            if (force.Count > 0)
            {
                foreach (string nm in new[] { "Axial Fx", "Shear Fy", "Shear Fz", "Shear |V|" }) comps.Add(new RawViewerWriter.Component(nm, "force", res.ForceUnit));
                foreach (string nm in new[] { "Torsion Mx", "Bending My", "Bending Mz", "Bending |M|" }) comps.Add(new RawViewerWriter.Component(nm, "moment", res.MomentUnit));
                records.Add(new KeyValuePair<string, List<RawViewerWriter.BeamRecord>>("force", force));
                records.Add(new KeyValuePair<string, List<RawViewerWriter.BeamRecord>>("moment", moment));
                res.ForceCases = fcases.Count;
                withData.UnionWith(fcases);
            }
        }

        // displacements: per joint, a case written only when it has every member joint
        if (m.Displacements.Count > 0)
        {
            string du = (m.DisplacementLengthText ?? "").Trim();
            double scale = InchesPer(du);
            res.DispUnit = du;
            if (double.IsNaN(scale))
            {
                res.Warnings.Add("joint displacements in \"" + du + "\", not a length unit Pluto converts: written as read (the deformed shape is true to scale only in inches)");
                scale = 1;
            }
            else if (scale != 1) res.Notes.Add("joint displacements printed in " + du + ": converted to inches (x " + N(scale) + ") for the deformed shape");
            var byCase = new Dictionary<int, Dictionary<int, double[]>>();
            foreach (var r in m.Displacements)
            {
                Dictionary<int, double[]> t;
                if (!byCase.TryGetValue(r.LC, out t)) byCase[r.LC] = t = new Dictionary<int, double[]>();
                t[r.Node] = new[] { r.V[0] * scale, r.V[1] * scale, r.V[2] * scale, r.V[3], r.V[4], r.V[5] };
            }
            foreach (var kv in byCase.OrderBy(k => k.Key))
            {
                int missing = nodes.Keys.Count(n => !kv.Value.ContainsKey(n));
                if (missing > 0)
                {
                    res.DispCasesDropped++;
                    res.Warnings.Add("case " + kv.Key.ToString(CI) + ": joint displacements printed for " + (nodes.Count - missing).ToString(CI) + " of the " + nodes.Count.ToString(CI) +
                                     " member joints: not written for this case (a missing joint would draw undeformed)");
                    continue;
                }
                foreach (int n in nodes.Keys)
                {
                    double[] v = kv.Value[n];
                    var dd = new Disp();
                    dd.node = n; dd.LC = kv.Key;
                    dd.DR = new[] { (float)v[0], (float)v[1], (float)v[2], (float)Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]), (float)v[3], (float)v[4], (float)v[5] };
                    disps.Add(dd);
                }
                res.DispCases++;
                withData.Add(kv.Key);
            }
            if (res.DispCases > 0)
            {
                foreach (string nm in new[] { "DX", "DY", "DZ", "|D|" }) comps.Add(new RawViewerWriter.Component(nm, "displacement", "in"));
                foreach (string nm in new[] { "RX", "RY", "RZ" }) comps.Add(new RawViewerWriter.Component(nm, "displacement", "rad"));
            }
        }

        // reactions: a node total on every member end at the support
        if (m.Reactions.Count > 0)
        {
            var recs = new List<RawViewerWriter.BeamRecord>();
            var rcases = new HashSet<int>();
            var offModel = new HashSet<int>();
            foreach (var r in m.Reactions)
            {
                List<int[]> ends;
                if (!nodeEnds.TryGetValue(r.Node, out ends)) { offModel.Add(r.Node); continue; }
                double[] v = r.V;
                double[] vals = { v[0], v[1], v[2], Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]), v[3], v[4], v[5], Math.Sqrt(v[3] * v[3] + v[4] * v[4] + v[5] * v[5]) };
                foreach (int[] e in ends) recs.Add(Rec(r.LC, e[0], e[1], vals));
                rcases.Add(r.LC);
            }
            if (offModel.Count > 0) res.Warnings.Add("support reactions at joints no member uses: not shown: " + Ids(offModel.ToList()));
            if (recs.Count > 0)
            {
                res.ReactionForceUnit = m.ReactionForceText; res.ReactionMomentUnit = m.ReactionMomentText;
                foreach (string nm in new[] { "Reaction FX", "Reaction FY", "Reaction FZ", "Reaction |F|" }) comps.Add(new RawViewerWriter.Component(nm, "restraint", m.ReactionForceText));
                foreach (string nm in new[] { "Reaction MX", "Reaction MY", "Reaction MZ", "Reaction |M|" }) comps.Add(new RawViewerWriter.Component(nm, "restraint", m.ReactionMomentText));
                records.Add(new KeyValuePair<string, List<RawViewerWriter.BeamRecord>>("restraint", recs));
                res.ReactionCases = rcases.Count;
                withData.UnionWith(rcases);
            }
        }

        // code checks
        var checks = new List<StaadFrameCheck>();
        foreach (var g in m.CodeChecks.GroupBy(c => c.Print).OrderBy(g => g.Key))
        {
            var pr = m.Prints.FirstOrDefault(p => p.Order == g.Key);
            if (pr == null) continue;
            var ck = new StaadFrameCheck();
            ck.Print = pr;
            ck.Code = g.Select(c => c.Code).FirstOrDefault(c => !string.IsNullOrEmpty(c)) ?? "code check";
            ck.Rows.AddRange(g.Where(c => beams.ContainsKey(c.Member)));
            checks.Add(ck);
        }
        res.CodeChecks = checks.Count;
        var codeRecs = new List<RawViewerWriter.BeamRecord>();
        var checkedCase = new HashSet<int>();
        var perCase = new Dictionary<string, List<StaadFrameCheck>>(StringComparer.Ordinal);
        var perCaseOrder = new List<string>();
        foreach (var ck in checks)
        {
            int only = !ck.Print.AllCases && ck.Print.Cases.Count == 1 ? ck.Print.Cases[0] : 0;
            // a check that gave no ratio (STAAD checked nothing: an envelope of no strength or serviceability TYPE, round 4)
            // does not take its case's place from a later check that did
            bool rated = ck.Rows.Any(c => c.HasRatio);
            if (only != 0 && rated && checkedCase.Add(only))
            {
                ck.InCase = true; ck.Plane = only;
                res.CodeChecksInCases++;
                withData.Add(only);
                List<StaadFrameCheck> l;
                if (!perCase.TryGetValue(ck.Code, out l)) { perCase[ck.Code] = l = new List<StaadFrameCheck>(); perCaseOrder.Add(ck.Code); }
                l.Add(ck);
            }
            else
            {
                ck.Plane = CheckIdBase + ck.Print.Order;
                caseNames[ck.Plane] = "Check " + ck.Print.Order.ToString(CI) + ": " + ck.Code + ", " +
                    (only != 0 ? "case " + only.ToString(CI) : "worst of " + (ck.Print.AllCases ? "every case" : "LOAD LIST " + ck.Print.LoadList));
                res.CodeCheckEntries++;
                if (only != 0 && !rated) res.Notes.Add("CHECK CODE " + ck.Print.Order.ToString(CI) + " (" + ck.Code + ", LOAD LIST " + ck.Print.LoadList + ") gave no ratio: an entry of its own, not case " + only.ToString(CI) + "'s");
                else if (only != 0) res.Notes.Add("CHECK CODE " + ck.Print.Order.ToString(CI) + " (" + ck.Code + ") is case " + only.ToString(CI) + "'s second check: an entry of its own");
            }
            foreach (var c in ck.Rows)
            {
                double r = c.HasRatio ? c.Ratio : double.NaN, lc = c.HasRatio && c.LC != 0 ? c.LC : double.NaN;
                codeRecs.Add(Rec(ck.Plane, c.Member, 0, new[] { r, lc }));
                codeRecs.Add(Rec(ck.Plane, c.Member, 1, new[] { r, lc }));
            }
        }
        int env = 0;
        foreach (string code in perCaseOrder)
        {
            var l = perCase[code];
            if (l.Count < 2) continue;
            int id = EnvelopeIdBase + (++env);
            caseNames[id] = "Code ratio " + code + ", largest over cases " + RangeText(l.Select(c => c.Plane).ToList());
            var best = new Dictionary<int, double[]>();
            foreach (var ck in l)
                foreach (var c in ck.Rows)
                {
                    if (!c.HasRatio) continue;
                    double[] b;
                    if (!best.TryGetValue(c.Member, out b) || c.Ratio > b[0]) best[c.Member] = new double[] { c.Ratio, ck.Plane };
                }
            foreach (var kv in best)
            {
                codeRecs.Add(Rec(id, kv.Key, 0, kv.Value));
                codeRecs.Add(Rec(id, kv.Key, 1, kv.Value));
            }
            res.CodeEnvelopes++;
        }
        if (codeRecs.Count > 0)
        {
            comps.Add(new RawViewerWriter.Component("Code ratio", "code", null));
            comps.Add(new RawViewerWriter.Component("Code case", "code", null));
            records.Add(new KeyValuePair<string, List<RawViewerWriter.BeamRecord>>("code", codeRecs));
        }

        // the load cases with results, in STAAD's numbering, then the checks' own entries
        foreach (int lc in withData.OrderBy(x => x))
        {
            AnlFrameLoadCase c;
            string title = m.LoadCases.TryGetValue(lc, out c) && !string.IsNullOrEmpty(c.Title) ? c.Title.Trim() : "";
            caseNames[lc] = lc.ToString(CI) + (title.Length > 0 ? ": " + title : "");
        }
        var ordered = caseNames.Keys.OrderBy(k => k).ToList();
        foreach (int k in ordered) res.LoadCaseNames.Add(caseNames[k]);
        bool hasResults = caseNames.Count > 0 && comps.Count > 0;

        // ---- binary ----
        string binPath = outBase + ".bin";
        string dir = Path.GetDirectoryName(Path.GetFullPath(binPath));
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
        var units = new Dictionary<string, string>();
        units["length"] = "in";
        units["force"] = res.ForceUnit;
        if (!hasResults)
        {
            comps.Clear();
            comps.Add(new RawViewerWriter.Component("Axial Fx", "force", res.ForceUnit));   // layout only; no planes written
        }
        var w = new RawViewerWriter(binPath, nodes, null, hasResults ? caseNames : null, null, beams, secs, comps, modelId, units);
        w.UpAxis = "Y";
        w.Write(hasResults);
        if (hasResults)
        {
            if (disps.Count > 0) w.AppendDisplacements(disps);
            foreach (var kv in records) if (kv.Value.Count > 0) w.AppendBeamForces(kv.Value, kv.Key);
        }
        res.Components = hasResults ? comps.Count : 0;
        res.BinPath = binPath;
        res.GeometryHash = w.GeometryHash;

        // ---- sidecar ----
        var sc = new FeaturesSidecar();
        sc.ModelId = modelId;
        sc.GeometryHash = w.GeometryHash;
        sc.Units["length"] = "in";
        sc.Units["force"] = res.ForceUnit;
        sc.Units["worldOffset"] = string.Format(CI, "{0} {1} {2}", off[0], off[1], off[2]);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string s in sectionOrder)
        {
            var grp = sc.AddGroup(Unique(names, s), null, "beams", bySection[s], new[] { "staad", "section" });
            grp.Hidden = false;
        }
        foreach (var kv in byKind.OrderBy(k => k.Key, StringComparer.Ordinal))
            sc.AddGroup(Unique(names, "MEMBER " + kv.Key), null, "beams", kv.Value, new[] { "staad", "kind" }).Hidden = true;
        foreach (string code in checks.Select(c => c.Code).Distinct())
        {
            var mine = checks.Where(c => c.Code == code).SelectMany(c => c.Rows).ToList();
            var fail = mine.Where(c => c.Status == "FAIL").Select(c => (uint)c.Member).Distinct().OrderBy(x => x).ToList();
            var nd = mine.Where(c => c.Status == "NOT DESIGNED").Select(c => (uint)c.Member).Distinct().OrderBy(x => x).ToList();
            if (fail.Count > 0) sc.AddGroup(Unique(names, "FAIL " + code), "#e53935", "beams", fail, new[] { "staad", "code", "fail" }).Hidden = true;
            if (nd.Count > 0) sc.AddGroup(Unique(names, "NOT DESIGNED " + code), "#8e24aa", "beams", nd, new[] { "staad", "code", "notDesigned" }).Hidden = true;
        }
        Supports(sc, m, nodes, names, res);
        sc.SourceJson = PipeSidecar.SourceOf(anlPath, "anl", null);
        string scPath = outBase + ".features.json";
        res.SidecarMerge = sc.MergeFrom(scPath, "staad");
        res.Groups = sc.Groups.Count;
        File.WriteAllText(scPath, sc.ToJson(), new UTF8Encoding(false));
        res.SidecarPath = scPath;
        return res;
    }

    // ---------------- supports: one item per support joint, one ticked node group per support type

    static void Supports(FeaturesSidecar sc, AnlFrameModel m, Dictionary<int, Node> nodes, HashSet<string> names, StaadFrameExportResult res)
    {
        sc.WriteSupports = true;
        var byType = new Dictionary<string, List<uint>>(StringComparer.Ordinal);
        var typeOrder = new List<string>();
        var off = new List<int>();
        var inclined = new List<int>();
        foreach (var s in m.Supports.Values)
        {
            if (!nodes.ContainsKey(s.Node)) { off.Add(s.Node); continue; }
            var spr = Springs(s.Springs);
            bool anchor = s.Restrained.All(b => b);
            var dof = new List<KeyValuePair<string, string>>();
            var entries = new List<string>();
            if (anchor)
            {
                entries.Add("{\"type\": \"ANC\", \"kind\": \"anchor\"}");
                for (int i = 0; i < 6; i++) dof.Add(new KeyValuePair<string, string>(DofKeys[i], "fixed"));
            }
            else
            {
                for (int i = 0; i < 6; i++)
                {
                    if (!s.Restrained[i] && spr[i] == 0) continue;
                    double[] d = { i % 3 == 0 ? 1 : 0, i % 3 == 1 ? 1 : 0, i % 3 == 2 ? 1 : 0 };
                    string kind = s.Restrained[i] ? (i < 3 ? "translation" : "rotation") : "spring";
                    string e = "{\"type\": " + Q(AnlFrameRelease.Dofs[i] + (s.Restrained[i] ? "" : " spring")) + ", \"kind\": " + Q(kind) +
                               ", \"direction\": [" + N(d[0]) + ", " + N(d[1]) + ", " + N(d[2]) + "]";
                    if (!s.Restrained[i]) e += ", \"stiffness\": " + N(spr[i]);
                    entries.Add(e + "}");
                    dof.Add(new KeyValuePair<string, string>(DofKeys[i], s.Restrained[i] ? "fixed" : "spring"));
                }
            }
            if (entries.Count == 0) continue;
            string type = (s.Type ?? "").Trim().ToUpperInvariant();
            if (type.StartsWith("INCLINED", StringComparison.Ordinal)) inclined.Add(s.Node);
            string name = s.Node.ToString(CI) + " " + (anchor ? "fixed" : string.Join(" ", Enumerable.Range(0, 6).Where(i => s.Restrained[i] || spr[i] != 0)
                          .Select(i => AnlFrameRelease.Dofs[i] + (s.Restrained[i] ? "" : "~")).ToArray()));
            sc.AddSupport(name, new[] { (uint)s.Node }, dof, new[] { "staad", "support" },
                          "\"restraints\": [" + string.Join(", ", entries.ToArray()) + "], \"source\": {\"exporter\": \"staad\", \"joint\": " + s.Node.ToString(CI) +
                          ", \"spec\": " + Q(s.Spec) + "}");
            res.Supports++;
            string gname = "Support " + (type.Length > 0 ? type : "springs");
            List<uint> l;
            if (!byType.TryGetValue(gname, out l)) { byType[gname] = l = new List<uint>(); typeOrder.Add(gname); }
            l.Add((uint)s.Node);
        }
        foreach (string g in typeOrder)
        {
            sc.AddNodeGroup(Unique(names, g), null, byType[g], new[] { "staad", "support" }).Hidden = false;
            res.SupportGroups++;
        }
        if (off.Count > 0) res.Warnings.Add("supports at joints no member uses: not drawn: " + Ids(off));
        if (inclined.Count > 0) res.Warnings.Add("INCLINED supports drawn along the global axes (their own axes are not read): " + Ids(inclined));
    }

    // KFX f KFY f ... -> the stiffness per DOF (0 none)
    static double[] Springs(string text)
    {
        var v = new double[6];
        string[] t = (text ?? "").Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i + 1 < t.Length; i++)
        {
            string u = t[i].ToUpperInvariant();
            int k = u.Length == 3 && u[0] == 'K' ? Array.IndexOf(AnlFrameRelease.Dofs, u.Substring(1)) : -1;
            double x;
            if (k >= 0 && double.TryParse(t[i + 1], NumberStyles.Float, CI, out x)) { v[k] = x; i++; }
        }
        return v;
    }

    // ---------------- sections

    // the key of a member's section: its table shape's name, or the property as written (PRIS ...)
    static string SectionKey(AnlFrameMember mem)
    {
        string p = Regex.Replace(mem.Property ?? "", @"\s+", " ").Trim();
        if (mem.Section.Length > 0 && mem.Section != "PRIS" && Regex.IsMatch(p, @"^TABLE\s", RegexOptions.IgnoreCase))
        {
            string why;
            var t = StdToFrame.TableOf(p.Contains("'") ? "'db'" : "AMERICAN", p, out why);
            if (t != null) return (t[0] == "ST" ? "" : t[0] + " ") + t[1] + (t[2] != "0" ? " SP " + t[2] : "");
            return p.Substring(6).Trim();
        }
        return p.Length > 0 ? p : "(no property)";
    }

    static RawViewerWriter.SectionDef Section(AnlFrameMember mem, string key, string library, AnlFrameMemberProperty mp, double propInches,
                                              out double delta, out string how, StaadFrameExportResult res)
    {
        delta = double.NaN;
        string p = Regex.Replace(mem.Property ?? "", @"\s+", " ").Trim();
        string why = null;
        // a table shape: SAP's library
        if (Regex.IsMatch(p, @"^TABLE\s", RegexOptions.IgnoreCase))
        {
            var t = StdToFrame.TableOf(p.Contains("'") ? "'db'" : "AMERICAN", p, out why);
            if (t != null && !string.IsNullOrEmpty(library))
            {
                string file = null, row = null, w = null;
                double sp;
                double.TryParse(t[2], NumberStyles.Float, CI, out sp);
                sp *= mem.PropertyInches;
                foreach (string n in Candidates(t[1]))
                {
                    if (t[0] == "T") row = FrameSapLibrary.TeeRow(library, n, "in", out file, out w);
                    else if (t[0] == "LD" || t[0] == "SD") row = FrameSapLibrary.DoubleAngleRow(library, n, t[0] == "LD", sp, "in", out file, out w);
                    else row = FrameSapLibrary.Row(library, n, "in", out file, out w);
                    if (row != null) break;
                    why = w;                                    // the last name tried says most (AISC's, not STAAD's older one)
                }
                if (row != null)
                {
                    var r = new S2kRow();
                    S2kRow.Tokenize(row, r);
                    var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < r.Keys.Count; i++) props[r.Keys[i]] = r.Values[i];
                    bool known;
                    var def = SapToPluto.FrameSection(key, props, out known);
                    if (known)
                    {
                        string shape = Get(props, "Shape"), label = Get(props, "SectInFile");
                        how = "SAP's library, " + label + " (" + shape + ") in " + Path.GetFileName(file);
                        double i33, i22, i23;
                        if (shape.Trim().Equals("Angle", StringComparison.OrdinalIgnoreCase) && t[0] == "ST" &&
                            Num(props, "I33", out i33) && Num(props, "I22", out i22) && Num(props, "I23", out i23) && i23 != 0)
                        {
                            delta = FrameStd.AngleTurn(i33, i22, i23);
                            how += "; turned " + N(Math.Round(delta, 4)) + " deg to STAAD's principal axes";
                        }
                        res.SectionsFromLibrary++;
                        return def;
                    }
                    why = "SAP's row of it is not a shape Pluto draws";
                }
            }
            else if (t != null) why = "no SAP section library";
        }
        // PRIS YD / ZD
        if (Regex.IsMatch(p, @"^PRIS", RegexOptions.IgnoreCase))
        {
            var v = PrisValues(p);
            double yd, zd;
            bool hy = v.TryGetValue("YD", out yd) && yd > 0, hz = v.TryGetValue("ZD", out zd) && zd > 0;
            yd *= mem.PropertyInches; zd *= mem.PropertyInches;
            if (hy && hz) { how = "PRIS: a rectangle ZD x YD"; res.SectionsPris++; return RawViewerWriter.SectionDef.Rect(key, (float)zd, (float)yd); }
            if (hy) { how = "PRIS: a circle of diameter YD"; res.SectionsPris++; return RawViewerWriter.SectionDef.Pipe(key, (float)yd, (float)(yd / 2)); }
        }
        // STAAD's member properties, the family's shape fitted to them
        if (mp != null && !double.IsNaN(propInches))
        {
            string fit;
            var def = Fit(key, mem.Family, mem.Section, mp, propInches, out fit);
            if (def != null)
            {
                how = fit + " fitted to STAAD's PRINT MEMBER PROPERTIES (member " + mp.Member.ToString(CI) + ")" + (why != null ? "; not from SAP's library: " + why : "");
                res.SectionsFitted++;
                return def;
            }
        }
        how = "a 4 in square placeholder (" + (why ?? "no shape known") + "; add PRINT MEMBER PROPERTIES to the .std, or name SAP's library)";
        res.SectionsGuessed++;
        res.Warnings.Add("section " + key + ": drawn as a 4 in square placeholder");
        return RawViewerWriter.SectionDef.Rect(key, 4f, 4f);
    }

    // the names to look a STAAD shape up by in SAP's library: as written, and STAAD's older AMERICAN names in AISC's
    static List<string> Candidates(string name)
    {
        var l = new List<string>();
        string n = (name ?? "").Trim().ToUpperInvariant();
        l.Add(n);
        Match a = Regex.Match(n, @"^L(\d{2})(\d{2})(\d{1,2})$");             // L40404: 4.0 x 4.0 x 4/16
        if (a.Success)
        {
            int t = int.Parse(a.Groups[3].Value, CI);
            l.Add("L" + Leg(int.Parse(a.Groups[1].Value, CI)) + "X" + Leg(int.Parse(a.Groups[2].Value, CI)) + "X" + Frac(t, 16));
        }
        Match h = Regex.Match(n, @"^HSST?(\d+(?:\.\d+)?)X(\d+(?:\.\d+)?)X(\d*\.\d+|\d+)$");   // HSST6X6X0.375
        if (h.Success && n.StartsWith("HSST", StringComparison.Ordinal))
        {
            double t = double.Parse(h.Groups[3].Value, CI);
            string head = "HSS" + h.Groups[1].Value + "X" + h.Groups[2].Value + "X";
            int s16 = (int)Math.Round(t * 16);
            if (Math.Abs(s16 / 16.0 - t) < 1e-6) l.Add(head + Frac(s16, 16));
            l.Add(head + t.ToString("0.000", CI).TrimStart('0'));
            l.Add(head + h.Groups[3].Value);
        }
        Match pp = Regex.Match(n, @"^PIP(S|X|XX)(\d{2,3})$");                // PIPX20: 2.0 in extra strong
        if (pp.Success)
        {
            string grade = pp.Groups[1].Value == "S" ? "STD" : pp.Groups[1].Value == "X" ? "XS" : "XXS";
            l.Add("Pipe" + Leg(int.Parse(pp.Groups[2].Value, CI)) + grade);
        }
        return l;
    }

    // tenths of an inch as AISC writes a size: 40 -> 4, 25 -> 2-1/2, 5 -> 1/2
    static string Leg(int tenths)
    {
        int whole = tenths / 10, rest = tenths % 10;
        string f = rest == 0 ? "" : rest == 5 ? "1/2" : (rest / 10.0).ToString("0.#", CI).TrimStart('0');
        return whole == 0 ? f : rest == 0 ? whole.ToString(CI) : whole.ToString(CI) + "-" + f;
    }

    static string Frac(int num, int den)
    {
        if (num % den == 0) return (num / den).ToString(CI);
        int whole = num / den; num %= den;
        int g = Gcd(num, den);
        string f = (num / g).ToString(CI) + "/" + (den / g).ToString(CI);
        return whole > 0 ? whole.ToString(CI) + "-" + f : f;
    }

    static int Gcd(int a, int b) { while (b != 0) { int t = a % b; a = b; b = t; } return a; }

    static Dictionary<string, double> PrisValues(string p)
    {
        var v = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        string[] t = p.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 1; i + 1 < t.Length; i++)
        {
            double x;
            if (double.TryParse(t[i + 1], NumberStyles.Float, CI, out x)) { v[t[i].ToUpperInvariant()] = x; i++; }
        }
        return v;
    }

    // the family's shape from STAAD's printed properties (AX IZ IY IX AY AZ SZ SY), in inches
    static RawViewerWriter.SectionDef Fit(string key, string family, string section, AnlFrameMemberProperty mp, double inch, out string fit)
    {
        fit = null;
        double ax = mp.V[0] * inch * inch, iz = mp.V[1] * Math.Pow(inch, 4), iy = mp.V[2] * Math.Pow(inch, 4);
        double sz = mp.V[6] * Math.Pow(inch, 3), sy = mp.V[7] * Math.Pow(inch, 3);
        if (!(ax > 0 && iz > 0 && iy > 0)) return null;
        string f = (family ?? "").ToUpperInvariant();
        bool round = f == "PIPE" || (f == "HSS" && Regex.IsMatch(section ?? "", @"^HSS[^X]*X[^X]*$", RegexOptions.IgnoreCase));
        if (sz > 0 && sy > 0)
        {
            double d = 2 * iz / sz, bf = 2 * iy / sy;
            if ((f == "W" || f == "S" || f == "M" || f == "HP") && d > 0 && bf > 0)
            {
                double tf = 6 * iy / (bf * bf * bf), tw = (ax - 2 * bf * tf) / (d - 2 * tf);
                if (tf > 0 && 2 * tf < d && tw > 0 && tw < bf) { fit = "an I shape"; return RawViewerWriter.SectionDef.IShape(key, (float)d, (float)bf, (float)tf, (float)bf, (float)tf, (float)tw); }
            }
            if (round && d > 0)
            {
                double disc = d * d - 4 * ax / Math.PI;
                double t = disc >= 0 ? (d - Math.Sqrt(disc)) / 2 : d / 2;
                fit = "a pipe"; return RawViewerWriter.SectionDef.Pipe(key, (float)d, (float)Math.Min(t, d / 2));
            }
            if (f == "HSS" && d > 0 && bf > 0)
            {
                double s = bf + d, disc = 4 * s * s - 16 * ax;
                double t = disc >= 0 ? (2 * s - Math.Sqrt(disc)) / 8 : Math.Min(bf, d) / 2;
                if (t > 0 && 2 * t < Math.Min(bf, d)) { fit = "a tube"; return RawViewerWriter.SectionDef.Box(key, (float)bf, (float)d, (float)t); }
            }
        }
        fit = "a rectangle of the same radii of gyration";
        return RawViewerWriter.SectionDef.Rect(key, (float)Math.Sqrt(12 * iy / ax), (float)Math.Sqrt(12 * iz / ax));
    }

    // ---------------- members

    // a member end's offset as a global vector (inches); null for none
    static double[] OffsetVector(double[] o, bool local, double[] x, double[] y, double[] z)
    {
        if (o == null || (o[0] == 0 && o[1] == 0 && o[2] == 0)) return null;
        if (!local) return o;
        return new[] { o[0] * x[0] + o[1] * y[0] + o[2] * z[0], o[0] * x[1] + o[1] * y[1] + o[2] * z[1], o[0] * x[2] + o[1] * y[2] + o[2] * z[2] };
    }

    // the viewer's release bits (v4-schema 4.3): STAAD's FX FY FZ MX MY MZ in SAP's P V2 V3 T M2 M3 slots; a spring
    // (K..) or partial moment release (MP, MPX, MPY, MPZ) also partly fixed
    static uint ReleaseBits(AnlFrameRelease r, int shift)
    {
        uint m = 0;
        for (int i = 0; i < 6; i++) if (r.Released[i]) m |= 1u << (shift + i);
        string[] t = (r.Extra ?? "").ToUpperInvariant().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (string u in t)
        {
            var dofs = new List<int>();
            if (u.Length == 3 && u[0] == 'K') { int k = Array.IndexOf(AnlFrameRelease.Dofs, u.Substring(1)); if (k >= 0) dofs.Add(k); }
            else if (u == "MP") dofs.AddRange(new[] { 3, 4, 5 });
            else if (u == "MPX") dofs.Add(3);
            else if (u == "MPY") dofs.Add(4);
            else if (u == "MPZ") dofs.Add(5);
            foreach (int k in dofs) m |= (1u << (shift + k)) | (1u << (12 + shift + k));
        }
        return m;
    }

    static void AddEnd(Dictionary<int, List<int[]>> map, int node, int bid, int end)
    {
        List<int[]> l;
        if (!map.TryGetValue(node, out l)) { l = new List<int[]>(); map[node] = l; }
        l.Add(new[] { bid, end });
    }

    static RawViewerWriter.BeamRecord Rec(int lc, int member, int end, double[] v)
    {
        var r = new RawViewerWriter.BeamRecord();
        r.LC = lc; r.elemID = member; r.End = end;
        r.Values = v.Select(x => (float)x).ToArray();
        return r;
    }

    // ---------------- small things

    // inches per unit of a printed length unit ("in", "INCH", "ft", "mm" ...); NaN when not one
    static double InchesPer(string u)
    {
        switch ((u ?? "").Trim().ToUpperInvariant())
        {
            case "IN": case "INCH": case "INCHES": return 1;
            case "FT": case "FEET": case "FOOT": return 12;
            case "MM": case "MMS": return 1 / 25.4;
            case "CM": case "CMS": return 1 / 2.54;
            case "M": case "METER": case "METERS": case "METRE": return 1 / 0.0254;
            default: return double.NaN;
        }
    }

    static string Get(Dictionary<string, string> p, string k) { string v; return p.TryGetValue(k, out v) && v != null ? v : ""; }
    static bool Num(Dictionary<string, string> p, string k, out double v) { return double.TryParse(Get(p, k), NumberStyles.Float, CI, out v); }
    static double Dot(double[] a, double[] b) { return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]; }
    static string N(double v) { return v.ToString("R", CI); }
    static string Q(string s) { return "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""; }

    static string Unique(HashSet<string> used, string name)
    {
        string n = name; int k = 2;
        while (!used.Add(n)) n = name + " (" + (k++).ToString(CI) + ")";
        return n;
    }

    static string Ids(List<int> ids)
    {
        var s = ids.OrderBy(x => x).ToList();
        return string.Join(", ", s.Take(12).Select(x => x.ToString(CI)).ToArray()) + (s.Count > 12 ? " ... (" + s.Count.ToString(CI) + ")" : "");
    }

    // "101 TO 104, 201"
    static string RangeText(List<int> ids)
    {
        var s = ids.Distinct().OrderBy(x => x).ToList();
        var parts = new List<string>();
        for (int i = 0; i < s.Count; )
        {
            int j = i;
            while (j + 1 < s.Count && s[j + 1] == s[j] + 1) j++;
            parts.Add(j - i >= 2 ? s[i].ToString(CI) + " TO " + s[j].ToString(CI) : string.Join(", ", s.Skip(i).Take(j - i + 1).Select(x => x.ToString(CI)).ToArray()));
            i = j + 1;
        }
        return string.Join(", ", parts.ToArray());
    }
}
