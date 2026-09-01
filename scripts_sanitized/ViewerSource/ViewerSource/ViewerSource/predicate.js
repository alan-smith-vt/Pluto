// Cross panel mode clearing
function clearPredModes() {
	interactionMode = 'default';
	elBtnNewPred.classList.remove('active');
	elBtnAdjPred.classList.remove('active');
}

// Helpers 
function round6(v) { return parseFloat(v.toFixed(6)); }
function newNodeId() { return 'n' + (nodeIdCounter++); }
function newExprId() { return 'e' + (exprIdCounter++); }
function plateCount() { return plateCg.length / 3; }
function nodeCountAll() { return positions.length / 3; }

// In-plane axes derivation
function buildPredAxes(leaf) {
	var nx = leaf.normal[0], ny = leaf.normal[1], nz = leaf.normal[2];
	
	// Pick the world axis least aligned with the normal as the reference
	var ax = Math.abs(nx), ay = Math.abs(ny), az = Math.abs(nz);
	var rx, ry, rz;
	if (ax <= ay && ax <= az)	{ rx = 1; ry = 0; rz = 0; }
	else if (ay <= az)			{ rx = 0; ry = 1; rz = 0; }
	else						{ rx = 0; ry = 0; rz = 1; }
	
	// u0 = ref - (ref*n)*n, then normalize
	var d = rx*nx + ry*ny + rz*nz;
	var u0x = rx - d*nx, u0y = ry - d*ny, u0z = rz - d*nz;
	var ulen = Math.sqrt(u0x*u0x + u0y*u0y + u0z*u0z);
	u0x /= ulen; u0y /= ulen; u0z /= ulen;
	
	// Rotate u0 about n by angle_deg
	var theta = (leaf.angle_deg || 0) * Math.PI / 180;
	var c = Math.cos(theta), s = Math.sin(theta);
	var cx = ny*u0z - nz*u0y;
	var cy = nz*u0x - nx*u0z;
	var cz = nx*u0y - ny*u0x;
	
	var ux = u0x*c + cx*s, uy = u0y*c + cy*s, uz = u0z*c + cz*s;
	leaf.uAxis = [ux, uy, uz];
	leaf.vAxis = [
		ny*uz - nz*uy, 
		nz*ux - nx*uz, 
		nx*uy - ny*ux];
}

// Raycast (returns hit point + world space normal)
function raycastMeshWithNormal(event) {
	if (!mesh) return null;
	var rect = renderer.domElement.getBoundingClientRect();
	var ndc = new THREE.Vector2(
		((event.clientX - rect.left) / rect.width) * 2 - 1,
		-((event.clientY - rect.top) / rect.height) * 2 + 1
	);
	var rc = new THREE.Raycaster();
	rc.setFromCamera(ndc, camera);
	var hits = rc.intersectObject(mesh);
	if (hits.length === 0) return null;
	var hit = hits[0];
	var nm = new THREE.Matrix3().getNormalMatrix(mesh.matrixWorld);
	var worldNormal = hit.face.normal.clone().applyMatrix3(nm).normalize();
	return { point: hit.point, normal: worldNormal };
}

// Node Factories
function makeLeafNode(point, normal) {
	var node = {
		id: newNodeId(),
		kind: 'leaf',
		negated: false,
		point: [point.x, point.y, point.z],
		normal: [round6(normal.x), round6(normal.y), round6(normal.z)],
		tol: lastLeafDefaults.tol,
		normal_tol_deg: lastLeafDefaults.normal_tol_deg,
		finite: lastLeafDefaults.finite,
		width: lastLeafDefaults.width,
		length: lastLeafDefaults.length,
		angle_deg: lastLeafDefaults.angle_deg,
		count: 0,
		matches: []
	};
	node.cosNormTol = Math.cos(node.normal_tol_deg * Math.PI / 180);
	buildPredAxes(node);
	return node;
}

function makeOpNode(opKind, children) {
	return {
		id: newNodeId(),
		kind: 'op',
		op: opKind,	//and/or
		negated: false,
		children: children || [],
		collapsed: false,
		count: 0,
		matches: []
	};
}

function makeExpression(rootNode, name, type) {
	return {
		id: newExprId(),
		name: name || ('Predicate ' + (expressions.length + 1)),
		type: type || 'plates',
		root: rootNode,
		collapsed: false
	};
}

// Tree traversal
function findNodeById(id) {
	for (var i = 0; i < expressions.length; i++) {
		var hit = walkFind(expressions[i].root, id);
		if (hit) return { node: hit.node, expr: expressions[i] };
	}
	return null;
}

function walkFind(node, id) {
	if (node.id === id) return { node: node };
	if (node.kind === 'op') {
		for (var i = 0; i < node.children.length; i++) {
			var h = walkFind(node.children[i], id);
			if (h) return h;
		}
	}
	return null;
}

function findParentOf(id) {
	for (var i = 0; i < expressions.length; i++) {
		var hit = walkParent(expressions[i].root, null, id);
		if (hit) return hit; 	// { parent }; parent is null when id is the root
	}
	return null;
}

function walkParent(node, parent, targetId) {
	if (node.id === targetId) return { parent: parent };
	if (node.kind === 'op') {
		for (var i = 0; i < node.children.length; i++) {
			var h = walkParent(node.children[i], node, targetId);
			if (h) return h;
		}
	}
	return null;
}

function findExprForNode(id) {
	var hit = findNodeById(id); return hit ? hit.expr : null;
}

// Evaluation - recursive over the tree
function evaluateExpression(expr) {
	evaluateNode(expr.root, expr.type);
}

function evaluateNode(node, type) {
	if (node.kind === 'leaf') {
		evaluateLeaf(node, type);
		return;
	}
	// op node: evaluate children first
	node.children.forEach(function(c) { evaluateNode(c, type); });
	var combined;
	if (node.op === 'and') {
		combined = intersectChildren(node.children, type);
	} else {
		combined = unionChildren(node.children, type);
	}
	node.matches = combined;
	node.count = combined.length;
}

function effectiveSet(node, type) {
	if (!node.negated) return new Set(node.matches);
	// negated: universe minus matches (CONSIDER RESTRICTING 'UNIVERSE' TO THE INFINITE PLANE)
	var universeSize = type === 'plates' ? plateCount() : nodeCountAll();
	var matched = new Set(node.matches);
	var result = new Set();
	for (var i = 0; i < universeSize; i++) {
		if (!matched.has(i)) result.add(i);
	}
	return result;
}

