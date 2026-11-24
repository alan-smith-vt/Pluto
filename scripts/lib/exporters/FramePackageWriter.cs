using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

// FramePackageWriter -- writes an AnlFrameModel as CalcFlow's analysis package: one folder of CSV files and a
// manifest, the single source every connection calc takes its inputs from. C# 5 (Windows PowerShell 5.1 Add-Type,
// warnings are errors). vault/calcflow/calcflow-staad-frame-reader.md.
//
//   manifest.json   source file + SHA-256, units, counts, analysis, problems
//   nodes.csv       Node, X/Y/Z (in)
//   members.csv     Member, NodeA, NodeB, Section, Family, Property, Beta, BetaKnown, ReleaseA, ReleaseB, OffsetA/B (in),
//                   Kind, Length (in), local x / y / z (unit vectors, global)
//   supports.csv    Node, Type, Restrained, Springs, Spec
//   loadcases.csv   LC, Kind, Method, LoadType, Title, Factors, HasForces
//   endforces.csv   Member, End (A start / B end), Node, LC, Fx..Mz: member local, STAAD's sign, the units in the header
//   ends.csv        the joint index: one row per member end at a joint (direction away from it, role, section, releases)
//   joints.csv      one row per joint: ends, columns / beams / braces, support
//   reactions.csv   Node, LC, Fx..Mz: support reactions as printed (global axes), the units in the header
//   displacements.csv  Node, LC, X Y Z (the length unit in the header), RX RY RZ (rad): joint displacements as printed
//   prints.csv      every output command of the file in order (PRINT ..., CHECK CODE ...), the LOAD LIST it ran under,
//                   whether its table is read into the package, and the rows read
//   codechecks.csv  one row per member per CHECK CODE command (Print = its Order in prints.csv): the code, the profile,
//                   PASS / FAIL / NOT DESIGNED, the ratio, the governing clause, case and location, the forces of that
//                   case as printed (with their units), STAAD's message
//   codecheckdetails.csv  the checks behind a result where TRACK printed them: demand, capacity, ratio, clause, case
//   memberinfo.csv  PRINT MEMBER INFORMATION: joints, length (the unit in the header), beta, releases as printed
//   memberproperties.csv  PRINT MEMBER PROPERTIES: AX IZ IY IX AY AZ SZ SY (the unit in the header)
//   sectiondisplacements.csv  PRINT SECTION DISPLACEMENTS: Member, LC, Station (0 = start joint), X Y Z global
//   sectionmaxima.csv  STAAD's largest local displacement of each member: where, in which case, and L / displacement
//
// A model with errors is refused: a package must be exactly what STAAD analysed. CSVs are UTF-8 with a BOM (Excel
// reads them as UTF-8), manifest.json without one; CRLF, invariant culture, each written to a temporary name and
// then moved into place.

