using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

// PipeBuilder  --  a parametric pipe-stress model from a TOML config -> PipeModel (CAESAR II terms), which
// CaesarNeutralWriter turns into a neutral file (.cii) CAESAR II imports. C# 5 / PowerShell 5.1
// (scripts/caesar/Build-Pipe.ps1); usage: vault/arms/pipe-builder.md; every key is shown in
// scripts/caesar/examples/pipe-example.toml. An unknown key is an error: a typo never passes silently.
//
// Units: geometry (leg lengths, positions along a run, start points) in model.length_unit, "ft" (default)
// or "in"; diameters, walls, insulation, corrosion, bend radii and translational gaps in inches; rotational
// gaps and coupling deflections in degrees; temperatures F; pressures psi; densities lb/cu.ft; weights lb;
// stiffness lb/in and in.lb/deg. The model goes out in CAESAR's English set (PipeModel).
//
// Any number may be an expression string over [parameters] ("3*span + 2"); positions along a run may
// also use L, the run's length. Build-Pipe.ps1 -Set "span=25" overrides a parameter.
//
// Geometry: a run is a start (a point, or a position on an earlier run: a branch, with a welding tee
// unless tee = "none") and legs (a direction and a length, or a point to go to). A corner between two
// legs gets a bend (LR = 1.5 x nominal, SR = 1 x nominal, a radius, or none) in CAESAR's convention: the
// element carrying the bend runs to the corner (the tangent intersection point) and its TO node is the
// bend's far point. Positions along a run are measured along the leg lines through the corners, so a
// position AT a corner is the bend's far point, and the arc occupies corner -/+ its tangent length
// T = R tan(angle / 2), where nothing else may go. A component placed at a corner starts at the far point
// and keeps its own length. Nodes are made where something needs one: the run's ends and corners,
// supports, component ends, couplings, branch points, SIFs, tie ends; max_length adds more on straights.

public class PipeBuildResult
{
    public PipeModel Model;
    public string Source = "";
    public string Title = "";
    public readonly List<string> Warnings = new List<string>();
    public readonly List<string> Overrides = new List<string>();
    public readonly Dictionary<string, int> ElementsByKind = new Dictionary<string, int>();
    public readonly Dictionary<string, int> RestraintsByType = new Dictionary<string, int>();
    public readonly List<PipeBuildNode> NodeTable = new List<PipeBuildNode>();   // every node: where it is and why
    public int Runs, Nodes, Elements, Bends, Couplings, Ties, Restraints, RestraintNodes, Sifs, Tees;
    public double PipeLength;          // ft, along the leg lines
    public double Ambient;
    public int Material;
    public string LengthUnit = "ft";
    public double UnitInches = 12.0;

    public string Summary()
    {
        var sb = new StringBuilder();
        sb.AppendLine("PipeBuilder: " + (Title.Length > 0 ? "\"" + Title + "\", " : "") + Runs + " run(s), " + Nodes + " nodes, " + Elements + " elements, " +
                      PipeLength.ToString("0.###", CultureInfo.InvariantCulture) + " ft of pipe");
        sb.AppendLine("  elements: " + string.Join(", ", ElementsByKind.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => k.Key + " " + k.Value).ToArray()));
        sb.AppendLine("  restraints: " + Restraints + " at " + RestraintNodes + " node(s)" + (RestraintsByType.Count > 0 ? ": " +
                      string.Join(", ", RestraintsByType.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => k.Key + " " + k.Value).ToArray()) : "") +
                      (Couplings > 0 ? "; " + Couplings + " coupling(s)" : "") + (Ties > 0 ? "; " + Ties + " tie(s)" : ""));
        sb.AppendLine("  SIF / tee entries: " + Sifs + " (" + Tees + " branch tee(s)); bends: " + Bends);
        sb.AppendLine("  ambient " + Ambient.ToString("0.###", CultureInfo.InvariantCulture) + " F (thermal strain from T - ambient); material " + Material +
                      " (CAESAR database: E, density and allowables come from it)");
        if (Overrides.Count > 0) sb.AppendLine("  overrides: " + string.Join(", ", Overrides.ToArray()));
        if (Warnings.Count > 0) sb.AppendLine("  " + Warnings.Count + " warning(s)");
        return sb.ToString();
    }

    public string ToJson()
    {
        var sb = new StringBuilder("{");
        Prop(sb, "source", Source); sb.Append(",");
        Prop(sb, "title", Title); sb.Append(",");
        sb.Append("\"counts\":{\"runs\":" + Runs + ",\"nodes\":" + Nodes + ",\"elements\":" + Elements + ",\"bends\":" + Bends + ",\"restraints\":" + Restraints +
                  ",\"restraintNodes\":" + RestraintNodes + ",\"couplings\":" + Couplings + ",\"ties\":" + Ties + ",\"sifs\":" + Sifs + ",\"tees\":" + Tees + "},");
        sb.Append("\"pipeLengthFt\":" + PipeLength.ToString("R", CultureInfo.InvariantCulture) + ",");
        sb.Append("\"ambientF\":" + Ambient.ToString("R", CultureInfo.InvariantCulture) + ",\"material\":" + Material + ",");
        sb.Append("\"elementsByKind\":{" + string.Join(",", ElementsByKind.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => Q(k.Key) + ":" + k.Value).ToArray()) + "},");
        sb.Append("\"restraintsByType\":{" + string.Join(",", RestraintsByType.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => Q(k.Key) + ":" + k.Value).ToArray()) + "},");
        sb.Append("\"overrides\":[" + string.Join(",", Overrides.Select(Q).ToArray()) + "],");
        sb.Append("\"warnings\":[" + string.Join(",", Warnings.Select(Q).ToArray()) + "]}");
        return sb.ToString();
    }

    // node, run, position along it, x y z (all in the config's length unit), what is there
    public string NodesCsv()
    {
        var sb = new StringBuilder();
        string u = LengthUnit;
        sb.Append("node,run,at_" + u + ",x_" + u + ",y_" + u + ",z_" + u + ",what\r\n");
        foreach (var n in NodeTable.OrderBy(x => x.Node))
        {
            sb.Append(n.Node.ToString(CultureInfo.InvariantCulture)).Append(',').Append(Csv(n.Run)).Append(',')
              .Append(double.IsNaN(n.At) ? "" : F(n.At / UnitInches)).Append(',')
              .Append(F(n.X / UnitInches)).Append(',').Append(F(n.Y / UnitInches)).Append(',').Append(F(n.Z / UnitInches)).Append(',')
              .Append(Csv(n.What)).Append("\r\n");
        }
        return sb.ToString();
    }

    static string F(double v) { return v.ToString("0.####", CultureInfo.InvariantCulture); }
    static string Csv(string s) { s = s ?? ""; return s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s; }
    static void Prop(StringBuilder sb, string k, string v) { sb.Append(Q(k)).Append(":").Append(Q(v)); }
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

public class PipeBuildNode
{
    public int Node;
    public string Run = "";
    public double At = double.NaN;     // in, along the run's leg lines (NaN: a bend's own node)
    public double X, Y, Z;             // in, where the node really is (a corner's node: the bend's far point)
    public string What = "";
}

public static class PipeBuilder
{
    public static PipeBuildResult Build(string tomlPath, string overrides)
    {
        if (!File.Exists(tomlPath)) throw new FileNotFoundException("PipeBuilder: config not found: " + tomlPath);
        return BuildText(File.ReadAllText(tomlPath), tomlPath, overrides);
    }

    public static PipeBuildResult BuildText(string toml, string source, string overrides)
    {
        var b = new Builder(TomlReader.Parse(toml, Path.GetFileName(source ?? "")), source ?? "", overrides);
        return b.Run();
    }

