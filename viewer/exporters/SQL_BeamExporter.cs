using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

// SQL_BeamExporter  --  pipe CSV (SQL_Tutor "Pipe Extraction v1", v4) -> List<SQL_Beam>
// for PipeBeamsToPluto.Export. C# 5 / Add-Type (PowerShell 5.1) compatible.
//
// Input CSV columns (Export-Csv, every field quoted):
//   ConnOid, PartOid, PartClass, X, Y, Z (meters), RunOid, RunName, Room, Udf3, Udf4,
//   SizeInches (optional; if absent the size is parsed from RunName here -- the PowerShell
//              size pass is no longer needed and hung on the 400k-row v4 file)
//   Legacy v3.x CSVs carry WeldOid instead of ConnOid -- accepted (joint key falls back).
//
// One row per (joint, part). A part with 2 joints -> one beam (chord); 3+ -> a star
// from each joint to the centroid; <2 -> skipped and counted.
//
// Room pre-filter: Build(csv, room) keeps only rows whose Room column equals `room`
// (trimmed, case-insensitive). Filtering happens at row level BEFORE parts are
// accumulated, so a part is in or out with all of its joints. Null/empty room = no filter.
// This replaces the Excel / second-CSV step.
//
// Usage from PowerShell 5.1:
//   Add-Type -Path .\RawViewerWriter.cs, .\FeaturesSidecar.cs, .\SQL_BeamExporter.cs, .\PipeBeamsToPluto.cs, .\Stubs.cs
//   $ex = New-Object Voyager.SQL_BeamExporter
//   $beams = $ex.Build('C:\Temp\pipe_v4_sized.csv', '<room>')      # or $ex.Build($csv) for the whole plant
//   $ex.Summary()
//   $r = [Voyager.PipeBeamsToPluto]::Export($beams, 'C:\Temp\pipes_<room>', 'ProjectX/pipes/<room>', 'in')

namespace Voyager
{
    public struct Vec3
    {
        public double X, Y, Z;
        public Vec3(double x, double y, double z) { X = x; Y = y; Z = z; }
    }

    public class SQL_Beam
    {
        public Vec3 P0;
        public Vec3 P1;
        public double Diameter;      // meters; double.NaN when size unparsed
        public string PartOid;
        public int PartClass;
        public string RunOid;
        public string RunName;
        public string Room;          // from the CSV Room column ("" if absent)
        public string SizeSource;    // "data" (OD column / pipe_sizes.csv), "name" (RunName regex), "none"
        public double D0, D1;        // meters at P0 / P1; OD column (v4.2) or EndOD (v4.1 taper experiment); NaN = unknown
    }

    public class SQL_BeamExporter
    {
        const double InchToMeter = 0.0254;

        // diagnostics, printed by the caller at the end
        public int RowsRead, RowsKept, PartsSeen, PartsSkipped, PartsUnsized, JointConflicts, RunConflicts;
        public int SizedFromData, SizedFromName, SizedNone;
        public int TaperedBeams;     // beams whose two ends differ (reducers / reducing tees)
        public string RoomFilter;

        // ---- sizes from the model (v4.1): pipe_sizes.csv = PartOid, SrcClass, NPD, OD (meters) ----
        // Pipes give one row per port (2), fittings one per feature. A part whose rows disagree on OD
        // (reducer / reducing tee) is counted in SizeSpans and gets the LARGEST OD for now (D0/D1 later).
        Dictionary<string, double> _sizeMax = new Dictionary<string, double>();
        Dictionary<string, double> _sizeMin = new Dictionary<string, double>();
        public int SizeRows, SizeParts, SizeSpans;

        public void LoadSizes(string csvPath)
        {
            _sizeMax.Clear(); _sizeMin.Clear(); SizeRows = SizeParts = SizeSpans = 0;
            foreach (var r in ReadCsv(csvPath))
            {
                SizeRows++;
                double od;
                if (!double.TryParse(Get(r, "OD"), NumberStyles.Float, CultureInfo.InvariantCulture, out od) || od <= 0) continue;
                string part = r["PartOid"];
                double cur;
                if (_sizeMax.TryGetValue(part, out cur)) { if (od > cur) _sizeMax[part] = od; if (od < _sizeMin[part]) _sizeMin[part] = od; }
                else { _sizeMax[part] = od; _sizeMin[part] = od; }
            }
            ProgressTick(SizeRows, true);
            SizeParts = _sizeMax.Count;
            foreach (var kv in _sizeMax) { if (kv.Value - _sizeMin[kv.Key] > 1e-6) SizeSpans++; }
        }

        // Parts whose rows carry two different ODs (reducers, reducing tees): PartOid -> (min, max) meters.
        public Dictionary<string, double[]> SizeSpanParts()
        {
            var d = new Dictionary<string, double[]>();
            foreach (var kv in _sizeMax) { if (kv.Value - _sizeMin[kv.Key] > 1e-6) d[kv.Key] = new double[] { _sizeMin[kv.Key], kv.Value }; }
            return d;
        }

