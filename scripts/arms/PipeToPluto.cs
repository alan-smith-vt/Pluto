using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

// PipeToPluto  --  bridge from the pipe CSV exporter's List<PipeBeam>
// to a Pluto v4 geometry-only binary + features sidecar.
// C# 5 / Add-Type (PowerShell 5.1) compatible.
//
// Input  : List<PipeBeam> { P0, P1 (Vec3, METERS), Diameter (meters or NaN),
//                               PartOid, PartClass, RunOid, RunName }
// Output : <out>.bin            v4 binary, beam domain only, no load cases
//          <out>.features.json  groups: ONE PER PIPE SIZE (colored, small -> large
//                               on a fixed ramp) plus UNSIZED (red). Nothing else
//                               is colored: class / run / star-vs-chord are kept as
//                               TAGS on the size groups only where cheap, never as
//                               groups, so "Color by groups" == color by size.
//
// Mapping (see SQL_Tutor vault "Beam Export - Viewer Review"):
//   weld point   -> node, deduplicated by rounded coordinate (welds are shared
//                   between parts, so this reconnects the network)
//   beam         -> BeamMember, dense int id; PartOid kept as the LABEL
//   Diameter     -> one SectionDef.Pipe per distinct size; NaN -> "UNSIZED"
//   categories   -> sidecar groups (never per-element strings in the binary)
//
// Usage from PowerShell 5.1:
//   Add-Type -Path .\RawViewerWriter.cs, .\FeaturesSidecar.cs, .\PipeCsvReader.cs, .\PipeToPluto.cs
//   $beams = (New-Object PipeCsvReader).Build('pipe_v3_2_sized.csv')
//   $r = [PipeToPluto]::Export($beams, 'C:\out\pipes', 'ProjectX/pipes/rev1', 'in')
//   $r.Summary()
//
// (The stub types Node/Element/etc. that RawViewerWriter references must be
// present in the Add-Type set, as they are in the STAAD scripts. If a script
// only needs this bridge, compile with the small stubs in Stubs.cs.)

public class PipeToPluto
{
    public class Result
    {
        public string BinPath, SidecarPath, GeometryHash;
        public int Nodes, Beams, Sections, Unsized, Stars, Chords, Synthetic;
        public string Summary()
        {
            return string.Format(CultureInfo.InvariantCulture,
                "nodes={0} beams={1} sections={2} unsized={3} chords={4} starArms={5} synthetic={6}\n{7}\n{8}\n{9}",
                Nodes, Beams, Sections, Unsized, Chords, Stars, Synthetic, BinPath, SidecarPath, GeometryHash);
        }
    }

