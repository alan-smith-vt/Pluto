using System;
using System.IO;
using System.Collections.Generic;
using System.Text;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Web.Script.Serialization;
using System.Linq.Expressions;
using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
using Excel = Microsoft.Office.Interop.Excel;
using System.Net;

// CLASSES AND METHODS USED WHEN TAKING SECTION CUTS THROUGH PLATE ELEMENTS.

// PlatePlane: Class containing all operations performed on model geometry to identify relevant
// elemenets and nodes for section cut.
public class PlatePlane
{
	// TO BE MOVED TO A DIFFERENT CLASS
	// /////////////////////////////////////////////////////////////
	//public static ILookup<int, StressRecord> GetStressLookup(StressRecord[] stresses)
	//{
	//	return stresses.ToLookup(s => s.elemID);
	//}

	//public static int[] GetLCs(int[] strLCs)
	//{
	//	return strLCs.Distinct().ToArray();
	//}
	// /////////////////////////////////////////////////////////////

	// Function to return base elements identified base on the user-input point.
	// If more than one base element is identified, Section-Cut.ps1 should return an error saying so,
	// asking the user to slighty adjust the base point of the section cut to be solely within one element.
	public static Element GetBaseElement(
		// Starting point for the section cut, input by the user.
		Vector3 a,

		Element[] Elements)
	{
		Element[] potentialBaseElems = Elements.Where(e =>
		{
			Vector3 v1 = a - e.n[0].xyz;
			//Console.WriteLine("Element {0} - Dot product: {1}", e.id, Math.Abs(Vector3.Dot(v1, e.rZ)));
			bool isBaseElem = Math.Round(Math.Abs(Vector3.Dot(v1, e.rZ)), 1) < 1e-6;
			if (isBaseElem)
			{
				//Console.WriteLine(Math.Round(Math.Abs(Vector3.Dot(v1, e.rZ)), 1));
				double angle = 0;

				for (int i = 0; i < e.nNodes; i++)
				{
					v1 = a - e.n[i].xyz;
					Vector3 v2 = a - e.n[(i + 1) % e.nNodes].xyz;

					angle += Math.Acos(Vector3.Dot(v1, v2) / (v1.Length() * v2.Length())) * 180 / Math.PI;
				}
				angle = Math.Round(angle, 1);
				if (angle != 360) { isBaseElem = false; }
				//Console.WriteLine(angle);
			}
			return isBaseElem;
		}).ToArray();

		if (potentialBaseElems.Length == 0)
		{
			throw new SectionCutException(
			"No elements found at the input point.");
		}
		else if (potentialBaseElems.Length > 1)
		{
			throw new SectionCutException(
			"More than one element was found at this point. Adjust the point so that it lies within one element only.");
		}
		return potentialBaseElems[0];
	}

	// Function to identify all elements that lie on the same 2-dimensional plane as a given
	// parametric vector form line.
	public static Element[] GetLineCoplanarElements(
		// Position and direction vectors for equation of the line.
		Vector3 a,
		Element bElem,

		Element[] Elements)
	{
		return Elements.Where(e =>
		{
			// Picking out the first node of a given element.
			Vector3 n1 = e.n[0].xyz;
			// Drawing a line from point 'a' on the line to the selected node.
			Vector3 lToNode1 = Vector3.Normalize(n1 - a);

			// If the line and the element are co-planar, point 'a' must lie on the same
			// plane as all nodes of the element, and thus, the plane of the line and
			// any line on the same plane will have a normal vector perfectly parallel
			// to the normal of the base element and the reference structural component.
			// As such, the dot product of a line between point 'a' and the selected
			// node, and the normal vector of the reference structural component will
			// evaluate to 0 if these are perpendicular, and the points lie on the same
			// plane.
			bool coplanar = Math.Abs(Vector3.Dot(lToNode1, bElem.rZ)) <= 1e-2 &&
				(1 - Math.Abs(Vector3.Dot(bElem.rZ, e.rZ))) <= 1e-6;

			// Return all elements where the condition is met.
			return coplanar;
		}).ToArray();
	}
	// -------------------------------------------------

	// Function to determine the vector representing the shortest distance between a
	// specified line and a given point.
	public static Vector3 GetVecP2L(
		// Equation of a line in parametric vector form (p(t) = a + t*b)
		Vector3 a,
		Vector3 b,

		// Position vector of a point in space
		Vector3 p)
	{
		// Scalar position factor 't' = (b*(p - a))/(b*b)
		// where 'b' is the direction vector of the specified line, 'a' is a point on the
		// specified line, and 'p' is a given point in space.
		// The resulting 't' is the factor for which the equation of the line results in the
		// shortest distance between the line and the given point 'p'.
		float tOfShortestDist = Vector3.Dot(b, (p - a)) / (b.LengthSquared());
		Vector3 pOnLine = a + (b * tOfShortestDist);

		// Return the vector representing the distance between point 'p' and the point along
		// the line at 't'.
		return p - pOnLine;
	}
	// -------------------------------------------------

	public static bool TestLineCrossesEl(
		// Equation of a line in parametric vector form (p(t) = a + t*b)
		Vector3 a,
		Vector3 b,

		// Element to be checked.
		Element e)
	{
		// For each node of an element, get the vector representing the distance and direction
		// of the specified line to that node.
		Vector3[] vecsP2L = new Vector3[e.nNodes];
		for (int i = 0; i < e.nNodes; i++) { vecsP2L[i] = GetVecP2L(a, b, e.n[i].xyz); }

		// Select the first of these vectors with a magnitude greater than 0 (i.e. where the
		// element node does not lie on the line).
		Vector3 refVec = vecsP2L.First(v => v.Length() >= 1e-6);


		// Condition met when any of the vectors calculated for each node is in the opposite
		// direction to the vector selected as the reference vector (dot product of the two
		// evaluates to be negative).
		////[Func[System.Numerics.Vector3, bool]]condition = {
		////	param(v)
		////return Vector3.Dot(refVec, v) - lt - 1e-6
		////}

		// Return 'true' if at least one pair of two vectors returns a negative dot product.
		// This means that at least two nodes are on opposite sides of the specified line
		// and the specified line crosses the element being checked.
		return vecsP2L.Any(v => Vector3.Dot(refVec, v) < -1e-6);
	}
	// -------------------------------------------------

	// Function to determine elements that are crossed by the section cut line.
	public static Element[] GetElemsCrossedByLine(
		Vector3 a,
		Vector3 b,
		Element[] elems)
	{
		return elems.Where(e => TestLineCrossesEl(a, b, e)).ToArray();
	}
}

public class PlateSectionCut
{
	public Hashtable cut; // information from the section cut JSON from which
						  // each section cut is formed:
						  // id: string of section cut ID
						  // name: string of section cut name
						  // group: sting of section cut group name
						  // point: Vector3 of coordinates for starting point
						  // axis: char indicating section cut direction
						  // length: float indicating the total length of the section cut
						  // visible: N/A - for the viewer
	public Vector3 a;
	public float length;
	public float area;
	public float length_actual;
	public float t1;
	public float t2;
	public string axis;
	public UnitSystem units;
	public List<Element> ElemList;
	public Dictionary<int, Element> ElemDict;
	public List<StressRecord> StrList;
	public Element baseElem;
	public Vector3 b;
	public Vector3 u;
	public Vector3 tractionDir;
	public Element[] sectionCutElems;
	public int[] sectElsList;
	public Dictionary<int, List<ElEdge>> sectElEdges;
	public List<ElEdge> sectEdgesThruEls = new List<ElEdge>(); // These are the integration limits across each element.
															   // For elements towards the middle of the section cut,
															   // the 't' values are always 0 and 1 since stresses are
															   // integrated from start to finish through the element.
															   // For elements at the ends of the section, 't' values
															   // are 0 and a linear interpolation scalar tbd.
	public int[] LCs;
	public object[] MIDs;
	public Dictionary<int, Dictionary<object, Dictionary<int, List<StressRecord>>>> edgeStresses =
			new Dictionary<int, Dictionary<object, Dictionary<int, List<StressRecord>>>>();
	public List<Segment> sectSegments = new List<Segment>();
	public Dictionary<object, Dictionary<int, List<Segment>>> sectSegDict =
			new Dictionary<object, Dictionary<int, List<Segment>>>();
	public Dictionary<object, Dictionary<int, Matrix4x4>> beamNAStresses =
			new Dictionary<object, Dictionary<int, Matrix4x4>>();
	public int[] webEls;
	public int[] flangeEls;
	public object[,] originalData;
	public object[,] sectionData;
	public string OutputDir;
	public ExcelFile OutputFile;
	/// <summary>When true, generates per-LC/MID V&V sheets instead of a summary.</summary>
	public bool Expand;
	/// <summary>
	/// Maps (modelID.ToString(), elemID, nodeID, LC) to the Excel row number
	/// in the "Original Data" sheet.  Built at end of _init_SectionCutResults
	/// when Expand = true.
	/// </summary>
	private Dictionary<string, int> _origRow;

	public void Merge(PlateSectionCut wsc, PlateSectionCut fsc)
	{
		// Section cut setup and configuration
		this.cut = new Hashtable(fsc.cut);
		this.a = wsc.a;
		this.axis = fsc.axis;
		this.units = wsc.units;
		this.length = fsc.length;
		this.Expand = false;//wsc.Expand; // Forced to false while not implemented.
		this.OutputDir = wsc.OutputDir;
		this.OutputFile = wsc.OutputFile;
		this.t1 = fsc.t1;
		this.t2 = fsc.t2;

		// In-process data structures
		this.ElemList = wsc.ElemList;
		this.ElemDict = wsc.ElemDict;
		this.baseElem = wsc.baseElem;
		this.sectElsList = wsc.sectElsList.Concat(fsc.sectElsList).ToArray();
		this.webEls = wsc.sectElsList;
		this.flangeEls = fsc.sectElsList;
		this.StrList = wsc.StrList.Concat(fsc.StrList).ToList();
		this.sectionCutElems = wsc.sectionCutElems.Concat(fsc.sectionCutElems).ToArray();

		this.sectElEdges = wsc.sectElEdges;
		foreach (var kvp in fsc.sectElEdges) { this.sectElEdges.Add(kvp.Key, kvp.Value); }

		this.sectEdgesThruEls = wsc.sectEdgesThruEls.Concat(fsc.sectEdgesThruEls).ToList();
		this.LCs = wsc.LCs.Intersect(fsc.LCs).ToArray();
		this.MIDs = wsc.MIDs.Intersect(fsc.MIDs).ToArray();
		this.sectSegments = wsc.sectSegments.Concat(fsc.sectSegments).ToList();

		this.sectSegDict = wsc.sectSegDict;
		foreach (var mid in fsc.MIDs)
		{
			foreach (var lc in fsc.LCs) { this.sectSegDict[mid][lc].AddRange(fsc.sectSegDict[mid][lc]); }
		}

		// Derived results
		this.area = wsc.area + fsc.area;
		this.length_actual = wsc.length_actual + fsc.length_actual;
		this.beamNAStresses = wsc.beamNAStresses;
		this.originalData = _CombineObjectTables(wsc.originalData, fsc.originalData);

	}

	private object[,] _CombineObjectTables(object[,] a, object[,] b)
	{

		int aCols = a.GetLength(1);
		int aRows = a.GetLength(0);
		int bCols = b.GetLength(1);
		int bRows = b.GetLength(0);
		int nCols = Math.Max(aCols, bCols);
		int totRows = aRows + bRows - 1;
		object[,] res = new object[totRows, nCols];
		for (int r = 0; r < aRows; r++)
			for (int c = 0; c < aCols; c++)
				res[r, c] = a[r, c];
		for (int r = 1; r < bRows; r++)
			for (int c = 0; c < bCols; c++)
				res[aRows + r - 1, c] = b[r, c];

