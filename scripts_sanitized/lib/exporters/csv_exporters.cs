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

public class MFormat
{
	private string _outDir;
	private List<string> _groups;
	public Dictionary<int, Dictionary<int, float[]>> CenterStresses;

	public MFormat(string outDir, List<string> groups)
	{
		_outDir = outDir;
		_groups = groups;
		Directory.CreateDirectory(_outDir);

		foreach (string groupName in _groups)
		{
			string csvPath = Path.Combine(outDir, groupName + ".csv");
			using (var writer = new StreamWriter(csvPath))
			{
				writer.WriteLine("Element,Source_File_Index,LC,SQX',SQY',MX',MY',MXY',SX',SY',SXY'");
			}
		}
	}

	// The original "M-like" writer. writes center stresses
	public void Write(StaadModel model, int modelId)
	{
		//var pb = new ProgressBar(_groups.Count, title:string.Format("Writing csv for {0}", modelId));
		foreach (string groupName in _groups)
		{
			//pb.Tick();
			Console.WriteLine("Writing csv for {0}", groupName);

			List<int> elementIds;
			if (!model.Groups.TryGetValue(groupName, out elementIds))
			{
				Console.WriteLine(string.Format("WARNING: Group {0} not present in dictionary",groupName));
				continue;
			}
			string csvPath = Path.Combine(_outDir, groupName + ".csv");

			using (var writer = new StreamWriter(csvPath, true)) //true for append mode
			{
				foreach (var kvp in model.CenterStresses)
				{
					int LC = kvp.Key;
					var lcDict = kvp.Value;
					//Console.WriteLine("Checking LC {0}", LC);
					foreach (int elemId in elementIds)
					{
						// if (LC == 201) { Console.WriteLine("Checking element {0}", elemId);}
						// This will error if we're missing a specific element for a specific lc
						float[] s;
						if (lcDict.TryGetValue(elemId, out s))
						{
							writer.WriteLine(string.Join(",",
							elemId,
							modelId,
							LC,
							s[(int)SC.SQx],
							s[(int)SC.SQy],
							s[(int)SC.Mx],
							s[(int)SC.My],
							s[(int)SC.Mxy],
							s[(int)SC.Sx],
							s[(int)SC.Sy],
							s[(int)SC.Sxy]
						));
						}
						else
						{
							// Write blanks for now for missing data
							// writer.WriteLine(string.Join(",",elemId,modelId,LC,"","","","","","","",""));
						}
					}
				}
			}
		}
		//pb.Finish();
	}

	// Same as above but takes a list of stress records instead of the model
	public void WriteStressList(Dictionary<string, List<int>> groups, List<StressRecord> stressList,
	ForceUnit force, LengthUnit length)
	{
		CenterStresses = new Dictionary<int, Dictionary<int, float[]>>();
		// Build center stress dict, no foreach in hot loop
		for (int i = 0; i < stressList.Count; i++)
		{
			StressRecord rec = stressList[i];
			int LC = rec.LC;
			int nid = rec.node;
			if (rec.node != -1)
			{
				continue; // only center stresses
			}
			Dictionary<int, float[]> lcdict;
			if (!CenterStresses.TryGetValue(LC, out lcdict))
			{
				lcdict = new Dictionary<int, float[]>();
				CenterStresses[LC] = lcdict;
			}
			// Convert to target units
			lcdict[rec.elemID] = rec.StressView(force, length);
		}

		//var pb = new ProgressBar(_groups.Count, title:string.Format("Writing csv for {0}", modelId));
		foreach (string groupName in _groups)
		{
			//pb.Tick();
			// Console.WriteLine("Writing csv for {0}", groupName);

			List<int> elementIds;
			if (!groups.TryGetValue(groupName, out elementIds))
			{
				Console.WriteLine(string.Format("WARNING: Group {0} not present in dictionary",groupName));
				continue;
			}
			string csvPath = Path.Combine(_outDir, groupName + ".csv");

			using (var writer = new StreamWriter(csvPath, true)) //true for append mode
			{
				foreach (var kvp in CenterStresses)
				{
					int LC = kvp.Key;
					var lcDict = kvp.Value;
					//Console.WriteLine("Checking LC {0}", LC);
					foreach (int elemId in elementIds)
					{
						// if (LC == 201) { Console.WriteLine("Checking element {0}", elemId);}
						// This will error if we're missing a specific element for a specific lc
						float[] s;
						if (lcDict.TryGetValue(elemId, out s))
						{
							// NaN guard, replace with all zeros
							if (float.IsNaN(s[0])) {s = new float[s.Length];}

							writer.WriteLine(string.Join(",",
							elemId,
							1,				//Hardcode modelId 1
							LC,
							s[(int)SC.SQx],
							s[(int)SC.SQy],
							s[(int)SC.Mx],
							s[(int)SC.My],
							s[(int)SC.Mxy],
							s[(int)SC.Sx],
							s[(int)SC.Sy],
							s[(int)SC.Sxy]
						));
						}
						else
						{
							// Write blanks for now for missing data
							// writer.WriteLine(string.Join(",",elemId,modelId,LC,"","","","","","","",""));
						}
					}
				}
			}
		}
		//pb.Finish();
	}


