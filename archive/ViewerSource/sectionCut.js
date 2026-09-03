// --- Section Cut Functions ---
function initSectionCutResources() {
	const box = new THREE.Box3().setFromObject(mesh);
	const size = box.getSize(new THREE.Vector3());
	modelMaxDim = Math.max(size.x, size.y, size.z);
	orbGeo = new THREE.SphereGeometry(1, 24, 16) //unit sphere
	orbMats.X = new THREE.MeshPhongMaterial({color: 0xff4444, shininess: 60 }); // red
	orbMats.Y = new THREE.MeshPhongMaterial({color: 0x44ff44, shininess: 60 }); // green
	orbMats.Z = new THREE.MeshPhongMaterial({color: 0x4488ff, shininess: 60 }); // blue
	lineMats.X = new THREE.MeshPhongMaterial({color: 0xff4444, shininess: 60 }); // red
	lineMats.Y = new THREE.MeshPhongMaterial({color: 0x44ff44, shininess: 60 }); // green
	lineMats.Z = new THREE.MeshPhongMaterial({color: 0x4488ff, shininess: 60 }); // blue
	initSelectionRing();
}

function initSelectionRing() {
	var geo = new THREE.SphereGeometry(1, 24, 16);
	var mat = new THREE.MeshBasicMaterial({ color: 0xffffff, wireframe: true });
	selectionRing = new THREE.Mesh(geo, mat);
	selectionRing.visible = false;
	scene.add(selectionRing);
}

function updateSelectionRing() {
	if (!selectionRing) return;
	if (!selectedCutId || !sectionCutObjects[selectedCutId]) {
		selectionRing.visible = false;
		return;
	}
	var orb = sectionCutObjects[selectedCutId].orb;
	selectionRing.position.copy(orb.position);
	//selectionRing.scale.setScalar(modelMaxDim * 0.003 * 1.4);
	selectionRing.visible = orb.visible;
	needsRender = true;
}

function raycastMesh(event){
	if (!mesh) return null;
	const rect = renderer.domElement.getBoundingClientRect();
	const ndc = new THREE.Vector2(
		((event.clientX - rect.left) / rect.width) * 2 - 1,
		-((event.clientY - rect.top) / rect.height) * 2 + 1
	);
	
	const raycaster = new THREE.Raycaster();
	raycaster.setFromCamera(ndc, camera);
	
	const hits = raycaster.intersectObject(mesh);
	if (hits.length === 0) return null;
	
	return hits[0].point;
}

function createOrb(position, axis) {
	const orb = new THREE.Mesh(orbGeo, orbMats[axis || 'Z']); //default blue
	orb.position.copy(position);
	
	// Set size based on model size
	//orb.scale.setScalar(modelMaxDim * 0.003); //.3% of the max model dimension
		
	scene.add(orb);
	return orb;
}

function createSectionLine(position, axis, lengthInches) {
	const lineGeo = new THREE.CylinderGeometry(1, 1, lengthInches, 8);
	const line = new THREE.Mesh(lineGeo, lineMats[axis]);
	
	line.position.copy(position);
	if (axis === 'X') line.rotation.z = Math.PI / 2;
	if (axis === 'Z') line.rotation.x = Math.PI / 2;
	
	scene.add(line);
	return line;
}

function removeVisuals(id) {
	var obj = sectionCutObjects[id];
	if (!obj) return;
	if (obj.orb) { scene.remove(obj.orb); obj.orb.geometry.dispose(); }
	if (obj.line) { scene.remove(obj.line); obj.line.geometry.dispose(); }
	delete sectionCutObjects[id];
	needsRender = true;
}

function rebuildVisuals(cut) {
	removeVisuals(cut.id);
	var pt = new THREE.Vector3(cut.point[0], cut.point[1], cut.point[2]);
	var orb = createOrb(pt, cut.axis);
	var line = createSectionLine(pt, cut.axis, cut.length);
	orb.visible = cut.visible;
	line.visible = cut.visible;
	sectionCutObjects[cut.id] = { orb: orb, line: line };
	needsRender = true;
}

