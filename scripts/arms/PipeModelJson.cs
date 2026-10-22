using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

// PipeModelJson  --  PipeModel <-> JSON text (format "pluto-pipe-model", version 2): the whole model, its carried
// CAESAR text included, so a model can be stored (the Excel workbook's base model, a debug dump) and read back
// EXACTLY: numbers are written with 17 significant digits and parsed by double.Parse (no JavaScriptSerializer:
// it turns fractions into decimal and caps the text at 2 MB). Keys are written in a fixed order, so the same
// model always gives the same text. NaN (a free displacement DOF) is null. Version 2 adds the loads (nodeLoads,
// an element's uniform / wind); a version 1 file (Stage A: displacements carried as nodeItems) still reads. C# 5.
//
//   string json = PipeModelJson.Write(model);   PipeModel back = PipeModelJson.Read(json);

public static class PipeModelJson
{
    public const string Format = "pluto-pipe-model";
    public const int Version = 2;

    // ---------------------------------------------------------------- write

    public static string Write(PipeModel m)
    {
        var w = new JW();
        w.Open();
        w.Str("format", Format); w.Num("version", Version);
        w.Str("title", m.Title); w.Str("notes", m.Notes); w.Bool("zUp", m.ZUp); w.Num("ambient", m.Ambient); w.Str("code", m.Code);
        w.Nums("unitFactors", m.UnitFactors);
        w.Lines("allowablesBlock", m.AllowablesBlock);
        if (m.Source != null)
        {
            var s = m.Source;
            w.Key("source"); w.Open();
            w.Str("fileName", s.FileName); w.Num("version", s.Version); w.Str("versionLine", s.VersionLine); w.Num("codePage", s.CodePage);
            w.Str("lineEnding", s.LineEnding); w.Bool("finalNewline", s.FinalNewline); w.Bool("seed", s.Seed);
            w.Lines("prelude", s.Prelude); w.Lines("sectionOrder", s.SectionOrder);
            w.Key("headers"); w.Open(); foreach (var kv in s.Headers.OrderBy(k => k.Key, StringComparer.Ordinal)) w.Str(kv.Key, kv.Value); w.Close();
            w.Key("sections"); w.Open(); foreach (var kv in s.Sections.OrderBy(k => k.Key, StringComparer.Ordinal)) w.Lines(kv.Key, kv.Value); w.Close();
            w.Key("linesPerBlock"); w.Open(); foreach (var kv in s.LinesPerBlock.OrderBy(k => k.Key, StringComparer.Ordinal)) w.Num(kv.Key, kv.Value); w.Close();
            w.Key("trailing"); w.Open(); foreach (var kv in s.Trailing.OrderBy(k => k.Key, StringComparer.Ordinal)) w.Lines(kv.Key, kv.Value); w.Close();
            w.Lines("miscelMaterialLines", s.MiscelMaterialLines); w.Lines("miscelTail", s.MiscelTail);
            w.Str("coordsCountLine", s.CoordsCountLine);
            w.Str("originalTitle", s.OriginalTitle); w.Str("originalNotes", s.OriginalNotes); w.Num("elementCount", s.ElementCount);
            w.Close();
        }
        w.Key("elements"); w.OpenArr();
        foreach (var e in m.Elements)
        {
            w.Open();
            w.Num("uid", e.Uid); w.Num("from", e.From); w.Num("to", e.To); w.Nums("d", new[] { e.Dx, e.Dy, e.Dz });
            w.Num("od", e.Od); w.Num("wall", e.Wall); w.Num("insul", e.Insulation); w.Num("corr", e.Corrosion);
            w.Nums("t", e.T); w.Nums("p", e.P);
            w.Num("e", e.Emod); w.Num("poisson", e.Poisson); w.Num("pipeDensity", e.PipeDensity);
            w.Num("insulDensity", e.InsulationDensity); w.Num("fluidDensity", e.FluidDensity); w.Num("material", e.Material);
            w.Nums("teeAngles", new[] { e.TeeAngle1, e.TeeAngle2 });
            w.Str("name", e.Name); w.Str("line", e.LineNumber); w.Str("kind", e.Kind);
            if (e.Bend != null)
            {
                var b = e.Bend;
                w.Key("bend"); w.Open();
                w.Num("radius", b.Radius); w.Num("type", b.Type);
                w.Key("nodes"); w.OpenArr(); foreach (var an in b.AngleNodes) w.NumArr(an); w.CloseArr();
                w.Num("miter", b.MiterPoints); w.Num("fittingThk", b.FittingThickness); w.Num("k", b.KFactor);
                w.Num("seq", b.Seq); w.Lines("raw", b.Raw);
                w.Close();
            }
            if (e.Rigid != null) { w.Key("rigid"); w.Open(); w.Num("weight", e.Rigid.Weight); w.Num("type", e.Rigid.Type); w.Num("seq", e.Rigid.Seq); w.Lines("raw", e.Rigid.Raw); w.Close(); }
            if (e.ExpJoint != null)
            {
                var x = e.ExpJoint;
                w.Key("expJoint"); w.Open();
                w.Num("axial", x.AxialStiffness); w.Num("transverse", x.TransverseStiffness); w.Num("bending", x.BendingStiffness);
                w.Num("torsion", x.TorsionStiffness); w.Num("effId", x.EffectiveId); w.Num("seq", x.Seq); w.Lines("raw", x.Raw);
                w.Close();
            }
            if (e.Reducer != null)
            {
                var r = e.Reducer;
                w.Key("reducer"); w.Open();
                w.Num("od2", r.Od2); w.Num("wall2", r.Wall2); w.Num("alpha", r.Alpha); w.Num("r1", r.R1); w.Num("r2", r.R2); w.Num("seq", r.Seq); w.Lines("raw", r.Raw);
                w.Close();
            }
            if (e.Uniform != null)
            {
                var u = e.Uniform;
                w.Key("uniform"); w.Open();
                w.Key("vectors"); w.OpenArr(); foreach (var v in u.Vectors) w.NumArr(v); w.CloseArr();
                w.Nums("g", u.G.Select(x => x ? 1.0 : 0.0).ToArray()); w.Num("seq", u.Seq); w.Lines("raw", u.Raw);
                w.Close();
            }
            if (e.Wind != null)
            {
                var x = e.Wind;
                w.Key("wind"); w.Open();
                w.Num("type", x.Type); w.Num("shape", x.Shape); w.Num("addedMass", x.AddedMass); w.Num("lift", x.Lift);
                w.Num("marineGrowth", x.MarineGrowth); w.Num("marineGrowthDensity", x.MarineGrowthDensity); w.Num("seq", x.Seq); w.Lines("raw", x.Raw);
                w.Close();
            }
            w.Lines("raw", e.Raw);
            Blocks(w, "carried", e.Carried);
            Blocks(w, "slotBlocks", e.SlotBlocks);
            w.Close();
        }
        w.CloseArr();
        w.Key("restraints"); w.OpenArr();
        foreach (var r in m.Restraints)
        {
            w.Open();
            w.Num("node", r.Node); w.Str("type", r.Type); w.Num("code", r.Code); w.Num("k", r.Stiffness); w.Num("gap", r.Gap); w.Num("mu", r.Mu);
            w.Num("cnode", r.CNode); w.Nums("cos", r.Cosines); w.Str("tag", r.Tag); w.Str("guid", r.Guid); w.Str("source", r.Source);
            w.Num("owner", r.OwnerUid); w.Num("slot", r.Slot); w.Lines("raw", r.Raw);
            w.Close();
        }
        w.CloseArr();
        w.Key("joints"); w.OpenArr();
        foreach (var j in m.Joints)
        {
            w.Open();
            w.Num("uid", j.Uid); w.Str("kind", j.Kind); w.Num("node", j.Node); w.Num("cnode", j.CNode); w.Str("frame", j.Frame);
            w.Key("dofs"); w.OpenArr();
            foreach (var d in j.Dofs) { w.Open(); w.Str("dof", d.Dof); w.Num("gap", d.Gap); w.Num("k", d.Stiffness); w.Close(); }
            w.CloseArr();
            w.Num("sif", j.Sif); w.Str("tag", j.Tag);
            w.Close();
        }
        w.CloseArr();
        w.Key("sifs"); w.OpenArr();
        foreach (var t in m.Sifs)
        {
            w.Open();
            w.Num("node", t.Node); w.Num("type", t.Type); w.Num("sifIn", t.SifIn); w.Num("sifOut", t.SifOut); w.Num("surfaceNode", t.SurfaceNode);
            w.Num("owner", t.OwnerUid); w.Num("slot", t.Slot); w.Lines("raw", t.Raw);
            w.Close();
        }
        w.CloseArr();
        w.Key("nodeLoads"); w.OpenArr();
        foreach (var d in m.NodeLoads)
        {
            w.Open();
            w.Str("kind", d.Kind); w.Num("node", d.Node);
            w.Key("vectors"); w.OpenArr(); foreach (var v in d.Vectors) w.NumArr(v); w.CloseArr();
            w.Str("source", d.Source); w.Num("owner", d.OwnerUid); w.Num("slot", d.Slot); w.Lines("raw", d.Raw);
            w.Close();
        }
        w.CloseArr();
        w.Key("coords"); w.OpenArr();
        foreach (var c in m.Coords)
        {
            w.Open(); w.Num("node", c.Node); w.Num("x", c.X); w.Num("y", c.Y); w.Num("z", c.Z); w.Str("raw", c.Raw); w.Nums("imported", c.ImportedXyz); w.Close();
        }
        w.CloseArr();
        w.Close();
        return w.ToString();
    }

