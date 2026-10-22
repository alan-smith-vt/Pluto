using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

// CaesarPipeImport  --  a CAESAR II neutral file (.cii) -> PipeModel that CaesarNeutralWriter writes back
// IDENTICALLY when nothing is edited. C# 5 / PowerShell 5.1 (scripts/caesar/Build-Pipe.ps1 -In job.cii).
//
// The model reads what it edits (element rows, bends, rigids, expansion joints, reducers, restraints, SIF /
// tees, forces / moments, imposed displacements, uniform loads, wind, COORDS, the ambient, title / notes) and
// carries the rest as written: every record and block with its number in CAESAR's own order, the restraint /
// SIF / load slots with the element block that held them, the blocks it does not read (offsets, allowables,
// node names, flanges, equipment; a load block in a layout it does not know) with their element, and the
// file's VERSION, CONTROL, UNITS, AUX_DATA, MISCEL_1 tail (nozzles, hangers, execution options) and any
// section it does not know. Values stay in the job's units.
//
// Load layouts (CAESAR II's documentation of the neutral file; FORCMNT, UNIFORM and WIND confirmed by a
// CAESAR 15.01 export, DISPLMNT seen in 11.00 jobs):
//   FORCMNT / DISPLMNT  2 slots per block, each the node (one value) then 9 lines, one per load vector:
//                       FX FY FZ MX MY MZ / DX DY DZ RX RY RZ; 9999.99 = not given (a free DOF)
//   UNIFORM             6 lines, 36 values in order: 9 vectors x (UX UY UZ, 1 = in g's)
//   WIND                1 line: type (0 wind, 1 wave, 2 off), shape factor (wave: drag), added mass, lift,
//                       marine growth, marine growth density
// UNIFORM and WIND are per element: every loaded element has its own block, an element without one has none.
//
// Every import checks itself: the model is written back in memory and compared with the source, line by
// line; the result says "identical" or names the lines that differ (and why it could not be identical).
//
//   $r = [CaesarPipeImport]::FromCii('<project dir>\job.cii'); $r.Identical; $r.Summary()

public class CaesarPipeImportResult
{
    public PipeModel Model;
    public CaesarModel Caesar;                // the reader's model of the source
    public string SourcePath = "";
    public bool Identical;                    // writing the model back gives the source, line for line
    public bool ByteIdentical;                // ... and byte for byte (FromCii only)
    public int SourceLines, WrittenLines;
    public readonly List<int> DifferingLines = new List<int>();     // 1-based, first 50
    public readonly List<string> Notes = new List<string>();        // why it is not identical, what was not carried
    public readonly List<string> Warnings = new List<string>();     // the reader's

