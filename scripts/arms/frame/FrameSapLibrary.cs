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

    // a shape's SAP row (all but SectionName and Material) from SAP's library, as SAP writes a catalog section's: the
    // entry of the newest AISC file that has it (by label or EDI name), FromFile=Yes, SectInFile its label, and the file by
    // its full path. SAP builds the section from the file (the catalog probe); the numbers are for Pluto, in the model's
    // length unit (the file's text as it is in inches). null with why when no file has it, or Pluto does not read its kind.
    //   I sections (W M S HP): SAP's own row to its digits (W10X33, W12X26): t3 D, t2 / t2b BF, tf / tfb TF, tw TW,
    //     FilletRadius KDES - TF, Area A, TorsConst J, I33 I22 AS2 AS3 Z33 Z22 R33 R22 as named, S33Top / S33Bot S33POS /
    //     S33NEG, S22Left / S22Right S22POS / S22NEG, Cw = TF BF^3 (D - TF)^2 / 24
    //   channels, tees, HSS boxes and pipes: SAP's dimensions (Channel / Tee / Box/Tube: t3 t2 tf tw; Pipe: t3 OD, tw the
    //     wall) and the file's properties as named, I23 0 (each is symmetric about an axis); a pipe's one axis is both. What
    //     SAP works out itself (fillet, Cw, shear center) and a channel's S22 (its two sides) are left to SAP, which reads
    //     the file
    //   angles: refused (SAP works out an angle's I23 about its legs, which the file does not give); double angles too
    //     (STAAD's are TABLE LD / SD with a spacing, not TABLE ST)
    public static string Row(string folder, string shape, string lengthUnit, out string file, out string why)
    {
        file = null; why = null;
        double per;
        if (!PerInch.TryGetValue(lengthUnit ?? "", out per)) { why = "the model's length unit " + lengthUnit + " is not one Pluto converts the library's inches to"; return null; }
        var files = AiscFiles(folder);
        if (files.Count == 0) { why = "SAP's library " + folder + " has no AISC file (AISC15.xml)"; return null; }
        KeyValuePair<string, Dictionary<string, string>> e = default(KeyValuePair<string, Dictionary<string, string>>);
        foreach (string f in files) if (EntriesOf(f).TryGetValue(shape.Trim(), out e)) { file = f; break; }
        if (file == null) { why = "no " + shape + " in SAP's library (" + string.Join(", ", files.Select(f => Path.GetFileName(f)).ToArray()) + " in " + folder + ")"; return null; }
        string other = OtherUnit(file), fn = Path.GetFileName(file);
        if (other != null) { why = fn + " gives its lengths in " + other + "; Pluto reads the AISC files' inches"; return null; }
        string kind = e.Key.ToUpperInvariant(), label = e.Value["LABEL"];
        var v = e.Value;
        Func<string, bool> has = k => v.ContainsKey(k) && Dec(v[k]).HasValue;
        Func<string, decimal> num = k => Dec(v[k]).Value;
        var w = new List<string>();
        Action<string, string, int> put = (field, key, pow) => w.Add(field + "=" + Len(num(key), per, pow));
        Action<string, string, int> opt = (field, key, pow) => { if (has(key)) put(field, key, pow); };
        Func<string[], string> lacks = keys =>
        {
            var missing = keys.Where(k => !has(k)).ToList();
            return missing.Count == 0 ? null : label + " in " + fn + ": no " + string.Join(", ", missing.ToArray());
        };
        Action props = () =>
        {
            put("Area", "A", 2); put("TorsConst", "J", 4); put("I33", "I33", 4); put("I22", "I22", 4); w.Add("I23=0"); put("AS2", "AS2", 2); put("AS3", "AS3", 2);
        };
        switch (kind)
        {
            case "STEEL_I_SECTION":
            {
                if ((why = lacks(new[] { "D", "BF", "TF", "TW", "A", "J", "I33", "I22", "AS2", "AS3" })) != null) return null;
                if ((v.ContainsKey("BFB") && v["BFB"] != v["BF"]) || (v.ContainsKey("TFB") && v["TFB"] != v["TF"]))
                { why = label + " in " + fn + ": flanges of two sizes; Pluto reads I sections of equal flanges"; return null; }
                w.Add("Shape=\"I/Wide Flange\""); put("t3", "D", 1); put("t2", "BF", 1); put("tf", "TF", 1); put("tw", "TW", 1); put("t2b", "BF", 1); put("tfb", "TF", 1);
                if (has("KDES") && num("KDES") > num("TF")) w.Add("FilletRadius=" + Len(num("KDES") - num("TF"), per, 1));
                props();
                opt("S33Top", "S33POS", 3); opt("S33Bot", "S33NEG", 3); opt("S22Left", "S22POS", 3); opt("S22Right", "S22NEG", 3);
                opt("Z33", "Z33", 3); opt("Z22", "Z22", 3); opt("R33", "R33", 1); opt("R22", "R22", 1);
                double tf = (double)num("TF"), bf = (double)num("BF"), d = (double)num("D");
                w.Add("CGOffset3=0"); w.Add("CGOffset2=0"); w.Add("EccV2=0"); w.Add("EccV3=0");
                w.Add("Cw=" + (tf * bf * bf * bf * (d - tf) * (d - tf) / 24 * Math.Pow(per, 6)).ToString("R", CultureInfo.InvariantCulture));
                w.Add("ConcCol=No"); w.Add("ConcBeam=No");
                break;
            }
            case "STEEL_CHANNEL":
            case "STEEL_TEE":
            {
                if ((why = lacks(new[] { "D", "BF", "TF", "TW", "A", "J", "I33", "I22", "AS2", "AS3" })) != null) return null;
                w.Add("Shape=" + (kind == "STEEL_TEE" ? "Tee" : "Channel")); put("t3", "D", 1); put("t2", "BF", 1); put("tf", "TF", 1); put("tw", "TW", 1);
                props();
                opt("S33Top", "S33POS", 3); opt("S33Bot", "S33NEG", 3);
                if (kind == "STEEL_TEE") { opt("S22Left", "S22POS", 3); opt("S22Right", "S22NEG", 3); }
                opt("Z33", "Z33", 3); opt("Z22", "Z22", 3); opt("R33", "R33", 1); opt("R22", "R22", 1);
                break;
            }
            case "STEEL_BOX":
            {
                if ((why = lacks(new[] { "HT", "B", "TF", "TW", "A", "J", "I33", "I22", "AS2", "AS3" })) != null) return null;
                w.Add("Shape=Box/Tube"); put("t3", "HT", 1); put("t2", "B", 1); put("tf", "TF", 1); put("tw", "TW", 1);
                props();
                opt("S33Top", "S33POS", 3); opt("S33Bot", "S33NEG", 3); opt("S22Left", "S22POS", 3); opt("S22Right", "S22NEG", 3);
                opt("Z33", "Z33", 3); opt("Z22", "Z22", 3); opt("R33", "R33", 1); opt("R22", "R22", 1);
                break;
            }
            case "STEEL_PIPE":
            {
                if ((why = lacks(new[] { "OD", "TDES", "A", "J", "I33", "AS2" })) != null) return null;
                w.Add("Shape=Pipe"); put("t3", "OD", 1); put("tw", "TDES", 1);
                put("Area", "A", 2); put("TorsConst", "J", 4); put("I33", "I33", 4); put("I22", "I33", 4); w.Add("I23=0"); put("AS2", "AS2", 2); put("AS3", "AS2", 2);
                opt("S33Top", "S33POS", 3); opt("S33Bot", "S33NEG", 3); opt("S22Left", "S33POS", 3); opt("S22Right", "S33NEG", 3);
                opt("Z33", "Z33", 3); opt("Z22", "Z33", 3); opt("R33", "R33", 1); opt("R22", "R33", 1);
                break;
            }
            case "STEEL_ANGLE":
                why = label + " is an angle in " + fn + ": SAP works out its I23 about its legs, which the file does not give, and STAAD's single-angle axes are not confirmed against SAP's";
                return null;
            case "STEEL_DOUBLE_ANGLE":
                why = label + " is a double angle in " + fn + ": STAAD gives one as TABLE LD / SD with a spacing, which Pluto does not read yet";
                return null;
            default:
                why = label + " is a " + e.Key + " in " + fn + ", a kind Pluto does not read from SAP's library: give it as PRIS";
                return null;
        }
        w.Add("FromFile=Yes");
        foreach (string m in new[] { "AMod", "A2Mod", "A3Mod", "JMod", "I2Mod", "I3Mod", "MMod", "WMod" }) w.Add(m + "=1");
        w.Add("SectInFile=" + label); w.Add("FileName=\"" + file + "\"");
        return string.Join("   ", w.ToArray());
    }

    static bool Is(string a, string b) { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }

    static decimal? Dec(string s)
    {
        decimal x;
        return decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out x) ? x : (decimal?)null;
    }

    // a library number (inches to the power pow) in the model's unit: the file's digits as they are in inches
    static string Len(decimal x, double per, int pow)
    {
        string t = x.ToString(CultureInfo.InvariantCulture);
        if (per == 1) return t.IndexOf('.') >= 0 ? t.TrimEnd('0').TrimEnd('.') : t;
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