    static void Blocks(JW w, string key, Dictionary<string, PipeModelBlock> blocks)
    {
        if (blocks.Count == 0) return;
        w.Key(key); w.Open();
        foreach (var kv in blocks.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            w.Key(kv.Key); w.Open(); w.Num("seq", kv.Value.Seq); w.Lines("lines", kv.Value.Lines); w.Close();
        }
        w.Close();
    }

    // a small writer: one value per line inside objects, arrays of numbers and strings on one line
    sealed class JW
    {
        readonly StringBuilder sb = new StringBuilder();
        readonly Stack<bool> first = new Stack<bool>();
        int depth;
        bool pendingKey;

        void Sep()
        {
            if (pendingKey) { pendingKey = false; return; }
            if (first.Count > 0)
            {
                if (!first.Peek()) sb.Append(',');
                first.Pop(); first.Push(false);
                sb.Append('\n').Append(' ', 2 * depth);
            }
        }

        public void Open() { Sep(); sb.Append('{'); first.Push(true); depth++; }
        public void Close() { depth--; bool empty = first.Pop(); if (!empty) sb.Append('\n').Append(' ', 2 * depth); sb.Append('}'); }
        public void OpenArr() { Sep(); sb.Append('['); first.Push(true); depth++; }
        public void CloseArr() { depth--; bool empty = first.Pop(); if (!empty) sb.Append('\n').Append(' ', 2 * depth); sb.Append(']'); }
        public void Key(string k) { Sep(); sb.Append(Q(k)).Append(": "); pendingKey = true; }
        public void Str(string k, string v) { Key(k); pendingKey = false; sb.Append(v == null ? "null" : Q(v)); }
        public void Num(string k, double v) { Key(k); pendingKey = false; sb.Append(N(v)); }
        public void Bool(string k, bool v) { Key(k); pendingKey = false; sb.Append(v ? "true" : "false"); }
        public void Nums(string k, double[] v)
        {
            Key(k); pendingKey = false;
            if (v == null) { sb.Append("null"); return; }
            sb.Append('[').Append(string.Join(", ", v.Select(N).ToArray())).Append(']');
        }
        public void NumArr(double[] v) { Sep(); sb.Append('[').Append(string.Join(", ", v.Select(N).ToArray())).Append(']'); }
        public void Lines(string k, IList<string> v)
        {
            Key(k); pendingKey = false;
            if (v == null) { sb.Append("null"); return; }
            if (v.Count == 0) { sb.Append("[]"); return; }
            sb.Append('[');
            for (int i = 0; i < v.Count; i++) sb.Append(i > 0 ? "," : "").Append('\n').Append(' ', 2 * depth + 2).Append(Q(v[i]));
            sb.Append('\n').Append(' ', 2 * depth).Append(']');
        }
        public override string ToString() { return sb.ToString() + "\n"; }
    }