		return res;
	}
	// Section cut constructor for regular section cuts
	public static PlateSectionCut CreatePlateSectionCut(
		Hashtable cut,
		StaadModel inputModel,
		string outputFolder,
		UnitSystem predefUnits,
		bool expand = false,
		int[] requestedLCs = null)
	{
		//ProgressBar pb = new ProgressBar(5, title: "Generating section cut");
		PlateSectionCut psc = new PlateSectionCut();
		psc.cut = (Hashtable)cut.Clone();
		psc.StrList = inputModel.StressList.ToList();
		psc.ElemList = inputModel.ElementList.ToList();
		psc.ElemDict = psc.ElemList.ToDictionary(e => e.id);
		psc.StrList = psc.StrList.Distinct().ToList();

		int[] allLCs = psc.StrList.Select(s => s.LC).Distinct().ToArray();
		psc.LCs = (requestedLCs != null && requestedLCs.Length > 0) ? requestedLCs : allLCs;

		psc.units = predefUnits;
		psc.Expand = expand;
		psc.OutputDir = outputFolder;
		psc._init_Cut();
		//pb.Tick();
		psc._init_Geometry();
		//pb.Tick();
		psc._init_Stresses();
		//pb.Tick();
		psc._init_OriginalData();
		//pb.Tick();
		psc._init_SectionCutResults();
		//pb.Finish();

		return psc;
	}

	public static PlateSectionCut CreateBeamWebSectionCut(
		Hashtable cut,
		StaadModel inputModel,
		string outputFolder,
		UnitSystem predefUnits,
		bool expand = false,
		int[] requestedLCs = null)
	{
		PlateSectionCut websc = new PlateSectionCut();
		websc.cut = (Hashtable)cut.Clone();
		websc.StrList = inputModel.StressList.ToList();
		websc.ElemList = inputModel.ElementList.ToList();
		websc.ElemDict = websc.ElemList.ToDictionary(e => e.id);

		websc.cut["axis"] = "Y";
		websc.cut["length"] = 140.0;

		websc.StrList = websc.StrList.Distinct().ToList();

		int[] allLCs = websc.StrList.Select(s => s.LC).Distinct().ToArray();
		websc.LCs = (requestedLCs != null && requestedLCs.Length > 0) ? requestedLCs : allLCs;

		websc.units = predefUnits;
		websc.Expand = expand;
		websc.OutputDir = outputFolder;
		websc._init_Cut();
		websc._init_Geometry();
		websc._ShiftToUpperBoundary();
		websc._init_Stresses();
		websc._init_OriginalData();

		return websc;
	}

	// Section cut constructor for T-beam section cuts
	public static PlateSectionCut TBeamSectionCut(
		Hashtable cut,
		StaadModel inputModel,
		string outputFolder,
		UnitSystem predefUnits,
		bool expand = false,
		int[] requestedLCs = null)
	{
		// Assemble PlateSectionCut for web elements
		PlateSectionCut websc = CreateBeamWebSectionCut(cut, inputModel, outputFolder,
			predefUnits, expand, requestedLCs);

		// Adjust base point for flange section cut
		cut["point"] = new double[] { websc.a.X + 0.01, websc.a.Y, websc.a.Z };

		// Assemble PlateSectionCut for flange elements
		PlateSectionCut flangesc = CreatePlateSectionCut(cut, inputModel, outputFolder,
			predefUnits, expand, requestedLCs);

		// Calculate depth from flange centroid to T-beam neutral axis
		double y_NA = ((0.0 * flangesc.area) + (-0.5 * websc.length_actual * websc.area)) /
			(flangesc.area + websc.area);
		// For each modelID and LC get the corresponding stress matrix at the neutral axis
		websc.GetNeutralAxisAxialStr(y_NA);

		// Assemble resulting T-beam PlateSectionCut composed of the flange and web objects
		PlateSectionCut TBeamSC = new PlateSectionCut();
		TBeamSC.Merge(websc, flangesc);
		TBeamSC._init_TBeamSectionCutResults();

		return TBeamSC;
	}
	private void _init_Cut()
	{
		// Process section cut input data\
		var point = (double[])cut["point"];
		a = new Vector3((float)point[0],
						(float)point[1],
						(float)point[2]);
		length = Convert.ToSingle(cut["length"]);
		t1 = -length / 2;
		t2 = length / 2;
		axis = (string)cut["axis"];
		baseElem = PlatePlane.GetBaseElement(a, ElemList.ToArray());
	}
	private void _init_Geometry()
	{
		// =======================================================================================
		// Establish section cut direction vectors:
		// ---------------------------------------------------------------------------------------
		// Section cut direction vector:
		string unitDir = "Unit" + axis;
		PropertyInfo unitVec = typeof(Vector3).GetProperty(unitDir);
		b = (Vector3)unitVec.GetValue(null); // Direction vector in global axis
		b -= baseElem.rZ * Vector3.Dot(b, baseElem.rZ); // Direction vector projected onto the plane
														// of the element

		if (Math.Round(b.Length()) != 1)
		{
			throw new SectionCutException(
			"Cannot take section cut normal to plane of elements. Double check section cut direction and try again.");
		}

		// 2D section cut direction vector for section forces:
		u = Vector3.Normalize(Vector3.Transform(b, Matrix4x4.Transpose(baseElem.R_gr)));

		tractionDir = Vector3.Normalize(Vector3.Cross(u, Vector3.UnitZ));
		float ax = tractionDir.X;
		float ay = tractionDir.Y;

		float dominant = (Math.Abs(ax) >= Math.Abs(ay)) ? tractionDir.X : tractionDir.Y;
		if (dominant < 0) tractionDir = -tractionDir;
		u = Vector3.Cross(tractionDir, Vector3.UnitZ); // Re-calculate 'u' so that it is always 90 degrees
													   // clockwise from 'tractionDir'

		// ---------------------------------------------------------------------------------------
		// =======================================================================================
		// Find subset of relevant elements
		// ---------------------------------------------------------------------------------------
		Element[] eOnPlaneOfLine = PlatePlane.GetLineCoplanarElements(a, baseElem, ElemList.ToArray());
		sectionCutElems = PlatePlane.GetElemsCrossedByLine(a, b, eOnPlaneOfLine);
		sectElEdges = GetDictofEdges(sectionCutElems);

		List<int> elems = sectElEdges.Select(ee => ee.Key).ToList();
		sectionCutElems = sectionCutElems.Where(e => elems.Contains(e.id)).ToArray();
		sectElsList = sectionCutElems.Select(e => e.id).ToArray();
		foreach (int elem in elems)
		{
			//int elem = grouping.Key;
			ElEdge[] ees = sectElEdges[elem].ToArray();

			if (ees.Length < 2) continue; // Skip corner graze cases

			ElEdge see1 = ees[0];
			ElEdge see2 = ees[1];

			ElEdge sete = new ElEdge(); // sete - one singular sectEdgeThruEl
			sete.elemID = elem;
			sete.e = ElemDict[elem];

			Node n1 = new Node();
			Node n2 = new Node();

			n1.id = 1;
			n1.xyz = Vector3.Lerp(see1.n[0].xyz, see1.n[1].xyz, see1.t[0]);

			n2.id = 2;
			n2.xyz = Vector3.Lerp(see2.n[0].xyz, see2.n[1].xyz, see2.t[0]);

			sete.n = new Node[] { n1, n2 };
			sete.b = sete.n[1].xyz - sete.n[0].xyz;
			sete.t = new float[] { 0, 1 };
			if (see2.t[1] < t1 && t1 < see1.t[1])
			{
				float segLength = see2.t[1] - see1.t[1];
				float lenToSegEnd = t1 - see1.t[1];
				float limitT = lenToSegEnd / segLength;
				sete.t[1] = limitT;
				sete.n[1].xyz = Vector3.Lerp(sete.n[0].xyz, sete.n[1].xyz, limitT);
			}
			else if (see1.t[1] < t2 && t2 < see2.t[1])
			{
				float segLength = see2.t[1] - see1.t[1];
				float lenToSegEnd = t2 - see1.t[1];
				float limitT = lenToSegEnd / segLength;
				sete.t[1] = limitT;
				sete.n[1].xyz = Vector3.Lerp(sete.n[0].xyz, sete.n[1].xyz, limitT);
			}
			sectEdgesThruEls.Add(sete);
		}
		area = sectEdgesThruEls.Sum(ee =>
			Vector3.Dot(ee.b, Vector3.Normalize(ee.b) * ee.t[1]) * ee.e.t);
		length_actual = sectEdgesThruEls.Sum(ee => Vector3.Dot(ee.b, Vector3.Normalize(ee.b) * ee.t[1]));
	}
	private void _ShiftToUpperBoundary()
	{
		List<ElEdge> ees = sectElEdges.SelectMany(ee => ee.Value)
			.OrderByDescending(ee => ee.t[1]).ToList();

		ElEdge upperEdge = ees[0];
		Vector3 newA = Vector3.Lerp(upperEdge.n[0].xyz, upperEdge.n[1].xyz, upperEdge.t[0]);
		float shift = upperEdge.t[1];
		for (int i = 0; i < ees.Count; i++) { ees[i].t[1] = ees[i].t[1] - shift; }

		sectElEdges = ees
				.GroupBy(ee => ee.elemID)
				.ToDictionary(g => g.Key, g => g.ToList());

		t1 = t1 - shift;
		t2 = t2 - shift;
		a = newA;
	}

