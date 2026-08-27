using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

// RawViewerWriter  --  Pluto v4 binary writer.
// Spec: vault/format/v4-schema.md (block directory + element domains).
// C# 5 / Add-Type (PowerShell 5.1) compatible -- keep it that way.
//
// Two-phase use, unchanged from the v3 writer the scripts already call:
//   1. Write()               -- lays down header, directory, geometry, IDs,
//                               sections, META, and NaN-fills EVERY field
//                               block (shell FLDS, shell FLDC, beam FLDS).
//   2. AppendStresses(...)   -- seeks to each record's single destination.
//      AppendDisplacements   -- fans one per-node record out to every shell
//                               corner AND every beam end touching the node.
//      AppendDsr / AppendStr -- shell DSR run / LC-independent strengths.
//      AppendBeamForces      -- beam end records into the beam FLDS.
//
// Domains: "shells" (when elements are given) then "beams" (when beams are
// given), in that order, so a beam-only export has beams as domain 0.
// Element IDs are per domain. Nodes are shared and written as float64.
// loadCaseNames may be empty/null -> no FLDS blocks (geometry-only).
//
// File layout (all blocks 8-byte aligned):
//   header(32) | directory | NODE | NDID | [LABL nodes] | ELEM d0 | ELID d0 | [LABL d0]
//   | [FLDC d0] | [SECT | ELEM d1 | ELID d1 | BPRP d1 | [LABL d1]] | META | FLDS d0 | [FLDS d1]
//
// Labels (optional): ONE identity string per node / per element (e.g. the
// source system's oid), shown by the viewer in the hover readout only.
// Set them with SetNodeLabels / SetShellLabels / SetBeamLabels BEFORE Write().
// Categories (class, run, room, ...) are NOT labels -- they are sidecar groups.
// The directory is fixed-size and written in Write() with final counts,
// so no APPEND flag is needed: every plane exists (as NaN) from the start
// and Append* writes in place. That is exactly the v3 strategy.
//
// geometryHash (META): SHA-256 over NODE, NDID, ELEM(d0..), ELID(d0..),
// SECT, BPRP(d0..) bytes in that order -- identical to the viewer's and
// FeaturesSidecar.ComputeGeometryHash, so a features file binds to this
// output. Field blocks are excluded, so a geometry-only export (no
// components at all is NOT allowed here -- pass writeFields:false to
// Write() instead) hashes the same as the full file.
//
// Components: BuildComponents(stress, displacement, strength, dsr) still
// assembles the ONE shell component list; kind == "str" entries split out
// into FLDC (constComponents), everything else forms the per-LC layout.

public class RawViewerWriter
{
    // ---- record / component shapes -------------------------------------

    public class Component
    {
        public string Name;
        public string Kind;      // "stress" | "displacement" | "dsr" | "str" | "force" | anything
        public string Unit;

        public Component(string name, string kind) { Name = name; Kind = kind; Unit = null; }
        public Component(string name, string kind, string unit) { Name = name; Kind = kind; Unit = unit; }
    }

    // Beam member (domain 1). Node ids are REAL ids (same table as shells).
    // LocalY: unit vector of the section's local y axis in world coordinates
    // (resolve roll angles / K-nodes before handing it over). Offsets in
    // section-local (y, z) at each end. ReleaseMask is a display hint.
    public class BeamMember
    {
        public int Id;
        public int NodeA;
        public int NodeB;
        public int SectionIndex;       // index into the sections list
        public double[] LocalY = { 0, 0, 1 };
        public double OffsetAy, OffsetAz, OffsetBy, OffsetBz;
        public uint ReleaseMask;
    }

    // Parametric cross-section (schema §6). Params in the section-local
    // (y, z) frame, file length units. For POLY, Points is the outline.
    public class SectionDef
    {
        public const uint RECT = 1, I = 2, BOX = 3, PIPE = 4, L = 5, C = 6, T = 7, POLY = 100;
        public string Name;
        public uint TypeCode;
        public float[] Params = new float[0];
        public float[][] Points;       // POLY only: [n][2]

        public static SectionDef Rect(string name, float b, float h) { return new SectionDef { Name = name, TypeCode = RECT, Params = new[] { b, h } }; }
        public static SectionDef IShape(string name, float d, float bfTop, float tfTop, float bfBot, float tfBot, float tw) { return new SectionDef { Name = name, TypeCode = I, Params = new[] { d, bfTop, tfTop, bfBot, tfBot, tw } }; }
        public static SectionDef Box(string name, float b, float h, float t) { return new SectionDef { Name = name, TypeCode = BOX, Params = new[] { b, h, t } }; }
        public static SectionDef Pipe(string name, float od, float t) { return new SectionDef { Name = name, TypeCode = PIPE, Params = new[] { od, t } }; }
        public static SectionDef Angle(string name, float b, float h, float t) { return new SectionDef { Name = name, TypeCode = L, Params = new[] { b, h, t } }; }
        public static SectionDef Channel(string name, float d, float bf, float tf, float tw) { return new SectionDef { Name = name, TypeCode = C, Params = new[] { d, bf, tf, tw } }; }
        public static SectionDef Tee(string name, float d, float bf, float tf, float tw) { return new SectionDef { Name = name, TypeCode = T, Params = new[] { d, bf, tf, tw } }; }
        public static SectionDef Poly(string name, float[][] pts) { return new SectionDef { Name = name, TypeCode = POLY, Points = pts }; }
    }

    // One beam-end result record: Values[] in the beam component order
    // (the whole list, or the run of one kind -- see AppendBeamForces).
    public class BeamRecord
    {
        public int LC;
        public int elemID;
        public int End;            // 0 = end A (NodeA), 1 = end B (NodeB)
        public float[] Values;
    }

