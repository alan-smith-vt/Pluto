// OoxmlCheck -- checks .xlsx files against the Office Open XML schema with Microsoft's Open XML SDK validator.
//   OoxmlCheck <file.xlsx>...   prints one line per schema error (file, part, path, description); exit 0 when every
//                               file is clean, 1 when any has errors, 2 when a file cannot be opened as a workbook
// Development only (tests\Tools.ps1 builds and runs it). Written as C# 5 so CalcFlow's lint holds.

using System;
using System.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;

namespace CalcFlow.Tools
{
    public static class OoxmlCheck
    {
        public static int Main(string[] args)
        {
            int code = 0;
            var validator = new OpenXmlValidator(FileFormatVersions.Office2016);
            foreach (string f in args)
            {
                try
                {
                    using (SpreadsheetDocument doc = SpreadsheetDocument.Open(f, false))
                    {
                        var errors = validator.Validate(doc).ToList();
                        foreach (ValidationErrorInfo e in errors.Take(50))
                            Console.WriteLine("{0}: {1} {2}: {3}", f, e.Part == null ? "" : e.Part.Uri.ToString(), e.Path == null ? "" : e.Path.XPath, e.Description);
                        if (errors.Count > 50) Console.WriteLine("{0}: and {1} more errors", f, errors.Count - 50);
                        if (errors.Count > 0 && code == 0) code = 1;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("{0}: cannot be opened as a workbook: {1}", f, ex.Message);
                    code = 2;
                }
            }
            return code;
        }
    }
}
