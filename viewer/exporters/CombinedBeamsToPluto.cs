using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

// CombinedBeamsToPluto  --  pipes + steel in ONE Pluto v4 file.
// C# 5 / Add-Type (PowerShell 5.1) compatible.
//
// Takes the pipe bridge's List<SQL_Beam> and the steel bridge's List<SteelMember>
// and writes a single beam-only, geometry-only binary + sidecar:
//   - ONE node table: endpoints deduped across BOTH disciplines (1e-5 m), so a
//     pipe support point and a steel work point at the same coordinate become
//     the same node
//   - ONE recenter offset (shared bbox), so everything lands in one scene frame
//   - merged section table (PIPE + I sections coexist)
//   - sidecar groups: per pipe size (blue->red ramp) and per steel section name
//     (blue->red ramp by depth); "UNSIZED PIPE" red, "UNSIZED STEEL" orange.
//     Discipline is carried as TAGS ("pipe"/"steel"), NOT as umbrella groups --
//     the viewer resolves one group per element, so overlapping groups would
//     clobber the size colors.
//   - labels: PartOid / MemberOid unprefixed (both are oids, unambiguous)
//
// Mapping rules are the same as the single-discipline bridges
// (PipeBeamsToPluto / SteelBeamsToPluto); those stay the reference for a
// one-discipline export. Either list may be null or empty, not both.
//
// Usage from PowerShell 5.1 (fresh window; one Add-Type call):
//   Add-Type -Path .\RawViewerWriter.cs, .\FeaturesSidecar.cs, .\SQL_BeamExporter.cs,
//                  .\SteelBeamsToPluto.cs, .\CombinedBeamsToPluto.cs, .\Stubs.cs
//   $pipes = (New-Object Voyager.SQL_BeamExporter).Build('C:\Temp\pipe_v4.csv', '<room>')
//   $steel = (New-Object Voyager.SteelBeamsToPluto).Build('C:\Temp\steel_v1.csv', '<room>')
//   $r = [Voyager.CombinedBeamsToPluto]::Export($pipes, $steel, 'C:\Temp\plant_<room>', '<plant>/combined/<room>', 'in')
//   $r.Summary()

namespace Voyager
{
    public class CombinedBeamsToPluto
    {
        public class Result
        {
            public string BinPath, SidecarPath, GeometryHash;
            public int Nodes, Beams, PipeBeams, SteelBeams, Sections;
            public int PipeUnsized, SteelUnsized, OrientDefaulted, CpApplied;
            public string Summary()
            {
                return string.Format(CultureInfo.InvariantCulture,
                    "nodes={0} beams={1} (pipe={2} steel={3}) sections={4}\n" +
                    "unsized: pipe={5} steel={6}  orientDefaulted={7} cpApplied={8}\n{9}\n{10}\n{11}",
                    Nodes, Beams, PipeBeams, SteelBeams, Sections,
                    PipeUnsized, SteelUnsized, OrientDefaulted, CpApplied, BinPath, SidecarPath, GeometryHash);
            }
        }