	// Writes the joint stresses with the old averaging method
	//	This is the same data as the viewer uses
	//	This has the slab/wall intersection issue (averages across disparate coordinate systems)
	// 	Requires pre-computing the JointStress dictionary ($model.BuildStressDictionaries())
	public void WriteJoints(StaadModel model, int modelId)
	{
		//var pb = new ProgressBar(_groups.Count, title:string.Format("Writing csv for {0}", modelId));
		foreach (string groupName in _groups)
		{
			//pb.Tick();
			Console.WriteLine("Writing joint csv for {0}", groupName);
			List<int> elementIds = model.Groups[groupName];
			string csvPath = Path.Combine(_outDir, groupName + ".csv");

			using (var writer = new StreamWriter(csvPath, true)) //true for append mode
			{
				foreach (var kvp in model.JointStresses)
				{
					int LC = kvp.Key;
					var lcDict = kvp.Value;
					foreach (int elemId in elementIds)
					{
						var nodes = model.ElementDict[elemId].n;
						foreach (var n in nodes)
						{
							if (n == null) { continue; }
							int nid = n.id;
							float[] s;
							if (lcDict.TryGetValue(nid, out s))
							{
								writer.WriteLine(string.Join(",",
								elemId,
								modelId,
								LC,
								s[(int)SC.SQx],
								s[(int)SC.SQy],
								s[(int)SC.Mx],
								s[(int)SC.My],
								s[(int)SC.Mxy],
								s[(int)SC.Sx],
								s[(int)SC.Sy],
								s[(int)SC.Sxy]
							));
							}
							else
							{
								// Write blanks for now for missing data
								// writer.WriteLine(string.Join(",",elemId,modelId,LC,"","","","","","","",""));
							}
						}
					}
				}
			}
		}
		//pb.Finish();
	}

	// Writes the raw joint stresses without any averaging
	public void WriteRawJoints(StaadModel model, int modelId)
	{
		//var pb = new ProgressBar(_groups.Count, title:string.Format("Writing csv for {0}", modelId));
		foreach (string groupName in _groups)
		{
			//pb.Tick();
			Console.WriteLine("Writing joint csv for {0}", groupName);
			List<int> elementIds = model.Groups[groupName];
			string csvPath = Path.Combine(_outDir, groupName + ".csv");

			// Check if group data present in stress data
			int firstElemId = elementIds[0];
			var firstLcDict = model.ElemStressDict.Values.First();
			if (!firstLcDict.ContainsKey(firstElemId)) continue;

			using (var writer = new StreamWriter(csvPath, true)) //true for append mode
			{
				foreach (var kvp in model.ElemStressDict)
				{
					int LC = kvp.Key;
					var lcDict = kvp.Value;
					foreach (int elemId in elementIds)
					{

						float[] stresses;
						if (lcDict.TryGetValue(elemId, out stresses))
						{
							int nSlots = stresses.Length / 8; //nNodes + 1
							for (int i = 0; i < nSlots; i++)
							{
								float[] s = new float[8];
								Array.Copy(stresses, i * 8, s, 0, 8);
								var elem = model.ElementDict[elemId];

								writer.WriteLine(string.Join(",",
								elemId,
								modelId,
								LC,
								s[(int)SC.SQx],
								s[(int)SC.SQy],
								s[(int)SC.Mx],
								s[(int)SC.My],
								s[(int)SC.Mxy],
								s[(int)SC.Sx],
								s[(int)SC.Sy],
								s[(int)SC.Sxy],
								" ",
								(i == 0 ? -1 : elem.n[i - 1].id) //nid
								));
							}
						}
						else
						{
							// Write blanks for now for missing data
							// writer.WriteLine(string.Join(",",elemId,modelId,LC,"","","","","","","",""));
						}
					}
				}
			}
		}
	}

