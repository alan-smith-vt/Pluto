// ================================================================
// shaders.js
// Vertex/fragment GLSL for true bilinear per-corner field rendering,
// plus colormap LUT (256x1 DataTexture) builders.
//
// The field is reconstructed in the FRAGMENT shader from the 4 corner
// values + quad-local UV, so a quad shows a true bilinear field with
// no diagonal triangle seam. Interpolation happens in SCALAR space,
// then maps to color through the LUT (correct contour bands).
//
// Deformation: every vertex carries a dispVec (its own corner's
// displacement vector); the vertex shader offsets position by
// dispScale * dispVec. dispScale is 0 when deformation is off, and
// is animated CPU-side (userScale * sin phase) when cycling.
//
// Display transforms (fragment):
//   uAbs         1.0 -> display |f| (abs AFTER interpolation, so the
//                interior zero-crossing line is exact)
//   uCategorical 1.0 -> nearest-corner value instead of bilinear
//                (category indices must never be interpolated)
// ================================================================

var FEAShaders = (function () {

    // elemVis is 1.0 on every vertex unless a view (the section-cut
    // isolate toggle) has hidden that element -- see the discard in
    // every fragment shader below.
    var vertex = [
        'attribute vec2 quadUV;',
        'attribute vec4 cornerVals;',
        'attribute vec3 dispVec;',
        'attribute float elemVis;',
        'attribute float catIdx;',
        'uniform float dispScale;',
        'varying vec2 vUV;',
        'varying vec4 vVals;',
        'varying float vVis;',
        'varying float vCat;',
        'void main() {',
        '  vUV = quadUV;',
        '  vVals = cornerVals;',
        '  vVis = elemVis;',
        '  vCat = catIdx;',
        '  vec3 p = position + dispScale * dispVec;',
        '  gl_Position = projectionMatrix * modelViewMatrix * vec4(p, 1.0);',
        '}'
    ].join('\n');

    // Shared scalar reconstruction: bilinear normally, nearest-corner in
    // categorical mode, optional abs. Used by both the field and flash
    // fragment shaders so the two can never disagree about the value.
    var fieldEvalGLSL = [
        'float fieldValue(vec2 uv, vec4 vals, float categorical, float useAbs) {',
        '  float f;',
        '  if (categorical > 0.5) {',
        '    f = uv.x < 0.5',
        '      ? (uv.y < 0.5 ? vals.x : vals.w)',
        '      : (uv.y < 0.5 ? vals.y : vals.z);',
        '  } else {',
        '    float u = uv.x, v = uv.y;',
        '    f = (1.0-u)*(1.0-v)*vals.x',
        '      +      u *(1.0-v)*vals.y',
        '      +      u *     v *vals.z',
        '      + (1.0-u)*    v *vals.w;',
        '    if (useAbs > 0.5) f = abs(f);',
        '  }',
        '  return f;',
        '}'
    ].join('\n');

    // Group coloring (features sidecar): when uGroupMode is on, paint
    // by the per-element category through groupPalette; ungrouped
    // elements (catIdx < 0) get a neutral grey.
    var groupGLSL = [
        'uniform float uGroupMode;',
        'uniform sampler2D groupPalette;',
        'uniform float uGroupCount;',
        'vec3 groupColor(float cat) {',
        '  if (cat < 0.0) return vec3(0.39, 0.40, 0.42);',
        '  return texture2D(groupPalette, vec2((cat + 0.5) / max(uGroupCount, 1.0), 0.5)).rgb;',
        '}'
    ].join('\n');

    var fragment = [
        'varying vec2 vUV;',
        'varying vec4 vVals;',
        'varying float vVis;',
        'varying float vCat;',
        groupGLSL,
        'uniform sampler2D colormap;',  // 256x1 LUT
        'uniform float vMin;',
        'uniform float vMax;',
        'uniform float alarmThreshold;',  // <= 0 disables
        'uniform vec3  alarmColor;',
        'uniform float uAbs;',            // 1.0 -> display |field|
        'uniform float uCategorical;',    // 1.0 -> nearest-corner category
        fieldEvalGLSL,
        'void main() {',
        '  if (vVis < 0.5) discard;',
        '  if (uGroupMode > 0.5) { gl_FragColor = vec4(groupColor(vCat), 1.0); return; }',
        '  float f = fieldValue(vUV, vVals, uCategorical, uAbs);',
        // NaN guard: any NaN corner -> f is NaN -> render no-data color.
        '  if (!(f == f)) { gl_FragColor = vec4(0.16, 0.16, 0.18, 1.0); return; }',
        // Overstress flag: any value at or above alarmThreshold renders in
        // alarmColor instead of the colormap. Default magenta -- distinct
        // from every colormap max (viridis yellow, turbo & coolwarm red,
        // grayscale white) and the matplotlib convention for clipped values.
        '  if (alarmThreshold > 0.0 && f >= alarmThreshold) {',
        '    gl_FragColor = vec4(alarmColor, 1.0); return;',
        '  }',
        '  float denom = max(vMax - vMin, 1e-6);',
        '  float t = clamp((f - vMin) / denom, 0.0, 1.0);',
        '  gl_FragColor = texture2D(colormap, vec2(t, 0.5));',
        '}'
    ].join('\n');

    // Flash overlay fragment: draw ONLY the alarm regions, in alarmColor,
    // pulsing via uFlashAlpha, on top of everything (the material is set
    // up with depthTest:false). Everything below threshold is discarded.
    var flashFragment = [
        'varying vec2 vUV;',
        'varying vec4 vVals;',
        'varying float vVis;',
        'uniform float alarmThreshold;',
        'uniform vec3  alarmColor;',
        'uniform float uAbs;',
        'uniform float uFlashAlpha;',
        fieldEvalGLSL,
        'void main() {',
        '  if (vVis < 0.5) discard;',
        '  float f = fieldValue(vUV, vVals, 0.0, uAbs);',
        '  if (!(f == f)) discard;',
        '  if (!(alarmThreshold > 0.0) || f < alarmThreshold) discard;',
        '  gl_FragColor = vec4(alarmColor, uFlashAlpha);',
        '}'
    ].join('\n');

    // Edge overlay: LineBasicMaterial can't carry per-vertex displacement,
    // so edges use this pair and deform in lockstep with the mesh.
    var edgeVertex = [
        'attribute vec3 dispVec;',
        'attribute float elemVis;',
        'uniform float dispScale;',
        'varying float vVis;',
        'void main() {',
        '  vVis = elemVis;',
        '  vec3 p = position + dispScale * dispVec;',
        '  gl_Position = projectionMatrix * modelViewMatrix * vec4(p, 1.0);',
        '}'
    ].join('\n');

    var edgeFragment = [
        'uniform vec3 uColor;',
        'uniform float uOpacity;',
        'varying float vVis;',
        'void main() {',
        '  if (vVis < 0.5) discard;',
        '  gl_FragColor = vec4(uColor, uOpacity);',
        '}'
    ].join('\n');

    // Colormap anchor points, evenly spaced, [r,g,b] 0-255.
    var colormaps = {
        viridis: [
            [68, 1, 84], [72, 40, 120], [62, 74, 137], [49, 104, 142],
            [38, 130, 142], [31, 158, 137], [53, 183, 121], [145, 213, 66],
            [253, 231, 37]
        ],
        turbo: [
            [48, 18, 59], [65, 69, 171], [57, 118, 235], [34, 168, 243],
            [24, 208, 190], [96, 229, 121], [169, 237, 68], [229, 213, 40],
            [251, 156, 38], [228, 79, 17], [122, 4, 3]
        ],
        grayscale: [
            [20, 20, 22], [240, 240, 240]
        ],
        coolwarm: [
            [59, 76, 192], [108, 138, 226], [167, 187, 244], [221, 221, 221],
            [244, 178, 152], [229, 110, 86], [180, 4, 38]
        ]
    };

    // Qualitative palette for categorical views (controlling DSR check).
    // Okabe-Ito: distinguishable under common color-vision deficiencies.
    var categoricalColors = [
        [230, 159, 0], [86, 180, 233], [0, 158, 115], [240, 228, 66],
        [0, 114, 178], [213, 94, 0], [204, 121, 167], [153, 153, 153]
    ];

    // Sample an anchor list at t in [0,1] -> [r,g,b].
    function sampleAnchors(anchors, t) {
        var seg = anchors.length - 1;
        var x = Math.max(0, Math.min(1, t)) * seg;
        var lo = Math.min(Math.floor(x), seg - 1);
        var fr = x - lo;
        var a = anchors[lo], b = anchors[lo + 1];
        return [
            a[0] + (b[0] - a[0]) * fr,
            a[1] + (b[1] - a[1]) * fr,
            a[2] + (b[2] - a[2]) * fr
        ];
    }

    // Build a 256x1 RGBA DataTexture LUT for the named colormap.
    function makeColormapTexture(name) {
        var anchors = colormaps[name] || colormaps.viridis;
        var n = 256;
        var data = new Uint8Array(n * 4);
        for (var i = 0; i < n; i++) {
            var c = sampleAnchors(anchors, i / (n - 1));
            data[i * 4]     = Math.round(c[0]);
            data[i * 4 + 1] = Math.round(c[1]);
            data[i * 4 + 2] = Math.round(c[2]);
            data[i * 4 + 3] = 255;
        }
        var tex = new THREE.DataTexture(data, n, 1, THREE.RGBAFormat);
        tex.minFilter = THREE.LinearFilter;
        tex.magFilter = THREE.LinearFilter;
        tex.generateMipmaps = false;
        tex.wrapS = THREE.ClampToEdgeWrapping;
        tex.wrapT = THREE.ClampToEdgeWrapping;
        tex.needsUpdate = true;
        return tex;
    }

    // Color for category i (cycles past the palette length).
    function categoryColor(i) {
        return categoricalColors[i % categoricalColors.length];
    }

    // Discrete n-entry LUT (NearestFilter -- hard category boundaries).
    // Used with vMin=-0.5, vMax=n-0.5 so integer category k lands in the
    // center of texel k through the exact same mapping the fragment
    // shader already applies.
    // Arbitrary RGB palette ([[r,g,b],...] 0-255) as an n x 1 nearest LUT.
    function makePaletteTexture(colors) {
        var n = Math.max(colors.length, 1);
        var data = new Uint8Array(n * 4);
        for (var i = 0; i < n; i++) {
            var c = colors[i] || [200, 200, 200];
            data[i * 4] = c[0]; data[i * 4 + 1] = c[1]; data[i * 4 + 2] = c[2]; data[i * 4 + 3] = 255;
        }
        var tex = new THREE.DataTexture(data, n, 1, THREE.RGBAFormat);
        tex.minFilter = THREE.NearestFilter;
        tex.magFilter = THREE.NearestFilter;
        tex.generateMipmaps = false;
        tex.wrapS = THREE.ClampToEdgeWrapping;
        tex.wrapT = THREE.ClampToEdgeWrapping;
        tex.needsUpdate = true;
        return tex;
    }

    function makeCategoricalTexture(n) {
        var data = new Uint8Array(n * 4);
        for (var i = 0; i < n; i++) {
            var c = categoryColor(i);
            data[i * 4]     = c[0];
            data[i * 4 + 1] = c[1];
            data[i * 4 + 2] = c[2];
            data[i * 4 + 3] = 255;
        }
        var tex = new THREE.DataTexture(data, n, 1, THREE.RGBAFormat);
        tex.minFilter = THREE.NearestFilter;
        tex.magFilter = THREE.NearestFilter;
        tex.generateMipmaps = false;
        tex.wrapS = THREE.ClampToEdgeWrapping;
        tex.wrapT = THREE.ClampToEdgeWrapping;
        tex.needsUpdate = true;
        return tex;
    }


    // ---- beam (solid extruded section) shaders ---------------------
    // Linear interpolation end A -> end B along beamT; same LUT / abs /
    // alarm uniforms as the shell shader. uNeutral = 1 draws a flat
    // steel-grey (envelope / strength / DSR views have no beam data yet).
    // Mild lambert term on the flat normal so solids read as 3D.
    var beamVertex = [
        'attribute float beamT;',
        'attribute vec2 endVals;',
        'attribute vec3 dispVec;',
        'attribute float elemVis;',
        'attribute float catIdx;',
        'uniform float dispScale;',
        'varying float vT;',
        'varying vec2 vEnds;',
        'varying float vVis;',
        'varying vec3 vNrm;',
        'varying float vCat;',
        'void main() {',
        '  vT = beamT;',
        '  vCat = catIdx;',
        '  vEnds = endVals;',
        '  vVis = elemVis;',
        '  vNrm = normalize(normalMatrix * normal);',
        '  vec3 p = position + dispScale * dispVec;',
        '  gl_Position = projectionMatrix * modelViewMatrix * vec4(p, 1.0);',
        '}'
    ].join('\n');

    var beamFragment = [
        'varying float vT;',
        'varying vec2 vEnds;',
        'varying float vVis;',
        'varying vec3 vNrm;',
        'varying float vCat;',
        groupGLSL,
        'uniform sampler2D colormap;',
        'uniform float vMin;',
        'uniform float vMax;',
        'uniform float alarmThreshold;',
        'uniform vec3  alarmColor;',
        'uniform float uAbs;',
        'uniform float uNeutral;',
        'uniform vec3  neutralColor;',
        'void main() {',
        '  if (vVis < 0.5) discard;',
        '  float shade = 0.72 + 0.28 * abs(vNrm.z);',
        '  if (uGroupMode > 0.5) { gl_FragColor = vec4(groupColor(vCat) * shade, 1.0); return; }',
        '  if (uNeutral > 0.5) { gl_FragColor = vec4(neutralColor * shade, 1.0); return; }',
        '  float f = mix(vEnds.x, vEnds.y, vT);',
        '  if (uAbs > 0.5) f = abs(f);',
        '  if (!(f == f)) { gl_FragColor = vec4(vec3(0.16, 0.16, 0.18) * shade, 1.0); return; }',
        '  if (alarmThreshold > 0.0 && f >= alarmThreshold) {',
        '    gl_FragColor = vec4(alarmColor * shade, 1.0); return;',
        '  }',
        '  float denom = max(vMax - vMin, 1e-6);',
        '  float t = clamp((f - vMin) / denom, 0.0, 1.0);',
        '  gl_FragColor = vec4(texture2D(colormap, vec2(t, 0.5)).rgb * shade, 1.0);',
        '}'
    ].join('\n');

    return {
        beamVertex: beamVertex,
        beamFragment: beamFragment,
        vertex: vertex,
        fragment: fragment,
        flashFragment: flashFragment,
        edgeVertex: edgeVertex,
        edgeFragment: edgeFragment,
        colormaps: colormaps,
        names: Object.keys(colormaps),
        sampleAnchors: sampleAnchors,
        makeColormapTexture: makeColormapTexture,
        categoryColor: categoryColor,
        makeCategoricalTexture: makeCategoricalTexture,
        makePaletteTexture: makePaletteTexture,
        groupUniforms: function () {
            return {
                uGroupMode: { value: 0 },
                groupPalette: { value: makePaletteTexture([[200, 200, 200]]) },
                uGroupCount: { value: 1 }
            };
        }
    };
})();
