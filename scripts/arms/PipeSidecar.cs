using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

// PipeSidecar  --  what a pipe model adds to the viewer's sidecar beyond the groups and supports CaesarToPluto writes:
// the viewer is the confirmation step, so it shows everything the model holds. C# 5, one global batch (prefix Pipe).
// Spec: vault/format/features-sidecar.md (loads, properties, review, model.source).
//
//   groups      hidden colour groups by temperature and pressure vector ("T1 300 F", "P1 150 lb./sq.in."), material
//               ("Mat 106"), insulation, fluid density and wind shape factor -- tick one to see where a value applies
//   properties  one item per element: CAESAR's fields (deltas, OD / wall, insulation, corrosion, T1-T9, P1-P9, material,
//               densities, bend, rigid, expansion joint, reducer, uniform loads, wind), the element's beams as members,
//               the job's unit labels once for the section
//   loads       forces / moments and imposed displacements per node, uniform loads and wind per run of elements with
//               the same values (their beams as members), every load vector the model holds
//   review      PipeReview's checks plus the caller's messages, each with its nodes, elements and beams (click to
//               locate in the viewer)
//   model.source  where the model came from: file name, size, saved time, SHA-256, kind, the model hash when given
// Element number = position in the model (CAESAR's input order) = the CaesarModel element index + 1, which is how the
// beams are numbered (CaesarToPluto: element number x 100 + piece).

public static class PipeSidecar
{
    static readonly CultureInfo CI = CultureInfo.InvariantCulture;

    // blue -> red over a sorted set of values (cold to hot, low to high)
    static readonly string[] Ramp = {
        "#3b4cc0", "#4f7fd8", "#6fa8e6", "#8fd0d8", "#a5dca0", "#c9e35a",
        "#f2e34a", "#f6be2e", "#f39221", "#e8641c", "#d13a1f", "#b40426" };

    static string RampColor(int i, int n)
    {
        if (n <= 1) return Ramp[Ramp.Length - 1];
        return Ramp[(int)Math.Round((double)i * (Ramp.Length - 1) / (n - 1))];
    }

    public static string Label(CaesarModel cm, int factor)
    {
        if (cm == null) return "";
        string s = (cm.Units.Label(factor) ?? "").Trim();
        return s;
    }

    static string Nice(double v)
    {
        if (v == 0) return "0";
        double a = Math.Abs(v);
        return a >= 0.01 && a < 1e6 ? v.ToString("0.####", CI) : v.ToString("G4", CI);
    }

    // ---- groups (hidden): where each temperature, pressure, material ... applies -----------------------------
    public static int AddGroups(FeaturesSidecar sc, PipeModel pm, CaesarModel cm, List<uint>[] elementBeams, HashSet<string> used)
    {
        if (pm == null || elementBeams == null) return 0;
        int added = 0;
        string tUnit = Label(cm, CaesarUnits.TempScale), pUnit = Label(cm, CaesarUnits.Pressure);
        Func<int, double, string, string> withUnit = (k, v, u) => u.Length > 0 ? " " + u : "";
        // vectors: T1..T9, P1..P9
        for (int pass = 0; pass < 2; pass++)
            for (int k = 0; k < 9; k++)
            {
                var byVal = new SortedDictionary<double, List<uint>>();
                for (int i = 0; i < pm.Elements.Count && i < elementBeams.Length; i++)
                {
                    double v = pass == 0 ? pm.Elements[i].T[k] : pm.Elements[i].P[k];
                    if (v == 0 || double.IsNaN(v) || elementBeams[i] == null) continue;
                    List<uint> l;
                    if (!byVal.TryGetValue(v, out l)) byVal[v] = l = new List<uint>();
                    l.AddRange(elementBeams[i]);
                }
                int j = 0;
                foreach (var kv in byVal)
                {
                    string name = (pass == 0 ? "T" : "P") + (k + 1) + " " + Nice(kv.Key) + withUnit(k, kv.Key, pass == 0 ? tUnit : pUnit);
                    var g = sc.AddGroup(Unique(used, name), RampColor(j++, byVal.Count), "beams", kv.Value,
                                        new[] { "caesar", pass == 0 ? "temperature" : "pressure", (pass == 0 ? "T" : "P") + (k + 1) });
                    g.Hidden = true;
                    added++;
                }
            }
        // material, insulation, fluid, wind
        added += ByValue(sc, pm, elementBeams, used, e => e.Material, v => "Mat " + Nice(v), "material", false);
        string thk = Label(cm, CaesarUnits.Wall), dens = Label(cm, CaesarUnits.FluidDensity);
        added += ByValue(sc, pm, elementBeams, used, e => e.Insulation, v => "Insulation " + Nice(v) + (thk.Length > 0 ? " " + thk : ""), "insulation", true);
        added += ByValue(sc, pm, elementBeams, used, e => e.FluidDensity, v => "Fluid " + Nice(v) + (dens.Length > 0 ? " " + dens : ""), "fluid", true);
        added += ByValue(sc, pm, elementBeams, used, e => !PipeModelWind.IsNone(e.Wind) ? e.Wind.Shape : 0,
                         v => "Wind shape " + Nice(v), "wind", true);
        return added;
    }

