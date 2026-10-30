// Glyph looks for SapToPluto's sidecar groups, from an optional rules CSV (approved 2026-10-30, board laptop:690;
// schema laptop:697). C# 5. Generic: rules match group names, anchors come from the model (beam ends, releases,
// neighbouring beams); no domain words here. Domain maps live with their arms (e.g. scripts/duct/sap-looks.csv).
//   Group,Glyph,At,Tier,Label,Section,Color,Dir,Context
//   Group    group name or wildcard (* and ?), case-insensitive; the first matching row wins
//   At       members (glyph per member: centroid / node; no anchors written) | ends-I | ends-J | mid |
//            ends-released (the released end(s) of each beam, from FRAME RELEASE ASSIGNMENTS)
//   Dir      empty | adjacent: each beam anchor points along another beam (not in the group) meeting its end
//   Context  yes: tag the group "context" and give it no look
// Rows starting with # are comments. Groups no row matches are written as before.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

public class SapLooks
{
    public class Rule
    {
        public string Group, Glyph, At, Tier, Label, Section, Color, Dir;
        public bool Context;
        public Regex Match;
    }

    public readonly List<Rule> Rules = new List<Rule>();
    public int Applied, ContextGroups, AnchorCount;

    public static SapLooks Read(string path)
    {
        var res = new SapLooks();
        string[] lines = File.ReadAllLines(path);
        List<string> cols = null;
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;
            List<string> f = SplitCsv(line);
            if (cols == null) { cols = f.Select(c => c.Trim().ToLowerInvariant()).ToList(); continue; }
            Func<string, string> v = k => { int i = cols.IndexOf(k); return i >= 0 && i < f.Count ? f[i].Trim() : ""; };
            var r = new Rule
            {
                Group = v("group"), Glyph = v("glyph"), At = v("at").ToLowerInvariant(), Tier = v("tier"), Label = v("label"),
                Section = v("section"), Color = v("color"), Dir = v("dir").ToLowerInvariant(),
                Context = v("context").StartsWith("y", StringComparison.OrdinalIgnoreCase)
            };
            if (r.Group == "") continue;
            if (r.At == "") r.At = "members";
            if (!new[] { "members", "ends-i", "ends-j", "mid", "ends-released" }.Contains(r.At)) throw new Exception("SapLooks: At '" + r.At + "' (group " + r.Group + ")");
            r.Match = new Regex("^" + Regex.Escape(r.Group).Replace("\\*", ".*").Replace("\\?", ".") + "$", RegexOptions.IgnoreCase);
            res.Rules.Add(r);
        }
        return res;
    }

    public Rule Find(string group) { return Rules.FirstOrDefault(r => r.Match.IsMatch(group)); }

    // releasedEnds: beam id -> { I released, J released } (any DOF)
    public void Apply(FeaturesSidecar sc, Dictionary<int, RawViewerWriter.BeamMember> beams, Dictionary<int, Node> nodes, Dictionary<int, bool[]> releasedEnds)
    {
        // beams at each node, for Dir=adjacent
        var atNode = new Dictionary<int, List<int>>();
        foreach (KeyValuePair<int, RawViewerWriter.BeamMember> b in beams)
            foreach (int n in new[] { (int)b.Value.NodeA, (int)b.Value.NodeB })
            { List<int> l; if (!atNode.TryGetValue(n, out l)) atNode[n] = l = new List<int>(); l.Add(b.Key); }

        foreach (FeaturesSidecar.Group g in sc.Groups)
        {
            if (g.RawJson != null || g.Name == null) continue;
            Rule r = Find(g.Name);
            if (r == null) continue;
            if (r.Context) { if (!g.Tags.Contains("context")) g.Tags.Add("context"); ContextGroups++; continue; }
            var anchors = new List<Anchor>();
            if (r.At != "members")
            {
                var inGroup = new HashSet<int>();
                foreach (FeaturesSidecar.Member m in g.Members) if (m.Domain == "beams") foreach (uint id in m.Ids) inGroup.Add((int)id);
                foreach (int id in inGroup.OrderBy(i => i))
                {
                    RawViewerWriter.BeamMember bm;
                    if (!beams.TryGetValue(id, out bm)) continue;
                    var ts = new List<double>();
                    if (r.At == "ends-i") ts.Add(0);
                    else if (r.At == "ends-j") ts.Add(1);
                    else if (r.At == "mid") ts.Add(0.5);
                    else
                    {
                        bool[] e;
                        if (releasedEnds.TryGetValue(id, out e)) { if (e[0]) ts.Add(0); if (e[1]) ts.Add(1); }
                    }
                    foreach (double t in ts)
                    {
                        double[] dir = null;
                        if (r.Dir == "adjacent")
                        {
                            int end = t < 0.5 ? (int)bm.NodeA : (int)bm.NodeB;
                            List<int> others;
                            if (atNode.TryGetValue(end, out others))
                            {
                                int o = others.Where(x => x != id && !inGroup.Contains(x)).DefaultIfEmpty(-1).First();
                                if (o >= 0) dir = Axis(nodes[(int)beams[o].NodeA], nodes[(int)beams[o].NodeB]);
                            }
                        }
                        anchors.Add(new Anchor { Domain = "beams", Id = (uint)id, T = t, Dir = dir });
                    }
                }
            }
            SetLook(g, r, r.At == "members" ? null : anchors);
            Applied++; AnchorCount += anchors.Count;
        }
    }

    public string Summary() { return string.Format("looks: {0} groups with a glyph ({1} anchors), {2} context groups", Applied, AnchorCount, ContextGroups); }

    public class Anchor { public string Domain; public uint Id; public double T; public double[] Dir; }

    // the one place that touches the sidecar writer's look / anchors API (FeaturesSidecar.cs, schema laptop:697)
    static void SetLook(FeaturesSidecar.Group g, Rule r, List<Anchor> anchors)
    {
        g.Look = new FeaturesSidecar.Look
        {
            Glyph = r.Glyph, Tier = r.Tier == "" ? null : r.Tier,
            Label = r.Label == "" ? null : r.Label, Section = r.Section == "" ? null : r.Section
        };
        if (r.Color != "") g.Color = r.Color;
        g.Anchors.Clear();
        if (anchors == null) return;
        foreach (Anchor a in anchors) g.Anchors.Add(new FeaturesSidecar.Anchor { Domain = a.Domain, Id = a.Id, T = a.T, Dir = a.Dir });
    }

    static double[] Axis(Node a, Node b)
    {
        double dx = b.xyz.X - a.xyz.X, dy = b.xyz.Y - a.xyz.Y, dz = b.xyz.Z - a.xyz.Z, l = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        return l < 1e-12 ? null : new[] { dx / l, dy / l, dz / l };
    }

    static List<string> SplitCsv(string line)
    {
        var f = new List<string>(); var cur = new System.Text.StringBuilder(); bool q = false;
        foreach (char ch in line)
        {
            if (ch == '"') q = !q;
            else if (ch == ',' && !q) { f.Add(cur.ToString()); cur.Length = 0; }
            else cur.Append(ch);
        }
        f.Add(cur.ToString());
        return f;
    }
}