function updateCutScales() {
	for (var id in sectionCutObjects) {
		var obj = sectionCutObjects[id];
		if (!obj.orb || !obj.orb.visible) continue;
		
		var dist = camera.position.distanceTo(obj.orb.position);
		var s = dist > 20 ? 0.01 * dist : Math.max(0.1, 0.074 - 0.0048 * Math.log(dist));
		if (s > modelMaxDim * 0.003) { s = modelMaxDim * 0.003; }
		
		obj.orb.scale.setScalar(s);
		
		if (obj.line) {
			var cut = findCut(id);
			if (cut) {
				obj.line.scale.set(s / 3, 1, s / 3);
			}
		}
	}
	
	if (selectionRing && selectionRing.visible && selectedCutId) {
		var selOrb = sectionCutObjects[selectedCutId];
		if (selOrb && selOrb.orb) {
			selectionRing.scale.setScalar(selOrb.orb.scale.x * 1.2);
		}
	}
}
	
// =====================
// CRUD
// =====================

function findCut(id) {
	for (var i = 0; i < sectionCuts.length; i++) {
		if (sectionCuts[i].id === id) return sectionCuts[i];
	}
	return null;
}

function createSectionCut(point, axis, lengthFt) {
	var id = 'cut_' + (cutIdCounter++);
	var cut = {
		id: id,
		name: 'Cut ' + cutIdCounter,
		group: null,
		point: [point.x !== undefined ? point.x  : point[0], 
				point.y !== undefined ? point.y : point[1], 
				point.z !== undefined ? point.z : point[2]],
		axis: axis,
		length: lengthFt * 12,
		visible: true
	};
	sectionCuts.push(cut);
	rebuildVisuals(cut);
	selectCut(id);
	return cut;
}

function deleteSectionCut(id) {
	removeVisuals(id);
	sectionCuts = sectionCuts.filter(function(c) { return c.id !== id; });
	// Remove empty groups
	sectionGroups = sectionGroups.filter(function(g) {
		return sectionCuts.some(function(c) { return c.group === g.name; });
	});
	if (selectedCutId === id) {
		selectedCutId = null;
		setControlsEnabled(false);
	}
	rebuildGroupDropdown();
	rebuildCutList();
	updateSelectionRing();
}

function moveSectionCut(id, newPoint) {
	var cut = findCut(id);
	if (!cut) return;
	cut.point = [newPoint.x, newPoint.y, newPoint.z];
	rebuildVisuals(cut);
	updateSelectionRing();
	populateControls(cut);
}

// ======================
// Selection and controls
// ======================

function selectCut(id) {
	selectedCutId = id;
	var cut = findCut(id);
	if (cut) {
		updateSelectionRing();
		setControlsEnabled(true);
		populateControls(cut);
	}
	rebuildCutList();
}

function setControlsEnabled(on) {
	elBtnAdj.disabled = !on;
	elBtnDel.disabled = !on;
	elName.disabled = !on;
	elGroup.disabled = !on;
	elLength.disabled = !on;
	elPosX.disabled = !on;
	elPosY.disabled = !on;
	elPosZ.disabled = !on;
	elAxisBtns.forEach(function(b) { b.disabled = !on });
	if (!on) {
		elName.value = '';
		elLength.value = '10';
		elPosX.value = '';
		elPosY.value = '';
		elPosZ.value = '';
	}
}

function populateControls(cut) {
	elName.value = cut.name;
	elGroup.value = cut.group || '';
	elLength.value = (cut.length / 12).toFixed(1);
	elPosX.value = cut.point[0].toLocaleString(undefined, {maximumFractionDigits:1});
	elPosY.value = cut.point[1].toLocaleString(undefined, {maximumFractionDigits:1});
	elPosZ.value = cut.point[2].toLocaleString(undefined, {maximumFractionDigits:1});
	elAxisBtns.forEach(function(b) {
		b.classList.toggle('active', b.getAttribute('data-axis') === cut.axis);
	});
	currentAxis = cut.axis;
}

// Mode buttons
elBtnNew.addEventListener('click', function() {
	if (interactionMode === 'placement') {
		interactionMode = 'default';
		elBtnNew.classList.remove('active');
	} else {
		interactionMode = 'placement';
		elBtnNew.classList.add('active');
		elBtnAdj.classList.remove('active');
	}
});

elBtnAdj.addEventListener('click', function() {
	if (!selectedCutId) return;
	if (interactionMode === 'adjust') {
		interactionMode = 'default';
		elBtnAdj.classList.remove('active');
	} else {
		interactionMode = 'adjust';
		elBtnAdj.classList.add('active');
		elBtnNew.classList.remove('active');
	}
});

