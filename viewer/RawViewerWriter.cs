using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;

// RawViewerWriter
//
// Writes the v3 per-corner viewer binary. The corner field block is
//   float32[nFieldLC][nElements][maxCorners=4][cornerComponents]
// Every element reserves 4 corner slots; triangles NaN-pad the unused slot.
// There is NO separate center slot in this format -- corners only.
//
// Workflow: BuildComponents(stress, displacement, strength, dsr) assembles
// the ONE component list the constructor takes; kind == "str" entries are
// split out into the LC-independent design-strength block, everything else
// forms the per-LC corner layout.
//
// Two-phase use:
//   1. Write()              -- lays down header/geometry/IDs/meta and fills
//                              the ENTIRE corner block (and the design-
//                              strength block, if any) with NaN.
//   2. AppendStresses(...)  -- seeks to each record's single destination.
//      AppendDisplacements  -- fans one per-node record out to every element
//                              corner that touches that node.
//      AppendStr            -- like AppendStresses/AppendDsr, but into the
//                              LC-independent design-strength block.
//
// Design strengths (phi factors applied -- NOT capacities, which
// correspond to true failure and are not computed here): optional
// per-corner components that do NOT vary with load case, stored ONCE as float32[nElements][maxCorners][nStr] in a block
// declared purely via the metadata JSON (meta.strengths = { offset,
// components }). No header change, no version bump; older viewers ignore
// the metadata key. The block sits between the element-ID table and the
// metadata so (a) its offset is known before the metadata that references
// it is built, and (b) it lives before cornerFieldOffset, keeping the
// reader's append-mode LC-count-from-file-size derivation valid.
//
// All layout/index state is built once in the constructor and reused.

public class RawViewerWriter
{
    // ---- record / component shapes -------------------------------------

    // Component: one scalar field in a corner. Kind is "stress" | "displacement" | "dsr".
    // Unit is an optional display string (e.g. "psi", "in", "rad"); the viewer
    // shows it in the legend, readout and worst-case table. Null omits it.
    public class Component
    {
        public string Name;
        public string Kind;
        public string Unit;

        public Component(string name, string kind)
        {
            Name = name;
            Kind = kind;
            Unit = null;
        }

        public Component(string name, string kind, string unit)
        {
            Name = name;
            Kind = kind;
            Unit = unit;
        }
    }

    // ---- name catalogs / component factory (placeholders -- fill in) ----
    // The intended workflow: call BuildComponents(...) with the four
    // section flags to assemble the ONE component list the constructor
    // takes. Everything downstream is dynamic: names come from these
    // catalogs (and enums defined elsewhere), units from nameUnits().

    public static string[] StressNames = { /* TODO */ };
    public static string[] DispNames = { /* TODO */ };

    // Display unit string for a component name (shown by the viewer in
    // the legend/readout), or null for unitless.
    public static string nameUnits(string name)
    {
        // TODO
        return null;
    }

    // Assemble the full component list from the section flags. Entries
    // with Kind == "str" become the LC-independent design-strength block;
    // everything else is a per-LC corner component, in list order.
    public static List<Component> BuildComponents(bool stress, bool displacement, bool strength, bool dsr)
    {
        // TODO
        throw new NotImplementedException();
    }

    // Layout derived once from the component list. Assumes each kind occupies
    // a single contiguous run; the constructor fails fast otherwise.
    public class ComponentLayout
    {
        public int CornerComponents;
        private Dictionary<string, int> kindStart;
        private Dictionary<string, int> kindCount;
        private Dictionary<string, int> nameSlot;