    // ---- name catalogs / component factory (placeholders -- fill in) ----
    public static string[] StressNames = { /* TODO */ };
    public static string[] DispNames = { /* TODO */ };

    public static string nameUnits(string name)
    {
        // TODO
        return null;
    }

    public static List<Component> BuildComponents(bool stress, bool displacement, bool strength, bool dsr)
    {
        // TODO
        throw new NotImplementedException();
    }

    // Layout derived once from a component list. Each kind must occupy a
    // single contiguous run (append offset math relies on it).
    public class ComponentLayout
    {
        public int CornerComponents;
        private Dictionary<string, int> kindStart;
        private Dictionary<string, int> kindCount;
        private Dictionary<string, int> nameSlot;

        public ComponentLayout(List<Component> components)
        {
            if (components == null || components.Count == 0)
                throw new Exception("ComponentLayout: components list is empty.");

            CornerComponents = components.Count;
            kindStart = new Dictionary<string, int>();
            kindCount = new Dictionary<string, int>();
            nameSlot = new Dictionary<string, int>();
            Dictionary<string, int> firstSeen = new Dictionary<string, int>();
            Dictionary<string, int> lastSeen = new Dictionary<string, int>();

            for (int i = 0; i < components.Count; i++)
            {
                Component c = components[i];
                if (c == null || c.Name == null || c.Kind == null)
                    throw new Exception(string.Format("ComponentLayout: null component or field at index {0}.", i));
                if (nameSlot.ContainsKey(c.Name))
                    throw new Exception(string.Format("ComponentLayout: duplicate component name '{0}'.", c.Name));
                nameSlot[c.Name] = i;
                if (!firstSeen.ContainsKey(c.Kind)) { firstSeen[c.Kind] = i; kindCount[c.Kind] = 0; }
                lastSeen[c.Kind] = i;
                kindCount[c.Kind] = kindCount[c.Kind] + 1;
            }
            foreach (KeyValuePair<string, int> kv in firstSeen)
            {
                string kind = kv.Key;
                int span = lastSeen[kind] - firstSeen[kind] + 1;
                if (span != kindCount[kind])
                    throw new Exception(string.Format(
                        "ComponentLayout: kind '{0}' is not contiguous (slots {1}..{2} but {3} fields).",
                        kind, firstSeen[kind], lastSeen[kind], kindCount[kind]));
                kindStart[kind] = firstSeen[kind];
            }
        }

        public int Start(string kind)
        {
            int s;
            if (!kindStart.TryGetValue(kind, out s))
                throw new Exception(string.Format("ComponentLayout: no fields of kind '{0}'.", kind));
            return s;
        }
        public int Count(string kind) { int c; return kindCount.TryGetValue(kind, out c) ? c : 0; }
        public bool HasKind(string kind) { return kindStart.ContainsKey(kind); }
    }

    private struct JointTarget { public int ElemIndex; public int Slot; }

    // ---- format constants --------------------------------------------------
    private const int FLOAT_SIZE = 4;
    private const uint MAGIC = 0x46454156;
    private const uint VERSION = 4;
    private const int HEADER_SIZE = 32;
    private const int DIR_ENTRY_SIZE = 32;
    private const uint GLOBAL_DOMAIN = 0xFFFFFFFF;
    private const int MAX_CORNERS = 4;
    private const int BEAM_SLOTS = 2;          // stations: end A, end B
    private const int ELEM_RECORD_U32 = 6;
    private const int BPRP_F32 = 8;

    private class Block
    {
        public string Tag; public uint Domain; public long Offset; public long Length; public uint Count;
        public uint Flags = 0;                 // reserved (APPEND not used: planes are NaN-prefilled)
        public byte[] Bytes;                   // resident payload, or null for NaN-filled field blocks
    }

    // ---- built-once instance state -------------------------------------
    private readonly string path;

    // shells (domain 0)
    private readonly ComponentLayout layout;
    private readonly List<Component> cornerComps;
    private readonly int cornerComponents;
    private readonly Element[] elemOrder;
    private readonly Dictionary<int, int> elemIdToIndex;
    private readonly Dictionary<int, List<JointTarget>> nodeToTargets;
    private readonly int nElements;
    private readonly int strideCorner, strideElem;
    private readonly long strideLC;
    private readonly List<Component> strengthComponents;   // FLDC, or null
    private readonly int nStr, strideElemStr;

    // beams (domain 1), optional
    private readonly BeamMember[] beamOrder;               // null when absent
    private readonly Dictionary<int, int> beamIdToIndex;
    private readonly Dictionary<int, List<JointTarget>> nodeToBeamEnds;
    private readonly List<SectionDef> sections;
    private readonly ComponentLayout beamLayout;
    private readonly List<Component> beamComps;
    private readonly int nBeams, nBeamComps, strideBeamElem;
    private readonly uint shellDomain, beamDomain;   // directory domain indices (GLOBAL_DOMAIN = absent)
    private readonly long strideBeamLC;

    // shared
    private readonly Node[] nodeOrder;
    private readonly int nNodes;
    private readonly int[] fieldLcIds;
    private readonly Dictionary<int, int> lcIdToPlane;
    private readonly Dictionary<int, string> lcNamesById;
    private readonly int nFieldLC;
    private readonly string modelId;
    private readonly Dictionary<string, string> units;

    private Dictionary<int, string> nodeLabels, shellLabels, beamLabels;   // optional, by real id
    private List<Block> blocks;
    private long dirOffset;
    private long fieldOffsetShell, fieldOffsetBeam, strengthOffset;
    private long totalLength;
    private string geometryHash;

    // ---- construction ---------------------------------------------------

