using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

public class LoadCombo
{
	public readonly int Id;
	public readonly string Name;
	public readonly int[] Cases;
	public readonly float[] Factors;
	
	public LoadCombo(int id, string name, int[] cases, float[] factors)
	{
		this.Id = id;
		this.Name = name;
		this.Cases = cases;
		this.Factors = factors;
	}
}

public class ComboFileDto
{
	public int schemaVersion { get; set; }
	public List<ComboDto> combinations { get; set; }
}

public class ComboDto
{
	public int id { get; set; }
	public string name { get; set; }
	public string type { get; set; }//Optional, not used yet
	public List<TermDto> terms { get; set; }
}

public class TermDto
{
	public int @case { get; set; } // case is a protected c# name
	public float factor { get; set; }
}

public static class ComboLoader
{
	public static LoadCombo[] Load(string path)
	{
		string json = File.ReadAllText(path);
		
		JavaScriptSerializer ser = new JavaScriptSerializer();
		ComboFileDto file = ser.Deserialize<ComboFileDto>(json);
		
		if (file == null || file.combinations == null)
		{throw new Exception("No combinations found in JSON file");}
		
		LoadCombo[] result = new LoadCombo[file.combinations.Count];
		for (int i = 0; i < file.combinations.Count; i++)
		{
			ComboDto c = file.combinations[i];
			
			if (c.terms == null || c.terms.Count == 0)
			{throw new Exception("Combo has no terms");}
		
			int[] cases = new int[c.terms.Count];
			float[] factors = new float[c.terms.Count];
			for (int t = 0; t < c.terms.Count; t++)
			{
				cases[t] = c.terms[t].@case;
				factors[t] = c.terms[t].factor;
			}
			
			result[i] = new LoadCombo(c.id, c.name, cases, factors);
		}
		
		return result;
	}

	public static int[] UniqueCases(LoadCombo[] combos)
	{
		HashSet<int> seen = new HashSet<int>();
		foreach (LoadCombo combo in combos)
		{
			foreach(int caseId in combo.Cases)
			{
				seen.Add(caseId);
			}
		}
		
		int[] result = new int[seen.Count];
		seen.CopyTo(result);
		Array.Sort(result);
		return result;
	}
	
	public static LoadCombo[] ValidateCombos(LoadCombo[] combos, Dictionary<int, string> LoadCaseNames)
	{
		List<LoadCombo> kept = new List<LoadCombo>();
		HashSet<int> presentCases = new HashSet<int>();
		foreach (var kvp in LoadCaseNames)
		{
			presentCases.Add(kvp.Key);
		}
		
		foreach (LoadCombo combo in combos)
		{
			bool allPresent = true;
			foreach (int caseId in combo.Cases)
			{
				if (!presentCases.Contains(caseId))
				{
					allPresent = false;
					Console.WriteLine(string.Format("WARNING: Combo {0}: {1} contains case id {2} not present in model, skipping", 
						combo.Id, combo.Name, caseId));
					break;
				}
			}
			
			if (allPresent)
			{
				kept.Add(combo);
				// Mutate dictionary in place
				LoadCaseNames[combo.Id] = combo.Name;
			}
		}
		return kept.ToArray();
	}
}

public class LoadCombiner
{
	private const int NStress = 8;
	private const int NDisp = 6;
	private GeometryResult geom;
	
	// lc -> compositeKey -> 8-float values
	public readonly Dictionary<int, Dictionary<long, float[]>> byCaseStress;
	public readonly Dictionary<int, Dictionary<long, float[]>> byCaseDisp;
	
	public LoadCombiner(GeometryResult geom, List<StressRecord> stressList)
	{
		this.geom = geom; // used for thickness lookup to assign to combo records
		this.byCaseStress = BuildStressIndex(stressList);
		this.byCaseDisp = null;
	}
	
	public LoadCombiner(GeometryResult geom, List<StressRecord> stressList, List<Disp> dispList)
	{
		this.geom = geom; // used for thickness lookup to assign to combo records
		this.byCaseStress = BuildStressIndex(stressList);
		this.byCaseDisp = (dispList != null) ? BuildDispIndex(dispList) : null;
	}
	
	private static Dictionary<int, Dictionary<long, float[]>> BuildStressIndex(List<StressRecord> stressList)
	{
		Dictionary<int, Dictionary<long, float[]>> byCase = new Dictionary<int, Dictionary<long, float[]>>();
		for (int i = 0; i < stressList.Count; i++)
		{
			StressRecord r = stressList[i];
			Dictionary<long, float[]> caseMap;
			if (!byCase.TryGetValue(r.LC, out caseMap))
			{
				caseMap = new Dictionary<long, float[]>();
				byCase[r.LC] = caseMap;
			}
			caseMap[Key(r.elemID, r.node)] = r.Sf;
		}
		return byCase;
	}
	
