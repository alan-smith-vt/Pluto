using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

// CaesarCompare  --  two pipe models compared by MEANING, not text: what CAESAR would read from each. Built
// for the verification loop (your model -> .cii -> CAESAR -> CAESAR's own re-export -> Compare) and for "what
// did this edit change". C# 5. Build-Pipe.ps1 -In <model> -Compare <other.cii | other.toml>.
//
//   elements    matched by FROM-TO; every modelled field compared (relative tolerance), added / removed ones
//   nodes       positions by CAESAR's rules (CaesarGeometry on what each model writes), moves beyond a tolerance
//   restraints  as written (joints expanded), matched as a multiset of node, type, CNODE, gap, stiffness
//               (rigid however written), friction, direction; SIF / tee entries likewise
//   loads       forces / moments and imposed displacements per node and vector, as a multiset; uniform loads
//               and wind on each matched element
//   carried     the blocks each element carries as written (allowables, node names ...), the file's units,
//               the MISCEL_1 tail (nozzles, hangers, options)
//   settings    ambient, vertical axis, title

public class CaesarCompareResult
{
    public string NameA = "", NameB = "";
    public readonly List<string> Settings = new List<string>();
    public readonly List<string> ElementsAdded = new List<string>();     // in B, not in A
    public readonly List<string> ElementsRemoved = new List<string>();   // in A, not in B
    public readonly List<string> ElementsChanged = new List<string>();
    public readonly List<string> NodesMoved = new List<string>();
    public readonly List<string> RestraintsAdded = new List<string>();
    public readonly List<string> RestraintsRemoved = new List<string>();
    public readonly List<string> SifsAdded = new List<string>();
    public readonly List<string> SifsRemoved = new List<string>();
    public readonly List<string> LoadsAdded = new List<string>();     // node loads in B, not in A
    public readonly List<string> LoadsRemoved = new List<string>();
    public readonly List<string> LoadsChanged = new List<string>();   // uniform / wind on matched elements
    public readonly List<string> Carried = new List<string>();
    public int ElementsCompared, NodesCompared, RestraintsCompared;
    public double WorstMove;

    public int Differences
    {
        get
        {
            return Settings.Count + ElementsAdded.Count + ElementsRemoved.Count + ElementsChanged.Count + NodesMoved.Count + RestraintsAdded.Count +
                   RestraintsRemoved.Count + SifsAdded.Count + SifsRemoved.Count + LoadsAdded.Count + LoadsRemoved.Count + LoadsChanged.Count + Carried.Count;
        }
    }

    public string Summary(int maxPerList)
    {
        var sb = new StringBuilder();
        sb.AppendLine("CaesarCompare: " + NameA + " vs " + NameB + ": " + (Differences == 0 ? "no differences" : Differences + " difference(s)") +
                      " (" + ElementsCompared + " elements, " + NodesCompared + " node positions, " + RestraintsCompared + " restraints compared; worst node move " +
                      WorstMove.ToString("0.####", CultureInfo.InvariantCulture) + ")");
        List("settings", Settings, sb, maxPerList);
        List("elements only in " + NameB, ElementsAdded, sb, maxPerList);
        List("elements only in " + NameA, ElementsRemoved, sb, maxPerList);
        List("elements changed", ElementsChanged, sb, maxPerList);
        List("nodes moved", NodesMoved, sb, maxPerList);
        List("restraints only in " + NameB, RestraintsAdded, sb, maxPerList);
        List("restraints only in " + NameA, RestraintsRemoved, sb, maxPerList);
        List("SIF / tees only in " + NameB, SifsAdded, sb, maxPerList);
        List("SIF / tees only in " + NameA, SifsRemoved, sb, maxPerList);
        List("node loads only in " + NameB, LoadsAdded, sb, maxPerList);
        List("node loads only in " + NameA, LoadsRemoved, sb, maxPerList);
        List("uniform / wind changed", LoadsChanged, sb, maxPerList);
        List("carried data", Carried, sb, maxPerList);
        return sb.ToString();
    }