elBtnDel.addEventListener('click', function() {
	if (selectedCutId) deleteSectionCut(selectedCutId);
});

// Field change handlers
elName.addEventListener('change', function() {
	var cut = findCut(selectedCutId);
	if (cut) { cut.name = this.value; rebuildCutList(); }
});

elLength.addEventListener('change', function() {
	var cut = findCut(selectedCutId);
	if (cut) { cut.length = parseFloat(this.value) * 12; rebuildVisuals(cut); rebuildCutList(); }
});

elPosX.addEventListener('change', function() {applyPosChange(0, this.value); });
elPosY.addEventListener('change', function() {applyPosChange(1, this.value); });
elPosZ.addEventListener('change', function() {applyPosChange(2, this.value); });

function applyPosChange(idx, val) {
	var cut = findCut(selectedCutId);
	if (!cut) return;
	var num = parseFloat(val.replace(/,/g, ''));
	if (isNaN(num)) { populateControls(cut); return; }
	cut.point[idx] = num;
	rebuildVisuals(cut);
	updateSelectionRing();
	populateControls(cut);
}

elAxisBtns.forEach(function(btn) {
	btn.addEventListener('click', function() {
		if (this.disabled) return;
		currentAxis = this.getAttribute('data-axis');
		elAxisBtns.forEach(function(b) { b.classList.remove('active'); });
		this.classList.add('active');
		var cut = findCut(selectedCutId);
		if (cut) { cut.axis = currentAxis; rebuildVisuals(cut); rebuildCutList(); }
	});
});

// Group dropdown
elGroup.addEventListener('change', function() {
	var cut = findCut(selectedCutId);
	console.log('cut: ', cut);
	if (!cut) return;
	console.log('after return on null cut');
	console.log('this.value =', this.value);
	if (this.value === '__new__') {
		console.log('new group triggered');
		var name = prompt('New group name:');
		console.log('prompt returned:', name);
		if (!name || !name.trim()) { this.value = cut.group || ''; return; }
		name = name.trim();
		if (!sectionGroups.find(function(g) {return g.name === name; })) {
			sectionGroups.push({ name: name, visible: true, collapsed: false });
		}
		cut.group = name;
		rebuildGroupDropdown();
		this.value = name;
	} else {
		cut.group = this.value || null;
	}
	rebuildCutList();
});

function rebuildGroupDropdown() {
	var val = elGroup.value;
	elGroup.innerHTML = '<option value="">Ungrouped</option>';
	sectionGroups.forEach(function(g) {
		elGroup.innerHTML += '<option value="' + g.name + '">' + g.name + '</option>';
	});
	elGroup.innerHTML += '<option value="__new__">+ New group...</option>';
	elGroup.value = val;
}

// Visibility toggle
function toggleCutVis(id) {
	var cut = findCut(id);
	if (!cut) return;
	cut.visible = !cut.visible;
	var obj = sectionCutObjects[id];
	if (obj) {
		if (obj.orb) obj.orb.visible = cut.visible;
		if (obj.line) obj.line.visible = cut.visible;
	}
	updateSelectionRing();
	rebuildCutList();
}

function toggleGroupVis(groupName) {
	var g = sectionGroups.find(function(x) { return x.name === groupName; });
	if (!g) return;
	g.visible = !g.visible;
	sectionCuts.forEach(function(c) {
		if (c.group === groupName) {
			c.visible = g.visible;
			var obj = sectionCutObjects[c.id];
			if (obj) {
				if (obj.orb) obj.orb.visible = c.visible;
				if (obj.line) obj.line. visible = c.visible;
			}
		}
	});
	updateSelectionRing();
	rebuildCutList();
}

function toggleGroupCollapse(groupName) {
	var g = sectionGroups.find(function(x) { return x.name === groupName; });
	if (g) { g.collapsed = !g.collapsed; rebuildCutList(); }
}

// Cut list rendering
var axisColors = { X: '#ff4444', Y: '#44ff44', Z: '#4488ff' };

