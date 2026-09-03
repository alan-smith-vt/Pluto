using System;
using System.IO;
using System.Collections.Generic;
using System.Text;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Web.Script.Serialization;
using System.IO.Compression;
using System.Xml.Linq;

public class CsvReader
{
	public static Dictionary<int, float> ReadNodeArea(string path)
	{
		var result = new Dictionary<int, float>();
		var lines = File.ReadAllLines(path);

		for (int i = 1; i < lines.Length; i++)
		{
			var tokens = lines[i].Split(',');
			int node;
			float area;
			if (int.TryParse(tokens[0].Trim(), out node) &&
				float.TryParse(tokens[1].Trim(), out area))
			{
				result[node] = area;
			}
		}
		return result;
	}

	public static Dictionary<string, List<string>> loadCsvStringListString(string path)
	{
		string[] lines = File.ReadAllLines(path);
		string[] headers = lines[0].Split(',');
		int cols = headers.Length;
		int rows = lines.Length;

		Dictionary<string, List<string>> result = new Dictionary<string, List<string>>();

		for (int c = 0; c < cols; c++)
		{
			string header = headers[c];
			result[header] = new List<string>();
		}

		for (int r = 1; r < rows; r++)
		{
			string line = lines[r];
			string[] vals = line.Split(',');
			for (int c = 0; c < cols; c++)
			{
				Debug.Printf(string.Format("c:{0}, val length:{1}", c, vals.Length), false);
				string val = vals[c];
				Debug.Printf(string.Format("c:{0}, val:{1}", c, val), false);
				result[headers[c]].Add(val);
			}
		}
		return result;
	}

	public static List<object> loadTableToObject(string path)
	{
		string groupName = System.IO.Path.GetFileNameWithoutExtension(path);
		string[] lines = File.ReadAllLines(path);
		string[] headers = lines[0].Split(',');
		int cols = headers.Length;
		int rows = lines.Length;

		List<object> result = new List<object>();
		object[,] table = new object[rows, cols];
		for (int c = 0; c < cols; c++)
		{
			string header = headers[c];
			table[0, c] = header;
		}

		for (int r = 1; r < rows; r++)
		{
			string line = lines[r];
			string[] vals = line.Split(',');
			for (int c = 0; c < cols; c++)
			{
				Debug.Printf(string.Format("c:{0}, val length:{1}", c, vals.Length), false);
				string val = vals[c];
				Debug.Printf(string.Format("c:{0}, val:{1}", c, val), false);
				table[r, c] = val;
			}
		}
		result.Add(groupName);
		result.Add(table);
		return result;
	}
}

public class XlsxReader
{
	public static void Test(string path)
	{
		//Dictionary<string, List<string>>
		var zip = ZipFile.OpenRead(path);

		// Console.WriteLine("Hello!");

		// shared strings (text cells point into this table)
		var ssEntry = zip.GetEntry("xl/sharedStrings.xml");
		var shared = new List<string>();
		if (ssEntry != null)
		{
			foreach (var si in XDocument.Load(ssEntry.Open()).Root.Elements())
			{
				shared.Add(si.Value);
			}
		}

		// sheed data
		var sheet = XDocument.Load(zip.GetEntry("xl/worksheets/sheet1.xml").Open());
		XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
		foreach (var c in sheet.Descendants(ns + "c"))
		{
			var t = (string)c.Attribute("t");       // "s" = shared string index
			var vElem = c.Element(ns + "v");
			if (vElem == null) continue;
			var v = vElem.Value;
			string text = (t == "s") ? shared[int.Parse(v)] : v;
			// Console.WriteLine(c.Attribute("r").Value + " = " + text);
			// c.Attribute("r" gives the cell ref like "B2"
		}
	}

