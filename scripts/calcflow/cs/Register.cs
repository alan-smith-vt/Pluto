// Register.cs -- the connection register (Connection_Register.xlsx), the one workbook the engineer edits per project:
//   tblConnections  one row per member end at a joint (the key: Node, Member, End). Blue: Type (the connection type
//                   from the calc library; blank = no calc), Support (the member it frames into: the type's "support"
//                   slot), Other (more slots, "brace=12:A; gusset=30"), Group (connections that share one calc, on
//                   their envelope), Disposition, Comment. Grey: what the tool writes (role, section, status, DCRs).
//   tblOverrides    Connection (or a Group), Step (blank = every step), Param, Value: a parameter for one connection
//   tblCombos       the load cases in scope: Use Y / N, Basis LRFD / ASD (seeded from the package)
//   tblSettings     DcrLimit, GoverningK ...
// New writes a fresh register from a package (no Excel; an existing file is never overwritten: once the engineer has
// saved it, only Excel writes it). Load reads it without Excel. C# 5 (Windows PowerShell 5.1 Add-Type, warnings are
// errors).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace CalcFlow
{
    public static class RegisterSchema
    {
        public static readonly string[] ConnectionColumns = { "Connection", "Node", "Member", "End", "Type", "Support", "Other", "Group", "Disposition", "Comment",
            "Role", "Section", "Status", "Status Detail", "Max DCR", "Governing Check", "Governing Combo", "Calc", "Design Rev" };
        public static readonly HashSet<string> ConnectionInputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Type", "Support", "Other", "Group", "Disposition", "Comment" };
        public static readonly string[] OverrideColumns = { "Connection", "Step", "Param", "Value", "Note" };
        public static readonly string[] ComboColumns = { "LC", "Title", "Kind", "Use", "Basis" };
        public static readonly HashSet<string> ComboInputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Use", "Basis" };
        public static readonly string[] SettingColumns = { "Setting", "Value", "Notes" };
        public static readonly string[] Dispositions = { "Excluded", "Accepted", "Attention", "Hold" };

        public static string ConnectionId(int node, int member, string end)
        {
            return "J" + node.ToString(CultureInfo.InvariantCulture) + "-M" + member.ToString(CultureInfo.InvariantCulture) + (end ?? "").ToUpperInvariant();
        }
    }

    public sealed class RegConnection
    {
        public string Connection = "", End = "", Type = "", Other = "", Group = "", Disposition = "", Comment = "";
        public int Node, Member, Support, Row;
        public Dictionary<string, string> All = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public string Key { get { return AnalysisPackage.EndKey(Node, Member, End); } }
    }

    public sealed class RegOverride
    {
        public string Connection = "", Step = "", Param = "", Value = "";
    }

    public sealed class RegCombo
    {
        public int LC;
        public bool Use;
        public string Basis = "";
    }

    public sealed class Register
    {
        public string Path = "";
        public readonly List<RegConnection> Connections = new List<RegConnection>();
        public readonly List<RegOverride> Overrides = new List<RegOverride>();
        public readonly SortedDictionary<int, RegCombo> Combos = new SortedDictionary<int, RegCombo>();
        public readonly Dictionary<string, string> Settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> Errors = new List<string>();

        public string Setting(string name, string dflt)
        {
            string v;
            return Settings.TryGetValue(name, out v) && v.Length > 0 ? v : dflt;
        }

        public static Register Load(string path)
        {
            var reg = new Register();
            reg.Path = System.IO.Path.GetFullPath(path);
            XWorkbook wb = Xlsx.Read(path);
            List<Dictionary<string, string>> rows = wb.TableRows("tblConnections");
            if (rows == null) { reg.Errors.Add("the register has no table tblConnections"); return reg; }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var r in rows)
            {
                var c = new RegConnection();
                c.All = r;
                c.Row = Int(V(r, "__row"));
                c.Node = Int(V(r, "Node")); c.Member = Int(V(r, "Member")); c.End = V(r, "End").ToUpperInvariant();
                c.Type = V(r, "Type"); c.Other = V(r, "Other"); c.Group = V(r, "Group"); c.Disposition = V(r, "Disposition"); c.Comment = V(r, "Comment");
                string sup = V(r, "Support");
                if (sup.Length > 0 && !int.TryParse(sup, NumberStyles.Integer, CultureInfo.InvariantCulture, out c.Support))
                    reg.Errors.Add("row " + c.Row + ": Support must be a member number ('" + sup + "')");
                c.Connection = V(r, "Connection");
                if (c.Connection.Length == 0) c.Connection = RegisterSchema.ConnectionId(c.Node, c.Member, c.End);
                if (c.Node <= 0 || c.Member <= 0 || (c.End != "A" && c.End != "B")) { reg.Errors.Add("row " + c.Row + ": Node, Member and End (A or B) identify the member end"); continue; }
                if (c.Disposition.Length > 0 && !RegisterSchema.Dispositions.Contains(c.Disposition, StringComparer.OrdinalIgnoreCase))
                    reg.Errors.Add("row " + c.Row + " (" + c.Connection + "): Disposition must be blank or one of " + string.Join(", ", RegisterSchema.Dispositions));
                if (!seen.Add(c.Key)) { reg.Errors.Add("row " + c.Row + ": " + c.Connection + " is listed twice"); continue; }
                reg.Connections.Add(c);
            }
            foreach (var r in wb.TableRows("tblOverrides") ?? new List<Dictionary<string, string>>())
            {
                var o = new RegOverride();
                o.Connection = V(r, "Connection"); o.Step = V(r, "Step"); o.Param = V(r, "Param"); o.Value = V(r, "Value");
                if (o.Connection.Length == 0 || o.Param.Length == 0) { reg.Errors.Add("tblOverrides row " + V(r, "__row") + ": Connection and Param are needed"); continue; }
                reg.Overrides.Add(o);
            }
            foreach (var r in wb.TableRows("tblCombos") ?? new List<Dictionary<string, string>>())
            {
                var c = new RegCombo();
                c.LC = Int(V(r, "LC"));
                if (c.LC <= 0) continue;
                c.Use = V(r, "Use").StartsWith("Y", StringComparison.OrdinalIgnoreCase);
                c.Basis = V(r, "Basis").ToUpperInvariant();
                if (c.Basis.Length > 0 && c.Basis != "LRFD" && c.Basis != "ASD") reg.Errors.Add("tblCombos LC " + c.LC + ": Basis must be LRFD, ASD or blank");
                reg.Combos[c.LC] = c;
            }
            foreach (var r in wb.TableRows("tblSettings") ?? new List<Dictionary<string, string>>())
                if (V(r, "Setting").Length > 0) reg.Settings[V(r, "Setting")] = V(r, "Value");
            return reg;
        }

        static string V(Dictionary<string, string> r, string k) { string v; return r.TryGetValue(k, out v) ? (v ?? "").Trim() : ""; }

        static int Int(string s)
        {
            int v;
            return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : 0;
        }

        // ---- a new register from a package ----

        // the member a connection most likely frames into: at the joint, a column for a beam or a brace, else a beam;
        // never its own continuation (the collinear member of the same section). 0 = none (e.g. a column at a support).
        public static int GuessSupport(AnalysisPackage pkg, PkgEnd e)
        {
            PkgJoint j;
            if (!pkg.Joints.TryGetValue(e.Node, out j)) return 0;
            var others = j.Ends.Where(o => o.Member != e.Member && o.Member != e.ThroughMember).ToList();
            if (others.Count == 0) return 0;
            string[] prefer = e.Role == "Column" ? new[] { "Beam" } : new[] { "Column", "Beam" };
            foreach (string role in prefer)
            {
                // a member that runs through the joint (it has a collinear partner) first, then the lowest number
                var c = others.Where(o => o.Role == role).OrderBy(o => o.ThroughMember != 0 ? 0 : 1).ThenBy(o => o.Member).FirstOrDefault();
                if (c != null) return c.Member;
            }
            return 0;
        }

        public static void WriteNew(string path, AnalysisPackage pkg, Library lib)
        {
            if (File.Exists(path)) throw new IOException("The register " + path + " exists: it is the engineer's file and is never overwritten. Choose another name.");
            var conns = new List<Dictionary<string, string>>();
            foreach (PkgJoint j in pkg.Joints.Values)
                foreach (PkgEnd e in j.Ends.OrderBy(x => x.Member))
                {
                    var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (string c in RegisterSchema.ConnectionColumns) d[c] = "";
                    d["Connection"] = RegisterSchema.ConnectionId(e.Node, e.Member, e.End);
                    d["Node"] = e.Node.ToString(CultureInfo.InvariantCulture); d["Member"] = e.Member.ToString(CultureInfo.InvariantCulture); d["End"] = e.End;
                    int s = GuessSupport(pkg, e);
                    d["Support"] = s > 0 ? s.ToString(CultureInfo.InvariantCulture) : "";
                    d["Role"] = e.Role + (j.Support.Length > 0 ? " at a " + j.Support + " support" : "");
                    d["Section"] = e.Section;
                    conns.Add(d);
                }
            bool anyCombos = pkg.Cases.Values.Any(c => c.Kind == "COMBINATION" || c.Kind == "REPEAT");
            var combos = pkg.Cases.Values.Where(c => c.HasForces).Select(c =>
            {
                var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                d["LC"] = c.Id.ToString(CultureInfo.InvariantCulture); d["Title"] = c.Title; d["Kind"] = c.Kind + (c.Method.Length > 0 ? " " + c.Method : "");
                d["Use"] = !anyCombos || c.Kind == "COMBINATION" || c.Kind == "REPEAT" ? "Y" : "N";
                d["Basis"] = "";
                return d;
            }).ToList();
            var settings = new List<Dictionary<string, string>>();
            Action<string, string, string> set = (k, v, n) =>
            {
                var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                d["Setting"] = k; d["Value"] = v; d["Notes"] = n;
                settings.Add(d);
            };
            set("DcrLimit", "1.0", "a DCR above this needs attention");
            set("GoverningK", "5", "governing_set: the number of cases added to the concurrent envelope by the normalised sum");
            set("Package", pkg.Folder, "the analysis package this register was seeded from (information)");
            WriteTables(path, conns, new List<Dictionary<string, string>>(), combos, settings, lib == null ? null : lib.Types.Keys.ToList());
        }

        // the register's four tables, as written by WriteNew (and by tests that need a filled-in register)
        public static void WriteTables(string path, List<Dictionary<string, string>> conns, List<Dictionary<string, string>> overrides,
            List<Dictionary<string, string>> combos, List<Dictionary<string, string>> settings, List<string> types)
        {
            if (File.Exists(path)) throw new IOException("The register " + path + " exists: it is the engineer's file and is never overwritten. Choose another name.");
            var sheets = new List<XOutSheet>();
            XOutSheet cs = WorkbookTables.Sheet("Connections", "tblConnections", RegisterSchema.ConnectionColumns, conns,
                "one row per member end at a joint. Type: the connection type (blank = no calc). Support: the member it frames into. Group: connections that share one calc. Blue columns are yours; the tool writes the grey ones.",
                c => RegisterSchema.ConnectionInputs.Contains(c));
            int last = conns.Count + 2 + 1000;
            Action<XOutSheet, string[], string, string> list = (sh, cols, col, values) =>
            {
                int k = Array.IndexOf(cols, col);
                if (k < 0) return;
                var v = new XValidation();
                v.Sqref = Xlsx.ColName(k) + "3:" + Xlsx.ColName(k) + last.ToString(CultureInfo.InvariantCulture);
                v.List = values; v.Strict = false;
                sh.Validations.Add(v);
            };
            if (types != null && types.Count > 0) list(cs, RegisterSchema.ConnectionColumns, "Type", "\"" + string.Join(",", types.OrderBy(x => x, NaturalComparer.Instance)) + "\"");
            list(cs, RegisterSchema.ConnectionColumns, "Disposition", "\"" + string.Join(",", RegisterSchema.Dispositions) + "\"");
            cs.FreezeCols = 4;
            sheets.Add(cs);
            sheets.Add(WorkbookTables.Sheet("Overrides", "tblOverrides", RegisterSchema.OverrideColumns, overrides,
                "a parameter for one connection or a Group (Step blank = every step); it wins over the type's default in the calc library", c => true));
            XOutSheet cb = WorkbookTables.Sheet("Combos", "tblCombos", RegisterSchema.ComboColumns, combos,
                "the load cases the calcs take: Use Y / N; Basis LRFD / ASD (a template with a Basis takes only its own)", c => RegisterSchema.ComboInputs.Contains(c));
            list(cb, RegisterSchema.ComboColumns, "Use", "\"Y,N\""); list(cb, RegisterSchema.ComboColumns, "Basis", "\"LRFD,ASD\"");
            sheets.Add(cb);
            sheets.Add(WorkbookTables.Sheet("Settings", "tblSettings", RegisterSchema.SettingColumns, settings, "project settings", c => c != "Notes"));
            XlsxWriter.Write(path, sheets, null);
        }
    }
}