    static int ByValue(FeaturesSidecar sc, PipeModel pm, List<uint>[] elementBeams, HashSet<string> used,
                       Func<PipeModelElement, double> value, Func<double, string> name, string tag, bool ramp)
    {
        var byVal = new SortedDictionary<double, List<uint>>();
        for (int i = 0; i < pm.Elements.Count && i < elementBeams.Length; i++)
        {
            double v = value(pm.Elements[i]);
            if (v == 0 || double.IsNaN(v) || elementBeams[i] == null) continue;
            List<uint> l;
            if (!byVal.TryGetValue(v, out l)) byVal[v] = l = new List<uint>();
            l.AddRange(elementBeams[i]);
        }
        int j = 0;
        foreach (var kv in byVal)
        {
            var g = sc.AddGroup(Unique(used, name(kv.Key)), ramp ? RampColor(j, byVal.Count) : null, "beams", kv.Value, new[] { "caesar", tag });
            g.Hidden = true;
            j++;
        }
        return byVal.Count;
    }

    static string Unique(HashSet<string> used, string name)
    {
        string n = name;
        for (int k = 2; used.Contains(n); k++) n = name + " (" + k + ")";
        used.Add(n);
        return n;
    }

    // ---- the sections ---------------------------------------------------------------------------------------
    public sealed class Counts { public int Properties, Loads, Review, Errors, Warnings; }

    public static Counts AddSections(FeaturesSidecar sc, PipeModel pm, CaesarModel cm, List<uint>[] elementBeams, List<PipeReviewItem> review)
    {
        var c = new Counts();
        if (pm != null)
        {
            sc.AddSection("properties", Properties(pm, cm, elementBeams, out c.Properties));
            sc.AddSection("loads", Loads(pm, cm, elementBeams, out c.Loads));
        }
        if (review != null)
        {
            sc.AddSection("review", Review(review, elementBeams));
            c.Review = review.Count;
            c.Errors = review.Count(x => x.Level == "error");
            c.Warnings = review.Count(x => x.Level == "warning");
        }
        return c;
    }

    static string Members(List<uint>[] elementBeams, IEnumerable<int> elementNumbers)
    {
        var ids = new List<uint>();
        if (elementBeams != null)
            foreach (int en in elementNumbers)
                if (en >= 1 && en <= elementBeams.Length && elementBeams[en - 1] != null) ids.AddRange(elementBeams[en - 1]);
        return "[{\"domain\": \"beams\", \"ids\": [" + string.Join(",", ids.Select(x => x.ToString(CI)).ToArray()) + "]}]";
    }

