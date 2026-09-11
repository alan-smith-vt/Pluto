// Atlas.cs - neighborhood explorer for SP3D Core tables (PowerShell 5.1 via Add-Type).
//
// Same constraints as SqlExplorer.cs: C# 5 only, System.Data.SqlClient, compiled in ONE
// Add-Type call together with SqlExplorer.cs (Import-SqlExplorer.ps1 does this).
//
// What it does: starting from one oid, walks CoreRelationOrigin `Hops` times (both
// directions by default), classifies every node via CoreBaseClass, attaches bbox
// (CoreSpatialIndex) and, for 80014 ports, JDistribPort/JDPipePort geometry.
// Emits: text tree (console), edges CSV, and a self-contained HTML graph.
//
// Nothing here reads JNamedItem / strName / ItemName - labels are classid + oid only.
//
// Fill in before use:  [Voyager.Atlas]::Plant = "<plant>"   (the MDB prefix)

using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Text;

namespace Voyager
{
    public class AtlasNode
    {
        public Guid Oid;
        public int ClassId;
        public int Depth;              // hops from the root (0 = root)
        public bool Expanded;          // did we query edges from/to this node
        public int Flags;              // CoreBaseClass.persistentFlag (bit 1024 = "deleted?" per the inherited rule; per-class for 240007/280225)
        public bool Flag1024 { get { return (Flags & 1024) != 0; } }
        public bool HasBbox;
        public double Cx, Cy, Cz;      // bbox centroid (m)
        public double Ex, Ey, Ez;      // bbox extents (m)
        public bool HasPort;           // JDistribPort row found (80014 only)
        public double Px, Py, Pz;      // PlacePointX/Y/Z (m)
        public double Npd;             // JDPipePort.NPD
        public double Od;              // JDPipePort.PipingOutsideDiameter (m)
        public int OutTotal, InTotal;  // true edge counts (before capping)
        public int OutShown, InShown;

        public string Short { get { return Oid.ToString("D").Substring(0, 8); } }
    }

    public class AtlasEdge
    {
        public Guid From;
        public Guid To;
        public Guid RelationType;
    }

    public class AtlasResult
    {
        public Guid Root;
        public int Hops;
        public bool Bidirectional;
        public Dictionary<Guid, AtlasNode> Nodes = new Dictionary<Guid, AtlasNode>();
        public List<AtlasEdge> Edges = new List<AtlasEdge>();
        public Dictionary<Guid, int> RelationCensus = new Dictionary<Guid, int>();
        public List<string> Log = new List<string>();
    }

    public static class Atlas
    {
        // ---- configuration -------------------------------------------------------
        public static string Plant = "<plant>";        // e.g. "ABC" -> ABC_MDB.dbo.*
        public static string Db = "MDB";               // which family member to walk: "MDB", "RDB", "CDB"
        public static int MaxEdgesPerNode = 5;         // per node, per direction
        public static int MaxFrontier = 400;           // safety valve on nodes expanded per hop

        // Shown when reached, never expanded (fan-out sinks / noise).
        public static HashSet<int> NoExpand = new HashSet<int>(new int[] {
            80013, 10059, 10001, 300002, 150001, 140043, 80037, 8, 12, 16, 80034, 21, 3
        });

        // Optional friendly names. Keys = relationType GUID string (lower-case).
        public static Dictionary<string, string> RelationLabels = new Dictionary<string, string>();

        // Class display names - vault Classid Atlas. Unknown classids print as the number.
        public static Dictionary<int, string> ClassNames = new Dictionary<int, string>() {
            {80012,"pipe"},{80005,"fitting"},{80054,"instrument"},{80055,"specialty"},
            {80042,"weld"},{80014,"port"},{80007,"HUB"},{80013,"run"},{9,"portproxy"},
            {8,"proxy8"},{80040,"gasket"},{80041,"boltset"},{20003,"tap?"},
            {80033,"pathleg"},{80034,"alongleg"},{80036,"endfeat"},{80038,"turnfeat"},
            {80059,"tapfeat"},{80060,"tapocc"},{80008,"unk8008"},
            {10059,"correl"},{150001,"graphics"}
        };

