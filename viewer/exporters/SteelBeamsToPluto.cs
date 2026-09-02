using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

// SteelBeamsToPluto  --  steel member CSV (W shapes) -> Pluto v4 geometry-only
// binary + features sidecar. C# 5 / Add-Type (PowerShell 5.1) compatible.
//
// Input CSV (Export-Csv, one ROW PER MEMBER -- unlike the pipe file's row-per-joint):
//   MemberOid, X0, Y0, Z0, X1, Y1, Z1,            end coordinates, METERS, plant frame
//   SectionName,                                   e.g. "W12X26"; groups + section name
//   D, Bf, Tf, Tw,                                 depth / flange width / flange thk /
//                                                  web thk, METERS (blank -> UNSIZED)
//   YDirX, YDirY, YDirZ,                           unit vector of the section local Y
//                                                  (web direction, bottom -> top flange)
//                                                  in world coords; blank -> default up
//   Room, RunName                                  optional (Room drives the pre-filter)
//   CP                                             optional; SP3D cardinal point, 15-point
//                                                  code (8 = top-center); blank/0/5/10+ =
//                                                  section centered on the routed line
//
// Mapping (same rules as PipeBeamsToPluto):
//   member end  -> node, deduplicated by rounded coordinate (1e-5 m) so a shared
//                  work point becomes one node and the frame reconnects
//   member      -> BeamMember; MemberOid kept as the LABEL
//   SectionName -> one SectionDef.IShape per distinct name (dims from its first row;
//                  rows that disagree on dims are counted, first row wins)
//   groups      -> ONE PER SECTION NAME, colored shallow -> deep on the fixed ramp,
//                  plus UNSIZED (red). Room/RunName are not grouped (yet).
//
// Usage from PowerShell 5.1 (fresh window; all files in ONE Add-Type call):
//   Add-Type -Path .\RawViewerWriter.cs, .\FeaturesSidecar.cs, .\SQL_BeamExporter.cs, .\SteelBeamsToPluto.cs, .\Stubs.cs
//   $ex = New-Object Voyager.SteelBeamsToPluto
//   $ex.Rooms('C:\Temp\steel_v1.csv')                       # optional census
//   $members = $ex.Build('C:\Temp\steel_v1.csv', '<room>')  # or $ex.Build($csv) for all
//   $ex.Summary()
//   $r = [Voyager.SteelBeamsToPluto]::Export($members, 'C:\Temp\steel_<room>', '<plant>/steel/<room>', 'in')
//   $r.Summary()
//
// (SQL_BeamExporter.cs is in the set because it defines Voyager.Vec3.)

namespace Voyager
{
    public class SteelMember
    {
        public Vec3 P0, P1;
        public Vec3 LocalY;           // world coords; ValidY says whether the CSV gave one
        public bool ValidY;
        public string SectionName;
        public double D, Bf, Tf, Tw;  // meters; NaN = unknown -> UNSIZED
        public string MemberOid;
        public string Room;
        public string RunName;
        public int Cp;                // SP3D cardinal point (15-point code); 0 = absent -> centroid
    }

    public class SteelBeamsToPluto
    {
        // ---- diagnostics, printed by Summary() ----
        public int RowsRead, RowsKept, MembersBuilt, Unsized, OrientMissing, OrientBad, DimConflicts, ZeroLength;
        public int CpOffCentroid, CpUnmapped;
        public string RoomFilter;

        public bool Progress = true;
        public int ProgressEveryRows = 5000;
        long _fileLen, _pos;
        DateTime _t0;
        int _lastBarLen;

        public string Summary()
        {
            return string.Format(CultureInfo.InvariantCulture,
                "room={0} rows={1} kept={2} members={3} unsized={4} zeroLength={5}\n" +
                "orient: missing={6} badVector={7}  dimConflicts={8}  cp: offCentroid={9} unmapped={10}",
                string.IsNullOrEmpty(RoomFilter) ? "(all)" : RoomFilter,
                RowsRead, RowsKept, MembersBuilt, Unsized, ZeroLength, OrientMissing, OrientBad, DimConflicts,
                CpOffCentroid, CpUnmapped);
        }