        // lengthUnit: "m" | "mm" | "in" | "ft" -- the unit the FILE is written in.
        // Everything arrives in meters and is converted.
        public static Result Export(List<SQL_Beam> pipes, List<SteelMember> steel,
                                    string outBase, string modelId, string lengthUnit)
        {
            int nPipes = pipes == null ? 0 : pipes.Count;
            int nSteel = steel == null ? 0 : steel.Count;
            if (nPipes + nSteel == 0) throw new Exception("CombinedBeamsToPluto: no beams.");
            double scale = LengthScale(lengthUnit);

            // ---- shared bbox recenter (whole meters; float32 wobble fix) ----
            double mnx = double.MaxValue, mny = double.MaxValue, mnz = double.MaxValue;
            double mxx = double.MinValue, mxy = double.MinValue, mxz = double.MinValue;
            for (int i = 0; i < nPipes; i++) { Grow(pipes[i].P0, pipes[i].P1, ref mnx, ref mny, ref mnz, ref mxx, ref mxy, ref mxz); }
            for (int i = 0; i < nSteel; i++) { Grow(steel[i].P0, steel[i].P1, ref mnx, ref mny, ref mnz, ref mxx, ref mxy, ref mxz); }
            var off = new Vec3(Math.Round((mnx + mxx) / 2), Math.Round((mny + mxy) / 2), Math.Round((mnz + mxz) / 2));

            const double NodeRound = 1e-5;              // meters: points within 0.01 mm are one node
            const double UnsizedOdMeters = 0.0508;      // 2 in placeholder pipe
            const double UD = 0.20, UBf = 0.165, UTf = 0.010, UTw = 0.006;   // placeholder W8x24-ish

            var nodeIdByKey = new Dictionary<string, int>();
            var nodes = new Dictionary<int, Node>();
            int nextNode = 1;

            var sections = new List<RawViewerWriter.SectionDef>();
            var members = new Dictionary<int, RawViewerWriter.BeamMember>();
            var beamLabels = new Dictionary<int, string>();
            var bySection = new Dictionary<int, List<uint>>();
            int nextBeam = 1;

            var r = new Result();

            // ---- pipes: one PIPE section per distinct OD ----
            var pipeSectionByOd = new Dictionary<long, int>();   // od micrometers -> section index
            int pipeUnsizedSection = -1;
            var pipeUnsizedIds = new List<uint>();
            for (int i = 0; i < nPipes; i++)
            {
                SQL_Beam b = pipes[i];
                if (CoordKey(b.P0, NodeRound) == CoordKey(b.P1, NodeRound)) continue;   // zero length
                int a = NodeFor(b.P0, off, scale, NodeRound, nodeIdByKey, nodes, ref nextNode);
                int z = NodeFor(b.P1, off, scale, NodeRound, nodeIdByKey, nodes, ref nextNode);

                int sec;
                if (double.IsNaN(b.Diameter))
                {
                    if (pipeUnsizedSection < 0)
                    {
                        pipeUnsizedSection = sections.Count;
                        sections.Add(RawViewerWriter.SectionDef.Pipe("UNSIZED PIPE", (float)(UnsizedOdMeters * scale), 0f));
                    }
                    sec = pipeUnsizedSection;
                    pipeUnsizedIds.Add((uint)nextBeam);
                }
                else
                {
                    long key = (long)Math.Round(b.Diameter * 1e6);
                    if (!pipeSectionByOd.TryGetValue(key, out sec))
                    {
                        sec = sections.Count;
                        pipeSectionByOd[key] = sec;
                        double inches = b.Diameter / 0.0254;
                        sections.Add(RawViewerWriter.SectionDef.Pipe(
                            "PIPE " + inches.ToString("0.###", CultureInfo.InvariantCulture) + " in",
                            (float)(b.Diameter * scale), 0f));
                    }
                }

                var m = new RawViewerWriter.BeamMember();
                m.Id = nextBeam;
                m.NodeA = a; m.NodeB = z; m.SectionIndex = sec;
                m.LocalY = new double[] { 0, 0, 1 };     // round section: any perpendicular is fine
                members[m.Id] = m;
                beamLabels[m.Id] = b.PartOid ?? "";
                AddTo(bySection, sec, (uint)m.Id);
                r.PipeBeams++;
                nextBeam++;
            }

            // ---- steel: one I section per distinct SectionName ----
            var steelSectionByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var steelDepth = new Dictionary<int, double>();      // section index -> depth (meters), ramp order
            int steelUnsizedSection = -1;
            var steelUnsizedIds = new List<uint>();
            for (int i = 0; i < nSteel; i++)
            {
                SteelMember b = steel[i];
                if (CoordKey(b.P0, NodeRound) == CoordKey(b.P1, NodeRound)) continue;   // zero length
                int a = NodeFor(b.P0, off, scale, NodeRound, nodeIdByKey, nodes, ref nextNode);
                int z = NodeFor(b.P1, off, scale, NodeRound, nodeIdByKey, nodes, ref nextNode);

                bool sized = !(double.IsNaN(b.D) || double.IsNaN(b.Bf) || double.IsNaN(b.Tf) || double.IsNaN(b.Tw));
                int sec;
                if (!sized)
                {
                    if (steelUnsizedSection < 0)
                    {
                        steelUnsizedSection = sections.Count;
                        sections.Add(RawViewerWriter.SectionDef.IShape("UNSIZED STEEL",
                            (float)(UD * scale), (float)(UBf * scale), (float)(UTf * scale),
                            (float)(UBf * scale), (float)(UTf * scale), (float)(UTw * scale)));
                    }
                    sec = steelUnsizedSection;
                    steelUnsizedIds.Add((uint)nextBeam);
                }
                else
                {
                    string name = string.IsNullOrEmpty(b.SectionName) ? "UNNAMED" : b.SectionName;
                    if (!steelSectionByName.TryGetValue(name, out sec))
                    {
                        sec = sections.Count;
                        steelSectionByName[name] = sec;
                        steelDepth[sec] = b.D;
                        sections.Add(RawViewerWriter.SectionDef.IShape(name,
                            (float)(b.D * scale), (float)(b.Bf * scale), (float)(b.Tf * scale),
                            (float)(b.Bf * scale), (float)(b.Tf * scale), (float)(b.Tw * scale)));
                    }
                }

                var m = new RawViewerWriter.BeamMember();
                m.Id = nextBeam;
                m.NodeA = a; m.NodeB = z; m.SectionIndex = sec;
                int dummy = 0;
                m.LocalY = ResolveLocalY(b, ref dummy);
                if (dummy > 0) r.OrientDefaulted++;
                double oy, oz;
                SteelBeamsToPluto.CpOffsets(b.Cp, (sized ? b.Bf : UBf) * scale / 2, (sized ? b.D : UD) * scale / 2, out oy, out oz);
                m.OffsetAy = oy; m.OffsetBy = oy; m.OffsetAz = oz; m.OffsetBz = oz;
                if (oy != 0 || oz != 0) r.CpApplied++;
                members[m.Id] = m;
                beamLabels[m.Id] = b.MemberOid ?? "";
                AddTo(bySection, sec, (uint)m.Id);
                r.SteelBeams++;
                nextBeam++;
            }

            // ---- binary (beam-only, geometry-only) ----
            var beamComps = new List<RawViewerWriter.Component>();
            beamComps.Add(new RawViewerWriter.Component("Axial N", "force", "kip"));   // layout only; no planes written
            var units = new Dictionary<string, string>();
            units["length"] = lengthUnit;
            string binPath = outBase + ".bin";
            var w = new RawViewerWriter(binPath, nodes, null, null, null,
                                        members, sections, beamComps, modelId, units);
            w.SetBeamLabels(beamLabels);
            w.Write(false);

            // ---- sidecar ----
            var sc = new FeaturesSidecar();
            sc.ModelId = modelId;
            sc.GeometryHash = w.GeometryHash;
            sc.Units["length"] = lengthUnit;
            sc.Units["worldOffset"] = string.Format(CultureInfo.InvariantCulture, "{0} {1} {2}",
                off.X * scale, off.Y * scale, off.Z * scale);

            var pipeSized = pipeSectionByOd.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList();
            for (int i = 0; i < pipeSized.Count; i++)
            {
                int si = pipeSized[i];
                List<uint> ids;
                if (!bySection.TryGetValue(si, out ids)) continue;
                sc.AddGroup(sections[si].Name, SizeColor(i, pipeSized.Count), "beams", ids,
                            new[] { "pipe", "size" }, StaadName(sections[si].Name));
            }
            if (pipeUnsizedIds.Count > 0)
                sc.AddGroup("UNSIZED PIPE", "#ff3b3b", "beams", pipeUnsizedIds, new[] { "pipe", "unsized" }, "PIPE_UNSIZED");

            // Steel stays white in the grouped view (matches the neutral color) so the
            // combined model reads Navisworks-style: white structure, colored pipe.
            // The groups still exist per section, so the legend/filtering keep working.
            var steelSized = steelSectionByName.Values.OrderBy(si => steelDepth[si]).ToList();
            for (int i = 0; i < steelSized.Count; i++)
            {
                int si = steelSized[i];
                List<uint> ids;
                if (!bySection.TryGetValue(si, out ids)) continue;
                sc.AddGroup(sections[si].Name, SteelWhite, "beams", ids,
                            new[] { "steel", "section" }, StaadName(sections[si].Name));
            }
            if (steelUnsizedIds.Count > 0)
                sc.AddGroup("UNSIZED STEEL", "#ff7a3b", "beams", steelUnsizedIds, new[] { "steel", "unsized" }, "STEEL_UNSIZED");

            string scPath = outBase + ".features.json";
            File.WriteAllText(scPath, sc.ToJson(), new UTF8Encoding(false));

            r.BinPath = binPath; r.SidecarPath = scPath; r.GeometryHash = w.GeometryHash;
            r.Nodes = nodes.Count; r.Beams = members.Count; r.Sections = sections.Count;
            r.PipeUnsized = pipeUnsizedIds.Count; r.SteelUnsized = steelUnsizedIds.Count;
            return r;
        }

