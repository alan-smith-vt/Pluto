using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;

public static class AppendPipeline
{
	// Stresses only version (no dsr)
	public static void AppendCombinedStresses(
		RawViewerWriter writer,
		LoadCombiner combiner,
		List<LoadCombo> combos)
	{
		ProgressBar pb = new ProgressBar(combos.Count, title: "Combining + writing stresses");
		foreach (LoadCombo combo in combos)
		{
			pb.Tick();
			var comboStresses = combiner.ParseCombo(combo);
			writer.AppendStresses(comboStresses);
		}
		pb.Finish();
	}

	// DSR & Stresses version
	public static void AppendCombinedStressesAndDSRs(
		RawViewerWriter writer,
		LoadCombiner combiner,
		DsrRunner runner,
		LoadCombo[] combos)
	{
		ProgressBar pb = new ProgressBar(combos.Length, title: "Combining + writing stresses and dsrs");
		foreach (LoadCombo combo in combos)
		{
			pb.Tick();
			List<StressRecord> comboStresses = combiner.ParseCombo(combo);
			writer.AppendStresses(comboStresses);
			if (combiner.byCaseDisp != null)
			{
				List<Disp> comboDisps = combiner.ParseComboDisp(combo);
				writer.AppendDisplacements(comboDisps);
			}
			List<DsrRecord> comboDsrs = runner.Calculate(comboStresses);
			writer.AppendDsr(comboDsrs);
		}
		pb.Finish();
	}

	// Chunked DSR writer for full stressList
	public static void ChunkedDSRWriter(
		RawViewerWriter writer,
		DsrRunner runner,
		List<StressRecord> stressList)
	{
		int CHUNK_SIZE = 10000;
		for (int start = 0; start < stressList.Count; start += CHUNK_SIZE)
		{
			int count = Math.Min(CHUNK_SIZE, stressList.Count - start);
			List<StressRecord> stressChunk = stressList.GetRange(start, count);
			List<DsrRecord> dsrChunk = runner.Calculate(stressChunk);
			writer.AppendDsr(dsrChunk);
		}
	}
}

public class ViewerExportPipeline
{
	public StaadModel model;
	public DsrRunner runner;

	// Maybe make this whole thing static?
	public ViewerExportPipeline()
	{
		// Pass
	}

	public void Run(ViewerExportConfig config)
	{
		ModelConfig modelConfig = new ModelConfig(config.anlPath);
		modelConfig.loadCenterStresses = config.loadCenterStresses;
		modelConfig.loadJointStresses = config.loadJointStresses;
		modelConfig.loadDisplacements = config.loadDisplacements;
		modelConfig.loadForces = config.loadForces;

		modelConfig.SetCoordStateReference(); // Defaults to Reference coordinate frame

		model = new StaadModel(modelConfig);

		if (config.expandCenter)
		{
			// Replace the stressList with joint stresses created from center stresses (i.e. flat shading on mesh)
			List<StressRecord> newStressList = ExpandCenterToJoints(model.StressList, model.ElementDict);
			model.StressList = newStressList;
		}

		if (config.rez)
		{
			RigidEndZoneVoider.Apply(model, config.rezPath);
		}

		if (config.fMuCorrection)
		{
			PSDS_FmuCorrection.Apply(model, config.staticSettlementPath, config.expandCenter);
		}

		if (config.oldFmuCorrection)
		{
			Console.WriteLine("APPLYING OLD INCORRECT F-MU MODIFICATION TO DATA FOR DEBUG PURPOSES. DO NOT USE FOR ANALYSIS");
			PSDS_FmuCorrection.ApplyOldCorrection(model);
		}

		LoadCombo[] combos = null;// initialize so the compiler shuts up
		if (config.comboPath != null)
		{
			combos = ComboLoader.Load(config.comboPath);
			// ValidateCombos mutates the LoadCaseNames in place to add combos
			combos = ComboLoader.ValidateCombos(combos, model.LoadCaseNames);
		}

		// Create the initial viewer file & write stresses
		var components = RawViewerWriter.BuildComponents(config.writeStresses, config.writeDisps,
			config.writeStrs, config.writeDsrs);

		RawViewerWriter rvw = new RawViewerWriter(config.rbnlPath, model.NodeDict, model.ElementDict,
			model.LoadCaseNames, components);

		rvw.Write();

		// Write primary stress list
		if (config.writeStresses)
		{
			rvw.AppendStresses(model.StressList);
		}

		// Write displacements if present
		if (config.writeDisps)
		{
			rvw.AppendDisplacements(model.DispList);
		}

		// Init the runner for design strengths and/or dsrs
		if (config.writeStrs || config.writeDsrs)
		{
			runner = new DsrRunner(model.geom, config.propertiesPath, config.propertiesPredicatePath);

			// Write the design strengths
			if (config.writeStrs)
			{
				rvw.AppendStr(runner.strs);
			}

			// Write DSRs for only primary stress list
			if (config.writeDsrs && config.comboPath == null)
			{
				AppendPipeline.ChunkedDSRWriter(rvw, runner, model.StressList);
			}

			// Write DSRs for only combo stresses
			if (config.writeDsrs && config.comboPath != null)
			{
				LoadCombiner combiner = new LoadCombiner(model.geom, model.StressList, model.DispList);
				// [AppendPipeline]::AppendCombinedStresses($rvw, $combiner, $combos)
				AppendPipeline.AppendCombinedStressesAndDSRs(rvw, combiner, runner, combos);
			}
		}
	}