	private void _init_Stresses()
	{
		// If all stress records have a blank model ID, assign a default model ID of 0.
		if (StrList.All(s => s.modelID == null))
		{
			foreach (StressRecord s in StrList) { s.modelID = 0; }
		}

		// Reduce the starting StressList to only include records relevant to this section cut.
		StrList = StrList.Where(s => sectElsList.Contains(s.elemID)).ToList();

		MIDs = StrList.Select(s => s.modelID).Distinct().ToArray();
		var stressByEl = StrList.ToLookup(s => s.elemID);

		// Stress Dictionary of 4 in depth keyed by element ID, node ID, model ID and LC number.
		// stressLookup[elemID][node][modelID][LC] = <StressRecord>
		var stressLookup = StrList
		.GroupBy(strEl => strEl.elemID)
		.ToDictionary(gEl => gEl.Key, gEl => gEl
			.GroupBy(strN => strN.node)
			.ToDictionary(gN => gN.Key, gN => gN
				.GroupBy(strMID => strMID.modelID)
				.ToDictionary(gMID => gMID.Key, gMID => gMID
					.ToDictionary(s => s.LC))));

		// Assembling list of unique LC numbers common to all elements in the section cut.
		foreach (int elem in sectElsList)
		{
			LCs = LCs.Intersect(stressByEl[elem].Select(s => s.LC)).ToArray();
			MIDs = MIDs.Intersect(stressByEl[elem].Select(s => s.modelID)).ToArray();
		}

		foreach (int elem in sectElsList)
		{
			List<ElEdge> see = sectElEdges[elem];

			ElEdge elEdge1 = see[0];
			int ee1n1 = elEdge1.n[0].id;
			int ee1n2 = elEdge1.n[1].id;

			ElEdge elEdge2 = see[1];
			int ee2n1 = elEdge2.n[0].id;
			int ee2n2 = elEdge2.n[1].id;

			ElEdge sete = sectEdgesThruEls.First(se => se.elemID == elem);

			foreach (object mid in MIDs)
			{
				foreach (int lc in LCs)
				{
					if (!edgeStresses.ContainsKey(elem)) edgeStresses[elem] =
						new Dictionary<object, Dictionary<int, List<StressRecord>>>();

					if (!edgeStresses[elem].ContainsKey(mid)) edgeStresses[elem][mid] =
						new Dictionary<int, List<StressRecord>>();
					if (!sectSegDict.ContainsKey(mid)) sectSegDict[mid] =
						new Dictionary<int, List<Segment>>();

					if (!edgeStresses[elem][mid].ContainsKey(lc)) edgeStresses[elem][mid][lc] =
						new List<StressRecord>();
					if (!sectSegDict[mid].ContainsKey(lc)) sectSegDict[mid][lc] =
						new List<Segment>();

					if (!stressLookup[elem].ContainsKey(ee1n1)) ee1n1 = -1;
					if (!stressLookup[elem].ContainsKey(ee1n2)) ee1n2 = -1;
					if (!stressLookup[elem].ContainsKey(ee2n1)) ee2n1 = -1;
					if (!stressLookup[elem].ContainsKey(ee2n2)) ee2n2 = -1;

					StressRecord ee1Str1 = stressLookup[elem][ee1n1][mid][lc];
					StressRecord ee1Str2 = stressLookup[elem][ee1n2][mid][lc];
					edgeStresses[elem][mid][lc].Add(StressRecord.Lerp(ee1Str1, ee1Str2, elEdge1.t[0], 1));

					StressRecord ee2Str1 = stressLookup[elem][ee2n1][mid][lc];
					StressRecord ee2Str2 = stressLookup[elem][ee2n2][mid][lc];
					edgeStresses[elem][mid][lc].Add(StressRecord.Lerp(ee2Str1, ee2Str2, elEdge2.t[0], 2));
					Segment seg = new Segment();
					seg.InterpolateStresses(sete, edgeStresses[elem][mid][lc], "lbf, in");

					sectSegments.Add(seg);
					sectSegDict[mid][lc].Add(seg);
				}
			}
		}
		if (sectSegments.Count == 0)
		{
			throw new SectionCutException("No results were found for the input section cut. File not generated.");
		}
	}
	private void _init_OriginalData()
	{
		// Initialize the data table to store the original stress data used to develop the
		// section cuts
		originalData = new object[(StrList.Count + 1), 13];

		string _tL = units.LengthUnit;
		string _sL = units.StressLabel();
		string _mpL = units.MomentPerWidthLabel();
		string _fL = units.ForceLabel();
		string _mL = units.MomentLabel();

		float _tc = UnitUtils.LengthConversionFactor(units.ThickUnit, units.LengthUnit);

		originalData[0, 0] = "Model ID";
		originalData[0, 1] = "Element";
		originalData[0, 2] = "Node";
		originalData[0, 3] = "LC";
		originalData[0, 4] = "Thick. (" + _tL + ")";
		originalData[0, 5] = "Sqx (" + _sL + ")";
		originalData[0, 6] = "Sqy (" + _sL + ")";
		originalData[0, 7] = "Mx (" + _mpL + ")";
		originalData[0, 8] = "My (" + _mpL + ")";
		originalData[0, 9] = "Mxy (" + _mpL + ")";
		originalData[0, 10] = "Sx (" + _sL + ")";
		originalData[0, 11] = "Sy (" + _sL + ")";
		originalData[0, 12] = "Sxy (" + _sL + ")";

		// Populate the object[,] with the stress data base on the number of records being passed.
		for (int i = 0; i < StrList.Count; i++)
		{
			originalData[i + 1, 0] = (object)StrList[i].modelID;
			originalData[i + 1, 1] = (int)(StrList[i].elemID);
			originalData[i + 1, 2] = (int)(StrList[i].node);
			originalData[i + 1, 3] = (int)(StrList[i].LC);
			originalData[i + 1, 4] = (double)(StrList[i].t * _tc);
			originalData[i + 1, 5] = (double)(StrList[i].S.M13);
			originalData[i + 1, 6] = (double)(StrList[i].S.M23);
			originalData[i + 1, 7] = (double)(StrList[i].M.M11);
			originalData[i + 1, 8] = (double)(StrList[i].M.M22);
			originalData[i + 1, 9] = (double)(StrList[i].M.M12);
			originalData[i + 1, 10] = (double)(StrList[i].S.M11);
			originalData[i + 1, 11] = (double)(StrList[i].S.M22);
			originalData[i + 1, 12] = (double)(StrList[i].S.M12);
		}

		if (Expand)
		{
			_origRow = new Dictionary<string, int>();
			for (int i = 0; i < StrList.Count; i++)
			{
				var s = StrList[i];

				string midStr = (s.modelID != null) ? s.modelID.ToString() : "0";
				string key = String.Format("{0}|{1}|{2}|{3}", midStr, s.elemID, s.node, s.LC);

				if (!_origRow.ContainsKey(key))
					_origRow[key] = i + 2;   // +1 for 1-based rows, +1 for header row
			}
		}
	}
	private void _init_SectionCutResults()
	{
		// Initialize single data table (object[,]) for section cut totals
		sectionData = new object[((LCs.Count() * MIDs.Count()) + 2), 18];

		string _tL = units.LengthUnit;
		string _sL = units.StressLabel();
		string _mpL = units.MomentPerWidthLabel();
		string _fL = units.ForceLabel();
		string _mL = units.MomentLabel();

		float _tc = UnitUtils.LengthConversionFactor(units.ThickUnit, units.LengthUnit);

		// Matrix mask to return a matrix containing only Mxy to be used for matrix operations
		Matrix4x4 ipMomUnit = new Matrix4x4(0, 1, 0, 0,
											1, 0, 0, 0,
											0, 0, 0, 0,
											0, 0, 0, 0);
		// Matrix with traction direction vector as its diagonal
		Matrix4x4 dirScale = Matrix4x4.CreateScale(Vector3.Abs(tractionDir));

		// For the table reporting the final section cut forces across a set of elements
		sectionData[0, 0] = "Section Info"; sectionData[0, 5] = "Average Section Cut Stress Components";
		sectionData[0, 13] = "Section Cut Forces"; sectionData[1, 0] = "Model ID";
		sectionData[1, 1] = "LC No.";
		sectionData[1, 2] = "Element";
		sectionData[1, 3] = "Length (in.)";
		sectionData[1, 4] = "Avg. Thick. (" + _tL + ")";
		sectionData[1, 5] = "Sqx (" + _sL + ")";
		sectionData[1, 6] = "Sqy (" + _sL + ")";
		sectionData[1, 7] = "Mx (" + _mpL + ")";
		sectionData[1, 8] = "My (" + _mpL + ")";
		sectionData[1, 9] = "Mxy (" + _mpL + ")";
		sectionData[1, 10] = "Sx (" + _sL + ") [+T]";
		sectionData[1, 11] = "Sy (" + _sL + ") [+T]";
		sectionData[1, 12] = "Sxy (" + _sL + ")";
		sectionData[1, 13] = "P_n (" + _fL + ") [+T]";
		sectionData[1, 14] = "V_ip (" + _fL + ")";
		sectionData[1, 15] = "V_oop (" + _fL + ")";
		sectionData[1, 16] = "M_b (" + _mL + ")";
		sectionData[1, 17] = "M_ip (" + _mL + ")";

		// Assemble section cut total forces record for each load combination
		for (int i = 2; i <= (LCs.Count() * MIDs.Count()) + 1; i++)
		{
			int lc = LCs[(i - 2) % LCs.Count()];// Load combination
			object mid = MIDs[(i - 2) / LCs.Count()]; // Model identifier
			List<Segment> segData = sectSegDict[mid][lc]; // All segment data for each element of
														  // the section cut for current LC
			sectionData[i, 0] = mid; // model identifier
			sectionData[i, 1] = lc; // Load combination
									// System.Collections.Generic.List of elements
			sectionData[i, 2] = String.Join(", ", segData.Select(s => s.elemID).ToArray());

			// Calculation of forces for each load combination by cummulative sum of
			// segment forces
			foreach (Segment s in segData)
			{
				// Element information
				double t = s.element.t * _tc; // Thickness of current element
				float length = Vector3.Distance(s.p2.xyz, s.p1.xyz); // Length of segment through element
				double eArea = length * t;

				// Element segment stresses
				Matrix4x4 avgSig = (s.S1 + s.S2) * (float)0.5; // Average stress in segment through element
				Matrix4x4 avgMom = (s.M1 + s.M2) * (float)0.5; // Average moments in segment through element

				// Calculating traction vectors, and individual stress components
				Vector3 tractVec = Vector3.Transform(tractionDir, avgSig);
				float normStress = Vector3.Dot(tractVec, tractionDir);
				Vector3 shearStresses = tractVec - normStress * tractionDir;
				float oopVStress = tractVec.Z;
				float ipVStress = Vector3.Dot((shearStresses - oopVStress * Vector3.UnitZ), u);

				float ipMom = avgMom.M12; // XY-twisting moment
				Matrix4x4 bMoms = avgMom - ipMomUnit * ipMom; // Matrix of X and Y bending moments
				float bendMom = bMoms.M11 * tractionDir.X + bMoms.M22 * tractionDir.Y; // Section bending moment

				avgSig = dirScale * avgSig; // Matrix of X and Y axial stresses relative to the section cut plane
				avgSig.M12 = avgSig.M21 = ipVStress; // Re-assigning in-plane shear stress to the matrix
				avgMom = dirScale * avgMom; // Matrix of X and Y bending moments relative to the section cut plane
				avgMom.M12 = avgMom.M21 = ipMom; // Re-assigning XY twisting moment to the matrix

				// Calculation of section length:
				sectionData[i, 3] = (double)(sectionData[i, 3] ?? 0.0) + length;
				// Weighted section thickness:
				sectionData[i, 4] = (double)(sectionData[i, 4] ?? 0.0) + t * length;

				// Weighted section stresses:
				sectionData[i, 5] = (double)(sectionData[i, 5] ?? 0.0) + avgSig.M13 * eArea; // Integration of Sqx across section area
				sectionData[i, 6] = (double)(sectionData[i, 6] ?? 0.0) + avgSig.M23 * eArea; // Integration of Sqy across section area
				sectionData[i, 7] = (double)(sectionData[i, 7] ?? 0.0) + avgMom.M11 * length; // Integration of Mx across section length
				sectionData[i, 8] = (double)(sectionData[i, 8] ?? 0.0) + avgMom.M22 * length; // Integration of My across section length
				sectionData[i, 9] = (double)(sectionData[i, 9] ?? 0.0) + avgMom.M12 * length; // Integration of Mxy across section length
				sectionData[i, 10] = (double)(sectionData[i, 10] ?? 0.0) + avgSig.M11 * eArea; // Integration of Sx across section area
				sectionData[i, 11] = (double)(sectionData[i, 11] ?? 0.0) + avgSig.M22 * eArea; // Integration of Sy across section area
				sectionData[i, 12] = (double)(sectionData[i, 12] ?? 0.0) + avgSig.M12 * eArea; // Integration of Sxy across section area

				// Calculation of section Fn:
				sectionData[i, 13] = (double)(sectionData[i, 13] ?? 0.0) + eArea * normStress;
				// Calculation of section Vip:
				sectionData[i, 14] = (double)(sectionData[i, 14] ?? 0.0) + eArea * ipVStress;
				// Calculation of section Voop:
				sectionData[i, 15] = (double)(sectionData[i, 15] ?? 0.0) + eArea * oopVStress;
				// Calculation of section Mb:
				sectionData[i, 16] = (double)(sectionData[i, 16] ?? 0.0) + length * bendMom;
				// Calculation of section Mip:
				sectionData[i, 17] = (double)(sectionData[i, 17] ?? 0.0) + length * ipMom;
			}
			double sectLength = (double)sectionData[i, 3];
			double sectArea = (double)sectionData[i, 4];
			// Average section thickness:
			sectionData[i, 4] = (double)sectionData[i, 4] / sectLength;
			// Average section stresses:
			sectionData[i, 5] = (double)sectionData[i, 5] / sectArea; // Sqx
			sectionData[i, 6] = (double)sectionData[i, 6] / sectArea; // Sqy
			sectionData[i, 7] = (double)sectionData[i, 7] / sectLength; // Mx
			sectionData[i, 8] = (double)sectionData[i, 8] / sectLength; // My
			sectionData[i, 9] = (double)sectionData[i, 9] / sectLength; // Mxy
			sectionData[i, 10] = (double)sectionData[i, 10] / sectArea; // Sx
			sectionData[i, 11] = (double)sectionData[i, 11] / sectArea; // Sy
			sectionData[i, 12] = (double)sectionData[i, 12] / sectArea; // Sxy
		}
	}
	private void _init_TBeamSectionCutResults()
	{
		string _tL = units.LengthUnit;
		string _sL = units.StressLabel();
		string _mpL = units.MomentPerWidthLabel();
		string _fL = units.ForceLabel();
		string _mL = units.MomentLabel();

		float _tc = UnitUtils.LengthConversionFactor(units.ThickUnit, units.LengthUnit);

		// Initialize single data table (object[,]) for section cut totals
		sectionData = new object[((LCs.Count() * MIDs.Count()) + 2), 16];

		// For the table reporting the final section cut forces across a set of elements
		sectionData[0, 0] = "Section Info"; sectionData[0, 6] = "Average Section Cut Stress Components";
		sectionData[0, 11] = "Section Cut Forces"; sectionData[1, 0] = "Model ID";
		sectionData[1, 1] = "LC No.";
		sectionData[1, 2] = "Element";
		sectionData[1, 3] = "Web Length (in.)";
		sectionData[1, 4] = "Flange Length (in.)";
		sectionData[1, 5] = "Avg. Thick. (" + _tL + ")";
		sectionData[1, 6] = "s_n (" + _sL + ") [+T]";
		sectionData[1, 7] = "v_v (" + _sL + ")";
		sectionData[1, 8] = "v_oop (" + _sL + ")";
		sectionData[1, 9] = "m_b (" + _mpL + ")";
		sectionData[1, 10] = "m_xy (" + _mpL + ")";
		sectionData[1, 11] = "P_n (" + _fL + ") [+T]";
		sectionData[1, 12] = "V_ip (" + _fL + ")";
		sectionData[1, 13] = "V_oop (" + _fL + ")";
		sectionData[1, 14] = "M_b (" + _mL + ")";
		sectionData[1, 15] = "M_ip (" + _mL + ")";

		// Assemble section cut total forces record for each load combination
		for (int i = 2; i <= (LCs.Count() * MIDs.Count()) + 1; i++)
		{
			int lc = LCs[(i - 2) % LCs.Count()];// Load combination
			object mid = MIDs[(i - 2) / LCs.Count()]; // Model identifier
			List<Segment> segData = sectSegDict[mid][lc]; // All segment data for each element of
														  // the section cut for current LC
			sectionData[i, 0] = mid; // model identifier
			sectionData[i, 1] = lc; // Load combination
									// System.Collections.Generic.List of elements
			sectionData[i, 2] = String.Join(", ", segData.Select(s => s.elemID).ToArray());

			// Calculation of forces for each load combination by cummulative sum of
			// segment forces
			foreach (Segment s in segData)
			{
				// Element information
				int elem = s.elemID;
				double t = s.element.t * _tc; // Thickness of current element
				float length = Vector3.Distance(s.p2.xyz, s.p1.xyz); // Length of segment through element
				double eArea = length * t;

				// Element segment stresses
				Matrix4x4 avgSig = (s.S1 + s.S2) * (float)0.5; // Average stress in segment through element
				Matrix4x4 avgMom = (s.M1 + s.M2) * (float)0.5; // Average moments in segment through element

				float p_n = 0;
				float v_v = 0;
				float v_oop = 0;
				float m_b = 0;
				float m_xy = 0;

				//if (s.LC == 1)
				//{
				//	Console.WriteLine("\nElement: {0}, Model: {1}", s.elemID, s.modelID);
				//	Console.WriteLine("------------------------------------------------");
				//}

				// Get average stresses and moments for the segment depending on whether elem is part of
				// the flange or the web
				if (webEls.Contains(elem))
				{
					p_n = avgSig.M11;
					v_v = avgSig.M12;
					v_oop = avgSig.M13;
					m_b = GetMomentContribution(s) / length;
					//if (s.LC == 1)
					//	Console.WriteLine("M_webE: {0} lb-in/in", m_b);
				}
				else if (flangeEls.Contains(elem))
				{
					p_n = avgSig.M22;
					v_v = avgSig.M23;
					v_oop = avgSig.M12;
					m_b = -avgMom.M22;
					//if (s.LC == 1)
					//	Console.WriteLine("M_flaE: {0} lb-in/in", m_b);
				}
				m_xy = avgMom.M12;

				// Element segment forces
				double P_n = p_n * eArea; // Integration of axial across section area
				double V_v = v_v * eArea; // Integration of vertical shear across section area
				double V_oop = v_oop * eArea; // Integration of horizontal shear across section area
				double M_b = m_b * length; // Integration of weak axis bending across section length
				double M_xy = m_xy * length; // Integration of torsion(?) across section length

				// Calculation of section length:
				if (webEls.Contains(elem))
					sectionData[i, 3] = (double)(sectionData[i, 3] ?? 0.0) + length; // Web length
				if (flangeEls.Contains(elem))
					sectionData[i, 4] = (double)(sectionData[i, 4] ?? 0.0) + length; // Flange length

				// Weighted section thickness:
				sectionData[i, 5] = (double)(sectionData[i, 5] ?? 0.0) + eArea;

				// Weighted section stresses:
				sectionData[i, 6] = (double)(sectionData[i, 6] ?? 0.0) + P_n;
				sectionData[i, 7] = (double)(sectionData[i, 7] ?? 0.0) + V_v;
				sectionData[i, 8] = (double)(sectionData[i, 8] ?? 0.0) + V_oop;
				sectionData[i, 9] = (double)(sectionData[i, 9] ?? 0.0) + M_b;
				sectionData[i, 10] = (double)(sectionData[i, 10] ?? 0.0) + M_xy;

				// Calculation of section p_n:
				sectionData[i, 11] = (double)(sectionData[i, 11] ?? 0.0) + P_n;
				// Calculation of section v_v:
				sectionData[i, 12] = (double)(sectionData[i, 12] ?? 0.0) + V_v;
				// Calculation of section v_oop:
				sectionData[i, 13] = (double)(sectionData[i, 13] ?? 0.0) + V_oop;
				// Calculation of section m_b:
				sectionData[i, 14] = (double)(sectionData[i, 14] ?? 0.0) + M_b;
				// Calculation of section m_xy:
				sectionData[i, 15] = (double)(sectionData[i, 15] ?? 0.0) + M_xy;
			}
			double sectLength = (double)sectionData[i, 3] + (double)sectionData[i, 4];
			double sectArea = (double)sectionData[i, 5];
			// Average section thickness:
			sectionData[i, 5] = (double)sectionData[i, 5] / sectLength;
			// Average section stresses:
			sectionData[i, 6] = (double)sectionData[i, 6] / sectArea; // p_n
			sectionData[i, 7] = (double)sectionData[i, 7] / sectArea; // v_v
			sectionData[i, 8] = (double)sectionData[i, 8] / sectArea; // v_oop
			sectionData[i, 9] = (double)sectionData[i, 9] / sectLength; // m_b
			sectionData[i, 10] = (double)sectionData[i, 10] / sectLength; // m_xy
		}
	}
	private void GetNeutralAxisAxialStr(double y_NA)
	{
		int eID = sectElEdges.First(ee => ee.Value[0].t[1] >= y_NA &&
			y_NA >= ee.Value[1].t[1]).Key;

		List<ElEdge> ees = sectElEdges[eID];
		float elLength = ees[0].t[1] - ees[1].t[1];
		float lenToNA = (float)(ees[0].t[1] - y_NA);
		float t_NA = lenToNA / elLength;

		foreach (object kMID in sectSegDict.Keys)
		{
			if (!beamNAStresses.ContainsKey(kMID)) beamNAStresses[kMID] =
						new Dictionary<int, Matrix4x4>();
			foreach (int kLC in sectSegDict[kMID].Keys)
			{
				Segment seg = sectSegDict[kMID][kLC].First(s => s.elemID == eID);
				if (!beamNAStresses[kMID].ContainsKey(kLC)) beamNAStresses[kMID][kLC] =
						Matrix4x4.Lerp(seg.S1, seg.S2, t_NA);
			}
		}
	}
	private float GetMomentContribution(Segment seg)
	{
		float thick = seg.element.t;
		List<ElEdge> ees = sectElEdges[seg.elemID];
		Matrix4x4 s_NA = beamNAStresses[seg.modelID][seg.LC];
		Matrix4x4 s1 = seg.S1;
		Matrix4x4 s2 = seg.S2;

		float sig_a = s1.M11 - s_NA.M11;
		float sig_b = s2.M11 - s_NA.M11;

		float ya = ees[0].t[1];
		float yb = ees[1].t[1];
		float h = yb - ya;

		if (sig_a * sig_b < 0)
		{
			//Console.WriteLine("LC {0}, El {1}", seg.LC, seg.elemID);
			float y0 = ya - h * sig_a / (sig_b - sig_a);
			float h_top = y0 - ya;
			float h_bot = yb - y0;

			float yC_top = ya + h_top / 3;
			float yC_bot = y0 + 2 * h_bot / 3;

			float F_top = sig_a * thick * h_top / 2;
			float F_bot = sig_b * thick * h_bot / 2;
			//Console.WriteLine("The distance from the top of the element to 0 is {0}", y0);
			//if (seg.LC == 1)
			//	Console.WriteLine("M_triangles: {0} lb-in", yC_top * F_top + yC_bot * F_bot);
			return yC_top * F_top + yC_bot * F_bot;
		}
		else
		{
			float yC = ya + (h / 3) * ((sig_a + 2 * sig_b) / (sig_a + sig_b));
			float F = (float)(0.5 * (sig_a + sig_b) * thick * h);

			//if (seg.LC == 1)
			//{
			//	Console.WriteLine("d_trap: {0} in", yC);
			//	Console.WriteLine("F_trap: {0} lbf", F);
			//	Console.WriteLine("M_trap: {0} lb-in", yC * F);
			//}
			return yC * F;
		}
	}