        // ---------- helpers (same rules as the single-discipline bridges) ----------

        static void Grow(Vec3 p0, Vec3 p1, ref double mnx, ref double mny, ref double mnz,
                         ref double mxx, ref double mxy, ref double mxz)
        {
            mnx = Math.Min(mnx, Math.Min(p0.X, p1.X)); mxx = Math.Max(mxx, Math.Max(p0.X, p1.X));
            mny = Math.Min(mny, Math.Min(p0.Y, p1.Y)); mxy = Math.Max(mxy, Math.Max(p0.Y, p1.Y));
            mnz = Math.Min(mnz, Math.Min(p0.Z, p1.Z)); mxz = Math.Max(mxz, Math.Max(p0.Z, p1.Z));
        }

        // CSV YDir = WEB direction; the viewer runs section depth along local z,
        // so the writer's LocalY = FLANGE direction = web x axis (see SteelBeamsToPluto).
        static double[] ResolveLocalY(SteelMember b, ref int defaulted)
        {
            double ax = b.P1.X - b.P0.X, ay = b.P1.Y - b.P0.Y, az = b.P1.Z - b.P0.Z;
            double al = Math.Sqrt(ax * ax + ay * ay + az * az);
            if (al > 1e-12) { ax /= al; ay /= al; az /= al; }

            double yx = 0, yy = 0, yz = 1;
            bool haveCsv = b.ValidY;
            if (haveCsv) { yx = b.LocalY.X; yy = b.LocalY.Y; yz = b.LocalY.Z; }
            double dot = yx * ax + yy * ay + yz * az;
            if (!haveCsv || Math.Abs(dot) > 0.99)
            {
                defaulted++;
                if (Math.Abs(az) > 0.99) { yx = 1; yy = 0; yz = 0; } else { yx = 0; yy = 0; yz = 1; }
                dot = yx * ax + yy * ay + yz * az;
            }
            yx -= dot * ax; yy -= dot * ay; yz -= dot * az;
            double l = Math.Sqrt(yx * yx + yy * yy + yz * yz);
            yx /= l; yy /= l; yz /= l;
            return new double[] { yy * az - yz * ay, yz * ax - yx * az, yx * ay - yy * ax };
        }

