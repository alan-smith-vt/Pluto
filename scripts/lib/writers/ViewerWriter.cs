using System;
using System.IO;
using System.Collections;
using System.Collections.Generic;
//using System.Numerics;
using System.Text;


//	--- New Writer for the single binary file for all stress data ---
//	Member data and forces are excluded from the scope of this file
//  Group data are excluded from the scope of this file
//  One model is written at a time, then we merge multiple model files
//	Note: 	this system only works up to <4GB. Need uint64 offsets past that
//			and need to switch to file.slice on js side due to browser memory limits
public static class ViewerWriter{
	
	// ==================================================================
	//	Main entry point - writes a single-model viewer binary file
	// ==================================================================
	
	public static void Write(
		string outputPath,
		string modelName,
		Dictionary<int, Node> nodes,
		Dictionary< int, Element> elements,
		Dictionary<int, Dictionary<int, float[]>> jointStresses,
		// jointStresses[lc][nid] -> float[14] (8 stress + 6 disp)
		Dictionary<int, string> loadCaseNames
	) {
		
		// Build id-to-index maps
		Dictionary<int, int> nodeIdToIndex;
		List<int> sortedNodeIds;
		BuildIndexMap(nodes.Keys, out nodeIdToIndex, out sortedNodeIds);
		
		Dictionary<int, int> elemIdToIndex;
		List<int> sortedElemIds;
		BuildIndexMap(elements.Keys, out elemIdToIndex, out sortedElemIds);
		
		int jointComponents = 14; 	// 8 stress + 6 disp
		
		// Compute envelopes across all real load cases
		Dictionary<int, float[]> jointMin, jointMax, jointAbsMax;
		ComputeEnvelopes(jointStresses, sortedNodeIds, loadCaseNames, jointComponents,
			out jointMin, out jointMax, out jointAbsMax);
			
		// Build LC list: real LCs + envelope LCs
		List<int> allLCs = new List<int>(loadCaseNames.Keys);
		const int ENV_MIN = -1;
		const int ENV_MAX = -2;
		const int ENV_ABSMAX = -3;
		
		loadCaseNames[ENV_MIN] = "ENV_MIN";
		loadCaseNames[ENV_MAX] = "ENV_MAX";
		loadCaseNames[ENV_ABSMAX] = "ENV_ABSMAX";
		
		// Add envelope data into stress dicts
		jointStresses[ENV_MIN] = jointMin;
		jointStresses[ENV_MAX] = jointMax;
		jointStresses[ENV_ABSMAX] = jointAbsMax;
		
		int nNodes = nodes.Count;
		int nElements = elements.Count;
		int nTotalLC = loadCaseNames.Count;
		
		ModelMeta model = new ModelMeta(); //class for model meta data
		model.Name = modelName;
		model.LoadCaseNames = loadCaseNames;
		
		// Console.Error.Write(string.Format("loadCasesNames: {0}", loadCasesNames));
		model.StressStartIndex = 0;
		
		// Header layout:
		// 0: 	magic			(uint32)
		// 4: 	version			(uint32)
		// 8: 	headerSize		(uint32)
		// 12:	nNodes			(uint32)
		// 16:	nElements		(uint32)
		// 20:	nModels			(uint32)
		// 24:	nTotalLC		(uint32)
		// 28:	jointComponents	(uint32)
		// 32:	metaOffset		(uint32)
		// 36:	metaLength		(uint32)
		// 40:	nodesOffset		(uint32)
		// 44:	elemsOffset		(uint32)
		// 48:	jointStressOffset	(uint32)
		// = 52 bytes total
		uint headerSize = 52;
		
		using (BinaryWriter writer = new BinaryWriter(File.Create(outputPath))){
			// Reserve header space
			long headerStart = writer.BaseStream.Position;
			for (int i = 0; i < headerSize / 4; i++) writer.Write((uint)0);
			
			// --- JSON Metadata ---
			uint metaOffset = (uint)writer.BaseStream.Position;
			string json = BuildMetaJson(nodeIdToIndex, elemIdToIndex,
				new List<ModelMeta> {model});
			byte[] jsonBytes = System.Text.Encoding.UTF8.GetBytes(json);
			uint metaLength = (uint)jsonBytes.Length;
			writer.Write(jsonBytes);
			
			// Pad to 4-byte boundary for js reading
			int padding = (4 - (int)(writer.BaseStream.Position % 4)) % 4;
			for (int i = 0; i < padding; i++) writer.Write((byte)0);
			
			// --- Nodes (float32 x,y,z per node) ---
			uint nodesOffset = (uint)writer.BaseStream.Position;
			WriteNodes(writer, nodes, sortedNodeIds);
			
			// --- Elements (uint8 nodeCount + 4x uint32 nodeIndex) ---
			uint elemsOffset = (uint)writer.BaseStream.Position;
			WriteElements(writer, elements, sortedElemIds, nodeIdToIndex);
			
			// Pad to 4-byte boundary for js reading
			padding = (4 - (int)(writer.BaseStream.Position % 4)) % 4;
			for (int i = 0; i < padding; i++) writer.Write((byte)0);
			
			// --- Joint Stresses (14 floats per node per LC) ---
			ProgressBar pb = new ProgressBar(loadCaseNames.Count, title: "Writing Stress Data for Viewer");
			uint jointStressOffset = (uint)writer.BaseStream.Position;
			WriteStresses(writer, jointStresses, loadCaseNames, sortedNodeIds, jointComponents, pb);
			pb.Finish();
			
			
			// --- Patch Header ---
			writer.BaseStream.Seek(headerStart, SeekOrigin.Begin);
			writer.Write((uint)0xFEA12345);			//  0: 	magic (placeholder for now)
			writer.Write((uint)1);					//  4: 	version
			writer.Write(headerSize);				//  8: 	headerSize
			writer.Write((uint)nNodes);				// 12:	nNodes
			writer.Write((uint)nElements);			// 16:	nElements
			writer.Write((uint)1);					// 20:	nModels
			writer.Write((uint)nTotalLC);			// 24:	nTotalLC
			writer.Write((uint)jointComponents);	// 28:	jointComponents
			writer.Write(metaOffset);				// 32:	metaOffset
			writer.Write(metaLength);				// 36:	metaLength
			writer.Write(nodesOffset);				// 40:	nodesOffset
			writer.Write(elemsOffset);				// 44:	elemsOffset
			writer.Write(jointStressOffset);		// 48:	jointStressOffset
		}
	}
	
