// ================================================================
// models.js  --  the loaded models and which one is active (model rail, stage 2).
// Design: vault/viewer/model-rail.md · module map: vault/viewer/Viewer modules.md
//
// Every model in the scene is a MODEL CONTEXT. Modules that act on "a model"
// (the Groups tab, the rail) take one through this interface and never reach
// for viewer globals or another module's internals:
//
//   ctx.id, ctx.kind ('primary' | 'overlay'), ctx.name()
//   ctx.shellModel()        shell domain view (header.nElements, elemIds, nodeIds, nodes, unified) or null
//   ctx.beamView()          beam domain view or null
//   ctx.paint(fam, cat)     write per-element group index (Float32Array, -1 = none) for 'shell' | 'beam'
//   ctx.setGroupLook(on, palette, count)   group painting on/off + palette texture
//   ctx.applyVisibility()   re-apply group eyes (reads FEAFeatures.elemOffFor(ctx, fam))
//   ctx.markerParent()      THREE.Object3D that node markers are added to (carries the placement)
//   ctx.look                { visible, xray, color }  -- set through setVisible / setXray / setColor
//   ctx.feat                state owned by features.js (opaque here)
//
// The primary context wraps the existing globals (feaModel, feaBuild, feaMaterial,
// FEABeams), so section cuts, predicates, load cases and the legend keep working
// on it unchanged; they stay primary-only (stage 3). Overlay contexts are made
// by overlays.js from FEAModelMesh drawables.
//
// Listeners: FEAModels.onChange(fn) fires on register / unregister / setActive.
// ================================================================

var FEAModels = (function () {

    var models = [];
    var active = null;
    var listeners = [];

    function emit(what) { listeners.forEach(function (fn) { try { fn(what); } catch (e) { console.error(e); } }); }

    function register(ctx) {
        if (models.indexOf(ctx) < 0) models.push(ctx);
        if (!active) active = ctx;
        emit('register');
    }
    function unregister(ctx) {
        var i = models.indexOf(ctx);
        if (i < 0) return;
        models.splice(i, 1);
        if (active === ctx) setActive(models[0] || null);
        emit('unregister');
    }
    function setActive(ctx) {
        if (ctx === active) return;
        var prev = active;
        active = ctx;
        if (window.FEAFeatures && FEAFeatures.bind) FEAFeatures.bind(ctx, prev);
        emit('active');
    }

    // ---- the primary model: an adapter over the viewer globals -------------
    function g(name) { return typeof window[name] !== 'undefined' ? window[name] : null; }
    function beams() { return window.FEABeams || null; }

    var primary = {
        id: 'primary',
        kind: 'primary',
        feat: {},
        look: { visible: true, xray: false, color: '#9ea3ad' },
        name: function () { return window.feaPrimaryName || 'model'; },
        shellModel: function () { return g('feaModel'); },
        beamView: function () { return beams() && beams().view ? beams().view() : null; },
        paint: function (fam, cat) {
            if (fam === 'shell') {
                var fb = g('feaBuild');
                if (fb) FEAAttributes.updateCatIdx(fb.geometry, fb.elemNCount, null, cat);
            } else if (beams() && beams().build && beams().build()) {
                var bb = beams().build();
                FEAAttributes.updateCatIdx(bb.geometry, null, bb, cat);
            }
        },
        setGroupLook: function (on, palette, count) {
            [g('feaMaterial'), beams() && beams().mesh && beams().mesh() ? beams().mesh().material : null].forEach(function (mat) {
                if (!mat || !mat.uniforms.uGroupMode) return;
                mat.uniforms.uGroupMode.value = on ? 1 : 0;
                if (on && palette) { mat.uniforms.groupPalette.value = palette; mat.uniforms.uGroupCount.value = count; }
            });
        },
        // eyes combine with the section-cut isolate through its writers
        applyVisibility: function () {
            var done = window.FEASectionCut && FEASectionCut.reapplyVis && FEASectionCut.reapplyVis();
            if (!done && beams() && beams().reapplyVis) beams().reapplyVis();
        },
        markerParent: function () { return g('scene'); },
        setVisible: function (on) {
            primary.look.visible = on;
            var m = g('mesh'); if (m) m.visible = on;
            var ed = g('feaEdges'); if (ed) ed.visible = on && !!(document.getElementById('feaEdges') || {}).checked;
            var bm = beams() && beams().mesh ? beams().mesh() : null;
            if (bm) bm.visible = on && (document.getElementById('beamShow') ? document.getElementById('beamShow').checked : true);
            redraw();
        },
        // x-ray covers the shells here and the beams through the beam panel's own switch
        setXray: function (on) {
            primary.look.xray = on;
            xrayMaterial(g('feaMaterial'), on);
            var cb = document.getElementById('beamXray');
            if (cb && cb.checked !== on) { cb.checked = on; cb.dispatchEvent(new Event('change')); }
            redraw();
        },
        // the primary keeps its field colouring when group painting is off: no flat model colour
        setColor: null
    };

    // Additive translucency: brightness counts overlapping surfaces (the beam x-ray rule).
    var XRAY_ALPHA = 0.10;
    function xrayMaterial(mat, on) {
        if (!mat || !mat.uniforms || !mat.uniforms.uXray) return;
        mat.uniforms.uXray.value = on ? XRAY_ALPHA : 0;
        mat.transparent = on;
        mat.depthWrite = !on;
        mat.blending = on ? THREE.AdditiveBlending : THREE.NormalBlending;
        mat.needsUpdate = true;
    }
    function redraw() { if (typeof needsRender !== 'undefined') needsRender = true; }

    return {
        register: register,
        unregister: unregister,
        setActive: setActive,
        active: function () { return active; },
        list: function () { return models.slice(); },
        primary: function () { return primary; },
        isPrimaryActive: function () { return active === primary; },
        onChange: function (fn) { listeners.push(fn); },
        xrayMaterial: xrayMaterial,
        // the primary is always registered first (it exists from page load; it may hold no model yet)
        _init: function () { register(primary); }
    };
})();
FEAModels._init();