	public List<StressRecord> ExpandCenterToJoints(List<StressRecord> stressList, Dictionary<int, Element> elemDict)
	{
		int n = stressList.Count;

		List<StressRecord> newStressList = new List<StressRecord>();
		newStressList.Capacity = n * 5; // Upsize the capacity to avoid multiple reallocations

		ProgressBar pb = new ProgressBar(n, title: "Expanding center stresses to joints");
		for (int i = 0; i < n; i ++)
		{
			pb.Tick();
			StressRecord rec = stressList[i];
			if (rec.node != -1) { continue; } // skip joint stresses
			Element elem;
			if (elemDict.TryGetValue(rec.elemID, out elem))
			{
				for (int j = 0; j < elem.nNodes; j++)
				{
					StressRecord nrec = new StressRecord();
					nrec.elemID = rec.elemID;
					nrec.node = elem.n[j].id;
					nrec.LC = rec.LC;
					nrec.t = rec.t;
					// Deep copy of float[] Sf since StressView mutates
					// Console.WriteLine(string.Format("Writing center record from eid: {0} to joint j: {1} nid: {2} sf[0]: {3}",
											// rec.elemID, j, elem.n[j].id, rec.Sf[0]));
					float[] copy = new float[rec.Sf.Length];
					Array.Copy(rec.Sf, copy, rec.Sf.Length);
					nrec.Sf = copy;
					// Console.WriteLine(string.Format("New record eid: {0} to joint j: {1} nid: {2} sf[0]: {3}",
											// nrec.elemID, j, nrec.node, nrec.Sf[0]));
					newStressList.Add(nrec);
				}
			}
			else
			{
				Console.WriteLine(string.Format("Element {0} not found in ElementDict", rec.elemID));
			}
		}
		pb.Finish();
		return newStressList;
	}
}

public class ViewerExportConfig
{
	public string basePath;
	public string anlPath;
	public string rbnlPath;

	// Parameters to set manually
	public bool loadCenterStresses = true;
	public bool loadJointStresses = true;
	public bool loadDisplacements = true;
	public bool loadForces = true;

	public bool writeStresses = true;
	public bool writeDisps = false;
	public bool writeStrs = true;
	public bool writeDsrs = true;

	public bool rez = false;
	public bool fMuCorrection = false;
	public bool oldFmuCorrection = false;

	public bool expandCenter = false;

	// Combo path varies by model, null path means no combos.
	public string comboPath;

	// Paths sanitized
	public string propertiesPath = "";
	public string propertiesPredicatePath = "";
	public string rezPath = "";
	public string staticSettlementPath = "";


	public ViewerExportConfig(string basePath)
	{
		this.basePath = basePath;
		this.anlPath = basePath + ".anl";
		this.rbnlPath = basePath + ".rbnl";
	}

}

