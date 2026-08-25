// ================================================================
// pluto.js  --  format entry point.
//
//   PlutoFormat.load(file, log)      -> unified model (any version)
//   PlutoFormat.domainView(model, d) -> v3-SHAPED view of one domain
//   PlutoFormat.shellView(model)     -> view of the first shell domain
//   FEABinary                        -> compat API the shell pipeline
//                                       (modelSet, updaters, pointQuery,
//                                       viewer) calls on a VIEW
//
// Version dispatch happens on header byte 4 before anything else is
// read: <=3 -> frozen v3Reader + adapter, 4 -> v4Reader. Everything
// downstream sees ONE model shape (vault/format/v4-schema.md §10).
//
// A domain VIEW is the object the pre-v4 shell code was written
// against: { header:{nNodes,nElements,nFieldLC,cornerComponents,
// maxCorners}, meta:{loadCases,components,dispVector,strengths,raw},
// nodes, elems, elemRecordU32, nodeIds, elemIds, strData } plus
// read closures. v3 corner-field / strength blocks have exactly the
// FLDS / FLDC layouts, so one generic reader serves both versions.
// ================================================================

var PlutoFormat = (function () {

    function readRange(file, byteStart, byteLength) {
        return file.slice(byteStart, byteStart + byteLength).arrayBuffer();
    }

    // ---- v3 -> unified adapter ----------------------------------------
    function fromV3(m) {
        var h = m.header;
        // Widen f32 nodes to f64, repack u32[5] element records to u32[6].
        var nodes = new Float64Array(m.nodes.length);
        for (var i = 0; i < m.nodes.length; i++) nodes[i] = m.nodes[i];
        var REC3 = 5, REC4 = 6;
        var elems = new Uint32Array(h.nElements * REC4);
        for (var e = 0; e < h.nElements; e++) {
            for (var k = 0; k < REC3; k++) elems[e * REC4 + k] = m.elems[e * REC3 + k];
            elems[e * REC4 + 5] = 0;
        }
        var strengths = m.meta.strengths;
        var constComps = strengths ? strengths.components.map(function (c) {
            return { name: c.name, kind: 'str', unit: c.unit };
        }) : [];
        var domain = {
            index: 0,
            name: 'shells',
            family: 'shell',
            maxSlots: h.maxCorners,
            components: m.meta.components,
            constComponents: constComps,
            dispVector: m.meta.dispVector,
            nElem: h.nElements,
            elemRecordU32: REC4,
            elems: elems,
            elemIds: m.elemIds,
            fields: h.nFieldLC > 0 ? {
                offset: h.cornerFieldOffset, nLC: h.nFieldLC, planeStride: m.lcByteStride
            } : null,
            constFields: strengths ? {
                offset: strengths.offset, nComp: constComps.length,
                byteLength: h.nElements * h.maxCorners * constComps.length * 4
            } : null,
            beamProps: null,
            constData: null
        };
        var raw = m.meta.raw || {};
        return {
            version: h.version,
            file: m.file,
            nNodes: h.nNodes,
            nodes: nodes,
            nodeIds: m.nodeIds,
            loadCases: m.meta.loadCases,
            sections: [],
            meta: raw,
            modelId: raw.modelId || null,
            geometryHash: raw.geometryHash || null,
            units: raw.lengthUnit ? { length: raw.lengthUnit } : (raw.units || null),
            domains: [domain],
            directory: null
        };
    }

    // ---- dispatch ------------------------------------------------------
    async function load(file, log) {
        log = log || function () {};
        if (file.size < 8) throw new Error('File is only ' + file.size + ' bytes.');
        var dv = new DataView(await readRange(file, 0, 8));
        var magic = dv.getUint32(0, true), version = dv.getUint32(4, true);
        if (magic !== FEAv4.MAGIC) {
            throw new Error('Not a recognized FEA field file: header magic is 0x' +
                (magic >>> 0).toString(16) + '.');
        }
        if (version <= 3) {
            log('Legacy v' + version + ' file: loading through the v3 shim.');
            return fromV3(await FEAv3.loadModel(file, log));
        }
        if (version === 4) return FEAv4.loadModel(file, log);
        throw new Error('Unsupported format version ' + version + ' (this viewer reads v3 and v4).');
    }

    // ---- generic slot-field readers (unified model) --------------------
    async function readDomainLC(model, dom, lc) {
        var f = dom.fields;
        if (!f) throw new Error('Domain "' + dom.name + '" has no field block.');
        if (lc < 0 || lc >= f.nLC) throw new Error('LC index out of range: ' + lc);
        var buf = await readRange(model.file, f.offset + lc * f.planeStride, f.planeStride);
        return new Float32Array(buf);       // [nElem][maxSlots][nComp]
    }

    async function readDomainElementRecord(model, dom, lc, elem) {
        var f = dom.fields;
        if (!f) throw new Error('Domain "' + dom.name + '" has no field block.');
        if (lc < 0 || lc >= f.nLC) throw new Error('LC index out of range: ' + lc);
        if (elem < 0 || elem >= dom.nElem) throw new Error('Element index out of range: ' + elem);
        var recFloats = dom.maxSlots * dom.components.length;
        var offset = f.offset + lc * f.planeStride + elem * recFloats * 4;
        return new Float32Array(await readRange(model.file, offset, recFloats * 4));
    }

    async function readDomainConst(model, dom) {
        if (!dom.constFields) return null;
        if (dom.constData) return dom.constData;
        var c = dom.constFields;
        dom.constData = new Float32Array(await readRange(model.file, c.offset, c.byteLength));
        return dom.constData;
    }

    // ---- v3-shaped domain view -----------------------------------------
    function domainView(model, d) {
        var dom = model.domains[d];
        if (!dom) return null;
        var view = {
            unified: model,
            domain: dom,
            domainIndex: d,
            family: dom.family,
            file: model.file,
            header: {
                version: model.version,
                nNodes: model.nNodes,
                nElements: dom.nElem,
                nFieldLC: dom.fields ? dom.fields.nLC : 0,
                cornerComponents: dom.components.length,
                maxCorners: dom.maxSlots
            },
            meta: {
                loadCases: model.loadCases,
                components: dom.components,
                dispVector: dom.dispVector,
                strengths: dom.constFields ? {
                    offset: dom.constFields.offset, components: dom.constComponents
                } : null,
                raw: model.meta
            },
            nodes: model.nodes,
            nodeIds: model.nodeIds,
            elems: dom.elems,
            elemRecordU32: dom.elemRecordU32,
            elemIds: dom.elemIds,
            lcByteStride: dom.fields ? dom.fields.planeStride : 0,
            sections: model.sections,
            beamProps: dom.beamProps,
            strData: null
        };
        view.readLC = function (lc) { return readDomainLC(model, dom, lc); };
        view.readElementRecord = function (lc, e) { return readDomainElementRecord(model, dom, lc, e); };
        view.readStrengths = async function () {
            view.strData = await readDomainConst(model, dom);
            return view.strData;
        };
        return view;
    }

    function findDomain(model, family) {
        for (var d = 0; d < model.domains.length; d++) {
            if (model.domains[d].family === family) return d;
        }
        return -1;
    }
    function shellView(model) {
        var d = findDomain(model, 'shell');
        return d >= 0 ? domainView(model, d) : null;
    }
    function beamView(model) {
        var d = findDomain(model, 'beam');
        return d >= 0 ? domainView(model, d) : null;
    }

    return {
        load: load,
        fromV3: fromV3,
        domainView: domainView,
        shellView: shellView,
        beamView: beamView,
        findDomain: findDomain,
        readDomainLC: readDomainLC,
        readDomainElementRecord: readDomainElementRecord,
        readDomainConst: readDomainConst,
        readRange: readRange
    };
})();

// Compat surface for the shell pipeline. Every function takes a domain
// VIEW (PlutoFormat.domainView) where the v3 reader took a model.
var FEABinary = {
    readRange: PlutoFormat.readRange,
    readLC: function (view, lc) { return view.readLC(lc); },
    readElementRecord: function (view, lc, e) { return view.readElementRecord(lc, e); },
    readStrengths: function (view) { return view.readStrengths(); },
    fieldIndex: function (header, elem, corner, comp) {
        var cc = header.cornerComponents;
        return elem * header.maxCorners * cc + corner * cc + comp;
    },
    lcByteStride: function (header) {
        return header.nElements * header.maxCorners * header.cornerComponents * 4;
    }
};