function rebuildCutList() {
	elList.innerHTML = '';
	
	// Grouped
	sectionGroups.forEach(function(g) {
		var groupCuts = sectionCuts.filter(function(c) { return c.group === g.name; });
		if (groupCuts.length === 0) return;
		
		var hdr = document.createElement('div');
		hdr.className = 'cut-group-header' + (g.collapsed ? '' : ' expanded');
		
		var arrow = document.createElement('span');
		arrow.className = 'cut-group-arrow';
		arrow.innerHTML = '&#9654;'; // right facing triangle
		arrow.addEventListener('click', function(e) { e.stopPropagation(); toggleGroupCollapse(g.name); });
		
		var name = document.createElement('span');
		name.className = 'cut-group-name';
		name.textContent = g.name;
		
		var count = document.createElement('span');
		count.className = 'cut-group-count';
		count.textContent = groupCuts.length;
		
		var vis = document.createElement('span');
		vis.className = 'cut-group-vis';
		vis.innerHTML = g.visible ? '&#9673;' : '&#9676;'; // Filled circle / dotted circle
		vis.addEventListener('click', function(e) { e.stopPropagation(); toggleGroupVis(g.name); });
		
		hdr.appendChild(arrow);
		hdr.appendChild(name);
		hdr.appendChild(count);
		hdr.appendChild(vis);
		hdr.addEventListener('click', function() {toggleGroupCollapse(g.name); });
		elList.appendChild(hdr);
		
		if (!g.collapsed) {
			groupCuts.forEach(function(c) { elList.appendChild(makeCutRow(c, true)); });
		}
	});
		
	// Ungrouped
	var ungrouped = sectionCuts.filter(function(c) {return !c.group; });
	if (ungrouped.length > 0 && sectionGroups.length > 0) {
		var sep = document.createElement('div');
		sep.className = 'cut-ungrouped-header';
		sep.textContent = 'Ungrouped';
		elList.appendChild(sep);
	}
	ungrouped.forEach(function(c) { elList.appendChild(makeCutRow(c, false)); });
}

function makeCutRow(cut, grouped) {
	var row = document.createElement('div');
	row.className = 'cut-row' + (grouped ? ' grouped' : '') + (cut.id == selectedCutId ? ' selected' : '');
	row.addEventListener('click', function() { selectCut(cut.id); });
	
	var dot = document.createElement('div');
	dot.className = 'cut-row-dot';
	dot.style.background = axisColors[cut.axis];
	
	var name = document.createElement('span');
	name.className = 'cut-row-name';
	name.textContent = cut.name;
	
	var info = document.createElement('span');
	info.className = 'cut-row-info';
	info.textContent = cut.axis + ' ' + (cut.length / 12).toFixed(1) + 'ft';
	
	var vis = document.createElement('span');
	vis.className = 'cut-row-vis';
	vis.innerHTML = cut.visible ? '&#9673;' : '&#9676;'; // Filled circle / dotted circle
	vis.addEventListener('click', function(e) { e.stopPropagation(); toggleCutVis(cut.id); });
	
	row.appendChild(dot);
	row.appendChild(name);
	row.appendChild(info);
	row.appendChild(vis);
	return row;
}


// Export / Import
document.getElementById('btnExport').addEventListener('click', function() {
	var data = { version: 1, groups: sectionGroups, cuts: sectionCuts };
	var blob = new Blob([JSON.stringify(data, null, 2)], {type: 'application/json' });
	var a = document.createElement('a');
	a.href = URL.createObjectURL(blob);
	a.download = 'section_cuts.json';
	a.click();
	URL.revokeObjectURL(a.href);
});

document.getElementById('btnImport').addEventListener('click', function() {
	document.getElementById('importFile').click();
});

document.getElementById('importFile').addEventListener('change', function(e) {
	var file = e.target.files[0];
	if (!file) return;
	var reader = new FileReader();
	reader.onload = function(ev) {
		try {
			var data = JSON.parse(ev.target.result);
			// Clear existing
			sectionCuts.forEach(function(c) { removeVisuals(c.id); });
			sectionCuts = [];
			sectionGroups = data.groups || [];
			selectedCutId = null;
			cutIdCounter = 0;
			// Rebuild
			(data.cuts || []).forEach(function(c) {
				cutIdCounter++;
				c.id = 'cut_' + cutIdCounter;
				sectionCuts.push(c);
				rebuildVisuals(c);
			});
			setControlsEnabled(false);
			rebuildGroupDropdown();
			rebuildCutList();
		} catch (err) {
			alert('Invalid JSON: ' + err.message);
		}
	};
	reader.readAsText(file);
	this.value = '';
});

