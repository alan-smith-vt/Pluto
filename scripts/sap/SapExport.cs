// Whatever SAP2000 holds -> .s2k text SapToPluto reads (model + results), no config, no manual export.
// The ANALYSIS MESH, not the objects: PointElm / AreaElm / LineElm renumbered 1..N (SapToPluto wants
// integer labels), with sections, local axes, restraints and groups carried from the objects. Auto-meshed
// models come out at the mesh SAP analysed (where the results live); unmeshed ones one-to-one with their
// objects. <base>.labels.csv maps every id back to its element / object name.
// Not carried: tendons, cables, links, solids (no viewer domain); frame end releases and explicit
// insertion offsets; reactions and link forces (vault/arms/sap-results-coverage).
// C# 5, Add-Type under PS 5.1 with SapSession.cs (Import-SapApi.ps1). Driven by Run-Sap.ps1 -Export.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using SAP2000v1;

public class SapExport
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public class Mesh : IResultIds
    {
        public Dictionary<string, int> Pid = new Dictionary<string, int>(), Aid = new Dictionary<string, int>(), Lid = new Dictionary<string, int>();
        public List<double[]> Coords = new List<double[]>();
        public SortedDictionary<int, double[]> PointAxes = new SortedDictionary<int, double[]>();   // id -> (a, b, c) when not all zero
        public List<string> PointNames = new List<string>();
        public class AreaRec { public string Elm, Section, Obj; public int[] Points; public double Angle; }
        public class LineRec { public string Elm, Section, Obj; public int I, J; }
        public List<AreaRec> Areas = new List<AreaRec>();
        public List<LineRec> Lines = new List<LineRec>();
        public Dictionary<string, List<int>> AreaObj = new Dictionary<string, List<int>>(), LineObj = new Dictionary<string, List<int>>();
        public Dictionary<int, int> SkippedLines = new Dictionary<int, int>();   // line object type -> count
        public double Seconds;

        public Mesh(cSapModel m)
        {
            DateTime t0 = DateTime.Now;
            int n = 0; string[] names = null;
            m.PointElm.GetNameList(ref n, ref names);
            for (int i = 0; i < n; i++)
            {
                double x = 0, y = 0, z = 0;
                m.PointElm.GetCoordCartesian(names[i], ref x, ref y, ref z, "Global");
                Pid[names[i]] = Coords.Count + 1;
                Coords.Add(new double[] { x, y, z });
                PointNames.Add(names[i]);
                double a = 0, b = 0, c = 0;
                m.PointElm.GetLocalAxes(names[i], ref a, ref b, ref c);
                if (a != 0 || b != 0 || c != 0) PointAxes[Coords.Count] = new double[] { a, b, c };
            }
            n = 0; names = null;
            m.AreaElm.GetNameList(ref n, ref names);
            for (int i = 0; i < n; i++)
            {
                int np = 0; string[] pts = null; string sect = "", obj = ""; double ang = 0;
                m.AreaElm.GetPoints(names[i], ref np, ref pts);
                m.AreaElm.GetProperty(names[i], ref sect);
                m.AreaElm.GetObj(names[i], ref obj);
                m.AreaElm.GetLocalAxes(names[i], ref ang);
                int[] ids = new int[np];
                for (int k = 0; k < np; k++) ids[k] = Pid[pts[k]];
                Areas.Add(new AreaRec { Elm = names[i], Section = sect, Obj = obj, Points = ids, Angle = ang });
                Aid[names[i]] = Areas.Count;
                Add(AreaObj, obj, Areas.Count);
            }
            n = 0; names = null;
            m.LineElm.GetNameList(ref n, ref names);
            for (int i = 0; i < n; i++)
            {
                string obj = ""; int otype = 0; double rdi = 0, rdj = 0;
                m.LineElm.GetObj(names[i], ref obj, ref otype, ref rdi, ref rdj);
                if (otype != 0)   // 0 = frame; cables / tendons have no beam domain
                {
                    SkippedLines[otype] = (SkippedLines.ContainsKey(otype) ? SkippedLines[otype] : 0) + 1;
                    continue;
                }
                string p1 = "", p2 = "", sect = ""; int ptype = 0; bool sv = false; double rel = 0, tot = 0;
                m.LineElm.GetPoints(names[i], ref p1, ref p2);
                m.LineElm.GetProperty(names[i], ref sect, ref ptype, ref sv, ref rel, ref tot);
                Lines.Add(new LineRec { Elm = names[i], Section = sect, Obj = obj, I = Pid[p1], J = Pid[p2] });
                Lid[names[i]] = Lines.Count;
                Add(LineObj, obj, Lines.Count);
            }
            Seconds = (DateTime.Now - t0).TotalSeconds;
        }

        static void Add(Dictionary<string, List<int>> d, string k, int v)
        {
            List<int> l;
            if (!d.TryGetValue(k, out l)) { l = new List<int>(); d[k] = l; }
            l.Add(v);
        }

        public int PointOfObj(cSapModel m, string obj)
        {
            string elm = "";
            if (m.PointObj.GetElm(obj, ref elm) != 0) return 0;
            int id; return Pid.TryGetValue(elm, out id) ? id : 0;
        }

        public string Point(string e) { int i; return Pid.TryGetValue(e, out i) ? i.ToString(Inv) : null; }
        public string Area(string e) { int i; return Aid.TryGetValue(e, out i) ? i.ToString(Inv) : null; }
        public string Line(string e) { int i; return Lid.TryGetValue(e, out i) ? i.ToString(Inv) : null; }
        public string Link(string e) { return null; }

        public string Summary()
        {
            List<string> sk = new List<string>();
            foreach (KeyValuePair<int, int> kv in SkippedLines) sk.Add("type " + kv.Key + ": " + kv.Value);
            return string.Format(Inv, "{0} points, {1} shells, {2} frame elements{3}  ({4:0}s)", Coords.Count, Areas.Count, Lines.Count,
                sk.Count > 0 ? "; line elements skipped " + string.Join(", ", sk.ToArray()) : "", Seconds);
        }
    }

    // PowerShell cannot pass the COM model to a constructor: build the mesh from the session.
    public static Mesh BuildMesh(SapSession s) { return new Mesh(s.Model); }

    // The model tables (analysis mesh) as .s2k text.
    public static string ModelS2k(SapSession s, Mesh mesh)
    {
        cSapModel m = s.Model;
        List<string> L = new List<string>();
        L.Add("File generated by SapExport.cs (SAP2000 OAPI, analysis mesh)"); L.Add("");
        L.Add("TABLE:  \"PROGRAM CONTROL\""); L.Add(Row("ProgramName", "SAP2000", "Version", s.Version(), "CurrUnits", s.Units())); L.Add("");

        List<string> cs = Rows(s.TableRows("Coordinate Systems"));
        if (cs.Count == 0) cs.Add(Row("Name", "GLOBAL", "Type", "Cartesian", "X", "0", "Y", "0", "Z", "0", "AboutZ", "0", "AboutY", "0", "AboutX", "0"));
        Table(L, "COORDINATE SYSTEMS", cs);

        List<string> rows = new List<string>();
        for (int i = 0; i < mesh.Coords.Count; i++)
        {
            string x = SapSession.R(mesh.Coords[i][0]), y = SapSession.R(mesh.Coords[i][1]), z = SapSession.R(mesh.Coords[i][2]);
            rows.Add(Row("Joint", Id(i + 1), "CoordSys", "GLOBAL", "CoordType", "Cartesian", "XorR", x, "Y", y, "Z", z,
                "SpecialJt", "No", "GlobalX", x, "GlobalY", y, "GlobalZ", z));
        }
        Table(L, "JOINT COORDINATES", rows);

        rows = new List<string>();
        for (int i = 0; i < mesh.Areas.Count; i++)
        {
            List<string> kv = new List<string> { "Area", Id(i + 1), "NumJoints", Id(mesh.Areas[i].Points.Length) };
            for (int k = 0; k < mesh.Areas[i].Points.Length; k++) { kv.Add("Joint" + (k + 1)); kv.Add(Id(mesh.Areas[i].Points[k])); }
            rows.Add(Row(kv.ToArray()));
        }
        Table(L, "CONNECTIVITY - AREA", rows);
        rows = new List<string>();
        for (int i = 0; i < mesh.Areas.Count; i++) rows.Add(Row("Area", Id(i + 1), "Section", mesh.Areas[i].Section, "MatProp", "Default"));
        Table(L, "AREA SECTION ASSIGNMENTS", rows);
        Table(L, "AREA SECTION PROPERTIES", Rows(s.TableRows("Area Section Properties")));
        rows = new List<string>();
        for (int i = 0; i < mesh.Areas.Count; i++)
            if (mesh.Areas[i].Angle != 0) rows.Add(Row("Area", Id(i + 1), "Angle", SapSession.R(mesh.Areas[i].Angle), "AdvanceAxes", "No"));
        Table(L, "AREA LOCAL AXES ASSIGNMENTS 1 - TYPICAL", rows);

        rows = new List<string>();
        for (int i = 0; i < mesh.Lines.Count; i++) rows.Add(Row("Frame", Id(i + 1), "JointI", Id(mesh.Lines[i].I), "JointJ", Id(mesh.Lines[i].J), "IsCurved", "No"));
        Table(L, "CONNECTIVITY - FRAME", rows);
        rows = new List<string>();
        for (int i = 0; i < mesh.Lines.Count; i++)
            rows.Add(Row("Frame", Id(i + 1), "SectionType", "N.A.", "AutoSelect", "N.A.", "AnalSect", mesh.Lines[i].Section, "DesignSect", "N.A.", "MatProp", "Default"));
        Table(L, "FRAME SECTION ASSIGNMENTS", rows);
        List<string> fsp = Rows(s.TableRows("Frame Section Properties 01 - General"));
        if (fsp.Count == 0) fsp = FrameSectionRows(s);   // no DatabaseTables (SAP 22): the shapes SapToPluto draws
        Table(L, "FRAME SECTION PROPERTIES 01 - GENERAL", fsp);

        rows = new List<string>();
        foreach (Dictionary<string, string> r in s.TableRows("Frame Insertion Point Assignments"))
        {
            List<int> ids; string f;
            if (!r.TryGetValue("Frame", out f) || !mesh.LineObj.TryGetValue(f, out ids)) continue;
            foreach (int i in ids) { r["Frame"] = Id(i); rows.Add(Row(r)); }
        }
        Table(L, "FRAME INSERTION POINT ASSIGNMENTS", rows);

        // Restraints from the point objects (works without DatabaseTables).
        rows = new List<string>();
        {
            int n = 0; string[] pts = null;
            m.PointObj.GetNameList(ref n, ref pts);
            string[] dof = { "U1", "U2", "U3", "R1", "R2", "R3" };
            for (int k = 0; k < n; k++)
            {
                bool[] r = new bool[6];
                if (m.PointObj.GetRestraint(pts[k], ref r) != 0 || Array.IndexOf(r, true) < 0) continue;
                int i = mesh.PointOfObj(m, pts[k]);
                if (i <= 0) continue;
                List<string> kv = new List<string> { "Joint", Id(i) };
                for (int d = 0; d < 6; d++) { kv.Add(dof[d]); kv.Add(r[d] ? "Yes" : "No"); }
                rows.Add(Row(kv.ToArray()));
            }
        }
        Table(L, "JOINT RESTRAINT ASSIGNMENTS", rows);
        rows = new List<string>();
        foreach (KeyValuePair<int, double[]> kv in mesh.PointAxes)
            rows.Add(Row("Joint", Id(kv.Key), "AngleA", SapSession.R(kv.Value[0]), "AngleB", SapSession.R(kv.Value[1]), "AngleC", SapSession.R(kv.Value[2]), "AdvanceAxes", "No"));
        Table(L, "JOINT LOCAL AXES ASSIGNMENTS 1 - TYPICAL", rows);

        rows = new List<string>();
        foreach (string g in s.Groups())
        {
            if (string.Equals(g, "ALL", StringComparison.OrdinalIgnoreCase)) continue;
            int n = 0; int[] types = null; string[] objs = null;
            m.GroupDef.GetAssignments(g, ref n, ref types, ref objs);
            for (int k = 0; k < n; k++)
            {
                List<int> ids;
                if (types[k] == 1) { int i = mesh.PointOfObj(m, objs[k]); if (i > 0) rows.Add(Row("GroupName", g, "ObjectType", "Joint", "ObjectLabel", Id(i))); }
                else if (types[k] == 5 && mesh.AreaObj.TryGetValue(objs[k], out ids)) foreach (int i in ids) rows.Add(Row("GroupName", g, "ObjectType", "Area", "ObjectLabel", Id(i)));
                else if (types[k] == 2 && mesh.LineObj.TryGetValue(objs[k], out ids)) foreach (int i in ids) rows.Add(Row("GroupName", g, "ObjectType", "Frame", "ObjectLabel", Id(i)));
            }
        }
        Table(L, "GROUPS 2 - ASSIGNMENTS", rows);
        L.Add("END TABLE DATA");
        return string.Join("\n", L.ToArray()) + "\n";
    }

    // FRAME SECTION PROPERTIES 01 rows from PropFrame for the shapes SapToPluto draws (Box/Tube, Pipe,
    // Rectangular, I, Angle, Channel, Tee); any other type gets its name only (RECT placeholder).
    public static List<string> FrameSectionRows(SapSession s)
    {
        cSapModel m = s.Model;
        List<string> rows = new List<string>();
        int n = 0; string[] names = null;
        m.PropFrame.GetNameList(ref n, ref names);
        for (int i = 0; i < n; i++)
        {
            eFramePropType type = eFramePropType.I;
            m.PropFrame.GetTypeOAPI(names[i], ref type);
            string file = "", mat = "", notes = "", guid = ""; int color = 0;
            double t3 = 0, t2 = 0, tf = 0, tw = 0, t2b = 0, tfb = 0;
            string shape = null;
            if (type == eFramePropType.Box && m.PropFrame.GetTube(names[i], ref file, ref mat, ref t3, ref t2, ref tf, ref tw, ref color, ref notes, ref guid) == 0) shape = "Box/Tube";
            else if (type == eFramePropType.Pipe && m.PropFrame.GetPipe(names[i], ref file, ref mat, ref t3, ref tw, ref color, ref notes, ref guid) == 0) shape = "Pipe";
            else if (type == eFramePropType.Rectangular && m.PropFrame.GetRectangle(names[i], ref file, ref mat, ref t3, ref t2, ref color, ref notes, ref guid) == 0) shape = "Rectangular";
            else if (type == eFramePropType.I && m.PropFrame.GetISection(names[i], ref file, ref mat, ref t3, ref t2, ref tf, ref tw, ref t2b, ref tfb, ref color, ref notes, ref guid) == 0) shape = "I/Wide Flange";
            else if (type == eFramePropType.Angle && m.PropFrame.GetAngle(names[i], ref file, ref mat, ref t3, ref t2, ref tf, ref tw, ref color, ref notes, ref guid) == 0) shape = "Angle";
            else if (type == eFramePropType.Channel && m.PropFrame.GetChannel(names[i], ref file, ref mat, ref t3, ref t2, ref tf, ref tw, ref color, ref notes, ref guid) == 0) shape = "Channel";
            else if (type == eFramePropType.T && m.PropFrame.GetTee(names[i], ref file, ref mat, ref t3, ref t2, ref tf, ref tw, ref color, ref notes, ref guid) == 0) shape = "Tee";
            if (shape == null) { rows.Add(Row("SectionName", names[i], "Shape", type.ToString())); continue; }
            rows.Add(Row("SectionName", names[i], "Material", mat, "Shape", shape, "t3", SapSession.R(t3), "t2", SapSession.R(t2),
                "tf", SapSession.R(tf), "tw", SapSession.R(tw)));
        }
        return rows;
    }

    public static string LabelsCsv(Mesh mesh)
    {
        StringBuilder sb = new StringBuilder("kind,id,element,object\n");
        for (int i = 0; i < mesh.PointNames.Count; i++) sb.Append("joint,").Append(i + 1).Append(",\"").Append(mesh.PointNames[i]).Append("\",\n");
        for (int i = 0; i < mesh.Areas.Count; i++) sb.Append("area,").Append(i + 1).Append(",\"").Append(mesh.Areas[i].Elm).Append("\",\"").Append(mesh.Areas[i].Obj).Append("\"\n");
        for (int i = 0; i < mesh.Lines.Count; i++) sb.Append("frame,").Append(i + 1).Append(",\"").Append(mesh.Lines[i].Elm).Append("\",\"").Append(mesh.Lines[i].Obj).Append("\"\n");
        return sb.ToString();
    }

    static void Table(List<string> L, string name, List<string> rows)
    {
        if (rows.Count == 0) return;
        L.Add("TABLE:  \"" + name + "\""); L.AddRange(rows); L.Add("");
    }

    static List<string> Rows(List<Dictionary<string, string>> rs)
    {
        List<string> r = new List<string>();
        foreach (Dictionary<string, string> d in rs) r.Add(Row(d));
        return r;
    }

    static string Row(Dictionary<string, string> d)
    {
        StringBuilder sb = new StringBuilder();
        foreach (KeyValuePair<string, string> kv in d) sb.Append("   ").Append(kv.Key).Append('=').Append(SapSession.Q(kv.Value));
        return sb.ToString();
    }

    static string Row(params string[] kv)
    {
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i + 1 < kv.Length; i += 2) sb.Append("   ").Append(kv[i]).Append('=').Append(SapSession.Q(kv[i + 1]));
        return sb.ToString();
    }

    static string Id(int i) { return i.ToString(Inv); }
}
