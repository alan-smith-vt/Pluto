using System;
using System.Collections.Generic;

// FrameS2kSchema  --  which .s2k tables SAP is known to read as Pluto writes them (2026-11-05). SAP ignores a table or
// field it does not know without a word, so a table counts as confirmed only once SAP has imported it and exported it
// again: the proven Pluto writers (the tank builder's golden file, SapSession's test beam) and SAP's own exports. New
// rows in any other table are still written, with a notice (FrameS2k.ToDoc): the SAP probe (FrameProbe,
// Build-Frame.ps1 -Probe / -ProbeCheck) settles the rest, and its results move tables into this list.
// C# 5 (prefix Frame).

public static class FrameS2kSchema
{
    static readonly HashSet<string> Proven = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "PROGRAM CONTROL", "JOINT COORDINATES", "JOINT RESTRAINT ASSIGNMENTS", "JOINT LOCAL AXES ASSIGNMENTS 1 - TYPICAL",
        "MATERIAL PROPERTIES 01 - GENERAL", "MATERIAL PROPERTIES 02 - BASIC MECHANICAL PROPERTIES",
        "FRAME SECTION PROPERTIES 01 - GENERAL", "CONNECTIVITY - FRAME", "FRAME SECTION ASSIGNMENTS", "FRAME LOCAL AXES ASSIGNMENTS 1 - TYPICAL",
        "FRAME INSERTION POINT ASSIGNMENTS",
        "LINK PROPERTY DEFINITIONS 01 - GENERAL", "LINK PROPERTY DEFINITIONS 05 - GAP", "CONNECTIVITY - LINK", "LINK PROPERTY ASSIGNMENTS",
        "GROUPS 1 - DEFINITIONS", "GROUPS 2 - ASSIGNMENTS", "LOAD PATTERN DEFINITIONS", "JOINT LOADS - FORCE", "JOINT LOADS - GROUND DISPLACEMENT",
        "LOAD CASE DEFINITIONS", "CASE - STATIC 1 - LOAD ASSIGNMENTS", "CASE - STATIC 2 - NONLINEAR LOAD APPLICATION",
    };

    public static bool Confirmed(string table) { return Proven.Contains(table ?? ""); }
}
