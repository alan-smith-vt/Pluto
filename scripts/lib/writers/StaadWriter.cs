using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Linq;
using System.Linq.Expressions;
using System.Text.RegularExpressions;
using System.Numerics;

// Takes a parsed Sap2kParser with raw tables loaded and emits a STAAD input file
public class StaadWriter
{
	private GeometryResult geom;

	public Dictionary<string, Group> groups;
	public SubgradeType bound;
	public ModelArchetype archetype;
	public bool rigid;

	public StaadInputBuilder config;

	// Groups
	public Dictionary<string, Group> elemGroups;
	public Dictionary<string, Group> loadGroups;
	public Dictionary<string, Group> thicknessGroups;
	public Dictionary<string, Group> soilSpringsGroups;

	// Mapping R1 -> _DL_Roofing System, _DL_Guardrails, ect.
	public Dictionary<string, List<ReferenceLoad>> referenceLoads;

	private List<SoilSpringRegion> soilSpringRegions = new List<SoilSpringRegion>();

	public StaadWriter(StaadInputBuilder config)
	{
		this.config = config;
		geom = config.geom;
		groups = new Dictionary<string, Group>();
		elemGroups = config.elemGroups;
		loadGroups = config.loadGroups;
		thicknessGroups = config.thicknessGroups;
		soilSpringsGroups = config.soilSpringsGroups;
		this.AddSoilSpringRegion(config.baseMatSprings);
		this.AddSoilSpringRegion(config.tunnelSprings);
		this.AssembleReferenceLoads(config.loadData);

		this.bound = config.bound;
		this.archetype = config.archetype;
		this.rigid = config.rigid;
	}

	// =============================
	// --- Public writers ---
	// =============================

	public void Write(string outPath)
	{
		using (StreamWriter sw = new StreamWriter(outPath, false, new UTF8Encoding(false)))
		{
			bool flag = true;
			WriteHeader(sw);
			Debug.Printf("Wrote Header", flag);
			WriteJoints(sw);
			Debug.Printf("Wrote Joints", flag);
			WriteMembers(sw);
			Debug.Printf("Wrote Members", flag);
			WritePlates(sw);
			Debug.Printf("Wrote Plates", flag);
			WriteAllGroups(sw);
			Debug.Printf("Wrote Groups", flag);
			WriteSupports(sw);
			Debug.Printf("Wrote Supports", flag);
			WriteMaterials(sw);
			Debug.Printf("Wrote Materials", flag);
			WriteMemberProperties(sw);
			Debug.Printf("Wrote Member Properties", flag);
			WriteElementProperties(sw);
			Debug.Printf("Wrote Element Properties", flag);
			WriteConstants(sw);
			Debug.Printf("Wrote Constants", flag);
			WriteOffsets(sw);
			Debug.Printf("Wrote Offsets", flag);
			WriteLoads(sw);
			Debug.Printf("Wrote Loads", flag);
			WriteFooter(sw);
			Debug.Printf("Wrote Footer", flag);
		}
	}

	// =============================
	// --------- Helpers -----------
	// =============================

	// --- Public helper for assigning soil spring region data
	public void AddSoilSpringRegion(SoilSpringRegion region)
	{
		// Validate NodeIds against areas
		foreach (int id in region.NodeIds)
		{
			if (!region.AreaByNode.ContainsKey(id))
			{
				throw new System.Exception(string.Format(
					"SoilSpringRegion '{0}' references node {1} which is missing from AreaByNode",
					region.Name, id));
			}
		}
		// Sort node IDs in ascending for deterministic output
		region.NodeIds.Sort();
		soilSpringRegions.Add(region);
	}