	public void CreateOutputExcel()
	{
		string ts = DateTime.Now.ToString("MMddyy-HHmmss");
		string cutName = (cut["name"] != null) ? cut["name"].ToString() : "";
		string suffix = Expand ? "_VV" : "";
		string fileName = cutName != ""
			? String.Format("STAADCut_{0}{1}.xlsx", cutName, suffix)
			: String.Format("STAADCut_{0}{1}.xlsx", ts, suffix);
		//Console.WriteLine("Creating new Excel file");
		OutputFile = new ExcelFile(fileName, OutputDir);
		OutputFile.OpenExcel();
		//Console.WriteLine("Opened Excel");

		try
		{
			if (Expand)
			{
				OutputFile.RenameSheet(1, "Original Data");
				_WriteOriginalDataSheet(OutputFile.GetSheet(1));

				foreach (object mid in MIDs)
					foreach (int lc in LCs)
						_WriteExpandedSheet(
							OutputFile.AddSheet(_SheetName(mid, lc)),
							mid, lc);
			}
			else
			{
				OutputFile.RenameSheet(1, "Original Data");
				//Console.WriteLine("Writing original data to file");
				_WriteOriginalDataSheet(OutputFile.GetSheet(1));

				//Console.WriteLine("Writing section cut summary data to file");
				OutputFile.AddSheet("Section Cut Summary");
				_WriteSummarySheet(OutputFile.GetSheet(2));
			}
		}
		finally
		{
			//Console.WriteLine("Closing Excel file");
			OutputFile.CloseExcel();
		}
	}
	private static string _SheetName(object mid, int lc)
	{
		string midStr = mid != null ? mid.ToString() : "0";
		string midBase = System.IO.Path.GetFileNameWithoutExtension(midStr);
		string lcSuffix = "_LC" + lc.ToString();
		int maxMid = 31 - lcSuffix.Length;
		if (midBase.Length > maxMid) midBase = midBase.Substring(0, maxMid);
		return midBase + lcSuffix;
	}