	public static void WriteGeometryOnly(
			string outputPath,
		string modelName,
		Dictionary<int, Node> nodes,
		Dictionary< int, Element> elements
	) {
		
		// Build id-to-index maps
		Dictionary<int, int> nodeIdToIndex;
		List<int> sortedNodeIds;
		BuildIndexMap(nodes.Keys, out nodeIdToIndex, out sortedNodeIds);
		
		Dictionary<int, int> elemIdToIndex;
		List<int> sortedElemIds;
		BuildIndexMap(elements.Keys, out elemIdToIndex, out sortedElemIds);
		
		int jointComponents = 14; 	// 8 stress + 6 disp
			
		// Empty load case dictionary
		Dictionary<int, string> loadCaseNames = new Dictionary<int, string>();
		
		int nNodes = nodes.Count;
		int nElements = elements.Count;
		int nTotalLC = 0;
		
		ModelMeta model = new ModelMeta(); //class for model meta data
		model.Name = modelName;
		model.LoadCaseNames = loadCaseNames;
		
		// Console.Error.Write(string.Format("loadCasesNames: {0}", loadCasesNames));
		model.StressStartIndex = 0;
		
		// Header layout:
		// 0: 	magic			(uint32)
		// 4: 	version			(uint32)
		// 8: 	headerSize		(uint32)
		// 12:	nNodes			(uint32)
		// 16:	nElements		(uint32)
		// 20:	nModels			(uint32)
		// 24:	nTotalLC		(uint32)
		// 28:	jointComponents	(uint32)
		// 32:	metaOffset		(uint32)
		// 36:	metaLength		(uint32)
		// 40:	nodesOffset		(uint32)
		// 44:	elemsOffset		(uint32)
		// 48:	jointStressOffset	(uint32)
		// = 52 bytes total
		uint headerSize = 52;
		
		using (BinaryWriter writer = new BinaryWriter(File.Create(outputPath))){
			// Reserve header space
			long headerStart = writer.BaseStream.Position;
			for (int i = 0; i < headerSize / 4; i++) writer.Write((uint)0);
			
			// --- JSON Metadata ---
			uint metaOffset = (uint)writer.BaseStream.Position;
			string json = BuildMetaJson(nodeIdToIndex, elemIdToIndex,
				new List<ModelMeta> {model});
			byte[] jsonBytes = System.Text.Encoding.UTF8.GetBytes(json);
			uint metaLength = (uint)jsonBytes.Length;
			writer.Write(jsonBytes);
			
			// Pad to 4-byte boundary for js reading
			int padding = (4 - (int)(writer.BaseStream.Position % 4)) % 4;
			for (int i = 0; i < padding; i++) writer.Write((byte)0);
			
			// --- Nodes (float32 x,y,z per node) ---
			uint nodesOffset = (uint)writer.BaseStream.Position;
			WriteNodes(writer, nodes, sortedNodeIds);
			
			// --- Elements (uint8 nodeCount + 4x uint32 nodeIndex) ---
			uint elemsOffset = (uint)writer.BaseStream.Position;
			WriteElements(writer, elements, sortedElemIds, nodeIdToIndex);
			
			// Pad to 4-byte boundary for js reading
			padding = (4 - (int)(writer.BaseStream.Position % 4)) % 4;
			for (int i = 0; i < padding; i++) writer.Write((byte)0);
			
			// --- No joint stresses ---
			uint jointStressOffset = (uint)writer.BaseStream.Position;			
			
			// --- Patch Header ---
			writer.BaseStream.Seek(headerStart, SeekOrigin.Begin);
			writer.Write((uint)0xFEA12345);			//  0: 	magic (placeholder for now)
			writer.Write((uint)1);					//  4: 	version
			writer.Write(headerSize);				//  8: 	headerSize
			writer.Write((uint)nNodes);				// 12:	nNodes
			writer.Write((uint)nElements);			// 16:	nElements
			writer.Write((uint)1);					// 20:	nModels
			writer.Write((uint)nTotalLC);			// 24:	nTotalLC
			writer.Write((uint)jointComponents);	// 28:	jointComponents
			writer.Write(metaOffset);				// 32:	metaOffset
			writer.Write(metaLength);				// 36:	metaLength
			writer.Write(nodesOffset);				// 40:	nodesOffset
			writer.Write(elemsOffset);				// 44:	elemsOffset
			writer.Write(jointStressOffset);		// 48:	jointStressOffset (empty data)
		}
	}
	
