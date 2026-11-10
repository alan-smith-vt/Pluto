// Add-on (2026-10-26): hand-picked drops for the clean model. The viewer's Cut picker (viewer/scripts/addons/
// cutPicker.js) writes a list of the picked clean members' parts; DuctDrop in duct-config.psd1 (or -Drop <file>)
// names it, and the clean model loses those members after the scope cut. Self-contained: empty DuctDrop = off;
// delete this file, its call in BuildClean and the -Drop lines in Export-DuctsViewer.ps1 to remove it.
//
// File: one part per line, a Revit ElementId or N:<NavisId> (Navisworks-only members); '#' starts a comment.
// A clean member goes when every part it is made of is listed, except one ending at a cut (CUT_END): a cut splits
// a part in two and the kept half carries the same id as the picked far half.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

public partial class DuctsToPluto
{

    // Adds one list's parts to ManualDrop (several lists add up; the caller clears); returns the parts it read.
    // A cut-points CSV (Run-Sap -Points header) given here by mistake is refused rather than read as ids.
    public static int ReadManualDrop(DuctSettings settings, string path)
    {
        int n = 0;
        foreach (string raw in File.ReadAllLines(path))
        {
            string s = raw; int h = s.IndexOf('#'); if (h >= 0) s = s.Substring(0, h);
            s = s.Trim(); if (s == "") continue;
            if (s.StartsWith("Name,Found", StringComparison.OrdinalIgnoreCase))
                throw new Exception("DuctsToPluto: " + path + " is a cut points CSV (DuctCuts), not a drop list (DuctDrop)");
            settings.ManualDrop.Add(s); n++;
        }
        return n;
    }

}