function intersectChildren(children, type) {
	if (children.length === 0) return [];
	var sets = children.map(function(c) { return effectiveSet(c, type); });
	sets.sort(function(a, b) { return a.size - b.size; });
	var out = [];
	sets[0].forEach(function(idx) {
		var inAll = true;
		for (var i = 1; i < sets.length; i++) {
			if (!sets[i].has(idx)) { inAll = false; break; }
		}
		if (inAll) out.push(idx);
	});
	return out;
}

function unionChildren(children, type) {
	var combined = new Set();
	children.forEach(function(c) {
		effectiveSet(c, type).forEach(function(idx) { combined.add(idx); });
	});
	return Array.from(combined);
}

function evaluateLeaf(leaf, type) {
	var matches = [];
	var px = leaf.point[0], py = leaf.point[1], pz = leaf.point[2];
	var nx = leaf.normal[0], ny = leaf.normal[1], nz = leaf.normal[2];
	var tol = leaf.tol, cosTol = leaf.cosNormTol;
	
	var finite = leaf.finite;
	var halfW = 0, halfL = 0, ux = 0, uy = 0, uz = 0, vx = 0, vy = 0, vz = 0;
	if (finite) {
		halfW = leaf.width / 2;
		halfL = leaf.length/ 2;
		if (!isFinite(halfW) || !isFinite(halfL) || halfW <= 0 || halfL <= 0) {
			console.warn('finite leaf has invalid extensts', leaf);
			finite = false;
		} else {
			ux = leaf.uAxis[0]; uy = leaf.uAxis[1]; uz = leaf.uAxis[2];
			vx = leaf.vAxis[0]; vy = leaf.vAxis[1]; vz = leaf.vAxis[2];
		}
	}
	
	if (type === 'plates') {
		var N = plateCg.length; // N = elementCount * 3
		for (var k = 0; k < N; k += 3) {
			var dx = plateCg[k]		- px;
			var dy = plateCg[k+1]	- py;
			var dz = plateCg[k+2]	- pz;
			var dist = dx*nx + dy*ny + dz*nz;
			if (dist < 0) dist = -dist;
			if (dist > tol) continue;
			
			var dot = plateNm[k]*nx + plateNm[k+1]*ny + plateNm[k+2]*nz;
			if (dot < 0) dot = -dot;
			if (dot < cosTol) continue;
			
			if (finite) {
				var u = dx*ux + dy*uy + dz*uz;
				if (u < -halfW || u > halfW) continue;
				var v = dx*vx + dy*vy + dz*vz;
				if (v < -halfL || v > halfL) continue;
			}
			
			matches.push(k / 3);	// plate index
		}
	} else {
		// positions is packed: stride 3, length = nodeCount * 3
		var M = positions.length;
		for (var k = 0; k < M; k += 3) {
			var dx = positions[k]	- px;
			var dy = positions[k+1]	- py;
			var dz = positions[k+2]	- pz;
			var dist = dx*nx + dy*ny + dz*nz;
			if (dist < 0) dist = -dist;
			if (dist > tol) continue;	// node index
			
			if (finite) {
				var u = dx*ux + dy*uy + dz*uz;
				if (u < -halfW || u > halfW) continue;
				var v = dx*vx + dy*vy + dz*vz;
				if (v < -halfL || v > halfL) continue;
			}
			
			matches.push(k / 3);
		}
	}
	leaf.matches = matches;
	leaf.count = matches.length;
}

// Mutations
function createPredicate(point, normal) {
	var leaf = makeLeafNode(point, normal);
	
	if (selectedNodeIds.length === 1) {
		var sel = findNodeById(selectedNodeIds[0]);
		if (sel && sel.node.kind === 'op') {
			sel.node.children.push(leaf);
			evaluateExpression(sel.expr);
			refreshAll();
			return leaf;
		}
	}
	
	// Existing behavior - new top-level expression, select the new leaf
	var expr = makeExpression(leaf);
	expressions.push(expr);
	evaluateExpression(expr);
	selectedNodeIds = [leaf.id];
	refreshAll();
	return expr;
}

function moveLeaf(id, point, normal) {
	var hit = findNodeById(id);
	if (!hit || hit.node.kind !== 'leaf') return;
	var leaf = hit.node; 
	leaf.point = [point.x, point.y, point.z];
	leaf.normal = [round6(normal.x), round6(normal.y), round6(normal.z)];
	buildPredAxes(leaf);
	evaluateExpression(hit.expr);
	refreshAll();
}

function deleteSelectedNodes() {
	if (selectedNodeIds.length === 0) return;
	var ids = selectedNodeIds.slice();
	var affectedExprs = new Set();
	ids.forEach(function(id) {
		var hit = findNodeById(id); if (!hit) return;
		if (hit.expr.root.id === id) {
			expressions = expressions.filter(function(e) {return e.id !== hit.expr.id; });
		} else {
			var p = findParentOf(id);
			if (p && p.parent) {
				p.parent.children = p.parent.children.filter(function(c) { return c.id !== id; });
				affectedExprs.add(hit.expr.id);
			}
		}
	});
	affectedExprs.forEach(function(eid) {
		var expr = expressions.find(function(e) {return e.id === eid; });
		if (expr) evaluateExpression(expr);
	});
	selectedNodeIds = [];
	refreshAll();
}

function toggleNegateSelected() {
	if (selectedNodeIds.length === 0) return;
	var affectedExprs = new Set();
	selectedNodeIds.forEach(function(id) {
		var hit = findNodeById(id);
		if (hit) { hit.node.negated = !hit.node.negated; affectedExprs.add(hit.expr.id); }
	});
	affectedExprs.forEach(function(eid) {
		var expr = expressions.find(function(e) { return e.id === eid; });
		if (expr) evaluateExpression(expr);
	});
	refreshAll();
}

function groupSelectionInto(opKind) {
	if (selectedNodeIds.length === 0) return;
	var parents = selectedNodeIds.map(function(id) {
		var p = findParentOf(id); return p ? p.parent : null;
	});
	
	// Special case: wrap a single expression root in a new op
	if (selectedNodeIds.length === 1 && parents[0] === null) {
		var hit = findNodeById(selectedNodeIds[0]);
		if (!hit) return;
		var rootOp = makeOpNode(opKind, [hit.node]);
		hit.expr.root = rootOp;
		evaluateExpression(hit.expr);
		selectedNodeIds = [rootOp.id];
		refreshAll();
		return;
	}
	
	// Standard: all selected nodes share a parent op
	if (parents.indexOf(null) !== -1) return;
	var first = parents[0];
	for (var i = 1; i < parents.length; i++) if (parents[i] !== first) return;
	
	var ordered = first.children.filter(function(c) { return selectedNodeIds.indexOf(c.id) !== -1; });
	var insertIdx = first.children.indexOf(ordered[0]);
	var newOp = makeOpNode(opKind, ordered);
	first.children = first.children.filter(function(c) { return selectedNodeIds.indexOf(c.id) === -1; });
	first.children.splice(insertIdx, 0, newOp);
	
	var expr = findExprForNode(newOp.id);
	if (expr) evaluateExpression(expr);
	
	selectedNodeIds = [newOp.id];
	refreshAll();
}