    // nominal pipe sizes (ASME B36.10M): OD and walls by schedule, in inches
    static readonly double[] NpsList = { 0.5, 0.75, 1, 1.5, 2, 2.5, 3, 4, 5, 6, 8, 10, 12, 14, 16, 18, 20, 24 };
    static readonly double[] OdList = { 0.840, 1.050, 1.315, 1.900, 2.375, 2.875, 3.500, 4.500, 5.563, 6.625, 8.625, 10.750, 12.750, 14, 16, 18, 20, 24 };
    static readonly Dictionary<string, double[]> Walls = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase)
    {
        { "40",  new[] { 0.109, 0.113, 0.133, 0.145, 0.154, 0.203, 0.216, 0.237, 0.258, 0.280, 0.322, 0.365, 0.406, 0.438, 0.500, 0.562, 0.594, 0.688 } },
        { "STD", new[] { 0.109, 0.113, 0.133, 0.145, 0.154, 0.203, 0.216, 0.237, 0.258, 0.280, 0.322, 0.365, 0.375, 0.375, 0.375, 0.375, 0.375, 0.375 } },
        { "80",  new[] { 0.147, 0.154, 0.179, 0.200, 0.218, 0.276, 0.300, 0.337, 0.375, 0.432, 0.500, 0.594, 0.688, 0.750, 0.844, 0.938, 1.031, 1.219 } },
        { "XS",  new[] { 0.147, 0.154, 0.179, 0.200, 0.218, 0.276, 0.300, 0.337, 0.375, 0.432, 0.500, 0.500, 0.500, 0.500, 0.500, 0.500, 0.500, 0.500 } },
        { "160", new[] { 0.188, 0.219, 0.250, 0.281, 0.344, 0.375, 0.438, 0.531, 0.625, 0.719, 0.906, 1.125, 1.312, 1.406, 1.594, 1.781, 1.969, 2.344 } }
    };

    public static bool PipeSize(double nps, string schedule, out double od, out double wall)
    {
        od = 0; wall = 0;
        int k = Array.FindIndex(NpsList, v => Math.Abs(v - nps) < 1e-9);
        if (k < 0) return false;
        od = OdList[k];
        if (schedule == null) return true;
        string key = schedule.Trim().ToUpperInvariant().Replace("SCH", "").Trim();
        double[] w;
        if (!Walls.TryGetValue(key, out w)) return false;
        wall = w[k];
        return true;
    }

    // nominal size from an OD (for the bend radius of a section given by OD); NaN when not a standard OD
    public static double NominalOf(double od)
    {
        for (int k = 0; k < OdList.Length; k++) if (Math.Abs(OdList[k] - od) < 0.01) return NpsList[k];
        return od >= 14 && Math.Abs(od - Math.Round(od)) < 1e-9 ? od : double.NaN;
    }

    // ---------------------------------------------------------------- the builder

    sealed class Sec
    {
        public string Name;
        public double Od, Wall, Nps = double.NaN, Insulation, InsulationDensity, FluidDensity, Corrosion;   // in; densities lb/cu.in.
        public int Material;
    }

    sealed class Leg
    {
        public double[] Dir;      // unit vector
        public double Length;     // in
        public object Bend;       // the bend at the END of this leg (null: the run's, [defaults], LR)
    }

    sealed class Station
    {
        public double S;          // in, along the leg lines
        public int Node;
        public int Node2;         // a coupling: the downstream node (N + 1)
        public int Corner = -1;   // corner index when the station is a corner
        public readonly List<string> Why = new List<string>();
    }

    sealed class Span
    {
        public double A, B, Len;  // in: B = A + Len, or A + tangent + Len from a bend's far point
        public string Type;       // valve flange flange_pair flanged_valve rigid reducer expansion_joint
        public TomlTable Item;
        public string Name = "";
        public string Where = "";
    }

    sealed class CouplingAt
    {
        public double S;          // in
        public TomlTable Item;
        public string Where = "";
    }

    sealed class RunB
    {
        public string Name;
        public TomlTable Item;
        public string Where;
        public double[] Start;    // in
        public string FromRun;    // a branch: the run it starts on, and where (in)
        public double FromS;
        public bool IsBranch;
        public string Tee = "none";
        public Sec Section;
        public readonly List<Leg> Legs = new List<Leg>();
        public readonly List<double[]> Points = new List<double[]>();   // start, corners, end (in)
        public readonly List<double> CornerS = new List<double>();      // s of each corner (in)
        public readonly List<double> Theta = new List<double>();        // turn per corner (rad)
        public readonly List<double> BendR = new List<double>();        // radius per corner (in), 0 = no bend
        public readonly List<double> BendT = new List<double>();        // tangent length per corner (in)
        public double L;          // in
        public readonly SortedDictionary<double, Station> Stations = new SortedDictionary<double, Station>();
        public readonly List<Span> Spans = new List<Span>();
        public readonly List<CouplingAt> Couplings = new List<CouplingAt>();
        public readonly Dictionary<double, int> NodeAt = new Dictionary<double, int>();   // s (rounded) -> node
    }

    sealed class Builder
    {
        readonly TomlTable root;
        readonly string source;
        readonly PipeParameters par = new PipeParameters();
        readonly PipeBuildResult res = new PipeBuildResult();
        readonly PipeModel m = new PipeModel();
        readonly Dictionary<string, Sec> sections = new Dictionary<string, Sec>(StringComparer.OrdinalIgnoreCase);
        readonly List<RunB> runs = new List<RunB>();
        readonly Dictionary<string, RunB> runByName = new Dictionary<string, RunB>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<int> used = new HashSet<int>();
        TomlTable defaults;
        double lenUnit = 12.0;        // inches per geometry unit
        string lenName = "ft";
        int step = 10;
        int material;
        string bendNodesDefault = "mid";

        static readonly string[] SpanTypes = { "valve", "flange", "flange_pair", "flanged_valve", "rigid", "reducer", "expansion_joint" };

        public Builder(TomlTable r, string src, string overrides)
        {
            root = r; source = src;
            var p = Tbl(root, "parameters", "[parameters]");
            if (p != null) foreach (string k in p.Order) par.Define(k, p[k]);
            try { par.Override(overrides); }
            catch (FormatException ex) { throw Err(ex.Message); }
            res.Overrides.AddRange(par.Overridden);
        }

        // ---------------- values

        double Num(object o, string where)
        {
            try { return par.Value(o); }
            catch (FormatException ex) { throw Err(where + ": " + ex.Message); }
        }

        // a position along a run: L is the run's length (in the length unit) unless [parameters] defines L
        double NumL(object o, string where, double runLenUnits)
        {
            var s = o as string;
            if (s == null) return Num(o, where);
            try { return PipeExpr.Eval(s, n => n == "L" && !par.Has("L") ? runLenUnits : par.Get(n)); }
            catch (FormatException ex) { throw Err(where + ": " + ex.Message); }
        }

        static object Get(TomlTable t, string key) { object v; return t != null && t.TryGetValue(key, out v) ? v : null; }

        TomlTable Tbl(TomlTable t, string key, string where)
        {
            object v = Get(t, key);
            if (v == null) return null;
            var tt = v as TomlTable;
            if (tt == null) throw Err(where + " must be a table");
            return tt;
        }

        List<object> Arr(TomlTable t, string key, string where)
        {
            object v = Get(t, key);
            if (v == null) return null;
            var a = v as List<object>;
            if (a == null) throw Err(where + " must be an array");
            return a;
        }

        // a run's value, else [defaults], else null
        object RunVal(RunB r, string key) { return Get(r.Item, key) ?? Get(defaults, key); }

        double NumOpt(TomlTable t, string key, double dflt, string where) { object v = Get(t, key); return v == null ? dflt : Num(v, where + "." + key); }

        string Str(TomlTable t, string key, string dflt, string where)
        {
            object v = Get(t, key);
            if (v == null) return dflt;
            var s = v as string;
            if (s == null) throw Err(where + "." + key + " must be a string");
            return s;
        }

        // every key of a table must be one the builder reads
        void Allowed(TomlTable t, string where, string keys)
        {
            if (t == null) return;
            var ok = keys.Split(' ');
            foreach (string k in t.Order)
                if (Array.IndexOf(ok, k) < 0) throw Err(where + ": unknown key '" + k + "' (allowed: " + keys.Replace(" ", ", ") + ")");
        }

        InvalidOperationException Err(string msg) { return new InvalidOperationException("PipeBuilder (" + Path.GetFileName(source) + "): " + msg); }

        void Warn(string msg) { if (!res.Warnings.Contains(msg)) res.Warnings.Add(msg); }

        string U(double inches) { return Fmt(inches / lenUnit) + " " + lenName; }

        // ---------------- run

        public PipeBuildResult Run()
        {
            foreach (string k in root.Order)
                if (k == "loads" || k == "forces" || k == "displacements" || k == "uniform" || k == "wind" || k == "hangers")
                    throw Err("[" + k + "]: loads, imposed displacements, uniform loads, wind and hangers are not written yet: their layout in a CAESAR 15.01 neutral file " +
                              "is unconfirmed (a small job exported from CAESAR with one of each confirms it); add them in CAESAR after the import for now");
            Allowed(root, "the config", "model parameters defaults sections runs components supports ties sifs");
            var model = Tbl(root, "model", "[model]") ?? new TomlTable();
            Allowed(model, "[model]", "title notes length_unit node_step material ambient y_up code allowables_from");
            defaults = Tbl(root, "defaults", "[defaults]");
            Allowed(defaults, "[defaults]", "section temperature delta_t pressure bend bend_nodes max_length");
            res.Source = source;
            res.Title = Str(model, "title", "", "model");
            m.Title = res.Title;
            m.Notes = Str(model, "notes", "built by Pluto Build-Pipe.ps1 from " + Path.GetFileName(source), "model");
            lenName = Str(model, "length_unit", "ft", "model").ToLowerInvariant();
            if (lenName == "ft") lenUnit = 12.0;
            else if (lenName == "in") lenUnit = 1.0;
            else throw Err("model.length_unit must be \"ft\" or \"in\"");
            res.LengthUnit = lenName; res.UnitInches = lenUnit;
            step = (int)Math.Round(NumOpt(model, "node_step", 10, "model"));
            if (step < 1) throw Err("model.node_step must be at least 1");
            object matV = Get(model, "material");
            if (matV == null) throw Err("model.material is required: the CAESAR material number (102 = A53 Grade B); E, density and allowables come from CAESAR's database");
            material = (int)Math.Round(Num(matV, "model.material"));
            if (material <= 0) throw Err("model.material must be a CAESAR material number");
            m.Ambient = NumOpt(model, "ambient", 70.0, "model");
            res.Ambient = m.Ambient; res.Material = material;
            object yUp = Get(model, "y_up");
            if (yUp != null && !(yUp is bool)) throw Err("model.y_up must be true or false");
            m.ZUp = yUp is bool && !(bool)yUp;
            m.Code = Str(model, "code", "B31.1", "model");
            string seed = Str(model, "allowables_from", null, "model");
            if (seed != null) m.AllowablesBlock = AllowablesFrom(seed);
            else if (!string.Equals(m.Code, "B31.1", StringComparison.OrdinalIgnoreCase))
                throw Err("model.code \"" + m.Code + "\": only B31.1 is built in; for another code set allowables_from = a .cii exported from CAESAR with that code (its allowables block is copied)");
            bendNodesDefault = BendNodes(Str(defaults, "bend_nodes", "mid", "defaults"), "[defaults]");

            ReadSections();
            ReadRuns();
            ReadComponents();
            Bends();
            CollectStations();
            CheckRuns();
            foreach (var r in runs) BuildRun(r);
            Supports();
            Ties();
            Sifs();
            Finish();
            return res;
        }

        string BendNodes(string v, string where)
        {
            string s = (v ?? "mid").Trim().ToLowerInvariant();
            if (s != "mid" && s != "near-mid" && s != "none") throw Err(where + ".bend_nodes = \"mid\", \"near-mid\" or \"none\"");
            return s;
        }

        // a seed file's first allowables block, verbatim (another piping code than the built-in B31.1)
        List<string> AllowablesFrom(string seed)
        {
            string path = seed;
            if (!Path.IsPathRooted(path) && !string.IsNullOrEmpty(source)) path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(source)) ?? "", seed);
            if (!File.Exists(path)) throw Err("allowables_from: file not found: " + path);
            var block = new List<string>();
            bool on = false;
            foreach (string l in File.ReadAllLines(path))
            {
                if (l.StartsWith("#$", StringComparison.Ordinal))
                {
                    if (on) break;
                    on = l.Substring(2).Trim().StartsWith("ALLOWBLS", StringComparison.Ordinal);
                    continue;
                }
                if (on) block.Add(l);
            }
            int nAll = CaesarNeutralReader.Read(path).NumAllowables;   // CONTROL: how many blocks the section holds
            if (block.Count == 0 || nAll < 1 || block.Count % nAll != 0) throw Err("allowables_from: no allowables block in " + path);
            int per = block.Count / nAll;
            if (per != 28) Warn("allowables_from: " + per + "-line allowables blocks in " + Path.GetFileName(path) + " (28 in CAESAR 15.01): check the CAESAR version it came from");
            return block.Take(per).ToList();
        }

        // ---------------- sections

        void ReadSections()
        {
            object sv = Get(root, "sections");
            var items = new List<KeyValuePair<string, TomlTable>>();
            if (sv is TomlTable)
            {
                var t = (TomlTable)sv;
                foreach (string k in t.Order)
                {
                    var it = t[k] as TomlTable;
                    if (it == null) throw Err("sections." + k + " must be a table ([sections." + k + "])");
                    items.Add(new KeyValuePair<string, TomlTable>(k, it));
                }
            }
            else if (sv is List<object>)
            {
                foreach (object o in (List<object>)sv)
                {
                    var it = o as TomlTable;
                    if (it == null) throw Err("[[sections]] items must be tables");
                    items.Add(new KeyValuePair<string, TomlTable>(Str(it, "name", null, "[[sections]]"), it));
                }
            }
            else if (sv != null) throw Err("sections must be [sections.NAME] tables or [[sections]] items");
            if (items.Count == 0) throw Err("no [sections]: define at least one pipe size");
            foreach (var kv in items)
            {
                string name = kv.Key;
                if (string.IsNullOrEmpty(name)) throw Err("a [[sections]] item without a name");
                if (sections.ContainsKey(name)) throw Err("two sections named \"" + name + "\"");
                var t = kv.Value;
                string w = "sections." + name;
                Allowed(t, w, "name nps schedule od wall insulation insulation_density fluid_density fluid_sg corrosion material");
                var s = new Sec { Name = name };
                object nps = Get(t, "nps"), od = Get(t, "od"), wall = Get(t, "wall");
                string sch = Str(t, "schedule", null, w);
                if (sch != null && wall != null) throw Err(w + ": give schedule or wall, not both");
                if (nps != null)
                {
                    s.Nps = Num(nps, w + ".nps");
                    double o2, w2;
                    if (!PipeSize(s.Nps, sch, out o2, out w2))
                        throw Err(w + ": NPS " + Fmt(s.Nps) + (sch != null ? " schedule " + sch : "") + " is not in the built-in table (NPS 1/2 - 24; STD, XS, 40, 80, 160): give od and wall");
                    s.Od = o2; s.Wall = w2;
                }
                else if (sch != null) throw Err(w + ": a schedule needs nps");
                if (od != null) s.Od = Num(od, w + ".od");
                if (wall != null) s.Wall = Num(wall, w + ".wall");
                if (!(s.Od > 0)) throw Err(w + ": od (or nps) is required");
                if (!(s.Wall > 0)) throw Err(w + ": wall (or nps with a schedule) is required");
                if (s.Wall * 2 >= s.Od) throw Err(w + ": the wall is at least half the OD");
                if (double.IsNaN(s.Nps)) s.Nps = NominalOf(s.Od);
                s.Insulation = NumOpt(t, "insulation", 0, w);
                s.InsulationDensity = NumOpt(t, "insulation_density", 0, w) / 1728.0;
                s.FluidDensity = Fluid(t, w, 0);
                s.Corrosion = NumOpt(t, "corrosion", 0, w);
                s.Material = (int)Math.Round(NumOpt(t, "material", material, w));
                if (s.Insulation < 0 || s.InsulationDensity < 0 || s.FluidDensity < 0 || s.Corrosion < 0) throw Err(w + ": a negative insulation, density or corrosion");
                if (s.Corrosion >= s.Wall) throw Err(w + ": the corrosion allowance is not less than the wall");
                if (s.Insulation > 0 && !(s.InsulationDensity > 0)) Warn(w + ": insulation without insulation_density (lb/cu.ft): it adds no weight");
                sections[name] = s;
            }
        }

        // fluid_density (lb/cu.ft) or fluid_sg -> lb/cu.in.
        double Fluid(TomlTable t, string where, double dflt)
        {
            object d = Get(t, "fluid_density"), sg = Get(t, "fluid_sg");
            if (d != null && sg != null) throw Err(where + ": give fluid_density or fluid_sg, not both");
            if (d != null) return Num(d, where + ".fluid_density") / 1728.0;
            if (sg != null) return Num(sg, where + ".fluid_sg") * 62.4 / 1728.0;
            return dflt;
        }

        Sec SectionNamed(object v, string where)
        {
            var name = v as string;
            if (name == null) throw Err(where + ": no section (set section on the run or in [defaults])");
            Sec sec;
            if (!sections.TryGetValue(name, out sec)) throw Err(where + ": unknown section \"" + name + "\" (defined: " + string.Join(", ", sections.Keys.ToArray()) + ")");
            return sec;
        }

        // ---------------- runs: start, legs, corners

        void ReadRuns()
        {
            var list = Arr(root, "runs", "runs");
            if (list == null || list.Count == 0) throw Err("no [[runs]]");
            for (int i = 0; i < list.Count; i++)
            {
                var t = list[i] as TomlTable;
                if (t == null) throw Err("[[runs]] items must be tables");
                var r = new RunB { Item = t, Name = Str(t, "name", "run" + (i + 1).ToString(CultureInfo.InvariantCulture), "[[runs]]") };
                string w = "run \"" + r.Name + "\"";
                r.Where = w;
                Allowed(t, w, "name start from tee legs section temperature delta_t pressure bend bend_nodes max_length line fluid_density fluid_sg first_node");
                if (runByName.ContainsKey(r.Name)) throw Err("two runs named \"" + r.Name + "\"");
                r.Section = SectionNamed(RunVal(r, "section"), w);
                object from = Get(t, "from");
                if (from != null)
                {
                    var ft = from as TomlTable;
                    if (ft == null) throw Err(w + ": from = { run = \"name\", at = position } expected");
                    Allowed(ft, w + ".from", "run at");
                    if (Get(t, "start") != null) throw Err(w + ": give start or from, not both");
                    r.IsBranch = true;
                    r.FromRun = Str(ft, "run", null, w + ".from");
                    RunB src;
                    if (r.FromRun == null || !runByName.TryGetValue(r.FromRun, out src)) throw Err(w + ": from.run must name an earlier run");
                    object at = Get(ft, "at");
                    if (at == null) throw Err(w + ": from needs at (the position on run \"" + src.Name + "\")");
                    r.FromS = Math.Round(NumL(at, w + ".from.at", src.L / lenUnit) * lenUnit, 6);
                    if (r.FromS < -1e-6 || r.FromS > src.L + 1e-6) throw Err(w + ": from.at is outside run \"" + src.Name + "\" (0 .. " + U(src.L) + ")");
                    r.FromS = Math.Max(0, Math.Min(src.L, r.FromS));
                    r.Start = PointAt(src, r.FromS);
                    bool atEnd = r.FromS < 1e-6 || r.FromS > src.L - 1e-6;
                    r.Tee = Str(t, "tee", atEnd ? "none" : "welding", w).Trim().ToLowerInvariant();
                    if (r.Tee != "welding" && r.Tee != "none") throw Err(w + ": tee = \"welding\" or \"none\" (another tee type: a [[sifs]] entry with sif_in / sif_out)");
                }
                else
                {
                    if (Get(t, "tee") != null) throw Err(w + ": tee applies to a branch (from = {...})");
                    var st = Arr(t, "start", w + ".start");
                    if (st == null) { if (i > 0) throw Err(w + ": start = [x, y, z] (or from = { run, at }) is required"); r.Start = new double[3]; }
                    else r.Start = Vec(st, w + ".start", lenUnit);
                }
                runByName[r.Name] = r;
                runs.Add(r);
                Legs(r);
            }
        }

        void Legs(RunB r)
        {
            string w = r.Where;
            var legs = Arr(r.Item, "legs", w + ".legs");
            if (legs == null || legs.Count == 0) throw Err(w + ": legs = [...] is required");
            double[] at = (double[])r.Start.Clone();
            r.Points.Add((double[])at.Clone());
            for (int k = 0; k < legs.Count; k++)
            {
                string lw = w + " leg " + (k + 1).ToString(CultureInfo.InvariantCulture);
                var leg = new Leg();
                object o = legs[k];
                if (o is List<object>)
                {
                    var a = (List<object>)o;
                    if (a.Count != 2) throw Err(lw + ": [direction, length] expected");
                    leg.Dir = Direction(a[0], lw);
                    leg.Length = Num(a[1], lw + " length") * lenUnit;
                }
                else if (o is TomlTable)
                {
                    var lt = (TomlTable)o;
                    Allowed(lt, lw, "dir length to by bend");
                    object to = Get(lt, "to"), by = Get(lt, "by"), dir = Get(lt, "dir"), len = Get(lt, "length");
                    int given = (to != null ? 1 : 0) + (by != null ? 1 : 0) + (dir != null || len != null ? 1 : 0);
                    if (given != 1) throw Err(lw + ": one of { dir, length }, { to = [x, y, z] } or { by = [dx, dy, dz] }");
                    double[] vec = null;
                    if (to != null) { var tp = Vec(to as List<object>, lw + ".to", lenUnit); vec = Sub(tp, at); }
                    else if (by != null) vec = Vec(by as List<object>, lw + ".by", lenUnit);
                    else
                    {
                        if (dir == null || len == null) throw Err(lw + ": dir and length go together");
                        leg.Dir = Direction(dir, lw);
                        leg.Length = Num(len, lw + ".length") * lenUnit;
                    }
                    if (vec != null)
                    {
                        double l = Norm(vec);
                        if (!(l > 1e-9)) throw Err(lw + ": zero length");
                        leg.Dir = Scale(vec, 1 / l);
                        leg.Length = l;
                    }
                    leg.Bend = Get(lt, "bend");
                }
                else throw Err(lw + ": [direction, length] or a table expected");
                if (!(leg.Length > 1e-9)) throw Err(lw + ": the length must be positive");
                if (k > 0 && Dot(leg.Dir, r.Legs[k - 1].Dir) < -1 + 1e-9) throw Err(lw + ": turns straight back on the previous leg");
                if (k == legs.Count - 1 && leg.Bend != null) throw Err(lw + ": bend on the last leg (a bend belongs to the corner at the END of a leg)");
                r.Legs.Add(leg);
                at = Add(at, leg.Dir, leg.Length);
                r.Points.Add((double[])at.Clone());
            }
            double s = 0;
            for (int k = 0; k < r.Legs.Count; k++)
            {
                s += r.Legs[k].Length;
                if (k == r.Legs.Count - 1) break;
                r.CornerS.Add(Math.Round(s, 6));
                r.Theta.Add(Math.Acos(Math.Max(-1, Math.Min(1, Dot(r.Legs[k].Dir, r.Legs[k + 1].Dir)))));
            }
            r.L = Math.Round(s, 6);
        }

        double[] Direction(object o, string w)
        {
            var s = o as string;
            if (s != null)
            {
                string d = s.Trim().ToUpperInvariant();
                double sign = 1;
                if (d.StartsWith("+", StringComparison.Ordinal)) d = d.Substring(1);
                else if (d.StartsWith("-", StringComparison.Ordinal)) { sign = -1; d = d.Substring(1); }
                if (d == "X") return new[] { sign, 0.0, 0.0 };
                if (d == "Y") return new[] { 0.0, sign, 0.0 };
                if (d == "Z") return new[] { 0.0, 0.0, sign };
                throw Err(w + ": direction \"" + s + "\" (use +X -X +Y -Y +Z -Z or [x, y, z])");
            }
            var v = Vec(o as List<object>, w + " direction", 1.0);
            double l = Norm(v);
            if (!(l > 1e-12)) throw Err(w + ": zero direction");
            return Scale(v, 1 / l);
        }

        double[] Vec(List<object> a, string w, double scale)
        {
            if (a == null || a.Count != 3) throw Err(w + ": [x, y, z] expected");
            return new[] { Num(a[0], w) * scale, Num(a[1], w) * scale, Num(a[2], w) * scale };
        }

        // point on the leg lines at s (in): a corner's point is the tangent intersection
        static double[] PointAt(RunB r, double s)
        {
            double acc = 0;
            for (int k = 0; k < r.Legs.Count; k++)
            {
                if (s <= acc + r.Legs[k].Length + 1e-9 || k == r.Legs.Count - 1) return Add(r.Points[k], r.Legs[k].Dir, s - acc);
                acc += r.Legs[k].Length;
            }
            return (double[])r.Start.Clone();
        }

        // the leg a position lies on (a corner belongs to the leg it ends)
        static int LegOf(RunB r, double s)
        {
            double acc = 0;
            for (int k = 0; k < r.Legs.Count; k++) { acc += r.Legs[k].Length; if (s < acc - 1e-6) return k; }
            return r.Legs.Count - 1;
        }

        int CornerAt(RunB r, double s)
        {
            for (int k = 0; k < r.CornerS.Count; k++) if (Math.Abs(r.CornerS[k] - s) < 1e-6) return k;
            return -1;
        }

        // ---------------- components (spans and couplings)

        RunB RunRef(TomlTable t, string w)
        {
            string rn = Str(t, "run", null, w);
            if (rn == null) throw Err(w + ": node = n, or run = \"name\" with at / every");
            RunB r;
            if (!runByName.TryGetValue(rn, out r)) throw Err(w + ": unknown run \"" + rn + "\"");
            return r;
        }

        List<TomlTable> Items(string key)
        {
            var outT = new List<TomlTable>();
            var a = Arr(root, key, key);
            if (a == null) return outT;
            foreach (object o in a) { var t = o as TomlTable; if (t == null) throw Err("[[" + key + "]] items must be tables"); outT.Add(t); }
            return outT;
        }

        void ReadComponents()
        {
            int i = 0;
            foreach (var t in Items("components"))
            {
                i++;
                string type = Str(t, "type", "", "[[components]] " + i).Trim().ToLowerInvariant();
                string w0 = "[[components]] " + i + " (" + (type.Length > 0 ? type : "no type") + ")";
                var r = RunRef(t, w0);
                object atV = Get(t, "at");
                if (atV == null) throw Err(w0 + ": at is required");
                double a = Math.Round(NumL(atV, w0 + ".at", r.L / lenUnit) * lenUnit, 6);
                string w = "[[components]] " + type + " on run \"" + r.Name + "\" at " + U(a);
                if (type == "coupling")
                {
                    Allowed(t, w, "type run at name axial_gap axial_stiffness deflection rotational_stiffness sif");
                    if (a <= 1e-6 || a >= r.L - 1e-6) throw Err(w + ": a coupling must lie inside the run, not at an end");
                    r.Couplings.Add(new CouplingAt { S = a, Item = t, Where = w });
                    continue;
                }
                if (Array.IndexOf(SpanTypes, type) < 0) throw Err(w0 + ": type must be valve, flange, flange_pair, flanged_valve, rigid, reducer, expansion_joint or coupling");
                string keys = "type run at length name";
                if (type == "reducer") keys += " to_section";
                else if (type == "expansion_joint") keys += " axial_stiffness transverse_stiffness bending_stiffness torsion_stiffness effective_id";
                else keys += " weight";
                Allowed(t, w, keys);
                object lenV = Get(t, "length");
                if (lenV == null) throw Err(w + ": length is required (" + lenName + ")");
                double len = Num(lenV, w + ".length") * lenUnit;
                if (!(len > 1e-6)) throw Err(w + ": length must be positive" + (type == "expansion_joint" ? " (the bellows' length; a zero-length joint: a coupling with stiffnesses)" : ""));
                if (a < -1e-6 || a > r.L + 1e-6) throw Err(w + ": outside the run (0 .. " + U(r.L) + ")");
                r.Spans.Add(new Span { A = a, Len = len, Type = type, Item = t, Name = Str(t, "name", "", w), Where = w });
            }
        }

        // ---------------- bends: radius from the section in effect at the corner (after any reducer upstream)

        void Bends()
        {
            foreach (var r in runs)
            {
                var reducers = r.Spans.Where(x => x.Type == "reducer").OrderBy(x => x.A).ToList();
                for (int k = 0; k < r.CornerS.Count; k++)
                {
                    string w = r.Where + " corner " + (k + 1);
                    double c = r.CornerS[k], theta = r.Theta[k];
                    if (theta < 1e-6)
                    {
                        if (r.Legs[k].Bend != null) throw Err(w + ": a bend between legs in the same direction");
                        r.BendR.Add(0); r.BendT.Add(0);
                        Warn(r.Where + ": legs " + (k + 1) + " and " + (k + 2) + " run on in the same direction (a node there, no bend)");
                        continue;
                    }
                    Sec sec = r.Section;
                    foreach (var red in reducers) if (red.A < c - 1e-6) sec = ReducerTarget(red);
                    double rad = BendRadius(r, r.Legs[k].Bend, sec, w);
                    if (rad > 0 && theta > Math.PI * 179.0 / 180.0) throw Err(w + ": a bend of " + Fmt(theta * 180 / Math.PI) + " deg (CAESAR takes up to 179)");
                    r.BendR.Add(rad);
                    r.BendT.Add(rad > 0 ? rad * Math.Tan(theta / 2) : 0);
                }
                foreach (var sp in r.Spans)
                {
                    int k = CornerAt(r, sp.A);
                    sp.B = Math.Round(sp.A + (k >= 0 ? r.BendT[k] : 0) + sp.Len, 6);
                    if (sp.B > r.L + 1e-6) throw Err(sp.Where + ": ends at " + U(sp.B) + ", past the end of the run (" + U(r.L) + ")" +
                                                      (k >= 0 && r.BendT[k] > 0 ? " (it starts at the bend's far point, " + Fmt(r.BendT[k]) + " in past the corner)" : ""));
                }
            }
        }

        Sec ReducerTarget(Span sp)
        {
            string to = Str(sp.Item, "to_section", null, sp.Where);
            if (to == null) throw Err(sp.Where + ": to_section (the section after the reducer) is required");
            return SectionNamed(to, sp.Where + ".to_section");
        }

        double BendRadius(RunB r, object legBend, Sec sec, string w)
        {
            object b = legBend ?? RunVal(r, "bend") ?? "LR";
            var s = b as string;
            if (s != null)
            {
                string k = s.Trim().ToUpperInvariant();
                if (k == "NONE") return 0;
                if (k == "LR" || k == "SR")
                {
                    double nom = sec.Nps;
                    if (double.IsNaN(nom)) { nom = sec.Od; Warn(w + ": OD " + Fmt(sec.Od) + " in is not a standard size: the " + k + " radius is taken from the OD"); }
                    return (k == "LR" ? 1.5 : 1.0) * nom;
                }
            }
            double rad = Num(b, w + ".bend");
            if (!(rad > 0)) throw Err(w + ": a bend radius must be positive (or \"LR\", \"SR\", \"none\")");
            return rad;
        }

        // ---------------- stations: every position that needs a node

        Station St(RunB r, double s, string why)
        {
            double key = Math.Round(s, 6);
            Station st;
            if (!r.Stations.TryGetValue(key, out st))
            {
                st = new Station { S = key, Corner = CornerAt(r, key) };
                r.Stations[key] = st;
            }
            if (!st.Why.Contains(why)) st.Why.Add(why);
            return st;
        }

        // inside a bend's arc (not at its corner): the corner index, else -1
        int InBend(RunB r, double s)
        {
            for (int k = 0; k < r.CornerS.Count; k++)
                if (r.BendT[k] > 0 && Math.Abs(s - r.CornerS[k]) > 1e-6 && Math.Abs(s - r.CornerS[k]) < r.BendT[k] - 1e-6) return k;
            return -1;
        }

        Span InSpan(RunB r, double s)
        {
            foreach (var sp in r.Spans) if (s > sp.A + 1e-6 && s < sp.B - 1e-6) return sp;
            return null;
        }

        // positions (in) on a run: at = x | [x, ...], or every = d (from = d, to = L); positions from every that
        // fall in a bend's arc or inside a component are skipped (with a warning), explicit ones are checked later
        List<double> Positions(RunB r, TomlTable t, string w)
        {
            var outS = new List<double>();
            double Lu = r.L / lenUnit;
            object at = Get(t, "at"), every = Get(t, "every");
            if (at != null && every != null) throw Err(w + ": give at or every, not both");
            if ((Get(t, "from") != null || Get(t, "to") != null) && every == null) throw Err(w + ": from / to go with every");
            if (at is List<object>) foreach (object o in (List<object>)at) outS.Add(NumL(o, w + ".at", Lu) * lenUnit);
            else if (at != null) outS.Add(NumL(at, w + ".at", Lu) * lenUnit);
            else if (every != null)
            {
                double d = NumL(every, w + ".every", Lu) * lenUnit;
                if (!(d > 1e-6)) throw Err(w + ": every must be positive");
                double from = Get(t, "from") != null ? NumL(Get(t, "from"), w + ".from", Lu) * lenUnit : d;
                double to = Get(t, "to") != null ? NumL(Get(t, "to"), w + ".to", Lu) * lenUnit : r.L;
                if ((to - from) / d > 10000) throw Err(w + ": more than 10000 positions");
                var skipped = new List<string>();
                for (int k = 0; from + k * d <= to + 1e-6; k++)
                {
                    double s = from + k * d;
                    int bk = InBend(r, s);
                    var sp = InSpan(r, s);
                    if (bk >= 0) skipped.Add(U(s) + " (bend at corner " + (bk + 1) + ")");
                    else if (sp != null) skipped.Add(U(s) + " (inside the " + sp.Type + ")");
                    else outS.Add(s);
                }
                if (skipped.Count > 0) Warn(w + ": every " + U(d) + " skips " + string.Join(", ", skipped.ToArray()));
            }
            else throw Err(w + ": at = position(s) or every = spacing is required with run");
            foreach (double s in outS)
                if (s < -1e-6 || s > r.L + 1e-6) throw Err(w + ": position " + U(s) + " is outside run \"" + r.Name + "\" (0 .. " + U(r.L) + ")");
            return outS.Select(s => Math.Round(Math.Max(0, Math.Min(r.L, s)), 6)).ToList();
        }

        void CollectStations()
        {
            foreach (var r in runs)
            {
                St(r, 0, r.IsBranch ? "branch start" : "start");
                St(r, r.L, "end");
                for (int k = 0; k < r.CornerS.Count; k++) St(r, r.CornerS[k], r.BendR[k] > 0 ? "bend" : "corner");
                foreach (var cp in r.Couplings) St(r, cp.S, "coupling");
                foreach (var sp in r.Spans) { St(r, sp.A, sp.Type + " start"); St(r, sp.B, sp.Type + " end"); }
            }
            foreach (var r in runs) if (r.IsBranch) St(runByName[r.FromRun], r.FromS, "branch \"" + r.Name + "\"");
            int i = 0;
            foreach (var t in Items("supports"))
            {
                i++;
                if (Get(t, "node") != null) continue;
                var r = RunRef(t, "[[supports]] " + i);
                foreach (double s in Positions(r, t, "[[supports]] " + i + " on run \"" + r.Name + "\"")) St(r, s, "support");
            }
            i = 0;
            foreach (var t in Items("sifs"))
            {
                i++;
                if (Get(t, "node") != null) continue;
                var r = RunRef(t, "[[sifs]] " + i);
                foreach (double s in Positions(r, t, "[[sifs]] " + i + " on run \"" + r.Name + "\"")) St(r, s, "SIF");
            }
            i = 0;
            foreach (var t in Items("ties"))
            {
                i++;
                foreach (string end in new[] { "node", "cnode" })
                {
                    var et = Get(t, end) as TomlTable;
                    if (et == null) continue;
                    string w = "[[ties]] " + i + "." + end;
                    Allowed(et, w, "run at");
                    if (Get(et, "at") is List<object>) throw Err(w + ": one position (at = x)");
                    var r = RunRef(et, w);
                    foreach (double s in Positions(r, et, w)) St(r, s, "tie");
                }
            }
            // max_length: more nodes on the straights, never inside a component
            foreach (var r in runs)
            {
                object mv = RunVal(r, "max_length");
                if (mv == null) continue;
                double maxLen = Num(mv, r.Where + ".max_length") * lenUnit;
                if (!(maxLen > 1e-6)) throw Err(r.Where + ": max_length must be positive");
                var keys = r.Stations.Keys.ToList();
                for (int q = 0; q + 1 < keys.Count; q++)
                {
                    double a = keys[q], b = keys[q + 1];
                    if (r.Spans.Any(x => a > x.A - 1e-6 && b < x.B + 1e-6)) continue;
                    int ka = CornerAt(r, a), kb = CornerAt(r, b);
                    double a2 = a + (ka >= 0 ? r.BendT[ka] : 0), b2 = b - (kb >= 0 ? r.BendT[kb] : 0);   // the straight between the arcs
                    if (b2 - a2 <= maxLen + 1e-6) continue;
                    int n = (int)Math.Ceiling((b2 - a2) / maxLen - 1e-9);
                    for (int k = 1; k < n; k++) St(r, a2 + (b2 - a2) * k / n, "max_length");
                }
            }
        }

        // ---------------- checks before numbering

        void CheckRuns()
        {
            foreach (var r in runs)
            {
                string w = r.Where;
                // straight enough for each bend: from the run's start, between corners, to the run's end
                for (int k = 0; k <= r.CornerS.Count; k++)
                {
                    double a = k == 0 ? 0 : r.CornerS[k - 1], b = k == r.CornerS.Count ? r.L : r.CornerS[k];
                    double ta = k == 0 ? 0 : r.BendT[k - 1], tb = k == r.CornerS.Count ? 0 : r.BendT[k];
                    if (b - a < ta + tb - 1e-6)
                        throw Err(w + ": leg " + (k + 1) + " is " + U(b - a) + " but its bends need " + U(ta + tb) + " of it (tangent " +
                                  (ta > 0 ? Fmt(ta) + " in at corner " + k : "") + (ta > 0 && tb > 0 ? " + " : "") + (tb > 0 ? Fmt(tb) + " in at corner " + (k + 1) : "") +
                                  "): lengthen it, or use a smaller radius (bend = \"SR\" or a radius in inches)");
                }
                foreach (var sp in r.Spans)
                {
                    for (int k = 0; k < r.CornerS.Count; k++)
                    {
                        double c = r.CornerS[k];
                        if (sp.A < c - 1e-6 && sp.B > c + 1e-6) throw Err(sp.Where + ": runs " + U(sp.A) + " .. " + U(sp.B) + ", across corner " + (k + 1) + " at " + U(c));
                        if (Math.Abs(sp.B - c) < 1e-6 && r.BendR[k] > 0)
                            throw Err(sp.Where + ": ends at corner " + (k + 1) + ", where the bend is: a component cannot carry a bend; end it at the bend's near point (" + U(c - r.BendT[k]) + ")");
                    }
                }
                foreach (var st in r.Stations.Values)
                {
                    int k = InBend(r, st.S);
                    if (k >= 0)
                        throw Err(w + ": " + string.Join(", ", st.Why.ToArray()) + " at " + U(st.S) + " is inside the bend at corner " + (k + 1) + " (its arc runs " +
                                  U(r.CornerS[k] - r.BendT[k]) + " .. " + U(r.CornerS[k] + r.BendT[k]) + "; a position at " + U(r.CornerS[k]) + " is its far point)");
                    var sp = InSpan(r, st.S);
                    if (sp != null) throw Err(w + ": " + string.Join(", ", st.Why.ToArray()) + " at " + U(st.S) + " is inside the " + sp.Type + " (" + U(sp.A) + " .. " + U(sp.B) + ")");
                    if (st.Corner >= 0 && r.BendR[st.Corner] > 0 && st.Why.Any(x => x.StartsWith("branch \"", StringComparison.Ordinal)))
                        throw Err(w + ": a branch at corner " + (st.Corner + 1) + " (the bend's far point): start the branch on a straight");
                }
                foreach (var cp in r.Couplings)
                {
                    if (CornerAt(r, cp.S) >= 0) throw Err(cp.Where + ": a coupling at a corner (its axis is ambiguous): move it onto a straight");
                    if (r.Couplings.Count(x => Math.Abs(x.S - cp.S) < 1e-6) > 1) throw Err(cp.Where + ": two couplings at one position");
                }
            }
        }

        // ---------------- one run -> nodes and elements

        int NextFree(int from)
        {
            int n = from;
            while (used.Contains(n)) n += step;
            return n;
        }

        int Claim(int n, string w)
        {
            if (n <= 0) throw Err(w + ": node numbers must be positive");
            if (used.Contains(n)) throw Err(w + ": node " + n + " is taken (node_step " + step + "; a bend's own nodes take TO - 1 and TO - 2, a coupling N + 1; set first_node to separate runs)");
            used.Add(n);
            return n;
        }

        int NextHundred()
        {
            int max = used.Count > 0 ? used.Max() : 0;
            return (max / 100 + 1) * 100;
        }

        void Note(int node, RunB r, double at, double[] p, string what)
        {
            res.NodeTable.Add(new PipeBuildNode { Node = node, Run = r.Name, At = at, X = p[0], Y = p[1], Z = p[2], What = what });
        }

        void BuildRun(RunB r)
        {
            string w = r.Where;
            var stations = r.Stations.Values.ToList();

            // node numbers: the first run from node_step, every other from the next hundred (or first_node)
            object fv = Get(r.Item, "first_node");
            int first = fv != null ? (int)Math.Round(Num(fv, w + ".first_node")) : 0;
            if (fv != null && first <= 0) throw Err(w + ": first_node must be positive");
            int next;
            if (r.IsBranch)
            {
                stations[0].Node = runByName[r.FromRun].NodeAt[r.FromS];
                next = first > 0 ? first : NextHundred();
            }
            else
            {
                next = first > 0 ? first : (runs.IndexOf(r) == 0 ? step : NextHundred());
                stations[0].Node = Claim(next, w);
                next += step;
            }
            for (int i = 1; i < stations.Count; i++)
            {
                next = NextFree(next);
                stations[i].Node = Claim(next, w);
                next += step;
            }
            foreach (var st in stations)
            {
                r.NodeAt[st.S] = st.Node;
                if (r.Couplings.Any(x => Math.Abs(x.S - st.S) < 1e-6)) st.Node2 = Claim(st.Node + 1, w + " coupling at " + U(st.S));
            }

            // positions: on the leg lines, a bend's TO node at its far point
            foreach (var st in stations)
            {
                double[] p = st.Corner >= 0 && r.BendR[st.Corner] > 0 ? BendPoint(r, st.Corner, 1) : PointAt(r, st.S);
                if (r.IsBranch && st == stations[0]) continue;   // the header's node
                m.Positions[st.Node] = p;
                Note(st.Node, r, st.S, p, string.Join("; ", st.Why.ToArray()));
                if (st.Node2 > 0) { m.Positions[st.Node2] = p; Note(st.Node2, r, st.S, p, "coupling, downstream side"); }
            }
            if (!r.IsBranch) m.Coords.Add(new PipeModelCoord { Node = stations[0].Node, X = r.Start[0], Y = r.Start[1], Z = r.Start[2] });
            else if (r.Tee == "welding") { m.Sifs.Add(new PipeModelSif { Node = stations[0].Node, Type = 3 }); res.Tees++; }

            // properties along the run (a reducer changes the section downstream)
            Sec sec = r.Section;
            double[] temps = Temps(r, w), press = Press(r, w);
            double fluidRun = Get(r.Item, "fluid_density") != null || Get(r.Item, "fluid_sg") != null ? Fluid(r.Item, w, 0) : -1;
            string line = Str(r.Item, "line", "", w);
            string bendNodes = Get(r.Item, "bend_nodes") != null ? BendNodes(Str(r.Item, "bend_nodes", "mid", w), w) : bendNodesDefault;
            for (int i = 0; i + 1 < stations.Count; i++)
            {
                var a = stations[i]; var b = stations[i + 1];
                var e = new PipeModelElement();
                e.From = a.Node2 > 0 ? a.Node2 : a.Node;
                e.To = b.Node;
                // CAESAR's deltas run between points on the leg lines: from a corner, from its tangent intersection
                double[] pa = PointAt(r, a.S), pb = PointAt(r, b.S);
                e.Dx = pb[0] - pa[0]; e.Dy = pb[1] - pa[1]; e.Dz = pb[2] - pa[2];
                Props(e, sec, temps, press, fluidRun);
                if (i == 0 && line.Length > 0) e.LineNumber = line;
                if (b.Corner >= 0 && r.BendR[b.Corner] > 0)
                {
                    int k = b.Corner;
                    var bend = new PipeModelBend { Radius = r.BendR[k], FittingThickness = e.Wall };   // CAESAR writes the wall there itself
                    if (bendNodes != "none")
                    {
                        int mid = Claim(b.Node - 1, w + " bend at corner " + (k + 1));
                        bend.AngleNodes.Add(new double[] { PipeModelBend.MidPoint, mid });
                        double[] pm = BendPoint(r, k, 0.5);
                        m.Positions[mid] = pm;
                        Note(mid, r, double.NaN, pm, "bend mid point (corner " + (k + 1) + ")");
                        double straight = b.S - a.S - r.BendT[k] - (a.Corner >= 0 ? r.BendT[a.Corner] : 0);
                        if (bendNodes == "near-mid" && straight > 1e-6)   // no second node where the FROM node already is
                        {
                            int near = Claim(b.Node - 2, w + " bend at corner " + (k + 1));
                            bend.AngleNodes.Add(new double[] { 0, near });
                            double[] pn = BendPoint(r, k, 0);
                            m.Positions[near] = pn;
                            Note(near, r, double.NaN, pn, "bend near point (corner " + (k + 1) + ")");
                        }
                    }
                    e.Bend = bend;
                    e.Kind = "bend";
                    res.Bends++;
                }
                var span = r.Spans.FirstOrDefault(x => Math.Abs(x.A - a.S) < 1e-6 && Math.Abs(x.B - b.S) < 1e-6);
                if (span != null) Component(e, span, ref sec);
                m.Elements.Add(e);
                res.PipeLength += Math.Sqrt(e.Dx * e.Dx + e.Dy * e.Dy + e.Dz * e.Dz) / 12.0;
            }
            foreach (var cp in r.Couplings) MakeCoupling(r, r.Stations[Math.Round(cp.S, 6)], cp);
            res.Runs++;
        }

        // a point on the bend arc at corner k: f = 0 near point, 0.5 mid point, 1 far point
        static double[] BendPoint(RunB r, int k, double f)
        {
            double[] c = r.Points[k + 1];
            double[] d1 = r.Legs[k].Dir, d2 = r.Legs[k + 1].Dir;
            double T = r.BendT[k], R = r.BendR[k];
            double[] near = Add(c, d1, -T), far = Add(c, d2, T);
            if (f <= 0) return near;
            if (f >= 1) return far;
            double[] inward = Sub(d2, Scale(d1, Dot(d1, d2)));   // towards the centre, square to the incoming leg
            inward = Scale(inward, 1.0 / Norm(inward));
            double[] centre = Add(near, inward, R);
            double ang = r.Theta[k] * f;
            return Add(Add(centre, inward, -R * Math.Cos(ang)), d1, R * Math.Sin(ang));
        }

        double[] Temps(RunB r, string w)
        {
            var t = new double[9];
            if (Get(r.Item, "temperature") != null && Get(r.Item, "delta_t") != null) throw Err(w + ": give temperature or delta_t, not both");
            object tv = RunVal(r, "temperature"), dv = RunVal(r, "delta_t");
            if (Get(r.Item, "temperature") != null) dv = null;
            else if (Get(r.Item, "delta_t") != null) tv = null;
            if (tv != null && dv != null) throw Err("[defaults]: give temperature or delta_t, not both");
            object src = tv ?? dv;
            if (src == null) return t;
            string key = tv != null ? ".temperature" : ".delta_t";
            var a = src as List<object> ?? new List<object> { src };
            if (a.Count > 9) throw Err(w + key + ": at most 9 (T1 .. T9)");
            for (int k = 0; k < a.Count; k++)
            {
                double v = Num(a[k], w + key + "[" + (k + 1) + "]");
                t[k] = tv != null ? v : m.Ambient + v;
                if (t[k] == 0) Warn(w + ": T" + (k + 1) + " = 0 F, which CAESAR reads as no temperature (an empty field)");
                else if (Math.Abs(t[k]) < 1) Warn(w + ": T" + (k + 1) + " = " + Fmt(t[k]) + " F: CAESAR reads a value this small as an expansion coefficient (in./in.), not degrees");
            }
            return t;
        }

        double[] Press(RunB r, string w)
        {
            var p = new double[9];
            object pv = RunVal(r, "pressure");
            if (pv == null) return p;
            var a = pv as List<object> ?? new List<object> { pv };
            if (a.Count > 9) throw Err(w + ".pressure: at most 9 (P1 .. P9)");
            for (int k = 0; k < a.Count; k++) p[k] = Num(a[k], w + ".pressure[" + (k + 1) + "]");
            return p;
        }

        static void Props(PipeModelElement e, Sec sec, double[] temps, double[] press, double fluidRun)
        {
            e.Od = sec.Od; e.Wall = sec.Wall; e.Insulation = sec.Insulation; e.Corrosion = sec.Corrosion;
            e.InsulationDensity = sec.InsulationDensity;
            e.FluidDensity = fluidRun >= 0 ? fluidRun : sec.FluidDensity;
            e.Material = sec.Material;
            System.Array.Copy(temps, e.T, 9);
            System.Array.Copy(press, e.P, 9);
        }

        void Component(PipeModelElement e, Span sp, ref Sec sec)
        {
            string w = sp.Where;
            e.Name = sp.Name;
            e.Kind = sp.Type;
            switch (sp.Type)
            {
                case "valve": case "flange": case "flange_pair": case "flanged_valve": case "rigid":
                    {
                        int code = sp.Type == "valve" ? 1 : sp.Type == "flange" ? 2 : sp.Type == "flange_pair" ? 3 : sp.Type == "flanged_valve" ? 4 : 0;
                        object wt = Get(sp.Item, "weight");
                        if (wt == null) throw Err(w + ": weight (lb) is required (0 for a weightless rigid)");
                        double weight = Num(wt, w + ".weight");
                        if (weight < 0) throw Err(w + ": a negative weight");
                        e.Rigid = new PipeModelRigid { Weight = weight, Type = code };
                        break;
                    }
                case "reducer":
                    {
                        var s2 = ReducerTarget(sp);
                        e.Reducer = new PipeModelReducer { Od2 = s2.Od, Wall2 = s2.Wall };
                        if (Math.Abs(s2.Od - sec.Od) < 1e-9) Warn(w + ": to_section \"" + s2.Name + "\" has the same OD as the pipe before it");
                        sec = s2;    // downstream of the reducer
                        break;
                    }
                case "expansion_joint":
                    {
                        var x = new PipeModelExpJoint();
                        x.AxialStiffness = NumOpt(sp.Item, "axial_stiffness", 0, w);
                        x.TransverseStiffness = NumOpt(sp.Item, "transverse_stiffness", 0, w);
                        x.BendingStiffness = NumOpt(sp.Item, "bending_stiffness", 0, w);
                        x.TorsionStiffness = NumOpt(sp.Item, "torsion_stiffness", CaesarNeutralWriter.RigidStiffness, w);
                        x.EffectiveId = NumOpt(sp.Item, "effective_id", 0, w);
                        if (!(x.AxialStiffness > 0)) throw Err(w + ": axial_stiffness (lb/in) is required");
                        if (x.TransverseStiffness < 0 || x.BendingStiffness < 0 || x.TorsionStiffness < 0 || x.EffectiveId < 0) throw Err(w + ": a negative stiffness or effective_id");
                        if (x.TransverseStiffness > 0 && x.BendingStiffness > 0)
                            Warn(w + ": both transverse_stiffness and bending_stiffness: for a joint with length CAESAR derives one from the other, so give one");
                        if (!(x.TransverseStiffness > 0) && !(x.BendingStiffness > 0))
                            Warn(w + ": neither transverse_stiffness nor bending_stiffness: check the joint in CAESAR after the import");
                        if (!(x.EffectiveId > 0)) Warn(w + ": no effective_id (in): CAESAR applies no pressure thrust");
                        e.ExpJoint = x;
                        break;
                    }
            }
        }

        // a coupling: 6 CNODE restraints N -> N+1 in the pipe's own axes: axial (gap, stiffness), two shears
        // (rigid), torsion (rigid), two bendings (deflection allowance as a rotational gap, stiffness)
        void MakeCoupling(RunB r, Station st, CouplingAt cp)
        {
            var t = cp.Item;
            string w = cp.Where;
            double[] a = r.Legs[LegOf(r, st.S)].Dir;
            double axialGap = NumOpt(t, "axial_gap", 0, w), defl = NumOpt(t, "deflection", 0, w);
            double kAx = NumOpt(t, "axial_stiffness", 0, w), kRot = NumOpt(t, "rotational_stiffness", 0, w);
            double[] p1, p2;
            Perps(a, out p1, out p2);
            m.Restraints.Add(Restraint(st.Node, "X", axialGap, kAx, 0, st.Node2, a, "", w + " (axial)"));
            m.Restraints.Add(Restraint(st.Node, "X", 0, 0, 0, st.Node2, p1, "", w + " (shear)"));
            m.Restraints.Add(Restraint(st.Node, "X", 0, 0, 0, st.Node2, p2, "", w + " (shear)"));
            m.Restraints.Add(Restraint(st.Node, "RX", 0, 0, 0, st.Node2, a, "", w + " (torsion)"));
            m.Restraints.Add(Restraint(st.Node, "RX", defl, kRot, 0, st.Node2, p1, "", w + " (bending)"));
            m.Restraints.Add(Restraint(st.Node, "RX", defl, kRot, 0, st.Node2, p2, "", w + " (bending)"));
            double sif = NumOpt(t, "sif", 0, w);
            if (sif < 0) throw Err(w + ": a negative sif");
            if (sif > 0) m.Sifs.Add(new PipeModelSif { Node = st.Node, Type = 0, SifIn = sif, SifOut = sif });
            res.Couplings++;
        }

        // two unit vectors square to a (the other two global axes when a is one)
        static void Perps(double[] a, out double[] p1, out double[] p2)
        {
            int ax = AxisOf(a);
            if (ax >= 0)
            {
                p1 = new double[3]; p1[ax == 0 ? 1 : 0] = 1;
                p2 = new double[3]; p2[ax == 2 ? 1 : 2] = 1;
                return;
            }
            double[] t = Math.Abs(a[1]) < 0.9 ? new[] { 0.0, 1.0, 0.0 } : new[] { 1.0, 0.0, 0.0 };
            p1 = Cross(a, t); p1 = Scale(p1, 1 / Norm(p1));
            p2 = Cross(a, p1); p2 = Scale(p2, 1 / Norm(p2));
        }

        // 0 / 1 / 2 when d lies along a global axis, else -1
        static int AxisOf(double[] d)
        {
            for (int k = 0; k < 3; k++) if (Math.Abs(Math.Abs(d[k]) - 1) < 1e-9) return k;
            return -1;
        }

        // one restraint; with a direction, the type says translation (X Y Z) or rotation (RX RY RZ) and the
        // direction says along / about what: a global axis gives that axis' type, any other direction cosines
        PipeModelRestraint Restraint(int node, string type, double gap, double k, double mu, int cnode, double[] dir, string tag, string w)
        {
            string t = type.Trim().ToUpperInvariant();
            int code;
            if (!PipeModelCodes.Restraint.TryGetValue(t, out code)) throw Err(w + ": restraint type \"" + type + "\" (ANC X Y Z RX RY RZ GUI LIM +X +Y +Z -X -Y -Z)");
            if (gap < 0) throw Err(w + ": a gap must not be negative");
            if (k < 0) throw Err(w + ": a stiffness must not be negative");
            if (mu < 0) throw Err(w + ": a friction coefficient must not be negative");
            var r = new PipeModelRestraint { Node = node, Type = t, Code = code, Gap = gap, Stiffness = k, Mu = mu, CNode = cnode, Tag = tag ?? "", Source = w };
            if (dir != null)
            {
                bool rot = PipeModelCodes.IsRotation(t);
                if (!rot && t != "X" && t != "Y" && t != "Z") throw Err(w + ": a direction goes with X / Y / Z (along it) or RX / RY / RZ (about it), not " + t);
                double l = Norm(dir);
                if (!(l > 1e-12)) throw Err(w + ": a zero direction");
                int ax = AxisOf(Scale(dir, 1 / l));
                if (ax >= 0) r.Type = (rot ? "R" : "") + (ax == 0 ? "X" : ax == 1 ? "Y" : "Z");
                else
                {
                    r.Type = rot ? "RX" : "X";
                    r.Cosines = Scale(dir, 1 / l);
                    Warn("skewed restraints (a direction off the global axes) are written as CAESAR writes an axis restraint, type X / RX with the direction's cosines; " +
                         "no CAESAR file has shown a skewed one yet: check them after the import");
                }
                r.Code = PipeModelCodes.Restraint[r.Type];
            }
            if (PipeModelCodes.Unconfirmed.Contains(r.Type)) Warn("restraint type " + r.Type + " is written with code " + r.Code + ", which no CAESAR file has confirmed yet: check it in CAESAR after the import");
            return r;
        }

        // ---------------- supports, ties, SIFs

        List<int> NodesOf(TomlTable t, string w)
        {
            object n = Get(t, "node");
            if (n != null)
            {
                if (Get(t, "run") != null || Get(t, "at") != null || Get(t, "every") != null) throw Err(w + ": node, or run with at / every, not both");
                int node = (int)Math.Round(Num(n, w + ".node"));
                if (!m.Positions.ContainsKey(node)) throw Err(w + ": node " + node + " is not in the model");
                return new List<int> { node };
            }
            var r = RunRef(t, w);
            string pw = w + " on run \"" + r.Name + "\"";
            return Positions(r, t, pw).Select(s => r.NodeAt[s]).ToList();
        }

        // a per-type option: a number, or an inline table by type ({ X = 0.1, "+Y" = 0 })
        double Opt(TomlTable t, string key, string type, List<string> types, string w)
        {
            object v = Get(t, key);
            if (v == null) return 0;
            var tt = v as TomlTable;
            if (tt == null) return Num(v, w + "." + key);
            foreach (string k in tt.Order)
                if (!types.Contains(k.Trim().ToUpperInvariant())) throw Err(w + "." + key + "." + k + ": not one of this item's types (" + string.Join(" ", types.ToArray()) + ")");
            foreach (string k in tt.Order) if (string.Equals(k.Trim(), type, StringComparison.OrdinalIgnoreCase)) return Num(tt[k], w + "." + key + "." + k);
            return 0;
        }

        List<string> TypesOf(TomlTable t, string w)
        {
            var types = new List<string>();
            foreach (string key in new[] { "type", "dofs" })
            {
                object v = Get(t, key);
                if (v == null) continue;
                var a = v as List<object> ?? new List<object> { v };
                foreach (object o in a)
                {
                    var s = o as string;
                    if (s == null) throw Err(w + "." + key + ": restraint types are strings (\"+Y\", \"GUI\", \"RX\" ...)");
                    string u = s.Trim().ToUpperInvariant();
                    if (key == "dofs" && Array.IndexOf(new[] { "X", "Y", "Z", "RX", "RY", "RZ" }, u) < 0) throw Err(w + ".dofs: X Y Z RX RY RZ only (\"" + s + "\")");
                    if (types.Contains(u)) throw Err(w + ": " + u + " twice");
                    types.Add(u);
                }
            }
            if (types.Count == 0) throw Err(w + ": type = \"...\" (or a list) or dofs = [...] is required");
            return types;
        }

        double[] DirOf(TomlTable t, List<string> types, string w)
        {
            object d = Get(t, "direction");
            if (d == null) return null;
            if (types.Count != 1) throw Err(w + ": direction goes with a single type");
            return Vec(d as List<object>, w + ".direction", 1.0);
        }

        void Supports()
        {
            int i = 0;
            foreach (var t in Items("supports"))
            {
                i++;
                string w = "[[supports]] " + i;
                Allowed(t, w, "node run at every from to type dofs gap stiffness mu direction cnode tag");
                var types = TypesOf(t, w);
                var nodes = NodesOf(t, w);
                double[] dir = DirOf(t, types, w);
                int cnode = Get(t, "cnode") != null ? (int)Math.Round(Num(Get(t, "cnode"), w + ".cnode")) : 0;
                if (cnode != 0 && !m.Positions.ContainsKey(cnode)) throw Err(w + ": cnode " + cnode + " is not in the model");
                string tag = Str(t, "tag", "", w);
                foreach (int n in nodes)
                {
                    if (n == cnode) throw Err(w + ": node and cnode are both " + n);
                    foreach (string ty in types)
                        m.Restraints.Add(Restraint(n, ty, Opt(t, "gap", ty, types, w), Opt(t, "stiffness", ty, types, w), Opt(t, "mu", ty, types, w), cnode, dir, tag, w));
                }
            }
        }

        void Ties()
        {
            int i = 0;
            foreach (var t in Items("ties"))
            {
                i++;
                string w = "[[ties]] " + i;
                Allowed(t, w, "node cnode type dofs gap stiffness mu direction tag");
                var types = TypesOf(t, w);
                int a = TieEnd(t, "node", w), b = TieEnd(t, "cnode", w);
                if (a == b) throw Err(w + ": node and cnode are the same node (" + a + ")");
                double[] dir = DirOf(t, types, w);
                foreach (string ty in types)
                    m.Restraints.Add(Restraint(a, ty, Opt(t, "gap", ty, types, w), Opt(t, "stiffness", ty, types, w), Opt(t, "mu", ty, types, w), b, dir, Str(t, "tag", "", w), w));
                res.Ties++;
            }
        }

        int TieEnd(TomlTable t, string key, string w)
        {
            object v = Get(t, key);
            if (v == null) throw Err(w + ": " + key + " is required (a node number, or { run = \"name\", at = position })");
            var tt = v as TomlTable;
            if (tt != null)
            {
                var ns = NodesOf(tt, w + "." + key);
                if (ns.Count != 1) throw Err(w + "." + key + ": one position");
                return ns[0];
            }
            int n = (int)Math.Round(Num(v, w + "." + key));
            if (!m.Positions.ContainsKey(n)) throw Err(w + "." + key + ": node " + n + " is not in the model");
            return n;
        }

        void Sifs()
        {
            int i = 0;
            foreach (var t in Items("sifs"))
            {
                i++;
                string w = "[[sifs]] " + i;
                Allowed(t, w, "node run at every from to type sif_in sif_out");
                string type = Str(t, "type", "", w).Trim().ToLowerInvariant();
                int code;
                if (type == "" || type == "user") code = 0;
                else if (type == "welding" || type == "welding_tee") code = 3;
                else throw Err(w + ": type = \"welding\" (a welding tee), or sif_in / sif_out without a type (other tee types are not confirmed for the neutral file yet)");
                double si = NumOpt(t, "sif_in", 0, w), so = NumOpt(t, "sif_out", si, w);
                if (code == 0 && !(si > 0)) throw Err(w + ": sif_in (and sif_out) are required without a tee type");
                if (si < 0 || so < 0) throw Err(w + ": a negative SIF");
                foreach (int n in NodesOf(t, w)) m.Sifs.Add(new PipeModelSif { Node = n, Type = code, SifIn = si, SifOut = so });
            }
        }

        // ---------------- checks and counts

        void Finish()
        {
            res.Model = m;
            var errs = CaesarNeutralWriter.Validate(m);
            if (errs.Count > 0) throw Err(string.Join("; ", errs.ToArray()));
            var nodes = new HashSet<int>();
            var touching = new Dictionary<int, int>();
            foreach (var e in m.Elements)
            {
                var ends = new List<int> { e.From, e.To };
                if (e.Bend != null) foreach (var an in e.Bend.AngleNodes) ends.Add((int)an[1]);
                foreach (int n in ends.Distinct()) { nodes.Add(n); int c; touching.TryGetValue(n, out c); touching[n] = c + 1; }
            }
            foreach (var r in m.Restraints)
            {
                if (!nodes.Contains(r.Node)) throw Err(r.Source + ": a restraint at node " + r.Node + ", which is on no element");
                if (r.CNode > 0 && !nodes.Contains(r.CNode)) throw Err(r.Source + ": connects node " + r.Node + " to node " + r.CNode + ", which is on no element");
                string key = r.Type + (r.Cosines != null ? " (skewed)" : "") + (r.CNode > 0 ? " (CNODE)" : "");
                int c; res.RestraintsByType.TryGetValue(key, out c); res.RestraintsByType[key] = c + 1;
            }
            foreach (var g in m.Restraints.GroupBy(x => x.Node))
                if (g.Count() > 6 * touching[g.Key])
                    throw Err("node " + g.Key + ": " + g.Count() + " restraints; CAESAR holds 6 per element at a node and " + touching[g.Key] + " element(s) meet there");
            foreach (var s in m.Sifs) if (!nodes.Contains(s.Node)) throw Err("a SIF / tee at node " + s.Node + ", which is on no element");
            foreach (var g in m.Sifs.GroupBy(x => x.Node))
                if (g.Count() > 1) throw Err("node " + g.Key + ": " + g.Count() + " SIF / tee entries (a branch's welding tee is automatic; give tee = \"none\" on the branch to define your own)");
            foreach (var e in m.Elements) { int c; res.ElementsByKind.TryGetValue(e.Kind, out c); res.ElementsByKind[e.Kind] = c + 1; }
            res.Nodes = nodes.Count;
            res.Elements = m.Elements.Count;
            res.Restraints = m.Restraints.Count;
            res.RestraintNodes = m.Restraints.Select(x => x.Node).Distinct().Count();
            res.Sifs = m.Sifs.Count;
            m.Warnings.AddRange(res.Warnings);
            // CAESAR puts a node no element has reached yet at the previous element's end: check that holds
            var seen = new HashSet<int>(m.Coords.Select(c => c.Node));
            for (int i = 0; i < m.Elements.Count; i++)
            {
                var e = m.Elements[i];
                if (!seen.Contains(e.From))
                {
                    if (i == 0) throw Err("the first element starts at node " + e.From + ", which has no COORDS entry");
                    var prev = m.Elements[i - 1];
                    if (Dist(m.Positions[e.From], m.Positions[prev.To]) > 1e-6)
                        throw Err("element " + e.From + "-" + e.To + " starts at a new node away from the previous element's end, where CAESAR would put it");
                }
                seen.Add(e.From); seen.Add(e.To);
            }
        }

        // ---------------- vectors

        static double Dot(double[] a, double[] b) { return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]; }
        static double Norm(double[] a) { return Math.Sqrt(Dot(a, a)); }
        static double[] Add(double[] a, double[] d, double t) { return new[] { a[0] + d[0] * t, a[1] + d[1] * t, a[2] + d[2] * t }; }
        static double[] Sub(double[] a, double[] b) { return new[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] }; }
        static double[] Scale(double[] a, double t) { return new[] { a[0] * t, a[1] * t, a[2] * t }; }
        static double[] Cross(double[] a, double[] b) { return new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] }; }
        static double Dist(double[] a, double[] b) { return Norm(Sub(a, b)); }
        static string Fmt(double v) { return v.ToString("0.###", CultureInfo.InvariantCulture); }
    }
}
