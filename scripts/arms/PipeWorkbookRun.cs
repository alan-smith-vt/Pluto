using System;
using System.Collections.Generic;
using System.IO;

// PipeWorkbookRun  --  running the build from the pipe workbook (2026-10-23; the mechanism is the kit's WbRun since
// 2026-11-05). Build-Pipe.ps1 writes two launchers beside every workbook it writes (-Xlsx, -NewWorkbook), and the
// Start sheet links to them:
//   "<name> - preview.cmd"   Build-Pipe.ps1 -In <name>.xlsx -Preview -Watch: every save rebuilds the viewer files
//   "<name> - cii.cmd"       Build-Pipe.ps1 -In <name>.xlsx -Cii -Preview: the .cii for CAESAR and the preview with it
// Outputs are "<name>_edit" when the workbook was made from a .cii of the same name in the same folder.
// C# 5, one global batch (prefix Pipe).

public sealed class PipeWorkbookRun : WbRun
{
    public string PreviewCmd { get { return Name + " - preview.cmd"; } }
    public string CiiCmd { get { return Name + " - cii.cmd"; } }

    // the workbook at xlsxPath, the Pluto repo at repoRoot, the file the model came from (null for a new model)
    public static PipeWorkbookRun For(string xlsxPath, string repoRoot, string sourcePath)
    {
        var r = new PipeWorkbookRun();
        r.Place(xlsxPath, repoRoot, Path.Combine("scripts", "caesar", "Build-Pipe.ps1"), sourcePath, ".cii");
        return r;
    }

    // the two launchers, beside the workbook; returns their paths
    public List<string> WriteLaunchers()
    {
        var done = new List<string>();
        foreach (bool live in new[] { true, false }) done.Add(WriteLauncher(live ? PreviewCmd : CiiCmd, Launcher(live)));
        return done;
    }

    public string Launcher(bool live)
    {
        if (live)
            return LauncherText("Build-Pipe.ps1", "the live viewer preview", "live preview",
                new[] { "Every save of " + Echo(Name) + ".xlsx rebuilds " + Echo(Out) + ".bin and " + Echo(Out) + ".features.json beside it.",
                        "Open them in Pluto's viewer (Choose files..., both together) and tick Auto-reload. Close this window to stop." },
                Switches(true), new string[0]);
        return LauncherText("Build-Pipe.ps1", "the CAESAR .cii", "write the .cii",
            new[] { "Writing " + Echo(Out) + ".cii (and the viewer preview) from " + Echo(Name) + ".xlsx ..." },
            Switches(false), new[] { "In CAESAR II: File, Import, CAESAR II Neutral File, then " + Echo(Out) + ".cii" });
    }

    static string Switches(bool live) { return live ? "-Preview -Watch" : "-Cii -Preview"; }

    // the PowerShell command a launcher runs, as a formula giving its text
    public string CommandFormula(bool live) { return CommandFormula(Switches(live)); }
}