        // Class colors for the HTML graph (hex). Loaded from code/classids.json by Import-SqlExplorer.ps1.
        public static Dictionary<int, string> ClassColors = new Dictionary<int, string>();
        public static string DefaultColor = "#8a8f94";

        public static string ClassColor(int classid)
        {
            string c;
            if (classid == -1) { return "#c1121f"; }   // unresolved: red so it is never overlooked
            return ClassColors.TryGetValue(classid, out c) ? c : DefaultColor;
        }

        // Called by Import-AtlasClassMap for each classid in classids.json.
        public static void SetClass(int classid, string name, string color, bool noExpand)
        {
            if (!String.IsNullOrEmpty(name)) { ClassNames[classid] = name; }
            if (!String.IsNullOrEmpty(color)) { ClassColors[classid] = color; }
            if (noExpand) { NoExpand.Add(classid); } else { NoExpand.Remove(classid); }
        }

        private static string T(string table) { return Plant + "_" + Db + ".dbo." + table; }

        // ---- main entry ----------------------------------------------------------
        public static AtlasResult Explore(string rootOid, int hops, bool bidirectional)
        {
            AtlasResult r = new AtlasResult();
            r.Root = Guid.Parse(rootOid);
            r.Hops = hops;
            r.Bidirectional = bidirectional;

            AtlasNode root = new AtlasNode();
            root.Oid = r.Root; root.Depth = 0; root.ClassId = -1;
            r.Nodes[root.Oid] = root;
            Classify(r, new List<Guid>() { root.Oid });
            if (root.ClassId == -1)
            {
                // Do not abort: some DBs (e.g. the SDB) hold relation rows for oids that have
                // no local CoreBaseClass row. Walk the edges anyway; the root renders UNRESOLVED.
                r.Log.Add("root not in " + Db + " CoreBaseClass - continuing with UNRESOLVED root");
            }

            List<Guid> frontier = new List<Guid>() { root.Oid };
            for (int hop = 1; hop <= hops; hop++)
            {
                if (frontier.Count == 0) { break; }
                if (frontier.Count > MaxFrontier)
                {
                    r.Log.Add("hop " + hop + ": frontier " + frontier.Count + " truncated to " + MaxFrontier);
                    frontier = frontier.GetRange(0, MaxFrontier);
                }
                foreach (Guid g in frontier) { r.Nodes[g].Expanded = true; }

                List<AtlasEdge> raw = FetchEdges(frontier, bidirectional);
                r.Log.Add("hop " + hop + ": expanded " + frontier.Count + " nodes, " + raw.Count + " raw edges");

                // Count true fan-out per node/direction, then cap.
                Dictionary<Guid, int> outCnt = new Dictionary<Guid, int>();
                Dictionary<Guid, int> inCnt = new Dictionary<Guid, int>();
                List<AtlasEdge> kept = new List<AtlasEdge>();
                HashSet<string> seen = new HashSet<string>();
                foreach (AtlasEdge e in raw)
                {
                    string key = e.From + "|" + e.To + "|" + e.RelationType;
                    if (!seen.Add(key)) { continue; }
                    bool fromInFrontier = r.Nodes.ContainsKey(e.From) && r.Nodes[e.From].Expanded;
                    bool toInFrontier = r.Nodes.ContainsKey(e.To) && r.Nodes[e.To].Expanded;
                    bool keep = false;
                    if (fromInFrontier)
                    {
                        int c = Bump(outCnt, e.From);
                        r.Nodes[e.From].OutTotal = c;
                        if (c <= MaxEdgesPerNode) { keep = true; r.Nodes[e.From].OutShown = c; }
                    }
                    if (toInFrontier && bidirectional)
                    {
                        int c = Bump(inCnt, e.To);
                        r.Nodes[e.To].InTotal = c;
                        if (c <= MaxEdgesPerNode) { keep = true; r.Nodes[e.To].InShown = c; }
                    }
                    if (keep) { kept.Add(e); }
                }

                // New nodes from kept edges.
                List<Guid> fresh = new List<Guid>();
                foreach (AtlasEdge e in kept)
                {
                    foreach (Guid g in new Guid[] { e.From, e.To })
                    {
                        if (!r.Nodes.ContainsKey(g))
                        {
                            AtlasNode n = new AtlasNode();
                            n.Oid = g; n.Depth = hop; n.ClassId = -1;
                            r.Nodes[g] = n;
                            fresh.Add(g);
                        }
                    }
                }
                Classify(r, fresh);

                // Nodes that failed classification are NOT in the live MDB registry: either
                // soft-deleted or living in another database (catalog proxies point at these).
                // Keep them, labelled UNRESOLVED (classid -1), never expanded - dropping them
                // hid the cross-DB pointer for a day (2026-08-28).
                List<Guid> next = new List<Guid>();
                foreach (AtlasEdge e in kept)
                {
                    r.Edges.Add(e);
                    Bump(r.RelationCensus, e.RelationType);
                }
                foreach (Guid g in fresh)
                {
                    AtlasNode n = r.Nodes[g];
                    if (n.ClassId == -1) { continue; }
                    if (!NoExpand.Contains(n.ClassId)) { next.Add(g); }
                }
                frontier = next;
            }

            Enrich(r);
            return r;
        }