        // ASCII progress bar on the console (not Write-Progress). Set $ex.Progress = $false to silence.
        public bool Progress = true;
        public int ProgressEveryRows = 5000;
        long _fileLen, _pos;
        DateTime _t0;
        int _lastBarLen;

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

        public string Summary()
        {
            return string.Format(CultureInfo.InvariantCulture,
                "room={0} rows={1} kept={2} parts={3} skipped={4} unsized={5} jointConflicts={6} runConflicts={7}\n" +
                "sizes: fromData={8} fromName={9} none={10}  tapered beams={14}  (size table: {11} rows, {12} parts, {13} span two ODs)",
                string.IsNullOrEmpty(RoomFilter) ? "(all)" : RoomFilter,
                RowsRead, RowsKept, PartsSeen, PartsSkipped, PartsUnsized, JointConflicts, RunConflicts,
                SizedFromData, SizedFromName, SizedNone, SizeRows, SizeParts, SizeSpans, TaperedBeams);
        }

        public List<SQL_Beam> Build(string csvPath)
        {
            return Build(csvPath, null);
        }

        public List<SQL_Beam> Build(string csvPath, string room)
        {
            RowsRead = RowsKept = PartsSeen = PartsSkipped = PartsUnsized = JointConflicts = RunConflicts = 0;
            SizedFromData = SizedFromName = SizedNone = TaperedBeams = 0;
            RoomFilter = room == null ? null : room.Trim();
            bool filter = !string.IsNullOrEmpty(RoomFilter);

            var beams = new List<SQL_Beam>();
            var byPart = new Dictionary<string, PartAcc>();

            foreach (var r in ReadCsv(csvPath))
            {
                RowsRead++;
                ProgressTick(RowsRead, false);
                string rowRoom = Get(r, "Room");
                if (filter && !RoomMatch(rowRoom, RoomFilter)) continue;
                RowsKept++;

                string partOid = r["PartOid"];
                PartAcc acc;
                if (!byPart.TryGetValue(partOid, out acc))
                {
                    acc = new PartAcc();
                    acc.PartClass = int.Parse(r["PartClass"], CultureInfo.InvariantCulture);
                    acc.RunOid = Get(r, "RunOid");
                    acc.RunName = Get(r, "RunName");
                    acc.Room = rowRoom;
                    // SizeInches (PowerShell pre-parse) if present, else parse the run name here.
                    acc.Diameter = r.ContainsKey("SizeInches") ? ParseSize(r["SizeInches"])
                                                              : SizeFromRunName(acc.RunName);
                    acc.SizeSource = double.IsNaN(acc.Diameter) ? "none" : "name";
                    // LoadSizes() table (optional) beats the regex; the per-row OD (v4.2) beats both, see below.
                    double od;
                    if (_sizeMax.TryGetValue(partOid, out od)) { acc.Diameter = od; acc.SizeSource = "data"; }
                    byPart[partOid] = acc;
                }
                else if (acc.RunOid != Get(r, "RunOid") && !string.IsNullOrEmpty(Get(r, "RunOid")))
                {
                    RunConflicts++;   // same part, two runs: keep first, count it
                }

                var p = new Vec3(D(r["X"]), D(r["Y"]), D(r["Z"]));
                // v4: the joint is the hub (ConnOid). v3.x CSVs: WeldOid. Either way one point per joint.
                string jointOid = r.ContainsKey("ConnOid") ? r["ConnOid"] : Get(r, "WeldOid");
                Vec3 existing;
                if (acc.Joints.TryGetValue(jointOid, out existing))
                {
                    if (Dist(existing, p) > 1e-6) JointConflicts++;   // should never happen
                }
                else acc.Joints[jointOid] = p;
                // v4.2: OD column = the part's own size (meters), same on both of its rows.
                // v4.1 taper CSVs name it EndOD (size AT THAT END; a reducer's two rows differ) -- accepted.
                double eod;
                string odText = r.ContainsKey("EndOD") ? r["EndOD"] : Get(r, "OD");
                if (double.TryParse(odText, NumberStyles.Float, CultureInfo.InvariantCulture, out eod) && eod > 0)
                {
                    acc.EndOd[jointOid] = eod;
                    acc.SizeSource = "data";
                }
            }
            ProgressTick(RowsRead, true);

            foreach (var kv in byPart)
            {
                PartsSeen++;
                var acc = kv.Value;
                if (acc.EndOd.Count > 0) acc.Diameter = acc.EndOd.Values.Max();   // part size = its larger end
                var hubs = acc.Joints.Keys.ToList();
                var pts = hubs.Select(h => acc.Joints[h]).ToList();
                if (double.IsNaN(acc.Diameter)) PartsUnsized++;
                if (acc.SizeSource == "data") SizedFromData++; else if (acc.SizeSource == "name") SizedFromName++; else SizedNone++;

                if (pts.Count < 2) { PartsSkipped++; continue; }

                if (pts.Count == 2)
                {
                    beams.Add(Make(pts[0], pts[1], acc, kv.Key, acc.EndAt(hubs[0]), acc.EndAt(hubs[1])));
                    continue;
                }

                Vec3 c = Centroid(pts);
                for (int i = 0; i < pts.Count; i++) beams.Add(Make(pts[i], c, acc, kv.Key, acc.EndAt(hubs[i]), acc.Diameter));   // star arm: hub end -> centroid at the part's own size
            }
            return beams;
        }