    static void List(string title, List<string> items, StringBuilder sb, int max)
    {
        if (items.Count == 0) return;
        sb.AppendLine("  " + title + " (" + items.Count + "):");
        foreach (var s in items.Take(max)) sb.AppendLine("    " + s);
        if (items.Count > max) sb.AppendLine("    ... " + (items.Count - max) + " more");
    }

    public string ToJson()
    {
        var sb = new StringBuilder("{");
        sb.Append("\"a\":" + Q(NameA) + ",\"b\":" + Q(NameB) + ",\"differences\":" + Differences);
        sb.Append(",\"elementsCompared\":" + ElementsCompared + ",\"nodesCompared\":" + NodesCompared + ",\"restraintsCompared\":" + RestraintsCompared);
        sb.Append(",\"worstMove\":" + WorstMove.ToString("R", CultureInfo.InvariantCulture));
        Arr(sb, "settings", Settings); Arr(sb, "elementsAdded", ElementsAdded); Arr(sb, "elementsRemoved", ElementsRemoved); Arr(sb, "elementsChanged", ElementsChanged);
        Arr(sb, "nodesMoved", NodesMoved); Arr(sb, "restraintsAdded", RestraintsAdded); Arr(sb, "restraintsRemoved", RestraintsRemoved);
        Arr(sb, "sifsAdded", SifsAdded); Arr(sb, "sifsRemoved", SifsRemoved);
        Arr(sb, "loadsAdded", LoadsAdded); Arr(sb, "loadsRemoved", LoadsRemoved); Arr(sb, "loadsChanged", LoadsChanged); Arr(sb, "carried", Carried);
        return sb.Append('}').ToString();
    }

    static void Arr(StringBuilder sb, string k, List<string> v) { sb.Append(",\"" + k + "\":[" + string.Join(",", v.Select(Q).ToArray()) + "]"); }

    static string Q(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (char c in s ?? "")
        {
            if (c == '"' || c == '\\') sb.Append('\\').Append(c);
            else if (c < 0x20 || c > 0x7e) sb.Append("\\u").Append(((int)c).ToString("x4"));
            else sb.Append(c);
        }
        return sb.Append('"').ToString();
    }
}

public static class CaesarCompare
{
    const double Rel = 1e-6;