        private static int Bump(Dictionary<Guid, int> d, Guid k)
        {
            int c; d.TryGetValue(k, out c); c++; d[k] = c; return c;
        }

        // ---- SQL ------------------------------------------------------------------
        private static string InList(IEnumerable<Guid> ids)
        {
            StringBuilder sb = new StringBuilder();
            foreach (Guid g in ids)
            {
                if (sb.Length > 0) { sb.Append(","); }
                sb.Append("'").Append(g.ToString("D")).Append("'");   // Guid -> safe literal
            }
            return sb.ToString();
        }

        private static List<AtlasEdge> FetchEdges(List<Guid> frontier, bool bidirectional)
        {
            string ids = InList(frontier);
            string sql = "SELECT r.oid, r.oidTarget, r.relationType FROM " + T("CoreRelationOrigin") +
                         " r WHERE r.oid IN (" + ids + ")";
            if (bidirectional) { sql += " OR r.oidTarget IN (" + ids + ")"; }
            List<AtlasEdge> list = new List<AtlasEdge>();
            foreach (DataRow row in SqlExplorer.Query(sql).Rows)
            {
                AtlasEdge e = new AtlasEdge();
                e.From = (Guid)row[0]; e.To = (Guid)row[1]; e.RelationType = (Guid)row[2];
                list.Add(e);
            }
            return list;
        }

        private static void Classify(AtlasResult r, List<Guid> ids)
        {
            if (ids.Count == 0) { return; }
            // No persistentFlag filter: bit 1024 is a per-class property (every 240007 / 280225
            // carries it, no 240012 does - census 2026-08-28), NOT a deletion marker. Filtering on
            // it hid whole classes from the atlas.
            string sql = "SELECT oid, classid, persistentFlag FROM " + T("CoreBaseClass") +
                         " WHERE oid IN (" + InList(ids) + ")";
            foreach (DataRow row in SqlExplorer.Query(sql).Rows)
            {
                Guid g = (Guid)row[0];
                if (r.Nodes.ContainsKey(g))
                {
                    r.Nodes[g].ClassId = Convert.ToInt32(row[1]);
                    r.Nodes[g].Flags = row[2] == DBNull.Value ? 0 : Convert.ToInt32(row[2]);
                }
            }
        }

        private static double D(object o) { return o == DBNull.Value ? 0.0 : Convert.ToDouble(o); }

