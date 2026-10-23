using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

// PipeWorkbookRun  --  running the build from the workbook (2026-10-23). Excel buttons would need macros (an .xlsm),
// which production machines block, so Build-Pipe.ps1 writes two launchers beside every workbook it writes (-Xlsx,
// -NewWorkbook), and the Start sheet links to them:
//   "<name> - preview.cmd"   Build-Pipe.ps1 -In <name>.xlsx -Preview -Watch: every save rebuilds the viewer files
//   "<name> - cii.cmd"       Build-Pipe.ps1 -In <name>.xlsx -Cii -Preview: the .cii for CAESAR and the preview with it
// They find the workbook beside themselves (%~dp0), so the folder can move as a whole; the runner's path is the one
// at write time. Outputs go beside the workbook under its name, or "<name>_edit" when the workbook was made from a
// .cii of the same name in the same folder, so the CAESAR job it came from is never written over.
// The Start sheet's links are relative (HYPERLINK("<name> - preview.cmd")); its PowerShell commands take the
// workbook's place from CELL("filename") and fall back to the path at write time (LibreOffice, an unsaved copy).
// C# 5, one global batch (prefix Pipe).

public sealed class PipeWorkbookRun
{
    public string Script = "";      // Build-Pipe.ps1, absolute
    public string Viewer;           // the viewer's index.html, absolute; null = not known
    public string Folder = "";      // the workbook's folder at write time
    public string Name = "";        // the workbook's base name
    public string Out = "";         // the outputs' base name, beside the workbook

    public string PreviewCmd { get { return Name + " - preview.cmd"; } }
    public string CiiCmd { get { return Name + " - cii.cmd"; } }

    // the workbook at xlsxPath, the Pluto repo at repoRoot, the file the model came from (null for a new model)
    public static PipeWorkbookRun For(string xlsxPath, string repoRoot, string sourcePath)
    {
        var r = new PipeWorkbookRun();
        string full = Path.GetFullPath(xlsxPath);
        r.Folder = Path.GetDirectoryName(full) ?? "";
        r.Name = Path.GetFileNameWithoutExtension(full);
        r.Out = r.Name;
        if (!string.IsNullOrEmpty(sourcePath) && string.Equals(Path.GetExtension(sourcePath), ".cii", StringComparison.OrdinalIgnoreCase))
        {
            string src = Path.GetFullPath(sourcePath);
            if (string.Equals(Path.GetDirectoryName(src), r.Folder, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(Path.GetFileNameWithoutExtension(src), r.Name, StringComparison.OrdinalIgnoreCase))
                r.Out = r.Name + "_edit";
        }
        if (!string.IsNullOrEmpty(repoRoot))
        {
            r.Script = Path.Combine(repoRoot, "scripts", "caesar", "Build-Pipe.ps1");
            string v = Path.Combine(repoRoot, "viewer", "index.html");
            if (File.Exists(v)) r.Viewer = v;
        }
        return r;
    }

    // the two launchers, beside the workbook; returns their paths
    public List<string> WriteLaunchers()
    {
        var done = new List<string>();
        foreach (bool live in new[] { true, false })
        {
            string p = Path.Combine(Folder, live ? PreviewCmd : CiiCmd);
            File.WriteAllText(p, Launcher(live).Replace("\n", "\r\n"), Encoding.Default);
            done.Add(p);
        }
        return done;
    }

    public string Launcher(bool live)
    {
        var sb = new StringBuilder();
        sb.Append("@echo off\n");
        sb.Append("rem Pluto: " + Echo(live ? "the live viewer preview" : "the CAESAR .cii") + " of " + Echo(Name) + ".xlsx, the workbook beside this file.\n");
        sb.Append("rem Written by Build-Pipe.ps1 with the workbook (-Xlsx or -NewWorkbook writes it again).\n");
        sb.Append("title Pluto " + Echo(live ? "live preview" : "write the .cii") + " - " + Echo(Name) + "\n");
        if (live)
        {
            sb.Append("echo Every save of " + Echo(Name) + ".xlsx rebuilds " + Echo(Out) + ".bin and " + Echo(Out) + ".features.json beside it.\n");
            sb.Append("echo Open them in Pluto's viewer (Choose files..., both together) and tick Auto-reload. Close this window to stop.\n");
        }
        else sb.Append("echo Writing " + Echo(Out) + ".cii (and the viewer preview) from " + Echo(Name) + ".xlsx ...\n");
        sb.Append("echo.\n");
        sb.Append("\"%SystemRoot%\\System32\\WindowsPowerShell\\v1.0\\powershell.exe\" -NoProfile -ExecutionPolicy Bypass -File \"" + Bat(Script) +
                  "\" -In \"%~dp0" + Bat(Name) + ".xlsx\" -Out \"%~dp0" + Bat(Out) + "\" " + (live ? "-Preview -Watch" : "-Cii -Preview") + "\n");
        if (!live)
        {
            sb.Append("echo.\n");
            sb.Append("echo In CAESAR II: File, Import, CAESAR II Neutral File, then " + Echo(Out) + ".cii\n");
        }
        sb.Append("pause\n");
        return sb.ToString();
    }

    // inside quotes on a batch line only % is special
    static string Bat(string s) { return (s ?? "").Replace("%", "%%"); }

    // text on an echo / title / rem line: % doubled, the shell's operators escaped
    static string Echo(string s)
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

    // the PowerShell command a launcher runs, as a formula giving its text
    public string CommandFormula(bool live)
    {
        const string q = "\"\"";
        return "\"powershell.exe -NoProfile -ExecutionPolicy Bypass -File " + q + Script.Replace("\"", "") + q + " -In " + q + "\"&" + BookFormula() + "&\"" + q +
               " -Out " + q + "\"&" + FolderFormula() + "&\"" + Out.Replace("\"", "") + q + " " + (live ? "-Preview -Watch" : "-Cii -Preview") + "\"";
    }
}