public static class MLikePipeline
{
	public static void SEIS(string workingDir, string basePath)
	{
		var modelConfig = new ModelConfig("");
		modelConfig.loadCenterStresses = false;
		modelConfig.loadJointStresses = true;
		modelConfig.loadDisplacements = false;
		modelConfig.loadForces = false;

		string outDir = basePath + "/";
		string anlPath = basePath + ".anl";

		modelConfig.SetCoordStateReference(); //default
		modelConfig.anlPath = anlPath;

		var model = new StaadModel(modelConfig);

		var MF = new MFormat(outDir, modelConfig.FH_Groups);
		string comboPath = workingDir + ""; //sanitized
		var combos = ComboLoader.Load(comboPath);

		// ValidateCombos mutates the LoadCaseNames in place to add combos
		//	and removes combos with missing cases
		combos = ComboLoader.ValidateCombos(combos, model.LoadCaseNames);

		var combiner = new LoadCombiner(model.geom, model.StressList);
		ProgressBar pb = new ProgressBar(combos.Length, title: "Computing load combos and exporting as M-Like");
		foreach (var combo in combos)
		{
			pb.Tick();
			List<StressRecord> comboStresses = combiner.ParseCombo(combo);
			// Writes center stresses
			MF.WriteStressList(model.Groups, comboStresses,
				ForceUnit.Kip, LengthUnit.Foot);
		}
		pb.Finish();
		CsvHelpers.SplitLargeCsvFiles(outDir);
	}

	public static void SEIS_TORS(string basePathBE)
	{
		string fileName = System.IO.Path.GetFileName(basePathBE);
		string dir = System.IO.Path.GetDirectoryName(basePathBE);

		string baseName = fileName.Remove(18,3);
		string fileNameLB = baseName.Insert(18,"LB-");
		string fileNameUB = baseName.Insert(18,"UB-");

		string basePath = System.IO.Path.Combine(dir, baseName);
		string basePathLB = System.IO.Path.Combine(dir, fileNameLB);
		string basePathUB = System.IO.Path.Combine(dir, fileNameUB);

		var modelConfig = new ModelConfig("");
		modelConfig.loadCenterStresses = false;
		modelConfig.loadJointStresses = true;
		modelConfig.loadDisplacements = false;
		modelConfig.loadForces = false;

		string outDir = basePath + "/";
		string anlPath = basePath + ".anl";

		modelConfig.SetCoordStateReference(); //default

		modelConfig.anlPath = basePathBE + ".anl";
		var modelBE = new StaadModel(modelConfig);

		modelConfig.anlPath = basePathLB + ".anl";
		var modelLB = new StaadModel(modelConfig);

		modelConfig.anlPath = basePathUB + ".anl";
		var modelUB = new StaadModel(modelConfig);

		var MF = new MFormat(outDir, modelConfig.FH_Groups);

		Dictionary<string, List<int>> groups = modelBE.Groups;

		MF.WriteStressList(groups, modelLB.StressList,
				ForceUnit.Kip, LengthUnit.Foot);

		MF.WriteStressList(groups, modelBE.StressList,
				ForceUnit.Kip, LengthUnit.Foot);

		MF.WriteStressList(groups, modelUB.StressList,
				ForceUnit.Kip, LengthUnit.Foot);

		CsvHelpers.SplitLargeCsvFiles(outDir);
	}

	public static void TMI(string workingDir, string basePath)
	{
		var modelConfig = new ModelConfig("");
		modelConfig.loadCenterStresses = false;
		modelConfig.loadJointStresses = true;
		modelConfig.loadDisplacements = false;
		modelConfig.loadForces = false;

		string outDir = basePath + "/";
		string anlPath = basePath + ".anl";

		modelConfig.SetCoordStateReference(); //default
		modelConfig.anlPath = anlPath;

		var model = new StaadModel(modelConfig);

		var MF = new MFormat(outDir, modelConfig.FH_Groups);
		string comboPath = workingDir + ""; //sanitized
		var combos = ComboLoader.Load(comboPath);

		// ValidateCombos mutates the LoadCaseNames in place to add combos
		//	and removes combos with missing cases
		combos = ComboLoader.ValidateCombos(combos, model.LoadCaseNames);

		var combiner = new LoadCombiner(model.geom, model.StressList);
		ProgressBar pb = new ProgressBar(combos.Length, title: "Computing load combos and exporting as M-Like");
		foreach (var combo in combos)
		{
			pb.Tick();
			List<StressRecord> comboStresses = combiner.ParseCombo(combo);
			// Writes center stresses
			MF.WriteStressList(model.Groups, comboStresses,
				ForceUnit.Kip, LengthUnit.Foot);
		}
		pb.Finish();
		CsvHelpers.SplitLargeCsvFiles(outDir);
	}

