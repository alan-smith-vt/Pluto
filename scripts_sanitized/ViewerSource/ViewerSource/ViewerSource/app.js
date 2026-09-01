// --- Load all data files --- 
document.getElementById('fileInput').addEventListener('change', async (e) => {
	const file = e.target.files[0];
	const buffer = await file.arrayBuffer();
	model = parseViewerBinary(buffer);
	nNodes = model.nNodes;
	positions = model.positions;
	edgeIndices = model.edgeIndices;
	indices = model.triIndices;
	stresses = model.getJointStressesForLC(0, 0);
	
	// Cache plates for predicates
	buildPlateCache();
	
	// Populate dropdowns
	const modelSelect = document.getElementById('modelSelect');
	const lcSelect = document.getElementById('lcSelect');
	modelSelect.innerHTML = '';
	
	
	// Model dropdown
	model.meta.models.forEach((m,i) => {
		modelSelect.innerHTML += `<option value="${i}">${m.name}</option>`;
	});
		
	modelSelect.addEventListener('change', (e) => {
		const m = model.meta.models[e.target.value];
		lcSelect.innerHTML = '';
		m.loadCases.forEach((lc, i) => {
			lcSelect.innerHTML += `<option value="${i}">${lc}</option>`;
		});
	});
	
	info.textContent = `File loaded: ${model.nModels} models, ${model.nTotalLC} total combos, ${model.nNodes} nodes`;
	tryBuildMesh();
});

function parseViewerBinary(buffer){
	const view = new DataView(buffer);
	let o = 0;
	
	// --- Header ---
	const magic = view.getUint32(o, true); o += 4;
	if (magic != 0xFEA12345) throw new Error('Invalid file');
	const version = view.getUint32(o, true); o += 4;
	const headerSize = view.getUint32(o, true); o += 4;
	const nNodes = view.getUint32(o, true); o += 4;
	const nElements = view.getUint32(o, true); o += 4;
	const nModels = view.getUint32(o, true); o += 4;
	const nTotalLC = view.getUint32(o, true); o += 4;
	const jointComponents = view.getUint32(o, true); o += 4;
	const metaOffset = view.getUint32(o, true); o += 4;
	const metaLength = view.getUint32(o, true); o += 4;
	const nodesOffset = view.getUint32(o, true); o += 4;
	const elemsOffset = view.getUint32(o, true); o += 4;
	const jointStressOffset = view.getUint32(o, true); o += 4;

	
	// --- Metadata ---
	const metaBytes = new Uint8Array(buffer, metaOffset, metaLength);
	const meta = JSON.parse(new TextDecoder().decode(metaBytes));
	
	// --- Nodes (direct typed array view, no copy) ---
	const positions = new Float32Array(buffer, nodesOffset, nNodes * 3);
	
	// --- Elements ---
	const elements = [];
	o = elemsOffset;
	for (let i = 0; i < nElements; i++){
		const nodeCount = view.getUint8(o); o += 1;
		const n1 = view.getUint32(o, true); o += 4;
		const n2 = view.getUint32(o, true); o += 4;
		const n3 = view.getUint32(o, true); o += 4;
		const n4 = view.getUint32(o, true); o += 4;
		elements.push({ nodeCount, nodes: [n1, n2, n3, n4] });
	}
	
	// --- Build Three.js geometry ---
	const triIndices = [];
	const edgeIndices = [];
	
	for (let i = 0; i < elements.length; i++) {
		const e = elements[i];
		if (e.nodeCount >= 3){
			// First triangle: n1, n2, n3
			triIndices.push(e.nodes[0], e.nodes[1], e.nodes[2]);
			
			// Edges for tri
			edgeIndices.push(e.nodes[0], e.nodes[1]);
			edgeIndices.push(e.nodes[1], e.nodes[2]);
			
			if (e.nodeCount === 4){
				// Second triangle: n1, n3, n4
				triIndices.push(e.nodes[0], e.nodes[2], e.nodes[3]);
				
				// Quad edges (n2-n3 is diagonal, skip it)
				edgeIndices.push(e.nodes[2], e.nodes[3]);
				edgeIndices.push(e.nodes[3], e.nodes[0]);
			} else {
				// Tri closing edge
				edgeIndices.push(e.nodes[2], e.nodes[0]);
			}
		}
	}
	
	// --- Stress access helpers ---
	const jointStressArray = new Float32Array(buffer, jointStressOffset,
		nTotalLC * nNodes * jointComponents);
		
	function getJointStressesForLC(modelIndex, lcIndex) {
		const lcGlobal = meta.models[modelIndex].stressStartIndex + lcIndex;
		const start = lcGlobal * nNodes * jointComponents;
		return jointStressArray.subarray(start, start + nNodes * jointComponents);
	}
	
	function getJointStress(modelIndex, lcIndex, nodeIndex, component) {
		const lcGlobal = meta.models[modelIndex].stressStartIndex + lcIndex;
		return jointStressArray[lcGlobal * nNodes * jointComponents
			+ nodeIndex * jointComponents + component];
	}
	
	// --- Build displacement morph array for a given LC ---
	function getDispArray(modelIndex, lcIndex) {
		const stresses = getJointStressesForLC(modelIndex, lcIndex);
		const dispArray = new Float32Array(nNodes * 3);
		for (let i = 0; i < nNodes; i++){
			const base = i * jointComponents;
			const dx = stresses[base + 8];
			const dy = stresses[base + 9];
			const dz = stresses[base + 10];
		}
		// Replace NaN with 0 for morph targets
		dispArray[i * 3]	= isNaN(dx) ? 0 : dx;
		dispArray[i * 3 + 1]= isNaN(dy) ? 0 : dy;
		dispArray[i * 3 + 2]= isNaN(dz) ? 0 : dz;
		
		return dispArray;
	}
	
	return {
		// Header info
		nNodes,
		nElements,
		nModels,
		nTotalLC,
		jointComponents,
		
		// Metadata
		meta,
		
		// Geometry (ready for Three.js)
		positions,
		elements,
		triIndices,
		edgeIndices,
		
		// Stress access
		getJointStressesForLC,
		getJointStress,
		getDispArray,
		
		// Debugging
		elemsOffset,
		buffer
	};
}