	// =========================================================================
	// SUMMARY SHEET
	// Writes sectionData as a filterable table with frozen headers.
	// =========================================================================
	private void _WriteSummarySheet(Excel.Worksheet ws)
	{
		_SetSummaryWidths(ws);
		int nCols = sectionData.GetLength(1);

		OutputFile.WriteBlock(ws, 1, 1, sectionData);

		// Row 1: section info band
		OutputFile.Fill(ws, 1, 1, 1, nCols, ExcelFile.RGB(31, 78, 121));
		OutputFile.FontColor(ws, 1, 1, 1, nCols, ExcelFile.RGB(255, 255, 255));
		OutputFile.Bold(ws, 1, 1, 1, nCols);

		// Row 2: column headers
		OutputFile.Fill(ws, 2, 1, 2, nCols, ExcelFile.RGB(217, 225, 242));
		OutputFile.Bold(ws, 2, 1, 2, nCols);

		// Alternating row shading on data rows
		int nRows = sectionData.GetLength(0);
		for (int r = 3; r < nRows + 1; r += 2)
			OutputFile.Fill(ws, r, 1, r, nCols, ExcelFile.RGB(242, 242, 242));

		// Number formats on data rows
		OutputFile.NumberFmt(ws, 3, 4, nRows, 5, "0.000"); // Length, Thickness
		OutputFile.NumberFmt(ws, 3, 6, nRows, 18, "0.00"); // Stresses and forces

		OutputFile.Filter(ws, 2, 1, nCols);
		OutputFile.Freeze(ws, 2);

		Console.WriteLine("Summary written: {0} result rows.", sectionData.GetLength(0) - 2);
	}
	// =========================================================================
	// ORIGINAL DATA SHEET
	// Writes the full originalData table - the V&V expanded sheets reference
	// cells here instead of repeating raw values.
	// =========================================================================
	private void _WriteOriginalDataSheet(Excel.Worksheet ws)
	{
		// Col widths matching originalData layout:
		// 1:ModelID  2:ElemID  3:Node  4:LC  5:Thick  6-13:stresses
		OutputFile.ColWidth(ws, 1, 3, 10.0);
		OutputFile.ColWidth(ws, 4, 4, 8.0);
		OutputFile.ColWidth(ws, 5, 5, 12.0);
		OutputFile.ColWidth(ws, 6, 13, 13.0);

		int nCols = originalData.GetLength(1);
		int nRows = originalData.GetLength(0);

		OutputFile.WriteBlock(ws, 1, 1, originalData);
		OutputFile.Fill(ws, 1, 1, 1, nCols, ExcelFile.RGB(68, 114, 196));
		OutputFile.FontColor(ws, 1, 1, 1, nCols, ExcelFile.RGB(255, 255, 255));
		OutputFile.Bold(ws, 1, 1, 1, nCols);

		//for (int r = 3; r < nRows + 1; r += 2)
		//	OutputFile.Fill(ws, r, 1, r, nCols, ExcelFile.RGB(242, 242, 242));

		OutputFile.NumberFmt(ws, 2, 5, nRows, 5, "0.000");  // Thickness
		OutputFile.NumberFmt(ws, 2, 6, nRows, 13, "0.00");   // All stress components

		OutputFile.Filter(ws, 1, 1, nCols);
		OutputFile.Freeze(ws, 1);
	}
	// =========================================================================
	// EXPANDED V&V SHEET  (one per MID / LC combination)
	//
	// Layout (all sections use the same 27-column canvas):
	//   Row 1-2   : Info header (cut name, MID, LC)
	//   Step 1    : Source stresses â€” formulas reference 'Original Data' sheet
	//   Step 2    : Edge interpolation â€” formulas reference Step 1 cells
	//   Step 3    : Element averages  â€” formulas reference Step 2 cells
	//   Step 4    : Force integration â€” formulas reference Step 3 cells +
	//               inlined tractionDir / u vector constants
	//   Totals    : SUM of Step 4 force columns
	//
	// originalData column layout (Excel, 1-based):
	//   1:ModelID  2:ElemID  3:Node  4:LC  5:Thickness
	//   6:Sqx  7:Sqy  8:Mx  9:My  10:Mxy  11:Sx  12:Sy  13:Sxy
	// =========================================================================
	private void _WriteExpandedSheet(Excel.Worksheet ws, object mid, int lc)
	{
		string _tL = units.LengthUnit;
		string _sL = units.StressLabel();
		string _mpL = units.MomentPerWidthLabel();
		string _fL = units.ForceLabel();
		string _mL = units.MomentLabel();

		_SetExpandedWidths(ws);
		Dictionary<int, ElEdge> seteByElem = sectEdgesThruEls.ToDictionary(se => se.elemID);
		// â”€â”€ Column index constants â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
		// Step 1  (source data)
		const int S1_E = 1, S1_N = 2, S1_R = 3, S1_SQX = 4, S1_T = 12;
		// const int S1_SQX = 4, S1_SQY = 5, S1_MX = 6, S1_MY = 7, S1_MXY = 8,
		// 		  S1_SX = 9, S1_SY = 10, S1_SXY = 11, S1_T = 12;

		// Step 2  (edge interpolation â€” same stress col offsets as Step 1)
		const int S2_E = 1, S2_EDG = 2, S2_T0 = 3, S2_SQX = 4, S2_T = 12;
		//const int S2_E = 1, S2_EDG = 2, S2_T0 = 3;
		//const int S2_SQX = 4, S2_SQY = 5, S2_MX = 6, S2_MY = 7, S2_MXY = 8,
		//		  S2_SX = 9, S2_SY = 10, S2_SXY = 11, S2_T = 12;

		// Step 3: boundary interpolation
		// Col 3 is intentionally blank so that stress columns (4â€“11) align
		// with those in Steps 2 and 4, making cross-step formula references
		// use the same column index throughout.
		const int S3_E = 1, S3_T1 = 2, S3_SQX = 4, S3_TK = 12;  // element ID | sete.t[1] value
																// stress components (col 3 blank)

		//const int S3_SQX = 4, S3_SQY = 5,
		//		  S3_MX = 6, S3_MY = 7, S3_MXY = 8,
		//		  S3_SX = 9, S3_SY = 10, S3_SXY = 11, S3_TK = 12;

		// Step 4  (element averages)
		const int S4_E = 1, S4_L = 2, S4_T = 3, S4_SQX = 4;
		//const int S4_SQX = 4, S4_SQY = 5, S4_MX = 6, S4_MY = 7, S4_MXY = 8,
		//		  S4_SX = 9, S4_SY = 10, S4_SXY = 11;

		// Step 5  (force integration â€” 27 columns total)
		const int S5_E = 1, S5_L = 2, S5_T = 3;
		const int S5_TDX = 4, S5_TDY = 5, S5_UX = 6, S5_UY = 7;
		const int S5_SQX = 8, S5_SQY = 9, S5_MX = 10, S5_MY = 11, S5_MXY = 12,
				  S5_SX = 13, S5_SY = 14, S5_SXY = 15;
		// Intermediate vector quantities
		const int S5_TX = 16;   // tractVec.X
		const int S5_TY = 17;   // tractVec.Y
		const int S5_VP = 18;   // tractVec.Z  = Voop_psi
		const int S5_NS = 19;   // normStress_psi
								//const int S5_SHX = 20;  // shearX
								//const int S5_SHY = 21;  // shearY
		const int S5_VS = 20;   // Vip_psi
								// Final force components
		const int S5_FN = 21, S5_VIP = 22, S5_VOP = 23, S5_MB = 24, S5_MIP = 25;

		const int MAX_COLS = 25;

		// â”€â”€ Colors â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
		int cInfo = ExcelFile.RGB(31, 78, 121);   // dark navy
		int cS1 = ExcelFile.RGB(68, 114, 196);  // blue
		int cS2 = ExcelFile.RGB(84, 130, 53);   // green
		int cS3 = ExcelFile.RGB(207, 156, 59);   // gold
		int cS4 = ExcelFile.RGB(132, 60, 12);   // burnt orange
		int cS5 = ExcelFile.RGB(99, 37, 35);    // dark red
		int cTot = ExcelFile.RGB(32, 32, 32);    // near-black
		int cSub = ExcelFile.RGB(235, 241, 247); // light blue-grey for sub-headers
		int cAlt = ExcelFile.RGB(248, 248, 248); // alternating row tint
		int white = ExcelFile.RGB(255, 255, 255);

		List<Segment> segs = sectSegDict[mid][lc];
		string midStr = (mid != null) ? mid.ToString() : "0";
		string od = "Original Data";    // sheet name for SRef formulas
		int row = 1;

		// Row-position trackers â€” populated in each step, consumed by later steps
		var step1DataR = new Dictionary<int, int>(); // elem -> data row in Step 1
		var step2DataR = new Dictionary<int, int>(); // elem -> data row in Step 2
		var step3DataR = new Dictionary<int, int>(); // elem -> data row in Step 3
		var step4DataR = new Dictionary<int, int>(); // elem -> data row in Step 4
		var step5DataR = new Dictionary<int, int>(); // elem -> data row in Step 5

		// â”€â”€ Info header â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
		OutputFile.Fill(ws, row, 1, row, MAX_COLS, cInfo);
		OutputFile.FontColor(ws, row, 1, row, MAX_COLS, white);
		OutputFile.Bold(ws, row, 1, row, MAX_COLS);
		OutputFile.Write(ws, row, 1,
			String.Format("Section Cut: {0}  |  Axis: {1}  |  Length: {2} {3}",
						cut["name"], axis, length, units.LengthUnit) +
			String.Format("  |  t1: {0} {2}  |  t2: {1} {2}", t1, t2, units.LengthUnit));
		row++;

		OutputFile.Fill(ws, row, 1, row, MAX_COLS, cInfo);
		OutputFile.FontColor(ws, row, 1, row, MAX_COLS, white);
		OutputFile.Bold(ws, row, 1, row, MAX_COLS);
		OutputFile.Write(ws, row, 1, String.Format("Model ID: {0}  |  Load Case: {1}", mid, lc));
		row += 2; // blank separator


		// =====================================================================
		// STEP 1 â€” Source stress data (cell references into 'Original Data')
		// 4 rows per element: ee1-n1 | ee1-n2 | ee2-n1 | ee2-n2
		// =====================================================================
		row = _WriteSectionBanner(ws, row, MAX_COLS, cS1,
			"STEP 1 â€” Original STAAD Stress Records  " +
			"(formulas reference 'Original Data' tab)");

		string[] s1Hdr = {
			"Element","Node","Role",
			"Sqx (" + _sL + ")", "Sqy (" + _sL + ")", "Mx (" + _mpL + ")", "My (" + _mpL + ")",
			"Mxy (" + _mpL + ")", "Sx (" + _sL + ")", "Sy (" + _sL + ")", "Sxy (" + _sL + ")",
			"Thick. (" + _tL + ")"
		};
		row = _WriteSubHeader(ws, row, s1Hdr, cSub);

		foreach (Segment seg in segs)
		{
			int elem = seg.element.id;
			ElEdge[] ees = sectElEdges[elem].ToArray();
			ElEdge ee1 = ees[0], ee2 = ees[1];
			int[] nodes = { ee1.n[0].id, ee1.n[1].id, ee2.n[0].id, ee2.n[1].id };
			string[] roles = { "Edge1-n1", "Edge1-n2", "Edge2-n1", "Edge2-n2" };

			step1DataR[elem] = row;

			for (int ni = 0; ni < 4; ni++)
			{
				int nid = nodes[ni];
				string key = String.Format("{0}|{1}|{2}|{3}", midStr, elem, nid, lc);
				//var key = (midStr, elem, nid, lc);
				int odRow = (_origRow != null && _origRow.ContainsKey(key))
							? _origRow[key] : -1;

				OutputFile.Write(ws, row, S1_E, elem);
				OutputFile.Write(ws, row, S1_N, nid);
				OutputFile.Write(ws, row, S1_R, roles[ni]);

				if (odRow > 0)
				{
					// Sqxâ€¦Sxy: originalData Excel cols 6â€“13
					for (int sc = 0; sc < 8; sc++)
						OutputFile.Fmla(ws, row, S1_SQX + sc, ExcelFile.SRef(od, odRow, 6 + sc));
					// Thickness: originalData Excel col 5
					OutputFile.Fmla(ws, row, S1_T, ExcelFile.SRef(od, odRow, 5));
				}
				else
				{
					OutputFile.Write(ws, row, S1_SQX, "â€” record not found in Original Data");
				}

				if (ni % 2 == 1)
					//OutputFile.Fill(ws, row, 1, row, MAX_COLS, cAlt);
					OutputFile.Fill(ws, row, 1, row, 12, cAlt);
				row++;
			}
		}
		row++; // blank between steps


		// =====================================================================
		// STEP 2 â€” Linear interpolation along each element edge
		// Formula: = n1_cell + t[0] * (n2_cell âˆ’ n1_cell)
		// 2 rows per element: one for each edge
		// =====================================================================
		row = _WriteSectionBanner(ws, row, MAX_COLS, cS2,
			"STEP 2 â€” Linear Interpolation Along Element Edges  " +
			"|  formula: = node1 + t[0] Ã— (node2 âˆ’ node1)");

		string[] s2Hdr = {
			"Element","Edge","t[0]",
			"Sqx (" + _sL + ")", "Sqy (" + _sL + ")", "Mx (" + _mpL + ")", "My (" + _mpL + ")",
			"Mxy (" + _mpL + ")", "Sx (" + _sL + ")", "Sy (" + _sL + ")", "Sxy (" + _sL + ")",
			"Thick. (" + _tL + ")"
		};
		row = _WriteSubHeader(ws, row, s2Hdr, cSub);

		foreach (Segment seg in segs)
		{
			int elem = seg.element.id;
			ElEdge[] ees = sectElEdges[elem].ToArray();

			step2DataR[elem] = row;

			for (int ei = 0; ei < 2; ei++)          // edge 0 and edge 1
			{
				float t0 = ees[ei].t[0];
				int rN1 = step1DataR[elem] + ei * 2;      // Step 1 row for this edge n1
				int rN2 = step1DataR[elem] + ei * 2 + 1;  // Step 1 row for this edge n2

				OutputFile.Write(ws, row, S2_E, elem);
				OutputFile.Write(ws, row, S2_EDG, String.Format("Edge {0}", ei + 1));
				OutputFile.Write(ws, row, S2_T0, t0);

				for (int sc = 0; sc < 8; sc++)
				{
					int col = S2_SQX + sc;
					string n1a = ExcelFile.Addr(rN1, col);
					string n2a = ExcelFile.Addr(rN2, col);
					// t0 is a written constant in the same row, use absolute col ref
					string t0a = String.Format("${0}{1}", ExcelFile.Col(S2_T0), row);
					OutputFile.Fmla(ws, row, col, String.Format("={0}+{1}*({2}-{0})", n1a, t0a, n2a));
				}
				// Thickness: same for every node of the element, ref ee1-n1 row
				OutputFile.Fmla(ws, row, S2_T,
					String.Format("={0}", ExcelFile.Addr(step1DataR[elem], S1_T)));

				if (ei == 1)
					//OutputFile.Fill(ws, row, 1, row, MAX_COLS, cAlt);
					OutputFile.Fill(ws, row, 1, row, 12, cAlt);
				row++;
			}
		}
		row++;

		// â”€â”€ STEP 3: Boundary interpolation â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
		// formula: actual_end = ip1 + sete.t[1] * (ip2 â€“ ip1)
		// For elements that span the full cut window sete.t[1] = 1, so the formula
		// degenerates to actual_end = ip2 (no change).  For elements where the cut
		// boundary falls inside the element, 0 < sete.t[1] < 1 and the actual
		// integration endpoint is clipped accordingly.
		row = _WriteSectionBanner(ws, row, MAX_COLS, cS3,
			"STEP 3 â€” Boundary Clip  "
			+ "|  formula: actual_end = ip1 + t[1] Ã— (ip2 âˆ’ ip1)");

		string[] s3Hdr = {
			"Element", "t[1]", "---",
			"Sqx (" + _sL + ")", "Sqy (" + _sL + ")", "Mx (" + _mpL + ")", "My (" + _mpL + ")",
			"Mxy (" + _mpL + ")", "Sx (" + _sL + ")", "Sy (" + _sL + ")", "Sxy (" + _sL + ")",
			"Thick. (" + _tL + ")"
		};
		row = _WriteSubHeader(ws, row, s3Hdr, cSub);

		int altIdx = 0;
		foreach (Segment seg in segs)
		{
			int elem = seg.element.id;

			// Look up the sete (section edge through element) to get t[1].
			// sete.t[1] = 1.0 for interior elements, 0 < t[1] < 1 for boundary ones.
			ElEdge sete;
			float seteT1 = seteByElem.TryGetValue(elem, out sete) ? sete.t[1] : 1f;
			//for (int si = 0; si < sectEdgesThruEls.Count; si++)
			//{
			//	if (sectEdgesThruEls[si].elemID == elem)
			//	{
			//		sete = sectEdgesThruEls[si];
			//		break;
			//	}
			//}
			//float seteT1 = (sete != null) ? sete.t[1] : 1f;

			step3DataR[elem] = row;

			OutputFile.Write(ws, row, S3_E, elem);
			OutputFile.Write(ws, row, S3_T1, seteT1);     // constant â€” referenced by formula below

			// Absolute-column reference to the sete.t[1] cell so the formula is
			// self-contained when the sheet is inspected cell by cell.
			string t1Cell = "$" + ExcelFile.Col(S3_T1) + row.ToString();

			for (int sc = 0; sc < 8; sc++)
			{
				int col = S3_SQX + sc;                    // S3_SQX = 4 aligns with S2_SQX = 4
				string ip1 = ExcelFile.Addr(step2DataR[elem], col);   // edge 1 (lower bound)
				string ip2 = ExcelFile.Addr(step2DataR[elem] + 1, col);   // edge 2 (upper bound, full)
				OutputFile.Fmla(ws, row, col,
					"=" + ip1 + "+" + t1Cell + "*(" + ip2 + "-" + ip1 + ")");
			}

			// Thickness: pass through from Step 2
			OutputFile.Fmla(ws, row, S3_TK, "=" + ExcelFile.Addr(step2DataR[elem], S2_T));

			if (altIdx % 2 == 1)
				OutputFile.Fill(ws, row, 1, row, 12, cAlt);
			altIdx++;
			row++;
		}
		row++;   // blank separator before Step 4

		// =====================================================================
		// STEP 4 â€” Average stress through each element
		// Formula: = (edge1_interp + edge2_interp) / 2
		// 1 row per element
		// =====================================================================
		row = _WriteSectionBanner(ws, row, MAX_COLS, cS4,
			"STEP 4 â€” Average Stress Through Element  " +
			"|  formula: = ( edge1 + edge2 ) Ã· 2");

		string[] s4Hdr = {
			"Element","Length (ft)","Thick. (in)",
			"Sqx (" + _sL + ")", "Sqy (" + _sL + ")", "Mx (" + _mpL + ")", "My (" + _mpL + ")",
			"Mxy (" + _mpL + ")", "Sx (" + _sL + ")", "Sy (" + _sL + ")", "Sxy (" + _sL + ")"
		};
		row = _WriteSubHeader(ws, row, s4Hdr, cSub);

		altIdx = 0;
		foreach (Segment seg in segs)
		{
			int elem = seg.element.id;
			float len = Vector3.Distance(seg.p2.xyz, seg.p1.xyz);

			step4DataR[elem] = row;

			OutputFile.Write(ws, row, S4_E, elem);
			OutputFile.Write(ws, row, S4_L, len);
			OutputFile.Fmla(ws, row, S4_T, String.Format("={0}", ExcelFile.Addr(step3DataR[elem], S3_TK)));

			for (int sc = 0; sc < 8; sc++)
			{
				int col = S4_SQX + sc;
				string e1 = ExcelFile.Addr(step2DataR[elem], col); // edge 1 interp
				string e2 = ExcelFile.Addr(step3DataR[elem], col); // edge 2 interp
				OutputFile.Fmla(ws, row, col, String.Format("=({0}+{1})/2", e1, e2));
			}

			if (altIdx % 2 == 1)
				//OutputFile.Fill(ws, row, 1, row, MAX_COLS, cAlt);
				OutputFile.Fill(ws, row, 1, row, 11, cAlt);
			altIdx++;
			row++;
		}
		row++;


		// =====================================================================
		// STEP 5 â€” Force integration
		//
		// tractionDir and u are written as constants per row so every formula
		// is self-contained and easy to inspect cell-by-cell.
		//
		// tractVec = Transform(tractionDir, avgSig)  [row-vector Ã— matrix]:
		//   tractX = tDxÂ·Sx  + tDyÂ·Sxy
		//   tractY = tDxÂ·Sxy + tDyÂ·Sy
		//   tractZ = tDxÂ·Sqx + tDyÂ·Sqy         â† this is Voop_psi
		//
		// normStress = tractXÂ·tDx + tractYÂ·tDy
		// Vip_psi    = tractXÂ·ux + tractYÂ·uy
		//
		// Forces:
		//   Fn   = normStress_psi Â· t Â· L
		//   Vip  = Vip_psi       Â· t Â· L
		//   Voop = Voop_psi      Â· t Â· L
		//   Mb   = (tDxÂ·Mx + tDyÂ·My) Â· L
		//   Mip  = Mxy Â· L
		// =====================================================================
		row = _WriteSectionBanner(ws, row, MAX_COLS, cS5,
			"STEP 5 â€” Force Integration  " +
			"|  Fn, Vip, Voop = stress Ã— t Ã— L   Mb, Mip = moment Ã— L");

		string[] s5Hdr = {
			"Element","Length (" + units.LengthUnit + ")","Thick. (" + units.LengthUnit + ")",
			"tDx","tDy","ux","uy",
			"Sqx_avg (" + _sL + ")","Sqy_avg (" + _sL + ")","Mx_avg (" + _mpL + ")",
			"My_avg (" + _mpL + ")","Mxy_avg (" + _mpL + ")","Sx_avg (" + _sL + ")",
			"Sy_avg (" + _sL + ")","Sxy_avg (" + _sL + ")","tractX (" + _sL + ")",
			"tractY (" + _sL + ")","V_oop (" + _sL + ")","S_n (" + _sL + ")",
			//"shX (" + _sL + ")","shY (" + _sL + ")",
			"V_ip (" + _sL + ")",
			"F_n (" + _fL + ")","V_ip (" + _fL + ")","V_oop (" + _fL + ")",
			"M_b (" + _mL + ")","M_ip (" + _mL + ")"
		};
		row = _WriteSubHeader(ws, row, s5Hdr, cSub);

		int forcesStart = row;
		altIdx = 0;

		foreach (Segment seg in segs)
		{
			int elem = seg.element.id;
			float len = Vector3.Distance(seg.p2.xyz, seg.p1.xyz);

			step5DataR[elem] = row;

			// Static values written as constants
			OutputFile.Write(ws, row, S5_E, elem);
			OutputFile.Write(ws, row, S5_L, len);
			OutputFile.Fmla(ws, row, S5_T, String.Format("={0}", ExcelFile.Addr(step4DataR[elem], S4_T)));
			OutputFile.Write(ws, row, S5_TDX, tractionDir.X);
			OutputFile.Write(ws, row, S5_TDY, tractionDir.Y);
			OutputFile.Write(ws, row, S5_UX, u.X);
			OutputFile.Write(ws, row, S5_UY, u.Y);

			// Avg stress refs from Step 4
			for (int sc = 0; sc < 8; sc++)
				OutputFile.Fmla(ws, row, S5_SQX + sc,
					String.Format("={0}", ExcelFile.Addr(step4DataR[elem], S4_SQX + sc)));

			// Build cell address strings for this row (used repeatedly below)
			string R = row.ToString();
			string tDx = ExcelFile.Col(S5_TDX) + R;
			string tDy = ExcelFile.Col(S5_TDY) + R;
			string ux = ExcelFile.Col(S5_UX) + R;
			string uy = ExcelFile.Col(S5_UY) + R;
			string sqx = ExcelFile.Col(S5_SQX) + R;
			string sqy = ExcelFile.Col(S5_SQY) + R;
			string mx = ExcelFile.Col(S5_MX) + R;
			string my = ExcelFile.Col(S5_MY) + R;
			string mxy = ExcelFile.Col(S5_MXY) + R;
			string sx = ExcelFile.Col(S5_SX) + R;
			string sy = ExcelFile.Col(S5_SY) + R;
			string sxy = ExcelFile.Col(S5_SXY) + R;
			string L = ExcelFile.Col(S5_L) + R;
			string T = ExcelFile.Col(S5_T) + R;
			string txC = ExcelFile.Col(S5_TX) + R;  // tractX
			string tyC = ExcelFile.Col(S5_TY) + R;  // tractY
			string vpC = ExcelFile.Col(S5_VP) + R;  // Voop_psi
			string nsC = ExcelFile.Col(S5_NS) + R;  // normStress_psi
													//string shxC = ExcelFile.Col(S5_SHX) + R;  // shearX
													//string shyC = ExcelFile.Col(S5_SHY) + R;  // shearY
			string vsC = ExcelFile.Col(S5_VS) + R;  // Vip_psi

			// Intermediate vectors
			OutputFile.Fmla(ws, row, S5_TX, String.Format("={0}*{1}+{2}*{3}", tDx, sx, tDy, sxy));
			OutputFile.Fmla(ws, row, S5_TY, String.Format("={0}*{1}+{2}*{3}", tDx, sxy, tDy, sy));
			OutputFile.Fmla(ws, row, S5_VP, String.Format("={0}*{1}+{2}*{3}", tDx, sqx, tDy, sqy));
			OutputFile.Fmla(ws, row, S5_NS, String.Format("={0}*{1}+{2}*{3}", txC, tDx, tyC, tDy));
			//OutputFile.Fmla(ws, row, S5_SHX, String.Format("={0}-{1}*{2}", txC, nsC, tDx));
			//OutputFile.Fmla(ws, row, S5_SHY, String.Format("={0}-{1}*{2}", tyC, nsC, tDy));
			OutputFile.Fmla(ws, row, S5_VS, String.Format("={0}*{1}+{2}*{3}", txC, ux, tyC, uy));

			// Section force components
			OutputFile.Fmla(ws, row, S5_FN, String.Format("={0}*{1}*{2}", nsC, T, L));
			OutputFile.Fmla(ws, row, S5_VIP, String.Format("={0}*{1}*{2}", vsC, T, L));
			OutputFile.Fmla(ws, row, S5_VOP, String.Format("={0}*{1}*{2}", vpC, T, L));
			OutputFile.Fmla(ws, row, S5_MB, String.Format("=({0}*{1}+{2}*{3})*{4}", tDx, mx, tDy, my, L));
			OutputFile.Fmla(ws, row, S5_MIP, String.Format("={0}*{1}", mxy, L));

			if (altIdx % 2 == 1)
				OutputFile.Fill(ws, row, 1, row, MAX_COLS, cAlt);
			altIdx++;
			row++;
		}

		int forcesEnd = row - 1;
		row++;  // blank before totals


		// =====================================================================
		// SECTION TOTALS â€” SUM of force columns only
		// =====================================================================
		OutputFile.Fill(ws, row, 1, row, MAX_COLS, cTot);
		OutputFile.FontColor(ws, row, 1, row, MAX_COLS, white);
		OutputFile.Bold(ws, row, 1, row, MAX_COLS);
		OutputFile.Write(ws, row, 1, "SECTION TOTALS");
		row++;
		int totRow = row;
		// Totals sub-header (only label the force columns)
		string[] totLabels = {
			"Total Length (" + units.LengthUnit + ")","Avg. Thick. (in.)",
			"Î£F_n (" + _fL + ")","Î£V_ip (" + _fL + ")",
			"Î£V_oop (" + _fL + ")","Î£M_b (" + _mL + ")","Î£M_ip (" + _mL + ")"
		};
		int[] totCols = { S5_L, S5_T, S5_FN, S5_VIP, S5_VOP, S5_MB, S5_MIP };

		// Averages sub-header (only label the force columns)
		string[] avgStrLabels = {
			"Avg. S_n (" + _sL + ")", "Avg. S_ip (" + _sL + ")", "Avg. S_oop (" + _sL + ")" };
		string[] avgMomLabels = {
			"Avg. M_b (" + _mpL + ")","Avg. M_ip (" + _mpL + ")" };
		int[] avgStrCols = { S5_FN, S5_VIP, S5_VOP };
		int[] avgMomCols = { S5_MB, S5_MIP };

		for (int i = 0; i < totLabels.Length; i++)
			OutputFile.Write(ws, row, totCols[i], totLabels[i]);

		OutputFile.Fill(ws, row, 1, row, MAX_COLS, cSub);
		OutputFile.Bold(ws, row, 1, row, MAX_COLS);
		row++;

		// SUM formulas
		for (int i = 0; i < totCols.Length; i++)
		{
			string c = ExcelFile.Col(totCols[i]);
			OutputFile.Fmla(ws, row, totCols[i], String.Format("=SUM({0}{1}:{0}{2})", c, forcesStart, forcesEnd));
		}

		string cL = ExcelFile.Col(S5_L);
		string cT = ExcelFile.Col(S5_T);
		OutputFile.Fmla(ws, row, S5_T, String.Format(
			"=SUMPRODUCT({0}{1}:{0}{3},{2}{1}:{2}{3})/{0}{4}", cL, forcesStart, cT, forcesEnd, row));

		OutputFile.Bold(ws, row, 1, row, MAX_COLS);

		row++;

		for (int i = 0; i < avgStrLabels.Length; i++)
			OutputFile.Write(ws, row, avgStrCols[i], avgStrLabels[i]);
		for (int i = 0; i < avgMomLabels.Length; i++)
			OutputFile.Write(ws, row, avgMomCols[i], avgMomLabels[i]);

		OutputFile.Bold(ws, row, 1, row, MAX_COLS);

		row++;
		// AVG formulas
		for (int i = 0; i < avgStrCols.Length; i++)
		{
			string c = ExcelFile.Col(avgStrCols[i]);
			OutputFile.Fmla(ws, row, avgStrCols[i], String.Format("={0}{1}/({2}{1}*{3}{1})", c, row - 2, cL, cT));
		}
		for (int i = 0; i < avgMomCols.Length; i++)
		{
			string c = ExcelFile.Col(avgMomCols[i]);
			OutputFile.Fmla(ws, row, avgMomCols[i], String.Format("={0}{1}/{2}{1}", c, row - 2, cL));
		}

		OutputFile.Bold(ws, row, 1, row, MAX_COLS);

		// â”€â”€ Number formats (applied once to full column bands) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
		int nRows = sectElsList.Length;
		// t[0] column in Step 2, t[1] column in Step 3
		//OutputFile.NumberFmt(ws, 1, S2_T0, row, S2_T0, "0.0000");
		//OutputFile.NumberFmt(ws, 1 + 4 * nRows, S3_T1, row, S3_T1, "0.0000");

		// Stress/moment columns shared across Steps 1â€“4  (cols 4â€“12)
		OutputFile.NumberFmt(ws, 1, 4, row, 12, "0.00");

		// tDx/tDy/ux/uy constants and traction intermediates (cols 13â€“22)
		OutputFile.NumberFmt(ws, 1, 13, row, 22, "0.0000");

		// Final force results (cols 23â€“27)
		OutputFile.NumberFmt(ws, 1, S5_FN, row, S5_MIP, "0.00");

		// Length and thickness columns (col 2 in Steps 3â€“5)
		// They appear in col 2 of each averaging/force step; a broad band
		// covering col 2 would overlap label columns, so target it narrowly:
		OutputFile.NumberFmt(ws, 6 + 4 * nRows, 2, row, 3, "0.000");

		//OutputFile.AutoFit(ws);
		Console.WriteLine("Expanded sheet written: MID={0}, LC={1}.", mid, lc);
	}