	// loadData GROUPBY reference load id
	public void AssembleReferenceLoads(Dictionary<string, List<string>> loadData)
	{
		ValidateLoadData(loadData);
		referenceLoads = new Dictionary<string, List<ReferenceLoad>>();
		for (int i = 0; i < loadData["Reference Load"].Count; i++)
		{
			string referenceLoadId = loadData["Reference Load"][i];
			if (referenceLoadId == "") continue;

			// Init the empty list if the dictionary key doesn't exist
			if (!referenceLoads.ContainsKey(referenceLoadId))
			{
				referenceLoads[referenceLoadId] = new List<ReferenceLoad>();
			}

			string groupName = loadData["Load Group Name"][i].ToUpper();

			GroupTargetType type = loadGroups[groupName].Type;
			double load_1 = double.Parse(loadData["Load_1"][i]);
			double load_2;
			double.TryParse(loadData["Load_2"][i], out load_2); // defaults to zero if empty

			// Average if load_2 is present, load_1 otherwise
			double loadValue = load_2 != 0 ? (load_1 + load_2) / 2 : load_1;

			ReferenceLoad rl = new ReferenceLoad(
								referenceLoadId, // Redundancy
								groupName,
								loadValue.ToString("F7"), // Decimal notation, 7 decimal places
								loadData["Units"][i], // QA/QC
								loadData["Force/Moment Direction"][i],
								type);

			referenceLoads[referenceLoadId].Add(rl);
		}
	}

	private void ValidateLoadData(Dictionary<string, List<string>> loadData)
	{
		foreach (var groupName in loadData["Load Group Name"])
		{
			if (groupName == "") continue;
			var result = loadGroups.ContainsKey(groupName.ToUpper());
			if (!result)
			{
				Console.WriteLine(string.Format("{0} is NOT present in loadGroups", groupName));
			}
		}
	}

	public string Condense(List<int> input)
	{
		var nums = new SortedSet<int>(input);
		if (nums.Count == 0)
		{
			return "";
		}

		var parts = new List<string>();
		int start = 0;
		int end = 0;
		bool first = true;

		foreach (int n in nums)
		{
			if (first)
			{
				start = n;
				end = n;
				first = false;
			}
			else if (n == end + 1)
			{
				end = n;
			}
			else
			{
				parts.Add(start == end ? start.ToString() : string.Format("{0} TO {1}", start, end));
				start = n;
				end = n;
			}
		}

		parts.Add(start == end ? start.ToString() : string.Format("{0} TO {1}", start, end));

		return string.Join(" ", parts);
	}

	private string WrapStaad(string input, int maxLen = 70)
	{
		if (string.IsNullOrEmpty(input) || input.Length <= maxLen)
		{
			return input;
		}

		var lines = new List<string>();
		int pos = 0;
		int chunkLen = maxLen - 2;

		while (input.Length - pos > maxLen)
		{
			int breakAt = input.LastIndexOf(' ', pos + chunkLen, chunkLen);
			if (breakAt <= pos)
			{
				breakAt = pos + chunkLen;
			}
			lines.Add(input.Substring(pos, breakAt - pos) + " -");
			pos = breakAt;
			while (pos < input.Length && input[pos] == ' ')
			{
				pos++;
			}
		}
		lines.Add(input.Substring(pos));

		return string.Join("\r\n", lines);
	}

	// =============================
	// --- Section block writers ---
	// =============================

	private void WriteHeader(StreamWriter sw)
	{
		// Hardcode
		string dt = DateTime.Today.ToString("dd-MMM-yy");
		string[] tokens = new string[]
		{
			"" // sanitized
		};
		foreach (string token in tokens)
		{
			sw.WriteLine(token);
		}
	}

	private void WriteJoints(StreamWriter sw)
	{
		sw.WriteLine("**{ JOINT COORDINATES");
		sw.WriteLine("JOINT COORDINATES");
		List<Node> NodeList = geom.NodeDict.Select(n => n.Value).ToList();
		//foreach (Node n in geom.NodeDict.Values)
		for (int i = 0; i < NodeList.Count; i++)
		{
			Node n = NodeList[i];
			//sw.WriteLine(string.Format("{0} {1} {2} {3};",
			sw.Write(string.Format("{0} {1} {2} {3}; ",
					n.id,
					n.xyz.X,
					n.xyz.Y,
					n.xyz.Z
					));
			if (i % 2 == 0) sw.Write("\r\n");
		}
		sw.WriteLine("");
		sw.WriteLine("**}");
	}