	private static Dictionary<int, Dictionary<long, float[]>> BuildDispIndex(List<Disp> dispList)
	{
		Dictionary<int, Dictionary<long, float[]>> byCase = new Dictionary<int, Dictionary<long, float[]>>();
		for (int i = 0; i < dispList.Count; i++)
		{
			Disp r = dispList[i];
			Dictionary<long, float[]> caseMap;
			if (!byCase.TryGetValue(r.LC, out caseMap))
			{
				caseMap = new Dictionary<long, float[]>();
				byCase[r.LC] = caseMap;
			}
			caseMap[(long)r.node] = r.DR;
		}
		return byCase;
	}
	
	
	// Wrapper for doing all combos at once and appending to stressList
	public void AppendCombos(LoadCombo[] combos, List<StressRecord> stressList)
	{
		for (int c = 0; c < combos.Length; c++)
		{
			LoadCombo combo = combos[c];
			stressList.AddRange(ParseCombo(combo));
		}
	}
	
	// Parse a single combo and return the stress list just for that combo
	public List<StressRecord> ParseCombo(LoadCombo combo)
	{
		List<StressRecord> result = new List<StressRecord>();
		
		// Reused across combos; cleared each iteration
		Dictionary<long, float[]> acc = new Dictionary<long, float[]>();
		Dictionary<long, int[]> keyMeta = new Dictionary<long, int[]>(); // key -> {elemId, jointId}
		
		CombineInto(this.byCaseStress, combo, NStress, acc, keyMeta, "stress");

		// Emit one record per accumulated key.
		foreach (KeyValuePair<long, float[]> kv in acc)
		{
			int[] m = keyMeta[kv.Key];
			StressRecord rec = new StressRecord();
			rec.elemID = m[0];
			rec.node = m[1];
			rec.t = geom.ElementDict[m[0]].t;
			rec.LC = combo.Id;
			rec.Sf = kv.Value;
			result.Add(rec);
		}
		return result;
	}
	
	// Parse a single combo and return the disp list just for that combo
	public List<Disp> ParseComboDisp(LoadCombo combo)
	{
		List<Disp> result = new List<Disp>();
		
		// Reused across combos; cleared each iteration
		Dictionary<long, float[]> acc = new Dictionary<long, float[]>();
		Dictionary<long, int[]> keyMeta = new Dictionary<long, int[]>(); // key -> {elemId, jointId}
		
		CombineInto(this.byCaseDisp, combo, NDisp, acc, keyMeta, "displacement");

		// Emit one record per accumulated key.
		foreach (KeyValuePair<long, float[]> kv in acc)
		{
			int[] m = keyMeta[kv.Key];
			Disp rec = new Disp();
			rec.node = m[1];
			rec.LC = combo.Id;
			rec.DR = kv.Value;
			result.Add(rec);
		}
		return result;
	}
	
	//Shared accumulate to avoid duplicate code between stress and displacement parsers
	private static void CombineInto(
		Dictionary<int, Dictionary<long, float[]>> byCase,
		LoadCombo combo, int width,
		Dictionary<long, float[]> acc, Dictionary<long, int[]> keyMeta,
		string kindForError)
	{
		for (int t = 0; t < combo.Cases.Length; t++)
		{
			int lc = combo.Cases[t];
			float factor = combo.Factors[t];
			
			Dictionary<long, float[]> caseMap;
			if (!byCase.TryGetValue(lc, out caseMap))
			{
				throw new Exception("Combo " + combo.Id + " references load case " +
					lc + " with no " + kindForError + " records.");
			}
			
			foreach (KeyValuePair<long, float[]> kv in caseMap)
			{
				long key = kv.Key;
				float[] src = kv.Value;
				
				float[] dst;
				if (!acc.TryGetValue(key, out dst))
				{
					dst = new float[width];
					acc[key] = dst;
					keyMeta[key] = new int[] { (int)(key >> 32), (int)(uint)key };
				}
				
				for (int f = 0; f < width; f++)
				{
					dst[f] += factor * src[f];
				}
			}
		}
	}
	
	private static long Key(int elemId, int jointId)
	{
		return ((long)elemId << 32) | (uint)jointId;
	}
}