	// =========================================================================
	// LAYOUT HELPERS
	// =========================================================================

	/// <summary>
	/// Writes a full-width colored banner row and returns the next row index.
	/// </summary>
	private int _WriteSectionBanner(Excel.Worksheet ws, int row, int nCols,
									int color, string text)
	{
		OutputFile.Fill(ws, row, 1, row, nCols, color);
		OutputFile.FontColor(ws, row, 1, row, nCols, ExcelFile.RGB(255, 255, 255));
		OutputFile.Bold(ws, row, 1, row, nCols);
		OutputFile.MergeRow(ws, row, 1, nCols);
		OutputFile.Write(ws, row, 1, text);
		((Excel.Range)ws.Cells[row, 1]).WrapText = true;
		return row + 1;
	}

	/// <summary>
	/// Writes a row of column header strings starting at col 1
	/// and returns the next row index.
	/// </summary>
	private int _WriteSubHeader(Excel.Worksheet ws, int row,
								string[] headers, int bgColor)
	{
		for (int c = 0; c < headers.Length; c++)
			OutputFile.Write(ws, row, c + 1, headers[c]);
		OutputFile.Fill(ws, row, 1, row, headers.Length, bgColor);
		OutputFile.Bold(ws, row, 1, row, headers.Length);
		return row + 1;
	}
	// â”€â”€ Widths of Summary sheet / Original Data sheets  (â‰¤18 cols) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
	private void _SetSummaryWidths(Excel.Worksheet ws)
	{
		OutputFile.ColWidth(ws, 1, 5, 11.0);   // Model ID, Length, Avg Thickness
		OutputFile.ColWidth(ws, 2, 3, 8.0);    // LC, Element list
		OutputFile.ColWidth(ws, 6, 18, 13.0);  // stresses, forces

		//OutputFile.ColWidth(ws, 5, 11.0);   // Avg Thickness
		//for (int c = 6; c <= 13; c++) OutputFile.ColWidth(ws, c, 13.0);  // stresses
		//for (int c = 14; c <= 18; c++) OutputFile.ColWidth(ws, c, 13.0);  // forces
	}