    // Shell-only writer: identical signature to the v3 writer.
    public RawViewerWriter(
        string path,
        Dictionary<int, Node> nodes,
        Dictionary<int, Element> elements,
        Dictionary<int, string> loadCaseNames,
        List<Component> components)
        : this(path, nodes, elements, loadCaseNames, components, null, null, null, null, null) { }

    // Shells + beams. beamComponents: per-end component list for the beam
    // domain (e.g. force N,Vy,Vz,T,My,Mz + displacement tx,ty,tz); kind
    // "displacement" entries are filled by AppendDisplacements as well.
    public RawViewerWriter(
        string path,
        Dictionary<int, Node> nodes,
        Dictionary<int, Element> elements,
        Dictionary<int, string> loadCaseNames,
        List<Component> components,
        Dictionary<int, BeamMember> beams,
        List<SectionDef> sectionDefs,
        List<Component> beamComponents,
        string modelId,
        Dictionary<string, string> units)
    {
        if (path == null) throw new Exception("RawViewerWriter: path is null.");
        if (nodes == null || nodes.Count == 0) throw new Exception("RawViewerWriter: no nodes.");
        bool hasShells = elements != null && elements.Count > 0;
        bool hasBeams = beams != null && beams.Count > 0;
        if (!hasShells && !hasBeams) throw new Exception("RawViewerWriter: no elements and no beams.");
        if (hasShells && (components == null || components.Count == 0)) throw new Exception("RawViewerWriter: components list is empty.");
        if (loadCaseNames == null) loadCaseNames = new Dictionary<int, string>();

        this.path = path;
        this.modelId = modelId;
        this.units = units;

        // split shell list: "str" -> FLDC, rest -> FLDS layout
        cornerComps = new List<Component>();
        List<Component> strComps = new List<Component>();
        if (hasShells)
        {
            foreach (Component comp in components)
            {
                if (comp != null && comp.Kind == "str") strComps.Add(comp); else cornerComps.Add(comp);
            }
            layout = new ComponentLayout(cornerComps);
            cornerComponents = layout.CornerComponents;
        }

        nodeOrder = nodes.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToArray();
        elemOrder = hasShells ? elements.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToArray() : new Element[0];
        nNodes = nodeOrder.Length;
        nElements = elemOrder.Length;
        elemIdToIndex = BuildElemIndex(elemOrder);
        nodeToTargets = BuildNodeTargets(elemOrder);

        fieldLcIds = loadCaseNames.Keys.OrderBy(id => id).ToArray();
        nFieldLC = fieldLcIds.Length;
        lcIdToPlane = new Dictionary<int, int>();
        for (int i = 0; i < fieldLcIds.Length; i++) lcIdToPlane[fieldLcIds[i]] = i;
        lcNamesById = loadCaseNames;

        strideCorner = cornerComponents;
        strideElem = MAX_CORNERS * cornerComponents;
        strideLC = (long)nElements * strideElem;

        strengthComponents = strComps.Count > 0 ? strComps : null;
        nStr = strengthComponents == null ? 0 : strengthComponents.Count;
        strideElemStr = MAX_CORNERS * nStr;
        if (strengthComponents != null)
        {
            HashSet<string> seen = new HashSet<string>();
            foreach (Component c in strengthComponents)
            {
                if (c.Name == null) throw new Exception("RawViewerWriter: null design-strength component name.");
                if (!seen.Add(c.Name)) throw new Exception("RawViewerWriter: duplicate design-strength component '" + c.Name + "'.");
            }
        }

        shellDomain = hasShells ? 0u : GLOBAL_DOMAIN;
        beamDomain = hasShells ? 1u : 0u;

        // beams
        if (hasBeams)
        {
            if (beamComponents == null || beamComponents.Count == 0)
                throw new Exception("RawViewerWriter: beams given but beamComponents is empty.");
            beamOrder = beams.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToArray();
            nBeams = beamOrder.Length;
            sections = sectionDefs ?? new List<SectionDef>();
            beamComps = beamComponents;
            beamLayout = new ComponentLayout(beamComps);
            nBeamComps = beamLayout.CornerComponents;
            beamIdToIndex = new Dictionary<int, int>();
            nodeToBeamEnds = new Dictionary<int, List<JointTarget>>();
            for (int b = 0; b < nBeams; b++)
            {
                BeamMember bm = beamOrder[b];
                if (beamIdToIndex.ContainsKey(bm.Id)) throw new Exception("RawViewerWriter: duplicate beam id " + bm.Id + ".");
                beamIdToIndex[bm.Id] = b;
                if (bm.SectionIndex < 0 || bm.SectionIndex >= sections.Count)
                    throw new Exception(string.Format("RawViewerWriter: beam {0} references section {1} of {2}.", bm.Id, bm.SectionIndex, sections.Count));
                AddTarget(nodeToBeamEnds, bm.NodeA, b, 0);
                AddTarget(nodeToBeamEnds, bm.NodeB, b, 1);
            }
            strideBeamElem = BEAM_SLOTS * nBeamComps;
            strideBeamLC = (long)nBeams * strideBeamElem;
        }

        Layout();
    }

