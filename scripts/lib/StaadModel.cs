using System;
using System.IO;
using System.Collections.Generic;
using System.Text;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Web.Script.Serialization;
using System.Collections;

public class StaadModel
{
	//Parsed data
	public List<AnlSection> Sections;
	public LengthUnit Length = LengthUnit.Inch; // Other units not supported (yet)
	public ForceUnit Force = ForceUnit.Pound;

	public Dictionary<int, Node> NodeDict;
	public Dictionary<int, Element> ElementDict;
	public Dictionary<int, Member> MemberDict;
	public GeometryResult geom;

	public Dictionary<int, Dictionary<int, float[]>> JointStresses;
	public Dictionary<int, Dictionary<int, float[]>> CenterStresses;
	public Dictionary<int, Dictionary<int, Disp>> DispDict;
	// public Dictionary<int, Dictionary<int, float[]>> MemberForces;
	public Dictionary<int, Dictionary<int, float[]>> ElemStressDict;
	public Dictionary<int, Dictionary<int, Dictionary<int, StressRecord>>> ElemStressRecordDict;

	public List<Node> NodeList;
	public List<Element> ElementList;
	public List<Member> MemberList;
	public List<StressRecord> StressList;
	public List<Disp> DispList;
	public List<MemForce> ForceList;

	public Dictionary<string, List<int>> Groups;
	public Dictionary<int, string> LoadCaseNames;

	private string anlPath;
	private string indexPath;
	private string stressPath;
	private string forcePath;
	private string dispPath;
	private CoordFrame targetState;
	private ModelConfig config;

	public StaadModel(ModelConfig config)
	{
		this.config = config;
		this.anlPath = config.anlPath;
		this.indexPath = anlPath + ".index.json";
		this.forcePath = anlPath + ".force.json";
		this.stressPath = anlPath + ".stress.bin";
		this.dispPath = anlPath + ".disp.bin";
		this.targetState = config.targetState;

		LoadOrBuildIndex();         //ANL or json
		ParseGeometry();            //ANL file always

		LoadOrBuildStresses();
		AbsEqCases();				// Bandaid; remove for subsequent runs
		if (config.loadDisplacements) LoadOrBuildDisplacements();
		if (config.loadForces) ParseForces();
	}

	private void AbsEqCases()
	{
		int interval = 100000;
		int count = StressList.Count/interval+2;
		bool eqCasesPresent = false;
		var pb = new ProgressBar(count, title : "Taking the absolute value of load cases 30, 31, and 34");
		HashSet<int> eqCases = new HashSet<int> {30, 31, 34};
		for (int i = 0; i < StressList.Count; i++)
		{
			if (i % interval == 0) { pb.Tick(); }
			
			StressRecord rec = StressList[i];
			
			if (eqCases.Contains(rec.LC))
			{
				eqCasesPresent = true;
				for (int j = 0; j < rec.Sf.Length; j++)
				{
					rec.Sf[j] = Math.Abs(rec.Sf[j]);
				}
			}
		}
		pb.Finish();
		Console.WriteLine(string.Format("Found earthquake load cases in data: {0}",eqCasesPresent));
	}

	private void LoadOrBuildIndex()
	{
		var ser = new JavaScriptSerializer();

		if (File.Exists(indexPath)
			&& File.GetLastWriteTime(indexPath) > File.GetLastWriteTime(anlPath))

		{
			Sections = ser.Deserialize<List<AnlSection>>(File.ReadAllText(indexPath));
		}
		else
		{
			Sections = AnlIndexer.IndexFile(anlPath);
			AnlUnitStamper.StampUnits(anlPath, Sections); // mutate sections in place
			File.WriteAllText(indexPath, ser.Serialize(Sections));
		}
	}