function dissolveSelectedOp() {
	if (selectedNodeIds.length !== 1) return;
	var hit = findNodeById(selectedNodeIds[0]);
	if (!hit || hit.node.kind !== 'op') return;
	var op = hit.node;
	var expr = hit.expr;
	
	if (expr.root.id === op.id) {
		// Op is the expression's root
		var idx = expressions.indexOf(expr);
		expressions.splice(idx, 1);
		
		if (op.children.length === 0) {
			// Empty op as root -> expression just disappears
			selectedNodeIds = [];
		} else if (op.children.length === 1) {
			// Single child becomes the new root; name and type preserved
			var newExpr = makeExpression(op.children[0], expr.name, expr.type);
			expressions.splice(idx, 0, newExpr);
			evaluateExpression(newExpr);
			selectedNodeIds = [op.children[0].id];
		} else {
			// Multiple children -> each becomes its own top-level expression
			op.children.forEach(function(child, i) {
				var childName = expr.name + ' (' + (i + 1) + ') ';
				var newExpr = makeExpression(child, childName, expr.type);
				expressions.splice(idx + 1, 0, newExpr);
				evaluateExpression(newExpr);
			});
			selectedNodeIds = [op.children[0].id];
		}
	} else {
		// Op has a parent op -> splice its children into parent at op's index
		var p = findParentOf(op.id);
		if (!p || !p.parent) return;
		var parentChildren = p.parent.children;
		var opIdx = parentChildren.indexOf(op);
		Array.prototype.splice.apply(parentChildren, [opIdx, 1].concat(op.children));
		evaluateExpression(expr);
		selectedNodeIds = op.children.length ? [op.children[0].id] : [];
	}
	refreshAll();
}

// Selection TODO (~20 lines)
function onRowClick(nodeId, event) {
	var multi = event.ctrlKey || event.shiftKey || event.metaKey;
	if (multi) {
		var idk = selectedNodeIds.indexOf(nodeId);
		if (idx >= 0) selectedNodeIds.splice(idx, 1);
		else selectedNodeIds.push(nodeId);
	} else {
		selectedNodeIds = [nodeId];
	}
	refreshAll();
}

function clearSelection() {
	selectedNodeIds = [];
	refreshAll();
}

// Actionbar handlers TODO (~30 lines)
// Mode buttons
elBtnNewPred.addEventListener('click', function() {
	if (interactionMode === 'pred_placement') {
		interactionMode = 'default';
		elBtnNewPred.classList.remove('active');
	} else {
		interactionMode = 'pred_placement';
		elBtnNewPred.classList.add('active');
		elBtnAdjPred.classList.remove('active');
		if (typeof clearSectionCutModes === 'function') clearSectionCutModes();
	}
});

elBtnAdjPred.addEventListener('click', function() {
	if (selectedNodeIds.length !== 1) return;
	var hit = findNodeById(selectedNodeIds[0]);
	if (!hit || hit.node.kind !== 'leaf') return;
	if (interactionMode === 'pred_adjust'){
		interactionMode = 'default';
		elBtnAdjPred.classList.remove('active');
	} else {
		interactionMode = 'pred_adjust';
		elBtnAdjPred.classList.add('active');
		elBtnNewPred.classList.remove('active');
		if (typeof clearSectionCutModes === 'function') clearSectionCutModes();
	}
});

elBtnDelPred.addEventListener('click', deleteSelectedNodes);
elBtnNegate.addEventListener('click', toggleNegateSelected);
elBtnGroupAnd.addEventListener('click', function() { groupSelectionInto('and'); });
elBtnGroupOr.addEventListener('click', function() { groupSelectionInto('or'); });

// Refresh: action bar + inspector + tree + visuals TODO (~50 lines)
function refreshAll() {
	refreshActionBar();
	renderInspector();
	rebuildTree();
	updatePredVisuals();
	needsRender = true;
}

function refreshActionBar() {
	var n = selectedNodeIds.length;
	elBtnDelPred.disabled = n === 0;
	elBtnNegate.disabled = n === 0;
	
	var canAdjust = false;
	if (n === 1) {
		var hit = findNodeById(selectedNodeIds[0]);
		canAdjust = !!(hit && hit.node.kind === 'leaf');
	}
	elBtnAdjPred.disabled = !canAdjust;
	if (!canAdjust && interactionMode === 'pred_adjust') {
		interactionMode = 'default'; elBtnAdjPred.classList.remove('active');
	}
	
	var canGroup = canGroupSelection();
	elBtnGroupAnd.disabled = !canGroup;
	elBtnGroupOr.disabled = !canGroup;
}

function canGroupSelection() {
	if (selectedNodeIds.length === 0) return false;
	var parents = selectedNodeIds.map(function(id) {
		var p = findParentOf(id); return p ? p.parent : null;
	});
	// Allow single-selection of a root node - wraps the root in a new op
	if (selectedNodeIds.length === 1 && parents[0] === null) return true;
	
	if (parents.indexOf(null) !== -1) return false;
	for (var i = 1; i < parents.length; i++) if (parents[i] !== parents[0]) return false;
	return true;
}