	// Averages nodes coincident nodes on a per group basis (avoids slab/wall issues)
	// 	Assigns the resultant nodal stress to the first element that sees the node (avoids duplicate data)
	//	Includes center and joint stresses (so most elements will have two stresses, one center and one joint)
	//	Requires pre-computing of the model's "ElementStressDictionary" ($model.BuildElemStressDictionary())

	public void WriteAvgJoints(StaadModel model, int modelId)
	{
		bool flag = false;
		bool flag2 = false;
		int nidCheck = -100;
		int lcCheck = -100;
		int elemCheck = -100;
		//var pb = new ProgressBar(_groups.Count, title:string.Format("Writing csv for {0}", modelId));
		foreach (string groupName in _groups)
		{
			//pb.Tick();
			Console.WriteLine("Writing joint csv for {0}", groupName);
			List<int> elementIds = model.Groups[groupName];
			string csvPath = Path.Combine(_outDir, groupName + ".csv");

			// Check if group data present in stress data
			int firstElemId = elementIds[0];
			var firstLcDict = model.ElemStressDict.Values.First();
			var firstKey = firstLcDict.Keys.First();

			// Temporarily supressing this for PSDS
			// if (!elementIds.Contains(firstKey)) continue;


			// if (!firstLcDict.ContainsKey(firstElemId)) continue;

			using (var writer = new StreamWriter(csvPath, true)) //true for append mode
			{
				foreach (var kvp in model.ElemStressDict)
				{
					int LC = kvp.Key;
					var lcDict = kvp.Value;

					var sums = new Dictionary<int, float[]>();
					var counts = new Dictionary<int, int>();
					var nodeElems = new Dictionary<int, int>(); // nid -> any one elemId that touched the node


					foreach (int elemId in elementIds)
					{
						flag = ((elemId == elemCheck) && (LC == lcCheck));
						Debug.Printf(String.Format("Elem id = {0}", elemId), flag);
						float[] stresses;
						if (!lcDict.TryGetValue(elemId, out stresses)) continue;

						// Debug.Printf(String.Format("Stresses = {0}",stresses),flag);

						Element elem = model.ElementDict[elemId];
						int nNodes = elem.nNodes;
						Debug.Printf(String.Format("nNodes = {0}", nNodes), flag);

						// nNodes + 1 since center stresses take the zero index
						for (int i = 0; i < nNodes + 1; i++)
						{
							Debug.Printf(String.Format("i = {0}", i), flag);
							int baseIdx = i * 8;
							if (float.IsNaN(stresses[baseIdx])) continue;

							Debug.Printf(String.Format("nNodes = {0}", nNodes), flag);

							if (i == 0)
							{

								// Immediately write center stresses
								float[] s = new float[8];
								Array.Copy(stresses, i * 8, s, 0, 8);

								writer.WriteLine(string.Join(",",
								elemId, // elemId
								modelId,
								LC,
								s[(int)SC.SQx],
								s[(int)SC.SQy],
								s[(int)SC.Mx],
								s[(int)SC.My],
								s[(int)SC.Mxy],
								s[(int)SC.Sx],
								s[(int)SC.Sy],
								s[(int)SC.Sxy],
								" ",
								-1
								));

								// Debug.Printf(String.Format("Wrote stresses for i = 0"),flag);
							}
							else
							{
								int nid = elem.n[i - 1].id;
								flag2 = nid == nidCheck && LC == lcCheck;
								Debug.Printf(String.Format("--------Assigning nid = {0} to sums (i={1}, LC={2})", nid, i, LC), flag2);

								float[] sum;
								if (!sums.TryGetValue(nid, out sum))
								{
									sum = new float[8];
									sums[nid] = sum;
									counts[nid] = 0;
									nodeElems[nid] = elemId;
								}

								for (int j = 0; j < 8; j++) sum[j] += stresses[baseIdx + j];
								counts[nid]++;
							}
						}
					}

					// Divide and write
					foreach (var nidKvp in sums)
					{
						int nid = nidKvp.Key;
						flag2 = nid == nidCheck && LC == lcCheck;

						float[] s = nidKvp.Value;
						int c = counts[nid];
						Debug.Printf(String.Format("--------writing node = {0} (count = {1}, Mysum = {2}", nid, c, s[(int)SC.My]), flag2);
						for (int j = 0; j < 8; j++) s[j] /= c;

						writer.WriteLine(string.Join(",",
						nodeElems[nid], // elemId
						modelId,
						LC,
						s[(int)SC.SQx],
						s[(int)SC.SQy],
						s[(int)SC.Mx],
						s[(int)SC.My],
						s[(int)SC.Mxy],
						s[(int)SC.Sx],
						s[(int)SC.Sy],
						s[(int)SC.Sxy],
						" ",
						nid
						));
					}
				}
			}
		}
	}

}