	// ==================================================================
	//	Envelope Computation
	// ==================================================================
	
	private static void ComputeEnvelopes(
		Dictionary<int, Dictionary<int, float[]>> stresses,
		List<int> sortedIds,
		Dictionary<int, string> loadCaseNames,
		int components,
		out Dictionary<int, float[]> envMin,
		out Dictionary<int, float[]> envMax,
		out Dictionary<int, float[]> envAbsMax
	) {
		envMin = new Dictionary<int, float[]>();
		envMax = new Dictionary<int, float[]>();
		envAbsMax = new Dictionary<int, float[]>();
		
		// For each node
		foreach (int id in sortedIds) {
			float[] mins =		new float[components];
			float[] maxs =	 	new float[components];
			float[] absMaxs = 	new float[components];
			
			//for each stress component
			for (int c = 0; c < components; c++) {
				mins[c] = float.MaxValue; //positive inf
				maxs[c] = float.MinValue; //negative inf
				absMaxs[c] = 0;
			}
			
			foreach (int lc in loadCaseNames.Keys){
				float[] data = LookupStress(stresses, lc, id);
				if (data == null) continue;
				for (int c = 0; c < components; c++) {
					if (data[c] < mins[c]) mins[c] = data[c];
					if (data[c] > maxs[c]) maxs[c] = data[c];
					float abs = Math.Abs(data[c]);
					if (abs > absMaxs[c]) absMaxs[c] = abs;
				}
			}
			// Replace sentinel values with 0 if no data was found
			for (int c = 0; c < components; c++) {
				if (mins[c] == float.MaxValue) mins[c] = 0;
				if (maxs[c] == float.MinValue) maxs[c] = 0;
			}
			
			envMin[id] = mins;
			envMax[id] = maxs;
			envAbsMax[id] = absMaxs;
		}
	}
		
	// =========================================================
	// Binary section writers
	// =========================================================
	
	private static void WriteNodes(
		BinaryWriter writer,
		Dictionary<int, Node> nodes,
		List<int> sortedIds
	) {
		foreach (int nid in sortedIds) {
			Node n = nodes[nid];
			writer.Write((float)n.xyz.X);
			writer.Write((float)n.xyz.Y);
			writer.Write((float)n.xyz.Z);
		}
	}
	
	private static void WriteElements(
		BinaryWriter writer,
		Dictionary<int, Element> elements,
		List<int> sortedIds,
		Dictionary<int, int> nodeIdToIndex
	) {
		foreach (int eid in sortedIds) {
			Element e = elements[eid];			
			writer.Write((byte)e.nNodes);
			for (int i =0; i < 4; i++) {
				if (i < e.nNodes) {
					writer.Write((uint)nodeIdToIndex[e.n[i].id]);
				} else {
					writer.Write((uint)0);
				}
			}
		}
	}
	
	// Generic stress writer for both joint and center stresses
	