	// Reads the first sheet of an excel workbook. Columns must have headers with unique names
	public static Dictionary<string, List<string>> ReadFirstSheet(string path)
	{
		var result = new Dictionary<string, List<string>>();
		var colOrder = new List<string>();
		var letterToHeader = new Dictionary<string, string>();

		using (var zip = ZipFile.OpenRead(path))
		{
			var shared = new List<string>();
			var ssEntry = zip.GetEntry("xl/sharedStrings.xml");
			if (ssEntry != null)
			{
				var ssDoc = XDocument.Load(ssEntry.Open());
				XNamespace n = ssDoc.Root.Name.Namespace;
				foreach (var si in ssDoc.Root.Elements(n + "si"))
					shared.Add(si.Value);
			}

			// Console.WriteLine("DBG shared count = " + shared.Count);

			//first sheet, resolved via workbook -> rels
			XNamespace rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
			XNamespace pkg = "http://schemas.openxmlformats.org/package/2006/relationships";
			var wb = XDocument.Load(zip.GetEntry("xl/workbook.xml").Open());
			var rels = XDocument.Load(zip.GetEntry("xl/_rels/workbook.xml.rels").Open());
			string rid = (string)wb.Root.Element(wb.Root.Name.Namespace + "sheets")
										.Element(wb.Root.Name.Namespace + "sheet")
										.Attribute(rel + "id");

			// Console.WriteLine("DBG rid = " + rid);

			string target = null;
			foreach (var r in rels.Root.Elements(pkg + "Relationship"))
				if ((string)r.Attribute("Id") == rid) target = (string)r.Attribute("Target");
			// Console.WriteLine("DBG target raw = " + target);

			if (!target.StartsWith("xl/")) target = "xl/" + target.TrimStart('/');
			// Console.WriteLine("DBG target final = " + target);

			var doc = XDocument.Load(zip.GetEntry(target).Open());
			XNamespace ns = doc.Root.Name.Namespace;

			// Console.WriteLine("0");

			bool header = false;
			int rowNum = 0;
			foreach (var row in doc.Descendants(ns + "row"))
			{
				// Console.WriteLine("1");
				rowNum++;
				if (!header)
				{
					// Console.WriteLine("2");
					foreach (var c in row.Elements(ns + "c"))
					{
						// Console.WriteLine("2.1");
						string col = ColLetter((string)c.Attribute("r"));
						// Console.WriteLine("2.2");
						string h = CellText(c, ns, shared);
						// Console.WriteLine("DBG header col=" + col + " name=[" + h + "]");
						letterToHeader[col] = h;
						colOrder.Add(col);
						result[h] = new List<string>();
					}
					header = true;
					// Console.WriteLine("DBG colOrder count = " + colOrder.Count);
				}
				else
				{
					// Console.WriteLine("3");
					var vals = new Dictionary<string, string>();
					foreach (var c in row.Elements(ns + "c"))
					{
						string col = ColLetter((string)c.Attribute("r"));
						string t = (string)c.Attribute("t");
						var vElem = c.Element(ns + "v");
						string raw = (vElem == null) ? "<null>" : vElem.Value;
						if (t == "s")
						{
							int idx = int.Parse(raw);
							// Console.WriteLine("DBG row=" + rowNum + " col=" + col + " sharedIdx=" +
							// idx + " (max " + (shared.Count - 1) + ")");
						}
						vals[col] = CellText(c, ns, shared);
					}
					foreach (var col in colOrder)
					{
						// if (!letterToHeader.ContainsKey(col))
						// Console.WriteLine("DBG MISSING header for col=" + col);
						string v;
						if (!vals.TryGetValue(col, out v)) v = "";
						result[letterToHeader[col]].Add(v);
					}
				}
			}
		}

		return result;
	}
	