    // lengthUnit: "m" | "in" | "ft" -- the unit the FILE is written in.
    // Coordinates and diameters arrive in meters and are converted.
    public static Result Export(List<PipeBeam> beams, string outBase, string modelId, string lengthUnit)
    {
        if (beams == null || beams.Count == 0) throw new Exception("PipeToPluto: no beams.");
        double scale = LengthScale(lengthUnit);

        // Recenter: plant coordinates run to ~1e4-1e6 in file units, and float32
        // vertices that large wobble a few pixels as the camera moves (precision).
        // Subtract the bbox center (rounded to whole meters), record it in the
        // sidecar so the viewer can add it back in the hover readout.
        double mnx = double.MaxValue, mny = double.MaxValue, mnz = double.MaxValue;
        double mxx = double.MinValue, mxy = double.MinValue, mxz = double.MinValue;
        foreach (PipeBeam b0 in beams)
        {
            mnx = Math.Min(mnx, Math.Min(b0.P0.X, b0.P1.X)); mxx = Math.Max(mxx, Math.Max(b0.P0.X, b0.P1.X));
            mny = Math.Min(mny, Math.Min(b0.P0.Y, b0.P1.Y)); mxy = Math.Max(mxy, Math.Max(b0.P0.Y, b0.P1.Y));
            mnz = Math.Min(mnz, Math.Min(b0.P0.Z, b0.P1.Z)); mxz = Math.Max(mxz, Math.Max(b0.P0.Z, b0.P1.Z));
        }
        var off = new Vec3(Math.Round((mnx + mxx) / 2), Math.Round((mny + mxy) / 2), Math.Round((mnz + mxz) / 2));
        const double UnsizedOdMeters = 0.0508;     // 2 in placeholder for unparsed sizes
        const double NodeRound = 1e-5;              // meters: welds within 0.01 mm are one node

        // ---- nodes: dedupe by rounded coordinate ----
        var nodeIdByKey = new Dictionary<string, int>();
        var nodes = new Dictionary<int, Node>();
        int nextNode = 1;   // (node labels would need WeldOid on PipeBeam; not carried yet)

        // ---- sections: one per distinct diameter ----
        var sectionIndexByOd = new Dictionary<long, int>();    // od in micrometers -> index
        var sections = new List<RawViewerWriter.SectionDef>();
        int unsizedSection = -1;

        // ---- beams ----
        var members = new Dictionary<int, RawViewerWriter.BeamMember>();
        var beamLabels = new Dictionary<int, string>();
        var bySection = new Dictionary<int, List<uint>>();     // section index -> beam ids
        var unsized = new List<uint>();
        var synthetic = new List<uint>();
        var chords = new List<uint>();
        var stars = new List<uint>();

        // chord vs star: a part with exactly one beam is a chord
        var beamsPerPart = new Dictionary<string, int>();
        foreach (PipeBeam b in beams)
        {
            int c;
            beamsPerPart.TryGetValue(b.PartOid, out c);
            beamsPerPart[b.PartOid] = c + 1;
        }

        int nextBeam = 1;
        foreach (PipeBeam b in beams)
        {
            int a = NodeFor(b.P0, off, scale, NodeRound, nodeIdByKey, nodes, ref nextNode);
            int z = NodeFor(b.P1, off, scale, NodeRound, nodeIdByKey, nodes, ref nextNode);
            if (a == z) continue;                    // degenerate (zero length)

            int sec;
            if (double.IsNaN(b.Diameter))
            {
                if (unsizedSection < 0)
                {
                    unsizedSection = sections.Count;
                    sections.Add(RawViewerWriter.SectionDef.Pipe("UNSIZED", (float)(UnsizedOdMeters * scale), 0f));
                }
                sec = unsizedSection;
                if (!b.Synthetic) unsized.Add((uint)nextBeam);
            }
            else
            {
                long key = (long)Math.Round(b.Diameter * 1e6);
                if (!sectionIndexByOd.TryGetValue(key, out sec))
                {
                    sec = sections.Count;
                    sectionIndexByOd[key] = sec;
                    double inches = b.Diameter / 0.0254;
                    sections.Add(RawViewerWriter.SectionDef.Pipe(
                        "PIPE " + inches.ToString("0.###", CultureInfo.InvariantCulture) + " in",
                        (float)(b.Diameter * scale), 0f));
                }
            }

            var m = new RawViewerWriter.BeamMember();
            m.Id = nextBeam;
            m.NodeA = a;
            m.NodeB = z;
            m.SectionIndex = sec;
            m.LocalY = new double[] { 0, 0, 1 };     // round section: any perpendicular is fine
            members[m.Id] = m;
            beamLabels[m.Id] = b.PartOid ?? "";

            // synthetic (one-hub fallback) beams group as SYNTHETIC, not by size --
            // the viewer resolves one group per element, so membership is exclusive
            if (b.Synthetic) synthetic.Add((uint)m.Id);
            else AddTo(bySection, sec, (uint)m.Id);
            if (beamsPerPart[b.PartOid] == 1) chords.Add((uint)m.Id); else stars.Add((uint)m.Id);
            nextBeam++;
        }

        // ---- write the binary (beam-only, geometry-only) ----
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
        // Bbox center subtracted from every node, expressed in FILE units so the
        // viewer can add it straight back onto picked coordinates.
        sc.Units["worldOffset"] = string.Format(CultureInfo.InvariantCulture, "{0} {1} {2}",
            off.X * scale, off.Y * scale, off.Z * scale);
        // One group per pipe size, ascending, colored on a fixed ramp so the
        // legend reads small -> large. Unsized last, in red.
        var sized = sectionIndexByOd.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList();
        for (int i = 0; i < sized.Count; i++)
        {
            int si = sized[i];
            List<uint> ids;
            if (!bySection.TryGetValue(si, out ids)) continue;
            sc.AddGroup(sections[si].Name, SizeColor(i, sized.Count), "beams", ids,
                        new[] { "pipe", "size" }, StaadName(sections[si].Name));
        }
        if (unsized.Count > 0) sc.AddGroup("UNSIZED", "#ff3b3b", "beams", unsized, new[] { "pipe", "unsized" }, "PIPE_UNSIZED");
        // light steel-gray: reads like the white structure, a shade darker
        if (synthetic.Count > 0) sc.AddGroup("SYNTHETIC", "#c9ccd2", "beams", synthetic, new[] { "pipe", "synthetic" }, "PIPE_SYNTHETIC");
        string scPath = outBase + ".features.json";
        File.WriteAllText(scPath, sc.ToJson(), new System.Text.UTF8Encoding(false));

        var r = new Result();
        r.BinPath = binPath; r.SidecarPath = scPath; r.GeometryHash = w.GeometryHash;
        r.Nodes = nodes.Count; r.Beams = members.Count; r.Sections = sections.Count;
        r.Unsized = unsized.Count; r.Chords = chords.Count; r.Stars = stars.Count; r.Synthetic = synthetic.Count;
        return r;
    }

    // 12-step ramp (blue -> green -> yellow -> orange -> red); wraps beyond 12.
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
            default: throw new Exception("PipeToPluto: unknown length unit '" + unit + "'.");
        }
    }

    static int NodeFor(Vec3 p, Vec3 off, double scale, double round, Dictionary<string, int> byKey,
                       Dictionary<int, Node> nodes, ref int next)
    {
        string key = string.Format(CultureInfo.InvariantCulture, "{0}|{1}|{2}",
            Math.Round(p.X / round), Math.Round(p.Y / round), Math.Round(p.Z / round));
        int id;
        if (byKey.TryGetValue(key, out id)) return id;
        id = next++;
        byKey[key] = id;
        var n = new Node();
        n.id = id;
        // Node.xyz components are float in the STAAD codebase; cast explicitly.
        // Subtract the recenter offset BEFORE the cast so the float32 stays small.
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