	private void ParseGeometry()
	{
		bool flag = false;
		// Nodes
		Debug.Printf("Parsing Nodes", flag);
		var anp = new AnlNodeParser(anlPath);
		var nodeSection = Sections.Find(s => s.Name.ToUpper().Contains("JOINT COORDINATES"));
		anp.ParseSection(nodeSection);
		NodeList = anp.Nodes;
		NodeDict = NodeList.ToDictionary(n => n.id);

		// Groups
		Debug.Printf("Parsing Groups", flag);
		var grp = new AnlGroupParser(anlPath);
		var grpSections = Sections.FindAll(s => s.Name.ToUpper().Contains("GROUP"));
		foreach (var section in grpSections)
			grp.ParseGroups(section);
		Groups = grp.Groups;

		// Element Properties
		Debug.Printf("Element Props", flag);
		var elemPropSections = Sections.FindAll(s => s.Name.ToUpper().Contains("ELEMENT PROPERTY"));
		foreach (var section in elemPropSections)
			grp.ParseThickness(section);

		// Member Properties
		Debug.Printf("Parsing Member Props", flag);
		var memberPropSections = Sections.FindAll(s => s.Name.ToUpper().Contains("MEMBER PROPERTY"));
		foreach (var section in memberPropSections)
			grp.ParseMemberProperties(section);

		// Element Incidences
		Debug.Printf("Parsing Element Indices", flag);
		var geo = new AnlGeomParser(anlPath);
		var elemSection = Sections.Find(s => s.Name.ToUpper().Contains("ELEMENT INCIDENCES"));
		geo.ParseElementSection(elemSection, NodeDict, grp.Thickness);
		ElementList = geo.Elements;
		ElementDict = ElementList.ToDictionary(n => n.id);

		// Member Incidences
		Debug.Printf("Parsing Member Indices", flag);
		var memberSection = Sections.Find(s => s.Name.ToUpper().Contains("MEMBER INCIDENCES"));
		if (memberSection != null)
		{
			geo.ParseMemberSection(memberSection, NodeDict, grp.MemberSections);
			MemberList = geo.Members;
			MemberDict = MemberList.ToDictionary(n => n.id);
		}

		geom = new GeometryResult();
		geom.NodeDict = NodeDict;
		geom.ElementDict = ElementDict;
		geom.MemberDict = MemberDict;

		// Load Case Names
		Debug.Printf("Parsing Load Cases", flag);
		var loadSections = Sections.FindAll(s => s.Name.ToUpper() == "LOAD");
		foreach (var section in loadSections)
			grp.ParseLoadCaseNames(section);
		LoadCaseNames = grp.LoadCaseNames;
	}

	private void LoadOrBuildStresses()
	{
		var asp = new AnlStressParser(anlPath);
		var stressSections = Sections.FindAll(s => s.Name.ToUpper().Contains("STRESSES"));

		var stressStart = config.stressStart ?? 0;
		var stressEnd = config.stressEnd ?? stressSections.Count;
		Console.WriteLine(string.Format("stressStart = {0}, stressEnd = {1}", stressStart, stressEnd));
		bool multipleStressSections = stressSections.Count > 1;

		// var pb = new ProgressBar(stressEnd - stressStart, title: "Parsing ANL stress sections");

		for (int i = stressStart; i < stressEnd; i++)
		{
			var section = stressSections[i];
			// pb.Tick();
			if (section.Name.ToUpper().Contains("JOINT"))
			{
				if (config.loadJointStresses)
					asp.ParseSection(section, ElementDict, targetState, progress: !multipleStressSections);
			}
			else
			{
				if (config.loadCenterStresses)
					asp.ParseSection(section, ElementDict, targetState, progress: !multipleStressSections);
			}
		}

		StressList = asp.Stresses;
		// pb.Finish();
	}

	private void LoadOrBuildDisplacements()
	{
		var adp = new AnlDispParser(anlPath);
		var dispSections = Sections.FindAll(s => s.Name.ToUpper().Contains("DISPLACEMENTS"));
		foreach (var section in dispSections)
			adp.ParseSection(section);
		DispList = adp.Displacements;
		BuildDispDict();
	}

	private void BuildDispDict()
	{
		DispDict = DispList
					.GroupBy(d => d.node)
					.ToDictionary(
						g => g.Key,
						g => g.ToDictionary(d => d.LC));
	}

	public void BuildElemStressRecDictionary()
	{
		int count = StressList.Count;
		// [LC][eid][nid] -> stress record
		ElemStressRecordDict = new Dictionary<int, Dictionary<int, Dictionary<int, StressRecord>>>();
		for (int i = 0; i < count; i++)
		{
			StressRecord rec = StressList[i];
			int LC = rec.LC;
			Dictionary<int, Dictionary<int, StressRecord>> eDict;
			if (!ElemStressRecordDict.TryGetValue(LC, out eDict))
			{
				eDict = new Dictionary<int, Dictionary<int, StressRecord>>();
				ElemStressRecordDict[LC] = eDict;
			}

			int eid = rec.elemID;
			Dictionary<int, StressRecord> nDict;
			if (!eDict.TryGetValue(eid, out nDict))
			{
				nDict = new Dictionary<int, StressRecord>();
				eDict[eid] = nDict;
			}
			
			int nid = rec.node;
			nDict[nid] = rec; // pass by reference chains back to the ElemStressRecordDict
		}
	}