    static string N(double v)
    {
        if (double.IsNaN(v) || double.IsInfinity(v)) return "null";
        if (v == Math.Floor(v) && Math.Abs(v) < 1e15) return ((long)v).ToString(CultureInfo.InvariantCulture);
        return v.ToString("G17", CultureInfo.InvariantCulture);
    }

    static string Q(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (char c in s ?? "")
        {
            if (c == '"' || c == '\\') sb.Append('\\').Append(c);
            else if (c == '\n') sb.Append("\\n");
            else if (c == '\r') sb.Append("\\r");
            else if (c == '\t') sb.Append("\\t");
            else if (c < 0x20 || c > 0x7e) sb.Append("\\u").Append(((int)c).ToString("x4"));
            else sb.Append(c);
        }
        return sb.Append('"').ToString();
    }

    // ---------------------------------------------------------------- read

    public static PipeModel Read(string json)
    {
        var root = new JR(json ?? "").Document() as Dictionary<string, object>;
        if (root == null) throw new FormatException("PipeModelJson: not a JSON object");
        if (S(root, "format") != Format) throw new FormatException("PipeModelJson: format \"" + S(root, "format") + "\" (\"" + Format + "\" expected)");
        if (D(root, "version") > Version) throw new FormatException("PipeModelJson: version " + D(root, "version") + " is newer than this reader (" + Version + ")");
        var m = new PipeModel();
        m.Title = S(root, "title") ?? ""; m.Notes = S(root, "notes") ?? ""; m.ZUp = B(root, "zUp"); m.Ambient = D(root, "ambient"); m.Code = S(root, "code") ?? "";
        m.UnitFactors = Ds(root, "unitFactors");
        m.AllowablesBlock = L(root, "allowablesBlock");
        var so = O(root, "source");
        if (so != null)
        {
            var s = new PipeModelSource();
            s.FileName = S(so, "fileName") ?? ""; s.Version = D(so, "version"); s.VersionLine = S(so, "versionLine") ?? ""; s.CodePage = (int)D(so, "codePage");
            s.LineEnding = S(so, "lineEnding") ?? "\r\n"; s.FinalNewline = B(so, "finalNewline"); s.Seed = B(so, "seed");
            s.Prelude.AddRange(L(so, "prelude") ?? new List<string>()); s.SectionOrder.AddRange(L(so, "sectionOrder") ?? new List<string>());
            var h = O(so, "headers"); if (h != null) foreach (var kv in h) s.Headers[kv.Key] = (string)kv.Value;
            var sec = O(so, "sections"); if (sec != null) foreach (var kv in sec) s.Sections[kv.Key] = ToLines(kv.Value);
            var lpb = O(so, "linesPerBlock"); if (lpb != null) foreach (var kv in lpb) s.LinesPerBlock[kv.Key] = (int)(double)kv.Value;
            var tr = O(so, "trailing"); if (tr != null) foreach (var kv in tr) s.Trailing[kv.Key] = ToLines(kv.Value);
            s.MiscelMaterialLines = L(so, "miscelMaterialLines") ?? new List<string>(); s.MiscelTail = L(so, "miscelTail") ?? new List<string>();
            s.CoordsCountLine = S(so, "coordsCountLine");
            s.OriginalTitle = S(so, "originalTitle") ?? ""; s.OriginalNotes = S(so, "originalNotes") ?? ""; s.ElementCount = (int)D(so, "elementCount");
            m.Source = s;
        }
        foreach (var eo in Arr(root, "elements"))
        {
            var o = (Dictionary<string, object>)eo;
            var e = new PipeModelElement();
            e.Uid = I(o, "uid"); e.From = I(o, "from"); e.To = I(o, "to");
            var d = Ds(o, "d"); e.Dx = d[0]; e.Dy = d[1]; e.Dz = d[2];
            e.Od = D(o, "od"); e.Wall = D(o, "wall"); e.Insulation = D(o, "insul"); e.Corrosion = D(o, "corr");
            Array.Copy(Ds(o, "t"), e.T, 9); Array.Copy(Ds(o, "p"), e.P, 9);
            e.Emod = D(o, "e"); e.Poisson = D(o, "poisson"); e.PipeDensity = D(o, "pipeDensity");
            e.InsulationDensity = D(o, "insulDensity"); e.FluidDensity = D(o, "fluidDensity"); e.Material = I(o, "material");
            var ta = Ds(o, "teeAngles"); e.TeeAngle1 = ta[0]; e.TeeAngle2 = ta[1];
            e.Name = S(o, "name") ?? ""; e.LineNumber = S(o, "line") ?? ""; e.Kind = S(o, "kind") ?? "pipe";
            var bo = O(o, "bend");
            if (bo != null)
            {
                var b = new PipeModelBend { Radius = D(bo, "radius"), Type = I(bo, "type"), MiterPoints = I(bo, "miter"), FittingThickness = D(bo, "fittingThk"), KFactor = D(bo, "k"), Seq = I(bo, "seq"), Raw = L(bo, "raw") };
                foreach (var an in Arr(bo, "nodes")) b.AngleNodes.Add(((List<object>)an).Select(x => (double)x).ToArray());
                e.Bend = b;
            }
            var ro = O(o, "rigid");
            if (ro != null) e.Rigid = new PipeModelRigid { Weight = D(ro, "weight"), Type = I(ro, "type"), Seq = I(ro, "seq"), Raw = L(ro, "raw") };
            var xo = O(o, "expJoint");
            if (xo != null) e.ExpJoint = new PipeModelExpJoint { AxialStiffness = D(xo, "axial"), TransverseStiffness = D(xo, "transverse"), BendingStiffness = D(xo, "bending"), TorsionStiffness = D(xo, "torsion"), EffectiveId = D(xo, "effId"), Seq = I(xo, "seq"), Raw = L(xo, "raw") };
            var rdo = O(o, "reducer");
            if (rdo != null) e.Reducer = new PipeModelReducer { Od2 = D(rdo, "od2"), Wall2 = D(rdo, "wall2"), Alpha = D(rdo, "alpha"), R1 = D(rdo, "r1"), R2 = D(rdo, "r2"), Seq = I(rdo, "seq"), Raw = L(rdo, "raw") };
            var uo = O(o, "uniform");
            if (uo != null)
            {
                var u = new PipeModelUniform { Seq = I(uo, "seq"), Raw = L(uo, "raw") };
                var vs = Arr(uo, "vectors");
                for (int v = 0; v < 9 && v < vs.Count; v++) { var a = Nums(vs[v]); for (int k = 0; k < 3 && k < a.Length; k++) u.Vectors[v][k] = a[k]; }
                var g = Ds(uo, "g");
                for (int v = 0; g != null && v < 9 && v < g.Length; v++) u.G[v] = g[v] == 1;
                e.Uniform = u;
            }
            var wo = O(o, "wind");
            if (wo != null)
                e.Wind = new PipeModelWind { Type = I(wo, "type"), Shape = D(wo, "shape"), AddedMass = D(wo, "addedMass"), Lift = D(wo, "lift"),
                                             MarineGrowth = D(wo, "marineGrowth"), MarineGrowthDensity = D(wo, "marineGrowthDensity"), Seq = I(wo, "seq"), Raw = L(wo, "raw") };
            e.Raw = L(o, "raw");
            ReadBlocks(O(o, "carried"), e.Carried);
            ReadBlocks(O(o, "slotBlocks"), e.SlotBlocks);
            m.Elements.Add(e);
        }
        foreach (var ro0 in Arr(root, "restraints"))
        {
            var o = (Dictionary<string, object>)ro0;
            m.Restraints.Add(new PipeModelRestraint
            {
                Node = I(o, "node"), Type = S(o, "type") ?? "", Code = I(o, "code"), Stiffness = D(o, "k"), Gap = D(o, "gap"), Mu = D(o, "mu"), CNode = I(o, "cnode"),
                Cosines = Ds(o, "cos"), Tag = S(o, "tag") ?? "", Guid = S(o, "guid") ?? "", Source = S(o, "source") ?? "", OwnerUid = I(o, "owner"), Slot = I(o, "slot"), Raw = L(o, "raw")
            });
        }
        foreach (var jo in Arr(root, "joints"))
        {
            var o = (Dictionary<string, object>)jo;
            var j = new PipeModelJoint { Uid = I(o, "uid"), Kind = S(o, "kind") ?? "", Node = I(o, "node"), CNode = I(o, "cnode"), Frame = S(o, "frame") ?? "pipe", Sif = D(o, "sif"), Tag = S(o, "tag") ?? "" };
            foreach (var dofo in Arr(o, "dofs")) { var x = (Dictionary<string, object>)dofo; j.Dofs.Add(new PipeModelJointDof { Dof = S(x, "dof") ?? "", Gap = D(x, "gap"), Stiffness = D(x, "k") }); }
            m.Joints.Add(j);
        }
        foreach (var to in Arr(root, "sifs"))
        {
            var o = (Dictionary<string, object>)to;
            m.Sifs.Add(new PipeModelSif { Node = I(o, "node"), Type = I(o, "type"), SifIn = D(o, "sifIn"), SifOut = D(o, "sifOut"), SurfaceNode = I(o, "surfaceNode"), OwnerUid = I(o, "owner"), Slot = I(o, "slot"), Raw = L(o, "raw") });
        }
        foreach (var no in Arr(root, "nodeLoads"))
        {
            var o = (Dictionary<string, object>)no;
            var d = new PipeModelNodeLoad(S(o, "kind") ?? "force") { Node = I(o, "node"), Source = S(o, "source") ?? "", OwnerUid = I(o, "owner"), Slot = I(o, "slot"), Raw = L(o, "raw") };
            var vs = Arr(o, "vectors");
            for (int v = 0; v < 9 && v < vs.Count; v++) { var a = Nums(vs[v]); for (int k = 0; k < 6 && k < a.Length; k++) d.Vectors[v][k] = a[k]; }
            m.NodeLoads.Add(d);
        }
        foreach (var no in Arr(root, "nodeItems"))   // version 1: displacements carried as their slot lines
        {
            var o = (Dictionary<string, object>)no;
            var raw = L(o, "raw");
            var d = CaesarPipeImport.NodeLoad("displacement", raw) ?? new PipeModelNodeLoad("displacement") { Node = I(o, "node"), Raw = raw };
            d.OwnerUid = I(o, "owner"); d.Slot = I(o, "slot"); d.Source = "imported";
            m.NodeLoads.Add(d);
        }
        foreach (var co in Arr(root, "coords"))
        {
            var o = (Dictionary<string, object>)co;
            m.Coords.Add(new PipeModelCoord { Node = I(o, "node"), X = D(o, "x"), Y = D(o, "y"), Z = D(o, "z"), Raw = S(o, "raw"), ImportedXyz = Ds(o, "imported") });
        }
        return m;
    }

