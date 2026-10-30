using System;
using System.Collections.Generic;

// Everything the duct converter takes from the duct config / command line (2026-10-30, split stage 1): one object,
// filled once by Export-DuctsViewer.ps1 and handed to the Export* entry points, instead of public statics poked
// one by one. Defaults = an empty config. Config keys in brackets (scripts\duct\duct-config.example.psd1).
public class DuctSettings
{
    // primary systems [DuctSystems '<name>=#rrggbb'] and pinned mains [DuctMains '<name>=<Revit id>']
    public readonly List<string> PrimarySystems = new List<string>();
    public readonly Dictionary<string, string> PrimaryColors = new Dictionary<string, string>(StringComparer.Ordinal);
    public readonly List<KeyValuePair<string, string>> PrimaryPins = new List<KeyValuePair<string, string>>();
    // scope cut points [DuctCuts]: global inches, the Navisworks frame frames.csv uses
    public readonly List<KeyValuePair<string, double[]>> CutPoints = new List<KeyValuePair<string, double[]>>();
    public double CutSnapIn = 6, KeepSnapIn = 48;   // keep points may be picked on a duct surface, far from the centreline
    // hand-picked drops [DuctDrop]: Revit part ids
    public readonly HashSet<string> ManualDrop = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    // Revit categories left out of the Revit Pluto Mesh _norun file [RevitMeshNoRunExclude]
    public readonly HashSet<string> NoRunExclude = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    // duct orientation [DuctRotateLargeVertical]
    public bool RotateLargeVertical = true;
    // duct-to-steel supports [DuctSupportGapIn, DuctSpanFt]
    public DuctSupports.Settings Supports = new DuctSupports.Settings();
    // Navisworks hanger check: a hanger and a steel support within this (in) along the duct match [DuctHangerMatchIn]
    public double HangerMatchIn = 24;
    // utility rack steel [DuctSteel] and its connection settings [DuctSteelSnapIn, DuctSteelNearIn]
    public readonly List<SteelMember> Steel = new List<SteelMember>();
    public SteelModel.Settings SteelSettings = new SteelModel.Settings();
    // folder of the files shared with the add-ins (room boxes) [AddinDir]
    public const string AddinDefaultDir = @"C:\Temp\hvac";   // where the add-ins read them
    public string AddinDir = AddinDefaultDir;
}