function buildPlateCache() {
	if (!model) return;
	if (!model.elements) return;
	
	var N = model.elements.length;
	plateCg = new Float32Array(N * 3);
	plateNm = new Float32Array(N * 3);
	
	for (var i = 0; i < N; i++)
	{
		var el = model.elements[i];
		var nc = el.nodeCount, nodes = el.nodes;
		
		// Per-node base offsets into the packed positions array
		var b0 = nodes[0] * 3;
		var b1 = nodes[1] * 3;
		var b2 = nodes[2] * 3;
		
		var p0x = positions[b0], p0y = positions[b0+1], p0z = positions[b0+2];
		var p1x = positions[b1], p1y = positions[b1+1], p1z = positions[b1+2];
		var p2x = positions[b2], p2y = positions[b2+1], p2z = positions[b2+2];
		
		var cx, cy, cz, nx, ny, nz;
		
		if (nc === 3) {
			cx = (p0x + p1x + p2x) / 3;
			cy = (p0y + p1y + p2y) / 3;
			cz = (p0z + p1z + p2z) / 3;
			// (p1-p0) x (p2-p1)
			var ax = p1x-p0x, ay = p1y-p0y, az = p1z-p0z;
			var bx = p2x-p0x, by = p2y-p0y, bz = p2z-p0z;
			nx = ay*bz - az*by;
			ny = az*bx - ax*bz;
			nz = ax*by - ay*bx;
		} else {
			var b3 = nodes[3] * 3;
			var p3x = positions[b3], p3y = positions[b3+1], p3z = positions[b3+2];
			
			cx = (p0x + p1x + p2x + p3x) / 4;
			cy = (p0y + p1y + p2y + p3y) / 4;
			cz = (p0z + p1z + p2z + p3z) / 4;
			// (p2-p0) x (p3-p1) - diagonal cross, robust for warped quads
			var dx1 = p2x-p0x, dy1 = p2y-p0y, dz1 = p2z-p0z;
			var dx2 = p3x-p1x, dy2 = p3y-p1y, dz2 = p3z-p1z;
			nx = dy1*dz2 - dz1*dy2;
			ny = dz1*dx2 - dx1*dz2;
			nz = dx1*dy2 - dy1*dx2;
		}
		
		var len = Math.sqrt(nx*nx + ny*ny + nz*nz);
		if (len > 1e-12) { nx /= len; ny /= len; nz /= len; }
		
		var k = i * 3;
		plateCg[k] = cx; plateCg[k+1] = cy; plateCg[k+2] = cz;
		plateNm[k] = nx; plateNm[k+1] = ny; plateNm[k+2] = nz;
	}
}