	public void BuildElemStressDictionary()
	{
		Console.WriteLine("Building element based stress dictionary");
		ElemStressDict = new Dictionary<int, Dictionary<int, float[]>>();

		for (int i = 0; i < StressList.Count; i++)
		{
			var rec = StressList[i];
			Dictionary<int, float[]> lcDict;
			if (!ElemStressDict.TryGetValue(rec.LC, out lcDict))
			{
				lcDict = new Dictionary<int, float[]>();
				ElemStressDict[rec.LC] = lcDict;
			}

			float[] slots;
			if (!lcDict.TryGetValue(rec.elemID, out slots))
			{
				// Usage of the ElemStressDict requires looking up joint ids from the ElementDict if needed
				Element elem = ElementDict[rec.elemID];
				int len = (elem.nNodes + 1) * 8;
				slots = new float[len];
				for (int k = 0; k < len; k++)
				{
					slots[k] = float.NaN;
				}
				lcDict[rec.elemID] = slots;
			}

			int slotIdx;
			if (rec.node < 0)
			{
				slotIdx = 0;
			}
			else
			{
				Element elem = ElementDict[rec.elemID];
				slotIdx = -1;
				for (int n = 0; n < elem.nNodes; n++)
				{
					if (elem.n[n].id == rec.node)
					{
						slotIdx = n + 1;
						break;
					}
				}
				if (slotIdx < 0) { continue; }
			}
			Array.Copy(rec.Sf, 0, slots, slotIdx * 8, 8);
		}
	}

	//Wrapper so we can build stress dictionaries on demand
	public void BuildStressDictionaries()
	{
		StressDictBuilder.Build(
			StressList, ElementDict, DispDict,
			out JointStresses, out CenterStresses);
	}

	//Nested helper class for building center/joint stress dictionaries
	private static class StressDictBuilder
	{
		public static void Build(
			List<StressRecord> Stresses,
			Dictionary<int, Element> elems,
			Dictionary<int, Dictionary<int, Disp>> disps,
			out Dictionary<int, Dictionary<int, float[]>> jointStresses,
			out Dictionary<int, Dictionary<int, float[]>> centerStresses)
		{
			// Accessed with joints[lc][nid] -> float[14]
			var joints = new Dictionary<int, Dictionary<int, float[]>>(); // float[14]
			var sums = new Dictionary<int, Dictionary<int, float[]>>();  // float[14]
			var counts = new Dictionary<int, Dictionary<int, int>>();
			var jointCounts = new Dictionary<int, Dictionary<int, int>>();
			var centers = new Dictionary<int, Dictionary<int, float[]>>(); // float[8]
			bool hasJoint = false;

			var pb = new ProgressBar(Stresses.Count, title: "Building Center & Joint Stress Dictionaries");

			//Stresses is the flat List<StressRecord>
			for (int i = 0; i < Stresses.Count; i++)
			{
				pb.Tick();
				var rec = Stresses[i];
				if (rec.node > 0)
				{
					hasJoint = true;
					// lcDict is a REFERENCE to the given inner dict of
					// joints that we are assigning data to this loop
					var lcSums = EnsureLC<float[]>(joints, rec.LC);
					var lcCounts = EnsureLC<int>(jointCounts, rec.LC);

					float[] vals;

					if (!lcSums.TryGetValue(rec.node, out vals))
					{
						vals = new float[14];
						lcSums[rec.node] = vals;
						lcCounts[rec.node] = 0;
					}

					for (int j = 0; j < 8; j++) vals[j] += rec.Sf[j];
					lcCounts[rec.node]++;
				}
				else
				{
					//Console.WriteLine("0");
					// Always build center stresses
					var lcCenter = EnsureLC<float[]>(centers, rec.LC);
					float[] cvals = new float[8];
					Array.Copy(rec.Sf, 0, cvals, 0, 8);
					lcCenter[rec.elemID] = cvals;

					// Skip averaging center stresses if ANY joints have been found
					if (!hasJoint)
					{
						// Same as above, inner dict REFERENCES that point
						// to the associated location in the outer dict
						var lcSums = EnsureLC<float[]>(sums, rec.LC);
						var lcCounts = EnsureLC<int>(counts, rec.LC);
						Element elem = elems[rec.elemID];

						for (int j = 0; j < elem.nNodes; j++)
						{
							int nid = elem.n[j].id;
							float[] vals;
							// if no values assigned to this joint yet, init with zeros
							if (!lcSums.TryGetValue(nid, out vals))
							{
								vals = new float[14];
								lcSums[nid] = vals;
								lcCounts[nid] = 0;
							}
							// add the stresses and increment the running count
							for (int k = 0; k < 8; k++) vals[k] += rec.Sf[k];
							lcCounts[nid]++;
						}
					}
				}
			}
			//Average joint sums from joint stresses
			if (hasJoint)
			{
				foreach (var lcKvp in joints)
				{
					var lcCounts = jointCounts[lcKvp.Key];
					foreach (var nidKvp in lcKvp.Value)
					{

						float[] vals = nidKvp.Value;
						int c = lcCounts[nidKvp.Key];
						if (c > 0)
							for (int j = 0; j < 8; j++) vals[j] /= c;
						FillDisp(vals, nidKvp.Key, lcKvp.Key, disps);
					}
				}
				jointStresses = joints;
			}
			//Else average joint sums from center stresses
			else
			{
				//Double foreach to get down to the lc/nid values
				foreach (var lcKvp in sums)
				{
					var lcCounts = counts[lcKvp.Key];
					foreach (var nidKvp in lcKvp.Value)
					{
						//Divide by count to go from sum to average
						float[] vals = nidKvp.Value;
						int c = lcCounts[nidKvp.Key];
						if (c > 0)
							for (int j = 0; j < 8; j++) vals[j] /= c;

						//Check for displacements and fill if found, NaN otherwise
						FillDisp(vals, nidKvp.Key, lcKvp.Key, disps);
					}
				}
				jointStresses = sums;
			}
			centerStresses = centers;
			pb.Finish();
		}