	// â”€â”€ Widths of Expanded V&V sheet  (27 cols) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
	private void _SetExpandedWidths(Excel.Worksheet ws)
	{
		OutputFile.ColWidth(ws, 1, 2, 12.0);    // Element,Node / Edge / sete.t[1]
		OutputFile.ColWidth(ws, 3, 3, 11.0);    // Role / t[0] / blank
		OutputFile.ColWidth(ws, 4, 27, 14.0);  // stress band, traction intermediates, final forces
		OutputFile.ColWidth(ws, 13, 15, 8.0);   // tDx, tDy, ux, uy

		//OutputFile.ColWidth(ws, 2, 9.0);    // Node / Edge / sete.t[1]
		//for (int c = 4; c <= 12; c++) OutputFile.ColWidth(ws, c, 12.0);  // stress band
		//for (int c = 13; c <= 15; c++) OutputFile.ColWidth(ws, c, 8.0);   // tDx, tDy, ux, uy
		//for (int c = 16; c <= 22; c++) OutputFile.ColWidth(ws, c, 12.0);  // traction intermediates
		//for (int c = 23; c <= 27; c++) OutputFile.ColWidth(ws, c, 13.0);  // final forces
	}
	// ---------------------------------------------------------------------------------------
	// ---------------------------------------------------------------------------------------
	// Method used to assemble an [ElEdge] given the element, and a set of two nodes.
	// MOVE TO TYPES.CS?
	public ElEdge GetEdge(Element e, Node n0, Node n1)
	{
		ElEdge ee = new ElEdge();

		ee.elemID = e.id;
		ee.e = e;
		ee.n = new Node[] { n0, n1 };
		ee.b = n1.xyz - n0.xyz;
		ee.t = GetIntersectionOfLines(ee.n[0].xyz, ee.b, a, b);

		return ee;
	}
	// -------------------------------------------------

	// Method used to assemble a dictionary of <Element, ElEdge[]>
	// MOVE TO TYPES.CS?
	public Dictionary<int, List<ElEdge>> GetDictofEdges(Element[] Elements)
	{
		IEnumerable<ElEdge> infLineEdges = Elements
			.SelectMany(e => Enumerable.Range(0, e.nNodes)
				.Select(i => GetEdge(e, e.n[i], e.n[(i + 1) % e.nNodes])))
			.Where(ee => 0 <= ee.t[0] && ee.t[0] <= 1)
			.OrderBy(ee => Math.Abs(ee.t[1]))
			.GroupBy(ee => ee.elemID)
			.SelectMany(g => g
					.GroupBy(ee => ee.n[0].xyz + ee.b * (float)Math.Round(ee.t[0], 2))
					.Select(x => x.First()));

		Dictionary<int, List<ElEdge>> eeDict = infLineEdges
		.GroupBy(ee => ee.elemID)
		.Where(g => g
			.Any(ee => t1 <= ee.t[1] && ee.t[1] <= t2))
		.ToDictionary(g => g.Key, g => g.ToList());

		return eeDict;
	}
	// -------------------------------------------------