    static string Units(params string[] kv)
    {
        var parts = new List<string>();
        for (int i = 0; i + 1 < kv.Length; i += 2) if (!string.IsNullOrEmpty(kv[i + 1])) parts.Add(Q(kv[i]) + ": " + Q(kv[i + 1]));
        return "{" + string.Join(", ", parts.ToArray()) + "}";
    }

    public static string KindOf(PipeModelElement e)
    {
        if (e.Rigid != null)
        {
            switch (e.Rigid.Type)
            {
                case 1: return "Valve";
                case 2: return "Flange";
                case 3: return "Flange pair";
                case 4: return "Flanged valve";
                default: return "Rigid";
            }
        }
        if (e.ExpJoint != null) return "Expansion joint";
        if (e.Reducer != null) return "Reducer";
        if (e.Bend != null) return "Bend";
        return "Pipe";
    }

    static string Properties(PipeModel pm, CaesarModel cm, List<uint>[] elementBeams, out int count)
    {
        var sb = new StringBuilder();
        sb.Append("{\"version\": 1, \"units\": ");
        sb.Append(Units("length", Label(cm, CaesarUnits.Length), "diameter", Label(cm, CaesarUnits.Diameter), "wall", Label(cm, CaesarUnits.Wall),
                        "temperature", Label(cm, CaesarUnits.TempScale), "pressure", Label(cm, CaesarUnits.Pressure), "modulus", Label(cm, CaesarUnits.Modulus),
                        "pipeDensity", Label(cm, CaesarUnits.PipeDensity), "insulDensity", Label(cm, CaesarUnits.InsulationDensity),
                        "fluidDensity", Label(cm, CaesarUnits.FluidDensity), "force", Label(cm, CaesarUnits.Force),
                        "uniform", Label(cm, CaesarUnits.UniformLoad), "g", Label(cm, CaesarUnits.GLoad),
                        "stiffness", Label(cm, CaesarUnits.TransStiffness), "rotStiffness", Label(cm, CaesarUnits.RotStiffness)));
        sb.Append(", \"items\": [\n");
        count = 0;
        for (int i = 0; i < pm.Elements.Count; i++)
        {
            var e = pm.Elements[i];
            if (count > 0) sb.Append(",\n");
            count++;
            var v = new List<string>();
            Action<string, double> num = (k, x) => v.Add(Q(k) + ": " + N(x));
            num("dx", e.Dx); num("dy", e.Dy); num("dz", e.Dz);
            num("length", Math.Sqrt(e.Dx * e.Dx + e.Dy * e.Dy + e.Dz * e.Dz));
            num("od", e.Od); num("wall", e.Wall);
            if (e.Insulation != 0) num("insul", e.Insulation);
            if (e.Corrosion != 0) num("corr", e.Corrosion);
            string t = Trimmed(e.T), p = Trimmed(e.P);
            if (t != null) v.Add("\"T\": " + t);
            if (p != null) v.Add("\"P\": " + p);
            if (e.Material != 0) num("material", e.Material);
            if (e.Emod != 0) num("emod", e.Emod);
            if (e.Poisson != 0) num("poisson", e.Poisson);
            if (e.PipeDensity != 0) num("pipeDensity", e.PipeDensity);
            if (e.InsulationDensity != 0) num("insulDensity", e.InsulationDensity);
            if (e.FluidDensity != 0) num("fluidDensity", e.FluidDensity);
            if (e.Bend != null)
            {
                var b = e.Bend;
                int code = PipeModelBend.RadiusCode(b.Radius);
                string named = PipeModelBend.DisplayName(b.Radius);
                var nodes = b.AngleNodes.Where(a => a.Length > 1 && a[1] > 0).Select(a => "[" + (PipeModelBend.MidPoint == a[0] || Math.Abs(a[0] - PipeModelBend.MidPoint) < 1e-3 ? "\"M\"" : N(a[0])) + ", " + N(a[1]) + "]");
                v.Add("\"bend\": {\"radius\": " + N(b.Radius) + (named != null ? ", \"named\": " + Q(named) : "") + (code > 0 ? ", \"code\": " + code : "") +
                      (PipeModelBend.NoRadiusCode(code) ? ", \"noRadius\": true" : "") + ", \"type\": " + b.Type + (b.MiterPoints > 0 ? ", \"miterPoints\": " + b.MiterPoints : "") +
                      ", \"nodes\": [" + string.Join(", ", nodes.ToArray()) + "]}");
            }
            if (e.Rigid != null) v.Add("\"rigid\": {\"weight\": " + N(e.Rigid.Weight) + ", \"type\": " + Q(KindOf(e)) + "}");
            if (e.ExpJoint != null)
            {
                var x = e.ExpJoint;
                v.Add("\"expJoint\": {\"axial\": " + N(x.AxialStiffness) + ", \"transverse\": " + N(x.TransverseStiffness) + ", \"bending\": " + N(x.BendingStiffness) +
                      ", \"torsion\": " + N(x.TorsionStiffness) + ", \"effectiveId\": " + N(x.EffectiveId) + "}");
            }
            if (e.Reducer != null) v.Add("\"reducer\": {\"od2\": " + N(e.Reducer.Od2) + ", \"wall2\": " + N(e.Reducer.Wall2) + "}");
            if (e.Uniform != null) v.Add("\"uniform\": " + UniformVectors(e.Uniform));
            if (e.Wind != null) v.Add("\"wind\": " + WindJson(e.Wind));
            sb.Append("    {\"element\": ").Append(i + 1).Append(", \"from\": ").Append(e.From).Append(", \"to\": ").Append(e.To);
            sb.Append(", \"kind\": ").Append(Q(KindOf(e)));
            if (!string.IsNullOrEmpty((e.Name ?? "").Trim())) sb.Append(", \"name\": ").Append(Q(e.Name.Trim()));
            if (!string.IsNullOrEmpty((e.LineNumber ?? "").Trim())) sb.Append(", \"line\": ").Append(Q(e.LineNumber.Trim()));
            sb.Append(", \"members\": ").Append(Members(elementBeams, new[] { i + 1 }));
            sb.Append(", \"values\": {").Append(string.Join(", ", v.ToArray())).Append("}}");
        }
        sb.Append("\n  ]}");
        return sb.ToString();
    }