		// Init the inner dictionary if necessary
		private static Dictionary<int, T> EnsureLC<T>(
			Dictionary<int, Dictionary<int, T>> dict, int key)
		{
			Dictionary<int, T> inner;
			if (!dict.TryGetValue(key, out inner))
			{
				inner = new Dictionary<int, T>();
				dict[key] = inner;
			}
			return inner;
		}

		// Look for displacements, fill with NaNs if not found for the given node/lc pair
		private static void FillDisp(float[] vals, int nodeId, int lc,
			Dictionary<int, Dictionary<int, Disp>> disps)
		{
			Dictionary<int, Disp> nodeDisps;
			Disp disp; //this should be illegal..
			if (disps != null && disps.TryGetValue(nodeId, out nodeDisps) &&
				nodeDisps.TryGetValue(lc, out disp))
			{
				Array.Copy(disp.DR, 0, vals, 8, 6);
			}
			else
			{
				for (int i = 8; i < 14; i++) vals[i] = float.NaN;
			}
		}

	}

	// Function to add externally defined load combinations manually.
	public void AddLoadCaseNames(string[] lcList)
	{
		int[] existingLCs = StressList.Select(s => s.LC).Distinct().ToArray();
		foreach (string lc in lcList)
		{
			string[] lcTokens = lc.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
			int nLC = int.Parse(lcTokens[0]);
			if (!existingLCs.Contains(nLC)) continue;
			LoadCaseNames.Add(nLC, lcTokens[1]);
		}
	}