	// Function to calculate the point of intersection between two lines.
	// Used to calculate the scalar factor 't' for the line between two nodes at which a
	// section cut line intersects an element's edge.
	public static float[] GetIntersectionOfLines(
		// Position and direction vectors for equation of the line. 'a1' and 'b1' are
		// the vectors for the parametric vector form of the line between two given nodes.
		// 'a1' represents the position vector of the first node on an element's edge,
		// while 'b1' represents the direction vector of the element edge, calculated as
		// the position vector of the second node minus that of the first node.
		Vector3 a1,
		Vector3 b1,

		// Position and direction vectors for equation of the specified section cut line.
		Vector3 a2,
		Vector3 b2
		)
	{
		Vector3 r = a2 - a1;
		float det = (Vector3.Dot(b1, b1) * Vector3.Dot(b2, b2)) -
			(Vector3.Dot(b1, b2) * Vector3.Dot(b1, b2));

		// t1 scalar along line segment between nodes.
		float t1 = (Vector3.Dot(b1, r) * Vector3.Dot(b2, b2) -
			Vector3.Dot(b2, r) * Vector3.Dot(b1, b2)) / det;

		// t2 scalar along section cut line.
		float t2 = (Vector3.Dot(b1, r) * Vector3.Dot(b1, b2) -
			  Vector3.Dot(b2, r) * Vector3.Dot(b1, b1)) / det;

		return new float[] { t1, t2 };
	}

	// For each [ElEdge] per element in the section cut, calculate the point of intersection
	// along the element edge with the section cut line. Only include [ElEdge] where
	// the point of intersection scalar is between 0 and 1 (i.e. between the two nodes
	// that make up the [ElEdge]).
	//public static List<ElEdge> CalcTAndBetweenNodes(ElEdge[] elEdges, Vector3 a, Vector3 b, float t1, float t2)
	//{
	//	var elEdgeGroups = elEdges.AsParallel().Where(ee =>
	//	{
	//		ee.t = GetIntersectionOfLines(ee.n[0].xyz, ee.b, a, b);
	//
	//		return (ee => 0 <= ee.t[0] && ee.t[0] <= 1);// && t1 <= ee.t[1] && ee.t[1] <= t2);
	//													// Return all edges with t between 0 and 1, and all .
	//	}).GroupBy(ee => ee.elemID).Where(g => g.Any(ee => t1 <= ee.t[1] && ee.t[1] <= t2)).SelectMany(g => g)//.Where(ee => 0 <= ee.t[0] && ee.t[0] <= 1 &&
	//																											   //			  t1 <= ee.t[1] && ee.t[1] <= t2)
	//
	//	// Round t to 2 decipal places, and group results by the calculated "point". Where near 0 or 1,
	//	// this will calculate to the node coordinates. Group results by the resulting point.
	//	// This should result in only 2 "groups".
	//	.GroupBy(ee => ee.n[0].xyz + ee.b * (float)Math.Round(ee.t[0], 2));
	//
	//	return elEdgeGroups.Count() == 2 ? elEdgeGroups.Select(
	//	// Select the first result from each "point" group.
	//	point => point.First()).ToList() : null;
	//}

	//public static Segment[] GetSegments(
	//	ElEdge[] elEdges,
	//	Hashtable[] strsDicts,
	//	int[] LCs,
	//	//object ModelID,
	//	string ModelUnits
	//)
	//{
	//	return LCs.Select<int, Segment>(l =>
	//	{
	//		List<StressRecord> s = new List<StressRecord>(
	//			(StressRecord)(strsDicts[0][l]),
	//			(StressRecord)(strsDicts[1][l]),
	//			(StressRecord)(strsDicts[2][l]),
	//			(StressRecord)(strsDicts[3][l])
	//		);
	//		Segment seg = new Segment();
	//		//seg.InterpolateStresses(elEdges, s, ModelID, ModelUnits);
	//		seg.InterpolateStresses(elEdges, s, ModelUnits);
	//		return seg;
	//	}).ToArray();
	//}



}
public class ExcelFile
{
	public string BaseDirectory;
	public string FilePath;
	public Excel.Application xl = null;
	public Excel.Workbooks wbs = null;
	public Excel.Workbook wb = null;
	public Excel.Sheets sheets = null;
	//public Excel.Sheets wss = null;
	//public List<Excel.Worksheet> sheets = new List<Excel.Worksheet>();
	public Excel.Range cr = null;
	public bool WriteNew;

	public ExcelFile(string FileName, string OutputDirectory, bool writeNew = true)
	{
		BaseDirectory = OutputDirectory;
		if (!Directory.Exists(BaseDirectory)) Directory.CreateDirectory(BaseDirectory);
		FilePath = Path.Combine(BaseDirectory, FileName);
		WriteNew = writeNew;
	}

	public void OpenExcel()
	{
		try
		{
			// Start the background Excel application process
			xl = new Excel.Application();
			xl.Visible = false;
			//xl.DisplayAlerts = false; // Turn off prompts (like overwrite warnings)

			// Add a new workbook or open existing workbook
			wbs = xl.Workbooks;

			if (!WriteNew && !File.Exists(FilePath))
			{
				WriteNew = true;
				wb = wbs.Add(Type.Missing);
			}
			else if (WriteNew) { wb = wbs.Add(Type.Missing); }
			else { wb = wbs.Open(FilePath); }

			// Target the first worksheet
			sheets = wb.Worksheets;
			//sheets.Add((Excel.Worksheet)wss[1]);
		}
		catch (Exception ex)
		{
			Console.WriteLine("Error occurred: {0}", ex.Message);
		}
	}
	public void CloseExcel()
	{
		try
		{
			// File format for SaveAs
			Excel.XlFileFormat fmt;
			string ext = Path.GetExtension(FilePath).ToLowerInvariant();
			if (ext == ".xlsm") fmt = Excel.XlFileFormat.xlOpenXMLWorkbookMacroEnabled;
			else fmt = Excel.XlFileFormat.xlOpenXMLWorkbook;

			// Save and close down the file
			if (WriteNew)
			{
				wb.SaveAs(FilePath, fmt, Type.Missing, Type.Missing, Type.Missing,
					Type.Missing, Excel.XlSaveAsAccessMode.xlNoChange, Type.Missing,
					Type.Missing, Type.Missing, Type.Missing, Type.Missing);
			}
			else if (!WriteNew) { wb.Save(); }
			wb.Close(false);
			xl.Quit();
		}
		catch (Exception ex)
		{
			Console.WriteLine("Error occurred: {0}", ex.Message);
		}
		finally { _ReleaseAll(); }
	}

	// -- Sheet management ------------------------------------------------------
	public Excel.Worksheet GetSheet(int oneBasedIndex)
	{ return (Excel.Worksheet)sheets[oneBasedIndex]; }

	public Excel.Worksheet GetSheetByName(string name)
	{ return (Excel.Worksheet)sheets[name]; }

	public void RenameSheet(int oneBasedIndex, string name)
	{ GetSheet(oneBasedIndex).Name = _Clip(name); }

	public Excel.Worksheet AddSheet(string name)
	{
		var ws = (Excel.Worksheet)sheets.Add(Type.Missing, sheets[sheets.Count]);
		ws.Name = _Clip(name);
		return ws;
	}

	public static string _Clip(string s, int max = 31)
	{
		s = (s != null && s.Length > max) ? s.Substring(0, max) : (s != null ? s : "");
		return s;
	}

	// -- Cell writing ----------------------------------------------------------
	public void Write(Excel.Worksheet ws, int row, int col, object val)
	{ ((Excel.Range)ws.Cells[row, col]).Value2 = val; }

	public void Fmla(Excel.Worksheet ws, int row, int col, string formula)
	{ ((Excel.Range)ws.Cells[row, col]).Formula = formula; }

	/// <summary>Writes an object[,] block in one COM call (fastest bulk write).</summary>
	public void WriteBlock(Excel.Worksheet ws, int row, int col, object[,] data)
	{
		int r = data.GetLength(0), c = data.GetLength(1);
		ws.Range[(Excel.Range)ws.Cells[row, col],
				 (Excel.Range)ws.Cells[row + r - 1, col + c - 1]].Value2 = data;
	}

	// -- Formatting ------------------------------------------------------------
	public void Bold(Excel.Worksheet ws, int r1, int c1, int r2, int c2)
	{
		ws.Range[(Excel.Range)ws.Cells[r1, c1], (Excel.Range)ws.Cells[r2, c2]].Font.Bold = true;
	}

	public void FontColor(Excel.Worksheet ws, int r1, int c1, int r2, int c2, int oleColor)
	{
		ws.Range[(Excel.Range)ws.Cells[r1, c1], (Excel.Range)ws.Cells[r2, c2]].Font.Color = oleColor;
	}

	public void Fill(Excel.Worksheet ws, int r1, int c1, int r2, int c2, int oleColor)
	{
		ws.Range[(Excel.Range)ws.Cells[r1, c1], (Excel.Range)ws.Cells[r2, c2]].Interior.Color = oleColor;
	}

	public void Filter(Excel.Worksheet ws, int headerRow, int c1, int c2)
	{
		ws.Range[(Excel.Range)ws.Cells[headerRow, c1], (Excel.Range)ws.Cells[headerRow, c2]].AutoFilter(1);
	}

	public void Freeze(Excel.Worksheet ws, int belowRow)
	{
		((Excel._Worksheet)ws).Activate();
		((Excel.Range)ws.Cells[belowRow + 1, 1]).Select();
		xl.ActiveWindow.FreezePanes = true;
	}

	// â”€â”€ Merge a row band into one cell â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
	public void MergeRow(Excel.Worksheet ws, int row, int c1, int c2)
	{
		ws.Range[(Excel.Range)ws.Cells[row, c1],
				 (Excel.Range)ws.Cells[row, c2]].Merge();
	}

	// â”€â”€ Fixed column width (in character units, same scale as Excel UI) â”€â”€
	public void ColWidth(Excel.Worksheet ws, int col1, int col2, double width)
	{
		((Excel.Range)ws.Range[ws.Columns[col1], ws.Columns[col2]]).ColumnWidth = width;
	}

	// â”€â”€ Number format on a rectangular range â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
	public void NumberFmt(Excel.Worksheet ws, int r1, int c1, int r2, int c2,
						  string fmt)
	{
		ws.Range[(Excel.Range)ws.Cells[r1, c1],
				 (Excel.Range)ws.Cells[r2, c2]].NumberFormat = fmt;
	}

	//public void AutoFit(Excel.Worksheet ws) { ((Excel.Range)ws.UsedRange).Columns.AutoFit(); }
	//---------------------------------------------------------------------------
	// -- Address helpers -------------------------------------------------------
	/// <summary>1-based column number -> Excel letter(s). Col(1)="A", Col(27)="AA".</summary>
	public static string Col(int c)
	{
		string s = "";
		while (c > 0) { c--; s = (char)('A' + c % 26) + s; c /= 26; }
		return s;
	}

	/// <summary>Cell address, e.g. Addr(3,2) = "B3". Use abs flags for $B$3.</summary>
	public static string Addr(int row, int col, bool absRow = false, bool absCol = false)
	{ return String.Format("{0}{1}{2}{3}", absCol ? "$" : "", Col(col), absRow ? "$" : "", row); }

	/// <summary>Cross-sheet reference formula string, e.g. "='Original Data'!F12".</summary>
	public static string SRef(string sheetName, int row, int col)
	{ return String.Format("='{0}'!{1}", sheetName, Addr(row, col)); }

	/// <summary>OLE color from R, G, B components.</summary>
	public static int RGB(int r, int g, int b)
	{ return r | (g << 8) | (b << 16); }

	// -- COM cleanup -----------------------------------------------------------
	private void _ReleaseAll()
	{
		foreach (var o in new object[] { cr, sheets, wb, wbs, xl })
		{
			// Release every object in reverse order of creation
			_Rel(o);
		}

		// Force garbage collection to sweep up remaining runtime callable wrappers
		GC.Collect();
		GC.WaitForPendingFinalizers();
	}
	private static void _Rel(object o)
	{
		try { if (o != null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o); }
		catch { }
		finally { o = null; }
	}
	//---------------------------------------------------------------------------
}

// =======================================================================================
// Function to open a new Excel COM object to create section cut summary file.
// ---------------------------------------------------------------------------------------

public class SectionCutException : Exception
{
	public SectionCutException() { }
	public SectionCutException(string message) : base(message) { }
	public SectionCutException(string message, Exception inner) : base(message, inner) { }
}