    // T1..T9 without the unused tail; null when all are 0
    static string Trimmed(double[] a)
    {
        int n = a.Length;
        while (n > 0 && (a[n - 1] == 0 || double.IsNaN(a[n - 1]))) n--;
        if (n == 0) return null;
        return "[" + string.Join(", ", a.Take(n).Select(N).ToArray()) + "]";
    }

    static string UniformVectors(PipeModelUniform u)
    {
        var parts = new List<string>();
        for (int k = 0; k < 9; k++)
            if (u.Used(k)) parts.Add("{\"v\": " + (k + 1) + ", \"values\": [" + N(u.Vectors[k][0]) + ", " + N(u.Vectors[k][1]) + ", " + N(u.Vectors[k][2]) + "], \"g\": " + (u.G[k] ? "true" : "false") + "}");
        return "[" + string.Join(", ", parts.ToArray()) + "]";
    }

    static string WindJson(PipeModelWind w)
    {
        string s = "{\"type\": " + Q(PipeModelWind.TypeName(w.Type)) + ", \"shape\": " + N(w.Shape);
        if (w.Type == 1 || w.AddedMass != 0 || w.Lift != 0 || w.MarineGrowth != 0 || w.MarineGrowthDensity != 0)
            s += ", \"addedMass\": " + N(w.AddedMass) + ", \"lift\": " + N(w.Lift) + ", \"marineGrowth\": " + N(w.MarineGrowth) + ", \"marineGrowthDensity\": " + N(w.MarineGrowthDensity);
        return s + "}";
    }