// --- Build mesh when both geometry files are ready ---
function buildCubeDebug(){
	// 8 verticies of a unit
	positions = new Float32Array([
		0,0,0, 1,0,0, 1,1,0, 0,1,0, // front face
		0,0,1, 1,0,1, 1,1,1, 0,1,1 // back face
	]);
	
	// 12 triangles (2 per face)
	indices = new Uint32Array([
		0,1,2, 0,2,3, //front
		1,5,6, 1,6,2, //right
		5,4,7, 5,7,6, //back
		4,0,3, 4,3,7, //left
		3,2,6, 3,6,7, //top
		4,5,1, 4,1,0 //bottom
	]);
	
	//All white
	colors = new Float32Array(8*3).fill(1.0);
	
	const geo = new THREE.BufferGeometry();
	geo.setAttribute('position', new THREE.BufferAttribute(positions, 3));
	geo.setAttribute('color', new THREE.BufferAttribute(colors, 3));
	geo.setIndex(new THREE.BufferAttribute(indices, 1));
	geo.computeVertexNormals();
	
	const mat = new THREE.MeshStandardMaterial({
		vertexColors: true,
		side: THREE.DoubleSide,
		flatShading: true,
		roughness: 0.7,
		metalness: 0.1
	});
	
	mesh = new THREE.Mesh(geo, mat);
	
	const edges = new THREE.EdgesGeometry(geo);
	const lineMat = new THREE.LineBasicMaterial({ color: 0x000000});
	const wireframe = new THREE.LineSegments(edges, lineMat);
	mesh.add(wireframe);
	
	scene.add(mesh);
	
	geo.computeBoundingSphere();
	const center = geo.boundingSphere.center;
	const radius = geo.boundingSphere.radius;
	controls.target.copy(center);
	camera.position.set(
		center.x + radius * 1.5,
		center.y + radius * 1.5,
		center.z + radius * 1.5,
	);
	camera.far = Math.max(1000, radius * 10);
	camera.updateProjectionMatrix();
	controls.update();
}