    // Builds every resident block and assigns offsets. Re-run when labels are
    // attached after construction (labels never affect geometryHash).
    private void Layout()
    {
        // ---- resident blocks + layout ----
        Dictionary<int, int> nodeIdToIndex = new Dictionary<int, int>();
        for (int i = 0; i < nNodes; i++) nodeIdToIndex[nodeOrder[i].id] = i;

        blocks = new List<Block>();
        blocks.Add(new Block { Tag = "NODE", Domain = GLOBAL_DOMAIN, Count = (uint)nNodes, Bytes = BuildNodeBytes() });
        blocks.Add(new Block { Tag = "NDID", Domain = GLOBAL_DOMAIN, Count = (uint)nNodes, Bytes = BuildNodeIdBytes() });
        if (nodeLabels != null)
            blocks.Add(new Block { Tag = "LABL", Domain = GLOBAL_DOMAIN, Count = (uint)nNodes, Bytes = BuildLabelBytes(nodeOrder.Select(n => n.id).ToArray(), nodeLabels) });
        if (elemOrder.Length > 0)
        {
            blocks.Add(new Block { Tag = "ELEM", Domain = shellDomain, Count = (uint)nElements, Bytes = BuildShellElemBytes(nodeIdToIndex) });
            blocks.Add(new Block { Tag = "ELID", Domain = shellDomain, Count = (uint)nElements, Bytes = BuildShellElemIdBytes() });
            if (shellLabels != null)
                blocks.Add(new Block { Tag = "LABL", Domain = shellDomain, Count = (uint)nElements, Bytes = BuildLabelBytes(elemOrder.Select(e => e.id).ToArray(), shellLabels) });
            if (nStr > 0)
                blocks.Add(new Block { Tag = "FLDC", Domain = shellDomain, Count = 1, Length = (long)nElements * strideElemStr * FLOAT_SIZE });
        }
        if (beamOrder != null)
        {
            blocks.Add(new Block { Tag = "SECT", Domain = GLOBAL_DOMAIN, Count = (uint)sections.Count, Bytes = BuildSectBytes() });
            blocks.Add(new Block { Tag = "ELEM", Domain = beamDomain, Count = (uint)nBeams, Bytes = BuildBeamElemBytes(nodeIdToIndex) });
            blocks.Add(new Block { Tag = "ELID", Domain = beamDomain, Count = (uint)nBeams, Bytes = BuildBeamElemIdBytes() });
            blocks.Add(new Block { Tag = "BPRP", Domain = beamDomain, Count = (uint)nBeams, Bytes = BuildBprpBytes() });
            if (beamLabels != null)
                blocks.Add(new Block { Tag = "LABL", Domain = beamDomain, Count = (uint)nBeams, Bytes = BuildLabelBytes(beamOrder.Select(b => b.Id).ToArray(), beamLabels) });
        }
        geometryHash = ComputeGeometryHash();
        blocks.Add(new Block { Tag = "META", Domain = GLOBAL_DOMAIN, Count = 0, Bytes = BuildMetadata() });
        if (nFieldLC > 0)
        {
            if (elemOrder.Length > 0)
                blocks.Add(new Block { Tag = "FLDS", Domain = shellDomain, Count = (uint)nFieldLC, Length = (long)nFieldLC * strideLC * FLOAT_SIZE });
            if (beamOrder != null)
                blocks.Add(new Block { Tag = "FLDS", Domain = beamDomain, Count = (uint)nFieldLC, Length = (long)nFieldLC * strideBeamLC * FLOAT_SIZE });
        }

        dirOffset = HEADER_SIZE;
        long off = Align8(dirOffset + (long)blocks.Count * DIR_ENTRY_SIZE);
        foreach (Block b in blocks)
        {
            if (b.Bytes != null) b.Length = b.Bytes.Length;
            b.Offset = off;
            off = Align8(off + b.Length);
        }
        totalLength = off;
        strengthOffset = FindOffset("FLDC", shellDomain);
        fieldOffsetShell = FindOffset("FLDS", shellDomain);
        fieldOffsetBeam = FindOffset("FLDS", beamDomain);
    }

    private static long Align8(long n) { return (n + 7) & ~7L; }

    private long FindOffset(string tag, uint domain)
    {
        foreach (Block b in blocks) if (b.Tag == tag && b.Domain == domain) return b.Offset;
        return 0;
    }

    private static void AddTarget(Dictionary<int, List<JointTarget>> map, int nodeId, int elemIndex, int slot)
    {
        List<JointTarget> list;
        if (!map.TryGetValue(nodeId, out list)) { list = new List<JointTarget>(); map[nodeId] = list; }
        list.Add(new JointTarget { ElemIndex = elemIndex, Slot = slot });
    }

    private static Dictionary<int, int> BuildElemIndex(Element[] elems)
    {
        Dictionary<int, int> map = new Dictionary<int, int>();
        for (int e = 0; e < elems.Length; e++)
        {
            if (map.ContainsKey(elems[e].id)) throw new Exception("RawViewerWriter: duplicate element id " + elems[e].id + ".");
            map[elems[e].id] = e;
        }
        return map;
    }

    private static Dictionary<int, List<JointTarget>> BuildNodeTargets(Element[] elems)
    {
        Dictionary<int, List<JointTarget>> map = new Dictionary<int, List<JointTarget>>();
        for (int e = 0; e < elems.Length; e++)
            for (int s = 0; s < elems[e].nNodes; s++)
                AddTarget(map, elems[e].n[s].id, e, s);
        return map;
    }