    // lengthTol: a node move smaller than this (job length units) is not a difference
    public static CaesarCompareResult Compare(PipeModel a, string nameA, PipeModel b, string nameB, double lengthTol)
    {
        var r = new CaesarCompareResult { NameA = nameA ?? "A", NameB = nameB ?? "B" };
        if (lengthTol <= 0) lengthTol = 0.01;

        // settings
        if (!Same(a.Ambient, b.Ambient)) r.Settings.Add("ambient " + F(a.Ambient) + " -> " + F(b.Ambient));
        if (a.ZUp != b.ZUp) r.Settings.Add("vertical axis " + (a.ZUp ? "Z" : "Y") + " -> " + (b.ZUp ? "Z" : "Y"));
        if ((a.Title ?? "") != (b.Title ?? "")) r.Settings.Add("title \"" + a.Title + "\" -> \"" + b.Title + "\"");
        for (int i = 0; i < 22; i++)
            if (!Same(a.Factor(i), b.Factor(i))) { r.Settings.Add("units differ (factor " + i + ": " + F(a.Factor(i)) + " -> " + F(b.Factor(i)) + "): values are compared as written"); break; }

        // elements by FROM-TO (in order for repeats)
        var bByKey = new Dictionary<string, Queue<PipeModelElement>>();
        foreach (var e in b.Elements) { string k = Key(e); if (!bByKey.ContainsKey(k)) bByKey[k] = new Queue<PipeModelElement>(); bByKey[k].Enqueue(e); }
        var matched = new HashSet<PipeModelElement>();
        foreach (var ea in a.Elements)
        {
            Queue<PipeModelElement> q;
            if (!bByKey.TryGetValue(Key(ea), out q) || q.Count == 0) { r.ElementsRemoved.Add(Describe(ea)); continue; }
            var eb = q.Dequeue();
            matched.Add(eb);
            r.ElementsCompared++;
            var d = Fields(a, ea, b, eb);
            if (d.Count > 0) r.ElementsChanged.Add("element " + Key(ea) + ": " + string.Join("; ", d.ToArray()));
            string uniA = UKey(ea.Uniform), uniB = UKey(eb.Uniform), windA = WKey(ea.Wind), windB = WKey(eb.Wind);
            if (uniA != uniB) r.LoadsChanged.Add("element " + Key(ea) + ": uniform " + (uniA.Length > 0 ? uniA : "none") + " -> " + (uniB.Length > 0 ? uniB : "none"));
            if (windA != windB) r.LoadsChanged.Add("element " + Key(ea) + ": " + (windA.Length > 0 ? windA : "no wind") + " -> " + (windB.Length > 0 ? windB : "no wind"));
            var ca = Carried(a, ea); var cb = Carried(b, eb);
            foreach (var s in ca.Keys.Union(cb.Keys).OrderBy(x => x, StringComparer.Ordinal))
            {
                PipeModelBlock ba, bb;
                bool ha = ca.TryGetValue(s, out ba), hb = cb.TryGetValue(s, out bb);
                if (ha && !hb) r.Carried.Add("element " + Key(ea) + ": " + s + " block only in " + r.NameA);
                else if (!ha && hb) r.Carried.Add("element " + Key(ea) + ": " + s + " block only in " + r.NameB);
                else if (!ba.Lines.SequenceEqual(bb.Lines)) r.Carried.Add("element " + Key(ea) + ": " + s + " block differs");
            }
        }
        foreach (var eb in b.Elements) if (!matched.Contains(eb)) r.ElementsAdded.Add(Describe(eb));

        // restraints and SIFs as written (joints expanded), as multisets
        var ra = CaesarNeutralWriter.Restraints(a).Select(x => RKey(a, x)).ToList();
        var rb = CaesarNeutralWriter.Restraints(b).Select(x => RKey(b, x)).ToList();
        r.RestraintsCompared = Math.Max(ra.Count, rb.Count);
        Diff(ra, rb, r.RestraintsRemoved, r.RestraintsAdded);
        var sa = CaesarNeutralWriter.Sifs(a).Select(SKey).ToList();
        var sb2 = CaesarNeutralWriter.Sifs(b).Select(SKey).ToList();
        Diff(sa, sb2, r.SifsRemoved, r.SifsAdded);
        Diff(a.NodeLoads.SelectMany(LKeys).ToList(), b.NodeLoads.SelectMany(LKeys).ToList(), r.LoadsRemoved, r.LoadsAdded);

        // carried global data
        if (a.Source != null && b.Source != null && !a.Source.Seed && !b.Source.Seed)
        {
            if (!a.Source.MiscelTail.SequenceEqual(b.Source.MiscelTail) && !SameTail(a, b)) r.Carried.Add("MISCEL_1 tail (nozzles, hangers, execution options) differs");
            List<string> ua, ub;
            if (a.Source.Sections.TryGetValue("UNITS", out ua) && b.Source.Sections.TryGetValue("UNITS", out ub) && !ua.SequenceEqual(ub)) r.Carried.Add("UNITS block differs");
        }

        // node positions by CAESAR's rules
        try
        {
            var ga = CaesarGeometry.Build(CaesarNeutralReader.ParseText(CaesarNeutralWriter.ToText(a)));
            var gb = CaesarGeometry.Build(CaesarNeutralReader.ParseText(CaesarNeutralWriter.ToText(b)));
            foreach (int n in ga.RealNodeIds())
            {
                var pa = ga.Position(n);
                var pb = gb.Position(n);
                if (pa == null || pb == null) continue;
                r.NodesCompared++;
                double dist = Math.Sqrt(Sq(pa[0] - pb[0]) + Sq(pa[1] - pb[1]) + Sq(pa[2] - pb[2]));
                if (dist > r.WorstMove) r.WorstMove = dist;
                if (dist > lengthTol) r.NodesMoved.Add("node " + n + ": moved " + F(dist) + " (" + F(pb[0] - pa[0]) + ", " + F(pb[1] - pa[1]) + ", " + F(pb[2] - pa[2]) + ")");
            }
        }
        catch (Exception ex) { r.Settings.Add("node positions not compared: " + ex.Message); }
        return r;
    }