public static class CsvHelpers
{
	public static void WriteRawStress(StaadModel model, string csvPath)
	{
		using (var writer = new StreamWriter(csvPath, true)) //true for append mode
		{
			var stressList = model.StressList;
			var totalStresses = stressList.Count;
			var totalTicks = 100;
			var interval = totalStresses / totalTicks;

			var pb = new ProgressBar(totalTicks, title: "Writing stresses to CSV");
			writer.WriteLine("Element,LC,JointId,SQX,SQY,MX,MY,MXY,SX,SY,SXY");
			for (int i = 0; i < totalStresses; i++)
			{
				if (i % interval == 0)
				{
					pb.Tick();
				}
				var stress = model.StressList[i];
				var s = stress.Sf;
				writer.WriteLine(string.Join(",",
					stress.elemID,
					stress.LC,
					stress.node,
					s[(int)SC.SQx],
					s[(int)SC.SQy],
					s[(int)SC.Mx],
					s[(int)SC.My],
					s[(int)SC.Mxy],
					s[(int)SC.Sx],
					s[(int)SC.Sy],
					s[(int)SC.Sxy]
				));
			}
			pb.Finish();
		}
	}

	public static void WriteInfluenceAreas(StaadInputBuilder config, string csvPath)
	{
		// sanitized
	}

	public static void WriteStressDict(Dictionary<int, Dictionary<int, float[]>> stressDict, string csvPath)
	{
		using (var writer = new StreamWriter(csvPath, true)) //true for append mode
		{
			var totalTicks = stressDict.Count; // Number of LCs

			var pb = new ProgressBar(totalTicks, title: "Writing stresses to CSV");
			writer.WriteLine("Element,LC,JointId,SQX,SQY,MX,MY,MXY,SX,SY,SXY");
			foreach (var lcDict in stressDict)
			{
				var LC = lcDict.Key;
				if (LC < 0) continue;

				pb.Tick();

				foreach (var kvp in lcDict.Value)
				{
					var nid = kvp.Key;
					var s = kvp.Value;

					writer.WriteLine(string.Join(",",
						"N/A",
						LC,
						nid,
						s[(int)SC.SQx],
						s[(int)SC.SQy],
						s[(int)SC.Mx],
						s[(int)SC.My],
						s[(int)SC.Mxy],
						s[(int)SC.Sx],
						s[(int)SC.Sy],
						s[(int)SC.Sxy],
						s[8], // dx
						s[9], // dy
						s[10], // dz
						s[11], // rx
						s[12], // ry
						s[13] // rz
					));
				}
			}
			pb.Finish();
		}
	}