    // ---- block payload builders ------------------------------------------
    private byte[] BuildNodeBytes()
    {
        byte[] buf = new byte[nNodes * 24];
        for (int i = 0; i < nNodes; i++)
        {
            Node n = nodeOrder[i];
            Buffer.BlockCopy(BitConverter.GetBytes((double)n.xyz.X), 0, buf, i * 24, 8);
            Buffer.BlockCopy(BitConverter.GetBytes((double)n.xyz.Y), 0, buf, i * 24 + 8, 8);
            Buffer.BlockCopy(BitConverter.GetBytes((double)n.xyz.Z), 0, buf, i * 24 + 16, 8);
        }
        return buf;
    }
    private byte[] BuildNodeIdBytes()
    {
        byte[] buf = new byte[nNodes * 4];
        for (int i = 0; i < nNodes; i++) Buffer.BlockCopy(BitConverter.GetBytes((uint)nodeOrder[i].id), 0, buf, i * 4, 4);
        return buf;
    }
    private byte[] BuildShellElemBytes(Dictionary<int, int> nodeIdToIndex)
    {
        byte[] buf = new byte[nElements * ELEM_RECORD_U32 * 4];
        for (int e = 0; e < nElements; e++)
        {
            Element el = elemOrder[e];
            uint[] rec = new uint[ELEM_RECORD_U32];
            rec[0] = (uint)el.nNodes;
            for (int s = 0; s < MAX_CORNERS; s++) rec[1 + s] = s < el.nNodes ? (uint)nodeIdToIndex[el.n[s].id] : 0u;
            rec[5] = 0;
            Buffer.BlockCopy(rec, 0, buf, e * ELEM_RECORD_U32 * 4, ELEM_RECORD_U32 * 4);
        }
        return buf;
    }
    private byte[] BuildShellElemIdBytes()
    {
        byte[] buf = new byte[nElements * 4];
        for (int e = 0; e < nElements; e++) Buffer.BlockCopy(BitConverter.GetBytes((uint)elemOrder[e].id), 0, buf, e * 4, 4);
        return buf;
    }
    private byte[] BuildBeamElemBytes(Dictionary<int, int> nodeIdToIndex)
    {
        byte[] buf = new byte[nBeams * ELEM_RECORD_U32 * 4];
        for (int b = 0; b < nBeams; b++)
        {
            BeamMember bm = beamOrder[b];
            int ia, ib;
            if (!nodeIdToIndex.TryGetValue(bm.NodeA, out ia) || !nodeIdToIndex.TryGetValue(bm.NodeB, out ib))
                throw new Exception(string.Format("RawViewerWriter: beam {0} references a node not in the node table.", bm.Id));
            uint[] rec = { BEAM_SLOTS, (uint)ia, (uint)ib, (uint)bm.SectionIndex, 0, 0 };
            Buffer.BlockCopy(rec, 0, buf, b * ELEM_RECORD_U32 * 4, ELEM_RECORD_U32 * 4);
        }
        return buf;
    }
    private byte[] BuildBeamElemIdBytes()
    {
        byte[] buf = new byte[nBeams * 4];
        for (int b = 0; b < nBeams; b++) Buffer.BlockCopy(BitConverter.GetBytes((uint)beamOrder[b].Id), 0, buf, b * 4, 4);
        return buf;
    }
    private byte[] BuildBprpBytes()
    {
        float[] f = new float[nBeams * BPRP_F32];
        for (int b = 0; b < nBeams; b++)
        {
            BeamMember bm = beamOrder[b];
            int o = b * BPRP_F32;
            double[] y = bm.LocalY ?? new double[] { 0, 0, 1 };
            f[o] = (float)y[0]; f[o + 1] = (float)y[1]; f[o + 2] = (float)y[2];
            f[o + 3] = (float)bm.OffsetAy; f[o + 4] = (float)bm.OffsetAz;
            f[o + 5] = (float)bm.OffsetBy; f[o + 6] = (float)bm.OffsetBz;
            f[o + 7] = BitConverter.ToSingle(BitConverter.GetBytes(bm.ReleaseMask), 0);
        }
        byte[] buf = new byte[f.Length * 4];
        Buffer.BlockCopy(f, 0, buf, 0, buf.Length);
        return buf;
    }
    private byte[] BuildSectBytes()
    {
        using (MemoryStream ms = new MemoryStream())
        using (BinaryWriter bw = new BinaryWriter(ms))
        {
            bw.Write((uint)sections.Count);
            foreach (SectionDef s in sections)
            {
                bw.Write(s.TypeCode);
                bw.Write((uint)s.Params.Length);
                foreach (float p in s.Params) bw.Write(p);
                if (s.TypeCode == SectionDef.POLY)
                {
                    float[][] pts = s.Points ?? new float[0][];
                    bw.Write((uint)pts.Length);
                    foreach (float[] pt in pts) { bw.Write(pt[0]); bw.Write(pt[1]); }
                }
            }
            return ms.ToArray();
        }
    }

    // ---- labels (optional identity strings, by REAL id) -------------------
    public void SetNodeLabels(Dictionary<int, string> byNodeId)   { nodeLabels = byNodeId;  Layout(); }
    public void SetShellLabels(Dictionary<int, string> byElemId)  { shellLabels = byElemId; Layout(); }
    public void SetBeamLabels(Dictionary<int, string> byBeamId)
    {
        if (beamOrder == null) throw new Exception("SetBeamLabels: writer was built without beams.");
        beamLabels = byBeamId; Layout();
    }

    // LABL payload (schema section 3): u32[n+1] byte offsets into a UTF-8 pool
    // that follows. Missing ids get an empty string.
    private static byte[] BuildLabelBytes(int[] idsInOrder, Dictionary<int, string> labels)
    {
        int n = idsInOrder.Length;
        UTF8Encoding enc = new UTF8Encoding(false);
        byte[][] strs = new byte[n][];
        long pool = 0;
        for (int i = 0; i < n; i++)
        {
            string s;
            strs[i] = enc.GetBytes(labels.TryGetValue(idsInOrder[i], out s) && s != null ? s : "");
            pool += strs[i].Length;
        }
        if (pool > uint.MaxValue) throw new Exception("BuildLabelBytes: label pool exceeds 4 GB.");
        byte[] buf = new byte[(n + 1) * 4 + pool];
        uint off = 0;
        for (int i = 0; i < n; i++)
        {
            Buffer.BlockCopy(BitConverter.GetBytes(off), 0, buf, i * 4, 4);
            off += (uint)strs[i].Length;
        }
        Buffer.BlockCopy(BitConverter.GetBytes(off), 0, buf, n * 4, 4);
        int p = (n + 1) * 4;
        for (int i = 0; i < n; i++) { Buffer.BlockCopy(strs[i], 0, buf, p, strs[i].Length); p += strs[i].Length; }
        return buf;
    }

