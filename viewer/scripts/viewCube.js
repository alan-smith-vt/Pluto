// ================================================================
// viewCube.js
// Interactive orientation cube drawn in the top-right corner.
// Click a face to fly the camera to that axis-aligned view.
//
// The cube lives on its OWN canvas + WebGLRenderer (alpha) stacked
// over the main one, rather than a scissored viewport: DOM events
// land on it directly, so hover/click/cursor need no hit-testing
// against the main canvas, and OrbitControls never sees the click.
//
// World is Y-up here (camera.up = 0,1,0), so Y is the pole: the
// gimbal nudge in _snapToFace applies to the ±Y views.
//
// The snap animates the camera POSITION only and never touches
// camera.up -- rewriting up mid-flight breaks OrbitControls'
// spherical coordinates.
// ================================================================

function ViewCube(mainCamera, mainControls, onNeedsRender) {
    this._mainCamera = mainCamera;
    this._mainControls = mainControls;
    this._onNeedsRender = onNeedsRender || function () {};
    this._animating = false;
    this._hoveredFace = null;

    var size = 224;
    this._size = size;
    var dpr = window.devicePixelRatio || 1;

    var cnv = document.createElement('canvas');
    cnv.id = 'viewCubeCanvas';
    cnv.width = size * dpr;
    cnv.height = size * dpr;
    cnv.style.width = size + 'px';
    cnv.style.height = size + 'px';
    cnv.style.position = 'fixed';
    cnv.style.top = '14px';
    cnv.style.right = '14px';
    cnv.style.cursor = 'pointer';
    cnv.style.zIndex = '13';
    document.body.appendChild(cnv);
    this._canvas = cnv;

    this._renderer = new THREE.WebGLRenderer({
        canvas: cnv, alpha: true, antialias: true });
    this._renderer.setPixelRatio(dpr);
    this._renderer.setSize(size, size);
    this._renderer.setClearColor(0x000000, 0);

    this._scene = new THREE.Scene();
    this._camera = new THREE.OrthographicCamera(-1.8, 1.8, 1.8, -1.8, 0.1, 10);
    this._camera.position.set(2, 2, 2);
    this._camera.lookAt(0, 0, 0);

    this._buildCube();

    this._raycaster = new THREE.Raycaster();
    this._mouse = new THREE.Vector2();
    var self = this;
    cnv.addEventListener('click', function (e) { self._onClick(e); });
    cnv.addEventListener('mousemove', function (e) { self._onHover(e); });
    cnv.addEventListener('mouseleave', function () { self._clearHover(); });
}

// Face `dir` is the direction the camera sits FROM the target.
// Conventional axis colors (X red, Y green, Z blue) with the minus
// faces a shade darker, so orientation reads at a glance -- this is
// the viewer's only orientation gizmo.
ViewCube.prototype._buildCube = function () {
    var faces = [
        { dir: new THREE.Vector3( 0,  1,  0), label: 'Y+', color: 0x6ee06e },
        { dir: new THREE.Vector3( 0, -1,  0), label: 'Y−', color: 0x3a8a3a },
        { dir: new THREE.Vector3( 0,  0,  1), label: 'Z+', color: 0x5f8cff },
        { dir: new THREE.Vector3( 0,  0, -1), label: 'Z−', color: 0x2f4d92 },
        { dir: new THREE.Vector3( 1,  0,  0), label: 'X+', color: 0xff5252 },
        { dir: new THREE.Vector3(-1,  0,  0), label: 'X−', color: 0x9c2f2f }
    ];
    this._faces = faces;

    // Six separate planes (not one box) so each raycasts on its own.
    // A plane's default normal is +Z; these rotations aim it at `dir`.
    var half = 0.5;
    var placement = [
        { pos: [0,  half, 0], rot: [-Math.PI / 2, 0, 0] },   // Y+
        { pos: [0, -half, 0], rot: [ Math.PI / 2, 0, 0] },   // Y−
        { pos: [0, 0,  half], rot: [0, 0, 0] },              // Z+
        { pos: [0, 0, -half], rot: [0, Math.PI, 0] },        // Z−
        { pos: [ half, 0, 0], rot: [0,  Math.PI / 2, 0] },   // X+
        { pos: [-half, 0, 0], rot: [0, -Math.PI / 2, 0] }    // X−
    ];

    this._faceMeshes = [];
    for (var i = 0; i < 6; i++) {
        var mat = new THREE.MeshBasicMaterial({
            color: faces[i].color,
            side: THREE.DoubleSide,
            transparent: true,
            opacity: 0.85
        });
        var m = new THREE.Mesh(new THREE.PlaneGeometry(1, 1), mat);
        m.position.set(placement[i].pos[0], placement[i].pos[1], placement[i].pos[2]);
        m.rotation.set(placement[i].rot[0], placement[i].rot[1], placement[i].rot[2]);
        m.userData.faceIndex = i;
        this._scene.add(m);
        this._faceMeshes.push(m);

        // Sprite labels stay screen-facing, so every face reads upright
        // whatever the orientation.
        var c = document.createElement('canvas');
        c.width = c.height = 128;
        var ctx = c.getContext('2d');
        ctx.font = 'bold 56px Segoe UI, sans-serif';
        ctx.textAlign = 'center';
        ctx.textBaseline = 'middle';
        ctx.fillStyle = '#ffffff';
        ctx.fillText(faces[i].label, 64, 66);
        var tex = new THREE.CanvasTexture(c);
        tex.minFilter = THREE.LinearFilter;
        var spr = new THREE.Sprite(new THREE.SpriteMaterial({
            map: tex, transparent: true, depthTest: false }));
        spr.position.copy(faces[i].dir).multiplyScalar(0.51);
        spr.scale.set(0.5, 0.5, 1);
        this._scene.add(spr);
    }

    var edges = new THREE.EdgesGeometry(new THREE.BoxGeometry(1, 1, 1));
    this._scene.add(new THREE.LineSegments(edges,
        new THREE.LineBasicMaterial({
            color: 0xffffff, opacity: 0.4, transparent: true })));
};