	// Reads the named sheet of an excel workbook. Columns must have headers with unique names
	public static Dictionary<string, List<string>> ReadNamedSheet(string path, string sheetName)
	{
		var result = new Dictionary<string, List<string>>();
		var colOrder = new List<string>();
		var letterToHeader = new Dictionary<string, string>();

		using (var zip = ZipFile.OpenRead(path))
		{
			var shared = new List<string>();
			var ssEntry = zip.GetEntry("xl/sharedStrings.xml");
			if (ssEntry != null)
			{
				var ssDoc = XDocument.Load(ssEntry.Open());
				XNamespace n = ssDoc.Root.Name.Namespace;
				foreach (var si in ssDoc.Root.Elements(n + "si"))
					shared.Add(si.Value);
			}

			// Console.WriteLine("DBG shared count = " + shared.Count);

			//first sheet, resolved via workbook -> rels
			XNamespace rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
			XNamespace pkg = "http://schemas.openxmlformats.org/package/2006/relationships";
			var wb = XDocument.Load(zip.GetEntry("xl/workbook.xml").Open());
			var rels = XDocument.Load(zip.GetEntry("xl/_rels/workbook.xml.rels").Open());

			var ns = wb.Root.Name.Namespace;
			var sheetEl = wb.Root.Elements(ns + "sheets")
									.Elements(ns + "sheet")
									.FirstOrDefault(e => string.Equals((string)e.Attribute("name"), sheetName,
									StringComparison.OrdinalIgnoreCase));
									
			if (sheetEl == null)
				throw new InvalidOperationException(string.Format("Sheet '{0}' not found", sheetName));
			
			string rid = (string)sheetEl.Attribute(rel + "id");

			// Console.WriteLine("DBG rid = " + rid);

			string target = null;
			foreach (var r in rels.Root.Elements(pkg + "Relationship"))
				if ((string)r.Attribute("Id") == rid) target = (string)r.Attribute("Target");
			// Console.WriteLine("DBG target raw = " + target);

			if (!target.StartsWith("xl/")) target = "xl/" + target.TrimStart('/');
			// Console.WriteLine("DBG target final = " + target);

			var doc = XDocument.Load(zip.GetEntry(target).Open());
			// XNamespace ns = doc.Root.Name.Namespace;

			// Console.WriteLine("0");

			bool header = false;
			int rowNum = 0;
			foreach (var row in doc.Descendants(ns + "row"))
			{
				// Console.WriteLine("1");
				rowNum++;
				if (!header)
				{
					// Console.WriteLine("2");
					foreach (var c in row.Elements(ns + "c"))
					{
						// Console.WriteLine("2.1");
						string col = ColLetter((string)c.Attribute("r"));
						// Console.WriteLine("2.2");
						string h = CellText(c, ns, shared);
						// Console.WriteLine("DBG header col=" + col + " name=[" + h + "]");
						letterToHeader[col] = h;
						colOrder.Add(col);
						result[h] = new List<string>();
					}
					header = true;
					// Console.WriteLine("DBG colOrder count = " + colOrder.Count);
				}
				else
				{
					// Console.WriteLine("3");
					var vals = new Dictionary<string, string>();
					foreach (var c in row.Elements(ns + "c"))
					{
						string col = ColLetter((string)c.Attribute("r"));
						string t = (string)c.Attribute("t");
						var vElem = c.Element(ns + "v");
						string raw = (vElem == null) ? "<null>" : vElem.Value;
						if (t == "s")
						{
							int idx = int.Parse(raw);
							// Console.WriteLine("DBG row=" + rowNum + " col=" + col + " sharedIdx=" +
							// idx + " (max " + (shared.Count - 1) + ")");
						}
						vals[col] = CellText(c, ns, shared);
					}
					foreach (var col in colOrder)
					{
						// if (!letterToHeader.ContainsKey(col))
						// Console.WriteLine("DBG MISSING header for col=" + col);
						string v;
						if (!vals.TryGetValue(col, out v)) v = "";
						result[letterToHeader[col]].Add(v);
					}
				}
			}
		}

		return result;
	}

	private static string CellText(XElement c, XNamespace ns, List<string> shared)
	{
		var v = c.Element(ns + "v");
		if (v == null) return "";
		if ((string)c.Attribute("t") == "s")
		{
			int idx = int.Parse(v.Value);
			// Console.WriteLine("DBG CellText sharedIdx=" + idx + " count=" + shared.Count);
			return shared[idx];
		}
		return v.Value;
	}

	private static string ColLetter(string r)
	{
		int i = 0;
		while (i < r.Length && !char.IsDigit(r[i])) i++;
		return r.Substring(0, i);
	}

}

public static class InfluenceArea
{
	private struct Edge
	{
		public int IdA;
		public int IdB;
		public Vector3 OutwardNormal; //unit, in plate plane, points away from element interior
	}