public static class FramePackageWriter
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static readonly string[] Files = { "manifest.json", "nodes.csv", "members.csv", "supports.csv", "loadcases.csv",
        "endforces.csv", "ends.csv", "joints.csv", "prints.csv", "reactions.csv", "displacements.csv",
        "codechecks.csv", "codecheckdetails.csv", "memberinfo.csv", "memberproperties.csv", "sectiondisplacements.csv", "sectionmaxima.csv" };

    // writes the package into dir (created if needed); returns the paths written
    public static string[] Write(AnlFrameModel m, string dir)
    {
        if (m.ErrorCount > 0)
            throw new InvalidOperationException("The model has " + m.ErrorCount + " error(s); no package is written. First: " + m.ProblemsOf("Error")[0]);
        Directory.CreateDirectory(dir);
        var written = new List<string>();
        string fu = m.ForceUnitText, mu = m.MomentUnitText;

        written.Add(Csv(dir, "nodes.csv", new[] { "Node", "X (in)", "Y (in)", "Z (in)" },
            m.Nodes.Values.Select(n => new object[] { n.Id, n.X, n.Y, n.Z })));

        written.Add(Csv(dir, "members.csv", new[] { "Member", "NodeA", "NodeB", "Section", "Family", "Property", "Beta", "BetaKnown",
            "ReleaseA", "ReleaseB", "OffsetA (in)", "OffsetB (in)", "Kind", "Length (in)", "xX", "xY", "xZ", "yX", "yY", "yZ", "zX", "zY", "zZ" },
            m.Members.Values.Select(x => new object[] { x.Id, x.NodeA, x.NodeB, x.Section, x.Family, x.Property, x.Beta,
                x.BetaKnown ? "Y" : "N " + x.BetaSpec, x.ReleaseA.Text, x.ReleaseB.Text, Offset(x.OffsetA, x.OffsetALocal),
                Offset(x.OffsetB, x.OffsetBLocal), x.Kind, x.Length, V(x.LocalX, 0), V(x.LocalX, 1), V(x.LocalX, 2),
                V(x.LocalY, 0), V(x.LocalY, 1), V(x.LocalY, 2), V(x.LocalZ, 0), V(x.LocalZ, 1), V(x.LocalZ, 2) })));

        written.Add(Csv(dir, "supports.csv", new[] { "Node", "Type", "Restrained", "Springs", "Spec" },
            m.Supports.Values.Select(s => new object[] { s.Node, s.Type, s.RestrainedText, s.Springs, s.Spec })));

        written.Add(Csv(dir, "loadcases.csv", new[] { "LC", "Kind", "Method", "LoadType", "Title", "Factors", "HasForces" },
            m.LoadCases.Values.Select(c => new object[] { c.Id, c.Kind, c.Method, c.LoadType, c.Title, c.FactorsText, c.HasForces ? "Y" : "N" })));

        written.Add(Csv(dir, "endforces.csv", new[] { "Member", "End", "Node", "LC",
            "Fx (" + fu + ")", "Fy (" + fu + ")", "Fz (" + fu + ")", "Mx (" + mu + ")", "My (" + mu + ")", "Mz (" + mu + ")" },
            m.EndForces.OrderBy(f => f.Member).ThenBy(f => f.LC).ThenBy(f => f.End, StringComparer.Ordinal)
                .Select(f => new object[] { f.Member, f.End, f.Node, f.LC, f.F[0], f.F[1], f.F[2], f.F[3], f.F[4], f.F[5] })));

        written.Add(Csv(dir, "ends.csv", new[] { "Node", "Member", "End", "Role", "Section", "Family", "Kind", "Released",
            "DirX", "DirY", "DirZ", "yX", "yY", "yZ", "zX", "zY", "zZ", "ThroughMember", "ThroughEnd" },
            m.Joints.Values.SelectMany(j => j.Ends).Select(e => new object[] { e.Node, e.Member, e.End, e.Role, e.Section, e.Family,
                e.Kind, e.Released, V(e.Dir, 0), V(e.Dir, 1), V(e.Dir, 2), V(e.LocalY, 0), V(e.LocalY, 1), V(e.LocalY, 2),
                V(e.LocalZ, 0), V(e.LocalZ, 1), V(e.LocalZ, 2), e.ThroughMember == 0 ? (object)"" : e.ThroughMember, e.ThroughEnd })));

        written.Add(Csv(dir, "joints.csv", new[] { "Node", "X (in)", "Y (in)", "Z (in)", "Ends", "Columns", "Beams", "Braces", "Support" },
            m.Joints.Values.Select(j =>
            {
                AnlFrameNode n;
                m.Nodes.TryGetValue(j.Node, out n);
                return new object[] { j.Node, n == null ? (object)"" : n.X, n == null ? (object)"" : n.Y, n == null ? (object)"" : n.Z,
                    j.Ends.Count, j.Columns, j.Beams, j.Braces, j.Support == null ? "" : j.Support.Type };
            })));

        written.Add(Csv(dir, "prints.csv", new[] { "Order", "Command", "LoadList", "Cases", "Read", "Rows" },
            m.Prints.Select(p => new object[] { p.Order, p.Command, p.LoadList,
                p.AllCases ? "ALL" : string.Join(" ", p.Cases.Select(c => c.ToString(Inv)).ToArray()), p.Read ? "Y" : "N", p.Rows })));

        string rf = m.ReactionForceText.Length > 0 ? m.ReactionForceText : fu, rm = m.ReactionMomentText.Length > 0 ? m.ReactionMomentText : mu;
        written.Add(Csv(dir, "reactions.csv", new[] { "Node", "LC", "Fx (" + rf + ")", "Fy (" + rf + ")", "Fz (" + rf + ")", "Mx (" + rm + ")", "My (" + rm + ")", "Mz (" + rm + ")" },
            m.Reactions.OrderBy(x => x.Node).ThenBy(x => x.LC).Select(x => new object[] { x.Node, x.LC, x.V[0], x.V[1], x.V[2], x.V[3], x.V[4], x.V[5] })));

        string dl = m.DisplacementLengthText.Length > 0 ? m.DisplacementLengthText : "in";
        written.Add(Csv(dir, "displacements.csv", new[] { "Node", "LC", "X (" + dl + ")", "Y (" + dl + ")", "Z (" + dl + ")", "RX (rad)", "RY (rad)", "RZ (rad)" },
            m.Displacements.OrderBy(x => x.Node).ThenBy(x => x.LC).Select(x => new object[] { x.Node, x.LC, x.V[0], x.V[1], x.V[2], x.V[3], x.V[4], x.V[5] })));

        written.Add(Csv(dir, "codechecks.csv", new[] { "Print", "Member", "Code", "Profile", "Table", "Status", "Ratio", "Critical", "LC", "Location",
            "Axial", "FX", "FY", "FZ", "MX", "MY", "MZ", "Force unit", "Length unit", "Starred", "Note" },
            m.CodeChecks.Select(c => new object[] { c.Print, c.Member, c.Code, c.Profile, c.Table, c.Status, c.HasRatio ? (object)c.Ratio : "", c.Critical,
                c.LC == 0 ? (object)"" : c.LC, c.Status == "NOT DESIGNED" ? (object)"" : c.Location, c.Axial,
                Opt(c, 0), Opt(c, 1), Opt(c, 2), Opt(c, 3), Opt(c, 4), Opt(c, 5), c.ForceText, c.LengthText, c.Starred ? "Y" : "", c.Note })));

        written.Add(Csv(dir, "codecheckdetails.csv", new[] { "Print", "Member", "Group", "Check", "Demand", "Capacity", "Ratio", "Reference", "LC", "Location" },
            m.CodeCheckDetails.Select(d => new object[] { d.Print, d.Member, d.Group, d.Check, d.HasDemand ? (object)d.Demand : "", d.HasDemand ? (object)d.Capacity : "",
                d.Ratio, d.Reference, d.LC, d.Location })));

        string il = m.MemberInfoLengthText.Length > 0 ? m.MemberInfoLengthText : "in";
        written.Add(Csv(dir, "memberinfo.csv", new[] { "Member", "Start joint", "End joint", "Length (" + il + ")", "Beta (deg)", "Releases" },
            m.MemberInfo.OrderBy(x => x.Member).Select(x => new object[] { x.Member, x.StartJoint, x.EndJoint, x.Length, x.HasBeta ? (object)x.Beta : "", x.Releases })));

        string pl = m.MemberPropertyLengthText.Length > 0 ? m.MemberPropertyLengthText : "in";
        var propHead = new List<string> { "Member", "Profile" };
        for (int k = 0; k < 8; k++) propHead.Add(AnlFrameMemberProperty.Names[k] + " (" + pl + "^" + AnlFrameMemberProperty.Powers[k] + ")");
        written.Add(Csv(dir, "memberproperties.csv", propHead.ToArray(),
            m.MemberProperties.OrderBy(x => x.Member).Select(x => new object[] { x.Member, x.Profile, x.V[0], x.V[1], x.V[2], x.V[3], x.V[4], x.V[5], x.V[6], x.V[7] })));

        string sl = m.SectionDisplacementLengthText.Length > 0 ? m.SectionDisplacementLengthText : "in";
        written.Add(Csv(dir, "sectiondisplacements.csv", new[] { "Member", "LC", "Station", "X (" + sl + ")", "Y (" + sl + ")", "Z (" + sl + ")" },
            m.SectionDisplacements.OrderBy(x => x.Member).ThenBy(x => x.LC).SelectMany(x =>
                Enumerable.Range(0, x.V.Length / 3).Select(i => new object[] { x.Member, x.LC, i, x.V[3 * i], x.V[3 * i + 1], x.V[3 * i + 2] }))));

        written.Add(Csv(dir, "sectionmaxima.csv", new[] { "Member", "Max local displacement (" + sl + ")", "At (" + sl + ")", "LC", "L/Disp" },
            m.SectionMaxima.OrderBy(x => x.Member).Select(x => new object[] { x.Member, x.Disp, x.At, x.LC, x.HasRatio ? (object)x.LOverDisp : "" })));

        written.Insert(0, Manifest(dir, m));
        return written.ToArray();
    }

    static object V(double[] v, int i) { return v == null ? (object)"" : v[i]; }

    static object Opt(AnlFrameCodeCheck c, int k) { return c.Has[k] ? (object)c.F[k] : ""; }

    static string Offset(double[] v, bool local)
    {
        if (v == null) return "";
        return Num(v[0]) + " " + Num(v[1]) + " " + Num(v[2]) + (local ? " LOCAL" : "");
    }

    static string Num(double d) { return (d + 0.0).ToString("R", Inv); }

    static string Field(object v)
    {
        string s;
        if (v == null) s = "";
        else if (v is double) s = Num((double)v);
        else if (v is float) s = (((float)v) + 0.0f).ToString("R", Inv);
        else s = Convert.ToString(v, Inv);
        if (s.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 || (s.Length > 0 && (s[0] == ' ' || s[s.Length - 1] == ' ')))
            s = "\"" + s.Replace("\"", "\"\"") + "\"";
        return s;
    }

    static string Csv(string dir, string name, string[] header, IEnumerable<object[]> rows)
    {
        var sb = new StringBuilder();
        sb.Append(string.Join(",", header.Select(h => Field(h)).ToArray())).Append("\r\n");
        foreach (object[] r in rows) sb.Append(string.Join(",", r.Select(Field).ToArray())).Append("\r\n");
        return Save(dir, name, sb.ToString(), true);
    }

    // CSVs carry a BOM (so Excel reads them as UTF-8); manifest.json does not (JSON readers may refuse one)
    static string Save(string dir, string name, string text, bool bom)
    {
        string path = Path.Combine(dir, name);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, text, new UTF8Encoding(bom));
        if (File.Exists(path)) File.Delete(path);
        File.Move(tmp, path);
        return path;
    }

    // ---- manifest.json (written by hand: no serializer dependency) ----

    static string Manifest(string dir, AnlFrameModel m)
    {
        var sb = new StringBuilder();
        sb.Append("{\r\n");
        Prop(sb, "schema", "calcflow-analysis-package/1", true);
        Prop(sb, "source", Path.GetFileName(m.Source), true);
        Prop(sb, "sourceSha256", m.SourceSha256, true);
        Prop(sb, "written", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", Inv), true);
        Prop(sb, "geometryUnits", "in", true);
        Prop(sb, "forceUnit", m.ForceUnitText, true);
        Prop(sb, "momentUnit", m.MomentUnitText, true);
        Prop(sb, "endForces", "member local axes, STAAD's sign (the force on the member at its end), as printed", true);
        Prop(sb, "analysis", m.Analysis, true);
        Prop(sb, "propertyCountry", m.PropertyCountry, true);
        Prop(sb, "sectionStations", m.SectionStations.ToString(Inv), true);
        sb.Append("  \"counts\": { ");
        sb.Append("\"nodes\": ").Append(m.Nodes.Count).Append(", ");
        sb.Append("\"members\": ").Append(m.Members.Count).Append(", ");
        sb.Append("\"supports\": ").Append(m.Supports.Count).Append(", ");
        sb.Append("\"loadCases\": ").Append(m.LoadCases.Count).Append(", ");
        sb.Append("\"endForces\": ").Append(m.EndForces.Count).Append(", ");
        sb.Append("\"joints\": ").Append(m.Joints.Count).Append(", ");
        sb.Append("\"reactions\": ").Append(m.Reactions.Count).Append(", ");
        sb.Append("\"displacements\": ").Append(m.Displacements.Count).Append(", ");
        sb.Append("\"codeChecks\": ").Append(m.CodeChecks.Count).Append(", ");
        sb.Append("\"sectionDisplacements\": ").Append(m.SectionDisplacements.Count).Append(" },\r\n");
        sb.Append("  \"problems\": [");
        for (int i = 0; i < m.Problems.Count; i++)
        {
            AnlFrameProblem p = m.Problems[i];
            sb.Append(i == 0 ? "\r\n" : ",\r\n");
            sb.Append("    { \"severity\": ").Append(Js(p.Severity)).Append(", \"code\": ").Append(Js(p.Code))
              .Append(", \"message\": ").Append(Js(p.Message)).Append(" }");
        }
        sb.Append(m.Problems.Count > 0 ? "\r\n  ]\r\n" : "]\r\n");
        sb.Append("}\r\n");
        return Save(dir, "manifest.json", sb.ToString(), false);
    }

    static void Prop(StringBuilder sb, string name, string value, bool comma)
    {
        sb.Append("  ").Append(Js(name)).Append(": ").Append(Js(value)).Append(comma ? ",\r\n" : "\r\n");
    }

    static string Js(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (char c in s ?? "")
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20 || c > 0x7e) sb.Append("\\u").Append(((int)c).ToString("x4", Inv));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }
}