	private void WriteMembers(StreamWriter sw)
	{
		if (geom.MemberDict.Count > 0)
		{
			sw.WriteLine("**{ MEMBER INCIDENCES");
			sw.WriteLine("MEMBER INCIDENCES");
			foreach (var kvp in geom.MemberDict)
			{
				int id = kvp.Key;
				Member mem = kvp.Value;
				sw.WriteLine(string.Format("{0} {1} {2};",mem.id, mem.n[0].id, mem.n[1].id));
			}
			sw.WriteLine("**} ");
		}
	}

	private void WritePlates(StreamWriter sw)
	{
		sw.WriteLine("**{ ELEMENT INCIDENCES SHELL");
		sw.WriteLine("ELEMENT INCIDENCES SHELL");
		List<Element> ElementList = geom.ElementDict.Select(e => e.Value).ToList();
		// id n1 n2 n3 [n4]
		//foreach (Element e in geom.ElementDict.Values)
		for (int i = 0; i < ElementList.Count; i++)
		{
			Element e = ElementList[i];
			string j4OrNull = (e.nNodes == 4) ? (" " + e.n[3].id) : "";
			//sw.WriteLine(string.Format("{0} {1} {2} {3}{4};",
			sw.Write(string.Format("{0} {1} {2} {3}{4}; ",
					e.id,
					e.n[0].id,
					e.n[1].id,
					e.n[2].id,
					j4OrNull
					));
			if (i % 2 == 0) sw.Write("\r\n");
		}
		sw.WriteLine("");
		sw.WriteLine("**}");
	}

	private void WriteAllGroups(StreamWriter sw)
	{
		sw.WriteLine("**{ START GROUP DEFINITION");

		sw.WriteLine("START GROUP DEFINITION");

		WriteGroup(sw, "Geometry Groups", elemGroups);
		WriteGroup(sw, "Loading Groups", loadGroups);
		WriteGroup(sw, "Thickness Groups", thicknessGroups);
		WriteGroup(sw, "Soil Springs Groups", soilSpringsGroups);
		WriteGroup(sw, "Rebar Groups", config.rebarGroups);

		sw.WriteLine("END GROUP DEFINITION");
		sw.WriteLine("**}");
	}

	private void WriteGroup(StreamWriter sw, string jsonName, Dictionary<string, Group> singleGroup)
	{
		if (singleGroup == null) return;

		sw.WriteLine("**{ \t" + jsonName);
		foreach (var kvp in singleGroup)
		{
			Group grp = kvp.Value;
			string name = "_" + grp.Name;
			name = name.Replace(" ", "");
			List<int> ids = grp.Ids;
			if (ids.Count == 0) continue;
			// Console.WriteLine("Parsing group " + grp.Name);
			sw.WriteLine("**{ \t\t" + grp.Name);
			if (grp.Type == GroupTargetType.Plates)
			{
				sw.WriteLine("ELEMENT");
			}
			else if (grp.Type == GroupTargetType.Joints)
			{
				sw.WriteLine("JOINT");
			}
			string nameVals = name + " " + Condense(ids);
			sw.WriteLine(WrapStaad(nameVals));
			sw.WriteLine("**}");
		}
		sw.WriteLine("**}");
	}

	// Wrapper that loops over the list of soil spring regions to write them
	private void WriteSupports(StreamWriter sw)
	{
		sw.WriteLine("**{ SUPPORTS");
		sw.WriteLine("SUPPORTS");
		foreach (SoilSpringRegion region in soilSpringRegions)
		{
			WriteSoilSpringRegion(region, sw);
		}
		//Set psds bottom nodes to enforced
		if (archetype == ModelArchetype.PSDS)
		{
			sw.WriteLine(""); //sanitized. Node group and support cond.
			sw.WriteLine(""); //sanitized. Node group and support cond.
		}
		sw.WriteLine("**}");
	}