	public static void PSDS(string workingDir, string basePath, bool oldMethod)
	{
		var modelConfig = new ModelConfig("");
		modelConfig.loadCenterStresses = false;
		modelConfig.loadJointStresses = true;
		modelConfig.loadDisplacements = false;
		modelConfig.loadForces = false;

		string outDir = basePath + "/";
		string anlPath = basePath + ".anl";

		modelConfig.SetCoordStateReference(); //default
		modelConfig.anlPath = anlPath;

		var model = new StaadModel(modelConfig);

		if (oldMethod)
		{
			Console.WriteLine("APPLYING OLD INCORRECT F-MU MODIFICATION TO DATA FOR DEBUG PURPOSES. DO NOT USE FOR ANALYSIS");
			PSDS_FmuCorrection.ApplyOldCorrection(model);
		}
		else
		{
			string staticSettlementPath = ""; //sanitized

			PSDS_FmuCorrection.Apply(model, staticSettlementPath, false);
		}

		var MF = new MFormat(outDir, modelConfig.FH_Groups);

		// Writes center stresses
		MF.WriteStressList(model.Groups, model.StressList,
			ForceUnit.Kip, LengthUnit.Foot);

		CsvHelpers.SplitLargeCsvFiles(outDir);
	}

}

public static class DesignSpreadsheetPipeline
{
	public static void Create(string basePath)
	{
		string designOutDir = System.IO.Path.GetDirectoryName(basePath);
		string fileName = System.IO.Path.GetFileName(basePath);
		string outDir = basePath + "/ ";
		string anlPath = basePath + ".anl";

		// // No need to reload the model every time if we're just using the geometry
		// // 	Consider making non-static and load this stuff once
		ModelConfig config = new ModelConfig("");

		// Silly to use stdw_config here, consider replacing with a direct elemGroups loader
		string modelName = ""; //sanitized
		string root = ""; //sanitized
		StaadInputBuilder stdw_config = new StaadInputBuilder(root, modelName);

		stdw_config.bound = SubgradeType.BE;
		stdw_config.archetype = ModelArchetype.SEIS;
		stdw_config.rigid = false;

		stdw_config.Prepare();

		Dictionary<string, Group> groups = new Dictionary<string, Group>();
		foreach (string grpName in config.FH_Groups)
		{
			string name = grpName.Substring(1).ToUpper();
			groups[grpName] = stdw_config.elemGroups[name];
		}

		string reinforcementJSON = ""; //sanitized
		var predGrps = GroupsLoader.LoadGroupsJson(reinforcementJSON);
		var reinfGrps = GroupEvaluator.Evaluate(predGrps, stdw_config.geom);

		// Slabs output
		Console.WriteLine("Generating slab design output for " + fileName);
		ElemType elType = ElemType.Slab;
		string outName = fileName + "_" + elType + "design";
		string excelTemplate = ""; //sanitized
		stdw_config.geom.SetDesignTool(groups, excelTemplate, basePath, designOutDir, outName, elType, reinfGrps);

		// Walls output
		Console.WriteLine("Generating wall design output for " + fileName);
		elType = ElemType.Wall;
		outName = fileName + "_" + elType + "design";
		excelTemplate = ""; //sanitized
		stdw_config.geom.SetDesignTool(groups, excelTemplate, basePath, designOutDir, outName, elType, reinfGrps);
	}
}

