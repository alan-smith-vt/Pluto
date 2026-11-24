// Workbook.cs -- the engineer's workbook (CalcFlow.xlsx) and its launchers.
// The workbook is CalcFlow's front door: a Start tab whose link cells run the steps. Excel buttons would need macros
// (an .xlsm), which production machines block, so each step is a .cmd launcher beside the workbook and the Start tab
// links to it; where links to files are blocked too, the launcher is double-clicked. This is the pattern of Pluto's
// pipe and frame workbooks (scripts/arms/workbook/WbRun.cs, WbStart.cs), copied here because CalcFlow compiles apart
// from Pluto's batch.
//   WriteNew   CalcFlow.xlsx (Start: the steps, the link cells, the settings table tblStart) and the launchers,
//              written once: an existing workbook is never overwritten (once saved, only Excel writes it)
//   Settings   tblStart read back (the saved file, also while Excel has it open)
// What a launcher writes goes into other files (Model_Data.xlsx ...): no script can add tabs to a workbook that is
// open in Excel. C# 5 (Windows PowerShell 5.1 Add-Type, warnings are errors).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace CalcFlow
{
    public static class EngineerWorkbook
    {
        public const string FileName = "CalcFlow.xlsx";
        public const string PostProcessLauncher = "Post-process ANL.cmd";
        public const string AnlSetting = "Anl file";

        public const string CheckLauncher = "Check.cmd", RunLauncher = "Run calcs.cmd";
        public const string TemplatesSetting = "Templates folder", LimitSetting = "DCR limit";

        // the workbook and its launchers in dir; script: the full path of CalcFlow.ps1. pkg (the model read from the
        // .anl) fills Model Config and Load Cases, harvest (the templates learned) the schedule tabs; either may be
        // null. Returns the files written.
        public static string[] WriteNew(string dir, string script, AnalysisPackage pkg, HarvestResult harvest, string templatesDir)
        {
            Directory.CreateDirectory(dir);
            string wb = Path.Combine(dir, FileName);
            if (File.Exists(wb)) throw new Exception(wb + " exists: the workbook is yours once written and is never overwritten.");
            var sheets = new List<XOutSheet> { StartSheet(templatesDir ?? "") };
            sheets.AddRange(ProjectBook.Sheets(pkg, harvest));
            XlsxWriter.Write(wb, sheets, null);
            var files = new List<string> { wb };
            files.AddRange(WriteLaunchers(dir, script));
            return files.ToArray();
        }

        public static string[] WriteNew(string dir, string script) { return WriteNew(dir, script, null, null, ""); }

        // the launchers again (a moved repo: their path to CalcFlow.ps1 is the one at write time)
        public static string[] WriteLaunchers(string dir, string script)
        {
            return new[]
            {
                WriteLauncher(dir, PostProcessLauncher, LauncherText(script, "post-process", "Post-process .ANL",
                    new[] { "Reads the STAAD analysis output and writes " + ModelData.FileName + " beside the workbook." })),
                WriteLauncher(dir, CheckLauncher, LauncherText(script, "check", "Check",
                    new[] { "Reads the saved workbook and the model, and lists what is missing or does not fit.", "Nothing is calculated." })),
                WriteLauncher(dir, RunLauncher, LauncherText(script, "calcs", "Run calcs",
                    new[] { "Checks the saved workbook, then fills and calculates the connection calcs that are due", "and writes the connection summary." }))
            };
        }

        static XOutSheet StartSheet(string templatesDir)
        {
            var s = new XOutSheet("Start");
            var step = new XStyle(); step.Bold = true; step.HAlign = "center"; step.VAlign = "top";
            var body = new XStyle(); body.Wrap = true; body.VAlign = "top";
            var link = new XStyle(); link.Bold = true; link.Underline = true; link.Color = "FF0563C1"; link.Size = 12; link.VAlign = "top";
            const string blocked = " Excel asks before it opens a launcher: choose Yes. If links to files are blocked on this machine, double-click the .cmd file of the same name in this workbook's folder.";
            Func<string, string, XCell> go = (file, text) => new XCell(new XFormula("HYPERLINK(\"" + file + "\",\"\u25B6  " + text + "\")", "\u25B6  " + text), link);
            s.Add(new XCell("CalcFlow", Palette.Title()));
            s.Add(new XCell("Steel connection calcs from one STAAD analysis output. Work down the steps. Save this workbook before you click a link: a step reads the saved file.", Palette.Note()));
            s.Add();
            s.Add(new XCell("Step", Palette.Header(ColumnKind.Key)), new XCell("Do this", Palette.Header(ColumnKind.Key)), new XCell("What happens", Palette.Header(ColumnKind.Key)));
            var steps = new List<object[]>
            {
                new object[] { "Run the analysis in STAAD.Pro", "With your usual output commands at the end of the input (member forces, support reactions ...). CalcFlow reads the .anl it writes." },
                new object[] { go(PostProcessLauncher, "Post-process .ANL"), "Reads the .anl and writes " + ModelData.FileName + " beside this workbook (members, joints, supports, load cases, all end forces, enveloped end forces), then opens it." + blocked },
                new object[] { "Connection Lib: name your connections", "Give each connection a name of your own (a mark) and choose its template. On the tab of that template, add a row with the same name and fill in its values: every input of the template is a column of that row. Two marks may share a template (the same detail with other dimensions)." },
                new object[] { "Templates and Outputs: once per template", "On a template's tab, the Source row says where each column comes from (typed on the row, a force of the beam end, the beam number ...) and the Unit row its unit. On Outputs, mark the results to read back with Use = Y." },
                new object[] { "Model Config: a connection at each beam end", "One row per beam. Choose a mark in Connection I and Connection J where a calc is needed, and correct Frames into (the member that end connects to)." },
                new object[] { "Load Cases: which cases count", "Use = Y on the load cases the calcs take." },
                new object[] { go(CheckLauncher, "Check"), "Reads this workbook and the model and lists what is missing or does not fit, tab by tab. Nothing is calculated." },
                new object[] { go(RunLauncher, "Run calcs"), "Checks, then fills a copy of each template per connection, calculates it, reads the results back and writes the connection summary (reports folder)." }
            };
            for (int i = 0; i < steps.Count; i++)
            {
                object first = steps[i][0] is XCell ? steps[i][0] : new XCell(steps[i][0], body);
                string what = (string)steps[i][1];
                s.Add(new XCell(i + 1, step), first, new XCell(what, body));
                s.RowHeights[s.Rows.Count - 1] = what.Length > 220 ? 64 : what.Length > 120 ? 48 : 34;
            }
            s.Add();
            int top = s.Rows.Count;
            s.Add(new XCell("Setting", Palette.Header(ColumnKind.Key)), new XCell("Value", Palette.Header(ColumnKind.Input)), new XCell("Notes", Palette.Header(ColumnKind.Reference)));
            Action<string, string, string> setting = (k, v, n) => s.Add(new XCell(k, Palette.Data(ColumnKind.Key)), new XCell(v, Palette.Data(ColumnKind.Input)), new XCell(n, Palette.Data(ColumnKind.Reference)));
            setting(AnlSetting, "", "The STAAD .anl to read. Leave blank to be asked for it each time. A name without a folder is looked for beside this workbook.");
            setting(TemplatesSetting, templatesDir, "The folder that holds the calc template workbooks.");
            setting(LimitSetting, "1.0", "A DCR above this is flagged in the summary.");
            var t = new XOutTable();
            t.Name = "tblStart"; t.Top = top; t.Left = 0; t.Right = 2; t.Bottom = top + 3; t.Style = Palette.TableStyle;
            s.Tables.Add(t);
            s.Width(0, 12); s.Width(1, 46); s.Width(2, 110);
            s.Landscape = true; s.FitToWidth = true;
            return s;
        }

        // tblStart of a workbook: setting -> value (empty when the table is not there)
        public static Dictionary<string, string> Settings(string workbook)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            XWorkbook wb = Xlsx.Read(workbook);
            List<Dictionary<string, string>> rows = wb.TableRows("tblStart");
            if (rows == null) return d;
            foreach (var r in rows)
            {
                string k, v;
                if (r.TryGetValue("Setting", out k) && !string.IsNullOrEmpty(k)) d[k.Trim()] = r.TryGetValue("Value", out v) ? (v ?? "").Trim() : "";
            }
            return d;
        }

        // a launcher: runs CalcFlow.ps1 <verb> on the workbook beside it (%~dp0), then waits for a key
        public static string LauncherText(string script, string verb, string title, string[] says)
        {
            var sb = new StringBuilder();
            sb.Append("@echo off\n");
            sb.Append("rem CalcFlow: " + verb + " for " + FileName + ", the workbook beside this file.\n");
            sb.Append("rem Written with the workbook (CalcFlow.ps1 new); CalcFlow.ps1 launchers writes it again if the repo moved.\n");
            sb.Append("title CalcFlow - " + Echo(title) + "\n");
            foreach (string line in says) sb.Append("echo " + Echo(line) + "\n");
            sb.Append("echo.\n");
            sb.Append("\"%SystemRoot%\\System32\\WindowsPowerShell\\v1.0\\powershell.exe\" -NoProfile -ExecutionPolicy Bypass -File \"" + Bat(script) +
                "\" " + verb + " -Workbook \"%~dp0" + FileName + "\"\n");
            sb.Append("echo.\n");
            sb.Append("pause\n");
            return sb.ToString();
        }

        // CRLF, the console's code page
        static string WriteLauncher(string dir, string fileName, string text)
        {
            string p = Path.Combine(dir, fileName);
            File.WriteAllText(p, text.Replace("\n", "\r\n"), Encoding.Default);
            return p;
        }

        // inside quotes on a batch line only % is special
        static string Bat(string s) { return (s ?? "").Replace("%", "%%"); }

        // an echo line: the characters cmd would act on are escaped
        static string Echo(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s ?? "")
            {
                if (c == '%') sb.Append("%%");
                else if ("^&|<>()".IndexOf(c) >= 0) sb.Append('^').Append(c);
                else sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