function tryBuildMesh() {
	if (!positions || !indices) return;
	
	if (mesh){
		mesh.geometry.dispose();
		mesh.material.dispose();
		scene.remove(mesh);
	}
	
	const geo = new THREE.BufferGeometry();
	colors = new Float32Array(nNodes*3).fill(1.0);
	geo.setAttribute('position', new THREE.BufferAttribute(positions, 3));
	geo.setAttribute('color', new THREE.BufferAttribute(colors, 3));
	geo.setIndex(indices);
	geo.computeVertexNormals();
	
	const mat = new THREE.MeshStandardMaterial({
		vertexColors: true,
		side: THREE.DoubleSide,
		roughness: 0.7,
		metalness: 0.1,
		flatShading: true
	});
	
	mesh = new THREE.Mesh(geo, mat);
	
	if (edgeIndices){
		edgeGeo = new THREE.BufferGeometry();
		edgeGeo.setAttribute('position', geo.attributes.position);
		edgeGeo.setIndex(edgeIndices);
		wireframeObj = new THREE.LineSegments(edgeGeo, new THREE.LineBasicMaterial({ 
			color: 0x000000,
			depthTest: true, // Change to false for xray view
		}));
		wireframeObj.rederOrder = 0; //change to false for xray view
		mesh.add(wireframeObj);
		staticWireframe = wireframeObj.clone();
		staticWireframe.material = wireframeObj.material.clone();
		staticWireframe.material.color.set(0x888888);
		staticWireframe.material.transparent = true;
		staticWireframe.material.opacity = 0.15;
		scene.add(staticWireframe);
		edgeGeo.computeBoundingSphere();
	}
	
	scene.add(mesh);
	
	// Auto-fit camera
	geo.computeBoundingSphere();
	const center = geo.boundingSphere.center;	
	const radius = geo.boundingSphere.radius;
	
	controls.target.copy(center);
	camera.position.set(
		center.x + radius * 1.5,
		center.y + radius * 1.5,
		center.z + radius * 1.5,
	);
	camera.far = Math.max(1000, radius * 10);
	camera.updateProjectionMatrix();
	controls.update();
	modelSelect.dispatchEvent(new Event('change'));
	
	// --- Axes modeled off to the side of the model ---
	const box = new THREE.Box3().setFromObject(mesh);
	const centerBox = box.getCenter(new THREE.Vector3());
	const size = box.getSize(new THREE.Vector3());
	const maxDim = Math.max(size.x, size.y, size.z);

	const axes = new THREE.AxesHelper(maxDim * 0.15);
	axes.position.set(
		centerBox.x - size.x * 0.7,
		centerBox.y - size.y * 0.7,
		centerBox.z - size.z * 0.7
	);
	scene.add(axes);
	
	const axisLen = maxDim * 0.15;
	const axisOrigin = axes.position;
	
	scene.add(makeAxisLabel('X (N)', '#ff0000', new THREE.Vector3(axisOrigin.x + axisLen * 1.15, axisOrigin.y, axisOrigin.z), maxDim));
	scene.add(makeAxisLabel('Y', '#00ff00', new THREE.Vector3(axisOrigin.x, axisOrigin.y + axisLen * 1.15, axisOrigin.z), maxDim));
	scene.add(makeAxisLabel('Z (E)', '#0000ff', new THREE.Vector3(axisOrigin.x, axisOrigin.y, axisOrigin.z + axisLen * 1.15), maxDim));
	
	if (stresses) updateColors();
	initSectionCutResources();
	initTargetOrb();
}

function makeAxisLabel(text, color, position, maxDim) {
	const canvas = document.createElement('canvas');
	canvas.width = 192;
	canvas.height = 64;
	const ctx = canvas.getContext('2d');
	ctx.fillStyle = color;
	ctx.font = 'bold 48px Arial';
	ctx.textAlign = 'center';
	ctx.textBaseline = 'middle';
	ctx.fillText(text, 96, 32);
	
	const texture = new THREE.CanvasTexture(canvas);
	const material = new THREE.SpriteMaterial({ map: texture });
	const sprite = new THREE.Sprite(material);
	sprite.position.copy(position);
	sprite.scale.set(maxDim * 0.08, maxDim * 0.02, 1);
	return sprite;
}