        public ComponentLayout(List<Component> components)
        {
            if (components == null || components.Count == 0)
            {
                throw new Exception("ComponentLayout: components list is empty.");
            }

            CornerComponents = components.Count;
            kindStart = new Dictionary<string, int>();
            kindCount = new Dictionary<string, int>();
            nameSlot = new Dictionary<string, int>();

            // first/last index seen per kind, to validate contiguity
            Dictionary<string, int> firstSeen = new Dictionary<string, int>();
            Dictionary<string, int> lastSeen = new Dictionary<string, int>();

            for (int i = 0; i < components.Count; i++)
            {
                Component c = components[i];
                if (c == null || c.Name == null || c.Kind == null)
                {
                    throw new Exception(string.Format("ComponentLayout: null component or field at index {0}.", i));
                }
                if (nameSlot.ContainsKey(c.Name))
                {
                    throw new Exception(string.Format("ComponentLayout: duplicate component name '{0}'.", c.Name));
                }
                nameSlot[c.Name] = i;

                if (!firstSeen.ContainsKey(c.Kind))
                {
                    firstSeen[c.Kind] = i;
                    kindCount[c.Kind] = 0;
                }
                lastSeen[c.Kind] = i;
                kindCount[c.Kind] = kindCount[c.Kind] + 1;
            }

            // contiguity check: span (last-first+1) must equal count for each kind
            foreach (KeyValuePair<string, int> kv in firstSeen)
            {
                string kind = kv.Key;
                int span = lastSeen[kind] - firstSeen[kind] + 1;
                if (span != kindCount[kind])
                {
                    throw new Exception(string.Format(
                        "ComponentLayout: kind '{0}' is not contiguous (slots {1}..{2} but {3} fields). " +
                        "Append offset math requires contiguous kinds.",
                        kind, firstSeen[kind], lastSeen[kind], kindCount[kind]));
                }
                kindStart[kind] = firstSeen[kind];
            }
        }

        public int Start(string kind)
        {
            int s;
            if (!kindStart.TryGetValue(kind, out s))
            {
                throw new Exception(string.Format("ComponentLayout: no fields of kind '{0}'.", kind));
            }
            return s;
        }

        public int Count(string kind)
        {
            int c;
            if (!kindCount.TryGetValue(kind, out c)) return 0;
            return c;
        }

        public bool HasKind(string kind)
        {
            return kindStart.ContainsKey(kind);
        }
    }

    // One element corner that a given node maps into.
    private struct JointTarget
    {
        public int ElemIndex;   // index into the element-order array
        public int CornerSlot;  // 0-based corner slot (node position in element.n)
    }

    // StrRecord (defined elsewhere): one design-strength record per element
    // corner, no load-case dimension -- { elemID, node, Values[] }. Values[]
    // order matches the kind == "str" components in the order they appear
    // in the constructor's single component list.

    // ---- built-once instance state -------------------------------------

    private readonly string path;
    private readonly ComponentLayout layout;
    private readonly int cornerComponents;
    private readonly int maxCorners;        // 4
    private readonly int nFieldLC;

    private readonly Element[] elemOrder;          // element write order
    private readonly Dictionary<int, int> elemIdToIndex;
    private readonly Dictionary<int, List<JointTarget>> nodeToTargets;

    // STAAD load-case id -> 0-based field-LC plane index.
    // Plane order is ascending STAAD id; the metadata load-case array uses the
    // same order so JSON index N == plane N.
    private readonly int[] fieldLcIds;             // plane index -> STAAD id
    private readonly Dictionary<int, int> lcIdToPlane;
    private readonly Dictionary<int, string> lcNamesById;

    // byte offset of corner field block start (= header + geometry + ids + strengths + meta)
    private readonly long cornerFieldOffset;
    // stride helpers (in floats)
    private readonly int strideCorner;   // cornerComponents
    private readonly int strideElem;     // maxCorners * cornerComponents
    private readonly long strideLC;      // (long)nElements * strideElem

    // optional LC-independent design-strength block
    private readonly List<Component> strengthComponents;   // null -> no block
    private readonly int nStr;                              // 0 -> no block
    private readonly long strengthOffset;                   // 0 when absent
    private readonly int strideElemStr;                     // maxCorners * nStr

    private const int FLOAT_SIZE = 4;
    private const int MAGIC = 0x46454156;
    private const int VERSION = 3;
    private const int HEADER_SIZE = 60;
    private const int MAX_CORNERS = 4;

    // header/geometry blobs computed once so Write() and the append seek math agree
    private readonly byte[] metaBytes;
    private readonly long metaOffset;
    private readonly long nodesOffset;
    private readonly long elemsOffset;
    private readonly long nodeIdOffset;
    private readonly long elemIdOffset;
    private readonly Node[] nodeOrder;
    private readonly int nNodes;
    private readonly int nElements;

    // ---- construction ---------------------------------------------------