	// Function to get SRSS load case for StressRecords
	public void CalculateSRSS(string[] srssLCList)
	{
		// Lookup stresses, forces and disps by elemID and/or node keys
		var strLookup = StressList.ToLookup(s => new ValueTuple<int, int>(s.elemID, s.node));
		var fceLookup = ForceList.ToLookup(f => new ValueTuple<int, int>(f.memID, f.nodeID));
		var dispLookup = DispList.ToLookup(d => d.node);

		// List of unique keys
		var strENs = strLookup.Select(s => s.Key).Distinct().ToArray();
		var fceENs = fceLookup.Select(f => f.Key).Distinct().ToArray();
		int[] dispNs = dispLookup.Select(d => d.Key).Distinct().ToArray();
		List<StressRecord> addedStresses = new List<StressRecord>();
		List<MemForce> addedForces = new List<MemForce>();
		List<Disp> addedDisps = new List<Disp>();

		var pb = new ProgressBar(srssLCList.Length * (strENs.Length + fceENs.Length + dispNs.Length),
								 title: "Creating New SRSS Load Case Results");

		// For each SRSS combination input
		foreach (string srssLC in srssLCList)
		{
			int[] lcData = Array.ConvertAll(srssLC.Split((char[])null,
							StringSplitOptions.RemoveEmptyEntries), int.Parse);

			// New LC number for SRSS combination
			int newLC = lcData[0];
			// Load cases to be used for SRSS combination
			int[] srcLCs = lcData.Skip(1).ToArray();

			// For each unique key of element ID and node in StressList
			foreach (var en in strENs)
			{
				pb.Tick();

				// Create dictionary of stresses for this key, keyed by load case number
				var str = strLookup[en].GroupBy(s => s.LC).ToDictionary(g => g.Key, g => g.First());

				// Skip if the dictionary does not contain any of the input load cases
				var ssrcLCs = srcLCs.Intersect(str.Keys).ToArray();
				if (ssrcLCs.Length == 0) { continue; }

				// Initiate new stress record using existing record for first load case input
				StressRecord strNew = new StressRecord(str[ssrcLCs[0]]);
				strNew.LC = newLC;

				// Stresses and moments squared
				Matrix4x4 st = Matrix4x4Extensions.Square(strNew.S);
				Matrix4x4 m = Matrix4x4Extensions.Square(strNew.M);

				// Add squared stresses and moments for each load case input
				foreach (int lc in ssrcLCs.Skip(1).ToArray())
				{
					st += Matrix4x4Extensions.Square(str[lc].S);
					m += Matrix4x4Extensions.Square(str[lc].M);
				}

				// Take square root for final stress record
				strNew.S = Matrix4x4Extensions.SquareRoot(st);
				strNew.M = Matrix4x4Extensions.SquareRoot(m);
				strNew.Sf = new float[]{ strNew.S.M11, strNew.S.M12, strNew.S.M13, strNew.S.M22, strNew.S.M23,
							strNew.M.M11, strNew.M.M12, strNew.M.M22};

				addedStresses.Add(strNew);
			}
			// For each unique key of element ID and node in ForceList
			foreach (var en in fceENs)
			{
				pb.Tick();

				// Create dictionary of forces for this key, keyed by load case number
				var fce = fceLookup[en].GroupBy(f => f.LC).ToDictionary(g => g.Key, g => g.First());

				// Skip if the dictionary does not contain any of the input load cases
				var fsrcLCs = srcLCs.Intersect(fce.Keys).ToArray();
				if (fsrcLCs.Length == 0) { continue; }

				// Initiate new force record using existing record for first load case input
				MemForce fceNew = new MemForce(fce[fsrcLCs[0]]);
				fceNew.LC = newLC;

				// Forces and moments squared
				Vector3 fc = fceNew.F * fceNew.F;
				Vector3 m = fceNew.M * fceNew.M;

				// Add squared force and moments for each load case input
				foreach (int lc in fsrcLCs.Skip(1).ToArray())
				{
					fc += fce[lc].F * fce[lc].F;
					m += fce[lc].M * fce[lc].M;
				}

				// Take square root for final force record
				fceNew.F = Vector3.SquareRoot(fc);
				fceNew.M = Vector3.SquareRoot(m);
				fceNew.Ff = new float[]{ fceNew.F.X, fceNew.F.Y, fceNew.F.Z,
										 fceNew.M.X, fceNew.M.Y, fceNew.M.Z};

				addedForces.Add(fceNew);
			}
			// For each unique node in DispList
			foreach (int n in dispNs)
			{
				pb.Tick();

				// Create dictionary of disps for this key, keyed by load case number
				var disp = dispLookup[n].GroupBy(d => d.LC).ToDictionary(g => g.Key, g => g.First());

				// Skip if the dictionary does not contain any of the input load cases
				var dsrcLCs = srcLCs.Intersect(disp.Keys).ToArray();
				if (dsrcLCs.Length == 0) { continue; }

				// Initiate new disp record using existing record for first load case input
				Disp dispNew = new Disp(disp[dsrcLCs[0]]);
				dispNew.LC = newLC;

				// Displacements and rotations squared
				float[] dr = dispNew.DR.Select(d => d * d).ToArray();

				// Add squared displacements and rotations for each load case input
				foreach (int lc in dsrcLCs.Skip(1).ToArray())
				{
					dr = dr.Zip(disp[lc].DR, (d1, d2) => d1 + (d2 * d2)).ToArray();
				}

				// Take square root for final displacement record
				dispNew.DR = dr.Select(d => (float)Math.Sqrt(d)).ToArray();

				addedDisps.Add(dispNew);
			}
			StressList.AddRange(addedStresses);
			ForceList.AddRange(addedForces);
			DispList.AddRange(addedDisps);
		}
		pb.Finish();
	}
	// Function to build load combination stress records with existing load case data and given
	// list of laod cmobinations.
	public void AssembleLoadCombos(string[] loadComboList)
	{
		// Initialize empty list of load combinations to be added
		List<Hashtable> loadCombos = new List<Hashtable>();

		// Cycle through each load combination definition input
		foreach (string loadCombo in loadComboList)
		{
			Hashtable loadComb = new Hashtable();

			// Convert load combination definition of integers and floats to a list of numbers to be separated
			// into other lists in order
			Queue<float> lcData = new Queue<float>(Array.ConvertAll(loadCombo.Split((char[])null,
								StringSplitOptions.RemoveEmptyEntries), float.Parse));

			// First number in list corresponds to the new load combination number
			loadComb["ID"] = Convert.ToInt32(lcData.Dequeue());

			// Initiate empty lists to be populated with remaning numbers from load combinaiton input
			loadComb.Add("LCFactors", new List<float>());
			loadComb.Add("LCNumbers", new List<int>());

			// Separate load case numbers from load factors in order. Lists should be of the same length
			while (lcData.Count >= 2)
			{
				((List<float>)loadComb["LCFactors"]).Add(lcData.Dequeue());
				((List<int>)loadComb["LCNumbers"]).Add(Convert.ToInt32(lcData.Dequeue()));
			}
			// Add to list of new load combinations to be created
			loadCombos.Add(loadComb);
		}

		// Lookup stresses, forces and disps by elemID and/or node keys
		var strLookup = StressList.ToLookup(s => new ValueTuple<int, int>(s.elemID, s.node));
		var fceLookup = ForceList.ToLookup(f => new ValueTuple<int, int>(f.memID, f.nodeID));
		var dispLookup = DispList.ToLookup(d => d.node);

		// List of unique keys
		var strENs = strLookup.Select(s => s.Key).Distinct().ToArray();
		var fceENs = fceLookup.Select(f => f.Key).Distinct().ToArray();
		int[] dispNs = dispLookup.Select(d => d.Key).Distinct().ToArray();
		List<StressRecord> addedStresses = new List<StressRecord>();
		List<MemForce> addedForces = new List<MemForce>();
		List<Disp> addedDisps = new List<Disp>();

		var pb = new ProgressBar(loadCombos.Count * (strENs.Length + fceENs.Length + dispNs.Length),
								 title: "Creating New Load Combination Results");

		// For each load combination to be assembled
		foreach (Hashtable lc in loadCombos)
		{
			// ID of new load combination
			int id = (int)lc["ID"];
			// Load case numbers to be used in load combination
			List<int> lcNums = (List<int>)lc["LCNumbers"];
			// Load factors to be applied in load combination
			List<float> lcFacs = (List<float>)lc["LCFactors"];

			if (lcNums.Count != lcFacs.Count) { continue; }

			// For each unique key of element ID and node
			foreach (var en in strENs)
			{
				pb.Tick();

				// Create dictionary of stresses for this key, keyed by load case number
				var str = strLookup[en].GroupBy(s => s.LC).ToDictionary(g => g.Key, g => g.First());

				// Skip if the dictionary does not contain any of the input load cases
				if (lcNums.All(l => !str.ContainsKey(l))) { continue; }

				// Initiate new stress record using existing record for first load case input
				StressRecord rec = new StressRecord(str[lcNums[0]]);
				rec.LC = id;

				// Query stresses using the list of load case numbers (l) and multiply by corresponding factors
				// (f) and aggregate throughout.
				rec.S = lcFacs.Zip(lcNums, (f, l) =>
				{
					if (!str.ContainsKey(l)) return Matrix4x4.Identity - Matrix4x4.Identity;
					return ((StressRecord)str[l]).S * f;
				}).Aggregate((currStr, nextStr) => currStr + nextStr);

				// Query moments using the list of load case numbers (l) and multiply by corresponding factors
				// (f) and aggregate throughout.
				rec.M = lcFacs.Zip(lcNums, (f, l) =>
				{
					if (!str.ContainsKey(l)) return Matrix4x4.Identity - Matrix4x4.Identity;
					return ((StressRecord)str[l]).M * f;
				}).Aggregate((currMom, nextMom) => currMom + nextMom);

				rec.Sf = new float[]{rec.S.M11, rec.S.M12, rec.S.M13, rec.S.M22, rec.S.M23,
							rec.M.M11, rec.M.M12, rec.M.M22};

				addedStresses.Add(rec);
			}
			// For each unique key of member ID and node
			foreach (var en in fceENs)
			{
				pb.Tick();

				// Create dictionary of forces for this key, keyed by load case number
				var fce = fceLookup[en].GroupBy(f => f.LC).ToDictionary(g => g.Key, g => g.First());

				// Skip if the dictionary does not contain any of the input load cases
				if (lcNums.All(l => !fce.ContainsKey(l))) { continue; }

				// Initiate new force record using existing record for first load case input
				MemForce rec = new MemForce(fce[lcNums[0]]);
				rec.LC = id;

				// Query forces using the list of load case numbers (l) and multiply by corresponding factors
				// (f) and aggregate throughout.
				rec.F = lcFacs.Zip(lcNums, (f, l) =>
				{
					if (!fce.ContainsKey(l)) return Vector3.Zero;
					return ((MemForce)fce[l]).F * f;
				}).Aggregate((currFce, nextFce) => currFce + nextFce);

				// Query moments using the list of load case numbers (l) and multiply by corresponding factors
				// (f) and aggregate throughout.
				rec.M = lcFacs.Zip(lcNums, (f, l) =>
				{
					if (!fce.ContainsKey(l)) return Vector3.Zero;
					return ((MemForce)fce[l]).M * f;
				}).Aggregate((currMom, nextMom) => currMom + nextMom);

				rec.Ff = new float[]{rec.F.X, rec.F.Y, rec.F.Z,
							rec.M.X, rec.M.Y, rec.M.Z};

				addedForces.Add(rec);
			}
			// For each unique key of node
			foreach (int n in dispNs)
			{
				pb.Tick();

				// Create dictionary of displacements for this key, keyed by load case number
				var disp = dispLookup[n].GroupBy(d => d.LC).ToDictionary(g => g.Key, g => g.First());

				// Skip if the dictionary does not contain any of the input load cases
				if (lcNums.All(l => !disp.ContainsKey(l))) { continue; }

				// Initiate new displacement record using existing record for first load case input
				Disp rec = new Disp(disp[lcNums[0]]);
				rec.LC = id;

				// Query displacements using the list of load case numbers (l) and multiply by corresponding factors
				// (f) and aggregate throughout.
				rec.DR = lcFacs.Zip(lcNums, (f, l) =>
				{
					if (!disp.ContainsKey(l)) return new float[] { 0, 0, 0, 0, 0, 0 };
					return ((Disp)disp[l]).DR.Select(d => d * f).ToArray();
				}).Aggregate((currDisp, nextDisp) => currDisp.Zip(nextDisp, (a, b) => a + b).ToArray());

				addedDisps.Add(rec);
			}
		}
		StressList.AddRange(addedStresses);
		ForceList.AddRange(addedForces);
		DispList.AddRange(addedDisps);
		pb.Finish();
	}
	// ADDED FOR SECTION CUTS WITH MULTIPLE MODELS
	public void AssignModelIDs(object ModelID)
	{
		foreach (StressRecord s in StressList) { s.modelID = ModelID; }
	}
	public void AbsBandaid(int LC)
	{
		foreach (StressRecord s in StressList)
		{
			if (s.LC == LC)
			{
				s.S = new Matrix4x4(
					Math.Abs(s.S.M11), Math.Abs(s.S.M12), Math.Abs(s.S.M13), Math.Abs(s.S.M14),
					Math.Abs(s.S.M21), Math.Abs(s.S.M22), Math.Abs(s.S.M23), Math.Abs(s.S.M24),
					Math.Abs(s.S.M31), Math.Abs(s.S.M32), Math.Abs(s.S.M33), Math.Abs(s.S.M34),
					Math.Abs(s.S.M41), Math.Abs(s.S.M42), Math.Abs(s.S.M43), Math.Abs(s.S.M44));
				s.M = new Matrix4x4(
					Math.Abs(s.M.M11), Math.Abs(s.M.M12), Math.Abs(s.M.M13), Math.Abs(s.M.M14),
					Math.Abs(s.M.M21), Math.Abs(s.M.M22), Math.Abs(s.M.M23), Math.Abs(s.M.M24),
					Math.Abs(s.M.M31), Math.Abs(s.M.M32), Math.Abs(s.M.M33), Math.Abs(s.M.M34),
					Math.Abs(s.M.M41), Math.Abs(s.M.M42), Math.Abs(s.M.M43), Math.Abs(s.M.M44));
				s.Sf = s.Sf.Select(f => Math.Abs(f)).ToArray();
			}
		}
	}
	private void ParseForces()
	{
		//JSON was a mistake...
		var afp = new AnlForceParser(anlPath);
		var forceSections = Sections.FindAll(s => s.Name.ToUpper().Contains("MEMBER FORCES"));
		foreach (var section in forceSections)
			afp.ParseSection(section, MemberDict, targetState);
		ForceList = afp.Forces;
	}