// Inspector - switch on selection TODO (~200 lines)
function renderInspector() {
	elInspector.innerHTML = '';
	var n = selectedNodeIds.length;
	if (n === 0) {
		elInspector.innerHTML = '<div class="pd-inspector-empty">Nothing selected</div>';
		return;
	}
	
	var exprMap = {};
	selectedNodeIds.forEach(function(id) {
		var e = findExprForNode(id); if (e) exprMap[e.id] = e;
	});
	var exprList = Object.keys(exprMap).map(function(k) { return exprMap[k]; });
	
	if (exprList.length === 1) {
		renderExprHeader(exprList[0]);
		elInspector.appendChild(divider());
	} else {
		var hdr = document.createElement('div');
		hdr.className = 'pd-multi-summary';
		hdr.textContent = 'Selection spans ' + exprList.length + ' expressions.';
		elInspector.appendChild(hdr);
		elInspector.appendChild(divider());
	}
	
	if (n === 1) {
		var hit = findNodeById(selectedNodeIds[0]); if (!hit) return;
		if (hit.node.kind === 'leaf') renderLeafEditor(hit.node, hit.expr);
		else renderOpEditor(hit.node, hit.expr);
	} else {
		var counts = {leaf: 0, op: 0};
		selectedNodeIds.forEach(function(id) {
			var h = findNodeById(id); if (h) counts[h.node.kind]++;
		});
		var summary = document.createElement('div');
		summary.className = 'pd-multi-summary';
		summary.innerHTML = '<strong>' + n + ' nodes selected</strong><br>'
			+ counts.leaf + ' leaf, ' + counts.op + ' operator<br>'
			+ (canGroupSelection()
				? '<span style="color:#88aa88">Eligible to group (same parent)</span>'
				: '<span style="color:#aa6644">Cannot group (mixed parents or includes a root)</span>');
		elInspector.appendChild(summary);
	}
}

function renderExprHeader(expr) {
	var nameRow = fieldRow('Name');
	var nameInp = document.createElement('input');
	nameInp.type = 'text'; nameInp.className = 'sc-input'; nameInp.value = expr.name;
	nameInp.addEventListener('change', function() {
		expr.name = nameInp.value || '(unnamed)';
		rebuildTree();
	});
	nameRow.appendChild(nameInp);
	elInspector.appendChild(nameRow);
	
	var typeRow = fieldRow('Type');
	var typeWrap = document.createElement('div');
	typeWrap.style.cssText = 'display:flex; gap:4px flex:1;';
	['plates','nodes'].forEach(function(t) {
		var b = document.createElement('button');
		b.className = 'pd-type-btn' + (expr.type === t ? ' active' : '');
		b.textContent = t.charAt(0).toUpperCase() + t.slice(1);
		b.addEventListener('click', function() {
			expr.type = t;
			evaluateExpression(expr);
			refreshAll();
		});
		typeWrap.appendChild(b);
	});
	typeRow.appendChild(typeWrap);
	elInspector.appendChild(typeRow);
}
	
function renderOpEditor(node, expr) {
	var label = document.createElement('div');
	label.style.cssText = 'font-size:10px; color:#888; margin-bottom:8px; text-transform:uppercase; letter-spacing:0.5px;';
	label.textContent = 'Operator';
	elInspector.appendChild(label);
	
	var opRow = fieldRow('Op');
	var opWrap = document.createElement('div');
	opWrap.style.cssText = 'display:flex; gap:4px; flex:1;';
	['and','or'].forEach(function(o) {
		var b = document.createElement('button');
		b.className = 'pd-type-btn' + (node.op === o ? ' active' : '');
		b.textContent = o.toUpperCase();
		b.addEventListener('click', function() {
			node.op = o;
			evaluateExpression(expr);
			refreshAll();
		});
		opWrap.appendChild(b);
	});
	opRow.appendChild(opWrap);
	elInspector.appendChild(opRow);
	
	elInspector.appendChild(negateCheckbox(node, expr));
	
	var info = document.createElement('div');
	info.className = 'pd-multi-summary';
	info.innerHTML = node.children.length + ' child nodes(s)'
		+ (node.children.length === 0 ? ' - empty container' : '')
		+ '<br>Matches: <strong>' + node.count + '</strong> ' + (expr.type === 'plates' ? 'plates' : 'nodes');
	if (node.children.length === 0) info.style.color = '#d4a045';
	elInspector.appendChild(info);
	
	// Dissolve button
	var dissolveBtn = document.createElement('button');
	dissolveBtn.className = 'sc-btn sc-btn-danger';
	dissolveBtn.style.cssText = 'margin-top:8px;';
	dissolveBtn.textContent = 'Dissolve';
	dissolveBtn.title = 'Remove this operator; its children take its place in the parent';
	dissolveBtn.addEventListener('click', dissolveSelectedOp);
	elInspector.appendChild(dissolveBtn);
}