    // SHA-256 over NODE NDID ELEM(d asc) ELID(d asc) SECT BPRP(d asc) bytes.
    private string ComputeGeometryHash()
    {
        string[] order = { "NODE", "NDID", "ELEM", "ELID", "SECT", "BPRP" };
        using (SHA256 sha = SHA256.Create())
        {
            foreach (string tag in order)
                foreach (Block b in blocks.Where(x => x.Tag == tag && x.Bytes != null).OrderBy(x => x.Domain))
                    sha.TransformBlock(b.Bytes, 0, b.Bytes.Length, null, 0);
            sha.TransformFinalBlock(new byte[0], 0, 0);
            StringBuilder sb = new StringBuilder("sha256:");
            foreach (byte x in sha.Hash) sb.Append(x.ToString("x2"));
            return sb.ToString();
        }
    }

    public string GeometryHash { get { return geometryHash; } }

    // ---- phase 1: write everything, NaN-fill every field block ----------
    // writeFields:false produces a GEOMETRY-ONLY profile (same hash): the
    // FLDS/FLDC blocks are left out of the directory and not emitted.
    public void Write() { Write(true); }

    public void Write(bool writeFields)
    {
        List<Block> emit = writeFields ? blocks : blocks.Where(b => b.Bytes != null).ToList();
        using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write))
        using (BinaryWriter bw = new BinaryWriter(fs))
        {
            bw.Write(MAGIC);
            bw.Write(VERSION);
            bw.Write((uint)HEADER_SIZE);
            bw.Write((uint)emit.Count);
            bw.Write((ulong)dirOffset);
            bw.Write((ulong)0);

            foreach (Block b in emit)
            {
                bw.Write(TagU32(b.Tag));
                bw.Write(b.Domain);
                bw.Write((ulong)b.Offset);
                bw.Write((ulong)b.Length);
                bw.Write(b.Count);
                bw.Write(b.Flags);
            }

            foreach (Block b in emit)
            {
                PadTo(bw, b.Offset);
                if (b.Bytes != null) bw.Write(b.Bytes);
                else WriteNaNFloats(bw, b.Length / FLOAT_SIZE);
            }
            PadTo(bw, writeFields ? totalLength : Align8(fs.Position));
        }
    }

    private static uint TagU32(string tag)
    {
        return (uint)tag[0] | ((uint)tag[1] << 8) | ((uint)tag[2] << 16) | ((uint)tag[3] << 24);
    }

    private static void PadTo(BinaryWriter bw, long offset)
    {
        long cur = bw.BaseStream.Position;
        if (cur > offset) throw new Exception("RawViewerWriter: layout overrun at " + cur + " > " + offset + ".");
        while (cur < offset) { bw.Write((byte)0); cur++; }
    }

    private static void WriteNaNFloats(BinaryWriter bw, long totalFloats)
    {
        const int CHUNK = 8192;
        byte[] nanBuf = new byte[CHUNK * FLOAT_SIZE];
        byte[] oneNaN = BitConverter.GetBytes(float.NaN);
        for (int i = 0; i < CHUNK; i++) Buffer.BlockCopy(oneNaN, 0, nanBuf, i * FLOAT_SIZE, FLOAT_SIZE);
        long remaining = totalFloats;
        while (remaining > 0)
        {
            int n = (int)Math.Min((long)CHUNK, remaining);
            bw.Write(nanBuf, 0, n * FLOAT_SIZE);
            remaining -= n;
        }
    }

    // ---- phase 2: appends ------------------------------------------------

    private MemoryMappedFile OpenMap() { return MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.ReadWrite); }

    public void AppendStresses(List<StressRecord> stresses)
    {
        if (stresses == null) return;
        if (layout == null) throw new Exception("Append: writer was built without shell elements.");
        int start = layout.Start("stress");
        int count = layout.Count("stress");
        using (MemoryMappedFile mmf = OpenMap())
        using (MemoryMappedViewAccessor acc = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite))
        {
            foreach (StressRecord r in stresses)
            {
                int plane = ResolveLcPlane(r.LC, "Stresses");
                int elemIndex;
                if (!elemIdToIndex.TryGetValue(r.elemID, out elemIndex))
                    throw new Exception(string.Format("AppendStresses: element id {0} not in model.", r.elemID));
                int slot = CornerSlotForNode(elemIndex, r.node);
                if (slot < 0) continue;   // element-centre results mixed in -- skip
                if (r.Sf == null || r.Sf.Length < count)
                    throw new Exception(string.Format("AppendStresses: record for elem {0} node {1} has {2} values, expected {3}.",
                        r.elemID, r.node, (r.Sf == null ? 0 : r.Sf.Length), count));
                long bytePos = fieldOffsetShell + (FieldPos(plane, elemIndex, slot) + start) * FLOAT_SIZE;
                float[] sf = r.StressViewForce(ForceUnit.Kip, LengthUnit.Foot);
                acc.WriteArray(bytePos, sf, 0, count);
            }
        }
    }

    // Per-node: fan out to every shell corner AND every beam end touching the node.
    public void AppendDisplacements(List<Disp> disps)
    {
        if (disps == null) return;
        bool shellDisp = layout != null && layout.HasKind("displacement");
        int start = shellDisp ? layout.Start("displacement") : 0;
        int count = shellDisp ? layout.Count("displacement") : 0;
        bool beamDisp = beamLayout != null && beamLayout.HasKind("displacement");
        int bStart = beamDisp ? beamLayout.Start("displacement") : 0;
        int bCount = beamDisp ? beamLayout.Count("displacement") : 0;

        using (MemoryMappedFile mmf = OpenMap())
        using (MemoryMappedViewAccessor acc = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite))
        {
            foreach (Disp d in disps)
            {
                int plane = ResolveLcPlane(d.LC, "Displacements");
                if (d.DR == null || d.DR.Length < Math.Max(count, bCount))
                    throw new Exception(string.Format("AppendDisplacements: record for node {0} has {1} values, expected {2}.",
                        d.node, (d.DR == null ? 0 : d.DR.Length), Math.Max(count, bCount)));

                List<JointTarget> targets;
                if (shellDisp && nodeToTargets.TryGetValue(d.node, out targets))
                    foreach (JointTarget jt in targets)
                        acc.WriteArray(fieldOffsetShell + (FieldPos(plane, jt.ElemIndex, jt.Slot) + start) * FLOAT_SIZE, d.DR, 0, count);

                if (beamDisp && nodeToBeamEnds.TryGetValue(d.node, out targets))
                {
                    int n = Math.Min(bCount, d.DR.Length);
                    foreach (JointTarget jt in targets)
                        acc.WriteArray(fieldOffsetBeam + (BeamPos(plane, jt.ElemIndex, jt.Slot) + bStart) * FLOAT_SIZE, d.DR, 0, n);
                }
            }
        }
    }

    public void AppendDsr(List<DsrRecord> dsrs)
    {
        if (dsrs == null) return;
        if (layout == null) throw new Exception("Append: writer was built without shell elements.");
        if (!layout.HasKind("dsr")) throw new Exception("AppendDsr: component layout has no 'dsr' fields.");
        int start = layout.Start("dsr");
        int count = layout.Count("dsr");
        using (MemoryMappedFile mmf = OpenMap())
        using (MemoryMappedViewAccessor acc = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite))
        {
            foreach (DsrRecord r in dsrs)
            {
                int plane = ResolveLcPlane(r.LC, "Dsr");
                int elemIndex;
                if (!elemIdToIndex.TryGetValue(r.elemID, out elemIndex))
                    throw new Exception(string.Format("AppendDsr: element id {0} not in model.", r.elemID));
                int slot = CornerSlotForNode(elemIndex, r.node);
                if (slot < 0) continue;
                if (r.Values == null || r.Values.Length < count)
                    throw new Exception(string.Format("AppendDsr: record for elem {0} node {1} has {2} values, expected {3}.",
                        r.elemID, r.node, (r.Values == null ? 0 : r.Values.Length), count));
                acc.WriteArray(fieldOffsetShell + (FieldPos(plane, elemIndex, slot) + start) * FLOAT_SIZE, r.Values, 0, count);
            }
        }
    }

    public void AppendStr(List<StrRecord> strs)
    {
        if (strs == null) return;
        if (layout == null) throw new Exception("Append: writer was built without shell elements.");
        if (nStr == 0) throw new Exception("AppendStr: writer was built without design-strength components.");
        using (MemoryMappedFile mmf = OpenMap())
        using (MemoryMappedViewAccessor acc = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite))
        {
            foreach (StrRecord r in strs)
            {
                int elemIndex;
                if (!elemIdToIndex.TryGetValue(r.elemID, out elemIndex))
                    throw new Exception(string.Format("AppendStr: element id {0} not in model.", r.elemID));
                int slot = CornerSlotForNode(elemIndex, r.node);
                if (slot < 0) continue;
                if (r.Values == null || r.Values.Length < nStr)
                    throw new Exception(string.Format("AppendStr: record for elem {0} node {1} has {2} values, expected {3}.",
                        r.elemID, r.node, (r.Values == null ? 0 : r.Values.Length), nStr));
                long floatPos = (long)elemIndex * strideElemStr + (long)slot * nStr;
                acc.WriteArray(strengthOffset + floatPos * FLOAT_SIZE, r.Values, 0, nStr);
            }
        }
    }

    // Beam end results. kind: which contiguous run of the beam component
    // list the record's Values[] fills (e.g. "force"); null = whole list.
    public void AppendBeamForces(List<BeamRecord> records, string kind = "force")
    {
        if (records == null) return;
        if (beamOrder == null) throw new Exception("AppendBeamForces: writer was built without beams.");
        if (nFieldLC == 0) throw new Exception("AppendBeamForces: writer was built with no load cases (geometry-only).");
        int start = kind == null ? 0 : beamLayout.Start(kind);
        int count = kind == null ? nBeamComps : beamLayout.Count(kind);
        using (MemoryMappedFile mmf = OpenMap())
        using (MemoryMappedViewAccessor acc = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite))
        {
            foreach (BeamRecord r in records)
            {
                int plane = ResolveLcPlane(r.LC, "BeamForces");
                int b;
                if (!beamIdToIndex.TryGetValue(r.elemID, out b))
                    throw new Exception(string.Format("AppendBeamForces: beam id {0} not in model.", r.elemID));
                if (r.End < 0 || r.End >= BEAM_SLOTS)
                    throw new Exception(string.Format("AppendBeamForces: beam {0} end {1} out of range.", r.elemID, r.End));
                if (r.Values == null || r.Values.Length < count)
                    throw new Exception(string.Format("AppendBeamForces: record for beam {0} end {1} has {2} values, expected {3}.",
                        r.elemID, r.End, (r.Values == null ? 0 : r.Values.Length), count));
                acc.WriteArray(fieldOffsetBeam + (BeamPos(plane, b, r.End) + start) * FLOAT_SIZE, r.Values, 0, count);
            }
        }
    }

    // ---- offset helpers ------------------------------------------------------
    private long FieldPos(int lc, int elemIndex, int slot)
    {
        return (long)lc * strideLC + (long)elemIndex * strideElem + (long)slot * strideCorner;
    }
    private long BeamPos(int lc, int beamIndex, int end)
    {
        return (long)lc * strideBeamLC + (long)beamIndex * strideBeamElem + (long)end * nBeamComps;
    }
    private int CornerSlotForNode(int elemIndex, int nodeId)
    {
        Element el = elemOrder[elemIndex];
        for (int s = 0; s < el.nNodes; s++) if (el.n[s].id == nodeId) return s;
        return -1;
    }
    private int ResolveLcPlane(int staadLc, string what)
    {
        int plane;
        if (!lcIdToPlane.TryGetValue(staadLc, out plane))
            throw new Exception(string.Format("Append{0}: STAAD load-case id {1} is not a known field LC.", what, staadLc));
        return plane;
    }

    // ---- metadata (schema §5) ----------------------------------------------
    private byte[] BuildMetadata()
    {
        StringBuilder sb = new StringBuilder();
        sb.Append("{\"generator\":\"RawViewerWriter 4.0\"");
        if (modelId != null) { sb.Append(",\"modelId\":"); AppendJsonString(sb, modelId); }
        sb.Append(",\"geometryHash\":"); AppendJsonString(sb, geometryHash);
        if (units != null && units.Count > 0)
        {
            sb.Append(",\"units\":{");
            bool first = true;
            foreach (KeyValuePair<string, string> kv in units)
            {
                if (!first) sb.Append(","); first = false;
                AppendJsonString(sb, kv.Key); sb.Append(":"); AppendJsonString(sb, kv.Value);
            }
            sb.Append("}");
        }

        sb.Append(",\"loadCases\":[");
        for (int i = 0; i < fieldLcIds.Length; i++)
        {
            if (i > 0) sb.Append(",");
            sb.Append("{\"name\":"); AppendJsonString(sb, lcNamesById[fieldLcIds[i]]); sb.Append(",\"type\":\"primary\"}");
        }
        sb.Append("]");

        sb.Append(",\"domains\":[");
        if (elemOrder.Length > 0)
        {
            sb.Append("{\"name\":\"shells\",\"family\":\"shell\",\"maxSlots\":").Append(MAX_CORNERS);
            sb.Append(",\"components\":"); AppendComponents(sb, cornerComps);
            if (nStr > 0) { sb.Append(",\"constComponents\":"); AppendComponents(sb, strengthComponents, "str"); }
            AppendDispVector(sb, layout);
            sb.Append("}");
        }
        if (beamOrder != null)
        {
            if (elemOrder.Length > 0) sb.Append(",");
            sb.Append("{\"name\":\"beams\",\"family\":\"beam\",\"maxSlots\":").Append(BEAM_SLOTS);
            sb.Append(",\"components\":"); AppendComponents(sb, beamComps);
            AppendDispVector(sb, beamLayout);
            sb.Append("}");
        }
        sb.Append("]");

        if (beamOrder != null)
        {
            sb.Append(",\"sections\":[");
            for (int i = 0; i < sections.Count; i++)
            {
                if (i > 0) sb.Append(",");
                sb.Append("{\"name\":"); AppendJsonString(sb, sections[i].Name ?? ("Section " + i));
                sb.Append(",\"type\":"); AppendJsonString(sb, SectionTypeName(sections[i].TypeCode)); sb.Append("}");
            }
            sb.Append("]");
        }
        sb.Append("}");
        return new UTF8Encoding(false).GetBytes(sb.ToString());
    }

    private static string SectionTypeName(uint code)
    {
        switch (code)
        {
            case SectionDef.RECT: return "RECT"; case SectionDef.I: return "I"; case SectionDef.BOX: return "BOX";
            case SectionDef.PIPE: return "PIPE"; case SectionDef.L: return "L"; case SectionDef.C: return "C";
            case SectionDef.T: return "T"; case SectionDef.POLY: return "POLY"; default: return "UNKNOWN" + code;
        }
    }

    private static void AppendComponents(StringBuilder sb, List<Component> comps, string forceKind = null)
    {
        sb.Append("[");
        for (int i = 0; i < comps.Count; i++)
        {
            if (i > 0) sb.Append(",");
            sb.Append("{\"name\":"); AppendJsonString(sb, comps[i].Name);
            sb.Append(",\"kind\":"); AppendJsonString(sb, forceKind ?? comps[i].Kind);
            if (comps[i].Unit != null) { sb.Append(",\"unit\":"); AppendJsonString(sb, comps[i].Unit); }
            sb.Append("}");
        }
        sb.Append("]");
    }

    // First three of the "displacement" run are tx,ty,tz (STAAD DR order).
    private static void AppendDispVector(StringBuilder sb, ComponentLayout lay)
    {
        if (lay.HasKind("displacement") && lay.Count("displacement") >= 3)
        {
            int d0 = lay.Start("displacement");
            sb.Append(",\"displacementVector\":[").Append(d0).Append(",").Append(d0 + 1).Append(",").Append(d0 + 2).Append("]");
        }
    }

    private static void AppendJsonString(StringBuilder sb, string s)
    {
        sb.Append("\"");
        foreach (char c in s)
        {
            if (c == '"' || c == '\\') { sb.Append('\\'); sb.Append(c); }
            else if (c == '\n') sb.Append("\\n");
            else if (c == '\r') sb.Append("\\r");
            else if (c == '\t') sb.Append("\\t");
            else if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
            else sb.Append(c);
        }
        sb.Append("\"");
    }
}