        private static void Enrich(AtlasResult r)
        {
            List<Guid> all = new List<Guid>(r.Nodes.Keys);
            List<Guid> ports = new List<Guid>();
            foreach (AtlasNode n in r.Nodes.Values) { if (n.ClassId == 80014) { ports.Add(n.Oid); } }

            for (int i = 0; i < all.Count; i += 500)
            {
                List<Guid> chunk = all.GetRange(i, Math.Min(500, all.Count - i));
                string sql = "SELECT oid, xmin, xmax, ymin, ymax, zmin, zmax FROM " + T("CoreSpatialIndex") +
                             " WHERE oid IN (" + InList(chunk) + ")";
                foreach (DataRow row in SqlExplorer.Query(sql).Rows)
                {
                    AtlasNode n = r.Nodes[(Guid)row[0]];
                    double x0 = D(row[1]), x1 = D(row[2]), y0 = D(row[3]), y1 = D(row[4]), z0 = D(row[5]), z1 = D(row[6]);
                    n.HasBbox = true;
                    n.Cx = (x0 + x1) / 2; n.Cy = (y0 + y1) / 2; n.Cz = (z0 + z1) / 2;
                    n.Ex = x1 - x0; n.Ey = y1 - y0; n.Ez = z1 - z0;
                }
            }

            if (ports.Count > 0)
            {
                string ids = InList(ports);
                string sql = "SELECT oid, PlacePointX, PlacePointY, PlacePointZ FROM " + T("JDistribPort") +
                             " WHERE oid IN (" + ids + ")";
                try
                {
                    foreach (DataRow row in SqlExplorer.Query(sql).Rows)
                    {
                        AtlasNode n = r.Nodes[(Guid)row[0]];
                        n.HasPort = true; n.Px = D(row[1]); n.Py = D(row[2]); n.Pz = D(row[3]);
                    }
                    sql = "SELECT oid, NPD, PipingOutsideDiameter FROM " + T("JDPipePort") +
                          " WHERE oid IN (" + ids + ")";
                    foreach (DataRow row in SqlExplorer.Query(sql).Rows)
                    {
                        AtlasNode n = r.Nodes[(Guid)row[0]];
                        n.Npd = D(row[1]); n.Od = D(row[2]);
                    }
                }
                catch (Exception ex) { r.Log.Add("port views skipped: " + ex.Message); }
            }
        }

        // ---- labels ----------------------------------------------------------------
        public static string ClassName(int classid)
        {
            string s;
            if (classid == -1) { return "UNRESOLVED (not in MDB registry: other DB or deleted)"; }
            return ClassNames.TryGetValue(classid, out s) ? classid + " " + s : classid.ToString();
        }

        public static string RelLabel(Guid rel)
        {
            string s;
            if (RelationLabels.TryGetValue(rel.ToString("D").ToLowerInvariant(), out s)) { return s; }
            return rel.ToString("D").Substring(0, 8);
        }

        private static string F(double v) { return v.ToString("0.000", CultureInfo.InvariantCulture); }

        public static string NodeDetail(AtlasNode n)
        {
            StringBuilder sb = new StringBuilder();
            if (n.Flag1024) { sb.Append(" [flag1024]"); }
            if (n.HasBbox) { sb.Append(" bbox@(" + F(n.Cx) + "," + F(n.Cy) + "," + F(n.Cz) + ") ext " + F(n.Ex) + "x" + F(n.Ey) + "x" + F(n.Ez)); }
            else { sb.Append(" nobbox"); }
            if (n.HasPort) { sb.Append(" port@(" + F(n.Px) + "," + F(n.Py) + "," + F(n.Pz) + ") NPD " + n.Npd + " OD " + F(n.Od)); }
            return sb.ToString();
        }

        // ---- text report -----------------------------------------------------------
        public static string Report(AtlasResult r)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("ATLAS root " + r.Root.ToString("D") + "  hops=" + r.Hops + "  bidirectional=" + r.Bidirectional);
            sb.AppendLine("nodes=" + r.Nodes.Count + " edges=" + r.Edges.Count + "  cap=" + MaxEdgesPerNode + "/node/direction");
            foreach (string l in r.Log) { sb.AppendLine("  " + l); }
            sb.AppendLine();
            sb.AppendLine("relationType census (paste GUIDs into the vault, they are safe):");
            foreach (KeyValuePair<Guid, int> kv in r.RelationCensus)
            {
                sb.AppendLine("  " + kv.Key.ToString("D") + "  x" + kv.Value + "  " + RelLabel(kv.Key));
            }
            sb.AppendLine();
            HashSet<Guid> printed = new HashSet<Guid>();
            Tree(r, r.Root, 0, sb, printed);
            return sb.ToString();
        }