function renderLeafEditor(node, expr) {
	var label = document.createElement('div');
	label.style.cssText = 'font-size:10px; color:#888; margin-bottom:8px; text-transform:uppercase; letter-spacing:0.5px;';
	label.textContent = 'Leaf (' + (node.finite ? 'finite plane' : 'plane') + ')';
	elInspector.appendChild(label);
	
	// Negate + Finite + Show-extents on one row
	var togglesRow = document.createElement('div');
	togglesRow.style.cssText = 'display:flex; gap:14px; margin-bottom:8px; flex-wrap:wrap;';
	
	togglesRow.appendChild(checkboxInline('Negate (\u00AC)', node.negated, function(v) {
		node.negated = v; evaluateExpression(expr); refreshAll();
	}));
	togglesRow.appendChild(checkboxInline('Finite extents', node.finite, function(v) {
		node.finite = v;
		lastLeafDefaults.finite = v;
		if (node.finite) buildPredAxes(node);
		evaluateExpression(expr);
		refreshAll();
	}));
	if (node.finite) {
		togglesRow.appendChild(checkboxInline('Show', showExtentsMesh, function(v) {
			showExtentsMesh = v;
			updateExtentsMesh();
		}));
	}
	elInspector.appendChild(togglesRow);
	
	// Position block
	var pos = document.createElement('div');
	pos.className = 'sc-position-block';
	pos.innerHTML = '<div style="font-size:9px; color:#666; text-transform:uppercase; letter-spacing:0.5px; margin-bottom:5px;">Position (inches)</div>';
	attachPdTooltip(pos,
		'Scroll over a field to nudge by &plusmn;10.<br>' +
		'Hold <strong>Shift</strong> for &times;10, <strong>Alt</strong> for &divide;10.');
	
	
	['X','Y','Z'].forEach(function(axis, i) {
		var row = document.createElement('div'); row.className = 'sc-pos-row';
		var lbl = document.createElement('span'); lbl.className = 'sc-pos-label';
		lbl.style.color = ['#ff4444', '#44ff44', '#4444ff'][i]; lbl.textContent = axis;
		var inp = document.createElement('input'); inp.type = 'text'; inp.className = 'sc-pos-input';
		inp.value = node.point[i].toLocaleString(undefined, {maximumFractionDigits:1});
		// Text input, render with comma separated 1,000s
		inp.addEventListener('change', function() {
			var v = parseFloat(inp.value.replace(/,/g, ''));
			if (!isNaN(v)) {
				node.point[i] = v;
				evaluateExpression(expr);
				refreshAll();
			} else {
				inp.value = node.point[i].toLocaleString(undefined, {maximumFractionDigits:1});
			}
		});
		// Nudge with scroll
		inp.addEventListener('wheel', function(e) {
			e.preventDefault();
			var dir = e.deltaY < 0 ? 1 : -1;
			var step = 10;
			if (e.shiftKey) step = 100;
			else if (e.altKey) step = 1;
			node.point[i] += dir * step;
			evaluateExpression(expr);
			refreshAll();
		}, {passive: false }); //Passive false required for prevent default + wheel
		row.appendChild(lbl); 
		row.appendChild(inp);
		pos.appendChild(row);
	});
	elInspector.appendChild(pos);
	
	// Normal (one-line read-only)
	var normRow = fieldRow('Normal');
	var ns = document.createElement('span'); ns.className = 'pd-norm-inline';
	ns.textContent = '[ ' 	+ node.normal[0].toFixed(4) + ', '
							+ node.normal[1].toFixed(4) + ', '
							+ node.normal[2].toFixed(4) + ' ]';
	normRow.appendChild(ns);
	elInspector.appendChild(normRow);
	
	if (node.finite) {
		elInspector.appendChild(numberRow('Width', node.width, 'in', 0.1, 0.5, function(v) {
			node.width = v;
			lastLeafDefaults.width = v;
			evaluateExpression(expr);
			refreshAll();
		}));
		elInspector.appendChild(numberRow('Length', node.length, 'in', 0.1, 0.5, function(v) {
			node.length = v;
			lastLeafDefaults.length = v;
			evaluateExpression(expr); 
			refreshAll();
		}));
		elInspector.appendChild(numberRow('Angle', node.angle_deg, 'in', 0.1, 0.5, function(v) {
			node.angle_deg = v; 
			lastLeafDefaults.angle_deg = v;
			buildPredAxes(node);
			evaluateExpression(expr); 
			refreshAll();
		}));
	}
	
	elInspector.appendChild(numberRow('Dist tol', node.tol, 'in', 0.01, 0.1, function(v) {
		node.tol = v; 
		lastLeafDefaults.tol = v; 
		evaluateExpression(expr); 
		refreshAll();
	}));
	
	elInspector.appendChild(numberRow('Ang tol', node.normal_tol_deg, 'deg', 0.1, 0.5, function(v) {
		node.normal_tol_deg = v;
		lastLeafDefaults.normal_tol_deg = v;
		node.cosNormalTol = Math.cos(v * Math.PI / 180);
		evaluateExpression(expr); refreshAll();
	}));
	
	var matchInfo = document.createElement('div');
	matchInfo.className = 'pd-multi-summary';
	matchInfo.innerHTML = 'Matches: <strong>' + node.count + '</strong> ' + (expr.type === 'plates' ? 'plates' : 'nodes');
	elInspector.appendChild(matchInfo);
}

// Inspector helpers TODO (~40 lines)
function fieldRow(labelText) {
	var row = document.createElement('div'); row.className = 'sc-field-row';
	var lbl = document.createElement('span'); lbl.className = 'sc-label'; lbl.textContent = labelText;
	row.appendChild(lbl);
	return row;
}

function numberRow(labelText, value, unit, min, step, onChange) {
	var row = fieldRow(labelText);
	var inp = document.createElement('input');
	inp.type = 'number'; inp.className = 'sc-input';
	inp.style.cssText = 'width:60px; flex:0 0 auto; text-align:center;';
	inp.value = value; inp.min = min; inp.step = step;
	inp.addEventListener('change', function() {
		var v = parseFloat(inp.value); if (!isNaN(v)) onChange(v);
	});
	row.appendChild(inp);
	if (unit) {
		var u = document.createElement('span');
		u.style.cssText = 'font-size:10px; color:#666;'; u.textContent = unit;
		row.appendChild(u);
	}
	return row;
}

function checkboxInline(labelText, checked, onChange) {
	var row = document.createElement('label'); row.className = 'pd-checkbox-row';
	row.style.marginBottom = '0';
	var cb = document.createElement('input'); cb.type = 'checkbox'; cb.checked = checked;
	cb.addEventListener('change', function() { onChange(cb.checked); });
	row.appendChild(cb);
	var t = document.createElement('span'); t.textContent = labelText;
	row.appendChild(t);
	return(row);
}

function negateCheckbox(node, expr) {
	return checkboxInline('Negate (\u00AC)', node.negated, function(v) {
		node.negated = v; evaluateExpression(expr); refreshAll();
	});
}

function divider() { var d = document.createElement('div'); d.className = 'pd-divider'; return d; }

// Tree rendering TODO (~100 lines)
function rebuildTree() {
	elTreePane.innerHTML = '';
	if (expressions.length === 0) {
		var empty = document.createElement('div');
		empty.className = 'pd-empty-state';
		empty.innerHTML = 'No predicates yet.<br>Click <em>New</em>, then Ctrl+click on the model.';
		elTreePane.appendChild(empty);
		return;
	}
	expressions.forEach(function(expr) { renderNode(expr.root, 0, expr, true); });
}

