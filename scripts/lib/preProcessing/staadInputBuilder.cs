// STUB -- the production file was exported as a `// sanitized` husk (2026-09-01 audit).
// Restores the ~24-member surface consumed by StaadWriter.cs, csv_exporters.cs and
// pipeline.cs (DesignSpreadsheetPipeline / TotalReactionCalculator). Field types are
// inferred from the consumers; Prepare() throws because the group/spring/trench/load
// data it built came from proprietary inputs. Re-hydrate from the production environment;
// keep C# 5 only.
using System;
using System.Collections.Generic;

public class StaadInputBuilder
{
	public string root;
	public string modelName;

	public SubgradeType bound;
	public ModelArchetype archetype;
	public bool rigid;

	public GeometryResult geom;

	public Dictionary<string, Group> elemGroups = new Dictionary<string, Group>();
	public Dictionary<string, Group> loadGroups = new Dictionary<string, Group>();
	public Dictionary<string, Group> thicknessGroups = new Dictionary<string, Group>();
	public Dictionary<string, Group> soilSpringsGroups = new Dictionary<string, Group>();
	public Dictionary<string, Group> rebarGroups = new Dictionary<string, Group>();

	public SoilSpringRegion baseMatSprings;   // sanitized: Kh/Kv per LB/BE/UB bound
	public SoilSpringRegion tunnelSprings;

	// R1 -> [_DL_Roofing System, ...]
	public Dictionary<string, List<string>> loadData = new Dictionary<string, List<string>>();
	public Dictionary<string, string> referenceLoadTitles = new Dictionary<string, string>();
	public List<TorsionLoad> TorsionLoads = new List<TorsionLoad>();

	public List<TrenchGeometry> trenches = new List<TrenchGeometry>();
	public List<int> allIds_PSDS = new List<int>();
	public Dictionary<string, Dictionary<int, double>> trenchDispDicts = new Dictionary<string, Dictionary<int, double>>();

	// Input file paths (sanitized). modalDampingPath / rsaLoadsPath are prefixes; the writer appends bound + ".txt".
	public string loadCasesPath;
	public string tornadoLoadCasesPath;
	public string psdsStaticLoadCasesPath;
	public string psdsCompressionPath;
	public string modalDampingPath;
	public string rsaLoadsPath;

	public StaadInputBuilder(string root, string modelName)
	{
		this.root = root;
		this.modelName = modelName;
	}

	public void Prepare()
	{
		throw new NotImplementedException("StaadInputBuilder.Prepare is a sanitized stub; re-hydrate staadInputBuilder.cs.");
	}
}
