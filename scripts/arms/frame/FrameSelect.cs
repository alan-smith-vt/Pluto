using System;
using System.Collections.Generic;
using System.Linq;

// FrameSelect  --  the frames a load row names (2026-11-05): one frame label, or several, separated by commas,
// semicolons or spaces, each one of
//   C12            a frame by its label
//   C1:C9          the frames from C1 to C9 in the order the Frames sheet lists them
//   group:STEEL    the frames of a SAP group           line:L-101   the frames of a line (the workbook's Line column)
//   kind:PIPE      the frames of a kind (Kind column)  section:W10X33   the frames of a section
//   all            every frame
// The result keeps the model's order and names each frame once. C# 5 (prefix Frame).

public static class FrameSelect
{
    // order: the frames in sheet order (for ranges); kindOf / lineOf: frame label -> its Kind / Line; err: what does not
    // resolve ("" when everything does)
    public static List<FrameMember> Frames(FrameModel m, string selector, IList<string> order, IDictionary<string, string> kindOf, IDictionary<string, string> lineOf, out string err)
    {
        err = "";
        var byLabel = new Dictionary<string, FrameMember>(StringComparer.Ordinal);
        foreach (var f in m.Frames) byLabel[f.Label] = f;
        var hit = new HashSet<FrameMember>();
        var bad = new List<string>();
        foreach (string raw in (selector ?? "").Split(new[] { ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string tok = raw.Trim();
            int colon = tok.IndexOf(':');
            string head = colon > 0 ? tok.Substring(0, colon).ToLowerInvariant() : "", arg = colon > 0 ? tok.Substring(colon + 1) : "";
            FrameMember one;
            if (tok.Equals("all", StringComparison.OrdinalIgnoreCase)) { foreach (var f in m.Frames) hit.Add(f); continue; }
            if (byLabel.TryGetValue(tok, out one)) { hit.Add(one); continue; }
            int before = hit.Count;
            if (head == "group")
            {
                var g = m.Groups.FirstOrDefault(x => string.Equals(x.Label, arg, StringComparison.OrdinalIgnoreCase));
                if (g != null) foreach (var mm in g.Members) if (mm[0].Equals("Frame", StringComparison.OrdinalIgnoreCase) && byLabel.TryGetValue(mm[1], out one)) hit.Add(one);
                if (g == null) { bad.Add(tok + " (no such group)"); continue; }
            }
            else if (head == "line" || head == "kind")
            {
                var map = head == "line" ? lineOf : kindOf;
                foreach (var f in m.Frames) { string v; if (map != null && map.TryGetValue(f.Label, out v) && string.Equals(v, arg, StringComparison.OrdinalIgnoreCase)) hit.Add(f); }
            }
            else if (head == "section")
            {
                foreach (var f in m.Frames) if (string.Equals(f.Section, arg, StringComparison.OrdinalIgnoreCase)) hit.Add(f);
            }
            else if (colon > 0 && order != null)
            {
                int a = order.IndexOf(tok.Substring(0, colon)), b = order.IndexOf(arg);
                if (a < 0 || b < 0) { bad.Add(tok + " (" + (a < 0 ? tok.Substring(0, colon) : arg) + " is on no Frames row)"); continue; }
                for (int k = Math.Min(a, b); k <= Math.Max(a, b); k++) if (byLabel.TryGetValue(order[k], out one)) hit.Add(one);
            }
            else { bad.Add(tok + " (no such frame)"); continue; }
            if (hit.Count == before) bad.Add(tok + " (no frames)");
        }
        if (bad.Count > 0) err = string.Join(", ", bad.ToArray());
        return m.Frames.Where(hit.Contains).ToList();
    }
}
