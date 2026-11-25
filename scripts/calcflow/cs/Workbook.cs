// Workbook.cs -- the engineer's workbook (CalcFlow.xlsx) and its launchers.
// The workbook is CalcFlow's front door: a Start tab whose link cells run the steps. Excel buttons would need macros
// (an .xlsm), which production machines block, so each step is a .cmd launcher beside the workbook and the Start tab
// links to it; where links to files are blocked too, the launcher is double-clicked. This is the pattern of Pluto's
// pipe and frame workbooks (scripts/arms/workbook/WbRun.cs, WbStart.cs), copied here because CalcFlow compiles apart
// from Pluto's batch.
//   WriteNew   CalcFlow.xlsx (Start: the steps, the link cells, the settings table tblStart) and the launchers,
//              written once: an existing workbook is never overwritten (once saved, only Excel writes it)
//   Settings   tblStart read back (the saved file, also while Excel has it open)
//   Refresh    the one time a saved workbook is written again, and only when asked: it is rebuilt around the model
//              and the templates folder as they are now, with everything that was filled in carried over
//              (ProjectBook.Sheets), and the workbook as it was is kept beside it under a dated name. Excel must
//              have closed it.
// The Start tab also checks the force units as they are typed (formula cells: no step has to run).
// What a launcher writes goes into other files (Model_Data.xlsx ...): no script can add tabs to a workbook that is
// open in Excel. C# 5 (Windows PowerShell 5.1 Add-Type, warnings are errors).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace CalcFlow
{
    public sealed class RefreshResult
    {
        public string Workbook = "", Backup = "";
        public readonly List<string> Notes = new List<string>();
    }

    public static class EngineerWorkbook
    {
        public const string FileName = "CalcFlow.xlsx";
        public const string PostProcessLauncher = "Post-process ANL.cmd";
        public const string AnlSetting = "Anl file";

        public const string CheckLauncher = "Check.cmd", RunLauncher = "Run calcs.cmd", RefreshLauncher = "Refresh config.cmd";
        public const string TemplatesSetting = "Templates folder", LimitSetting = "DCR limit", FillSetting = "Input fill";

        // the workbook and its launchers in dir; script: the full path of CalcFlow.ps1. pkg (the model read from the
        // .anl) fills Model Config and Load Cases, harvest (the templates learned) the schedule tabs; either may be
        // null. Returns the files written.
        public static string[] WriteNew(string dir, string script, AnalysisPackage pkg, HarvestResult harvest, string templatesDir)
        {
            Directory.CreateDirectory(dir);
            string wb = Path.Combine(dir, FileName);
            if (File.Exists(wb)) throw new Exception(wb + " exists: the workbook is yours once written and is never overwritten.");
            List<XOutSheet> tabs = ProjectBook.Sheets(pkg, harvest);
            var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            settings[TemplatesSetting] = templatesDir ?? "";
            settings[FillSetting] = string.Join(", ", Harvest.InputFills.ToArray());
            var sheets = new List<XOutSheet> { StartSheet(settings, ProjectBook.UnitChecks(tabs)) };
            sheets.AddRange(tabs);
            XlsxWriter.Write(wb, sheets, null);
            var files = new List<string> { wb };
            files.AddRange(WriteLaunchers(dir, script));
            return files.ToArray();
        }

        public static string[] WriteNew(string dir, string script) { return WriteNew(dir, script, null, null, ""); }

        // The saved workbook written again around the model (pkg, may be null: Model Config and Load Cases stay as
        // they are) and the templates (harvest, may be null: the template tabs stay as they are). Everything filled in
        // is carried over; the workbook as it was is kept beside it. Refused while Excel has it open.
        public static RefreshResult Refresh(string workbook, string script, AnalysisPackage pkg, HarvestResult harvest, string templatesDir)
        {
            if (!File.Exists(workbook)) throw new Exception("No such workbook: " + workbook);
            if (SharedFile.IsLocked(workbook)) throw new Exception(Path.GetFileName(workbook) + " is open in Excel: save and close it, then refresh.");
            var res = new RefreshResult();
            res.Workbook = workbook;
            XWorkbook old = Xlsx.Read(workbook, 2048);
            Dictionary<string, string> settings = Settings(workbook);
            List<XOutSheet> tabs = ProjectBook.Sheets(pkg, harvest, old, templatesDir ?? "", res.Notes);
            var sheets = new List<XOutSheet> { StartSheet(settings, ProjectBook.UnitChecks(tabs)) };
            sheets.AddRange(tabs);
            var mine = new HashSet<string>(sheets.Select(s => s.Name), StringComparer.OrdinalIgnoreCase);
            foreach (XSheet s in old.Sheets)
                if (!mine.Contains(s.Name)) res.Notes.Add("tab '" + s.Name + "' is not one of CalcFlow's: it is not in the refreshed workbook (it is in the copy kept beside it)");
            string dir = Path.GetDirectoryName(workbook), tmp = Path.Combine(dir, "~refresh-" + Path.GetFileName(workbook));
            XlsxWriter.Write(tmp, sheets, null);
            res.Backup = Path.Combine(dir, Path.GetFileNameWithoutExtension(workbook) + " (before refresh " + DateTime.Now.ToString("yyyy-MM-dd HHmmss", System.Globalization.CultureInfo.InvariantCulture) + ").xlsx");
            File.Move(workbook, res.Backup);
            File.Move(tmp, workbook);
            if (!string.IsNullOrEmpty(script)) WriteLaunchers(dir, script);
            return res;
        }

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
                    new[] { "Checks the saved workbook, then fills and calculates the connection calcs that are due", "and writes the connection summary." })),
                WriteLauncher(dir, RefreshLauncher, LauncherText(script, "refresh", "Refresh config",
                    new[] { "Rebuilds the workbook around the model and the templates folder as they are now,", "keeping everything you have filled in. The workbook as it was is kept beside it." }))
            };
        }

        // settings: the values of tblStart to write (a new workbook: the templates folder; a refresh: what was saved).
        // checks: the template tabs whose force units the tab watches
        static XOutSheet StartSheet(Dictionary<string, string> settings, List<ProjectBook.UnitCheck> checks)
        {
            Func<string, string, string> saved = (k, d) => { string v; return settings != null && settings.TryGetValue(k, out v) && v != null ? v : d; };
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
                new object[] { "Templates and Outputs: once per template", "On a template's tab, the Source row says where each column comes from (typed on the row, a force of the beam end, the beam number ...) and the Unit row its unit. On Outputs, mark the results to read back with Use = Y. Template Sheets lists every sheet of every template workbook and the tab that holds its inputs. On any tab, select a cell for a note on what it is and what it takes." },
                new object[] { "Model Config: a connection at each beam end", "One row per beam. Choose a mark in Connection I and Connection J where a calc is needed, and correct Frames into (the member that end connects to). Then run Refresh config (below) when a template has no tab yet (a library of more than 20 templates gets a tab for the ones in use), or takes values per beam end: its tab of beam ends is listed from this tab." },
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
            const string refresh = "When something changed: the model has new beams or load cases, a template was added to the templates folder or its cells moved, or you gave a template's Input cells or Result cells on the Templates tab. Rebuilds this workbook around what you have filled in: nothing typed is lost, and the workbook as it was is kept beside it. The window asks you to save and close this workbook, and opens it again when it is done.";
            s.Add(new XCell("When needed", step), go(RefreshLauncher, "Refresh config"), new XCell(refresh, body));
            s.RowHeights[s.Rows.Count - 1] = 66;
            s.Add();
            int top = s.Rows.Count;
            s.Add(new XCell("Setting", Palette.Header(ColumnKind.Key)), new XCell("Value", Palette.Header(ColumnKind.Input)), new XCell("Notes", Palette.Header(ColumnKind.Reference)));
            Action<string, string, string> setting = (k, v, n) =>
            {
                s.Add(new XCell(k, Palette.Data(ColumnKind.Key)), new XCell(v, Palette.Data(ColumnKind.Input)), new XCell(n, Palette.Data(ColumnKind.Reference)));
                Hints.Range(s, Xlsx.Ref(s.Rows.Count - 1, 1), k, n);
            };
            setting(AnlSetting, saved(AnlSetting, ""), "The STAAD .anl to read. Leave blank to be asked for it each time. A name without a folder is looked for beside this workbook.");
            setting(TemplatesSetting, saved(TemplatesSetting, ""), "The folder that holds the calc template workbooks.");
            Palette.When(s, "B" + s.Rows.Count, "B" + s.Rows.Count + "=\"\"", Palette.Need);      // no template tab can be made without it
            setting(LimitSetting, saved(LimitSetting, "1.0"), "A DCR above this is flagged in the summary.");
            setting(FillSetting, saved(FillSetting, ""), "Leave blank when the cells to fill in are yellow in your templates. Else the fill colour that marks them, six hex digits (several: with commas), as CalcFlow.ps1 map lists them on its Fills tab. Then run Refresh config.");
            var t = new XOutTable();
            t.Name = "tblStart"; t.Top = top; t.Left = 0; t.Right = 2; t.Bottom = top + 4; t.Style = Palette.TableStyle;
            s.Tables.Add(t);

            // ---- what the colours of a cell say (cs/Needs.cs sets them from what is chosen on the tabs)
            s.Add();
            s.Add(new XCell("Colours", Palette.Title()));
            s.Add(new XCell("", body), new XCell("A cell changes colour as you choose things on the tabs. Check says the same in words.", Palette.Note()));
            foreach (string[] l in Needs.Legend)
            {
                var swatch = new XStyle(); swatch.Fill = l[0]; swatch.Color = l[1]; swatch.Bold = true; swatch.Border = true; swatch.HAlign = "center"; swatch.VAlign = "top";
                s.Add(new XCell(l[2], swatch), new XCell(l[3], body));
                s.Merges.Add("B" + s.Rows.Count + ":C" + s.Rows.Count);
                s.RowHeights[s.Rows.Count - 1] = l[3].Length > 150 ? 34 : 18;
            }

            // ---- the force units, checked as they are typed
            s.Add();
            s.Add(new XCell("Force units", Palette.Title()));
            s.Add(new XCell("", body), new XCell("Checked as you type, for every template a connection uses: each column that takes a force of the model (Source Fx to Mz) needs its Unit, and so does a load table on the Templates tab. The forces are converted from the model's units to the unit you give.", Palette.Note()));
            int ctop = s.Rows.Count;
            s.Add(new XCell("Units", Palette.Header(ColumnKind.Tool)), new XCell("Template tab", Palette.Header(ColumnKind.Key)), new XCell("Says", Palette.Header(ColumnKind.Tool)), new XCell("Missing", Palette.Header(ColumnKind.Tool)));
            var center = new XStyle(); center.HAlign = "center"; center.Bold = true; center.Border = true;
            foreach (ProjectBook.UnitCheck c in checks ?? new List<ProjectBook.UnitCheck>())
            {
                int r = s.Rows.Count + 1;
                string tab = c.Tab.Replace("\"", "\"\"");
                const string ok = "every force it takes has its unit", unused = "no connection uses this template yet",
                    fix = " force unit(s) missing: fill the Unit row of the tab, or the Force unit and Moment unit of its row on the Templates tab";
                s.Add(new XCell(new XFormula("IF(COUNTIF('" + ProjectBook.ConnectionLib + "'!$B$3:$B$2000,\"" + tab + "\")=0,\"Not used\",IF(D" + r + "=0,\"OK\",\"Fix\"))", !c.Used ? "Not used" : c.Missing == 0 ? "OK" : "Fix"), center),
                    new XCell(new XFormula("HYPERLINK(\"#'" + tab.Replace("'", "''") + "'!A1\",\"" + tab + "\")", c.Tab), link),
                    new XCell(new XFormula("IF(A" + r + "=\"Not used\",\"" + unused + "\",IF(D" + r + "=0,\"" + ok + "\",D" + r + "&\"" + fix + "\"))", !c.Used ? unused : c.Missing == 0 ? ok : c.Missing + fix), body),
                    new XCell(new XFormula(c.Formula, c.Missing.ToString(System.Globalization.CultureInfo.InvariantCulture)), body));
            }
            if (checks == null || checks.Count == 0) s.Add("", new XCell("(no template tab yet)", body), "", "");
            int cbottom = s.Rows.Count;
            Palette.Highlight(s, "A" + (ctop + 2) + ":A" + cbottom, "OK", Palette.Good);
            Palette.Highlight(s, "A" + (ctop + 2) + ":A" + cbottom, "Fix", Palette.Bad);
            Palette.Highlight(s, "A" + (ctop + 2) + ":A" + cbottom, "Not used", Palette.Muted);
            s.Width(0, 17); s.Width(1, 46); s.Width(2, 110);
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