// --- Color mapping ---
function updateColors(){
	const modelIdx = parseInt(document.getElementById('modelSelect').value) || 0;
	const comboIdx = parseInt(document.getElementById('lcSelect').value) || 0;
	const comp = parseInt(document.getElementById('compSelect').value) || 0;
	const clipPct = parseFloat(document.getElementById('clipPct').value) / 100;
	
	const envIdx = model.meta.models[modelIdx].loadCases.length - 1;
	stresses = model.getJointStressesForLC(modelIdx, comboIdx);
	let min = Infinity, max = -Infinity;
	let clipMin = 0, clipMax = 0;
	if (comp < 8) {
		// Stress: symmetric range from absMax envelope
		const envStresses = model.getJointStressesForLC(modelIdx, envIdx);
		for (let n = 0; n < nNodes; n++){
			const offset = n * model.jointComponents + comp;
			const v = envStresses[offset];
			if (v < min) min = v;
			if (v > max) max = v;
		}
		let absMax = Math.max(Math.abs(min), Math.abs(max));
		
		// Symmetric clip: reduce absMax
		absMax = absMax * clipPct || 1;
		clipMin = -absMax;
		clipMax = absMax;
		
	}
		
	// Compute actual min/max for displaying
	min = Infinity, max = -Infinity;
	for (let n = 0; n < nNodes; n++){
		const offset = n * model.jointComponents + comp;
		const v = stresses[offset];
		if (v < min) min = v;
		if (v > max) max = v;
	}
	
	// Displacement: use actual min/max of selected LC, no symmetry
	if (comp >= 8) {	
		// Asymmetric clip: shrink both ends equally 
		const span = max - min || 1;
		const mid = (max + min)/2;
		clipMin = mid - span * clipPct * 0.5;
		clipMax = mid + span * clipPct * 0.5;
	}
	
	// Second pass to actually compute the colors	
	for (let n = 0; n < nNodes; n++){
		const offset = n * model.jointComponents + comp;
		const v = stresses[offset];
		if (isNaN(v)){
			colors[n * 3]		= 0.7;
			colors[n * 3 + 1]	= 0.7;
			colors[n * 3 + 2]	= 0.7;
		} else {
			let t;
			if (comp < 8) {
				t = Math.min(1, Math.max(0, (v + clipMax) / (2 * clipMax)));
			} else {
				t = Math.min(1, Math.max(0, (v - clipMin) / (max - clipMin)));
			}
			const [r, g, b] = getJetColor(t);
			colors[n * 3]		= r;
			colors[n * 3 + 1]	= g;
			colors[n * 3 + 2]	= b;
		}
	}
	
	if (mesh) {
		mesh.geometry.attributes.color.needsUpdate =true;
		updateDisplacements();
		slider.dispatchEvent(new Event('input'));
	}
	
	info.textContent = `S${comp + 1} | Min: ${min.toFixed(2)} | Max: ${max.toFixed(2)} 
		| Clip: ${clipMin.toFixed(2)} to ${clipMax.toFixed(2)}`;
	needsRender = true;
}

function updateDisplacements(){
	const modelIdx = parseInt(document.getElementById('modelSelect').value) || 0;
	const comboIdx = parseInt(document.getElementById('lcSelect').value) || 0;
	dispArray = new Float32Array(nNodes * 3);
	wireDispArray = new Float32Array(edgeIndices.length * 3);
	
	let comp = 8; //XTr
	stresses = model.getJointStressesForLC(modelIdx, comboIdx);
	for (let n = 0; n < nNodes; n++){
		const offset = n * model.jointComponents + comp;
		dispArray[n * 3]	 = stresses[offset];
		dispArray[n * 3 + 1] = stresses[offset+1];
		dispArray[n * 3 + 2] = stresses[offset+2];
	}
	
	for (let i = 0; i < edgeIndices.length; i++){
		const nodeIdx = edgeIndices[i];
		wireDispArray[i * 3]	 = dispArray[nodeIdx * 3];
		wireDispArray[i * 3 + 1] = dispArray[nodeIdx * 3 + 1];
		wireDispArray[i * 3 + 2] = dispArray[nodeIdx * 3 + 2];
	}
	
	mesh.geometry.morphAttributes.position = [new THREE.Float32BufferAttribute(dispArray,3)];
	mesh.material.morphTargets = true;
	mesh.material.needsUpdate = true;
	mesh.geometry.morphTargetsRelative = true;
	
	mesh.children[0].geometry.morphAttributes.position = [new THREE.Float32BufferAttribute(dispArray,3)];
	mesh.children[0].material.morphTargets = true;
	mesh.children[0].material.needsUpdate = true;
	mesh.children[0].geometry.morphTargetsRelative = true;
	
	mesh.children[0].updateMorphTargets();
	mesh.updateMorphTargets();
	needsRender = true;
}