    public string Summary()
    {
        var m = Model;
        var sb = new StringBuilder();
        sb.AppendLine("CaesarPipeImport: " + Path.GetFileName(SourcePath) + ", CAESAR " + Caesar.Version.ToString("0.00", CultureInfo.InvariantCulture) +
                      ", " + m.Elements.Count + " elements, " + m.Restraints.Count + " restraints, " + m.Sifs.Count + " SIF / tee entries, units " +
                      (Caesar.Units.LengthUnit.Length > 0 ? Caesar.Units.LengthUnit : "?"));
        sb.AppendLine("  loads: " + m.Forces.Count() + " force / moment and " + m.Displacements.Count() + " imposed-displacement node(s), " +
                      "uniform loads on " + m.Elements.Count(e => e.Uniform != null) + " element(s), wind on " + m.Elements.Count(e => e.Wind != null));
        var carried = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var e in m.Elements) foreach (var kv in e.Carried) { int c; carried.TryGetValue(kv.Key, out c); carried[kv.Key] = c + 1; }
        sb.AppendLine("  carried as written: " + (carried.Count == 0 ? "no load or other blocks" : string.Join(", ", carried.Select(kv => kv.Key + " " + kv.Value).ToArray())) +
                      (Caesar.NumHangers > 0 ? "; " + Caesar.NumHangers + " hanger(s)" : "") + (Caesar.NumNozzles > 0 ? "; " + Caesar.NumNozzles + " nozzle(s)" : ""));
        sb.AppendLine(Identical ? "  self-check: writing it back gives the source identically (" + SourceLines + " lines)"
                                : "  self-check: NOT identical: " + WrittenLines + " lines written for " + SourceLines + "; differing lines " + string.Join(", ", DifferingLines.Select(x => x.ToString(CultureInfo.InvariantCulture)).ToArray()));
        foreach (var n in Notes) sb.AppendLine("  note: " + n);
        if (Warnings.Count > 0) sb.AppendLine("  " + Warnings.Count + " reader warning(s)");
        return sb.ToString();
    }

    public string ToJson()
    {
        var sb = new StringBuilder("{");
        sb.Append("\"source\":" + Q(SourcePath) + ",\"caesarVersion\":" + Caesar.Version.ToString("R", CultureInfo.InvariantCulture));
        sb.Append(",\"elements\":" + Model.Elements.Count + ",\"restraints\":" + Model.Restraints.Count + ",\"sifs\":" + Model.Sifs.Count +
                  ",\"forces\":" + Model.Forces.Count() + ",\"displacements\":" + Model.Displacements.Count() +
                  ",\"uniform\":" + Model.Elements.Count(e => e.Uniform != null) + ",\"wind\":" + Model.Elements.Count(e => e.Wind != null));
        sb.Append(",\"identical\":" + (Identical ? "true" : "false") + ",\"byteIdentical\":" + (ByteIdentical ? "true" : "false") + ",\"sourceLines\":" + SourceLines + ",\"writtenLines\":" + WrittenLines);
        sb.Append(",\"differingLines\":[" + string.Join(",", DifferingLines.Select(x => x.ToString(CultureInfo.InvariantCulture)).ToArray()) + "]");
        sb.Append(",\"notes\":[" + string.Join(",", Notes.Select(Q).ToArray()) + "]");
        sb.Append(",\"warnings\":[" + string.Join(",", Warnings.Select(Q).ToArray()) + "]}");
        return sb.ToString();
    }

    static string Q(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (char c in s ?? "")
        {
            if (c == '"' || c == '\\') sb.Append('\\').Append(c);
            else if (c < 0x20 || c > 0x7e) sb.Append("\\u").Append(((int)c).ToString("x4"));
            else sb.Append(c);
        }
        return sb.Append('"').ToString();
    }
}

public static class CaesarPipeImport
{
    // sections the writer builds from the model (the others are carried as written)
    static readonly string[] Built = { "ELEMENTS", "BEND", "RIGID", "EXPJT", "RESTRANT", "DISPLMNT", "FORCMNT", "UNIFORM", "WIND", "SIF&TEES", "REDUCERS", "MISCEL_1", "COORDS" };