	public void PrintSectionsToFiles(string outputDir)
	{
		string[] exclude = { "DISPLACEMENTS" }; // "STRESS",
		var uniqueNames = new HashSet<string>();
		for (int s = 0; s < Sections.Count; s++) { uniqueNames.Add(Sections[s].Name); }
		var names = new List<string>();

		// Loop over section names and compare against exclude list
		// Build "names" list based on the combination of section names and NOT in exclude
		foreach (string name in uniqueNames)
		{
			bool skip = false;
			for (int e = 0; e < exclude.Length; e++)
			{
				if (name.IndexOf(exclude[e], StringComparison.OrdinalIgnoreCase) >= 0) { skip = true; break; }
			}
			if (!skip) { names.Add(name); }
		}

		if (!Directory.Exists(outputDir)) { Directory.CreateDirectory(outputDir); }

		using (var reader = new FileStream(anlPath, FileMode.Open, FileAccess.Read, FileShare.Read))
		{
			for (int n = 0; n < names.Count; n++)
			{
				string name = names[n];
				string outPath = Path.Combine(outputDir, name.Replace(" ", "_") + ".txt");

				using (var w = new StreamWriter(outPath, false, new System.Text.UTF8Encoding(false)))
				{
					for (int s = 0; s < Sections.Count; s++)
					{
						if (Sections[s].Name != name) { continue; }
						int len = (int)(Sections[s].EndOffset - Sections[s].StartOffset);
						if (len <= 0) { continue; }

						byte[] buf = new byte[len];
						reader.Seek(Sections[s].StartOffset, SeekOrigin.Begin);
						reader.Read(buf, 0, len);
						w.Write(System.Text.Encoding.ASCII.GetString(buf, 0, len));
					}
				}
				Console.WriteLine(" " + name + " -> " + outPath);
			}
		}
	}

}