public static class TotalReactionCalculator
{
	public static void CalculateSEIS(string comboCsvPath, string outKeyword, bool influenceAreas)
	{
		// Load soil spring regions
		//	(use staad input builder for convenience)
		string modelName = ""; //sanitized
		string root = ""; //sanitized

		StaadInputBuilder stdw_config = new StaadInputBuilder(root, modelName);

		var modelConfig = new ModelConfig("");
		modelConfig.loadCenterStresses = false;
		modelConfig.loadJointStresses = false;
		modelConfig.loadDisplacements = true;
		modelConfig.loadForces = false;

		// 	Load 3 SEIS model results
		//	Foreach model
		// 		Compute load combos for deflections
		// 		Foreach load combo
		// 			Convert deflections to forces for soil spring regions
		// 			Sum everything up
		//			Flush to csv

		foreach (SubgradeType bound in SubgradeType.GetValues(typeof(SubgradeType)))
		{
			Console.WriteLine(string.Format("Loading {0} model", bound.ToString()));
			// Output file:
			string outCsv = string.Format(
				root + "", //sanitized
				outKeyword,
				bound.ToString());

			using (var writer = new StreamWriter(outCsv))
			{
				writer.WriteLine("Load Combo, Total Reaction X (kip)," +
					"Total Reaction Y (kip), Total Reaction Z (kip)");
				// Reuse the same "model input builder" object and just redo the "prepare" stage for each LB, BE, UB
				stdw_config.bound = bound;
				stdw_config.archetype = ModelArchetype.SEIS_TORS;
				stdw_config.rigid = false;

				stdw_config.Prepare();

				SoilSpringRegion baseMatSprings = stdw_config.baseMatSprings;
				SoilSpringRegion tunnelSprings = stdw_config.tunnelSprings;

				modelConfig.anlPath = string.Format(
					root + "", //sanitized
					bound.ToString());

				StaadModel model = new StaadModel(modelConfig);

				LoadCombo[] combos = ComboLoader.Load(comboCsvPath);
				combos = ComboLoader.ValidateCombos(combos, model.LoadCaseNames);
				LoadCombiner combiner = new LoadCombiner(model.geom, model.StressList, model.DispList);

				// Verify on earthquake load cases (30, 31, 34) to confirm soil spring * displacement approach is valid
				LoadCombo EQx = new LoadCombo(30, "EQx", new int[] {30}, new float[] {1f});
				LoadCombo EQz = new LoadCombo(31, "EQz", new int[] {31}, new float[] {1f});
				LoadCombo EQy = new LoadCombo(34, "EQy", new int[] {34}, new float[] {1f});

				LoadCombo staticSettlement = new LoadCombo(35, "staticSettlement", new int[] {35}, new float[] {1f});

				int oldLen = combos.Length;
				Array.Resize(ref combos, oldLen  + 4);
				combos[oldLen] = EQx;
				combos[oldLen + 1] = EQz;
				combos[oldLen + 2] = EQy;
				combos[oldLen + 3] = staticSettlement;

				// 204 combos
				foreach (var combo in combos)
				{
					string lc = string.Format("{0}: {1}", combo.Id, combo.Name);
					Console.WriteLine(string.Format("\tLoading {0} combo", lc));
					List<Disp> comboDisps = combiner.ParseComboDisp(combo);
					// Build dictionary of displacements since we only care about soil spring ids
					Dictionary<int, Disp> comboDispDict = comboDisps.ToDictionary(n => n.node);

					float totalX = 0;
					float totalY = 0;
					float totalZ = 0;

					foreach (SoilSpringRegion springs in new List<SoilSpringRegion> {baseMatSprings, tunnelSprings})
					{
						// For loop instead of foreach because faster
						for (int i = 0; i < springs.NodeIds.Count; i++)
						{
							int id = springs.NodeIds[i];
							float area = (float)springs.AreaByNode[id];
							float kh = (float)(area * springs.Kh); // kip/in
							float kv = (float)(area * springs.Kv); // kip/in
							Disp dispRec = comboDispDict[id];

							float dx = dispRec.DR[(int)DR.dx]; // in
							float dy = dispRec.DR[(int)DR.dy];
							float dz = dispRec.DR[(int)DR.dz];

							float rx = dx * kh; // kip
							float ry = dy * kv;
							ry = (ry < 0 || (combo.Id < 1000)) ? ry : 0; // compression only
							float rz = dz * kh;

							totalX += rx;
							totalY += ry;
							totalZ += rz;
						}
					}
					Console.WriteLine(string.Format("\t {0,10:F0},{1,10:F0},{2,10:F0}",totalX, totalY, totalZ));

					writer.WriteLine(string.Format("{0},{1},{2},{3}",lc, totalX, totalY, totalZ));
				}
			}
		}

		if (influenceAreas)
		{
			CsvHelpers.WriteInfluenceAreas(stdw_config, root + "/Results/InfluenceAreas.csv");
		}

	}
}

public static class BearingViewerExport
{
	public static void CreateBearingViewer()
	{
		// sanitized
	}

	public static void CreateSettlementViewer()
	{
		// sanitized
	}

	private static Disp FindWorstBearing(List<Disp> dispList)
	{
		float minBearing = 0;
		int minBearing_i = -1;
		for (int i = 0; i < dispList.Count; i++)
		{
			Disp d = dispList[i];
			float val = d.DR[(int)DR.dy];
			if (d.DR[(int)DR.dy] < minBearing)
			{
				minBearing = val;
				minBearing_i = i;
			}
		}
		return dispList[minBearing_i];
	}
}