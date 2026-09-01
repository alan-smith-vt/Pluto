// --- Scene Setup ---
const scene = new THREE.Scene();
scene.background = new THREE.Color(0x111111);

const camera = new THREE.PerspectiveCamera(50, innerWidth / innerHeight, 0.1, 1000);
camera.position.set(5,5,5);
camera.lookAt(0,0,0);

const renderer = new THREE.WebGLRenderer({ antialias: true });
renderer.setSize(innerWidth, innerHeight);
renderer.setPixelRatio(window.devicePixelRatio);
renderer.domElement.style.position = 'fixed';
document.body.appendChild(renderer.domElement);

const controls = new THREE.OrbitControls(camera, renderer.domElement);
controls.enableDamping = true;

// --- Lights ---
const hemiLight = new THREE.HemisphereLight(0xffffff, 0x444444, 0.5);
scene.add(hemiLight);

const camLight = new THREE.DirectionalLight(0xffffff, 1);
camLight.position.set(0,1,1);
camera.add(camLight);
scene.add(camera);

const ambientLight = new THREE.AmbientLight(0xffffff,0.4);
scene.add(ambientLight);

// ==========================================================================

// --- State ---
let mesh = null;
let nFiles = 0, nCombos = 0, nNodes = 0;
let stresses = null;
let colors = null;
let dispArray = null;
let nodeIdToIndex = new Map();
let nStresses = 14;
let wireDispArray = null;
let staticWireframe = null;
let targetOrb = null;
let targetOrbTimeout = null;
let pickAnim = null;

// Shared geometry/materials for cuts (create once, reuse)
let orbGeo = null;
let orbMats = { X: null, Y: null, Z: null };
let lineMats = { X: null, Y: null, Z: null };

// Data from files
let model = null;
let positions = null;
let indices = null;
let edgeIndices = null;
let loadNames = null;
let modelNames = null;


//Plate cache
let plateCg = null;
let plateNm = null;

// Animation logic
let animating = false;
let direction = 1;
let needsRender = true;
let isInteracting = false;
let lastCameraState = { pos: new THREE.Vector3(), target: new THREE.Vector3() };
let endTimeout = null;

// --- UI ---
const info = document.getElementById('info');
const controlsDiv = document.getElementById('controls');
const playBtn = document.getElementById('playBtn');
const slider = document.getElementById('dispScale');

// ==========================================================================

// Section cut state
let sectionCuts = [];
let sectionGroups = [];
let sectionCutObjects = {};
let cutIdCounter = 0;
let interactionMode = 'default';
let currentAxis = 'Z';
let modelMaxDim = 0;
let selectionRing = null;

// --- Section Cut UI ---
let elTab = document.getElementById('sectionCutTab');
let elPanel = document.getElementById('sectionCutPanel');
let elBtnNew = document.getElementById('btnNewCut');
let elBtnAdj = document.getElementById('btnAdjust');
let elBtnDel = document.getElementById('btnDelete');
let elName = document.getElementById('cutName');
let elGroup = document.getElementById('cutGroup');
let elLength = document.getElementById('cutLengthFt');
let elPosX = document.getElementById('cutPosX');
let elPosY = document.getElementById('cutPosY');
let elPosZ = document.getElementById('cutPosZ');
let elList = document.getElementById('cutList');
let elAxisBtns = document.querySelectorAll('.sc-axis-btn');

// ==========================================================================

// Predicate state

let expressions = [];
let selectedNodeIds = [];
let nodeIdCounter = 0;
let exprIdCounter = 0;
let undoStack = [];
let redoStack = [];
let MAX_UNDO = 50;
let isRestoring = false;

let highlightMesh = null;
let predExtentsMesh = null;
let showExtentsMesh = true;
let pdTooltip = null;
let dragNodeIds = null;

// --- Predicate UI ---
let elPredTab 		= document.getElementById('predicateTab');
let elPredPanel 	= document.getElementById('predicatePanel');
let elBtnNewPred 	= document.getElementById('btnNewPred');
let elBtnAdjPred 	= document.getElementById('btnAdjPred');
let elBtnDelPred 	= document.getElementById('btnDelPred');
let elBtnGroupOr 	= document.getElementById('btnGroupOr');
let elBtnGroupAnd 	= document.getElementById('btnGroupAnd');
let elBtnNegate 	= document.getElementById('btnNegate');
let elInspector		= document.getElementById('predInspector');
let elTreePane		= document.getElementById('predTreePane');
let elPredImportFile= document.getElementById('predImportFile');

let lastLeafDefaults = {
	tol:			0.1,
	normal_tol_deg:	5.0,
	finite:			false,
	width:			240,
	length:			240,
	angle_deg:		0
};
	


// let elPredName = document.getElementById('predName');
// let elPredPosX = document.getElementById('predPosX');
// let elPredPosY = document.getElementById('predPosY');
// let elPredPosZ = document.getElementById('predPosZ');
// let elPredNormal = document.getElementById('predNormal');
// let elPredDistTol = document.getElementById('predDistTol');
// let elPredAngTol = document.getElementById('predAngTol');
// let elPredCount = document.getElementById('predCount');
// let elPredList  = document.getElementById('predList');
// let elPredTypeBtns = document.querySelectorAll('.pd-type-btn');

// let elPredFinite = document.getElementById('predFinite');
// let elPredWidth = document.getElementById('predWidth');
// let elPredLength = document.getElementById('predLength');
// let elPredAngle = document.getElementById('predAngle');
// let elFiniteRows = document.querySelectorAll('.pd-finite-only');
// let elPredShowExtents = document.getElementById('predShowExtents');

let PD_WIDTH = 280