	public static Dictionary<int, double> Compute(
		Dictionary<int, Element> elements,
		double minYComponent, // Filters out wall elements by requiring y-normal component greater than this value
		double h, // half wall thickness
		List<int> wallNodesList)
	{
		HashSet<int> wallNodes = new HashSet<int>(wallNodesList);
		Dictionary<int, double> areaByNode = new Dictionary<int, double>();

		List<Element> qualified = new List<Element>();
		foreach (KeyValuePair<int, Element> kvp in elements)
		{
			if (IsHorizontalElement(kvp.Value, minYComponent))
			{
				qualified.Add(kvp.Value);
			}
		}

		// Console.WriteLine(string.Format("qualified count: {0}",qualified.Count));

		// Pass 1: median dual + collect edges
		Dictionary<KeyValuePair<int, int>, Edge> edges = new Dictionary<KeyValuePair<int, int>, Edge>();
		Dictionary<KeyValuePair<int, int>, int> edgeCount = new Dictionary<KeyValuePair<int, int>, int>();
		Dictionary<int, Vector3> nodePos = new Dictionary<int, Vector3>();

		foreach (Element elem in qualified)
		{
			AddMedianDualAreas(elem, areaByNode);
			CollectElementEdges(elem, edges, edgeCount, nodePos);
		}

		if (wallNodes == null || wallNodes.Count == 0) return areaByNode;

		// Pass 2: wall-strip correction for nodes in wallNodes
		Dictionary<int, List<Edge>> wallEdgesByNode = BuildWallEdgesByNode(edges, edgeCount, wallNodes);

		foreach (int nodeId in wallNodes)
		{
			List<Edge> incident;
			if (!wallEdgesByNode.TryGetValue(nodeId, out incident)) continue;

			double extra = ComputeNodeWallArea(nodeId, incident, nodePos, h);
			AccumulateArea(areaByNode, nodeId, extra);
		}
		// Console.WriteLine("1");

		return areaByNode;
	}

	// ---- Pass 1: median dual + edge collection
	public static bool IsHorizontalElement(Element elem, double minYComponent)
	{
		if (elem.nNodes < 3) return false; // beam elements

		Vector3 normal = Vector3.Cross(
			elem.n[1].xyz - elem.n[0].xyz,
			elem.n[2].xyz - elem.n[0].xyz);
		float mag = normal.Length();
		if (mag <= 0f) return false;

		return System.Math.Abs(normal.Y) / mag >= minYComponent;
	}

	private static void AddMedianDualAreas(Element elem, Dictionary<int, double> areaByNode)
	{
		int n = elem.nNodes;
		Vector3 centroid = Vector3.Zero;
		for (int i = 0; i < n; i++)
		{
			centroid += elem.n[i].xyz;
		}
		centroid /= n;

		for (int i = 0; i < n; i++)
		{
			int iPrev = (i - 1 + n) % n;
			int iNext = (i + 1) % n;

			Vector3 N = elem.n[i].xyz;
			Vector3 P = elem.n[iPrev].xyz;
			Vector3 Q = elem.n[iNext].xyz;

			Vector3 d1 = centroid - N;
			Vector3 d2 = 0.5f * (P - Q);
			double subArea = 0.5 * Vector3.Cross(d1, d2).Length();

			AccumulateArea(areaByNode, elem.n[i].id, subArea);
		}
	}

	private static void CollectElementEdges(
		Element elem,
		Dictionary<KeyValuePair<int, int>, Edge> edges,
		Dictionary<KeyValuePair<int, int>, int> edgeCount,
		Dictionary<int, Vector3> nodePos)
	{
		int n = elem.nNodes;

		// Element centroid + plate normal for outward direction
		Vector3 centroid = Vector3.Zero;
		for (int i = 0; i < n; i++)
		{
			centroid += elem.n[i].xyz;
			nodePos[elem.n[i].id] = elem.n[i].xyz;
		}
		centroid /= n;

		Vector3 plateNormal = Vector3.Cross(
			elem.n[1].xyz - elem.n[0].xyz,
			elem.n[2].xyz - elem.n[0].xyz);
		plateNormal = Vector3.Normalize(plateNormal);

		for (int i = 0; i < n; i++)
		{
			int idA = elem.n[i].id;
			int idB = elem.n[(i + 1) % n].id;
			KeyValuePair<int, int> key = EdgeKey(idA, idB);

			int c;
			if (edgeCount.TryGetValue(key, out c))
			{
				edgeCount[key] = c + 1;
				continue;
				// edge already has a normal from the first owning element
				//	edges are computed for all but only used for boundary elements
			}
			edgeCount[key] = 1;

			// Outward normal: perpendicular to edge, in plate plane, pointing away from centroid
			Vector3 edgeDir = Vector3.Normalize(elem.n[(i + 1) % n].xyz - elem.n[i].xyz);
			Vector3 edgeMid = 0.5f * (elem.n[i].xyz + elem.n[(i + 1) % n].xyz);
			Vector3 perp = Vector3.Normalize(Vector3.Cross(edgeDir, plateNormal));
			if (Vector3.Dot(perp, edgeMid - centroid) < 0f)
			{
				perp = -perp;
			}

			Edge edge;
			edge.IdA = key.Key;
			edge.IdB = key.Value;
			edge.OutwardNormal = perp;
			edges[key] = edge;
		}
	}

