using Autodesk.Navisworks.Api.Plugins;

namespace PlutoNavis
{
    // The fabrication leg of Pluto Ducts on its own: the Item/Type search for fabrication parts (MEP Fabrication
    // Ductwork / Hangers) and those parts only, same outputs, under C:\Temp\hvac\fab\<run>. Seconds, not the
    // ~10 min of the full run. Export-DuctsViewer.ps1 turns the run folder into its own overlay.
    [Plugin("FabButton", "Pluto",
        DisplayName = "Pluto Fab",
        ToolTip = "Read-only: fabrication ductwork / hangers only to C:\\Temp\\hvac\\fab")]
    [AddInPlugin(AddInLocation.AddIn)]
    public class FabButton : AddInPlugin
    {
        public override int Execute(params string[] parameters) { return DuctsButton.Run(true); }
    }
}
