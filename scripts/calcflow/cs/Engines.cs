// Engines.cs -- what calculates a calc: a batch of workbooks (copies of a template with their input cells already
// written by XlsxPatch) goes in, the same workbooks recalculated and saved come out, one per input, same file name.
//   LibreOfficeEngine  soffice headless (open source; any OS): one conversion per batch, a profile that recalculates
//                      every formula on load, a time limit that stops it
//   ExcelEngine        Excel through COM, in a Windows PowerShell 5.1 child (stages\Recalc-Workbooks.ps1); it is
//                      built in CalcFlow.ps1 as a DelegateEngine, since the stage is PowerShell
//   DelegateEngine     any recalculation given as a delegate (how PowerShell hands the Excel stage in)
//   ScriptedEngine     the tests' engine: outputs computed by a delegate from the inputs and written in as values
// Every engine names itself and its version; each record says which one calculated it.
// C# 5 (Windows PowerShell 5.1 Add-Type, warnings are errors).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace CalcFlow
{
    public interface ICalcEngine
    {
        string Name { get; }        // "LibreOffice", "Excel", "Scripted"
        string Version { get; }
        int BatchSize { get; }      // workbooks per Recalculate call

        // every file opened, recalculated in full and saved as outDir\<its file name>. A file that could not be
        // calculated is simply missing from outDir (the runner names it); a failure of the whole batch throws.
        void Recalculate(IList<string> files, string outDir);
    }

    public sealed class LibreOfficeEngine : ICalcEngine
    {
        readonly string _soffice, _profile;
        public int TimeoutSeconds = 900;
        public int Batch = 200;

        // profileDir: a folder of CalcFlow's own for LibreOffice's settings (never the engineer's profile)
        public LibreOfficeEngine(string soffice, string profileDir)
        {
            _soffice = soffice;
            _profile = profileDir;
        }

        public string Name { get { return "LibreOffice"; } }
        public string Version { get { string v = LibreOffice.Version(_soffice); return v.StartsWith("LibreOffice ", StringComparison.Ordinal) ? v.Substring(12) : v; } }
        public int BatchSize { get { return Batch; } }

        public void Recalculate(IList<string> files, string outDir)
        {
            foreach (var g in files.GroupBy(f => Path.GetExtension(f).ToLowerInvariant()))
            {
                string filter = g.Key == ".xlsm" ? "xlsm" : "xlsx";
                LibreOffice.Convert(_soffice, g.ToList(), outDir, filter, _profile, TimeoutSeconds, false);
            }
        }
    }

    public sealed class DelegateEngine : ICalcEngine
    {
        readonly string _name, _version;
        readonly int _batch;
        readonly Action<IList<string>, string> _recalc;

        public DelegateEngine(string name, string version, int batchSize, Action<IList<string>, string> recalc)
        {
            _name = name; _version = version; _batch = batchSize; _recalc = recalc;
        }

        public string Name { get { return _name; } }
        public string Version { get { return _version; } }
        public int BatchSize { get { return _batch; } }
        public void Recalculate(IList<string> files, string outDir) { _recalc(files, outDir); }
    }

    // The tests' engine. For each workbook the delegate gets its cells (TemplateBook) and returns the outputs to write,
    // keyed "Sheet!Cell" or "Sheet!name" (a defined name), as numbers, text, booleans or "#DIV/0!"-style errors.
    // A delegate that throws leaves that workbook out (a failed case).
    public sealed class ScriptedEngine : ICalcEngine
    {
        readonly Func<TemplateBook, IDictionary<string, object>> _calc;
        public int Batch = 50;
        public int Calls;
        public readonly List<string> Errors = new List<string>();    // file: why, for each workbook the delegate refused

        public ScriptedEngine(Func<TemplateBook, IDictionary<string, object>> calc) { _calc = calc; }

        public string Name { get { return "Scripted"; } }
        public string Version { get { return "1"; } }
        public int BatchSize { get { return Batch; } }

        public void Recalculate(IList<string> files, string outDir)
        {
            Calls++;
            Directory.CreateDirectory(outDir);
            foreach (string f in files)
            {
                TemplateBook b = TemplateBook.Open(f);
                IDictionary<string, object> outs;
                try { outs = _calc(b); }
                catch (Exception ex) { Errors.Add(Path.GetFileName(f) + ": " + ex.Message); continue; }
                var writes = new List<CellWrite>();
                foreach (var kv in outs)
                {
                    int bang = kv.Key.LastIndexOf('!');
                    string sheet = kv.Key.Substring(0, bang), cell = kv.Key.Substring(bang + 1);
                    TSheet ts; int r, c; string err;
                    if (!b.Locate(sheet, cell, out ts, out r, out c, out err)) throw new Exception("ScriptedEngine: " + err);
                    writes.Add(new CellWrite(ts.Name, Xlsx.Ref(r, c), kv.Value));
                }
                XlsxPatch.Write(f, Path.Combine(outDir, Path.GetFileName(f)), writes, false);
            }
        }
    }

    public static class Engines
    {
        // auto: Excel where it is installed (its COM class registered), else LibreOffice; null with why when neither.
        // Excel itself is driven from PowerShell (CalcFlow.ps1), so this only says which one to use.
        public static string Pick(string wanted, bool excelInstalled, out string why)
        {
            why = "";
            string w = (wanted ?? "auto").Trim().ToLowerInvariant();
            string so = LibreOffice.Find();
            if (w == "excel")
            {
                if (excelInstalled) return "excel";
                why = "Excel is not installed on this machine (use -Engine libreoffice)";
                return null;
            }
            if (w == "libreoffice")
            {
                if (so != null) return "libreoffice";
                why = "LibreOffice is not installed (soffice not found on PATH, in /usr/bin or in Program Files\\LibreOffice\\program)";
                return null;
            }
            if (w != "auto") { why = "-Engine is auto, excel or libreoffice (got '" + wanted + "')"; return null; }
            if (excelInstalled) return "excel";
            if (so != null) return "libreoffice";
            why = "neither Excel nor LibreOffice is installed: install LibreOffice (free) or run where Excel is";
            return null;
        }
    }
}