        private static void Tree(AtlasResult r, Guid oid, int indent, StringBuilder sb, HashSet<Guid> printed)
        {
            AtlasNode n = r.Nodes[oid];
            string pad = new string(' ', indent * 4);
            string mark = printed.Contains(oid) ? " (seen)" : "";
            sb.AppendLine(pad + "[" + ClassName(n.ClassId) + "] " + n.Short + mark + (printed.Contains(oid) ? "" : NodeDetail(n)));
            if (printed.Contains(oid)) { return; }
            printed.Add(oid);
            if (!n.Expanded) { return; }
            foreach (AtlasEdge e in r.Edges)
            {
                if (e.From == oid) { sb.Append(pad + "  --" + RelLabel(e.RelationType) + "--> "); TreeChild(r, e.To, indent, sb, printed); }
            }
            if (n.OutTotal > n.OutShown) { sb.AppendLine(pad + "  --> <and " + (n.OutTotal - n.OutShown) + " more out>"); }
            foreach (AtlasEdge e in r.Edges)
            {
                if (e.To == oid && e.From != oid) { sb.Append(pad + "  <--" + RelLabel(e.RelationType) + "-- "); TreeChild(r, e.From, indent, sb, printed); }
            }
            if (n.InTotal > n.InShown) { sb.AppendLine(pad + "  <-- <and " + (n.InTotal - n.InShown) + " more in>"); }
        }

        private static void TreeChild(AtlasResult r, Guid oid, int indent, StringBuilder sb, HashSet<Guid> printed)
        {
            // Print the child inline on the arrow line, then recurse for its own edges.
            AtlasNode n = r.Nodes[oid];
            if (printed.Contains(oid)) { sb.AppendLine("[" + ClassName(n.ClassId) + "] " + n.Short + " (seen)"); return; }
            sb.AppendLine("[" + ClassName(n.ClassId) + "] " + n.Short + NodeDetail(n));
            printed.Add(oid);
            if (!n.Expanded) { return; }
            // Children of a child: one level deeper.
            string pad = new string(' ', (indent + 1) * 4);
            foreach (AtlasEdge e in r.Edges)
            {
                if (e.From == oid) { sb.Append(pad + "  --" + RelLabel(e.RelationType) + "--> "); TreeChild(r, e.To, indent + 1, sb, printed); }
            }
            if (n.OutTotal > n.OutShown) { sb.AppendLine(pad + "  --> <and " + (n.OutTotal - n.OutShown) + " more out>"); }
            foreach (AtlasEdge e in r.Edges)
            {
                if (e.To == oid && e.From != oid) { sb.Append(pad + "  <--" + RelLabel(e.RelationType) + "-- "); TreeChild(r, e.From, indent + 1, sb, printed); }
            }
            if (n.InTotal > n.InShown) { sb.AppendLine(pad + "  <-- <and " + (n.InTotal - n.InShown) + " more in>"); }
        }

        // ---- CSV -------------------------------------------------------------------
        public static void WriteCsv(AtlasResult r, string path)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("FromOid,FromClass,ToOid,ToClass,RelationType");
            foreach (AtlasEdge e in r.Edges)
            {
                sb.AppendLine(e.From + "," + r.Nodes[e.From].ClassId + "," + e.To + "," + r.Nodes[e.To].ClassId + "," + e.RelationType);
            }
            File.WriteAllText(path, sb.ToString());