    // the blocks an element carries as written; a new model's first element its default allowables block
    static Dictionary<string, PipeModelBlock> Carried(PipeModel m, PipeModelElement e)
    {
        var def = m.Elements.Count > 0 && m.Elements[0] == e ? CaesarNeutralWriter.DefaultAllowables(m) : null;
        if (def == null) return e.Carried;
        var d = new Dictionary<string, PipeModelBlock>(e.Carried);
        d["ALLOWBLS"] = new PipeModelBlock { Seq = 1, Lines = def };
        return d;
    }

    static string RadiusText(double asWritten, double meaning)
    {
        string name = PipeModelBend.DisplayName(asWritten);
        return name != null ? name + " (" + F(meaning) + ")" : PipeModelBend.RadiusCode(asWritten) > 0 ? F(asWritten) + " (no radius)" : F(asWritten);
    }

    // the vectors of a uniform load that carry something ("" for none)
    static string UKey(PipeModelUniform u)
    {
        if (u == null) return "";
        var parts = new List<string>();
        for (int v = 0; v < 9; v++)
            if (u.Used(v)) parts.Add("U" + (v + 1) + " " + G(u.Vectors[v][0]) + "," + G(u.Vectors[v][1]) + "," + G(u.Vectors[v][2]) + (u.G[v] ? " g" : ""));
        return string.Join("; ", parts.ToArray());
    }

    static string WKey(PipeModelWind x)
    {
        if (PipeModelWind.IsNone(x)) return "";
        return PipeModelWind.TypeName(x.Type) + " " + G(x.Shape) + (x.Type == 1 ? " added mass " + G(x.AddedMass) + " lift " + G(x.Lift) + " growth " + G(x.MarineGrowth) + "/" + G(x.MarineGrowthDensity) : "");
    }

    // one key per used vector: "node 30 force F1 FX 100 FY -50" / "node 40 displacement D1 DX 0.5 DY 0"
    static IEnumerable<string> LKeys(PipeModelNodeLoad d)
    {
        var dofs = d.IsDisplacement ? PipeModelCodes.DisplacementDofs : PipeModelCodes.ForceDofs;
        for (int v = 0; v < 9; v++)
        {
            if (!d.Used(v)) continue;
            var sb = new StringBuilder("node " + d.Node + " " + d.Kind + " " + (d.IsDisplacement ? "D" : "F") + (v + 1));
            for (int k = 0; k < 6; k++)
            {
                double x = d.Vectors[v][k];
                if (double.IsNaN(x) || (!d.IsDisplacement && x == 0)) continue;
                sb.Append(" " + dofs[k] + " " + G(x));
            }
            yield return sb.ToString();
        }
    }

    // the ambient overlay aside, the tails are the same
    static bool SameTail(PipeModel a, PipeModel b)
    {
        var ta = a.Source.MiscelTail; var tb = b.Source.MiscelTail;
        if (ta.Count != tb.Count) return false;
        for (int i = 0; i < ta.Count; i++) if (ta[i] != tb[i] && i != ta.Count - 3) return false;
        return true;
    }

