using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Windows.Forms;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.Plugins;

namespace PlutoNavis
{
    // Which properties name a duct service (2026-10-07): for each selected item only (no parents, no
    // children; select the tree level you want in the Selection Tree), every property whose value contains
    // one of the service codes, as "Tab | Property = value". Read-only; result in a window whose text can be
    // copied. Matching ignores case.
    // The codes are project data, so they are not in the repo: C:\Temp\hvac\service-codes.txt (comma
    // separated), asked for on first use; edit the file (or delete it to be asked again) to change them.
    [Plugin("ServiceButton", "Pluto",
        DisplayName = "Pluto Service",
        ToolTip = "Read-only: which properties of the selected item contain the service codes in C:\\Temp\\hvac\\service-codes.txt")]
    [AddInPlugin(AddInLocation.AddIn)]
    public class ServiceButton : AddInPlugin
    {
        const string CodesFile = @"C:\Temp\hvac\service-codes.txt";
        static string[] Codes;

        public override int Execute(params string[] parameters)
        {
            Document doc = Autodesk.Navisworks.Api.Application.ActiveDocument;
            if (doc == null || doc.CurrentSelection.SelectedItems.Count == 0) { MessageBox.Show("Select an object first.", "Pluto Service"); return 0; }
            Codes = LoadCodes();
            if (Codes == null) return 0;
            var s = new StringBuilder();
            s.AppendLine("Codes: " + string.Join(", ", Codes) + "   (selected item only: no parents, no children)");
            foreach (ModelItem sel in doc.CurrentSelection.SelectedItems)
            {
                var hits = new List<string>();
                foreach (PropertyCategory pc in sel.PropertyCategories)
                    foreach (DataProperty dp in pc.Properties)
                    {
                        string v;
                        try { v = dp.Value == null ? "" : dp.Value.ToDisplayString(); } catch (Exception) { v = ""; }
                        if (v == null || !Mentions(v)) continue;
                        hits.Add("  " + pc.DisplayName + " | " + dp.DisplayName + " = " + v);
                    }
                s.AppendLine();
                s.AppendLine(sel.DisplayName + " [" + sel.ClassDisplayName + "]" + (hits.Count == 0 ? "  (none)" : ""));
                foreach (string h in hits) s.AppendLine(h);
            }
            Show(s.ToString());
            return 0;
        }

        // Codes from the local file; first use: ask (one line, comma separated) and save. null = cancelled.
        static string[] LoadCodes()
        {
            string line = System.IO.File.Exists(CodesFile) ? System.IO.File.ReadAllText(CodesFile) : Ask();
            if (line == null) return null;
            var codes = new List<string>();
            foreach (string c in line.Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                if (c.Trim().Length > 0) codes.Add(c.Trim());
            if (codes.Count == 0) { MessageBox.Show("No codes in " + CodesFile + ".", "Pluto Service"); return null; }
            return codes.ToArray();
        }

        static string Ask()
        {
            var f = new Form { Text = "Pluto Service: codes", Width = 460, Height = 150, StartPosition = FormStartPosition.CenterScreen,
                FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false };
            var lb = new Label { Text = "Service codes to look for, comma separated (saved to " + CodesFile + "):", Left = 10, Top = 10, Width = 430 };
            var tb = new TextBox { Left = 10, Top = 35, Width = 420 };
            var ok = new Button { Text = "OK", Left = 270, Top = 70, Width = 75, DialogResult = DialogResult.OK };
            var cancel = new Button { Text = "Cancel", Left = 355, Top = 70, Width = 75, DialogResult = DialogResult.Cancel };
            f.Controls.AddRange(new Control[] { lb, tb, ok, cancel });
            f.AcceptButton = ok; f.CancelButton = cancel;
            if (f.ShowDialog() != DialogResult.OK || tb.Text.Trim().Length == 0) return null;
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(CodesFile));
            System.IO.File.WriteAllText(CodesFile, tb.Text.Trim());
            return tb.Text;
        }

        static bool Mentions(string v)
        {
            foreach (string c in Codes) if (v.IndexOf(c, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        static void Show(string text)
        {
            var f = new Form { Text = "Pluto Service", Width = 900, Height = 600, StartPosition = FormStartPosition.CenterScreen };
            var tb = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill,
                Font = new Font("Consolas", 9f), Text = text.Replace("\r\n", "\n").Replace("\n", "\r\n") };
            f.Controls.Add(tb);
            f.Show();   // modeless: keep clicking in Navisworks and run it again
        }
    }
}