public class ModelConfig
{
	public string anlPath;
	public bool loadCenterStresses = true;
	public bool loadJointStresses = true;
	public bool loadDisplacements = true;
	public bool loadForces = true;
	public List<string> FH_Groups = new List<string>(); // Sanitized
	public List<string> SF_Groups = new List<string>(); // Sanitized
	public CoordFrame targetState = CoordFrame.Reference;
	public int? stressStart;
	public int? stressEnd;

	public ModelConfig(string anlPath)
	{
		this.anlPath = anlPath;
		init_groups();
	}

	public void SetCoordStateReference()
	{
		targetState = CoordFrame.Reference;
	}

	public void SetCoordStateLocal()
	{
		targetState = CoordFrame.Local;
	}

	private void init_groups()
	{
		// Sanitized
	}

}

// HELPERS TO PROBABLY PUT ELSEWHERE:
public static class Matrix4x4Extensions
{
	public static Matrix4x4 Square(this Matrix4x4 m)
	{
		return new Matrix4x4(
			m.M11 * m.M11, m.M12 * m.M12, m.M13 * m.M13, m.M14 * m.M14,
			m.M21 * m.M21, m.M22 * m.M22, m.M23 * m.M23, m.M24 * m.M24,
			m.M31 * m.M31, m.M32 * m.M32, m.M33 * m.M33, m.M34 * m.M34,
			m.M41 * m.M41, m.M42 * m.M42, m.M43 * m.M43, m.M44 * m.M44
		);
	}

	public static Matrix4x4 SquareRoot(this Matrix4x4 m)
	{
		return new Matrix4x4(
					(float)Math.Sqrt(m.M11), (float)Math.Sqrt(m.M12), (float)Math.Sqrt(m.M13), (float)Math.Sqrt(m.M14),
					(float)Math.Sqrt(m.M21), (float)Math.Sqrt(m.M22), (float)Math.Sqrt(m.M23), (float)Math.Sqrt(m.M24),
					(float)Math.Sqrt(m.M31), (float)Math.Sqrt(m.M32), (float)Math.Sqrt(m.M33), (float)Math.Sqrt(m.M34),
					(float)Math.Sqrt(m.M41), (float)Math.Sqrt(m.M42), (float)Math.Sqrt(m.M43), (float)Math.Sqrt(m.M44)
				);

	}
}