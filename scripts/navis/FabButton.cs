using Autodesk.Navisworks.Api.Plugins;

namespace PlutoNavis
{
    // Fabrication parts only: elements whose Element/Category contains "Fabrication" (MEP Fabrication
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