            string npath = Path.ChangeExtension(path, ".nodes.csv");
            sb = new StringBuilder();
            sb.AppendLine("Oid,ClassId,Flags,Depth,Expanded,HasBbox,Cx,Cy,Cz,Ex,Ey,Ez,HasPort,Px,Py,Pz,NPD,OD,OutTotal,InTotal");
            foreach (AtlasNode n in r.Nodes.Values)
            {
                sb.AppendLine(String.Join(",", new string[] {
                    n.Oid.ToString(), n.ClassId.ToString(), n.Flags.ToString(), n.Depth.ToString(), n.Expanded ? "1" : "0",
                    n.HasBbox ? "1" : "0", F(n.Cx), F(n.Cy), F(n.Cz), F(n.Ex), F(n.Ey), F(n.Ez),
                    n.HasPort ? "1" : "0", F(n.Px), F(n.Py), F(n.Pz), n.Npd.ToString(CultureInfo.InvariantCulture), F(n.Od),
                    n.OutTotal.ToString(), n.InTotal.ToString() }));
            }
            File.WriteAllText(npath, sb.ToString());
        }

        // ---- HTML graph (self-contained, no external scripts) --------------------------
        public static void WriteHtml(AtlasResult r, string path)
        {
            StringBuilder nodes = new StringBuilder();
            foreach (AtlasNode n in r.Nodes.Values)
            {
                if (nodes.Length > 0) { nodes.Append(","); }
                nodes.Append("{id:\"" + n.Oid + "\",s:\"" + n.Short + "\",c:" + n.ClassId + ",n:\"" + ClassName(n.ClassId) +
                             "\",col:\"" + ClassColor(n.ClassId) + "\",d:" + n.Depth + ",x:" + (n.Expanded ? 1 : 0) + ",f1024:" + (n.Flag1024 ? 1 : 0) + ",root:" + (n.Oid == r.Root ? 1 : 0) +
                             ",more:\"" + MoreTag(n) + "\",t:\"" + Js(NodeDetail(n).Trim()) + "\"}");
            }
            StringBuilder edges = new StringBuilder();
            foreach (AtlasEdge e in r.Edges)
            {
                if (edges.Length > 0) { edges.Append(","); }
                edges.Append("{f:\"" + e.From + "\",t:\"" + e.To + "\",r:\"" + Js(RelLabel(e.RelationType)) + "\",g:\"" + e.RelationType + "\"}");
            }
            // Legend: one swatch per distinct classid present, ordered by classid.
            List<int> present = new List<int>();
            foreach (AtlasNode n in r.Nodes.Values) { if (!present.Contains(n.ClassId)) { present.Add(n.ClassId); } }
            present.Sort();
            StringBuilder legend = new StringBuilder();
            foreach (int c in present)
            {
                legend.Append("<span class=\"lg\"><i style=\"background:" + ClassColor(c) + "\"></i>" + ClassName(c) + "</span> ");
            }
            string html = HtmlTemplate
                .Replace("__LEGEND__", legend.ToString())
                .Replace("__TITLE__", "Atlas " + r.Root.ToString("D").Substring(0, 8) + " hops=" + r.Hops)
                .Replace("__NODES__", nodes.ToString())
                .Replace("__EDGES__", edges.ToString())
                .Replace("__REPORT__", Js(Report(r)).Replace("\\n", "\\n\" +\n\""));
            File.WriteAllText(path, html);
        }

        private static string MoreTag(AtlasNode n)
        {
            List<string> p = new List<string>();
            if (n.OutTotal > n.OutShown) { p.Add("+" + (n.OutTotal - n.OutShown) + " out"); }
            if (n.InTotal > n.InShown) { p.Add("+" + (n.InTotal - n.InShown) + " in"); }
            return String.Join(", ", p.ToArray());
        }

        private static string Js(string s)
        {
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", "\\n").Replace("</", "<\\/");
        }

        // Layout: columns by hop depth, rows ordered by class; drag nodes; wheel zoom; click = highlight.
        private const string HtmlTemplate = @"<!DOCTYPE html>