	private void WriteSoilSpringRegion(SoilSpringRegion region, StreamWriter sw)
	{
		double totalArea = 0.0;
		for (int i = 0; i < region.NodeIds.Count; i++)
		{
			totalArea += region.AreaByNode[region.NodeIds[i]];
		}
		sw.WriteLine("**{ \t" + region.Name + " SOIL SPRINGS");
		sw.WriteLine(string.Format("* Vertical Stiffness: 	{0:F6} (force/length/area)", region.Kv));
		sw.WriteLine(string.Format("* Horizontal Stiffness: {0:F6} (force/length/area)", region.Kh));
		sw.WriteLine(string.Format("* Node count: {0}", region.NodeIds.Count));
		sw.WriteLine(string.Format("* Total area: {0}", totalArea));

		for (int i = 0; i < region.NodeIds.Count; i++)
		{
			int id = region.NodeIds[i];
			double area = region.AreaByNode[id];
			double kfx = area * region.Kh;
			double kfy = area * region.Kv;
			double kfz = area * region.Kh;

			// PSDS Branch
			if (archetype == ModelArchetype.PSDS)
			{
				sw.WriteLine(string.Format("{0} FIXED BUT FY MX MY MZ KFX {1:F6} KFZ {2:F6}",
					id, kfx, kfz));
			} else
			{
				sw.WriteLine(string.Format("{0} FIXED BUT MX MY MZ KFX {1:F6} KFY {2:F6} KFZ {3:F6}",
					id, kfx, kfy, kfz));
			}
		}
		sw.WriteLine("**}");
	}

	private void WriteMaterials(StreamWriter sw)
	{
		sw.WriteLine("**{ MATERIAL PROPERTY DEFINITION");

		// Hardcode
		string[] tokens = new string[]
		{
			"" // sanitized
		};
		foreach (string token in tokens)
		{
			sw.WriteLine(token);
		}

		sw.WriteLine("**}");
	}

	private void WriteMemberProperties(StreamWriter sw)
	{
		if (geom.MemberDict.Count > 0)
		{
			sw.WriteLine("**{ MEMBER PROPERTY");
			sw.WriteLine("MEMBER PROPERTY");
			foreach (var kvp in geom.MemberDict)
			{
				int id = kvp.Key;
				Member mem = kvp.Value;
				sw.WriteLine(string.Format("{0} PRIS YD {1:F4}",mem.id, mem.YD));
			}
			sw.WriteLine("**} ");
		}
	}

	private void WriteElementProperties(StreamWriter sw)
	{

		sw.WriteLine("**{ ELEMENT PROPERTY");
		sw.WriteLine("ELEMENT PROPERTY");
		// We're doing a lookup over all elements instead of just using the predicate???
		//	WriteProperties should just pull from the predicates instead of remaking extra groups
		ILookup<float, Element> ElemsByThick = geom.ElementDict.Values.ToLookup(e => e.t);

		foreach (float t in ElemsByThick.Select(e => e.Key))
		{
			if (t == 0)
			{
				System.Console.WriteLine(string.Format("WARNING: zero thickness element"));
				continue; // Skip no thickness elements
			}
			List<int> elList = ElemsByThick[t].Select(e => e.id).ToList();
			//-------Creating element groups by plate thickness--------------
			// Note: this is not writing these groups to the STAAD file but
			// that's fine, not needed
			Group tGroup = new Group();
			tGroup.Name = String.Format("_{0}inThickPlates", t);
			tGroup.Type = GroupTargetType.Plates;
			tGroup.Ids = elList;
			groups.Add(tGroup.Name, tGroup);
			//---------------------------------------------------------------
			string condensedElsList = Condense(elList);
			string thicknessGroup = condensedElsList + " THICKNESS " + t;
			thicknessGroup = WrapStaad(thicknessGroup);
			sw.WriteLine(thicknessGroup);
		}
		sw.WriteLine("**}");
	}