    // components: the FULL component list (e.g. from BuildComponents).
    // Entries with Kind == "str" are split out into the LC-independent
    // design-strength block (their list order is the Values[] order for
    // AppendStr); everything else forms the per-LC corner layout, in
    // list order.
    public RawViewerWriter(
        string path,
        Dictionary<int, Node> nodes,
        Dictionary<int, Element> elements,
        Dictionary<int, string> loadCaseNames,
        List<Component> components)
    {
        if (path == null) throw new Exception("RawViewerWriter: path is null.");
        if (nodes == null || nodes.Count == 0) throw new Exception("RawViewerWriter: no nodes.");
        if (elements == null || elements.Count == 0) throw new Exception("RawViewerWriter: no elements.");
        if (components == null || components.Count == 0)
        {
            throw new Exception("RawViewerWriter: components list is empty.");
        }

        this.path = path;

        // split the single list: kind "str" -> design-strength block,
        // everything else -> per-LC corner layout
        List<Component> cornerComps = new List<Component>();
        List<Component> strComps = new List<Component>();
        for (int ci = 0; ci < components.Count; ci++)
        {
            Component comp = components[ci];
            if (comp != null && comp.Kind == "str") strComps.Add(comp);
            else cornerComps.Add(comp);
        }

        layout = new ComponentLayout(cornerComps);
        cornerComponents = layout.CornerComponents;
        maxCorners = MAX_CORNERS;

        // fix a deterministic node/element write order (ascending id).
        nodeOrder = nodes.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToArray();
        elemOrder = elements.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToArray();
        nNodes = nodeOrder.Length;
        nElements = elemOrder.Length;

        elemIdToIndex = BuildElemIndex(elemOrder);
        nodeToTargets = BuildNodeTargets(elemOrder);

        // load-case plane mapping: ascending STAAD id -> plane index.
        // Envelopes are viewer-side only and never written here, so the file
        // holds exactly one plane per primary case. nFieldLC follows from that.
        if (loadCaseNames == null || loadCaseNames.Count == 0)
        {
            throw new Exception("RawViewerWriter: loadCaseNames is empty.");
        }
        fieldLcIds = loadCaseNames.Keys.OrderBy(id => id).ToArray();
        nFieldLC = fieldLcIds.Length;
        lcIdToPlane = new Dictionary<int, int>();
        for (int i = 0; i < fieldLcIds.Length; i++)
        {
            lcIdToPlane[fieldLcIds[i]] = i;
        }

        // strides (in floats)
        strideCorner = cornerComponents;
        strideElem = maxCorners * cornerComponents;
        strideLC = (long)nElements * strideElem;

        lcNamesById = loadCaseNames;

        // design-strength block shape (validated like the main component list)
        strengthComponents = strComps.Count > 0 ? strComps : null;
        nStr = strengthComponents == null ? 0 : strengthComponents.Count;
        strideElemStr = maxCorners * nStr;
        if (strengthComponents != null)
        {
            Dictionary<string, bool> seen = new Dictionary<string, bool>();
            for (int i = 0; i < strengthComponents.Count; i++)
            {
                Component c = strengthComponents[i];
                if (c.Name == null)
                {
                    throw new Exception(string.Format(
                        "RawViewerWriter: null design-strength component name at index {0}.", i));
                }
                if (seen.ContainsKey(c.Name))
                {
                    throw new Exception(string.Format(
                        "RawViewerWriter: duplicate design-strength component name '{0}'.", c.Name));
                }
                seen[c.Name] = true;
            }
        }

        // ---- block offsets (must match the v3 header table exactly) ----
        // The design-strength block precedes the metadata so BuildMetadata can
        // embed its (already known) offset.
        long off = HEADER_SIZE;
        nodesOffset = off; off += (long)nNodes * 3 * FLOAT_SIZE;
        elemsOffset = off; off += (long)nElements * 5 * FLOAT_SIZE; // uint32[5] = ncount,id1..4
        nodeIdOffset = off; off += (long)nNodes * FLOAT_SIZE;        // uint32
        elemIdOffset = off; off += (long)nElements * FLOAT_SIZE;     // uint32
        strengthOffset = nStr > 0 ? off : 0;
        off += (long)nElements * strideElemStr * FLOAT_SIZE;         // 0 when no strengths

        metaBytes = BuildMetadata(cornerComps);
        metaOffset = off; off += metaBytes.Length;
        cornerFieldOffset = off;
    }