ViewCube.prototype._pick = function (e) {
    var rect = this._canvas.getBoundingClientRect();
    this._mouse.x = ((e.clientX - rect.left) / rect.width) * 2 - 1;
    this._mouse.y = -((e.clientY - rect.top) / rect.height) * 2 + 1;
    this._raycaster.setFromCamera(this._mouse, this._camera);
    var hits = this._raycaster.intersectObjects(this._faceMeshes);
    return hits.length ? hits[0].object.userData.faceIndex : null;
};

ViewCube.prototype._onClick = function (e) {
    if (this._animating) return;
    var fi = this._pick(e);
    if (fi !== null) this._snapToFace(fi);
};

ViewCube.prototype._onHover = function (e) {
    var fi = this._pick(e);
    if (fi === this._hoveredFace) return;
    if (this._hoveredFace !== null)
        this._faceMeshes[this._hoveredFace].material.opacity = 0.85;
    this._hoveredFace = fi;
    if (fi !== null) this._faceMeshes[fi].material.opacity = 1.0;
    this.render();
};

ViewCube.prototype._clearHover = function () {
    if (this._hoveredFace === null) return;
    this._faceMeshes[this._hoveredFace].material.opacity = 0.85;
    this._hoveredFace = null;
    this.render();
};

ViewCube.prototype._snapToFace = function (faceIndex) {
    var face = this._faces[faceIndex];
    var target = this._mainControls.target;
    var cam = this._mainCamera;
    var dist = cam.position.distanceTo(target) || 1;

    var toPos = face.dir.clone().multiplyScalar(dist).add(target);
    // Looking straight down the up-axis is gimbal-locked in
    // OrbitControls' spherical coords -- nudge off the pole.
    if (Math.abs(face.dir.y) > 0.9) toPos.z += dist * 0.001;

    var fromPos = cam.position.clone();
    var start = performance.now();
    var duration = 400;
    var self = this;
    this._animating = true;

    (function step() {
        var t = Math.min(1, (performance.now() - start) / duration);
        var ease = t * (2 - t);                   // ease-out quad
        self._mainCamera.position.lerpVectors(fromPos, toPos, ease);
        self._mainControls.update();
        self._onNeedsRender();
        if (t < 1) requestAnimationFrame(step);
        else self._animating = false;
    })();
};

// The viewer swaps between perspective and orthographic cameras.
ViewCube.prototype.setMainCamera = function (cam) { this._mainCamera = cam; };

// Browser zoom changes devicePixelRatio; follow it or the cube blurs.
ViewCube.prototype.updatePixelRatio = function () {
    var dpr = window.devicePixelRatio || 1;
    this._renderer.setPixelRatio(dpr);
    this._renderer.setSize(this._size, this._size);
    this.render();
};

// Mirror the main camera's orientation about the cube's own origin.
ViewCube.prototype.render = function () {
    var dir = new THREE.Vector3();
    this._mainCamera.getWorldDirection(dir);
    this._camera.position.copy(dir).multiplyScalar(-3);
    this._camera.up.copy(this._mainCamera.up);
    this._camera.lookAt(0, 0, 0);
    this._renderer.render(this._scene, this._camera);
};