	private static void WriteStresses(
		BinaryWriter writer,
		Dictionary<int, Dictionary<int, float[]>> stresses,
		Dictionary<int, string> loadCaseNames,
		List<int> sortedIds,
		int components,
		ProgressBar pb
	) {
		//Do we really need to pass loadCaseNames? Model json builder just references it
		foreach (int lc in loadCaseNames.Keys){
			pb.Tick();
			float[] firstData = LookupStress(stresses, lc, sortedIds[0]);//Why?
			foreach (int id in sortedIds) {
				//for a given lc, for a given nid or eid
				float[] data = LookupStress(stresses, lc, id);
				
				if (data != null) {
					//Consider a node without displacements, need to store float.NaN
					//	when we make that original dictionary
					for (int c = 0; c < components; c++) writer.Write(data[c]);
				} else { 
					for (int c = 0; c < components; c++) writer.Write(float.NaN);
				}
			}
		}
	}
	
	// =========================================================
	// Index mapping
	// =========================================================
	
	private static void BuildIndexMap(
		ICollection<int> keys,
		out Dictionary<int, int> idToIndex,
		out List<int> sortedIds
	) {
		sortedIds = new List<int>(keys);
		sortedIds.Sort();
		idToIndex = new Dictionary<int, int>();
		for (int i = 0; i < sortedIds.Count; i++) {
			idToIndex[sortedIds[i]] = i;
		}
	}
	
	// =========================================================
	// Stress Lookup (null-safe nested dictionary access)
	// =========================================================
	
	private static float[] LookupStress(
		Dictionary<int, Dictionary<int, float[]>> stresses,
		int lc, int id
	) {
		if (!stresses.ContainsKey(lc)) return null;
		if (!stresses[lc].ContainsKey(id)) return null;
		return stresses[lc][id];
	}
	
	// =========================================================
	// JSON metadata builder
	// =========================================================
	
	private static string BuildMetaJson(
		Dictionary<int, int> nodeIdToIndex,
		Dictionary<int, int> elemIdToIndex,
		List<ModelMeta> models
	) {
		System.Text.StringBuilder sb = new System.Text.StringBuilder();
		sb.Append("{");
		
		AppendIdMap(sb, "nodeIdToIndex", nodeIdToIndex);
		sb.Append(",");
		AppendIdMap(sb, "elemIdToIndex", elemIdToIndex);
		sb.Append(",");
		
		sb.Append("\"models\":[");
		//While we only have one model now, make it generic to work
		// with merge later when there will be multiple models
		for (int m = 0; m < models.Count; m++){
			if (m > 0) sb.Append(",");
			ModelMeta mm = models[m];
			// Console.Error.Write(string.Format("model inside buildJson: {0}", mm));
			sb.Append("{");
			// Notepad++ not highlighting escape characters :(
			sb.Append(string.Format("\"name\":\"{0}\",",mm.Name));
			sb.Append(string.Format("\"stressStartIndex\":{0},", mm.StressStartIndex));
			sb.Append("\"loadCases\":[");
			int count = 0;
			foreach (var kvp in mm.LoadCaseNames)
			{
				if (count > 0) sb.Append(",");
				count++;
				sb.Append(string.Format("\"{0} - {1}\"", kvp.Key, kvp.Value));
			}
			sb.Append("]}");
		}
		sb.Append("]");
		
		sb.Append("}");
		return sb.ToString();
	}
	
	private static void AppendIdMap(
		System.Text.StringBuilder sb,
		string name,
		Dictionary<int, int> map
	) {
		sb.Append(string.Format("\"{0}\":{{", name));
		bool first = true;
		foreach (var kvp in map) {
			if (!first) sb.Append(",");
			sb.Append(string.Format("\"{0}\":{1}", kvp.Key, kvp.Value));
			first = false;
		}
		sb.Append("}");
	}


	// Debug helper for viewer data
	public static void PrintStressRange(Dictionary<int, Dictionary<int, float[]>> stresses, int components)
	{
		float[] mins = new float[components];
		float[] maxs = new float[components];
		for (int c = 0; c < components; c++) { mins[c] = float.MaxValue; maxs[c] = float.MinValue; }
		
		foreach (var lc in stresses.Values)
			foreach (var vals in lc.Values)
				for (int c = 0; c < components; c++)
				{
					if (float.IsNaN(vals[c])) continue;
					if (vals[c] < mins[c]) mins[c] = vals[c];
					if (vals[c] > maxs[c]) maxs[c] = vals[c];
				}
				
		for (int c = 0; c < components; c++)
			Console.WriteLine("C{0}: min={1:G6} max={2:G6}", c, mins[c], maxs[c]);
	}
}