	// Note that this was moved. Previous order was "Materials -> constants -> element properties"
	private void WriteConstants(StreamWriter sw)
	{
		sw.WriteLine("**{ CONSTANTS");
		sw.WriteLine("CONSTANTS");
		sw.WriteLine("MATERIAL CRACKED ALL");

		if (rigid)
		{
			sw.WriteLine("MATERIAL CRACKED_RIGID MEMBER _RigidEndZones");
		}

		sw.WriteLine("**} ");
	}

	private void WriteOffsets(StreamWriter sw)
	{
		sw.WriteLine("**{ ELEMENT OFFSET");
		// Hardcoded offsets sanitized

		sw.WriteLine("**}");
		if (archetype == ModelArchetype.PSDS)
		{
			sw.WriteLine("**{ COMPRESSION ONLY MEMBER DEF");
			using (var reader = new StreamReader(config.psdsCompressionPath))
			{
				sw.Write(reader.ReadToEnd());
			}
			sw.WriteLine("**}");
		}
	}

	private void WriteReferenceLoads(StreamWriter sw, List<ReferenceLoad> loads)
	{
		//Quick check for ANY joint or element loads
		bool jointLoads = false;
		bool elementLoads = false;
		foreach (ReferenceLoad load in loads)
		{
			if (load.Type == GroupTargetType.Joints)
			{
				jointLoads = true;
			}
			if (load.Type == GroupTargetType.Plates)
			{
				elementLoads = true;
			}
		}

		//Joint Loads (if present)
		if (jointLoads)
		{
			sw.WriteLine("JOINT LOAD");
			for (int i = 0; i < loads.Count; i++)
			{
				ReferenceLoad load = loads[i];
				if (load.Type == GroupTargetType.Joints)
				{
					// Consider parsing load.Load as double and writing a fixed number of decimals
					sw.WriteLine(string.Format("_{0} {1} {2}", load.GroupName.Replace(" ",""), load.Direction, load.Load));
				}
			}
		}
		//Element Loads (if present)
		if (elementLoads)
		{
			sw.WriteLine("ELEMENT LOAD");
			for (int i = 0; i < loads.Count; i++)
			{
				ReferenceLoad load = loads[i];
				if (load.Type == GroupTargetType.Plates)
				{
					// Consider parsing load.Load as double and writing a fixed number of decimals
					sw.WriteLine(string.Format("_{0} PR {1} {2}", load.GroupName.Replace(" ",""), load.Direction, load.Load));
				}
			}
		}
	}

	private void WriteReferenceLoadsForMass(StreamWriter sw, List<ReferenceLoad> loads, double scale)
	{
		//Quick check for ANY joint or element loads
		bool jointLoads = false;
		bool elementLoads = false;
		foreach (ReferenceLoad load in loads)
		{
			if (load.Type == GroupTargetType.Joints)
			{
				jointLoads = true;
			}
			if (load.Type == GroupTargetType.Plates)
			{
				elementLoads = true;
			}
		}

		//Joint Loads (if present)
		if (jointLoads)
		{
			sw.WriteLine("JOINT LOAD");
			for (int i = 0; i < loads.Count; i++)
			{
				ReferenceLoad load = loads[i];
				if (load.Direction[0] == 'M') { continue; } // Skip moments
				if (load.Type == GroupTargetType.Joints)
				{
					foreach (string dir in new string[] {"FX","FY","FZ"})
					{
						double loadVal = Math.Abs(double.Parse(load.Load))*scale;
						sw.WriteLine(string.Format("_{0} {1} {2:F7}", load.GroupName.Replace(" ",""), dir, loadVal));
					}
				}
			}
		}
		//Element Loads (if present)
		if (elementLoads)
		{
			sw.WriteLine("ELEMENT LOAD");
			for (int i = 0; i < loads.Count; i++)
			{
				ReferenceLoad load = loads[i];
				if (load.Type == GroupTargetType.Plates)
				{
					foreach (string dir in new string[] {"GX","GY","GZ"})
					{
						double loadVal = Math.Abs(double.Parse(load.Load))*scale;
						sw.WriteLine(string.Format("_{0} PR {1} {2:F7}", load.GroupName.Replace(" ",""), dir, loadVal));
					}
				}
			}
		}
	}