<html><head><meta charset=""utf-8""><title>__TITLE__</title>
<style>
body{margin:0;font:12px Consolas,monospace;background:#fafafa;color:#222}
#top{padding:6px 10px;background:#eee;border-bottom:1px solid #ccc}
#wrap{display:flex;height:calc(100vh - 32px)}
svg{flex:1;background:#fff;cursor:grab}
#side{width:34%;overflow:auto;border-left:1px solid #ccc;padding:8px;white-space:pre;background:#f6f6f6}
.n rect{stroke:#333;stroke-width:1}
.n text{pointer-events:none}
.e{stroke:#888;fill:none;marker-end:url(#a)}
.e.hi{stroke:#d9772f;stroke-width:2.5}
.el{fill:#666;font-size:10px}
.lg{display:inline-block;margin-right:10px;white-space:nowrap}.lg i{display:inline-block;width:10px;height:10px;margin-right:3px;border-radius:2px;vertical-align:middle}
.dim{opacity:.15}
</style></head><body>
<div id=""top""><b>__TITLE__</b> &nbsp; drag = pan/move node, wheel = zoom, click node = highlight, click empty = reset. Faded + red dashed border = persistentFlag bit 1024 set (&quot;deleted?&quot; per the inherited rule; class-wide for some classes - see vault CoreBaseClass).<br/>__LEGEND__
&nbsp; <button id=""bReset"">Reset layout</button> <button id=""bExport"">Export layout</button> <button id=""bImport"">Import layout</button> <span id=""saved""></span></div>
<div id=""wrap""><svg id=""g""><defs><marker id=""a"" viewBox=""0 0 10 10"" refX=""10"" refY=""5"" markerWidth=""8"" markerHeight=""8"" orient=""auto""><path d=""M0,0 L10,5 L0,10 z"" fill=""#888""/></marker></defs><g id=""vp""></g></svg>
<div id=""side""></div></div>
<script>
var N=[__NODES__], E=[__EDGES__], REPORT=""__REPORT__"";
var byId={}; N.forEach(function(n){byId[n.id]=n;});
function color(n){ return n.col; }
// layout: x by depth, y spread within depth (ordered by class then oid), root centered
var cols={}; N.forEach(function(n){ (cols[n.d]=cols[n.d]||[]).push(n); });
var W=200,H=90;
Object.keys(cols).forEach(function(d){ var c=cols[d]; c.sort(function(a,b){return a.c-b.c||(a.s<b.s?-1:1);});
  c.forEach(function(n,i){ n.px=60+d*W*1.6; n.py=60+(i-(c.length-1)/2)*H+600; }); });
// layout persistence: localStorage per root oid (survives reload in this browser profile) + export/import JSON
var ROOT=(N.filter(function(n){return n.root;})[0]||{}).id||'atlas', KEY='atlas-layout-'+ROOT;
function layoutJson(){ var o={}; N.forEach(function(n){ o[n.id]=[Math.round(n.px),Math.round(n.py)]; }); return JSON.stringify(o); }
function applyLayout(txt){ try{ var o=JSON.parse(txt); var k=0; N.forEach(function(n){ if(o[n.id]){ n.px=o[n.id][0]; n.py=o[n.id][1]; k++; } }); return k; }catch(e){ return -1; } }
function saveLayout(){ try{ localStorage.setItem(KEY,layoutJson()); document.getElementById('saved').textContent='layout saved '+new Date().toLocaleTimeString(); }catch(e){} }
try{ var s0=localStorage.getItem(KEY); if(s0){ applyLayout(s0); document.getElementById('saved').textContent='layout restored'; } }catch(e){}
var svg=document.getElementById('g'), vp=document.getElementById('vp'), side=document.getElementById('side');
var NS='http://www.w3.org/2000/svg';
function el(t,a){var e=document.createElementNS(NS,t);for(var k in a)e.setAttribute(k,a[k]);return e;}
var edgeEls=[], nodeEls={};
E.forEach(function(e){ var p=el('path',{'class':'e'}); var l=el('text',{'class':'el'}); l.textContent=e.r; vp.appendChild(p); vp.appendChild(l); edgeEls.push({e:e,p:p,l:l}); });
N.forEach(function(n){ var g=el('g',{'class':'n'}); var rc=el('rect',{width:150,height:44,rx:6,fill:color(n)});
  if(n.f1024){ rc.setAttribute('opacity','0.55'); rc.setAttribute('stroke','#c1121f'); rc.setAttribute('stroke-dasharray','4,3'); rc.setAttribute('stroke-width','2'); }
  if(n.root){rc.setAttribute('stroke-width',3);rc.setAttribute('stroke','#000');}
  if(!n.x){rc.setAttribute('stroke-dasharray','4 3');}
  var t1=el('text',{x:8,y:17,fill:'#fff','font-weight':'bold'}); t1.textContent=n.n;
  var t2=el('text',{x:8,y:32,fill:'#fff'}); t2.textContent=n.s+(n.more?'  ['+n.more+']':'');
  g.appendChild(rc); g.appendChild(t1); g.appendChild(t2); vp.appendChild(g); nodeEls[n.id]=g;
  g.addEventListener('mousedown',function(ev){drag={n:n,ox:ev.clientX,oy:ev.clientY,moved:false};ev.stopPropagation();});
  g.addEventListener('click',function(ev){ if(drag&&drag.moved)return; highlight(n); ev.stopPropagation(); }); });
function draw(){ N.forEach(function(n){ nodeEls[n.id].setAttribute('transform','translate('+n.px+','+n.py+')'); });
  edgeEls.forEach(function(x){ var a=byId[x.e.f], b=byId[x.e.t]; var ax=a.px+75, ay=a.py+22, bx=b.px+75, by=b.py+22;
    var dx=bx-ax, dy=by-ay, len=Math.sqrt(dx*dx+dy*dy)||1; var ex=bx-dx/len*80, ey=by-dy/len*26;
    var sx=ax+dx/len*80, sy=ay+dy/len*26; var mx=(sx+ex)/2, my=(sy+ey)/2, off=(a===b?40:0);
    x.p.setAttribute('d','M'+sx+','+sy+' Q'+(mx-dy/len*20)+','+(my+dx/len*20+off)+' '+ex+','+ey);
    x.l.setAttribute('x',mx-dy/len*14); x.l.setAttribute('y',my+dx/len*14); x.l.textContent=x.e.r; }); }
var scale=0.8, tx=0, ty=-350, drag=null, pan=null;
function apply(){ vp.setAttribute('transform','translate('+tx+','+ty+') scale('+scale+')'); }
svg.addEventListener('mousedown',function(ev){pan={x:ev.clientX,y:ev.clientY};});
window.addEventListener('mousemove',function(ev){ if(drag){drag.n.px+=(ev.clientX-drag.ox)/scale;drag.n.py+=(ev.clientY-drag.oy)/scale;drag.ox=ev.clientX;drag.oy=ev.clientY;drag.moved=true;draw();}
  else if(pan){tx+=ev.clientX-pan.x;ty+=ev.clientY-pan.y;pan={x:ev.clientX,y:ev.clientY};apply();} });
window.addEventListener('mouseup',function(){ if(drag&&drag.moved) saveLayout(); drag=null;pan=null; });
document.getElementById('bReset').addEventListener('click',function(){ try{localStorage.removeItem(KEY);}catch(e){} location.reload(); });
document.getElementById('bExport').addEventListener('click',function(){ side.textContent='Copy this into '+ROOT.substring(0,8)+'.layout.json (or paste it back with Import):\n\n'+layoutJson(); });
document.getElementById('bImport').addEventListener('click',function(){ var txt=window.prompt('Paste layout JSON'); if(!txt) return; var k=applyLayout(txt); if(k>0){ saveLayout(); draw(); side.textContent='layout applied to '+k+' nodes'; } else side.textContent='layout JSON not understood'; });
svg.addEventListener('click',function(){ highlight(null); });
svg.addEventListener('wheel',function(ev){ ev.preventDefault(); var f=ev.deltaY<0?1.1:0.9; var r=svg.getBoundingClientRect(); var mx=ev.clientX-r.left,my=ev.clientY-r.top;
  tx=mx-(mx-tx)*f; ty=my-(my-ty)*f; scale*=f; apply(); },{passive:false});
function highlight(n){ var keep={}; if(n){ keep[n.id]=1; E.forEach(function(e){ if(e.f===n.id)keep[e.t]=1; if(e.t===n.id)keep[e.f]=1; }); }
  N.forEach(function(m){ nodeEls[m.id].setAttribute('class', n&&!keep[m.id]?'n dim':'n'); });
  edgeEls.forEach(function(x){ var on=n&&(x.e.f===n.id||x.e.t===n.id); x.p.setAttribute('class', n?(on?'e hi':'e dim'):'e'); x.l.setAttribute('class', n&&!on?'el dim':'el'); });
  if(n){ var s='['+n.n+'] '+n.id+'\n'+n.t+(n.more?'\n'+n.more:'')+'\n\nOUT:\n'; E.forEach(function(e){ if(e.f===n.id) s+='  --'+e.r+'--> ['+byId[e.t].n+'] '+byId[e.t].s+'   '+e.g+'\n'; });
    s+='IN:\n'; E.forEach(function(e){ if(e.t===n.id) s+='  <--'+e.r+'-- ['+byId[e.f].n+'] '+byId[e.f].s+'   '+e.g+'\n'; }); side.textContent=s; }
  else side.textContent=REPORT; }
draw(); apply(); highlight(null);
</script></body></html>";
    }
}
