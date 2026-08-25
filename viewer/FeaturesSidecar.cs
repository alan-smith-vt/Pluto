// ================================================================
// FeaturesSidecar.cs
// Programmatic writer/reader for the Pluto features sidecar
// (*.features.json). Spec: vault/format/features-sidecar.md
//
// No package dependencies (hand-rolled JSON like RawViewerWriter) so it
// drops into the existing C# side as-is. Round-trips unknown sections
// verbatim so a file the viewer wrote keeps anything this code does not
// model (predicates, sectionCuts, ...).
//
// GROUPS are the SAME object the STAAD exporter consumes: a named list
// of member IDs. Predicates produce groups; STAAD groups are written
// from groups; `Color` is only a display attribute. So the pipeline is
//   predicates -> Group (ids) -> [viewer color] / [STAAD GROUP block]
// and nothing forks.
//
// Typical use (steel + pipes trib study):
//   var sc = new FeaturesSidecar { ModelId = "...", GeometryHash = hash };
//   sc.Units["length"] = "in"; sc.Units["force"] = "kip";
//   sc.AddGroup("W shapes", "#8c93a8", "beams", wIds, tags: new[]{"steel"});
//   sc.AddGroup("Pipe 8in insulated", "#e0913a", "beams", pipeIds, tags: new[]{"pipe","8","insulated"});
//   File.WriteAllText("model.features.json", sc.ToJson());
//
// GeometryHash: SHA-256 over the raw bytes of the v4 blocks
// NODE NDID ELEM ELID SECT BPRP in that order (domains ascending
// within each tag) -- use ComputeGeometryHash(...) with the exact byte
// arrays the binary writer emitted. Must match the viewer's hash for
// the file to bind.
// ================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

public class FeaturesSidecar
{
    public const string Format = "pluto-features";
    public const int EnvelopeVersion = 1;
    public const int GroupsVersion = 1;

    public string ModelId { get; set; }
    public string GeometryHash { get; set; }
    public Dictionary<string, string> Units { get; } = new Dictionary<string, string>();
    public List<Group> Groups { get; } = new List<Group>();

    // Sections we don't model, kept as raw JSON text: name -> json value.
    public Dictionary<string, string> PassThrough { get; } = new Dictionary<string, string>();

    public class Member
    {
        public string Domain;          // domain name from META.domains[].name ("shells", "beams") or family
        public List<uint> Ids = new List<uint>();       // real element IDs (per-domain namespace)
        public List<uint> NodeIds = new List<uint>();   // real node IDs (for node groups)
        public string PredicateId;     // runtime member: resolved from the predicate, no IDs stored
    }

    public class Group
    {
        public string Id = Guid.NewGuid().ToString("N");
        public string Name;
        public string Color;           // "#rrggbb" or null (viewer auto-assigns)
        public List<string> Tags = new List<string>();
        public List<Member> Members = new List<Member>();
        public string SourcePredicateId; // set when a predicate produced this group
        public string StaadName;         // name to use in the STAAD GROUP block; null = Name
    }

    // ---- building --------------------------------------------------------
    public Group AddGroup(string name, string color, string domain, IEnumerable<uint> elementIds,
                          IEnumerable<string> tags = null, string staadName = null, string sourcePredicateId = null)
    {
        var g = new Group { Name = name, Color = color, StaadName = staadName, SourcePredicateId = sourcePredicateId };
        if (tags != null) g.Tags.AddRange(tags);
        g.Members.Add(new Member { Domain = domain, Ids = elementIds.ToList() });
        Groups.Add(g);
        return g;
    }

    // Group whose members are computed from a predicate at runtime (no IDs stored).
    public Group AddPredicateGroup(string name, string color, string predicateId,
                                   IEnumerable<string> tags = null, string staadName = null)
    {
        var g = new Group { Name = name, Color = color, StaadName = staadName };
        if (tags != null) g.Tags.AddRange(tags);
        g.Members.Add(new Member { PredicateId = predicateId });
        Groups.Add(g);
        return g;
    }

    public Group AddNodeGroup(string name, string color, IEnumerable<uint> nodeIds,
                              IEnumerable<string> tags = null, string staadName = null)
    {
        var g = new Group { Name = name, Color = color, StaadName = staadName };
        if (tags != null) g.Tags.AddRange(tags);
        g.Members.Add(new Member { Domain = "nodes", NodeIds = nodeIds.ToList() });
        Groups.Add(g);
        return g;
    }

    // ---- geometryHash ----------------------------------------------------
    // blocksInOrder: the raw bytes of NODE, NDID, ELEM(d0..dn), ELID(d0..dn),
    // SECT, BPRP(d0..dn) -- omit blocks the file does not contain.
    public static string ComputeGeometryHash(IEnumerable<byte[]> blocksInOrder)
    {
        using (var sha = SHA256.Create())
        {
            foreach (var b in blocksInOrder)
                if (b != null && b.Length > 0) sha.TransformBlock(b, 0, b.Length, null, 0);
            sha.TransformFinalBlock(new byte[0], 0, 0);
            var sb = new StringBuilder("sha256:");
            foreach (var x in sha.Hash) sb.Append(x.ToString("x2"));
            return sb.ToString();
        }
    }