        public List<SteelMember> Build(string csvPath)
        {
            return Build(csvPath, null);
        }

        public List<SteelMember> Build(string csvPath, string room)
        {
            RowsRead = RowsKept = MembersBuilt = Unsized = OrientMissing = OrientBad = DimConflicts = ZeroLength = 0;
            CpOffCentroid = CpUnmapped = 0;
            RoomFilter = room == null ? null : room.Trim();
            bool filter = !string.IsNullOrEmpty(RoomFilter);

            // first dims seen per section name, to count conflicting rows
            var dimsBySection = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);
            var members = new List<SteelMember>();

            foreach (var r in ReadCsv(csvPath))
            {
                RowsRead++;
                ProgressTick(RowsRead, false);
                string rowRoom = Get(r, "Room");
                if (filter && !string.Equals(rowRoom.Trim(), RoomFilter, StringComparison.OrdinalIgnoreCase)) continue;
                RowsKept++;

                var m = new SteelMember();
                m.MemberOid = Get(r, "MemberOid");
                m.SectionName = Get(r, "SectionName").Trim();
                m.Room = rowRoom;
                m.RunName = Get(r, "RunName");
                m.P0 = new Vec3(D(r["X0"]), D(r["Y0"]), D(r["Z0"]));
                m.P1 = new Vec3(D(r["X1"]), D(r["Y1"]), D(r["Z1"]));
                m.D = DOpt(Get(r, "D")); m.Bf = DOpt(Get(r, "Bf"));
                m.Tf = DOpt(Get(r, "Tf")); m.Tw = DOpt(Get(r, "Tw"));
                if (double.IsNaN(m.D) || double.IsNaN(m.Bf) || double.IsNaN(m.Tf) || double.IsNaN(m.Tw)) Unsized++;

                int cp;
                m.Cp = int.TryParse(Get(r, "CP"), NumberStyles.Integer, CultureInfo.InvariantCulture, out cp) ? cp : 0;
                if (m.Cp >= 1 && m.Cp <= 9 && m.Cp != 5) CpOffCentroid++;
                else if (m.Cp > 10) CpUnmapped++;   // 11..15 shear-center codes: centroid for doubly-symmetric W

                double yx = DOpt(Get(r, "YDirX")), yy = DOpt(Get(r, "YDirY")), yz = DOpt(Get(r, "YDirZ"));
                double len = Math.Sqrt(yx * yx + yy * yy + yz * yz);
                if (double.IsNaN(len) || len < 1e-9) { OrientMissing++; m.ValidY = false; }
                else { m.LocalY = new Vec3(yx / len, yy / len, yz / len); m.ValidY = true; }

                if (!string.IsNullOrEmpty(m.SectionName) && !double.IsNaN(m.D))
                {
                    double[] seen;
                    var dims = new double[] { m.D, m.Bf, m.Tf, m.Tw };
                    if (dimsBySection.TryGetValue(m.SectionName, out seen))
                    {
                        for (int i = 0; i < 4; i++)
                            if (Math.Abs(seen[i] - dims[i]) > 1e-6) { DimConflicts++; break; }
                    }
                    else dimsBySection[m.SectionName] = dims;
                }

                members.Add(m);
                MembersBuilt++;
            }
            ProgressTick(RowsRead, true);
            return members;
        }

