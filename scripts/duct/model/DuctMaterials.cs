using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

// Duct material per system (user 2026-11-25): carbon or stainless, from DuctMaterials in duct-config.psd1
// (@{ <run> = 'carbon' | 'stainless' }), the rest DuctMaterialDefault (carbon). Run = the system name as the viewer shows
// it; "<run> fragments" and "other: <run>" match their run (DuctThickness.RunOf). Rigid links carry none.
// With DuctMaterials set, a frame of a system it does not list gets the default and the flag MATERIAL_DEFAULT (a SAP
// FLAG_ group). frames.csv Material -> SapDucts: materials DUCT_CS / DUCT_SS, sections ..._CS / ..._SS.
public static class DuctMaterials
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Apply(List<CEdge> clean, DuctSettings set)
    {
        var count = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var unlisted = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (CEdge ce in clean)
        {
            if (ce.Rigid || ce.Hanger) { ce.Material = ""; continue; }
            string sys = ce.Src.Count > 0 || ce.SysName != "" ? DuctGeom.SystemOf(ce) : ce.Source;
            string run = DuctThickness.RunOf(sys), m;
            if (set.Materials.TryGetValue(run, out m)) used.Add(run);
            else
            {
                m = set.MaterialDefault;
                if (set.Materials.Count > 0) { ce.Flags.Add("MATERIAL_DEFAULT"); int u; unlisted.TryGetValue(run, out u); unlisted[run] = u + 1; }
            }
            ce.Material = m;
            int c; count.TryGetValue(m, out c); count[m] = c + 1;
        }
        var unusedKeys = set.Materials.Keys.Where(k => !used.Contains(k)).OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
        string per = count.Count == 0 ? "no duct frames" : string.Join(", ", count.Select(kv => kv.Key + " " + kv.Value).ToArray());
        if (set.Materials.Count == 0)
            return string.Format(Inv, "materials: {0} (DuctMaterials not set: every duct {1}, the default)", per, set.MaterialDefault);
        return string.Format(Inv, "materials (DuctMaterials): {0}{1}{2}", per,
            unlisted.Count > 0 ? string.Format(Inv, "; systems not listed, {0} (MATERIAL_DEFAULT): {1}", set.MaterialDefault,
                string.Join(", ", unlisted.Select(kv => kv.Key + " (" + kv.Value + ")").ToArray())) : "",
            unusedKeys.Count > 0 ? "; listed but matching no frame: " + string.Join(", ", unusedKeys.ToArray()) : "");
    }
}