function renderNode(node, depth, expr, isExprRoot) {
	var row = document.createElement('div');
	row.className = 'pd-tree-row' + (isExprRoot ? ' expr-root' : '');
	if (selectedNodeIds.indexOf(node.id) !== -1) row.classList.add('selected');
	row.draggable = true;
	row.dataset.nodeId = node.id;
	row.addEventListener('dragstart', 	function(e) { onRowDragStart(node, e); });
	row.addEventListener('dragover', 	function(e) { onRowDragOver(node, e); });
	row.addEventListener('dragleave', 	function(e) { onRowDragLeave(e); });
	row.addEventListener('drop', 		function(e) { onRowDrop(node, e); });
	row.addEventListener('dragend', 	function(e) { onDragEnd(); });
	
	var indent = document.createElement('span');
	indent.className = 'pd-tree-indent';
	indent.style.width = (depth * 14) + 'px';
	row.appendChild(indent);
	
	var arrow = document.createElement('span');
	arrow.className = 'pd-tree-arrow';
	if (node.kind === 'op') {
		arrow.innerHTML = '&#9654';
		if (!node.collapsed) arrow.classList.add('expanded');
		arrow.addEventListener('click', function(e) {
			e.stopPropagation(); node.collapsed = !node.collapsed; rebuildTree();
		});
	} else {
		arrow.classList.add('leaf-spacer'); arrow.innerHTML = '&#9654;';
	}
	row.appendChild(arrow);
	
	var pill = document.createElement('span');
	pill.className = 'pd-pill';
	if (node.kind === 'op') {
		pill.classList.add(node.op === 'and' ? 'op-and' : 'op-or');
		pill.textContent = node.op.toUpperCase();
	} else {
		pill.classList.add('leaf');
		pill.textContent = node.finite ? 'RECT' : 'PLANE';
	}
	row.appendChild(pill);
	
	var neg = document.createElement('span');
	neg.className = 'pd-neg-badge';
	neg.textContent = node.negated ? '\u00AC' : '';
	row.appendChild(neg);
	
	var name = document.createElement('span');
	name.className = 'pd-tree-name';
	if (isExprRoot) {
		name.textContent = expr.name;
	} else if (node.kind === 'op') {
		name.textContent = '(' + node.children.length + ' children)';
		name.style.color = '#888';
	} else {
		var n = node.normal;
		name.textContent = 'n=[' + n[0].toFixed(2) + ',' + n[1].toFixed(2) + ',' + n[2].toFixed(2) + ']';
		name.style.color = '#999';
		name.style.fontFamily = 'monospace';
		name.style.fontSize = '10px';
	}
	row.appendChild(name);
	
	// Right-side trailing info: count + type pill at root, count only otherwise
	var info = document.createElement('span');
	info.className = 'pd-tree-info';
	info.textContent = node.count;
	row.appendChild(info);
	
	if (isExprRoot) {
		var t = document.createElement('span');
		t.className = 'pd-tree-info';
		t.textContent = expr.type;
		row.appendChild(t);
	}
	
	if (node.kind === 'op' && node.children.length === 0) {
		var warn = document.createElement('span');
		warn.className = 'pd-warn-badge';
		warn.textContent = "\u26A0\uFE0F"; //warning triangle emoji style
		warn.title = 'Empty operator';
		row.appendChild(warn);
	}
	
	row.addEventListener('click', function(e) { e.stopPropagation(); onRowClick(node.id, e); });
	elTreePane.appendChild(row);
	
	if (node.kind === 'op' && !node.collapsed) {
		node.children.forEach(function(c) { renderNode(c, depth + 1, expr, false); });
	}
}

elTreePane.addEventListener('click', function(e) {
	if (e.target === elTreePane) clearSelection();
});

// Drag and Drop TODO (~200 lines)
function onRowDragStart(node, e) {
	var ids;
	if (selectedNodeIds.indexOf(node.id) !== -1 && selectedNodeIds.length > 1) {
		ids = selectedNodeIds.slice();
	} else {
		ids = [node.id];
		refreshActionBar(); renderInspector();
	}
	dragNodeIds = pruneDescendantsFromSet(ids);
	e.dataTransfer.effectAllowed = 'move';
	try { e.dataTransfer.setData('text/plain', dragNodeIds.join(',')); } catch (err) {}
	var idsCopy = dragNodeIds.slice();
	setTimeout(function() {
		idsCopy.forEach(function(id) {
			var r = document.querySelector('.pd-tree-row[data-node-id="' + id + '"]');
			if (r) r.classList.add('dragging');
		});
	}, 0);
}

function onRowDragOver(node, e) {
	if (!dragNodeIds) return;
	if (dragNodeIds.indexOf(node.id) !== -1) return;
	if (anyAncestor(dragNodeIds, node.id)) return;
	e.preventDefault();
	e.dataTransfer.dropEffect = 'move';
	clearDropIndicators();
	setDropIndicator(e.currentTarget, getDropZone(e, e.currentTarget, node));
}

function onRowDragLeave(e) {
	e.currentTarget.classList.remove('drop-into', 'drop-before', 'drop-after');
}

function onRowDrop(node, e) {
	e.preventDefault();
	if (!dragNodeIds) return;
	moveNodesToTarget(dragNodeIds, node, getDropZone(e, e.currentTarget, node));
	cleanupDrag();
}

elTreePane.addEventListener('dragover', function(e) {
	if (!dragNodeIds) return;
	if (e.target !== elTreePane) return;
	e.preventDefault();
	e.dataTransfer.dropEffect = 'move';
	elTreePane.classList.add('drop-new-expr');
});

elTreePane.addEventListener('dragleave', function(e) {
	if (e.target === elTreePane) elTreePane.classList.remove('drop-new-expr');
});

elTreePane.addEventListener('drop', function(e) {
	if (!dragNodeIds) return;
	if (e.target !== elTreePane) return;
	e.preventDefault();
	moveNodesToTarget(dragNodeIds, null, 'new-expression');
	cleanupDrag();
});

function onDragEnd() { cleanupDrag(); }

function getDropZone(e, row, node) {
	var rect = row.getBoundingClientRect();
	var y = e.clientY - rect.top;
	var h = rect.height;
	if (node.kind === 'op') {
		if (y < h * 0.25) return 'before';
		return 'into';						// anything below the top quarter -> into
	}
	if (y < h * 0.5) return 'before';
	return 'after';
}

function setDropIndicator(row, zone) {
	if (zone === 'into')		row.classList.add('drop-into');
	else if (zone === 'before')	row.classList.add('drop-before');
	else if (zone === 'after')	row.classList.add('drop-after');
}

function clearDropIndicators() {
	document.querySelectorAll('.pd-tree-row').forEach(function(r) {
		r.classList.remove('drop-into', 'drop-before', 'drop-after');
	});
	elTreePane.classList.remove('drop-new-expr');
}

function cleanupDrag() {
	document.querySelectorAll('.pd-tree-row.dragging').forEach(function(r) { r.classList.remove('dragging'); });
	clearDropIndicators();
	dragNodeIds = null;
}

function anyAncestor(draggedIds, targetId) {
	for (var i = 0; i < draggedIds.length; i++) {
		var h = findNodeById(draggedIds[i]);
		if (h && containsId(h.node, targetId)) return true;
	}
	return false;
}

function containsId(node, id) {
	if (node.id === id) return true;
	if (node.kind === 'op') {
		for (var i = 0; i < node.children.length; i++) {
			if (containsId(node.children[i], id)) return true;
		}
	}
	return false;
}

function pruneDescendantsFromSet(ids) {
	return ids.filter(function(id) {
		var p = findParentOf(id);
		while(p && p.parent) {
			if (ids.indexOf(p.parent.id) !== -1) return false;
			p = findParentOf(p.parent.id);
		}
		return true;
	});
}