        // Distinct Room values with row counts -- to see what rooms a CSV holds before filtering.
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

        // ---------- helpers ----------
        class PartAcc
        {
            public int PartClass;
            public string RunOid, RunName, Room;
            public double Diameter;
            public string SizeSource;
            public Dictionary<string, Vec3> Joints = new Dictionary<string, Vec3>();
            public Dictionary<string, double> EndOd = new Dictionary<string, double>();   // hub -> OD at that end
            public double EndAt(string hub) { double v; return EndOd.TryGetValue(hub, out v) ? v : Diameter; }
        }

        // A row's Room can hold several rooms. Match the whole string or any
        // '/', ',' or ';' delimited token (trimmed, case-insensitive). Tokens
        // after the first may elide the shared prefix: "A-123/321" = A-123 and
        // A-321. A short token is expanded two ways against the FIRST token and
        // matches on either: prefix up to the last '-' + token ("A-" + "321"),
        // and tail replacement (first token with its last N chars swapped).
        internal static bool RoomMatch(string rowRoom, string filter)
        {
            if (rowRoom == null) return false;
            if (Eq(rowRoom.Trim(), filter)) return true;
            var toks = rowRoom.Split(new[] { '/', ',', ';' });
            string first = toks.Length > 0 ? toks[0].Trim() : "";
            for (int i = 0; i < toks.Length; i++)
            {
                string t = toks[i].Trim();
                if (t.Length == 0) continue;
                if (Eq(t, filter)) return true;
                if (i == 0 || t.Length >= first.Length) continue;
                int dash = first.LastIndexOf('-');
                if (dash >= 0 && Eq(first.Substring(0, dash + 1) + t, filter)) return true;
                if (Eq(first.Substring(0, first.Length - t.Length) + t, filter)) return true;
            }
            return false;
        }
        static bool Eq(string a, string b) { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }

        static string Get(Dictionary<string, string> r, string col)
        {
            string v;
            return r.TryGetValue(col, out v) ? v : "";
        }

        SQL_Beam Make(Vec3 a, Vec3 b, PartAcc acc, string partOid, double d0, double d1)
        {
            var bm = new SQL_Beam();
            bm.P0 = a; bm.P1 = b; bm.SizeSource = acc.SizeSource;
            bm.D0 = d0; bm.D1 = d1;
            // single-radius viewer for now: the larger end (NaN-safe)
            bm.Diameter = double.IsNaN(d0) ? d1 : (double.IsNaN(d1) ? d0 : Math.Max(d0, d1));
            if (!double.IsNaN(d0) && !double.IsNaN(d1) && Math.Abs(d0 - d1) > 1e-6) TaperedBeams++;
            bm.PartOid = partOid; bm.PartClass = acc.PartClass;
            bm.RunOid = acc.RunOid; bm.RunName = acc.RunName; bm.Room = acc.Room;
            return bm;
        }

        static Vec3 Centroid(List<Vec3> pts)
        {
            double x = 0, y = 0, z = 0;
            foreach (var p in pts) { x += p.X; y += p.Y; z += p.Z; }
            return new Vec3(x / pts.Count, y / pts.Count, z / pts.Count);
        }

        static double Dist(Vec3 a, Vec3 b)
        {
            double dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        static double ParseSize(string s)
        {
            double v;
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v)
                 ? v * InchToMeter : double.NaN;
        }

        // Size from the run name: the ONLY '"' in the name follows the size, e.g. -1 1/2"- ; -3/4"- ; -2"- .
        // Same rule as the PowerShell Get-SizeInches (SQL_Tutor "Pipe Extraction v1"); NaN when absent.
        static readonly System.Text.RegularExpressions.Regex SizeRx =
            new System.Text.RegularExpressions.Regex("(\\d+ \\d+/\\d+|\\d+/\\d+|\\d+\\.?\\d*)\"",
                                                     System.Text.RegularExpressions.RegexOptions.Compiled);
        static double SizeFromRunName(string name)
        {
            if (string.IsNullOrEmpty(name)) return double.NaN;
            var m = SizeRx.Match(name);
            if (!m.Success) return double.NaN;
            string t = m.Groups[1].Value;
            double whole = 0, frac = 0;
            if (t.Contains(" "))
            {
                var wf = t.Split(' ');
                whole = double.Parse(wf[0], CultureInfo.InvariantCulture);
                t = wf[1];
            }
            if (t.Contains("/"))
            {
                var ab = t.Split('/');
                frac = double.Parse(ab[0], CultureInfo.InvariantCulture) / double.Parse(ab[1], CultureInfo.InvariantCulture);
            }
            else frac = double.Parse(t, CultureInfo.InvariantCulture);
            return (whole + frac) * InchToMeter;
        }

        static double D(string s) { return double.Parse(s, CultureInfo.InvariantCulture); }

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
                    _pos = rd.BaseStream.Position;   // buffered, so it steps in 4 KB-ish chunks -- fine for a bar
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