    static void ReadBlocks(Dictionary<string, object> o, Dictionary<string, PipeModelBlock> into)
    {
        if (o == null) return;
        foreach (var kv in o)
        {
            var b = (Dictionary<string, object>)kv.Value;
            into[kv.Key] = new PipeModelBlock { Seq = I(b, "seq"), Lines = L(b, "lines") ?? new List<string>() };
        }
    }

    static object Get(Dictionary<string, object> o, string k) { object v; return o != null && o.TryGetValue(k, out v) ? v : null; }
    static string S(Dictionary<string, object> o, string k) { return Get(o, k) as string; }
    static double D(Dictionary<string, object> o, string k) { object v = Get(o, k); return v is double ? (double)v : 0.0; }
    static int I(Dictionary<string, object> o, string k) { return (int)Math.Round(D(o, k)); }
    static bool B(Dictionary<string, object> o, string k) { object v = Get(o, k); return v is bool && (bool)v; }
    static Dictionary<string, object> O(Dictionary<string, object> o, string k) { return Get(o, k) as Dictionary<string, object>; }
    static List<object> Arr(Dictionary<string, object> o, string k) { return Get(o, k) as List<object> ?? new List<object>(); }
    static double[] Ds(Dictionary<string, object> o, string k)
    {
        var a = Get(o, k) as List<object>;
        return a == null ? null : a.Select(x => x is double ? (double)x : double.NaN).ToArray();
    }
    static double[] Nums(object v)
    {
        var a = v as List<object>;
        return a == null ? new double[0] : a.Select(x => x is double ? (double)x : double.NaN).ToArray();
    }
    static List<string> L(Dictionary<string, object> o, string k) { object v = Get(o, k); return v == null ? null : ToLines(v); }
    static List<string> ToLines(object v)
    {
        var a = v as List<object>;
        return a == null ? null : a.Select(x => x as string ?? "").ToList();
    }