    static string Loads(PipeModel pm, CaesarModel cm, List<uint>[] elementBeams, out int count)
    {
        var items = new List<string>();
        // forces / moments and imposed displacements at nodes, in node order (forces first)
        foreach (var x in pm.NodeLoads.OrderBy(l => l.IsDisplacement ? 1 : 0).ThenBy(l => l.Node))
        {
            if (x.Node <= 0) continue;
            var vecs = new List<string>();
            for (int v = 0; v < 9; v++)
                if (x.Used(v)) vecs.Add("{\"v\": " + (v + 1) + ", \"values\": [" + string.Join(", ", x.Vectors[v].Select(d => x.IsDisplacement ? N(d) : N(double.IsNaN(d) ? 0 : d)).ToArray()) + "]}");
            if (vecs.Count == 0) continue;
            string kind = x.IsDisplacement ? "displacement" : "force";
            var els = new List<int>();
            for (int i = 0; i < pm.Elements.Count; i++) if (pm.Elements[i].From == x.Node || pm.Elements[i].To == x.Node) els.Add(i + 1);
            items.Add("{\"id\": " + Q("caesar-" + kind + "-" + x.Node) + ", \"name\": " + Q((x.IsDisplacement ? "D " : "F ") + x.Node) + ", \"kind\": " + Q(kind) +
                      ", \"nodeIds\": [" + x.Node + "], \"elements\": [" + string.Join(", ", els.Select(n => n.ToString(CI)).ToArray()) + "], \"vectors\": [" +
                      string.Join(", ", vecs.ToArray()) + "], \"tags\": [\"caesar\", \"load\", " + Q(kind) + "]}");
        }
        // uniform loads and wind: runs of consecutive elements with the same values
        for (int pass = 0; pass < 2; pass++)
        {
            int i = 0, n = pm.Elements.Count;
            while (i < n)
            {
                var e = pm.Elements[i];
                bool has = pass == 0 ? !PipeModelUniform.IsNone(e.Uniform) : !PipeModelWind.IsNone(e.Wind);
                if (!has) { i++; continue; }
                int j = i + 1;
                while (j < n && (pass == 0 ? pm.Elements[j].Uniform != null && pm.Elements[j].Uniform.SameAs(e.Uniform) : pm.Elements[j].Wind != null && pm.Elements[j].Wind.SameAs(e.Wind))
                       && pm.Elements[j].From == pm.Elements[j - 1].To) j++;
                var els = Enumerable.Range(i + 1, j - i).ToList();
                var nodes = new List<int>();
                for (int k = i; k < j; k++) { if (!nodes.Contains(pm.Elements[k].From)) nodes.Add(pm.Elements[k].From); if (!nodes.Contains(pm.Elements[k].To)) nodes.Add(pm.Elements[k].To); }
                string kind = pass == 0 ? "uniform" : "wind";
                string span = e.From + "-" + pm.Elements[j - 1].To;
                string body = pass == 0 ? "\"vectors\": " + UniformVectors(e.Uniform) : "\"wind\": " + WindJson(e.Wind);
                items.Add("{\"id\": " + Q("caesar-" + kind + "-" + (i + 1)) + ", \"name\": " + Q((pass == 0 ? "U " : "Wind ") + span) + ", \"kind\": " + Q(kind) +
                          ", \"elements\": [" + string.Join(", ", els.Select(x => x.ToString(CI)).ToArray()) + "], \"nodeIds\": [" + string.Join(", ", nodes.Select(x => x.ToString(CI)).ToArray()) +
                          "], \"members\": " + Members(elementBeams, els) + ", " + body + ", \"tags\": [\"caesar\", \"load\", " + Q(kind) + "]}");
                i = j;
            }
        }
        count = items.Count;
        return "{\"version\": 1, \"units\": " +
               Units("force", Label(cm, CaesarUnits.Force), "moment", Label(cm, CaesarUnits.MomentInput), "length", Label(cm, CaesarUnits.Length),
                     "rotation", "deg", "uniform", Label(cm, CaesarUnits.UniformLoad), "g", Label(cm, CaesarUnits.GLoad).Length > 0 ? Label(cm, CaesarUnits.GLoad) : "g") +
               ", \"note\": \"load vectors as CAESAR numbers them (F1..F9, D1..D9, U1..U9); CAESAR builds the load cases from them. Wind's direction and speed are in CAESAR's load cases, not the model\"" +
               ", \"items\": [\n    " + string.Join(",\n    ", items.ToArray()) + (items.Count > 0 ? "\n  " : "") + "]}";
    }