function moveNodesToTarget(ids, targetNode, position) {
	if (targetNode && anyAncestor(ids, targetNode.id)) return;
	var draggedNodes = [];
	ids.forEach(function(id) {
		var h = findNodeById(id); if (h) draggedNodes.push(h.node);
	});
	if (draggedNodes.length === 0) return;
	
	var sourceExprIds = [];
	var affectedExprIds = new Set();
	draggedNodes.forEach(function(n) {
		var srcExpr = findExprForNode(n.id);
		if (srcExpr && srcExpr.root.id === n.id) {
			sourceExprIds.push(srcExpr.id);
		} else if (srcExpr) {
			var p = findParentOf(n.id);
			if (p && p.parent) {
				p.parent.children = p.parent.children.filter(function(c) { return c.id !== n.id; });
				affectedExprIds.add(srcExpr.id);
			}
		}
	});
	if (sourceExprIds.length) {
		expressions = expressions.filter(function(e) { return sourceExprIds.indexOf(e.id) === -1; });
	}
	
	if (position === 'new-expression') {
		var rootNode = draggedNodes.length === 1 ? draggedNodes[0] : makeOpNode('and', draggedNodes);
		var newExpr = makeExpression(rootNode);
		expressions.push(newExpr);
		evaluateExpression(newExpr);
		selectedNodeIds = [rootNode.id];
	} else if (position === 'into') {
		Array.prototype.push.apply(targetNode.children, draggedNodes);
		affectedExprIds.add(findExprForNode(targetNode.id).id);
		selectedNodeIds = draggedNodes.map(function(n) { return n.id; });
	} else {
		var p = findParentOf(targetNode.id);
		if (p && p.parent) {
			var idx = p.parent.children.indexOf(targetNode);
			if (position === 'after') idx++;
			Array.prototype.splice.apply(p.parent.children, [idx, 0].concat(draggedNodes));
			affectedExprIds.add(findExprForNode(targetNode.id).id);
			selectedNodeIds = draggedNodes.map(function(n) { return n.id; });
		} else {
			var targetExpr = findExprForNode(targetNode.id);
			var exprIdx = expressions.indexOf(targetExpr);
			if (position === 'after') exprIdx++;
			var rootNode2 = draggedNodes.length === 1 ? draggedNodes[0] : makeOpNode('and', draggedNodes);
			var newExpr2 = makeExpression(rootNode2);
			expressions.splice(exprIdx, 0, newExpr2);
			evaluateExpression(newExpr2);
			selectedNodeIds = [rootNode2.id];
		}
	}
	
	affectedExprIds.forEach(function(eid) {
		var expr = expressions.find(function(e) { return e.id === eid; });
		if (expr) evaluateExpression(expr);
	});
	
	refreshAll();
}

// Visualization - highlight overlay + finite-plane extents mesh TODO (~100 lines, some already written)
function updatePredVisuals() {
	// teardown both
	if (highlightMesh) {
		scene.remove(highlightMesh);
		highlightMesh.traverse(function(o) { 
			if (o.geometry) o.geometry.dispose();
			if (o.material) o.material.dispose();
		});
		highlightMesh = null;
	}
	updateExtentsMesh();
	
	// Render highlight for the selected node (single selection only)
	// TODO: change this to support highlighting compound expressions
	if (selectedNodeIds.length !== 1) return;
	var hit = findNodeById(selectedNodeIds[0]); if (!hit) return;
	
	var matchSet = effectiveSet(hit.node, hit.expr.type);
	if (matchSet.size === 0) return;
	
	if (hit.expr.type === 'plates') {
		buildPlateOverlay(Array.from(matchSet));
	} else {
		buildNodeOverlay(Array.from(matchSet));
	}
	needsRender = true;
}

function buildPlateOverlay(matches) {
	var triCount = 0;
	for (var i = 0; i < matches.length; i++) {
		triCount += model.elements[matches[i]][0] === 3 ? 1 : 2;
	}
	var pos = new Float32Array(triCount * 9);
	var w = 0;

	for (var i = 0; i < matches.length; i++) {
		var el = model.elements[matches[i]];
		var nc = el.nodeCount, nodes = el.nodes;
		var b0 = nodes[0]*3, b1 = nodes[1]*3, b2 = nodes[2]*3;
		
		// tri 1 : p0, p2, p3
		pos[w++] = positions[b0]; pos[w++] = positions[b0+1]; pos[w++] = positions[b0+2];
		pos[w++] = positions[b1]; pos[w++] = positions[b1+1]; pos[w++] = positions[b1+2];
		pos[w++] = positions[b2]; pos[w++] = positions[b2+1]; pos[w++] = positions[b2+2];
		
		if (nc == 4) {
			var b3 = nodes[3]*3;
			// tri 1 : p0, p2, p3
			pos[w++] = positions[b0]; pos[w++] = positions[b0+1]; pos[w++] = positions[b0+2];
			pos[w++] = positions[b2]; pos[w++] = positions[b2+1]; pos[w++] = positions[b2+2];
			pos[w++] = positions[b3]; pos[w++] = positions[b3+1]; pos[w++] = positions[b3+2];
		}
	}
	var geo = new THREE.BufferGeometry();
	geo.setAttribute('position', new THREE.BufferAttribute(pos, 3));
	
	var mat = new THREE.MeshBasicMaterial({
		color: 0x2e7d32,
		transparent: true,
		opacity: 0.55,
		side: THREE.DoubleSide,
		depthWrite: false,
		polygonOffset: true,
		polygonOffsetFactor: -1,
		polygonOffsetUnits: -1
	});
	
	highlightMesh = new THREE.Mesh(geo, mat);
	scene.add(highlightMesh);
}
	
function buildNodeOverlay(matches) {
		// node overlay
		var n = matches.length;
		var pos = new Float32Array(n * 3);
		for (var i = 0; i < n; i++) {
			var src = matches[i] * 3;
			var dst = i * 3;
			pos[dst]	 = positions[src];
			pos[dst + 1] = positions[src + 1];
			pos[dst + 2] = positions[src + 2];
		}
		
		var geo = new THREE.BufferGeometry();
		geo.setAttribute('position', new THREE.BufferAttribute(pos, 3));
		
		var mat = new THREE.PointsMaterial({
			color: 0x2e7d32,		// Material dark green 700
			size: 8,				// pixels
			sizeAttenuation: false,	// constant on-screen size at any zoom
			depthTest: true,
			transparent: true,
			opacity: 0.9
		});
		
		highlightMesh = new THREE.Points(geo, mat);
		scene.add(highlightMesh);
}

