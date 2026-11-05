using System;
using System.IO;
using System.Text;

// WbRun  --  running a build from a workbook (the kit, 2026-11-05; from PipeWorkbookRun, 2026-10-23). Excel buttons
// would need macros (an .xlsm), which production machines block, so a runner writes .cmd launchers beside every
// workbook it writes and the Start sheet links to them. They find the workbook beside themselves (%~dp0), so the
// folder can move as a whole; the runner's path is the one at write time. Outputs go beside the workbook under its
// name, or "<name>_edit" when the workbook was made from a source file of the same name in the same folder (the job it
// came from is never written over). The Start sheet's commands take the workbook's place from CELL("filename") and fall
// back to the path at write time (LibreOffice, an unsaved copy). A workbook's own class names its launchers
// (PipeWorkbookRun, FrameWorkbookRun). C# 5.

public class WbRun
{
    public string Script = "";      // the runner (.ps1), absolute
    public string Viewer;           // the viewer's index.html, absolute; null = not known
    public string Folder = "";      // the workbook's folder at write time
    public string Name = "";        // the workbook's base name
    public string Out = "";         // the outputs' base name, beside the workbook

    // the workbook at xlsxPath, the Pluto repo at repoRoot (script: the runner's path inside it), the file the model came
    // from (null for a new model); sourceExt: the source kind that gets "_edit" outputs (".cii", ".s2k")
    protected void Place(string xlsxPath, string repoRoot, string script, string sourcePath, string sourceExt)
    {
        string full = Path.GetFullPath(xlsxPath);
        Folder = Path.GetDirectoryName(full) ?? "";
        Name = Path.GetFileNameWithoutExtension(full);
        Out = Name;
        if (!string.IsNullOrEmpty(sourcePath) && string.Equals(Path.GetExtension(sourcePath), sourceExt, StringComparison.OrdinalIgnoreCase))
        {
            string src = Path.GetFullPath(sourcePath);
            if (string.Equals(Path.GetDirectoryName(src), Folder, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(Path.GetFileNameWithoutExtension(src), Name, StringComparison.OrdinalIgnoreCase))
                Out = Name + "_edit";
        }
        if (!string.IsNullOrEmpty(repoRoot))
        {
            Script = Path.Combine(repoRoot, script);
            string v = Path.Combine(repoRoot, "viewer", "index.html");
            if (File.Exists(v)) Viewer = v;
        }
    }

    // a launcher: what it builds (rem / title), echo lines before the command, the runner's switches, echo lines after
    public string LauncherText(string runner, string what, string title, string[] before, string switches, string[] after)
    {
        var sb = new StringBuilder();
        sb.Append("@echo off\n");
        sb.Append("rem Pluto: " + Echo(what) + " of " + Echo(Name) + ".xlsx, the workbook beside this file.\n");
        sb.Append("rem Written by " + runner + " with the workbook (-Xlsx or -NewWorkbook writes it again).\n");
        sb.Append("title Pluto " + Echo(title) + " - " + Echo(Name) + "\n");
        foreach (string line in before) sb.Append("echo " + line + "\n");
        sb.Append("echo.\n");
        sb.Append("\"%SystemRoot%\\System32\\WindowsPowerShell\\v1.0\\powershell.exe\" -NoProfile -ExecutionPolicy Bypass -File \"" + Bat(Script) +
                  "\" -In \"%~dp0" + Bat(Name) + ".xlsx\" -Out \"%~dp0" + Bat(Out) + "\" " + switches + "\n");
        if (after.Length > 0)
        {
            sb.Append("echo.\n");
            foreach (string line in after) sb.Append("echo " + line + "\n");
        }
        sb.Append("pause\n");
        return sb.ToString();
    }

    // a launcher file beside the workbook (CRLF, the console's code page); returns its path
    public string WriteLauncher(string fileName, string text)
    {
        string p = Path.Combine(Folder, fileName);
        File.WriteAllText(p, text.Replace("\n", "\r\n"), Encoding.Default);
        return p;
    }

    // inside quotes on a batch line only % is special
    public static string Bat(string s) { return (s ?? "").Replace("%", "%%"); }

    // text on an echo / title / rem line: % doubled, the shell's operators escaped
    public static string Echo(string s)
    {
        var sb = new StringBuilder();
        foreach (char c in s ?? "")
        {
            if (c == '%') sb.Append("%%");
            else if ("^&<>|".IndexOf(c) >= 0) sb.Append('^').Append(c);
            else sb.Append(c);
        }
        return sb.ToString();
    }

    // ---- the Start sheet's formulas ----------------------------------------------------------------------------

    // an Excel string literal
    public static string Lit(string s) { return "\"" + (s ?? "").Replace("\"", "\"\"") + "\""; }

    // the workbook's folder (with its trailing separator) and its full path, from CELL("filename"); the place at write
    // time where CELL gives none (LibreOffice, a copy never saved)
    public string FolderFormula()
    {
        const string cf = "CELL(\"filename\",$A$1)";
        return "IFERROR(LEFT(" + cf + ",FIND(\"[\"," + cf + ")-1)," + Lit(Folder + Path.DirectorySeparatorChar) + ")";
    }

    public string BookFormula()
    {
        const string cf = "CELL(\"filename\",$A$1)";
        return "IFERROR(LEFT(" + cf + ",FIND(\"[\"," + cf + ")-1)&MID(" + cf + ",FIND(\"[\"," + cf + ")+1,FIND(\"]\"," + cf + ")-FIND(\"[\"," + cf + ")-1)," +
               Lit(Path.Combine(Folder, Name + ".xlsx")) + ")";
    }

    // the PowerShell command a launcher runs with these switches, as a formula giving its text
    public string CommandFormula(string switches)
    {
        const string q = "\"\"";
        return "\"powershell.exe -NoProfile -ExecutionPolicy Bypass -File " + q + Script.Replace("\"", "") + q + " -In " + q + "\"&" + BookFormula() + "&\"" + q +
               " -Out " + q + "\"&" + FolderFormula() + "&\"" + Out.Replace("\"", "") + q + " " + switches + "\"";
    }

    // the command that brings this workbook to the layout of the Pluto that runs it, over itself (-Refresh)
    public string RefreshFormula()
    {
        const string q = "\"\"";
        return "\"powershell.exe -NoProfile -ExecutionPolicy Bypass -File " + q + Script.Replace("\"", "") + q + " -In " + q + "\"&" + BookFormula() + "&\"" + q + " -Refresh\"";
    }
}