        // #f2f2f5 = the viewer's neutral beam white (0.95, 0.95, 0.96)
        const string SteelWhite = "#f2f2f5";

        static readonly string[] Ramp = {
            "#3b4cc0", "#4f7fd8", "#6fa8e6", "#8fd0d8", "#a5dca0", "#c9e35a",
            "#f2e34a", "#f6be2e", "#f39221", "#e8641c", "#d13a1f", "#b40426" };
        static string SizeColor(int i, int n)
        {
            if (n <= 1) return Ramp[6];
            int idx = (int)Math.Round((double)i * (Ramp.Length - 1) / Math.Max(n - 1, 1));
            return Ramp[Math.Min(Math.Max(idx, 0), Ramp.Length - 1)];
        }
        static string StaadName(string sectionName)
        {
            var chars = sectionName.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray();
            return new string(chars).ToUpperInvariant();
        }

        static double LengthScale(string unit)
        {
            switch ((unit ?? "m").ToLowerInvariant())
            {
                case "m": return 1.0;
                case "mm": return 1000.0;
                case "in": return 1.0 / 0.0254;
                case "ft": return 1.0 / 0.3048;
                default: throw new Exception("CombinedBeamsToPluto: unknown length unit '" + unit + "'.");
            }
        }

        static string CoordKey(Vec3 p, double round)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0}|{1}|{2}",
                Math.Round(p.X / round), Math.Round(p.Y / round), Math.Round(p.Z / round));
        }

        static int NodeFor(Vec3 p, Vec3 off, double scale, double round, Dictionary<string, int> byKey,
                           Dictionary<int, Node> nodes, ref int next)
        {
            string key = CoordKey(p, round);
            int id;
            if (byKey.TryGetValue(key, out id)) return id;
            id = next++;
            byKey[key] = id;
            var n = new Node();
            n.id = id;
            n.xyz.X = (float)((p.X - off.X) * scale);
            n.xyz.Y = (float)((p.Y - off.Y) * scale);
            n.xyz.Z = (float)((p.Z - off.Z) * scale);
            nodes[id] = n;
            return id;
        }

        static void AddTo<K>(Dictionary<K, List<uint>> map, K key, uint id)
        {
            List<uint> list;
            if (!map.TryGetValue(key, out list)) { list = new List<uint>(); map[key] = list; }
            list.Add(id);
        }
    }
}