    private static Dictionary<int, int> BuildElemIndex(Element[] elems)
    {
        Dictionary<int, int> map = new Dictionary<int, int>();
        for (int e = 0; e < elems.Length; e++)
        {
            int id = elems[e].id;
            if (map.ContainsKey(id))
            {
                throw new Exception(string.Format("RawViewerWriter: duplicate element id {0}.", id));
            }
            map[id] = e;
        }
        return map;
    }

    // node id -> every (element index, corner slot) that touches it
    private static Dictionary<int, List<JointTarget>> BuildNodeTargets(Element[] elems)
    {
        Dictionary<int, List<JointTarget>> map = new Dictionary<int, List<JointTarget>>();
        for (int e = 0; e < elems.Length; e++)
        {
            Element el = elems[e];
            int nc = el.nNodes;
            for (int s = 0; s < nc; s++)
            {
                int nid = el.n[s].id;
                List<JointTarget> list;
                if (!map.TryGetValue(nid, out list))
                {
                    list = new List<JointTarget>();
                    map[nid] = list;
                }
                JointTarget t;
                t.ElemIndex = e;
                t.CornerSlot = s;
                list.Add(t);
            }
        }
        return map;
    }

    // ---- phase 1: write everything, NaN-fill the whole corner block -----

    public void Write()
    {
        using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write))
        using (BinaryWriter bw = new BinaryWriter(fs))
        {
            // ---- header: 15 little-endian uint32 (60 bytes) ----
            bw.Write((uint)MAGIC);
            bw.Write((uint)VERSION);
            bw.Write((uint)HEADER_SIZE);
            bw.Write((uint)nNodes);
            bw.Write((uint)nElements);
            bw.Write((uint)nFieldLC);
            bw.Write((uint)cornerComponents);
            bw.Write((uint)maxCorners);
            bw.Write((uint)metaOffset);
            bw.Write((uint)metaBytes.Length);
            bw.Write((uint)nodesOffset);
            bw.Write((uint)elemsOffset);
            bw.Write((uint)nodeIdOffset);
            bw.Write((uint)elemIdOffset);
            bw.Write((uint)cornerFieldOffset);

            // ---- nodes: float32[nNodes][3] x,y,z ----
            for (int i = 0; i < nodeOrder.Length; i++)
            {
                Node n = nodeOrder[i];
                bw.Write((float)n.xyz.X);
                bw.Write((float)n.xyz.Y);
                bw.Write((float)n.xyz.Z);
            }

            // ---- elements: uint32[5] ncount, id1..id4 (node indices, pad slot 4 with 0) ----
            Dictionary<int, int> nodeIdToIndex = new Dictionary<int, int>();
            for (int i = 0; i < nodeOrder.Length; i++)
            {
                nodeIdToIndex[nodeOrder[i].id] = i;
            }
            for (int e = 0; e < elemOrder.Length; e++)
            {
                Element el = elemOrder[e];
                bw.Write((uint)el.nNodes);
                for (int s = 0; s < MAX_CORNERS; s++)
                {
                    if (s < el.nNodes)
                    {
                        bw.Write((uint)nodeIdToIndex[el.n[s].id]);
                    }
                    else
                    {
                        bw.Write((uint)0);
                    }
                }
            }

            // ---- real (sparse) node IDs: uint32[nNodes] ----
            for (int i = 0; i < nodeOrder.Length; i++)
            {
                bw.Write((uint)nodeOrder[i].id);
            }

            // ---- real (sparse) element IDs: uint32[nElements] ----
            for (int e = 0; e < elemOrder.Length; e++)
            {
                bw.Write((uint)elemOrder[e].id);
            }

            // ---- design-strength block (if any): nElements * maxCorners * nStr, all NaN ----
            if (nStr > 0)
            {
                WriteNaNFloats(bw, (long)nElements * strideElemStr);
            }

            // ---- metadata JSON blob ----
            bw.Write(metaBytes);

            // ---- corner field block: nFieldLC * nElements * maxCorners * cornerComponents, all NaN ----
            WriteNaNFloats(bw, (long)nFieldLC * strideLC);
        }
    }

    // Chunked NaN fill to avoid a giant allocation.
    private static void WriteNaNFloats(BinaryWriter bw, long totalFloats)
    {
        const int CHUNK = 8192;
        byte[] nanBuf = new byte[CHUNK * FLOAT_SIZE];
        byte[] oneNaN = BitConverter.GetBytes(float.NaN);
        for (int i = 0; i < CHUNK; i++)
        {
            Buffer.BlockCopy(oneNaN, 0, nanBuf, i * FLOAT_SIZE, FLOAT_SIZE);
        }
        long remaining = totalFloats;
        while (remaining > 0)
        {
            int n = (int)Math.Min((long)CHUNK, remaining);
            bw.Write(nanBuf, 0, n * FLOAT_SIZE);
            remaining -= n;
        }
    }

    // ---- phase 2: appends ------------------------------------------------

    // Per-element-corner: each record has exactly one destination.
    public void AppendStresses(List<StressRecord> stresses)
    {
        if (stresses == null) return;
        int start = layout.Start("stress");
        int count = layout.Count("stress");

        using (MemoryMappedFile mmf = MemoryMappedFile.CreateFromFile(
            path, FileMode.Open, null, 0, MemoryMappedFileAccess.ReadWrite))
        using (MemoryMappedViewAccessor acc = mmf.CreateViewAccessor(
            0, 0, MemoryMappedFileAccess.ReadWrite))
        {
            for (int i = 0; i < stresses.Count; i++)
            {
                StressRecord r = stresses[i];
                int plane = ResolveLcPlane(r.LC, "stress");

                int elemIndex;
                if (!elemIdToIndex.TryGetValue(r.elemID, out elemIndex))
                {
                    throw new Exception(string.Format(
                        "AppendStresses: element id {0} not in model.", r.elemID));
                }
                int slot = CornerSlotForNode(elemIndex, r.node);
                if (slot < 0)
                {
                    // Not a corner of this element (e.g. element-center results are
                    // commonly mixed into the stress list) -- silently skip.
                    continue;
                }
                if (r.Sf == null || r.Sf.Length < count)
                {
                    throw new Exception(string.Format(
                        "AppendStresses: record for elem {0} node {1} has {2} values, expected {3}.",
                        r.elemID, r.node, (r.Sf == null ? 0 : r.Sf.Length), count));
                }

                long floatPos = FieldPos(plane, elemIndex, slot) + start;
                long bytePos = cornerFieldOffset + floatPos * FLOAT_SIZE;
                float[] sf_kipft_forceView = r.StressViewForce(ForceUnit.Kip, LengthUnit.Foot);
                acc.WriteArray(bytePos, sf_kipft_forceView, 0, count);
            }
        }
    }

    // Per-node: fan one record out to every element corner touching that node.
    public void AppendDisplacements(List<Disp> disps)
    {
        if (disps == null) return;
        int start = layout.Start("displacement");
        int count = layout.Count("displacement");

        using (MemoryMappedFile mmf = MemoryMappedFile.CreateFromFile(
            path, FileMode.Open, null, 0, MemoryMappedFileAccess.ReadWrite))
        using (MemoryMappedViewAccessor acc = mmf.CreateViewAccessor(
            0, 0, MemoryMappedFileAccess.ReadWrite))
        {
            for (int i = 0; i < disps.Count; i++)
            {
                Disp d = disps[i];
                int plane = ResolveLcPlane(d.LC, "displacement");

                List<JointTarget> targets;
                if (!nodeToTargets.TryGetValue(d.node, out targets))
                {
                    // node in no element -> stays NaN, consistent with Write()
                    continue;
                }
                if (d.DR == null || d.DR.Length < count)
                {
                    throw new Exception(string.Format(
                        "AppendDisplacements: record for node {0} has {1} values, expected {2}.",
                        d.node, (d.DR == null ? 0 : d.DR.Length), count));
                }

                for (int t = 0; t < targets.Count; t++)
                {
                    JointTarget jt = targets[t];
                    long floatPos = FieldPos(plane, jt.ElemIndex, jt.CornerSlot) + start;
                    long bytePos = cornerFieldOffset + floatPos * FLOAT_SIZE;
                    acc.WriteArray(bytePos, d.DR, 0, count);
                }
            }
        }
    }

    // Per-element-corner, same shape as AppendStresses: each record has exactly
    // one destination. DSR fields occupy the "dsr" run of the corner layout.
    public void AppendDsr(List<DsrRecord> dsrs)
    {
        if (dsrs == null) return;
        // guard: writer was built with a layout that actually reserves dsr slots
        if (!layout.HasKind("dsr"))
        {
            throw new Exception("AppendDsr: component layout has no 'dsr' fields.");
        }
        int start = layout.Start("dsr");
        int count = layout.Count("dsr");

        using (MemoryMappedFile mmf = MemoryMappedFile.CreateFromFile(
            path, FileMode.Open, null, 0, MemoryMappedFileAccess.ReadWrite))
        using (MemoryMappedViewAccessor acc = mmf.CreateViewAccessor(
            0, 0, MemoryMappedFileAccess.ReadWrite))
        {
            for (int i = 0; i < dsrs.Count; i++)
            {
                DsrRecord r = dsrs[i];
                int plane = ResolveLcPlane(r.LC, "dsr");

                int elemIndex;
                if (!elemIdToIndex.TryGetValue(r.elemID, out elemIndex))
                {
                    throw new Exception(string.Format(
                        "AppendDsr: element id {0} not in model.", r.elemID));
                }
                int slot = CornerSlotForNode(elemIndex, r.node);
                if (slot < 0)
                {
                    // Not a corner of this element (e.g. element-center results are
                    // commonly mixed into the dsr list) -- silently skip.
                    continue;
                }
                if (r.Values == null || r.Values.Length < count)
                {
                    throw new Exception(string.Format(
                        "AppendDsr: record for elem {0} node {1} has {2} values, expected {3}.",
                        r.elemID, r.node, (r.Values == null ? 0 : r.Values.Length), count));
                }

                long floatPos = FieldPos(plane, elemIndex, slot) + start;
                long bytePos = cornerFieldOffset + floatPos * FLOAT_SIZE;
                acc.WriteArray(bytePos, r.Values, 0, count);
            }
        }
    }

    // Per-element-corner, same shape as AppendStresses/AppendDsr but into
    // the LC-independent design-strength block: no load-case dimension, each
    // record has exactly one destination.
    public void AppendStr(List<StrRecord> strs)
    {
        if (strs == null) return;
        if (nStr == 0)
        {
            throw new Exception("AppendStr: writer was built without design-strength components.");
        }

        using (MemoryMappedFile mmf = MemoryMappedFile.CreateFromFile(
            path, FileMode.Open, null, 0, MemoryMappedFileAccess.ReadWrite))
        using (MemoryMappedViewAccessor acc = mmf.CreateViewAccessor(
            0, 0, MemoryMappedFileAccess.ReadWrite))
        {
            for (int i = 0; i < strs.Count; i++)
            {
                StrRecord r = strs[i];

                int elemIndex;
                if (!elemIdToIndex.TryGetValue(r.elemID, out elemIndex))
                {
                    throw new Exception(string.Format(
                        "AppendStr: element id {0} not in model.", r.elemID));
                }
                int slot = CornerSlotForNode(elemIndex, r.node);
                if (slot < 0)
                {
                    // Not a corner of this element (e.g. element-center results
                    // mixed into the list) -- silently skip, like the other appends.
                    continue;
                }
                if (r.Values == null || r.Values.Length < nStr)
                {
                    throw new Exception(string.Format(
                        "AppendStr: record for elem {0} node {1} has {2} values, expected {3}.",
                        r.elemID, r.node, (r.Values == null ? 0 : r.Values.Length), nStr));
                }

                long floatPos = (long)elemIndex * strideElemStr + (long)slot * nStr;
                long bytePos = strengthOffset + floatPos * FLOAT_SIZE;
                acc.WriteArray(bytePos, r.Values, 0, nStr);
            }
        }
    }

    // ---- offset / io helpers --------------------------------------------

    // float-index of corner slot 0 for (lc, elemIndex, cornerSlot)
    private long FieldPos(int lc, int elemIndex, int cornerSlot)
    {
        return (long)lc * strideLC
             + (long)elemIndex * strideElem
             + (long)cornerSlot * strideCorner;
    }

    private int CornerSlotForNode(int elemIndex, int nodeId)
    {
        Element el = elemOrder[elemIndex];
        for (int s = 0; s < el.nNodes; s++)
        {
            if (el.n[s].id == nodeId) return s;
        }
        return -1;
    }

    private int ResolveLcPlane(int staadLc, string what)
    {
        int plane;
        if (!lcIdToPlane.TryGetValue(staadLc, out plane))
        {
            throw new Exception(string.Format(
                "Append{0}: STAAD load-case id {1} is not a known field LC.", what, staadLc));
        }
        return plane;
    }

    // ---- metadata --------------------------------------------------------

    // { loadCases:[{name,type}], components:[{name,kind,unit?}],
    //   displacementVector:[ix,iy,iz]?, strengths:{offset,components}? }
    // `components` here is the CORNER subset (kind != "str") -- the "str"
    // entries were split into strengthComponents by the constructor and
    // are emitted under the separate strengths key.
    // load-case array order == plane order (fieldLcIds), so JSON index N is plane N.
    // displacementVector tags which components hold translation X/Y/Z for the
    // viewer's deformed-shape mode: the first three of the "displacement" run
    // (AppendDisplacements writes STAAD DR records, whose order is tx,ty,tz,
    // rx,ry,rz). Omitted when fewer than 3 displacement components exist.
    private byte[] BuildMetadata(List<Component> components)
    {
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        sb.Append("{\"loadCases\":[");

        for (int i = 0; i < fieldLcIds.Length; i++)
        {
            if (i > 0) sb.Append(",");
            int id = fieldLcIds[i];
            string name = lcNamesById[id];
            // envelopes are viewer-side only; every case written here is primary.
            sb.Append("{\"name\":");
            AppendJsonString(sb, name);
            sb.Append(",\"type\":\"primary\"}");
        }

        sb.Append("],\"components\":[");
        for (int i = 0; i < components.Count; i++)
        {
            if (i > 0) sb.Append(",");
            sb.Append("{\"name\":");
            AppendJsonString(sb, components[i].Name);
            sb.Append(",\"kind\":\"");
            sb.Append(components[i].Kind);
            sb.Append("\"");
            if (components[i].Unit != null)
            {
                sb.Append(",\"unit\":");
                AppendJsonString(sb, components[i].Unit);
            }
            sb.Append("}");
        }
        sb.Append("]");

        if (layout.HasKind("displacement") && layout.Count("displacement") >= 3)
        {
            int d0 = layout.Start("displacement");
            sb.Append(",\"displacementVector\":[");
            sb.Append(d0);
            sb.Append(",");
            sb.Append(d0 + 1);
            sb.Append(",");
            sb.Append(d0 + 2);
            sb.Append("]");
        }

        // LC-independent design-strength block: declared here (metadata only, no
        // header field) so the format stays v3 and older viewers just
        // ignore the key.
        if (nStr > 0)
        {
            sb.Append(",\"strengths\":{\"offset\":");
            sb.Append(strengthOffset);
            sb.Append(",\"components\":[");
            for (int i = 0; i < strengthComponents.Count; i++)
            {
                if (i > 0) sb.Append(",");
                sb.Append("{\"name\":");
                AppendJsonString(sb, strengthComponents[i].Name);
                if (strengthComponents[i].Unit != null)
                {
                    sb.Append(",\"unit\":");
                    AppendJsonString(sb, strengthComponents[i].Unit);
                }
                sb.Append("}");
            }
            sb.Append("]}");
        }

        sb.Append("}");

        return new System.Text.UTF8Encoding(false).GetBytes(sb.ToString());
    }

    private static void AppendJsonString(System.Text.StringBuilder sb, string s)
    {
        sb.Append("\"");
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '"' || c == '\\')
            {
                sb.Append('\\');
                sb.Append(c);
            }
            else if (c == '\n') sb.Append("\\n");
            else if (c == '\r') sb.Append("\\r");
            else if (c == '\t') sb.Append("\\t");
            else sb.Append(c);
        }
        sb.Append("\"");
    }
}