    public static CaesarPipeImportResult FromCii(string path)
    {
        var cm = CaesarNeutralReader.Read(path);
        var res = FromCaesar(cm, path);
        if (res.Identical)
        {
            byte[] src;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var ms = new MemoryStream()) { fs.CopyTo(ms); src = ms.ToArray(); }
            byte[] back = CaesarNeutralWriter.ToBytes(res.Model);
            res.ByteIdentical = back.Length == src.Length && back.SequenceEqual(src);
            if (!res.ByteIdentical) res.Notes.Add("the same lines, but the source's line endings or encoding are irregular (e.g. CR CR LF): written back with " +
                                                  (res.Model.Source.LineEnding == "\r\n" ? "CRLF" : "LF") + " in code page " + res.Model.Source.CodePage);
        }
        return res;
    }

    public static CaesarPipeImportResult FromCaesar(CaesarModel cm, string path)
    {
        var res = new CaesarPipeImportResult();
        res.Caesar = cm;
        res.SourcePath = path ?? "";
        res.Warnings.AddRange(cm.Warnings);
        var m = new PipeModel();
        res.Model = m;
        var src = new PipeModelSource();
        m.Source = src;
        src.FileName = Path.GetFileName(path ?? "");
        src.Version = cm.Version;
        src.VersionLine = cm.VersionLine;
        src.CodePage = cm.CodePage > 0 ? cm.CodePage : 1252;
        src.LineEnding = cm.LineEnding;
        src.FinalNewline = cm.FinalNewline;
        src.Prelude.AddRange(cm.Prelude);
        src.SectionOrder.AddRange(cm.SectionOrder);
        foreach (var kv in cm.RawHeaders) src.Headers[kv.Key] = kv.Value;
        foreach (var kv in cm.LinesPerBlock) src.LinesPerBlock[kv.Key] = kv.Value;
        foreach (var kv in cm.RawSections)
            if (!Built.Contains(kv.Key) && !CaesarNeutralWriter.CarriedSections.Contains(kv.Key)) src.Sections[kv.Key] = new List<string>(kv.Value);
        int ne = cm.Elements.Count;
        src.ElementCount = ne;
        if (cm.DuplicateSections.Count > 0) res.Notes.Add("the source repeats section(s) " + string.Join(", ", cm.DuplicateSections.ToArray()) + "; only the first of each is kept");

        // settings
        m.ZUp = cm.ZUp;
        m.Code = "";
        if (cm.Units.Factors.Any(f => !double.IsNaN(f))) m.UnitFactors = (double[])cm.Units.Factors.Clone();
        List<string> ver;
        if (src.Sections.TryGetValue("VERSION", out ver))
            foreach (string l in ver)
            {
                string t = l.TrimStart();
                if (t.StartsWith("PROJECT:", StringComparison.Ordinal)) m.Title = t.Substring(8).Trim();
                else if (t.StartsWith("NOTES  :", StringComparison.Ordinal)) m.Notes = t.Substring(8).Trim();
            }
        src.OriginalTitle = m.Title;
        src.OriginalNotes = m.Notes;

        // MISCEL_1: material lines, then the tail (nozzles, hangers, execution options) as written
        List<string> misc;
        if (cm.RawSections.TryGetValue("MISCEL_1", out misc))
        {
            misc = SplitTrailingBlanks(src, "MISCEL_1", misc);
            int matLines = Math.Min(misc.Count, (ne + 5) / 6);
            src.MiscelMaterialLines = misc.Take(matLines).ToList();
            src.MiscelTail = misc.Skip(matLines).ToList();
            double amb;
            if (src.MiscelTail.Count >= 4 && TryField(src.MiscelTail[src.MiscelTail.Count - 3], 2, out amb)) m.Ambient = amb;
            else res.Notes.Add("MISCEL_1: no execution-option lines; the ambient temperature is not known (70 F assumed for new files)");
        }

        // blocks of each section, as written
        var blocks = new Dictionary<string, List<List<string>>>();
        Func<string, int, List<List<string>>> slice = (s, count) =>
        {
            List<List<string>> b;
            if (blocks.TryGetValue(s, out b)) return b;
            List<string> lines;
            b = cm.RawSections.TryGetValue(s, out lines) ? CaesarNeutralReader.SliceBlocks(lines, count) : null;
            blocks[s] = b;
            return b;
        };
        var counts = new Dictionary<string, int>
        {
            { "BEND", cm.NumBends }, { "RIGID", cm.NumRigids }, { "EXPJT", cm.NumExpJoints }, { "RESTRANT", cm.NumRestraintBlocks },
            { "DISPLMNT", cm.NumDisplBlocks }, { "FORCMNT", cm.NumForceBlocks }, { "UNIFORM", cm.NumUniform }, { "WIND", cm.NumWind },
            { "OFFSETS", cm.NumOffsets }, { "ALLOWBLS", cm.NumAllowables }, { "SIF&TEES", cm.NumSifTees }, { "NODENAME", cm.NumNodeNames },
            { "REDUCERS", cm.NumReducers }, { "FLANGES", cm.NumFlanges }, { "EQUIPMNT", cm.NumEquipment }
        };
        // lines past a section's blocks (blank lines at the end of a file, a remainder) are carried as written
        foreach (var kv in counts)
        {
            List<string> lines;
            if (!cm.RawSections.TryGetValue(kv.Key, out lines) || lines.Count == 0) continue;
            int used = kv.Value > 0 && lines.Count >= kv.Value ? lines.Count / kv.Value * kv.Value : 0;
            if (used < lines.Count) src.Trailing[kv.Key] = lines.Skip(used).ToList();
        }
        Func<string, int, List<string>> block = (s, ptr) =>
        {
            var b = slice(s, counts[s]);
            return b != null && ptr > 0 && ptr <= b.Count ? new List<string>(b[ptr - 1]) : null;
        };
        var claimed = new HashSet<string>();   // section#block already given to an element
        int forceLines = src.Lines("FORCMNT", 0), uniLines = src.Lines("UNIFORM", 0), windLines = src.Lines("WIND", 0);
        bool forceSlots = forceLines >= 4 && forceLines % 2 == 0;   // 2 slots of node + vectors
        if (cm.NumForceBlocks > 0 && !forceSlots) res.Notes.Add("FORCMNT: " + forceLines + "-line blocks (20 documented): carried as written, not read");
        if (cm.NumUniform > 0 && uniLines != 6) res.Notes.Add("UNIFORM: " + uniLines + "-line blocks (6 documented): carried as written, not read");
        if (cm.NumWind > 0 && windLines != 1) res.Notes.Add("WIND: " + windLines + "-line blocks (1 documented): carried as written, not read");
        var forceOwner = new SortedDictionary<int, int>();   // FORCMNT block -> owner uid

        // elements
        for (int i = 0; i < ne; i++)
        {
            var ce = cm.Elements[i];
            var e = new PipeModelElement();
            e.Uid = i + 1;
            e.From = ce.From; e.To = ce.To;
            e.Dx = Z(ce.Real(0, 2)); e.Dy = Z(ce.Real(0, 3)); e.Dz = Z(ce.Real(0, 4)); e.Od = Z(ce.Real(0, 5));
            e.Wall = Z(ce.Real(1, 0)); e.Insulation = Z(ce.Real(1, 1)); e.Corrosion = Z(ce.Real(1, 2));
            for (int k = 0; k < 3; k++) e.T[k] = Z(ce.Real(1, 3 + k));
            for (int k = 0; k < 6; k++) e.T[3 + k] = Z(ce.Real(2, k));
            for (int k = 0; k < 6; k++) e.P[k] = Z(ce.Real(3, k));
            for (int k = 0; k < 3; k++) e.P[6 + k] = Z(ce.Real(4, k));
            e.Emod = Z(ce.Real(4, 3)); e.Poisson = Z(ce.Real(4, 4)); e.PipeDensity = Z(ce.Real(4, 5));
            e.InsulationDensity = Z(ce.Real(5, 0)); e.FluidDensity = Z(ce.Real(5, 1));
            double ta = ce.Real(7, 4), tb = ce.Real(7, 5);
            e.TeeAngle1 = double.IsNaN(ta) ? 9999.99 : ta; e.TeeAngle2 = double.IsNaN(tb) ? 9999.99 : tb;
            e.Material = ce.Material;
            e.Name = ce.Name;
            e.LineNumber = ce.LineNumberRaw;
            e.Raw = new List<string>(ce.RecordLines);
            if (ce.Bend > 0 && Claim(claimed, "BEND", ce.Bend, res, e))
            {
                var cb = cm.Bends[ce.Bend - 1];
                var b = new PipeModelBend { Radius = cb.Radius, Type = cb.TypeCode, MiterPoints = cb.MiterPoints, FittingThickness = cb.FittingThickness, KFactor = cb.KFactor, Seq = ce.Bend, Raw = block("BEND", ce.Bend) };
                foreach (var an in cb.AngleNodes) b.AngleNodes.Add(new[] { an[0], an[1] });
                e.Bend = b;
                e.Kind = "bend";
            }
            if (ce.Rigid > 0 && Claim(claimed, "RIGID", ce.Rigid, res, e))
            {
                var cr = cm.Rigids[ce.Rigid - 1];
                e.Rigid = new PipeModelRigid { Weight = cr.Weight, Type = cr.TypeCode, Seq = ce.Rigid, Raw = block("RIGID", ce.Rigid) };
                e.Kind = cr.TypeCode == 1 ? "valve" : cr.TypeCode == 2 ? "flange" : cr.TypeCode == 3 ? "flange_pair" : cr.TypeCode == 4 ? "flanged_valve" : "rigid";
            }
            if (ce.ExpJt > 0 && Claim(claimed, "EXPJT", ce.ExpJt, res, e))
            {
                var x = cm.ExpJoints[ce.ExpJt - 1];
                e.ExpJoint = new PipeModelExpJoint
                {
                    AxialStiffness = Z(x.AxialStiffness), TransverseStiffness = Z(x.TransverseStiffness), BendingStiffness = Z(x.BendingStiffness),
                    TorsionStiffness = Z(x.TorsionalStiffness), EffectiveId = Z(x.EffectiveId), Seq = ce.ExpJt, Raw = block("EXPJT", ce.ExpJt)
                };
                e.Kind = "expansion_joint";
            }
            if (ce.Reducer > 0 && Claim(claimed, "REDUCERS", ce.Reducer, res, e))
            {
                var r = cm.Reducers[ce.Reducer - 1];
                e.Reducer = new PipeModelReducer { Od2 = r.Od2, Wall2 = r.Wall2, Alpha = r.Alpha, R1 = r.R1, R2 = r.R2, Seq = ce.Reducer, Raw = block("REDUCERS", ce.Reducer) };
                e.Kind = "reducer";
            }
            int[] ptrs = { ce.Forcmnt, ce.Uniform, ce.Wind, ce.Offsets, ce.Allowbls, ce.NodeName, ce.Flange, ce.Equipmnt };
            string[] names = { "FORCMNT", "UNIFORM", "WIND", "OFFSETS", "ALLOWBLS", "NODENAME", "FLANGES", "EQUIPMNT" };
            for (int k = 0; k < names.Length; k++)
            {
                if (ptrs[k] <= 0 || !Claim(claimed, names[k], ptrs[k], res, e)) continue;
                var lines = block(names[k], ptrs[k]);
                if (lines == null) { res.Notes.Add(names[k] + " block " + ptrs[k] + " of element " + e.From + "-" + e.To + " is not in the file"); continue; }
                if (names[k] == "FORCMNT" && forceSlots) { e.SlotBlocks["FORCMNT"] = new PipeModelBlock { Seq = ptrs[k], Lines = lines }; forceOwner[ptrs[k]] = e.Uid; continue; }
                if (names[k] == "UNIFORM" && uniLines == 6) { e.Uniform = Uniform(lines); e.Uniform.Seq = ptrs[k]; continue; }
                if (names[k] == "WIND" && windLines == 1) { e.Wind = Wind(lines); e.Wind.Seq = ptrs[k]; continue; }
                e.Carried[names[k]] = new PipeModelBlock { Seq = ptrs[k], Lines = lines };
            }
            int[] sptr = { ce.Restraint, ce.SifTee, ce.Displmnt };
            string[] snames = { "RESTRANT", "SIF&TEES", "DISPLMNT" };
            for (int k = 0; k < snames.Length; k++)
            {
                if (sptr[k] <= 0 || !Claim(claimed, snames[k], sptr[k], res, e)) continue;
                var lines = block(snames[k], sptr[k]);
                if (lines != null) e.SlotBlocks[snames[k]] = new PipeModelBlock { Seq = sptr[k], Lines = lines };
            }
            m.Elements.Add(e);
        }

        // restraints, SIF / tees, displacements: their slots as written and the element block that held them
        int rsl = cm.LinesPerBlock.ContainsKey("RESTRANT/slot") ? cm.LinesPerBlock["RESTRANT/slot"] : 4;
        foreach (var cr in cm.Restraints)
        {
            var r = new PipeModelRestraint
            {
                Node = cr.Node, Type = cr.TypeName, Code = cr.TypeCode, Stiffness = cr.Stiffness, Gap = cr.Gap, Mu = cr.Friction, CNode = cr.CNode,
                Cosines = cr.Cosines != null ? (double[])cr.Cosines.Clone() : null, Tag = cr.Tag, Guid = cr.Guid, Source = "imported",
                OwnerUid = cr.ElementIndex >= 0 ? cr.ElementIndex + 1 : 0, Slot = cr.Slot, Raw = SlotLines(slice("RESTRANT", counts["RESTRANT"]), cr.Block, cr.Slot, rsl)
            };
            if (r.OwnerUid == 0) res.Notes.Add("restraint at node " + cr.Node + " sits in a block no element points to: it is placed anew");
            m.Restraints.Add(r);
        }
        int ssl = cm.LinesPerBlock.ContainsKey("SIF&TEES/slot") ? cm.LinesPerBlock["SIF&TEES/slot"] : 7;
        foreach (var ct in cm.SifTees)
        {
            m.Sifs.Add(new PipeModelSif
            {
                Node = ct.Node, Type = ct.TypeCode, SifIn = ct.SifIn, SifOut = ct.SifOut, SurfaceNode = ct.SurfaceNode,
                OwnerUid = ct.ElementIndex >= 0 ? ct.ElementIndex + 1 : 0, Slot = ct.Slot, Raw = SlotLines(slice("SIF&TEES", counts["SIF&TEES"]), ct.Block, ct.Slot, ssl)
            });
        }
        int dsl = cm.LinesPerBlock.ContainsKey("DISPLMNT/slot") ? cm.LinesPerBlock["DISPLMNT/slot"] : 10;
        foreach (var cd in cm.Displacements)
        {
            var raw = SlotLines(slice("DISPLMNT", counts["DISPLMNT"]), cd.Block, cd.Slot, dsl);
            var d = raw != null ? NodeLoad("displacement", raw) : null;
            if (d == null)
            {
                d = new PipeModelNodeLoad("displacement") { Node = cd.Node, Raw = raw };
                for (int v = 0; v < 9 && cd.Vectors != null && v < cd.Vectors.Length; v++) for (int k = 0; k < 6; k++) d.Vectors[v][k] = cd.Vectors[v][k];
            }
            d.OwnerUid = cd.ElementIndex >= 0 ? cd.ElementIndex + 1 : 0;
            d.Slot = cd.Slot;
            d.Source = "imported";
            if (raw == null) res.Notes.Add("imposed displacement at node " + cd.Node + ": its slot lines are not in the file; it is written anew");
            m.NodeLoads.Add(d);
        }
        // forces / moments: the slots of the blocks elements name (CAESAR's documented layout: 2 slots per block)
        var fblocks = forceSlots ? slice("FORCMNT", counts["FORCMNT"]) : null;
        foreach (var kv in forceOwner)
        {
            if (fblocks == null || kv.Key > fblocks.Count) continue;
            var b = fblocks[kv.Key - 1];
            int sl = b.Count / 2;
            for (int q = 0; q < 2; q++)
            {
                var raw = b.GetRange(q * sl, sl);
                var f = NodeLoad("force", raw);
                if (f == null) continue;   // an unused slot (node 0): given back as written
                f.OwnerUid = kv.Value; f.Slot = q; f.Raw = raw; f.Source = "imported";
                m.NodeLoads.Add(f);
            }
        }

        // COORDS: the count line and each entry as written
        List<string> coords;
        if (cm.RawSections.TryGetValue("COORDS", out coords))
        {
            coords = SplitTrailingBlanks(src, "COORDS", coords);
            var entries = coords.Where(l => l.Trim().Length > 0).ToList();
            if (coords.Count != entries.Count) res.Notes.Add("COORDS has blank lines, which are not kept");
            if (entries.Count > 0) src.CoordsCountLine = entries[0];
            for (int k = 0; k < cm.Coords.Count; k++)
            {
                var c = cm.Coords[k];
                var pc = new PipeModelCoord { Node = (int)Math.Round(c[0]), X = c[1], Y = c[2], Z = c[3], Raw = k + 1 < entries.Count ? entries[k + 1] : null };
                pc.ImportedXyz = new[] { c[1], c[2], c[3] };
                m.Coords.Add(pc);
            }
        }

        // node positions (job length units), CAESAR's rules
        try
        {
            var g = CaesarGeometry.Build(cm);
            foreach (var e in m.Elements)
                foreach (int n in new[] { e.From, e.To })
                {
                    var pos = g.Position(n);
                    if (pos != null) m.Positions[n] = new[] { pos[0], pos[1], pos[2] };
                }
        }
        catch (Exception ex) { res.Notes.Add("node positions not computed: " + ex.Message); }

        var coded = m.Elements.Where(e => e.Bend != null && PipeModelBend.RadiusCode(e.Bend.Radius) > 0).ToList();
        if (coded.Count > 0)
            res.Notes.Add(coded.Count + " bend(s) carry a coded radius CAESAR wrote (" + string.Join(", ", coded.Select(e => e.Bend.Radius.ToString("0.0###", CultureInfo.InvariantCulture)).Distinct().ToArray()) +
                          ": a radius chosen by name, e.g. at element " + coded[0].From + "-" + coded[0].To + "): kept as written, so CAESAR resolves it as before; the viewer draws them as sharp corners until the code is confirmed");
        SelfCheck(res);
        return res;
    }

    // the global sections of a CAESAR file as a seed for a new model: VERSION, UNITS, the execution options
    // (not its nozzles or hangers) and the first allowables block; the factors of its units
    public static PipeModelSource SeedFrom(CaesarModel cm, string path, out List<string> allowables, out double[] unitFactors)
    {
        var s = new PipeModelSource { Seed = true, FileName = Path.GetFileName(path ?? ""), Version = cm.Version, VersionLine = cm.VersionLine, ElementCount = -1 };
        List<string> l;
        if (cm.RawSections.TryGetValue("VERSION", out l)) s.Sections["VERSION"] = new List<string>(l);
        if (cm.RawSections.TryGetValue("UNITS", out l)) s.Sections["UNITS"] = new List<string>(l);
        if (cm.RawSections.TryGetValue("MISCEL_1", out l) && l.Count >= 4) s.MiscelTail = l.Skip(l.Count - 4).ToList();
        allowables = null;
        if (cm.RawSections.TryGetValue("ALLOWBLS", out l))
        {
            var b = CaesarNeutralReader.SliceBlocks(l, cm.NumAllowables);
            if (b != null && b.Count > 0) allowables = new List<string>(b[0]);
        }
        unitFactors = cm.Units.Factors.Any(f => !double.IsNaN(f)) ? (double[])cm.Units.Factors.Clone() : null;
        return s;
    }

    // A new model takes a CAESAR job's settings (Build-Pipe.ps1 -Seed): its VERSION lines, UNITS, execution
    // options and first allowables block (unless the model has its own). A model in English units (the TOML
    // builder's) takes only an English seed. Returns a one-line description.
    public static string ApplySeed(PipeModel m, string seedPath, bool modelIsEnglish)
    {
        var cm = CaesarNeutralReader.Read(seedPath);
        List<string> allow;
        double[] f;
        var s = SeedFrom(cm, seedPath, out allow, out f);
        string unit = cm.Units.LengthUnit.Length > 0 ? cm.Units.LengthUnit : "?";
        bool english = f == null || new[] { 0, 1, 2, 6, 7, 8 }.All(i => Math.Abs(f[i] - PipeModelCodes.EnglishFactors[i]) < 1e-6);
        if (modelIsEnglish && !english)
            throw new InvalidOperationException("seed " + Path.GetFileName(seedPath) + " is not in CAESAR's English units (length " + unit + "): this model is in English units, so it takes an English seed");
        if (!s.Sections.ContainsKey("UNITS") || s.MiscelTail.Count < 4)
            throw new InvalidOperationException("seed " + Path.GetFileName(seedPath) + " has no UNITS section or execution options: not a usable CAESAR neutral file");
        m.Source = s;
        if (m.AllowablesBlock == null && allow != null) m.AllowablesBlock = allow;
        if (!modelIsEnglish) m.UnitFactors = f;
        return "seed " + Path.GetFileName(seedPath) + " (CAESAR " + cm.Version.ToString("0.00", CultureInfo.InvariantCulture) + ", " + unit + "): VERSION lines, UNITS, execution options" +
               (allow != null && m.AllowablesBlock == allow ? ", allowables block" : "");
    }

    // write the model back in memory and compare with the source, line by line
    public static void SelfCheck(CaesarPipeImportResult res)
    {
        var cm = res.Caesar;
        var source = new List<string>(cm.Prelude);
        foreach (string name in cm.SectionOrder)
        {
            source.Add(cm.RawHeaders[name]);
            source.AddRange(cm.RawSections[name]);
        }
        List<string> written;
        try { written = CaesarNeutralWriter.ToLines(res.Model); }
        catch (Exception ex) { res.Notes.Add("the model could not be written back: " + ex.Message); res.Identical = false; return; }
        res.SourceLines = source.Count;
        res.WrittenLines = written.Count;
        int n = Math.Max(source.Count, written.Count);
        for (int i = 0; i < n; i++)
        {
            string a = i < source.Count ? source[i] : null, b = i < written.Count ? written[i] : null;
            if (a != b) { if (res.DifferingLines.Count < 50) res.DifferingLines.Add(i + 1); else break; }
        }
        res.Identical = res.DifferingLines.Count == 0 && cm.DuplicateSections.Count == 0;
    }

    // a section's trailing blank lines go to Trailing (written back after the section); the rest is returned
    static List<string> SplitTrailingBlanks(PipeModelSource src, string section, List<string> lines)
    {
        int end = lines.Count;
        while (end > 0 && lines[end - 1].Trim().Length == 0) end--;
        if (end < lines.Count) src.Trailing[section] = lines.Skip(end).ToList();
        return lines.Take(end).ToList();
    }

    // a FORCMNT / DISPLMNT slot: the node, then one line of 6 values per vector; null for an unused slot (node 0)
    public static PipeModelNodeLoad NodeLoad(string kind, List<string> lines)
    {
        if (lines == null || lines.Count < 2) return null;
        int fb = 0;
        var first = CaesarNeutralReader.Nums(lines[0], ref fb);
        if (first.Length < 1 || Math.Round(first[0]) <= 0) return null;
        var d = new PipeModelNodeLoad(kind) { Node = (int)Math.Round(first[0]), Raw = new List<string>(lines) };
        for (int v = 0; v < 9 && v + 1 < lines.Count; v++)
        {
            var x = CaesarNeutralReader.Nums(lines[v + 1], ref fb);
            for (int k = 0; k < 6 && k < x.Length; k++)
                d.Vectors[v][k] = Math.Abs(x[k] - PipeModelNodeLoad.Free) < 0.01 ? double.NaN : x[k];
        }
        return d;
    }

    // a UNIFORM block: 36 values in order, 9 vectors x (UX UY UZ, g flag)
    public static PipeModelUniform Uniform(List<string> lines)
    {
        var u = new PipeModelUniform { Raw = new List<string>(lines) };
        var vals = Values(lines, 36);
        for (int v = 0; v < 9; v++)
        {
            for (int k = 0; k < 3; k++) u.Vectors[v][k] = vals[4 * v + k];
            u.G[v] = Math.Round(vals[4 * v + 3]) == 1;
        }
        return u;
    }

    // a WIND block: type, shape factor (or drag), added mass, lift, marine growth, its density
    public static PipeModelWind Wind(List<string> lines)
    {
        var x = Values(lines, 6);
        return new PipeModelWind { Type = (int)Math.Round(x[0]), Shape = x[1], AddedMass = x[2], Lift = x[3], MarineGrowth = x[4], MarineGrowthDensity = x[5], Raw = new List<string>(lines) };
    }

    // the 13-column fields of a block's lines in order (6 per line), n of them (missing ones 0)
    static double[] Values(List<string> lines, int n)
    {
        var r = new double[n];
        for (int i = 0; i < n; i++)
        {
            int li = i / 6, col = i % 6;
            double v;
            if (li < lines.Count && TryField(lines[li], col, out v)) r[i] = v;
        }
        return r;
    }

    static List<string> SlotLines(List<List<string>> blocks, int block, int slot, int slotLines)
    {
        if (blocks == null || block <= 0 || block > blocks.Count || slot < 0) return null;
        var b = blocks[block - 1];
        if ((slot + 1) * slotLines > b.Count) return null;
        return b.GetRange(slot * slotLines, slotLines);
    }

    // a block goes to the first element that names it (CAESAR gives each block one owner)
    static bool Claim(HashSet<string> claimed, string section, int block, CaesarPipeImportResult res, PipeModelElement e)
    {
        if (claimed.Add(section + "#" + block.ToString(CultureInfo.InvariantCulture))) return true;
        res.Notes.Add(section + " block " + block + " is named by element " + e.From + "-" + e.To + " too; that element is written without it");
        return false;
    }

    static bool TryField(string line, int col, out double v)
    {
        v = 0;
        int at = 2 + 13 * col;
        if (line == null || line.Length <= at) return false;
        string f = line.Substring(at, Math.Min(13, line.Length - at)).Trim();
        return double.TryParse(f, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
    }

    static double Z(double v) { return double.IsNaN(v) ? 0.0 : v; }
}