function getJetColor(t){
	let r = Math.min(1.0, Math.max(0.0, 1.5 - Math.abs(t - 0.75) * 4))
	let g = Math.min(1.0, Math.max(0.0, 1.5 - Math.abs(t - 0.5) * 4))
	let b = Math.min(1.0, Math.max(0.0, 1.5 - Math.abs(t - 0.25) * 4))
	return [r, g, b]
}

function updateDispAnimation(){
	if (!animating) return;	
	let v = parseFloat(slider.value) + direction*slider.max/30;
	if (v > slider.max || v <= slider.min) direction *=-1;
	slider.value = v;
	slider.dispatchEvent(new Event('input'));
	needsRender = true;
}

// ========================
// Target Orb Movement
// ========================

function initTargetOrb(){
	var geo = new THREE.SphereGeometry(1, 24, 16);
	var mat = new THREE.MeshPhongMaterial({ color: 0xff3333, shininess: 60 });
	targetOrb = new THREE.Mesh(geo, mat);
	targetOrb.visible = false;
	scene.add(targetOrb);
}

function updateTargetOrb(){
	if (!targetOrb || !targetOrb.visible) return;
	var dist =  camera.position.distanceTo(controls.target)
	if (dist < 0.03) {
		targetOrb.visible = false;
	} else {
		var s = dist > 20 ? 0.01 * dist : Math.max(0.1, 0.074 - 0.048 * Math.log(dist));
		targetOrb.scale.setScalar(s);
	}
}

controls.addEventListener('start', function() {
	showTargetOrb();
});

controls.addEventListener('end', function() {
	hideTargetOrb(600); // 600ms
});

function showTargetOrb() {
	if (!targetOrb) return;
	targetOrb.position.copy(controls.target);
	targetOrb.visible = true;
	if (targetOrbTimeout) { clearTimeout(targetOrbTimeout); targetOrbTimeout = null; }
}

function hideTargetOrb(delay) {
	if (targetOrbTimeout) clearTimeout(targetOrbTimeout);
	targetOrbTimeout = setTimeout(function() {
		if (targetOrb) targetOrb.visible = false;
	}, delay);
}
	
function startPickAnim(target) {
	pickAnim = {
		fromTarget: controls.target.clone(),
		toTarget: target,
		start: performance.now(),
		duration: 200
	};
	showTargetOrb();
	hideTargetOrb(1200);
}

function updatePickAnim() {
	if (!pickAnim) return;
	var t = Math.min(1, (performance.now() - pickAnim.start) / pickAnim.duration);
	var ease = t * (2 - t);
	controls.target.lerpVectors(pickAnim.fromTarget, pickAnim.toTarget, ease);
	targetOrb.position.copy(controls.target);
	if (t >= 1) pickAnim = null;
}

// Click handler
renderer.domElement.addEventListener('click', function(e) {
	if (!e.ctrlKey) return;
	if (interactionMode === 'placement') {
		var pt = raycastMesh(e);
		if (!pt) return;
		var ft = parseFloat(elLength.value) || 10;
		createSectionCut(pt, currentAxis, ft);
		interactionMode = 'default';
		elBtnNew.classList.remove('active');
		
	} else if (interactionMode === 'adjust' && selectedCutId) {
		var pt2 = raycastMesh(e);
		if (pt2) moveSectionCut(selectedCutId, pt2);
		
	} else if (interactionMode === 'pred_placement') {
		var hit = raycastMeshWithNormal(e);
		if (!hit) return;
		createPredicate(hit.point, hit.normal);
		interactionMode = 'default';
		clearPredModes();
		
	} else if (interactionMode === 'pred_adjust' && selectedNodeIds.length === 1) {
		var hit = raycastMeshWithNormal(e);
		if (hit) moveLeaf(selectedNodeIds[0], hit.point, hit.normal);
	} else { // Default: move target orb
		var pt3 = raycastMesh(e);
		if (pt3) startPickAnim(pt3);
	}
});