    static void Diff(List<string> a, List<string> b, List<string> onlyA, List<string> onlyB)
    {
        var count = new Dictionary<string, int>();
        foreach (var k in b) { int c; count.TryGetValue(k, out c); count[k] = c + 1; }
        foreach (var k in a)
        {
            int c;
            if (count.TryGetValue(k, out c) && c > 0) count[k] = c - 1;
            else onlyA.Add(k);
        }
        foreach (var kv in count) for (int i = 0; i < kv.Value; i++) onlyB.Add(kv.Key);
        onlyA.Sort(StringComparer.Ordinal);
        onlyB.Sort(StringComparer.Ordinal);
    }

    static string Key(PipeModelElement e) { return e.From.ToString(CultureInfo.InvariantCulture) + "-" + e.To.ToString(CultureInfo.InvariantCulture); }

    static string Describe(PipeModelElement e)
    {
        return "element " + Key(e) + " (" + e.Kind + ", d " + F(e.Dx) + " " + F(e.Dy) + " " + F(e.Dz) + ")";
    }

    static string RKey(PipeModel m, PipeModelRestraint x)
    {
        double kf = m.Factor(PipeModelCodes.IsRotationCode(x.Code) ? 14 : 13);
        bool rigid = x.Stiffness <= 0 || x.Stiffness >= 1e11 * kf;
        var c = x.Cosines ?? PipeModelCodes.DefaultCosines(x.Type);
        string name = PipeModelCodes.NameOf(x.Code) ?? ("code " + x.Code);
        return "node " + x.Node + " " + name + (x.CNode > 0 ? " cnode " + x.CNode : "") + (rigid ? "" : " K " + G(x.Stiffness)) +
               (x.Gap != 0 ? " gap " + G(x.Gap) : "") + (x.Mu != 0 ? " mu " + G(x.Mu) : "") +
               (x.Code == 1 ? "" : " dir " + G(c[0]) + "," + G(c[1]) + "," + G(c[2])) + (string.IsNullOrEmpty(x.Tag) ? "" : " tag " + x.Tag);
    }

    static string SKey(PipeModelSif s)
    {
        return "node " + s.Node + " type " + s.Type + (s.SifIn != 0 ? " in " + G(s.SifIn) : "") + (s.SifOut != 0 ? " out " + G(s.SifOut) : "") + (s.SurfaceNode > 0 ? " surface " + s.SurfaceNode : "");
    }

    // a bend radius by its meaning: a code CAESAR writes for a radius chosen by name, as the radius it names
    // (CAESAR writes Long back as the plain radius); a code that is no radius as 0; a plain radius as written
    static double Radius(PipeModel m, PipeModelElement e)
    {
        int code = PipeModelBend.RadiusCode(e.Bend.Radius);
        if (code == 0) return e.Bend.Radius;
        double r = PipeModelBend.ResolveCode(code, e.Od / m.Factor(20)) * m.Factor(0);
        return double.IsNaN(r) ? e.Bend.Radius : r;
    }