	// ---- Pass 2: wall-strip correction ----
	private static Dictionary<int, List<Edge>> BuildWallEdgesByNode(
		Dictionary<KeyValuePair<int, int>, Edge> edges,
		Dictionary<KeyValuePair<int, int>, int> edgeCount,
		HashSet<int> wallNodes)
	{
		Dictionary<int, List<Edge>> result = new Dictionary<int, List<Edge>>();
		foreach (KeyValuePair<KeyValuePair<int, int>, Edge> kvp in edges)
		{
			Edge e = kvp.Value;
			if (!wallNodes.Contains(e.IdA) || !wallNodes.Contains(e.IdB))
			{
				continue;
			}
			if (edgeCount[kvp.Key] != 1)
			{
				System.Console.WriteLine("WARNING: wall edge (" + e.IdA +
					"," + e.IdB + ") is not a topological boundary");
			}
			AddToList(result, e.IdA, e);
			AddToList(result, e.IdB, e);
		}
		return result;
	}

	private static double ComputeNodeWallArea(
		int nodeId,
		List<Edge> incident,
		Dictionary<int, Vector3> nodePos,
		double h)
	{
		Vector3 C = nodePos[nodeId];

		if (incident.Count == 1)
		{
			// Chain endpoint: rectangular cap, half-edge length x h
			Edge e = incident[0];
			int otherId = (e.IdA == nodeId) ? e.IdB : e.IdA;
			double edgeLen = (nodePos[otherId] - C).Length();
			return 0.5 * edgeLen * h;
		}

		if (incident.Count == 2)
		{
			// Interior wall-run node: bisector offset (handles both straight runs and corners)
			Edge e1 = incident[0];
			Edge e2 = incident[1];
			int other1 = (e1.IdA == nodeId) ? e1.IdB : e1.IdA;
			int other2 = (e2.IdA == nodeId) ? e2.IdB : e2.IdA;

			Vector3 a = e1.OutwardNormal;
			Vector3 b = e2.OutwardNormal;

			// Offset vertex C' = C + h * (a+b) / (1+aÂ·b)
			Vector3 sum = a + b;
			float denom = 1f + Vector3.Dot(a, b);
			Vector3 offset = (denom < 1e-6f) ? (float)h * a : (float)h * sum / denom;
			Vector3 Cp = C + offset;

			// For each incident edge, C owns the half of the trapezoid nearest to it:
			// 	corners: C, edgeMid, edgeMid + h * edgeNormal, C'
			Vector3 mid1 = 0.5f * (C + nodePos[other1]);
			Vector3 mid2 = 0.5f * (C + nodePos[other2]);
			Vector3 midOff1 = mid1 + (float)h * a;
			Vector3 midOff2 = mid2 + (float)h * b;

			return QuadArea(C, mid1, midOff1, Cp) + QuadArea(C, mid2, midOff2, Cp);
		}

		System.Console.WriteLine("WARNING: wall node " + nodeId + " has " + incident.Count + " incident wall edges");
		return 0.0;
	}

	private static double QuadArea(Vector3 v0, Vector3 v1, Vector3 v2, Vector3 v3)
	{
		// Planar quad area: 0.5 * |d1 x d2| using diagonals
		return 0.5 * Vector3.Cross(v2 - v0, v3 - v1).Length();
	}

	// --- Utilities ---
	private static KeyValuePair<int, int> EdgeKey(int a, int b)
	{
		return a < b
			? new KeyValuePair<int, int>(a, b)
			: new KeyValuePair<int, int>(b, a);
	}

	private static void AccumulateArea(Dictionary<int, double> dict, int id, double value)
	{
		double existing;
		if (dict.TryGetValue(id, out existing))
		{
			dict[id] = existing + value;
		}
		else
		{
			dict[id] = value;
		}
	}

	private static void AddToList<T>(Dictionary<int, List<T>> dict, int key, T value)
	{
		List<T> list;
		if (!dict.TryGetValue(key, out list))
		{
			list = new List<T>();
			dict[key] = list;
		}
		list.Add(value);
	}

}