// Escape - Reset interaction mode and un-toggle new/adjust buttons
renderer.domElement.addEventListener('keydown', function(e) {
	if (e.ctrlKey || e.metaKey) {
		if (e.key === 'z' && !e.shiftKey) { e.preventDefault(); undo(); return; }
		if (e.key === 'Z' || e.key === 'y') { e.preventDefault(); redo(); return; }
	}
	if (e.key === 'Escape') {
		interactionMode = 'default';
		elBtnNew.classList.remove('active');
		elBtnAdj.classList.remove('active');
		clearPredModes();
		console.log(`Mode changed to ${interactionMode}`);
	}
});

// --- Dropdown / checkbox listeners ---
document.getElementById('modelSelect').addEventListener('change', updateColors);
document.getElementById('lcSelect').addEventListener('change', updateColors);
document.getElementById('compSelect').addEventListener('change', updateColors);
document.getElementById('clipPct').addEventListener('input', function() {
	document.getElementById('clipLabel').textContent = this.value + '%';
	updateColors();
});
slider.addEventListener('input', function() {
	document.getElementById('dispLabel').textContent = parseFloat(this.value).toFixed(1) + 'x';
	const t = parseFloat(this.value);
	mesh.morphTargetInfluences[0] = t;
	mesh.children[0].morphTargetInfluences[0] = t;
	needsRender = true;
});
playBtn.addEventListener('click', () => {
	animating = !animating;
	playBtn.innerHTML = animating ? '&#9208;' : '&#9654;';
});
document.getElementById('sliderMax').addEventListener('change', function() {
	const max = parseFloat(this.value);
	slider.value = 0;
	slider.max = max;
	slider.min = -max;
	document.getElementById('dispLabel').textContent = 0 + 'x';
	
	mesh.morphTargetInfluences[0] = 0;
	mesh.children[0].morphTargetInfluences[0] = 0;
});

elTab.addEventListener('click', function() {
	elPanel.classList.toggle('collapsed');
	elTab.style.right = elPanel.classList.contains('collapsed') ? '0px' : (280) + 'px';
});

elPredTab.addEventListener('click', function() {
	elPredPanel.classList.toggle('collapsed');
	elPredTab.style.right = elPredPanel.classList.contains('collapsed') ? '0px' : PD_WIDTH + 'px';
});

controls.addEventListener('start', () => {
	if (endTimeout) {
		clearTimeout(endTimeout);
		endTimeout = null;
		return; // already at 0.5, don't reset
	}
	renderer.setPixelRatio(1.0);
	needsRender = true;
});

controls.addEventListener('change', () => {
	needsRender = true;
});

controls.addEventListener('end', () => {
	endTimeout = setTimeout(() => {
		renderer.setPixelRatio(1.0);
		needsRender = true;
		endTimeout = null;
	}, 500);
});

function cameraSettled() {
	const posDelta = camera.position.distanceToSquared(lastCameraState.pos);
	const targetDelta = controls.target.distanceToSquared(lastCameraState.target);
	// threshold in world units squared
	const threshold = 1;
	return posDelta < threshold && targetDelta < threshold;
}

// --- Render loop ---
function animate() {
	requestAnimationFrame(animate);
	updatePickAnim();
	updateTargetOrb();
	updateCutScales();
	
	controls.update();
	updateDispAnimation();
	
	// Avoid static renders
	if (needsRender) {
		renderer.render(scene, camera);
		
		// check if we've settled
		if (cameraSettled()) {
			needsRender = false;
		}
		
		lastCameraState.pos.copy(camera.position);
		lastCameraState.target.copy(controls.target);
	}
}
//buildCubeDebug();
animate();

// --- Handle resize ---
function onResize() {
	camera.aspect = innerWidth / innerHeight;
	camera.updateProjectionMatrix();
	renderer.setSize(innerWidth, innerHeight);
	needsRender = true;
}
window.addEventListener('resize',onResize);
onResize();