	public static void SplitLargeCsvFiles(string folderPath)
	{
		const long maxBytes = 75L * 1024 * 1024;

		string[] files = Directory.GetFiles(folderPath, "*.csv");
		for (int f = 0; f < files.Length; f++)
		{
			string path = files[f];
			long size = new FileInfo(path).Length;
			if (size <= maxBytes) continue;

			int nParts = (int)((size + maxBytes - 1) / maxBytes);

			string header;
			long dataBytes;
			int totalDataLines = 0;
			using (var reader = new StreamReader(path))
			{
				header = reader.ReadLine();
				if (header == null) continue;

				long headerBytes = Encoding.UTF8.GetByteCount(header) + Environment.NewLine.Length;
				dataBytes = size - headerBytes;

				string countLine;
				while ((countLine = reader.ReadLine()) != null)
				{
					totalDataLines++;
				}
			}

			if (totalDataLines == 0) continue;

			int linesPerPart = (totalDataLines + nParts - 1) / nParts;

			string dir = Path.GetDirectoryName(path);
			string baseName = Path.GetFileNameWithoutExtension(path);
			string ext = Path.GetExtension(path);

			using (var reader = new StreamReader(path))
			{
				reader.ReadLine(); //skip header

				for (int part = 1; part <= nParts; part++)
				{
					string outName = string.Format("{0}_{1}of{2}{3}", baseName, part, nParts, ext);
					string outPath = Path.Combine(dir, outName);

					using (var writer = new StreamWriter(outPath))
					{
						writer.WriteLine(header);

						int linesThisPart = (part == nParts)
							? totalDataLines - linesPerPart * (nParts - 1)
							: linesPerPart;

						for (int i = 0; i < linesThisPart; i++)
						{
							string line = reader.ReadLine();
							if (line == null) break;
							writer.WriteLine(line);
						}
					}
				}
			}
			File.Delete(path);
		}
	}
}

public class CsvMerger
{
	public static void Merge(string[] folders, string outDir)
	{
		Directory.CreateDirectory(outDir);
		var names = Directory.GetFiles(folders[0], "*.csv").Select(Path.GetFileName);
		foreach (var name in names)
		{
			using (var w = new StreamWriter(Path.Combine(outDir, name)))
			{
				for (int i = 0; i < folders.Length; i++)
				{
					using (var r = new StreamReader(Path.Combine(folders[i], name)))
					{
						if (i > 0) { r.ReadLine(); }
						string line;
						while ((line = r.ReadLine()) != null) { w.WriteLine(line); }
					}
				}
			}
		}
	}
}

public class ColumnGrouper
{
	public Dictionary<string, XZBucket> Buckets;
	public Dictionary<string, ColumnStack> Stacks;

	private float _gridSize = 12; //threshold for grouping XZ
	private Dictionary<int, Member> _members;
	private List<MemForce> _forceList;
	private Dictionary<int, Dictionary<int, Dictionary<int, float[]>>> _forces;

	//For each member in the column list
	//Get XZ from n[0] (maybe check orientation?)
	public ColumnGrouper(Dictionary<int, Member> Members, List<MemForce> forceList)
	{
		_members = Members;
		_forceList = forceList;
		BuildForceDict();
	}

	public void GroupColumns(List<int> columnMemberIds)
	{
		Buckets = new Dictionary<string, XZBucket>();
		for (int i = 0; i < columnMemberIds.Count; i++)
		{
			Member mem = _members[columnMemberIds[i]];
			float x = mem.n[0].xyz.X;
			float z = mem.n[0].xyz.Z;
			string key = MakeKey(x, z);

			XZBucket bucket;
			if (!Buckets.TryGetValue(key, out bucket))
			{
				bucket = new XZBucket();
				bucket.X = x;
				bucket.Z = z;
				bucket.MemberIds = new List<int>();
				Buckets[key] = bucket;
			}
			bucket.MemberIds.Add(columnMemberIds[i]);
		}

		// Collect unique XZ coordinates and sort
		var xs = new List<float>();
		var zs = new List<float>();
		foreach (var bucket in Buckets.Values)
		{
			if (!ListContains(xs, bucket.X)) xs.Add(bucket.X);
			if (!ListContains(zs, bucket.Z)) zs.Add(bucket.Z);
		}
		xs.Sort();
		zs.Sort();


		// Build Stacks with grid labels
		Stacks = new Dictionary<string, ColumnStack>();
		foreach (var bucket in Buckets.Values)
		{
			int row = FindIndex(xs, bucket.X);
			int col = FindIndex(zs, bucket.Z);

			//A + 1 = B if cast as char
			string label = (char)('B' + col) + (row + 2).ToString();

			var stack = new ColumnStack();
			stack.X = bucket.X;
			stack.Z = bucket.Z;
			stack.GridRow = row;
			stack.GridCol = col;
			stack.MemberIds = bucket.MemberIds;
			stack.PopulateForces(_forces);
			stack.PopulateMembers(_members);

			Stacks[label] = stack;
		}
		Console.WriteLine("Buckets count: {0}, Stacks count: {1}", Buckets.Count, Stacks.Count);
	}

