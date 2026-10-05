using System.Windows.Forms;
using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.Plugins;

namespace PlutoNavis
{
    // Proof of concept: one button on the "Tool add-ins" ribbon tab.
    // Plugin id = "<ClassName>.<DeveloperId>" -> "HelloButton.Pluto".
    [Plugin("HelloButton", "Pluto",
        DisplayName = "Pluto Hello",
        ToolTip = "Proves the Pluto add-in loads.")]
    [AddInPlugin(AddInLocation.AddIn)]
    public class HelloButton : AddInPlugin
    {
        public override int Execute(params string[] parameters)
        {
            Document doc = Autodesk.Navisworks.Api.Application.ActiveDocument;   // not WinForms' Application
            string title = string.IsNullOrEmpty(doc.FileName) ? "(no file open)" : doc.FileName;
            int models = doc.Models.Count;

            MessageBox.Show(
                "Pluto add-in is alive.\n\nFile: " + title + "\nAppended models: " + models,
                "Pluto");
            return 0;
        }
    }
}