	private void WriteLoads(StreamWriter sw)
	{
		sw.WriteLine("**{ DEFINE REFERENCE LOADS (& Mode shape cutoff)");
		sw.WriteLine("CUT OFF MODE SHAPE 300");
		sw.WriteLine("DEFINE REFERENCE LOADS");

		// Accidental Torsion point loads (triangular distribution)
		if (archetype == ModelArchetype.SEIS_TORS)
		{
			foreach (TorsionLoad tload in config.TorsionLoads)
			{
				sw.WriteLine(string.Format("Load {0} LOADTYPE None TITLE {1}", tload.id, tload.title));
				sw.WriteLine("MEMBER LOAD");
				foreach (MemberLoadTrap mlt in tload.data)
				{
					sw.WriteLine(string.Format("{0} {1} {2} {3}",
						mlt.memberId, mlt.dir, mlt.F0, mlt.F1));
				}
			}
		}
		else // Only write primary reference loads for non-seismic torsion cases
		{
			// Consider sorting R1, R2, ect as a list to enforce sequential ordering
			foreach (var kvp in referenceLoads)
			{
				string referenceLoadId = kvp.Key;
				List<ReferenceLoad> loads = kvp.Value;

				// Consider adding a name to these reference loads (and type?)
				sw.WriteLine(string.Format("LOAD {0} LOADTYPE None TITLE {1}", referenceLoadId, config.referenceLoadTitles[referenceLoadId]));
				if (referenceLoadId == "R1") { sw.WriteLine("SELFWEIGHT Y -1.0"); }

				WriteReferenceLoads(sw, loads);
			}

			// Mass definition
			List<string> massLoads = new List<string> {"R1", "R2", "R3", "R4", "R5", "R7", "R9"};
			List<string> reducedMass25pLL = new List<string> {"R5", "R9"};

			sw.WriteLine("**{ Dynamic Mass Reference Load");
			sw.WriteLine("LOAD R43 LOADTYPE MASS TITLE DYNAMIC_MASS");
			sw.WriteLine("SELFWEIGHT X 1.0");
			sw.WriteLine("SELFWEIGHT Y 1.0");
			sw.WriteLine("SELFWEIGHT Z 1.0");
			foreach (string referenceLoadId in massLoads)
			{
				List<ReferenceLoad> loads;
				if (!referenceLoads.TryGetValue(referenceLoadId, out loads))
				{
					throw new System.Exception(string.Format("referenceLoadId {0} not present in referenceLoads dictionary", referenceLoadId));
				}

				double scale = reducedMass25pLL.Contains(referenceLoadId) ? 0.25 : 1.0;
				WriteReferenceLoadsForMass(sw, loads, scale);
			}
			sw.WriteLine("**}");
		}

		sw.WriteLine("END DEFINE REFERENCE LOADS");
		sw.WriteLine("**}");

		// Modal Damping
		if (archetype == ModelArchetype.SEIS)
		{
			string mdPath = config.modalDampingPath + bound + ".txt";
			using (var reader = new StreamReader(mdPath))
			{
				sw.Write(reader.ReadToEnd());
			}
		}

		sw.WriteLine("**{ DEFINE LOAD CASES");

		switch (archetype)
		{
			case ModelArchetype.PSDS:
				foreach (TrenchGeometry tg in config.trenches)
				{
					sw.WriteLine("**{ \t" + tg.Name);
					sw.WriteLine(string.Format("LOAD {0} LOADTYPE Gravity TITLE {1}", tg.LoadCase, tg.Name));
					sw.WriteLine("REFERENCE LOAD");
					sw.WriteLine("R1 1.0 R2 1.0 R3 1.0 R4 1.0 R5 0.8 R9 0.8 R7 1.0 R44 1.0 R54 1.0");
					sw.WriteLine("SUPPORT DISPLACEMENT LOAD");

					string trenchName = tg.Name;

					foreach (int id in config.allIds_PSDS)
					{
						double totalDisp = config.trenchDispDicts[trenchName][id];
						sw.WriteLine(string.Format("{0} FY {1:F4}",id, totalDisp));
					}
					sw.WriteLine("**} ");
					// break; // ONLY ONE TRENCH FOR DEBUG PURPOSES
				}
				break;

			case ModelArchetype.PSDS_STATIC:
				using (var reader = new StreamReader(config.psdsStaticLoadCasesPath))
				{
					sw.Write(reader.ReadToEnd());
				}
				break;

			case ModelArchetype.TMI:
				using (var reader = new StreamReader(config.loadCasesPath))
				{
					sw.Write(reader.ReadToEnd());
				}
				using (var reader = new StreamReader(config.tornadoLoadCasesPath))
				{
					sw.Write(reader.ReadToEnd());
				}
				break;

			case ModelArchetype.SEIS:
				using (var reader = new StreamReader(config.loadCasesPath))
				{
					sw.Write(reader.ReadToEnd());
				}

				// SEIS uses the RSA RESULTS
				string rsaPathFull = config.rsaLoadsPath + bound + ".txt";
				using (var reader = new StreamReader(rsaPathFull))
				{
					sw.Write(reader.ReadToEnd());
				}
				break;

			// RSA uses the base load cases path (not RSA loads)
			case ModelArchetype.RSA_MASS:
				using (var reader = new StreamReader(config.loadCasesPath))
				{
					sw.Write(reader.ReadToEnd());
				}
				break;
			case ModelArchetype.SEIS_TORS:
				string[] tokens = new string[] {
									"*Seismic effects due to torsion on the lateral force resisting",
									"*system. Multiplied by 1.2 for seismic.",
									"LOAD 32 LOADTYPE Seismic-H TITLE EX_TORSION_POS",
									"REFERENCE LOAD",
									"R56 1.2",
									"LOAD 33 LOADTYPE Seismic-H TITLE EX_TORSION_NEG",
									"REFERENCE LOAD",
									"R57 1.2",
									"LOAD 36 LOADTYPE Seismic-H TITLE EZ_TORSION_POS",
									"REFERENCE LOAD",
									"R58 1.2",
									"LOAD 37 LOADTYPE Seismic-H TITLE EZ_TORSION_NEG",
									"REFERENCE LOAD",
									"R59 1.2"};
				foreach (string token in tokens)
				{
					sw.WriteLine(token);
				}
				break;

			default:
				throw new ArgumentException("Model Archetype not implemented yet: " + archetype.ToString());
		}

		sw.WriteLine("**}");
	}

	private void WriteFooter(StreamWriter sw)
	{
		sw.WriteLine("**{ PRINT SPECIFICATIONS");

		string[] tokens = new string[]
		{
			"UNIT INCH POUND",
			"LOAD LIST ALL",
			"PERFORM ANALYSIS PRINT ALL",
			"PRINT CG",
			"*PRINT ELEMENT STRESSES LIST ALL",
			"PRINT MEMBER SECTION FORCES LIST ALL",
			"PRINT ELEMENT JOINT STRESSES LIST ALL",
			"PRINT JOINT DISPLACEMENTS LIST ALL",
			"FINISH"
		};

		foreach (string token in tokens)
		{
			sw.WriteLine(token);
		}

		sw.WriteLine("**}");
	}
}