        // Distinct Room values with row counts -- to see what a CSV holds before filtering.
        public Dictionary<string, int> Rooms(string csvPath)
        {
            var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in ReadCsv(csvPath))
            {
                string k = Get(r, "Room").Trim();
                int c; d.TryGetValue(k, out c); d[k] = c + 1;
            }
            return d;
        }

        // ================= export =================

        public class Result
        {
            public string BinPath, SidecarPath, GeometryHash;
            public int Nodes, Beams, Sections, Unsized, OrientDefaulted, CpApplied;
            public string Summary()
            {
                return string.Format(CultureInfo.InvariantCulture,
                    "nodes={0} beams={1} sections={2} unsized={3} orientDefaulted={4} cpApplied={5}\n{6}\n{7}\n{8}",
                    Nodes, Beams, Sections, Unsized, OrientDefaulted, CpApplied, BinPath, SidecarPath, GeometryHash);
            }
        }

        // lengthUnit: "m" | "mm" | "in" | "ft" -- the unit the FILE is written in.
        // Coordinates and dims arrive in meters and are converted.
        public static Result Export(List<SteelMember> members, string outBase, string modelId, string lengthUnit)
        {
            if (members == null || members.Count == 0) throw new Exception("SteelBeamsToPluto: no members.");
            double scale = LengthScale(lengthUnit);

            // Recenter at the bbox center (whole meters) -- same float32 wobble fix
            // as the pipe bridge; offset recorded in the sidecar.
            double mnx = double.MaxValue, mny = double.MaxValue, mnz = double.MaxValue;
            double mxx = double.MinValue, mxy = double.MinValue, mxz = double.MinValue;
            foreach (SteelMember b0 in members)
            {
                mnx = Math.Min(mnx, Math.Min(b0.P0.X, b0.P1.X)); mxx = Math.Max(mxx, Math.Max(b0.P0.X, b0.P1.X));
                mny = Math.Min(mny, Math.Min(b0.P0.Y, b0.P1.Y)); mxy = Math.Max(mxy, Math.Max(b0.P0.Y, b0.P1.Y));
                mnz = Math.Min(mnz, Math.Min(b0.P0.Z, b0.P1.Z)); mxz = Math.Max(mxz, Math.Max(b0.P0.Z, b0.P1.Z));
            }
            var off = new Vec3(Math.Round((mnx + mxx) / 2), Math.Round((mny + mxy) / 2), Math.Round((mnz + mxz) / 2));
            const double NodeRound = 1e-5;   // meters: work points within 0.01 mm are one node
            // UNSIZED placeholder: a generic W8x24-ish shape so the member is visible
            const double UD = 0.20, UBf = 0.165, UTf = 0.010, UTw = 0.006;

            var nodeIdByKey = new Dictionary<string, int>();
            var nodes = new Dictionary<int, Node>();
            int nextNode = 1;

            var sectionIndexByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var sections = new List<RawViewerWriter.SectionDef>();
            var sectionDepth = new List<double>();     // meters, for ramp ordering
            int unsizedSection = -1;

            var beamMembers = new Dictionary<int, RawViewerWriter.BeamMember>();
            var beamLabels = new Dictionary<int, string>();
            var bySection = new Dictionary<int, List<uint>>();
            var unsizedIds = new List<uint>();
            int orientDefaulted = 0;
            int cpApplied = 0;

            int nextBeam = 1;
            foreach (SteelMember b in members)
            {
                // degenerate (zero length): drop BEFORE minting nodes, no orphans
                if (CoordKey(b.P0, NodeRound) == CoordKey(b.P1, NodeRound)) continue;
                int a = NodeFor(b.P0, off, scale, NodeRound, nodeIdByKey, nodes, ref nextNode);
                int z = NodeFor(b.P1, off, scale, NodeRound, nodeIdByKey, nodes, ref nextNode);

                bool sized = !(double.IsNaN(b.D) || double.IsNaN(b.Bf) || double.IsNaN(b.Tf) || double.IsNaN(b.Tw));
                int sec;
                if (!sized)
                {
                    if (unsizedSection < 0)
                    {
                        unsizedSection = sections.Count;
                        sections.Add(RawViewerWriter.SectionDef.IShape("UNSIZED",
                            (float)(UD * scale), (float)(UBf * scale), (float)(UTf * scale),
                            (float)(UBf * scale), (float)(UTf * scale), (float)(UTw * scale)));
                        sectionDepth.Add(UD);
                    }
                    sec = unsizedSection;
                    unsizedIds.Add((uint)nextBeam);
                }
                else
                {
                    string name = string.IsNullOrEmpty(b.SectionName) ? "UNNAMED" : b.SectionName;
                    if (!sectionIndexByName.TryGetValue(name, out sec))
                    {
                        sec = sections.Count;
                        sectionIndexByName[name] = sec;
                        // symmetric W: same flange top and bottom; first row's dims win
                        sections.Add(RawViewerWriter.SectionDef.IShape(name,
                            (float)(b.D * scale), (float)(b.Bf * scale), (float)(b.Tf * scale),
                            (float)(b.Bf * scale), (float)(b.Tf * scale), (float)(b.Tw * scale)));
                        sectionDepth.Add(b.D);
                    }
                }

                var m = new RawViewerWriter.BeamMember();
                m.Id = nextBeam;
                m.NodeA = a;
                m.NodeB = z;
                m.SectionIndex = sec;
                m.LocalY = ResolveLocalY(b, ref orientDefaulted);
                double oy, oz;
                CpOffsets(b.Cp, (sized ? b.Bf : UBf) * scale / 2, (sized ? b.D : UD) * scale / 2, out oy, out oz);
                m.OffsetAy = oy; m.OffsetBy = oy; m.OffsetAz = oz; m.OffsetBz = oz;
                if (oy != 0 || oz != 0) cpApplied++;
                beamMembers[m.Id] = m;
                beamLabels[m.Id] = b.MemberOid ?? "";
                AddTo(bySection, sec, (uint)m.Id);
                nextBeam++;
            }

            // ---- binary (beam-only, geometry-only) ----
            var beamComps = new List<RawViewerWriter.Component>();
            beamComps.Add(new RawViewerWriter.Component("Axial N", "force", "kip"));   // layout only; no planes written
            var units = new Dictionary<string, string>();
            units["length"] = lengthUnit;
            string binPath = outBase + ".bin";
            var w = new RawViewerWriter(binPath, nodes, null, null, null,
                                        beamMembers, sections, beamComps, modelId, units);
            w.SetBeamLabels(beamLabels);
            w.Write(false);

            // ---- sidecar: one group per section name, shallow -> deep on the ramp ----
            var sc = new FeaturesSidecar();
            sc.ModelId = modelId;
            sc.GeometryHash = w.GeometryHash;
            sc.Units["length"] = lengthUnit;
            sc.Units["worldOffset"] = string.Format(CultureInfo.InvariantCulture, "{0} {1} {2}",
                off.X * scale, off.Y * scale, off.Z * scale);

            var ordered = sectionIndexByName.Values.OrderBy(si => sectionDepth[si]).ToList();
            for (int i = 0; i < ordered.Count; i++)
            {
                int si = ordered[i];
                List<uint> ids;
                if (!bySection.TryGetValue(si, out ids)) continue;
                sc.AddGroup(sections[si].Name, SizeColor(i, ordered.Count), "beams", ids,
                            new[] { "steel", "section" }, StaadName(sections[si].Name));
            }
            if (unsizedIds.Count > 0) sc.AddGroup("UNSIZED", "#ff3b3b", "beams", unsizedIds, new[] { "steel", "unsized" }, "STEEL_UNSIZED");
            string scPath = outBase + ".features.json";
            File.WriteAllText(scPath, sc.ToJson(), new UTF8Encoding(false));

            var r = new Result();
            r.BinPath = binPath; r.SidecarPath = scPath; r.GeometryHash = w.GeometryHash;
            r.Nodes = nodes.Count; r.Beams = beamMembers.Count; r.Sections = sections.Count;
            r.Unsized = unsizedIds.Count; r.OrientDefaulted = orientDefaulted; r.CpApplied = cpApplied;
            return r;
        }

        // ---------- helpers ----------

        // SP3D 15-point cardinal code -> section-local (y=flange, z=web) offset of the
        // ring center relative to the routed line, in FILE units. The line passes
        // THROUGH the cardinal point, so the ring shifts the opposite way:
        //   cols: 1,4,7 left (-y) | 2,5,8 center | 3,6,9 right (+y)
        //   rows: 1,2,3 bottom (-z) | 4,5,6 middle | 7,8,9 top (+z)
        //   e.g. CP 8 (top-center, CONFIRMED vs Navisworks) -> ring drops by halfD.
        // 10 = centroid; 11..15 (shear-center codes) = centroid for doubly-symmetric W.
        public static void CpOffsets(int cp, double halfBf, double halfD, out double oy, out double oz)
        {
            oy = 0; oz = 0;
            if (cp < 1 || cp > 9) return;
            int col = (cp - 1) % 3;          // 0 left, 1 center, 2 right
            int row = (cp - 1) / 3;          // 0 bottom, 1 middle, 2 top
            oy = col == 0 ? halfBf : col == 2 ? -halfBf : 0;
            oz = row == 0 ? halfD : row == 2 ? -halfD : 0;
        }

        // The CSV YDir is the WEB direction (bottom -> top flange). The viewer's
        // section outline runs the depth along local *z* (beamGeometry.js iShape),
        // so the writer's LocalY must be the FLANGE direction: LocalY = web x axis
        // (then local z = axis x LocalY = web). Fallback web (missing or
        // parallel-to-axis vector): global Z, or global X for a vertical member.
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
                // default up = global Z, or global X for a near-vertical member
                if (Math.Abs(az) > 0.99) { yx = 1; yy = 0; yz = 0; } else { yx = 0; yy = 0; yz = 1; }
                dot = yx * ax + yy * ay + yz * az;
            }
            // project the web off the axis and renormalize
            yx -= dot * ax; yy -= dot * ay; yz -= dot * az;
            double l = Math.Sqrt(yx * yx + yy * yy + yz * yz);
            yx /= l; yy /= l; yz /= l;
            // flange direction = web x axis (unit: both unit and orthogonal)
            return new double[] { yy * az - yz * ay, yz * ax - yx * az, yx * ay - yy * ax };
        }

        // 12-step ramp (blue -> green -> yellow -> orange -> red); same as the pipe bridge.
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
                default: throw new Exception("SteelBeamsToPluto: unknown length unit '" + unit + "'.");
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

        static string Get(Dictionary<string, string> r, string col)
        {
            string v;
            return r.TryGetValue(col, out v) ? v : "";
        }

        static double D(string s) { return double.Parse(s, CultureInfo.InvariantCulture); }

        static double DOpt(string s)
        {
            double v;
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : double.NaN;
        }

        void ProgressTick(int rows, bool final)
        {
            if (!Progress) return;
            if (!final && rows % ProgressEveryRows != 0) return;
            double frac = _fileLen > 0 ? Math.Min(1.0, (double)_pos / _fileLen) : 0;
            if (final) frac = 1.0;
            const int width = 40;
            int filled = (int)Math.Round(frac * width);
            string bar = "[" + new string('#', filled) + new string('.', width - filled) + "] " +
                         string.Format(CultureInfo.InvariantCulture, "{0,3:0}% {1,8:N0} rows {2,5:0}s",
                                       frac * 100, rows, (DateTime.Now - _t0).TotalSeconds);
            Console.Write("\r" + bar.PadRight(_lastBarLen));
            _lastBarLen = bar.Length;
            if (final) Console.WriteLine();
        }

        // Minimal RFC-4180-ish reader: quoted fields with "" escapes; no embedded newlines.
        IEnumerable<Dictionary<string, string>> ReadCsv(string path)
        {
            _fileLen = new FileInfo(path).Length;
            _pos = 0; _t0 = DateTime.Now; _lastBarLen = 0;
            using (var rd = new StreamReader(path))
            {
                string header = rd.ReadLine();
                if (header == null) yield break;
                var cols = SplitLine(header);
                string line;
                while ((line = rd.ReadLine()) != null)
                {
                    if (line.Length == 0) continue;
                    var f = SplitLine(line);
                    var d = new Dictionary<string, string>(cols.Count);
                    for (int i = 0; i < cols.Count; i++) d[cols[i]] = i < f.Count ? f[i] : "";
                    _pos = rd.BaseStream.Position;
                    yield return d;
                }
            }
        }

        static List<string> SplitLine(string line)
        {
            var result = new List<string>();
            var sb = new StringBuilder();
            bool inQ = false;
            for (int i = 0; i < line.Length; i++)
            {
                char ch = line[i];
                if (inQ)
                {
                    if (ch == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else if (ch == '"') inQ = false;
                    else sb.Append(ch);
                }
                else if (ch == '"') inQ = true;
                else if (ch == ',') { result.Add(sb.ToString()); sb.Length = 0; }
                else sb.Append(ch);
            }
            result.Add(sb.ToString());
            return result;
        }
    }
}