    // a strict reader of standard JSON: objects -> Dictionary, arrays -> List<object>, numbers -> double (exact), strings, bools, null
    sealed class JR
    {
        readonly string s;
        int i;
        public JR(string text) { s = text; }

        public object Document()
        {
            var v = Value();
            Ws();
            if (i < s.Length) throw Err("text after the JSON value");
            return v;
        }

        FormatException Err(string m) { return new FormatException("PipeModelJson: " + m + " at character " + (i + 1).ToString(CultureInfo.InvariantCulture)); }

        void Ws() { while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\n' || s[i] == '\r' || s[i] == '\uFEFF')) i++; }

        object Value()
        {
            Ws();
            if (i >= s.Length) throw Err("a value was expected");
            char c = s[i];
            if (c == '{') return Obj();
            if (c == '[') return ArrV();
            if (c == '"') return Str();
            if (c == 't' && s.Substring(i).StartsWith("true", StringComparison.Ordinal)) { i += 4; return true; }
            if (c == 'f' && s.Substring(i).StartsWith("false", StringComparison.Ordinal)) { i += 5; return false; }
            if (c == 'n' && s.Substring(i).StartsWith("null", StringComparison.Ordinal)) { i += 4; return null; }
            int st = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            double d;
            if (i == st || !double.TryParse(s.Substring(st, i - st), NumberStyles.Float, CultureInfo.InvariantCulture, out d)) throw Err("a value was expected");
            return d;
        }

        Dictionary<string, object> Obj()
        {
            var o = new Dictionary<string, object>(StringComparer.Ordinal);
            i++;
            Ws();
            if (i < s.Length && s[i] == '}') { i++; return o; }
            while (true)
            {
                Ws();
                if (i >= s.Length || s[i] != '"') throw Err("a key was expected");
                string k = Str();
                Ws();
                if (i >= s.Length || s[i] != ':') throw Err("':' expected");
                i++;
                o[k] = Value();
                Ws();
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == '}') { i++; return o; }
                throw Err("',' or '}' expected");
            }
        }

        List<object> ArrV()
        {
            var a = new List<object>();
            i++;
            Ws();
            if (i < s.Length && s[i] == ']') { i++; return a; }
            while (true)
            {
                a.Add(Value());
                Ws();
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == ']') { i++; return a; }
                throw Err("',' or ']' expected");
            }
        }

        string Str()
        {
            var sb = new StringBuilder();
            i++;
            while (i < s.Length && s[i] != '"')
            {
                char c = s[i++];
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= s.Length) break;
                char e = s[i++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u':
                        if (i + 4 > s.Length) throw Err("a short \\u escape");
                        sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        i += 4;
                        break;
                    default: sb.Append(e); break;
                }
            }
            if (i >= s.Length) throw Err("an unterminated string");
            i++;
            return sb.ToString();
        }
    }
}