    // ---- JSON out --------------------------------------------------------
    public string ToJson()
    {
        var sb = new StringBuilder();
        sb.Append("{\n");
        sb.Append("  \"format\": ").Append(Q(Format)).Append(",\n");
        sb.Append("  \"version\": ").Append(EnvelopeVersion).Append(",\n");
        sb.Append("  \"model\": {");
        sb.Append("\"modelId\": ").Append(Q(ModelId)).Append(", ");
        sb.Append("\"geometryHash\": ").Append(Q(GeometryHash)).Append(", ");
        sb.Append("\"units\": {");
        sb.Append(string.Join(", ", Units.Select(kv => Q(kv.Key) + ": " + Q(kv.Value))));
        sb.Append("}},\n");
        sb.Append("  \"groups\": {\"version\": ").Append(GroupsVersion).Append(", \"items\": [\n");
        for (int i = 0; i < Groups.Count; i++)
        {
            var g = Groups[i];
            sb.Append("    {");
            sb.Append("\"id\": ").Append(Q(g.Id)).Append(", ");
            sb.Append("\"name\": ").Append(Q(g.Name)).Append(", ");
            if (g.Color != null) sb.Append("\"color\": ").Append(Q(g.Color)).Append(", ");
            sb.Append("\"tags\": [").Append(string.Join(", ", g.Tags.Select(Q))).Append("], ");
            if (g.SourcePredicateId != null) sb.Append("\"source\": {\"predicateId\": ").Append(Q(g.SourcePredicateId)).Append("}, ");
            if (g.StaadName != null) sb.Append("\"export\": {\"staadName\": ").Append(Q(g.StaadName)).Append("}, ");
            sb.Append("\"members\": [");
            for (int m = 0; m < g.Members.Count; m++)
            {
                var mem = g.Members[m];
                if (mem.PredicateId != null) { sb.Append("{\"predicateId\": ").Append(Q(mem.PredicateId)).Append("}"); if (m + 1 < g.Members.Count) sb.Append(", "); continue; }
                sb.Append("{\"domain\": ").Append(Q(mem.Domain));
                if (mem.Ids.Count > 0) sb.Append(", \"ids\": [").Append(string.Join(",", mem.Ids)).Append("]");
                if (mem.NodeIds.Count > 0) sb.Append(", \"nodeIds\": [").Append(string.Join(",", mem.NodeIds)).Append("]");
                sb.Append("}");
                if (m + 1 < g.Members.Count) sb.Append(", ");
            }
            sb.Append("]}");
            sb.Append(i + 1 < Groups.Count ? ",\n" : "\n");
        }
        sb.Append("  ]}");
        foreach (var kv in PassThrough)
            sb.Append(",\n  ").Append(Q(kv.Key)).Append(": ").Append(kv.Value);
        sb.Append("\n}\n");
        return sb.ToString();
    }

    private static string Q(string s)
    {
        if (s == null) return "null";
        var sb = new StringBuilder("\"");
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    // ---- STAAD bridge ----------------------------------------------------
    // Emits a STAAD "DEFINE GROUP" style block from the groups' element
    // members (node groups via _NODE). Adjust prefixes to the existing
    // exporter's conventions; this only shows the intended mapping.
    public string ToStaadGroupBlock()
    {
        var sb = new StringBuilder();
        sb.Append("START GROUP DEFINITION\n");
        var memberGroups = Groups.Where(g => g.Members.Any(m => m.Ids.Count > 0)).ToList();
        if (memberGroups.Count > 0)
        {
            sb.Append("MEMBER\n");
            foreach (var g in memberGroups)
                sb.Append("_").Append(StaadSafe(g.StaadName ?? g.Name)).Append(" ")
                  .Append(string.Join(" ", g.Members.SelectMany(m => m.Ids))).Append("\n");
        }
        var nodeGroups = Groups.Where(g => g.Members.Any(m => m.NodeIds.Count > 0)).ToList();
        if (nodeGroups.Count > 0)
        {
            sb.Append("JOINT\n");
            foreach (var g in nodeGroups)
                sb.Append("_").Append(StaadSafe(g.StaadName ?? g.Name)).Append(" ")
                  .Append(string.Join(" ", g.Members.SelectMany(m => m.NodeIds))).Append("\n");
        }
        sb.Append("END GROUP DEFINITION\n");
        return sb.ToString();
    }

    private static string StaadSafe(string name)
    {
        var s = new string(name.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
        return s.Length > 24 ? s.Substring(0, 24) : s;
    }
}
