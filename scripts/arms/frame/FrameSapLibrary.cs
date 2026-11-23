using System;
using System.Collections.Generic;
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