function updateExtentsMesh() {
	if (predExtentsMesh) {
		scene.remove(predExtentsMesh);
		predExtentsMesh.traverse(function(o) {
			if (o.geometry) o.geometry.dispose();
			if (o.material) o.material.dispose();
		});
		predExtentsMesh = null;
	}
	if (!showExtentsMesh) return;
	if (selectedNodeIds.length !== 1) return; // Probably fine to restrict extents to a single leaf
	var hit = findNodeById(selectedNodeIds[0]); if (!hit) return;
	if (hit.node.kind !== 'leaf' || !hit.node.finite) return;
	var pred = hit.node;
	
	var geo = new THREE.PlaneGeometry(pred.width, pred.length);
	var mat = new THREE.MeshBasicMaterial({
		color: 0x88cc88,
		transparent: true,
		opacity: 0.15,
		side: THREE.DoubleSide,
		depthWrite: false
	});
	var mesh = new THREE.Mesh(geo, mat);
	var edges = new THREE.LineSegments(
		new THREE.WireframeGeometry(geo),
		new THREE.LineBasicMaterial({ color: 0x000000 })
		);
	mesh.add(edges);	
	mesh.position.set(pred.point[0], pred.point[1], pred.point[2]);
	
	// Orient: local +X -> uAxis, local +Y -> vAxis, local +Z -> normal
	var u = new THREE.Vector3(pred.uAxis[0],  pred.uAxis[1],  pred.uAxis[2]);
	var v = new THREE.Vector3(pred.vAxis[0],  pred.vAxis[1],  pred.vAxis[2]);
	var n = new THREE.Vector3(pred.normal[0], pred.normal[1], pred.normal[2]);
	var m = new THREE.Matrix4().makeBasis(u, v, n);
	mesh.quaternion.setFromRotationMatrix(m);
	
	predExtentsMesh = mesh;
	scene.add(predExtentsMesh);
}

// Tooltips
function attachPdTooltip(el, html) {
	el.addEventListener('mouseenter', function() {
		if (!pdTooltip) {
			pdTooltip = document.createElement('div');
			pdTooltip.className = 'pd-tooltip';
			document.body.appendChild(pdTooltip);
		}
		pdTooltip.innerHTML = html;
		pdTooltip.style.left = '0';
		pdTooltip.style.top = '0';
		pdTooltip.style.display = 'block';
		var tw = pdTooltip.offsetWidth;
		var rect = el.getBoundingClientRect();
		pdTooltip.style.left = Math.max(8, rect.left - tw - 8) + 'px';
		pdTooltip.style.top = rect.top + 'px';
	});
	el.addEventListener('mouseleave', function() {
		if (pdTooltip) pdTooltip.style.display = 'none';
	});
}

// Export / Import (~100 lines)
function exportNode(node) {
	if (node.kind === 'leaf') {
		var p = {
			kind: node.finite ? 'finitePlane' : 'plane',
			negated: node.negated,
			point: node.point,
			normal: node.normal,
			tol: node.tol,
			normal_tol_deg: node.normal_tol_deg
		};
		if (node.finite) {
			p.width		= node.width;
			p.length	= node.length;
			p.angle_deg = node.angle_deg;
		}
		return p;
	}
	return {
		kind: node.op, 		// 'and' | 'or
		negated: node.negated,
		children: node.children.map(exportNode)
	};
}

function importNode(data) {
	if (!data || !data.kind) return null;
	if (data.kind === 'plane' || data.kind === 'finitePlane') {
		var leaf = {
			id: newNodeId(),
			kind: 'leaf',
			negated: !!data.negated,
			point: data.point ? data.point.slice() : [0,0,0],
			normal: data.normal ? data.normal.slice() : [0,1,0],
			tol: typeof data.tol === 'number' ? data.tol : 0.1,
			normal_tol_deg: typeof data.normal_tol_deg === 'number' ? data.normal_tol_deg : 5.0,
			finite:		data.kind === 'finitePlane',
			width:		typeof data.width		=== 'number' ? data.width		: 24,
			length:		typeof data.length		=== 'number' ? data.length		: 24,
			angle_deg:	typeof data.angle_deg	=== 'number' ? data.angle_deg	: 0,
			count: 0,
			matches: []
		};
		leaf.cosNormTol = Math.cos(leaf.normal_tol_deg * Math.PI / 180);
		buildPredAxes(leaf);
		return leaf;
	} if (data.kind === 'and' || data.kind === 'or') {
		return {
			id: newNodeId(),
			kind: 'op',
			op: data.kind,
			negated: !!data.negated,
			children: (data.children || []).map(importNode),
			collapsed: true,
			count: 0,
			matches: []
		};
	}
	console.warn('Unknown predicate kind on import:', data.kind);
	return null;
}

document.getElementById('btnPredExport').addEventListener('click', function() {
	var out = {
		version: 3,
		expressions: expressions.map(function(e) {
			return { name: e.name, type: e.type, root: exportNode(e.root) };
		})
	};
	var blob = new Blob([JSON.stringify(out, null, '\t')], { type: 'application/json' });
	var a = document.createElement('a');
	a.href = URL.createObjectURL(blob);
	a.download = 'predicates.json';
	a.click();
	URL.revokeObjectURL(a.href);
});

document.getElementById('btnPredImport').addEventListener('click', function() {
	elPredImportFile.click();
});

elPredImportFile.addEventListener('change', function(e) {
	var file = e.target.files[0]; if (!file) return;
	var reader = new FileReader();
	reader.onload = function(ev) {
		try {
			var data = JSON.parse(ev.target.result);
			expressions = [];
			selectedNodeIds = [];
			
			if (Array.isArray(data.expressions)) {
				//newer nested format
				data.expressions.forEach(function(g) {
					var root = importNode(g.root);
					if (!root) return;
					var expr = makeExpression(root, g.name, g.type);
					expressions.push(expr);
					evaluateExpression(expr);
				});
			} else if (Array.isArray(data.groups)) {
				// v1 flat format - each group is a single leaf expression
				data.groups.forEach(function(g) {
					var p = g.predicate || {};
					var leaf = importNode(p);
					if (!leaf) return;
					var expr = makeExpression(leaf, g.name, g.type);
					expressions.push(expr);
					evaluateExpression(expr);
				});
			}
			
			refreshAll();
		} catch (err) { alert('Invalid JSON: ' + err.essage); }
	};
	reader.readAsText(file);
	this.value = '';
});

// Initial paint
refreshAll();