    static string Review(List<PipeReviewItem> review, List<uint>[] elementBeams)
    {
        var order = review.Select((x, k) => new { x, k }).OrderBy(a => PipeReview.Rank(a.x.Level)).ThenBy(a => a.k).Select(a => a.x).ToList();
        var items = new List<string>();
        int id = 1;
        foreach (var r in order)
        {
            var s = new StringBuilder();
            s.Append("{\"id\": ").Append(Q("r" + id++)).Append(", \"level\": ").Append(Q(r.Level)).Append(", \"code\": ").Append(Q(r.Code)).Append(", \"text\": ").Append(Q(r.Text));
            if (r.Nodes.Count > 0) s.Append(", \"nodeIds\": [").Append(string.Join(", ", r.Nodes.Select(n => n.ToString(CI)).ToArray())).Append("]");
            if (r.Elements.Count > 0)
            {
                s.Append(", \"elements\": [").Append(string.Join(", ", r.Elements.Select(n => n.ToString(CI)).ToArray())).Append("]");
                s.Append(", \"members\": ").Append(Members(elementBeams, r.Elements));
            }
            s.Append("}");
            items.Add(s.ToString());
        }
        return "{\"version\": 1, \"summary\": " + Q(PipeReview.Summary(review)) + ", \"items\": [\n    " + string.Join(",\n    ", items.ToArray()) + (items.Count > 0 ? "\n  " : "") + "]}";
    }

    // ---- model.source: the file a model came from ------------------------------------------------------------
    public static string SourceOf(string path, string kind, string modelHash)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(path))
        {
            parts.Add("\"file\": " + Q(Path.GetFileName(path)));
            if (!string.IsNullOrEmpty(kind)) parts.Add("\"kind\": " + Q(kind));
            if (File.Exists(path))
            {
                var fi = new FileInfo(path);
                parts.Add("\"bytes\": " + fi.Length.ToString(CI));
                parts.Add("\"saved\": " + Q(fi.LastWriteTimeUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CI)));
                parts.Add("\"sha256\": " + Q(FileSha(path)));
            }
        }
        if (!string.IsNullOrEmpty(modelHash)) parts.Add("\"model\": " + Q(modelHash));
        parts.Add("\"written\": " + Q(DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CI)));
        return "{" + string.Join(", ", parts.ToArray()) + "}";
    }

    static string FileSha(string path)
    {
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var sha = SHA256.Create())
        {
            var sb = new StringBuilder();
            foreach (byte b in sha.ComputeHash(fs)) sb.Append(b.ToString("x2", CI));
            return sb.ToString();
        }
    }

    // ---- JSON ---------------------------------------------------------------------------------------------------
    static string N(double v) { return double.IsNaN(v) || double.IsInfinity(v) ? "null" : v.ToString("R", CI); }

    public static string Q(string s)
    {
        if (s == null) return "null";
        var sb = new StringBuilder("\"");
        foreach (char c in s)
        {
            if (c == '"') sb.Append("\\\"");
            else if (c == '\\') sb.Append("\\\\");
            else if (c == '\n') sb.Append("\\n");
            else if (c == '\r') sb.Append("\\r");
            else if (c == '\t') sb.Append("\\t");
            else if (c < 0x20 || c > 0x7e) sb.Append("\\u").Append(((int)c).ToString("x4"));
            else sb.Append(c);
        }
        return sb.Append('"').ToString();
    }
}
