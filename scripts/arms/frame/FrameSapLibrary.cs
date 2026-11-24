using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

// FrameSapLibrary  --  SAP2000's section libraries, for the catalog sections an .s2k names (2026-11-23;
// vault/format/sap-s2k-tables.md). A catalog section's row names its library file and the shape in it (FromFile=Yes,
// SectInFile, FileName). SAP 24.2 (the catalog probe, 2026-11-23):
//   - builds the section from that file; given the reference alone it reads the rest from the file;
//   - finds a bare or wrong path's file name again in its own library folder, with a dialog for every section;
//   - makes a file it cannot find at all a General section whose every property is 1, with one dialog.
// So before an .s2k is written every catalog row's file is checked: a path this machine has is kept, a file name SAP's
// library has gets that full path, and anything else (or a shape the file does not hold) refuses the model.
// A shape picked from STAAD's table (TABLE ST, no SAP row in the .std) is read from the library's AISC file (Row): an
// entry is an element per shape, its kind as the tag (<STEEL_I_SECTION> <LABEL>W10X33</LABEL> <EDI_STD>W10X33</EDI_STD>
// <D>9.73</D> ...), its lengths in inches (SAP's own W10X33 and W12X26 rows in a Kip-in model give the AISC15.xml
// entries' numbers, 2026-11-23). The kinds' fields (the user's entries, 2026-11-23):
//   STEEL_I_SECTION     D BF TF TW KDES A I33 Z33 AS3 I22 Z22 AS2 J S33POS S33NEG S22POS S22NEG R33 R22
//   STEEL_CHANNEL       the same and X (the centroid from the back of the web)
//   STEEL_TEE           the same and Y (the centroid from the flange's top)
//   STEEL_BOX           HT B TF TW A I33 Z33 AS3 I22 Z22 AS2 J S33POS S33NEG S22POS S22NEG R33 R22 (TF, TW the design wall)
//   STEEL_PIPE          OD TDES A I33 Z33 AS2 J S33POS S33NEG R33 (one axis: a circle)
//   STEEL_ANGLE         D B TF TW KDES A I33 Z33 AS3 I22 Z22 AS2 J X Y S33POS S33NEG S22POS S22NEG R33 R22 (no I23)
//   STEEL_DOUBLE_ANGLE  D B TF TW DIS A I33 ... J Y S33POS ... R22
// C# 5 (prefix Frame).
public static class FrameSapLibrary
{
    // SAP's section library: <Program Files>\Computers and Structures\SAP2000 <major>\Property Libraries\Sections, for
    // the model's version (its major number), else the newest installed; null when this machine has none
    public static string Folder(string version)
    {
        var roots = new List<string>();
        foreach (string r in new[] { Environment.GetEnvironmentVariable("ProgramW6432"), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetEnvironmentVariable("ProgramFiles") })
            if (!string.IsNullOrEmpty(r) && !roots.Contains(r, StringComparer.OrdinalIgnoreCase)) roots.Add(r);
        var found = new List<string>();
        foreach (string r in roots)
        {
            string csi = Path.Combine(r, "Computers and Structures");
            if (!Directory.Exists(csi)) continue;
            foreach (string d in Directory.GetDirectories(csi, "SAP2000 *"))
            {
                string sec = Path.Combine(Path.Combine(d, "Property Libraries"), "Sections");
                if (Directory.Exists(sec)) found.Add(sec);
            }
        }
        if (found.Count == 0) return null;
        string major = (version ?? "").Split('.')[0].Trim();
        var mine = found.FirstOrDefault(f => Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(f))).Equals("SAP2000 " + major, StringComparison.OrdinalIgnoreCase));
        return mine ?? found.OrderBy(f => Major(f)).Last();
    }

    static int Major(string sections)
    {
        int v;
        string d = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(sections)));
        return int.TryParse(d.Substring(d.LastIndexOf(' ') + 1), out v) ? v : 0;
    }

    // a file's name from a path written on any machine (SAP writes Windows paths)
    static string FileOf(string path)
    {
        int k = path.LastIndexOfAny(new[] { '\\', '/' });
        return k < 0 ? path : path.Substring(k + 1);
    }

    // whether a library file names the shape (a whole word: W10X33, not W10X33A)
    static bool Holds(string file, string shape, Dictionary<string, string> cache)
    {
        string text;
        if (!cache.TryGetValue(file, out text)) cache[file] = text = File.ReadAllText(file);
        return Regex.IsMatch(text, "(?<![A-Za-z0-9.])" + Regex.Escape(shape) + "(?![A-Za-z0-9.])", RegexOptions.IgnoreCase);
    }

    // the library's US AISC files (AISC15.xml, AISC14.xml ...; not the metric AISC15M.xml), newest edition first: STAAD's
    // AMERICAN table names its shapes as AISC does (W10X33)
    static List<string> AiscFiles(string folder)
    {
        return Directory.GetFiles(folder, "*.xml").Select(f => new { F = f, M = Regex.Match(Path.GetFileName(f), @"^AISC(\d+)\.xml$", RegexOptions.IgnoreCase) })
                        .Where(x => x.M.Success).OrderByDescending(x => int.Parse(x.M.Groups[1].Value, CultureInfo.InvariantCulture)).Select(x => x.F).ToList();
    }

    // a library file's entries: the shape's label (upper case) -> its element name and fields
    static readonly Dictionary<string, Dictionary<string, KeyValuePair<string, Dictionary<string, string>>>> Entries =
        new Dictionary<string, Dictionary<string, KeyValuePair<string, Dictionary<string, string>>>>(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, string> Header = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    static Dictionary<string, KeyValuePair<string, Dictionary<string, string>>> EntriesOf(string file)
    {
        Dictionary<string, KeyValuePair<string, Dictionary<string, string>>> d;
        if (Entries.TryGetValue(file, out d)) return d;
        string text = File.ReadAllText(file);
        d = new Dictionary<string, KeyValuePair<string, Dictionary<string, string>>>(StringComparer.OrdinalIgnoreCase);
        var alias = new List<KeyValuePair<string, KeyValuePair<string, Dictionary<string, string>>>>();
        int first = -1;
        foreach (Match e in Regex.Matches(text, @"<(\w+)>\s*<LABEL>\s*([^<]*?)\s*</LABEL>(.*?)</\1>", RegexOptions.Singleline))
        {
            if (first < 0) first = e.Index;
            var f = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match x in Regex.Matches(e.Groups[3].Value, @"<(\w+)>\s*([^<]*?)\s*</\1>")) f[x.Groups[1].Value] = x.Groups[2].Value;
            string label = e.Groups[2].Value.ToUpperInvariant();
            f["LABEL"] = e.Groups[2].Value;
            var entry = new KeyValuePair<string, Dictionary<string, string>>(e.Groups[1].Value, f);
            if (!d.ContainsKey(label)) d[label] = entry;
            // a shape is found by its label or its EDI name (HSS24X12X3/4, HSS24X12X.750): labels first
            string edi;
            if (f.TryGetValue("EDI_STD", out edi) && edi.Length > 0 && !d.ContainsKey(edi.ToUpperInvariant())) alias.Add(new KeyValuePair<string, KeyValuePair<string, Dictionary<string, string>>>(edi.ToUpperInvariant(), entry));
        }
        foreach (var a in alias) if (!d.ContainsKey(a.Key)) d[a.Key] = a.Value;
        Header[file] = first < 0 ? text : text.Substring(0, first);
        return Entries[file] = d;
    }

    // a length unit the file's header states that is not inches (null: none stated, or inches)
    static string OtherUnit(string file)
    {
        foreach (Match u in Regex.Matches(Header[file], @"<(\w*UNIT\w*)>\s*([^<]*?)\s*</\1>", RegexOptions.IgnoreCase))
        {
            string tag = u.Groups[1].Value.ToUpperInvariant(), v = u.Groups[2].Value.Trim().ToLowerInvariant();
            bool length = tag.Contains("LENGTH") || Regex.IsMatch(v, @"^(in|inch|inches|ft|foot|feet|mm|cm|m|meter|meters|metre|metres|millimeter|millimeters)$");
            if (length && !Regex.IsMatch(v, @"^(in|inch|inches)$")) return u.Groups[2].Value.Trim();
        }
        return null;
    }

    // inches -> the model's length unit
    static readonly Dictionary<string, double> PerInch = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) {
        { "in", 1 }, { "ft", 1.0 / 12 }, { "mm", 25.4 }, { "cm", 2.54 }, { "m", 0.0254 } };

    // an entry of the library: the newest AISC file with one of these names (labels first, then EDI names) that accept
    // takes (null: taken; else why not); null with why when none
    static KeyValuePair<string, Dictionary<string, string>>? Find(string folder, string[] names, Func<string, Dictionary<string, string>, string> accept, out string file, out string why)
    {
        file = null; why = null;
        var files = AiscFiles(folder);
        if (files.Count == 0) { why = "SAP's library " + folder + " has no AISC file (AISC15.xml)"; return null; }
        string refused = null;
        foreach (string f in files)
            foreach (string n in names)
            {
                KeyValuePair<string, Dictionary<string, string>> e;
                if (!EntriesOf(f).TryGetValue(n.Trim(), out e)) continue;
                string no = accept == null ? null : accept(e.Key.ToUpperInvariant(), e.Value);
                if (no != null) { if (refused == null) refused = no; continue; }
                file = f;
                return e;
            }
        why = refused ?? "no " + string.Join(" or ", names) + " in SAP's library (" + string.Join(", ", files.Select(f => Path.GetFileName(f)).ToArray()) + " in " + folder + ")";
        return null;
    }

    // a shape's SAP row (all but SectionName and Material) from SAP's library, as SAP writes a catalog section's: the
    // entry of the newest AISC file that has it (by label or EDI name), FromFile=Yes, SectInFile its label, and the file by
    // its full path. SAP builds the section from the file (the catalog probe); the numbers are for Pluto, in the model's
    // length unit (the file's text as it is in inches). null with why when no file has it, or Pluto does not read its kind.
    // SAP's own rows, from the library probe (2026-11-23; equal to Pluto's in every field Pluto writes):
    //   all     the file's properties as named (Area A, TorsConst J, I33 I22 AS2 AS3 Z33 Z22 R33 R22), I23 0, S33Top /
    //           S33Bot S33POS / S33NEG, S22Left / S22Right S22POS / S22NEG (a pipe's one axis is both), ConcCol ConcBeam No
    //   I       t3 D, t2 / t2b BF, tf / tfb TF, tw TW, FilletRadius KDES - TF, Cw = TF BF^3 (D - TF)^2 / 24
    //   Channel t3 D, t2 BF, tf TF, tw TW, FilletRadius KDES - TF, CGOffset3 = t2 / 2 - X
    //   Tee     the same, CGOffset2 = t3 / 2 - Y
    //   Box/Tube  t3 HT, t2 B, tf TF, tw TW, FilletRadius 0, Cw 0;  Pipe  t3 OD, tw TDES, Cw 0
    //   Double Angle  t3 D, t2 B, SngAngWid (B - DIS) / 2, tf TF, tw TW, dis DIS, FilletRadius 0, CGOffset2 = t3 / 2 - Y
    //   Angle   t3 D, t2 B, tf TF, tw TW, FilletRadius KDES - TF, CGOffset3 = t2 / 2 - X, CGOffset2 = Y - t3 / 2, and an I23
    //           SAP works out itself (0.424146744778473 on L3X2-1/2X3/16): Pluto's is the legs' and the root fillet's
    //           (AngleI23; 0.424899, 0.18 % more), which sets the frame's orientation (StdToFrame) within about 0.02 deg
    // What SAP works out from the file and Pluto does not (a channel's, tee's or double angle's shear center EccV2 / EccV3
    // and Cw) is left out; SAP reads the file
    public static string Row(string folder, string shape, string lengthUnit, out string file, out string why)
    {
        return RowOf(folder, new[] { shape }, null, lengthUnit, out file, out why);
    }

    // a tee STAAD cuts from a W (TABLE ... T 'W44X262'): SAP's WT of half its depth and weight (WT22X131)
    public static string TeeName(string w)
    {
        var m = Regex.Match((w ?? "").Trim(), @"^W(\d+(?:\.\d+)?)X(\d+(?:\.\d+)?)$", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        Func<string, string> half = x => (decimal.Parse(x, CultureInfo.InvariantCulture) / 2).ToString(CultureInfo.InvariantCulture);
        return "WT" + Trim(half(m.Groups[1].Value)) + "X" + Trim(half(m.Groups[2].Value));
    }

    public static string TeeRow(string folder, string w, string lengthUnit, out string file, out string why)
    {
        string wt = TeeName(w);
        if (wt == null) { file = null; why = "a tee cut from " + w + ", not a W shape"; return null; }
        return RowOf(folder, new[] { wt }, (k, f) => k == "STEEL_TEE" ? null : wt + " is not a tee in SAP's library", lengthUnit, out file, out why);
    }

    // a double angle STAAD gives as TABLE ... LD / SD 'L4X3X1/4' [SP s]: SAP's 2L entry of that angle whose vertical legs
    // are the long (LD) or short (SD) ones and whose spacing is s (the model's length unit), by its label 2L4X3X1/4[LLBB |
    // SLBB]
    public static string DoubleAngleRow(string folder, string angle, bool longLegs, double spacing, string lengthUnit, out string file, out string why)
    {
        file = null; why = null;
        double per;
        if (PerInch.TryGetValue(lengthUnit ?? "", out per)) spacing /= per;                // the model's unit -> the file's inches
        var m = Regex.Match((angle ?? "").Trim(), @"^L([\d./-]+)X([\d./-]+)X([\d./-]+)$", RegexOptions.IgnoreCase);
        decimal a, b;
        if (!m.Success || !Frac(m.Groups[1].Value, out a) || !Frac(m.Groups[2].Value, out b)) { why = angle + " is not an angle's name (L4X3X1/4)"; return null; }
        decimal vertical = longLegs ? Math.Max(a, b) : Math.Min(a, b);
        string n = angle.Trim().ToUpperInvariant();
        var names = a == b ? new[] { "2" + n } : new[] { "2" + n + (longLegs ? "LLBB" : "SLBB"), "2" + n };
        return RowOf(folder, names, (k, f) =>
        {
            if (k != "STEEL_DOUBLE_ANGLE") return "2" + n + " is not a double angle in SAP's library";
            decimal? d = Dec(f.ContainsKey("D") ? f["D"] : null), dis = Dec(f.ContainsKey("DIS") ? f["DIS"] : "0");
            if (!d.HasValue || d.Value != vertical) return f["LABEL"] + " in SAP's library has legs of " + (d.HasValue ? d.Value.ToString(CultureInfo.InvariantCulture) : "?") + " back to back, not the " + (longLegs ? "long" : "short") + " ones (" + vertical.ToString(CultureInfo.InvariantCulture) + ")";
            if (!dis.HasValue || Math.Abs((double)dis.Value - spacing) > 1e-9 * Math.Max(1, spacing)) return f["LABEL"] + " in SAP's library is spaced " + (dis.HasValue ? dis.Value.ToString(CultureInfo.InvariantCulture) : "?") + " in, not " + spacing.ToString("R", CultureInfo.InvariantCulture) + " (Pluto knows SAP's labels for angles back to back with no spacing only)";
            return null;
        }, lengthUnit, out file, out why);
    }

    // a library file's EDI name for one of its labels (HSS24X12X3/4 -> HSS24X12X.750, as STAAD 2025 names it): the file
    // the row names, else the file of that name in SAP's library on this machine (as Check finds it); null when neither
    // is here or it has no such label
    public static string EdiName(string file, string label)
    {
        if (string.IsNullOrEmpty(file) || string.IsNullOrEmpty(label)) return null;
        if (!File.Exists(file))
        {
            string folder = Folder(""), name = FileOf(file);
            file = folder == null || name.Length == 0 ? null : Directory.GetFiles(folder).FirstOrDefault(f => Path.GetFileName(f).Equals(name, StringComparison.OrdinalIgnoreCase));
            if (file == null) return null;
        }
        KeyValuePair<string, Dictionary<string, string>> e;
        string edi;
        return EntriesOf(file).TryGetValue(label.Trim(), out e) && e.Value.TryGetValue("EDI_STD", out edi) && edi.Length > 0 ? edi : null;
    }

    static string RowOf(string folder, string[] names, Func<string, Dictionary<string, string>, string> accept, string lengthUnit, out string file, out string why)
    {
        file = null; why = null;
        double per;
        if (!PerInch.TryGetValue(lengthUnit ?? "", out per)) { why = "the model's length unit " + lengthUnit + " is not one Pluto converts the library's inches to"; return null; }
        var found = Find(folder, names, accept, out file, out why);
        if (found == null) return null;
        string fn = Path.GetFileName(file), other = OtherUnit(file);
        if (other != null) { why = fn + " gives its lengths in " + other + "; Pluto reads the AISC files' inches"; return null; }
        string kind = found.Value.Key.ToUpperInvariant(), label = found.Value.Value["LABEL"];
        var v = found.Value.Value;
        Func<string, bool> has = k => v.ContainsKey(k) && Dec(v[k]).HasValue;
        Func<string, decimal> num = k => Dec(v[k]).Value;
        var w = new List<string>();
        Action<string, string, int> put = (field, key, pow) => w.Add(field + "=" + Len(num(key), per, pow));
        Action<string, string, int> opt = (field, key, pow) => { if (has(key)) put(field, key, pow); };
        Action<string, decimal> len = (field, x) => w.Add(field + "=" + Len(x, per, 1));
        Func<string[], string> lacks = keys =>
        {
            var missing = keys.Where(k => !has(k)).ToList();
            return missing.Count == 0 ? null : label + " in " + fn + ": no " + string.Join(", ", missing.ToArray());
        };
        Action<string, string> props = (i22, as3) =>
        {
            put("Area", "A", 2); put("TorsConst", "J", 4); put("I33", "I33", 4); put("I22", i22, 4); w.Add("I23=0"); put("AS2", "AS2", 2); put("AS3", as3, 2);
        };
        Action<string, string> moduli = (s22, z22r22) =>
        {
            opt("S33Top", "S33POS", 3); opt("S33Bot", "S33NEG", 3);
            if (s22 != null) { opt("S22Left", s22 + "POS", 3); opt("S22Right", s22 + "NEG", 3); }
            opt("Z33", "Z33", 3); opt("Z22", "Z" + z22r22, 3); opt("R33", "R33", 1); opt("R22", "R" + z22r22, 1);
        };
        Action<string, string, string> tail = (cg3, cg2, rest) =>
        {
            if (cg3 != null) w.Add("CGOffset3=" + cg3);
            if (cg2 != null) w.Add("CGOffset2=" + cg2);
            if (rest != null) w.AddRange(rest.Split(' '));
            w.Add("ConcCol=No"); w.Add("ConcBeam=No");
        };
        string[] plates = { "D", "BF", "TF", "TW", "A", "J", "I33", "I22", "AS2", "AS3" };
        switch (kind)
        {
            case "STEEL_I_SECTION":
            {
                if ((why = lacks(plates)) != null) return null;
                if ((v.ContainsKey("BFB") && v["BFB"] != v["BF"]) || (v.ContainsKey("TFB") && v["TFB"] != v["TF"]))
                { why = label + " in " + fn + ": flanges of two sizes; Pluto reads I sections of equal flanges"; return null; }
                w.Add("Shape=\"I/Wide Flange\""); put("t3", "D", 1); put("t2", "BF", 1); put("tf", "TF", 1); put("tw", "TW", 1); put("t2b", "BF", 1); put("tfb", "TF", 1);
                if (has("KDES") && num("KDES") > num("TF")) len("FilletRadius", num("KDES") - num("TF"));
                props("I22", "AS3"); moduli("S22", "22");
                double tf = (double)num("TF"), bf = (double)num("BF"), d = (double)num("D");
                tail("0", "0", "EccV2=0 EccV3=0 Cw=" + (tf * bf * bf * bf * (d - tf) * (d - tf) / 24 * Math.Pow(per, 6)).ToString("R", CultureInfo.InvariantCulture));
                break;
            }
            case "STEEL_CHANNEL":
            case "STEEL_TEE":
            {
                if ((why = lacks(plates)) != null) return null;
                bool tee = kind == "STEEL_TEE";
                w.Add("Shape=" + (tee ? "Tee" : "Channel")); put("t3", "D", 1); put("t2", "BF", 1); put("tf", "TF", 1); put("tw", "TW", 1);
                if (has("KDES") && num("KDES") > num("TF")) len("FilletRadius", num("KDES") - num("TF"));
                props("I22", "AS3"); moduli("S22", "22");
                if (tee) tail("0", has("Y") ? Len(num("D") / 2 - num("Y"), per, 1) : null, null);
                else tail(has("X") ? Len(num("BF") / 2 - num("X"), per, 1) : null, "0", null);
                break;
            }
            case "STEEL_BOX":
            {
                if ((why = lacks(new[] { "HT", "B", "TF", "TW", "A", "J", "I33", "I22", "AS2", "AS3" })) != null) return null;
                w.Add("Shape=Box/Tube"); put("t3", "HT", 1); put("t2", "B", 1); put("tf", "TF", 1); put("tw", "TW", 1); w.Add("FilletRadius=0");
                props("I22", "AS3"); moduli("S22", "22");
                tail("0", "0", "EccV2=0 EccV3=0 Cw=0");
                break;
            }
            case "STEEL_PIPE":
            {
                if ((why = lacks(new[] { "OD", "TDES", "A", "J", "I33", "AS2" })) != null) return null;
                w.Add("Shape=Pipe"); put("t3", "OD", 1); put("tw", "TDES", 1);
                props("I33", "AS2"); moduli("S33", "33");
                tail("0", "0", "EccV2=0 EccV3=0 Cw=0");
                break;
            }
            case "STEEL_DOUBLE_ANGLE":
            {
                if (accept == null) { why = label + " is a double angle in " + fn + ": STAAD gives one as TABLE LD / SD"; return null; }
                if ((why = lacks(new[] { "D", "B", "TF", "TW", "A", "J", "I33", "I22", "AS2", "AS3" })) != null) return null;
                decimal dis = has("DIS") ? num("DIS") : 0;
                w.Add("Shape=\"Double Angle\""); put("t3", "D", 1); put("t2", "B", 1); len("SngAngWid", (num("B") - dis) / 2); put("tf", "TF", 1); put("tw", "TW", 1); len("dis", dis); w.Add("FilletRadius=0");
                props("I22", "AS3"); moduli("S22", "22");
                tail("0", has("Y") ? Len(num("D") / 2 - num("Y"), per, 1) : null, null);
                break;
            }
            case "STEEL_ANGLE":
            {
                if ((why = lacks(new[] { "D", "B", "TF", "TW", "A", "J", "I33", "I22", "AS2", "AS3" })) != null) return null;
                decimal fr = has("KDES") && num("KDES") > num("TF") ? num("KDES") - num("TF") : 0;
                w.Add("Shape=Angle"); put("t3", "D", 1); put("t2", "B", 1); put("tf", "TF", 1); put("tw", "TW", 1);
                if (fr > 0) len("FilletRadius", fr);
                put("Area", "A", 2); put("TorsConst", "J", 4); put("I33", "I33", 4); put("I22", "I22", 4);
                w.Add("I23=" + (AngleI23((double)num("D"), (double)num("B"), (double)num("TF"), (double)num("TW"), (double)fr) * Math.Pow(per, 4)).ToString("R", CultureInfo.InvariantCulture));
                put("AS2", "AS2", 2); put("AS3", "AS3", 2);
                moduli("S22", "22");
                tail(has("X") ? Len(num("B") / 2 - num("X"), per, 1) : null, has("Y") ? Len(num("Y") - num("D") / 2, per, 1) : null, null);
                break;
            }
            default:
                why = label + " is a " + found.Value.Key + " in " + fn + ", a kind Pluto does not read from SAP's library: give it as PRIS";
                return null;
        }
        w.Add("FromFile=Yes");
        foreach (string m in new[] { "AMod", "A2Mod", "A3Mod", "JMod", "I2Mod", "I3Mod", "MMod", "WMod" }) w.Add(m + "=1");
        w.Add("SectInFile=" + label); w.Add("FileName=\"" + file + "\"");
        return string.Join("   ", w.ToArray());
    }

    // an angle's product of inertia with SAP's sign (its D leg along local 2, the heel toward +3, the B leg toward -3): the
    // two legs and the root fillet of radius r, about their centroid. SAP's own is a little less (L3X2-1/2X3/16: 0.424899
    // here, 0.424147 SAP's), its section model not being this one; no toe radius explains the rest
    static double AngleI23(double d, double b, double tf, double tw, double r)
    {
        var parts = new List<double[]>();                          // area, centroid x, y, product about its own centroid
        parts.Add(new[] { tw * d, tw / 2, d / 2, 0 });
        parts.Add(new[] { (b - tw) * tf, (tw + b) / 2, tf / 2, 0 });
        if (r > 0)
        {
            // the fillet: an r x r square at the inner corner less the quarter disc centred at (tw + r, tf + r)
            parts.Add(new[] { r * r, tw + r / 2, tf + r / 2, 0 });
            double aq = Math.PI * r * r / 4, k = 4 * r / (3 * Math.PI);
            parts.Add(new[] { -aq, tw + r - k, tf + r - k, -(r * r * r * r / 8 - aq * k * k) });
        }
        double a = parts.Sum(q => q[0]), cx = parts.Sum(q => q[0] * q[1]) / a, cy = parts.Sum(q => q[0] * q[2]) / a;
        return -parts.Sum(q => q[3] + q[0] * (q[1] - cx) * (q[2] - cy));
    }

    // whether two AISC names are one shape, written as SAP labels and STAAD's EDI names differ: HSS24X12X3/4 and
    // HSS24X12X.750, HSS5.500X0.375 and HSS5.5X.375 (the same letters, the same numbers between the X's)
    public static bool SameShape(string a, string b)
    {
        if (string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase)) return true;
        var ma = Regex.Match((a ?? "").Trim(), @"^([A-Za-z]+)(.+)$"); var mb = Regex.Match((b ?? "").Trim(), @"^([A-Za-z]+)(.+)$");
        if (!ma.Success || !mb.Success || !Is(ma.Groups[1].Value, mb.Groups[1].Value)) return false;
        string[] pa = ma.Groups[2].Value.Split('X', 'x'), pb = mb.Groups[2].Value.Split('X', 'x');
        if (pa.Length != pb.Length) return false;
        for (int k = 0; k < pa.Length; k++)
        {
            decimal x, y;
            if (!Frac(pa[k], out x) || !Frac(pb[k], out y) || x != y) return false;
        }
        return true;
    }

    // a dimension as AISC names it: 2-1/2, 3/16, 2.5, .750
    static bool Frac(string s, out decimal x)
    {
        x = 0;
        var m = Regex.Match(s ?? "", @"^(?:(\d*\.?\d+)(?:-(\d+)/(\d+))?|(\d+)/(\d+))$");
        if (!m.Success) return false;
        if (m.Groups[4].Success) { x = decimal.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture) / decimal.Parse(m.Groups[5].Value, CultureInfo.InvariantCulture); return true; }
        x = decimal.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        if (m.Groups[2].Success) x += decimal.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) / decimal.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        return true;
    }

    static string Trim(string t) { return t.IndexOf('.') >= 0 ? t.TrimEnd('0').TrimEnd('.') : t; }

    static bool Is(string a, string b) { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }

    static decimal? Dec(string s)
    {
        decimal x;
        return decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out x) ? x : (decimal?)null;
    }

    // a library number (inches to the power pow) in the model's unit: the file's digits as they are in inches
    static string Len(decimal x, double per, int pow)
    {
        if (per == 1) return Trim(x.ToString(CultureInfo.InvariantCulture));
        return ((double)x * Math.Pow(per, pow)).ToString("R", CultureInfo.InvariantCulture);
    }

    // every catalog section's library file checked, and its path made this machine's where SAP's library holds it;
    // the refusals returned, notes added. folder: SAP's library here (null: none, the rows written unchecked)
    public static List<string> Check(FrameModel m, string folder, List<string> notes)
    {
        if (string.IsNullOrEmpty(folder)) folder = null;          // PowerShell passes "" for $null
        var refusals = new List<string>();
        var cache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var unchecked_ = new List<string>();
        foreach (var s in m.Sections)
        {
            var r = s.Rec(FrameSection.General);
            if (r == null || !r.Yes("FromFile")) continue;
            string file = (r.Get("FileName") ?? "").Trim(), shape = (r.Get("SectInFile") ?? s.Label).Trim(), who = "section " + s.Label + " (" + shape + ")";
            if (file.Length > 0 && File.Exists(file))
            {
                if (!Holds(file, shape, cache)) refusals.Add(who + ": its library file " + file + " has no " + shape + " (SAP would make it a General section with every property 1)");
                continue;
            }
            if (folder == null) { unchecked_.Add(s.Label); continue; }
            string name = FileOf(file), local = name.Length == 0 ? null : Directory.GetFiles(folder).FirstOrDefault(f => Path.GetFileName(f).Equals(name, StringComparison.OrdinalIgnoreCase));
            if (local == null || !File.Exists(local))
            {
                refusals.Add(who + ": its library file " + (file.Length == 0 ? "(none named)" : file) + " is not on this machine" + (name.Length == 0 ? "" : ", nor " + name + " in SAP's library " + folder) +
                             " (SAP would make it a General section with every property 1); pick the shape from a library SAP has");
                continue;
            }
            if (!Holds(local, shape, cache)) { refusals.Add(who + ": SAP's library file " + local + " has no " + shape + " (SAP would make it a General section with every property 1)"); continue; }
            r.Set("FileName", local);
            notes.Add(who + ": its library file is SAP's own here, " + local + (file.Length == 0 ? "" : " (the model named " + file + ")"));
        }
        if (unchecked_.Count > 0)
            notes.Add("SAP's section library is not on this machine: the catalog sections' files are not checked (" + string.Join(", ", unchecked_.Take(12).ToArray()) + (unchecked_.Count > 12 ? " ..." : "") +
                      "); a file SAP cannot find becomes a General section with every property 1");
        return refusals;
    }
}
