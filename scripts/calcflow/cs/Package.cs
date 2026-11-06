// Package.cs -- the analysis package read back from its folder (manifest.json and the CSV files that
// FramePackageWriter writes from a STAAD .anl). CalcFlow reads only the package, never the model file: another source
// (the pipeline that will build STAAD models from the plant data) only has to write the same files.
// C# 5 (Windows PowerShell 5.1 Add-Type, warnings are errors).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace CalcFlow
{
    public sealed class PkgMember
    {
        public int Id, NodeA, NodeB;
        public string Section = "", Family = "", Property = "", ReleaseA = "", ReleaseB = "", Kind = "";
        public double Beta, Length;
        public bool BetaKnown = true;
    }

    public sealed class PkgEnd
    {
        public int Node, Member;
        public string End = "", Role = "", Section = "", Family = "", Kind = "", Released = "";
        public double[] Dir = new double[3];
        public int ThroughMember;
        public string Key { get { return AnalysisPackage.EndKey(Node, Member, End); } }
    }

    public sealed class PkgJoint
    {
        public int Node;
        public double X, Y, Z;
        public string Support = "";
        public readonly List<PkgEnd> Ends = new List<PkgEnd>();
    }

    public sealed class PkgCase
    {
        public int Id;
        public string Kind = "", Method = "", LoadType = "", Title = "";
        public bool HasForces;
    }

    public sealed class AnalysisPackage
    {
        public string Folder = "", Source = "", SourceSha256 = "", Schema = "";
        public Unit ForceUnit, MomentUnit;
        public readonly Dictionary<int, PkgMember> Members = new Dictionary<int, PkgMember>();
        public readonly SortedDictionary<int, PkgJoint> Joints = new SortedDictionary<int, PkgJoint>();
        public readonly Dictionary<string, PkgEnd> Ends = new Dictionary<string, PkgEnd>(StringComparer.Ordinal);
        public readonly SortedDictionary<int, PkgCase> Cases = new SortedDictionary<int, PkgCase>();
        // "member|end" -> case -> Fx Fy Fz Mx My Mz (member local, STAAD's sign, ForceUnit / MomentUnit)
        readonly Dictionary<string, SortedDictionary<int, double[]>> _forces = new Dictionary<string, SortedDictionary<int, double[]>>(StringComparer.Ordinal);

        public static string EndKey(int node, int member, string end)
        {
            return node.ToString(CultureInfo.InvariantCulture) + "|" + member.ToString(CultureInfo.InvariantCulture) + "|" + (end ?? "").ToUpperInvariant();
        }

        public PkgEnd End(int node, int member, string end)
        {
            PkgEnd e;
            return Ends.TryGetValue(EndKey(node, member, end), out e) ? e : null;
        }

        // the end of a member at a joint (null when the member does not end there)
        public PkgEnd EndAt(int node, int member)
        {
            PkgJoint j;
            if (!Joints.TryGetValue(node, out j)) return null;
            return j.Ends.FirstOrDefault(e => e.Member == member);
        }

        // the printed end forces of a member end, by case (empty when none)
        public SortedDictionary<int, double[]> Forces(int member, string end)
        {
            SortedDictionary<int, double[]> d;
            return _forces.TryGetValue(member.ToString(CultureInfo.InvariantCulture) + "|" + (end ?? "").ToUpperInvariant(), out d) ? d : new SortedDictionary<int, double[]>();
        }

        public static AnalysisPackage Load(string folder)
        {
            var p = new AnalysisPackage();
            p.Folder = Path.GetFullPath(folder);
            string man = Path.Combine(p.Folder, "manifest.json");
            if (!File.Exists(man)) throw new FileNotFoundException("Not an analysis package (no manifest.json): " + p.Folder, man);
            string json = File.ReadAllText(man);
            p.Schema = Js(json, "schema"); p.Source = Js(json, "source"); p.SourceSha256 = Js(json, "sourceSha256");
            if (!p.Schema.StartsWith("calcflow-analysis-package/", StringComparison.Ordinal))
                throw new Exception("Unknown package schema '" + p.Schema + "' in " + man);

            foreach (var r in Csv.ReadTable(Path.Combine(p.Folder, "members.csv")))
            {
                var m = new PkgMember();
                m.Id = Int(r, "Member"); m.NodeA = Int(r, "NodeA"); m.NodeB = Int(r, "NodeB");
                m.Section = V(r, "Section"); m.Family = V(r, "Family"); m.Property = V(r, "Property");
                m.ReleaseA = V(r, "ReleaseA"); m.ReleaseB = V(r, "ReleaseB"); m.Kind = V(r, "Kind");
                m.Beta = Num(r, "Beta"); m.Length = Num(r, "Length (in)");
                m.BetaKnown = !V(r, "BetaKnown").StartsWith("N", StringComparison.OrdinalIgnoreCase);
                p.Members[m.Id] = m;
            }
            foreach (var r in Csv.ReadTable(Path.Combine(p.Folder, "joints.csv")))
            {
                var j = new PkgJoint();
                j.Node = Int(r, "Node"); j.X = Num(r, "X (in)"); j.Y = Num(r, "Y (in)"); j.Z = Num(r, "Z (in)"); j.Support = V(r, "Support");
                p.Joints[j.Node] = j;
            }
            foreach (var r in Csv.ReadTable(Path.Combine(p.Folder, "ends.csv")))
            {
                var e = new PkgEnd();
                e.Node = Int(r, "Node"); e.Member = Int(r, "Member"); e.End = V(r, "End").ToUpperInvariant(); e.Role = V(r, "Role");
                e.Section = V(r, "Section"); e.Family = V(r, "Family"); e.Kind = V(r, "Kind"); e.Released = V(r, "Released");
                e.Dir = new[] { Num(r, "DirX"), Num(r, "DirY"), Num(r, "DirZ") };
                string tm = V(r, "ThroughMember");
                int t;
                if (tm.Length > 0 && int.TryParse(tm, NumberStyles.Integer, CultureInfo.InvariantCulture, out t)) e.ThroughMember = t;
                p.Ends[e.Key] = e;
                PkgJoint j;
                if (!p.Joints.TryGetValue(e.Node, out j)) { j = new PkgJoint(); j.Node = e.Node; p.Joints[e.Node] = j; }
                j.Ends.Add(e);
            }
            foreach (var r in Csv.ReadTable(Path.Combine(p.Folder, "loadcases.csv")))
            {
                var c = new PkgCase();
                c.Id = Int(r, "LC"); c.Kind = V(r, "Kind"); c.Method = V(r, "Method"); c.LoadType = V(r, "LoadType"); c.Title = V(r, "Title");
                c.HasForces = V(r, "HasForces").StartsWith("Y", StringComparison.OrdinalIgnoreCase);
                p.Cases[c.Id] = c;
            }
            List<string[]> f = Csv.ReadAll(Path.Combine(p.Folder, "endforces.csv"));
            if (f.Count > 0)
            {
                string[] h = f[0];
                p.ForceUnit = UnitInHeader(h, 4); p.MomentUnit = UnitInHeader(h, 7);
                for (int i = 1; i < f.Count; i++)
                {
                    string[] r = f[i];
                    if (r.Length < 10) continue;
                    var v = new double[6];
                    for (int k = 0; k < 6; k++) v[k] = double.Parse(r[4 + k], NumberStyles.Float, CultureInfo.InvariantCulture);
                    string key = r[0].Trim() + "|" + r[1].Trim().ToUpperInvariant();
                    SortedDictionary<int, double[]> d;
                    if (!p._forces.TryGetValue(key, out d)) { d = new SortedDictionary<int, double[]>(); p._forces[key] = d; }
                    d[int.Parse(r[3], NumberStyles.Integer, CultureInfo.InvariantCulture)] = v;
                }
            }
            return p;
        }

        // "Fx (kip)" -> kip, "Mx (kip-ft)" -> kip-ft
        static Unit UnitInHeader(string[] h, int i)
        {
            if (i >= h.Length) throw new Exception("endforces.csv has no column " + (i + 1));
            Match m = Regex.Match(h[i], @"\(([^)]+)\)");
            Unit u;
            if (!m.Success || !Units.TryParse(m.Groups[1].Value, out u)) throw new Exception("endforces.csv: no readable unit in '" + h[i] + "'");
            return u;
        }

        static string Js(string json, string key)
        {
            Match m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            return m.Success ? Regex.Unescape(m.Groups[1].Value) : "";
        }

        static string V(Dictionary<string, string> r, string k) { string v; return r.TryGetValue(k, out v) ? (v ?? "").Trim() : ""; }

        static int Int(Dictionary<string, string> r, string k)
        {
            int v;
            return int.TryParse(V(r, k), NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : 0;
        }

        static double Num(Dictionary<string, string> r, string k)
        {
            double v;
            return double.TryParse(V(r, k), NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : 0;
        }
    }
}