    static List<string> Fields(PipeModel ma, PipeModelElement a, PipeModel mb, PipeModelElement b)
    {
        var d = new List<string>();
        Num(d, "DX", a.Dx, b.Dx); Num(d, "DY", a.Dy, b.Dy); Num(d, "DZ", a.Dz, b.Dz);
        Num(d, "OD", a.Od, b.Od); Num(d, "wall", a.Wall, b.Wall); Num(d, "insulation", a.Insulation, b.Insulation); Num(d, "corrosion", a.Corrosion, b.Corrosion);
        for (int k = 0; k < 9; k++) Num(d, "T" + (k + 1), a.T[k], b.T[k]);
        for (int k = 0; k < 9; k++) Num(d, "P" + (k + 1), a.P[k], b.P[k]);
        Num(d, "E", a.Emod, b.Emod); Num(d, "Poisson", a.Poisson, b.Poisson); Num(d, "pipe density", a.PipeDensity, b.PipeDensity);
        Num(d, "insulation density", a.InsulationDensity, b.InsulationDensity); Num(d, "fluid density", a.FluidDensity, b.FluidDensity);
        if (a.Material != b.Material) d.Add("material " + a.Material + " -> " + b.Material);
        if ((a.Name ?? "") != (b.Name ?? "")) d.Add("name \"" + a.Name + "\" -> \"" + b.Name + "\"");
        if ((a.LineNumber ?? "") != (b.LineNumber ?? "")) d.Add("line \"" + a.LineNumber + "\" -> \"" + b.LineNumber + "\"");
        if ((a.Bend == null) != (b.Bend == null)) d.Add(a.Bend == null ? "bend added" : "bend removed");
        else if (a.Bend != null)
        {
            double ra = Radius(ma, a), rb = Radius(mb, b);
            if (!Same(ra, rb)) d.Add("bend radius " + RadiusText(a.Bend.Radius, ra) + " -> " + RadiusText(b.Bend.Radius, rb));
            if (a.Bend.Type != b.Bend.Type) d.Add("bend type " + a.Bend.Type + " -> " + b.Bend.Type);
            string na = string.Join(" ", a.Bend.AngleNodes.Select(x => G(x[0]) + "@" + G(x[1])).ToArray()), nb = string.Join(" ", b.Bend.AngleNodes.Select(x => G(x[0]) + "@" + G(x[1])).ToArray());
            if (na != nb) d.Add("bend nodes " + na + " -> " + nb);
        }
        if ((a.Rigid == null) != (b.Rigid == null)) d.Add(a.Rigid == null ? "rigid added" : "rigid removed");
        else if (a.Rigid != null) { Num(d, "rigid weight", a.Rigid.Weight, b.Rigid.Weight); if (a.Rigid.Type != b.Rigid.Type) d.Add("rigid type " + a.Rigid.Type + " -> " + b.Rigid.Type); }
        if ((a.ExpJoint == null) != (b.ExpJoint == null)) d.Add(a.ExpJoint == null ? "expansion joint added" : "expansion joint removed");
        else if (a.ExpJoint != null)
        {
            Num(d, "EJ axial K", a.ExpJoint.AxialStiffness, b.ExpJoint.AxialStiffness); Num(d, "EJ transverse K", a.ExpJoint.TransverseStiffness, b.ExpJoint.TransverseStiffness);
            Num(d, "EJ bending K", a.ExpJoint.BendingStiffness, b.ExpJoint.BendingStiffness); Num(d, "EJ torsion K", a.ExpJoint.TorsionStiffness, b.ExpJoint.TorsionStiffness);
            Num(d, "EJ effective ID", a.ExpJoint.EffectiveId, b.ExpJoint.EffectiveId);
        }
        if ((a.Reducer == null) != (b.Reducer == null)) d.Add(a.Reducer == null ? "reducer added" : "reducer removed");
        else if (a.Reducer != null) { Num(d, "reducer OD2", a.Reducer.Od2, b.Reducer.Od2); Num(d, "reducer wall2", a.Reducer.Wall2, b.Reducer.Wall2); }
        return d;
    }

    static void Num(List<string> d, string what, double a, double b) { if (!Same(a, b)) d.Add(what + " " + F(a) + " -> " + F(b)); }

    static bool Same(double a, double b)
    {
        if (a == b) return true;
        if (double.IsNaN(a) || double.IsNaN(b)) return double.IsNaN(a) && double.IsNaN(b);
        return Math.Abs(a - b) <= Rel * Math.Max(Math.Abs(a), Math.Abs(b)) + 1e-12;
    }

    static double Sq(double v) { return v * v; }
    static string F(double v) { return v.ToString("0.####", CultureInfo.InvariantCulture); }
    static string G(double v) { if (v == 0) v = 0; return Math.Round(v, 6).ToString("G7", CultureInfo.InvariantCulture); }
}