	private void BuildForceDict()
	{
		//[memberId][lc][nodeId]
		var dict = new Dictionary<int, Dictionary<int, Dictionary<int, float[]>>>();

		for (int i = 0; i < _forceList.Count; i++)
		{
			MemForce rec = _forceList[i];

			Dictionary<int, Dictionary<int, float[]>> lcDict;
			if (!dict.TryGetValue(rec.memID, out lcDict))
			{
				lcDict = new Dictionary<int, Dictionary<int, float[]>>();
				dict[rec.memID] = lcDict;
			}

			Dictionary<int, float[]> nodeDict;
			if (!lcDict.TryGetValue(rec.LC, out nodeDict))
			{
				nodeDict = new Dictionary<int, float[]>();
				lcDict[rec.LC] = nodeDict;
			}

			nodeDict[rec.nodeID] = rec.Ff;
		}
		_forces = dict;
	}

	private bool ListContains(List<float> list, float val)
	{
		for (int i = 0; i < list.Count; i++)
		{
			if (Math.Abs(list[i] - val) < _gridSize) return true;
		}
		return false;
	}

	private int FindIndex(List<float> list, float val)
	{
		for (int i = 0; i < list.Count; i++)
		{
			if (Math.Abs(list[i] - val) < _gridSize) return i;
		}
		return -1;
	}

	private string MakeKey(float x, float z)
	{
		int gx = (int)Math.Round(x / _gridSize);
		int gz = (int)Math.Round(z / _gridSize);
		return gx + "," + gz;//composte string key
	}

	public class XZBucket
	{
		public float X;
		public float Z;
		public List<int> MemberIds;
	}

	public void SectionCutColumnsSF(string path)
	{
		// Config
		Dictionary<char, float> baseOffsetDict; // sanitized

		StringBuilder sb = new StringBuilder();
		sb.AppendLine("ColumnID, LoadCase#, TopOffset, Axial (T+), Vy, Vz, Torsion, My, Mz, " +
			"BotOffset, Axial (T+), Vy, Vz, Torsion, My, Mz");

		foreach (var kvp in Stacks)
		{
			// TODO: Should these be in ft or inches?
			string name = kvp.Key;
			var column = kvp.Value;
			char letter = name[0];
			float topOffset = 1f; // 1 ft from top (2ft slab)
			float baseOffset = baseOffsetDict[letter];
			var loadCombos = _forces[column.MemberIds[0]].Keys; // load combos

			foreach (int lc in loadCombos)
			{
				float[] fTop = column.GetForcesFromTop(topOffset, lc); // 1 ft from top (2ft slab)
				float[] fBot = column.GetForcesFromBot(baseOffset, lc); //variable offset from bottom, see config
				string p1 = string.Format("{0},{1},{2},", name, lc, topOffset);
				string p2 = string.Format("{0},{1},{2},{3},{4},{5},{6},", fTop[0], fTop[1], fTop[2], fTop[3], fTop[4], fTop[5], baseOffset);
				string p3 = string.Format("{0},{1},{2},{3},{4},{5}", fBot[0], fBot[1], fBot[2], fBot[3], fBot[4], fBot[5]);
				sb.AppendLine(p1 + p2 + p3);
			}
		}
		File.WriteAllText(path, sb.ToString());
	}
}
