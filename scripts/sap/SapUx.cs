using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using SAP2000v1;

// What the engineers see when they open the HVAC SAP model (user 2026-11-25). Called by SapDucts.Build before Save.
//   groups      every group defined once, in reading order: model-wide (DUCT_ALL, SRC_, MAT_), steel (STEEL_), supports
//               (SUPPORT_, TAP_STUBS), penetrations (PEN_), checks (FLAG_, SPAN_), systems (SYS_) last. Colours:
//               SYS_ = the run colour (frames.csv SystemColor), STEEL_ALL grey, SUPPORT_GUIDE / _ANCHOR, PEN_, TAP_STUBS.
//               Checks and aggregate groups stay uncoloured: an object is in many groups, and a coloured flag would
//               paint over its system. A group holds frames, points and links alike.
//   properties  what the blank template brings in and nothing uses is deleted: frame sections, area sections, link
//               properties, then materials. DUCT_CS / DUCT_SS and SUP_GUIDE / SUP_ANCHOR always stay.
//   legend      <model>.legend.txt beside the .sdb: groups with counts and meaning, section / material naming, how to
//               make a guide an anchor, and the two display settings the API cannot set (Color By: Groups, extrusion).
// Display options (colour by, extrusion) have no API in SAP 26 (cView: RefreshView / RefreshWindow only; cNamedDisplay:
// list / rename / delete): the legend says where to set them. Units are the model's own (kip, in).
public partial class SapDucts
{
    static readonly string[] GroupOrder = { "DUCT_ALL", "SRC_", "MAT_", "STEEL_", "SUPPORT_", "TAP_STUBS", "PEN_", "FLAG_", "SPAN_", "SYS_" };
    static readonly Dictionary<string, string> FixedColors = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        { "STEEL_ALL", "#8b919c" }, { "SUPPORT_GUIDE", "#ffcc00" }, { "SUPPORT_ANCHOR", "#ff8a3d" },
        { "PEN_WALL", "#b07cff" }, { "PEN_SLAB", "#7c5cff" }, { "TAP_STUBS", "#5a5f68" }
    };

    static int OrderOf(string g)
    {
        for (int i = 0; i < GroupOrder.Length; i++) if (g == GroupOrder[i] || (GroupOrder[i].EndsWith("_") && g.StartsWith(GroupOrder[i]))) return i;
        return GroupOrder.Length - 1;   // unknown: just before the systems
    }

    // define every group once (order, colour), then assign frames / points / links; returns group -> member count
    SortedDictionary<string, int> DefineGroups(Dictionary<string, List<string>> frames, Dictionary<string, int> sysColor)
    {
        var all = new HashSet<string>(frames.Keys.Concat(pointGroups.Keys).Concat(linkGroups.Keys), StringComparer.Ordinal);
        foreach (string g in all.OrderBy(OrderOf).ThenBy(x => x, StringComparer.Ordinal))
        {
            int col = -1; string hex;
            if (!sysColor.TryGetValue(g, out col)) col = FixedColors.TryGetValue(g, out hex) && OleColor(hex, out col) ? col : -1;
            Check(M.GroupDef.SetGroup(g, col, true, true, true, true, true, true, true, true, true, true, true), "SetGroup " + g);
        }
        var count = new SortedDictionary<string, int>(StringComparer.Ordinal);
        Action<string, int> add = (g, n) => { int c; count.TryGetValue(g, out c); count[g] = c + n; };
        foreach (KeyValuePair<string, List<string>> kv in frames) { foreach (string f in kv.Value) Check(M.FrameObj.SetGroupAssign(f, kv.Key, false, eItemType.Objects), "SetGroupAssign " + f + " " + kv.Key); add(kv.Key, kv.Value.Count); }
        foreach (KeyValuePair<string, List<string>> kv in pointGroups) { foreach (string p in kv.Value) Check(M.PointObj.SetGroupAssign(p, kv.Key, false, eItemType.Objects), "SetGroupAssign " + p + " " + kv.Key); add(kv.Key, kv.Value.Count); }
        foreach (KeyValuePair<string, List<string>> kv in linkGroups) { foreach (string l in kv.Value) Check(M.LinkObj.SetGroupAssign(l, kv.Key, false, eItemType.Objects), "SetGroupAssign " + l + " " + kv.Key); add(kv.Key, kv.Value.Count); }
        return count;
    }

    // delete the properties nothing uses (the blank template's defaults, a NOSIZE section no frame took); returns a note
    string CleanProperties()
    {
        var keepMat = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { MatCS, MatSS };
        var keepLink = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { SapDucts.LinkGuide, SapDucts.LinkAnchor };
        int n = 0; string[] names = null;
        // frame sections in use
        var usedSec = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        M.FrameObj.GetNameList(ref n, ref names);
        for (int i = 0; i < n; i++) { string s = "", auto = ""; if (M.FrameObj.GetSection(names[i], ref s, ref auto) == 0) usedSec.Add(s); }
        var gone = new List<string>(); int failed = 0;
        n = 0; names = null; M.PropFrame.GetNameList(ref n, ref names);
        for (int i = 0; i < n; i++)
        {
            if (usedSec.Contains(names[i])) { string mat = ""; if (M.PropFrame.GetMaterial(names[i], ref mat) == 0) keepMat.Add(mat); continue; }
            if (M.PropFrame.Delete(names[i]) == 0) gone.Add(names[i]); else failed++;
        }
        // areas: the duct model has none
        n = 0; names = null; M.PropArea.GetNameList(ref n, ref names);
        for (int i = 0; i < n; i++) { if (M.PropArea.Delete(names[i]) == 0) gone.Add(names[i]); else failed++; }
        n = 0; names = null; M.PropLink.GetNameList(ref n, ref names);
        for (int i = 0; i < n; i++) { if (keepLink.Contains(names[i])) continue; if (M.PropLink.Delete(names[i]) == 0) gone.Add(names[i]); else failed++; }
        n = 0; names = null; M.PropMaterial.GetNameList(ref n, ref names);
        for (int i = 0; i < n; i++) { if (keepMat.Contains(names[i])) continue; if (M.PropMaterial.Delete(names[i]) == 0) gone.Add(names[i]); else failed++; }
        return F("NEW properties: {0} unused ones deleted ({1}){2}", gone.Count, gone.Count > 0 ? string.Join(", ", gone.Take(15).ToArray()) + (gone.Count > 15 ? " ..." : "") : "none",
            failed > 0 ? F("; {0} SAP would not delete (still referenced)", failed) : "");
    }

    static readonly Dictionary<string, string> FlagMeaning = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        { "NO_THICKNESS", "duct wall thickness not given (thickness table / Navisworks): the section uses the 0.001 in placeholder" },
        { "NO_SIZE", "duct with no size: section DUCT_NOSIZE (12x12 placeholder)" },
        { "NO_ORIENTATION", "no width direction: the box's local axes are SAP's default (or a support link without a duct axis)" },
        { "MATERIAL_DEFAULT", "system not listed in DuctMaterials: the default material was used" },
        { "UNSUPPORTED_SPAN", "duct longer than the span limit between supports (SPAN_<id> groups, longest first)" },
        { "OPEN_NEAR", "steel end left open with other steel close by: probably a missed connection" },
        { "NO_SLAB", "vertical steel end not fixed: no slab within reach" },
        { "SUPPORT_NO_HANGER", "support with no Navisworks hanger near it" },
        { "HANGER_NO_STEEL_SUPPORT", "Navisworks hanger with no steel support near it" },
        { "LARGE_GAP", "support with a large duct-to-steel gap" },
        { "STEEL_OVERLAP", "steel top above the duct bottom at a support" },
        { "MANUAL_BRIDGE", "joined by a hand-picked bridge (viewer Bridge picker)" },
        { "FRAGMENT_JOINED", "named a fragment, but connected to its main in the final model: part of the main" },
        { "SPLIT_FROM_MAIN", "named the main, but not connected to it in the final model: a fragment (left out of SAP by default)" },
        { "NAVIS_BRIDGE", "Navisworks-only part joined into a gap of the Revit duct" },
        { "SIZE_FROM_NEIGHBOURS", "size taken from the neighbouring ducts, not the stated one" },
        { "CUT_END", "duct ends at a scope cut" },
        { "OVERLAP_SIZE_CONFLICT", "two overlapping ducts of different sizes, both kept" },
        { "LARGE_SIDE_VERTICAL", "box with its larger side vertical" },
        { "ROTATED", "box turned 90 deg about its centreline (larger side horizontal)" },
        { "OPEN_END", "duct end connected to nothing" },
        { "JOINED_BY_PROXIMITY", "open duct end within 6 in of a duct of its own system, connected to it (rigid link to its centreline, or directly)" },
        { "SUPPORT", "steel joint added where a duct support lands on the member" },
        { "GUID_MISMATCH", "Revit and Navisworks give the same part different IfcGUIDs" },
        { "NEAR_GAP", "two duct ends close together but not touching, joined across the gap" },
        { "REVIT_ONLY", "part only in Revit (no Navisworks row)" },
        { "NAVIS_ONLY", "part only in Navisworks (no Revit element)" },
        { "FLOATING", "duct piece with no support (left out of the model)" },
        { "ROTATION_CONFLICT", "run whose boxes disagree on which side is vertical: left as modelled" },
        { "RUNNAME_DEVIATION", "part whose RunName differs from the rest of its run" },
        { "SIZE_DIFFERS", "Revit connector size differs from the Navisworks stated size (Revit used)" },
        { "SPLIT", "part split in two at a joint on its length" },
        { "SYSTEM_CHANGE", "system name changes along a connected run" },
        { "SYSTEM_NAME_CONFLICT", "part named for one system but connected to another (the majority name used)" },
    };

    static string GroupMeaning(string g)
    {
        if (g == "DUCT_ALL") return "every duct frame";
        if (g.StartsWith("SRC_")) return "duct frames by source (" + g.Substring(4).ToLowerInvariant() + ")";
        if (g == "MAT_CARBON") return "duct frames of carbon steel (" + MatCS + ")";
        if (g == "MAT_STAINLESS") return "duct frames of stainless steel (" + MatSS + ")";
        if (g == "STEEL_ALL") return "every steel frame";
        if (g == "STEEL_RELEASED") return "steel frames with an M2+M3 end release (hinge)";
        if (g == "STEEL_JOINT_OFFSET") return "steel frames whose end is offset from its joint (the member is not moved)";
        if (g.StartsWith("STEEL_")) return "steel joints: " + g.Substring(6).ToLowerInvariant().Replace('_', ' ');
        if (g == "SUPPORT_ALL") return "every duct support link";
        if (g == "SUPPORT_GUIDE") return "guide links (" + SapDucts.LinkGuide + ")";
        if (g == "SUPPORT_ANCHOR") return "anchor links (" + SapDucts.LinkAnchor + ")";
        if (g == "TAP_STUBS") return "rigid tap stubs (duct centreline to a branch on its side)";
        if (g == "PEN_WALL") return "duct joints where a duct passes through a wall (no restraint)";
        if (g == "PEN_SLAB") return "duct joints where a duct passes through a slab (no restraint)";
        if (g.StartsWith("SPAN_")) return "one unsupported span";
        if (g.StartsWith("SYS_")) return "system " + g.Substring(4);
        if (g.StartsWith("FLAG_")) { string m; return FlagMeaning.TryGetValue(g.Substring(5), out m) ? "check: " + m : "check (see the run's match.txt)"; }
        return "";
    }

    void WriteLegend(string savePath, SortedDictionary<string, int> count, string propNote)
    {
        string path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(savePath)) ?? ".", Path.GetFileNameWithoutExtension(savePath) + ".legend.txt");
        var s = new StringBuilder();
        s.AppendLine("HVAC duct SAP model " + Path.GetFileName(savePath) + "  (Pluto, " + DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ")");
        s.AppendLine("Units kip, in. Coordinates: the plant model's, absolute inches.");
        s.AppendLine();
        s.AppendLine("ON OPENING");
        s.AppendLine("  Display > Set Display Options > Color By: Groups   (systems in their colours, steel grey, supports yellow / orange)");
        s.AppendLine("  Display > Set Display Options > Extrude View        (the duct boxes and W shapes as solids)");
        s.AppendLine("  The API cannot save these two settings in the file.");
        s.AppendLine();
        s.AppendLine("OBJECT NAMES");
        s.AppendLine("  Duct frames <system>_F<n> (SYSA_F12; fragments SYSA_FRAG_F7; other systems OTHER_<name>_F<n>), rigid tap stubs");
        s.AppendLine("  LNK_F<n>, steel STL_SF<n>, support links SUP_<id>. F<n> / SF<n> / <id> are the rows of the run's frames.csv,");
        s.AppendLine("  steel_frames.csv and supports.csv. Joints: N<n> duct, S<n> steel, SS<n> a support joint on steel.");
        s.AppendLine("  A duct frame's GUID field holds its Revit ElementId / Navisworks id.");
        s.AppendLine();
        s.AppendLine("SUPPORTS");
        s.AppendLine("  Every duct support is a two-joint link from the duct joint to the steel joint: local 1 along the stub (duct ->");
        s.AppendLine("  steel), local 2 along the duct, local 3 across it.");
        s.AppendLine("  " + SapDucts.LinkGuide + "   guide: rigid along the stub (U1) and across the duct (U3); slides along the duct (U2); rotations free");
        s.AppendLine("  " + SapDucts.LinkAnchor + "  anchor: rigid in all six");
        s.AppendLine("  To make a guide an anchor: select the link (Select > Groups > SUPPORT_GUIDE, or click it),");
        s.AppendLine("  Assign > Link/Support > Link/Support Properties > " + SapDucts.LinkAnchor + ".");
        s.AppendLine("  A run held only by guides along one line (no turn in plan) slides and twists: give it an anchor.");
        s.AppendLine();
        s.AppendLine("SECTIONS AND MATERIALS");
        s.AppendLine("  DUCT_<W>x<H>_t<thk>_<mat> box, DUCT_<D>dia_t<thk>_<mat> pipe; <mat> CS = " + MatCS + ", SS = " + MatSS + ".");
        s.AppendLine("  " + MatCS + "  carbon sheet (A1011): E 29000 ksi, Fy 33, Fu 52, alpha 6.5e-6 /F, 0.2836 lb/in3");
        s.AppendLine("  " + MatSS + "  stainless (304L):    E 28000 ksi, Fy 30, Fu 70, alpha 9.6e-6 /F, 0.29 lb/in3");
        s.AppendLine("  Thickness t0.001 is a placeholder (no thickness given): FLAG_NO_THICKNESS. DUCT_NOSIZE: no size, FLAG_NO_SIZE.");
        s.AppendLine("  Steel: W shapes by name (" + SteelMat + "); STEEL_NOSIZE where the steel had no section dimensions.");
        s.AppendLine("  " + propNote.Replace("NEW properties: ", "Template properties: "));
        s.AppendLine();
        s.AppendLine("GROUPS (members)");
        foreach (KeyValuePair<string, int> kv in count.OrderBy(k => OrderOf(k.Key)).ThenBy(k => k.Key, StringComparer.Ordinal))
            s.AppendLine(F("  {0,-28} {1,6}  {2}", kv.Key, kv.Value, GroupMeaning(kv.Key)));
        s.AppendLine();
        s.AppendLine("No loads, load cases or combinations are defined beyond SAP's default DEAD / MODAL.");
        File.WriteAllText(path, s.ToString(), new UTF8Encoding(false));
        LegendPath = path;
    }

    public string LegendPath = "";
}
