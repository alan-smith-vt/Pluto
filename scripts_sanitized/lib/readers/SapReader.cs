using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Linq;
using System.Numerics;

// -------------------------------------------------------------------
// Sap2kParser
// 	Parses SAP2000 .$2k / .s2k text export files into a table-row dict
// -------------------------------------------------------------------

public class Sap2kParser
{
	// Raw parsed tables: table name -> list of rows; each row
	// is a key-value dict from the "Key=Value" tokens on that line.
	private Dictionary<string, List<Dictionary<string, string>>> _tables;
	
	public Sap2kParser(string path)
	{
		_tables = new Dictionary<string, List<Dictionary<string, string>>>(StringComparer.OrdinalIgnoreCase);
		string[] lines = File.ReadAllLines(path);
		
		string currentTable = null;
		StringBuilder pending = new StringBuilder();
		
		for (int i = 0; i < lines.Length; i++)
		{
			string raw = lines[i];
			if (raw == null)
			{
				continue;
			}
			
			string line = raw.Trim();
			
			// Skip blanks and banner lines (rows of ; or similar)
			if (line.Length == 0)
			{
				continue;
			}
			if (IsBannerLine(line))
			{
				continue;
			}
			
			// Table header: TABLE:  "NAME"
			if (line.StartsWith("TABLE:", StringComparison.OrdinalIgnoreCase))
			{
				currentTable = ExtractTableName(line);
				if (!_tables.ContainsKey(currentTable))
				{
					_tables[currentTable] = new List<Dictionary<string, string>>();
				}
				pending.Length = 0;
				continue;
			}
		
			
			// End-of-file marker
			if (line.StartsWith("END TABLE DATA", StringComparison.OrdinalIgnoreCase))
			{
				break;
			}
			
			if (currentTable == null)
			{
				continue;
			}
			
			// Handle line continuations: trailing "_" means more on next line
			string assembled;
			if (EndsWithContinuation(line))
			{
				// Strip the trailing "_" and any whitespace before it
				string trimmed = StripContinuation(line);
				pending.Append(trimmed);
				pending.Append(' ');
				continue;
			}
			else
			{
				if (pending.Length > 0)
				{
					pending.Append(line);
					assembled = pending.ToString();
					pending.Length = 0;
				}
				else
				{
					assembled = line;
				}
			}
			
			Dictionary<string, string> row = TokenizeRow(assembled);
			if (row != null && row.Count > 0)
			{
				_tables[currentTable].Add(row);
			}
		}
	}
	
	// --------------------------------------------------
	// Helpers
	// --------------------------------------------------
	private static bool IsBannerLine(string line)
	{
		// Lines like ";;;;;;;;;;;;;;" used as section dividers
		for (int i = 0; i < line.Length; i++)
		{
			if (line[i] != ';')
			{
				return false;
			}
		}
		return true;
	}
	
	private static string ExtractTableName(string line)
	{
		// Format TABLE:  "NAME"
		int q1 = line.IndexOf('"');
		if (q1 < 0)
		{
			// Fallback: take everything after the colon
			int colon = line.IndexOf(':');
			return line.Substring(colon + 1).Trim();
		}
		int q2 = line.IndexOf('"', q1 + 1);
		if (q2 < 0)
		{
			return line.Substring(q1 + 1).Trim();
		}
		return line.Substring(q1 + 1, q2 - q1 - 1).Trim();
	}
	
	private static bool EndsWithContinuation(string line)
	{
		// Continuation char "_"
		if (line.Length == 0)
		{
			return false;
		}
		return line[line.Length - 1] == '_';
	}
	
	private static string StripContinuation(string line)
	{
		// Drop the trailing underscore
		int end = line.Length - 1;
		int i = end - 1;
		while (i >= 0 && char.IsWhiteSpace(line[i]))
		{
			i--;
		}
		return line.Substring(0, i + 1);
	}
	
	// Tokenize a logical row (continuations already merged) into key=value pairs.
	// Respects double-quoted values that may contain spaces.
	private static Dictionary<string, string> TokenizeRow(string line)
	{
		Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		
		int i = 0;
		int n = line.Length;
		while (i < n)
		{
			// Skip whitespace
			while (i < n && char.IsWhiteSpace(line[i]))
			{
				i++;
			}
			if (i >= n)
			{
				break;
			}
			
			// Read key (up to '=' or whitespace)
			int keyStart = i;
			while (i < n && line[i] != '=' && !char.IsWhiteSpace(line[i]))
			{
				i++;
			}
			if (i >= n || line[i] != '=')
			{
				// Malformed token (no '='): skip whatever is left of it
				while (i < n && !char.IsWhiteSpace(line[i]))
				{
					i++;
				}
				continue;
			}
			string key = line.Substring(keyStart, i - keyStart);
			i++; // skip '='
			
			// Read Value: quoted or unquoted
			string value;
			if (i < n && line[i] == '"')
			{
				i++; // skip opening quote
				int valStart = i;
				while (i < n && line[i] != '"')
				{
					i++;
				}
				value = line.Substring(valStart, i - valStart);
				if (i < n)
				{
					i++; // skip closing quote
				}
			}
			else
			{
				int valStart = i;
				while (i < n && !char.IsWhiteSpace(line[i]))
				{
					i++;
				}
				value = line.Substring(valStart, i - valStart);
			}
			
			result[key] = value;
		}
		
		return result;
	}
	
	// --------------------------------------------------
	// Raw Table Access
	// --------------------------------------------------
	public bool HasTable(string name)
	{
		return _tables.ContainsKey(name);
	}
	
	public List<Dictionary<string, string>> GetTable(string name)
	{
		List<Dictionary<string, string>> rows;
		if (_tables.TryGetValue(name, out rows))
		{
			return rows;
		}
		return new List<Dictionary<string, string>>();
	}
	
	public IEnumerable<string> TableNames()
	{
		return _tables.Keys;
	}
}


// -------------------------------------------------------------------
// Sap2kReader
// 	Calls Sap2kParser then extracts geometry (node/elem lists)
//	Nodes are converted from 
//		Sap local -> Sap Global (Z up) -> Staad Global (Y up)
// -------------------------------------------------------------------

public static class Sap2kReader {
	public static GeometryResult ExtractGeometry(string sapFilePath)
	{
		Console.WriteLine("Start");
		Sap2kParser parser = new Sap2kParser(sapFilePath);
		Console.WriteLine("File Parsed");
		
		List<Node> nodeList = GetJoints(parser);
		Dictionary<int, Node> nodeDict = nodeList.ToDictionary(n => n.id);
		Console.WriteLine(string.Format("Nodes Extracted {0}",nodeList.Count));
		
		// Populate the node coordinates in the element object using the nodeDict (instead of just node ids)
		List<Element> elementList = GetElements(parser, nodeDict);
		Dictionary<int, Element> elementDict = elementList.ToDictionary(n => n.id);
		Console.WriteLine(string.Format("Elements Extracted {0}",elementList.Count));
		
		return new GeometryResult{ NodeDict = nodeDict, ElementDict = elementDict};
	}
	
	// Extract joints with global-coord transformation applied where needed
	private static List<Node> GetJoints(Sap2kParser parser)
	{
		Dictionary<string, Sap2kCoordSystem> systems = BuildSystems(parser);
		List<Node> joints = new List<Node>();
		
		List<Dictionary<string, string>> rows = parser.GetTable("JOINT COORDINATES");
		for (int i = 0; i < rows.Count; i++)
		{
			Dictionary<string, string> row = rows[i];
			
			int id = (int)ParseD(row, "Joint");
			double x = ParseD(row, "XorR");
			double y = ParseD(row, "Y");
			double z = ParseD(row, "Z");
			
			string sysName;
			if (!row.TryGetValue("CoordSys", out sysName))
			{
				sysName = "GLOBAL"; //default to global
			}
			Node j = new Node();
			j.id = id;
			
			if (string.Equals(sysName, "GLOBAL", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(sysName, "GLOBAL_meshrefine", StringComparison.OrdinalIgnoreCase))
			{
				// Already global, no transform needed
				j.xyz = new Vector3((float)x, (float)y, (float)z);
			}
			else
			{
				Sap2kCoordSystem sys;
				double gx, gy, gz;
				if (systems.TryGetValue(sysName, out sys))
				{
					sys.LocalToGlobal(x, y, z, out gx, out gy, out gz);
					j.xyz = new Vector3((float)gx, (float)gy, (float)gz);
				}
				else
				{
					// default to raw values if the above fails
					// j.X = x; j.Y = y; j.Z = z;
					j.xyz = new Vector3((float)x, (float)y, (float)z);
				}
				
			}
			// Convert from Sap Global (Z up) to Staad Global (Y up)
			var StaadXyz = new Vector3(j.xyz.X, j.xyz.Z, -j.xyz.Y);
			j.xyz = StaadXyz;
			
			joints.Add(j);
		}
		
		return joints;
	}
	
	// Populates a list of elements with eid -> nids & xyz
	private static List<Element> GetElements(Sap2kParser parser, Dictionary<int, Node> nodeDict)
	{
		List<Element> elements = new List<Element>();
		List<Dictionary<string, string>> rows = parser.GetTable("CONNECTIVITY - AREA");
		for (int i = 0; i < rows.Count; i++)
		{
			Dictionary<string, string> row = rows[i];
			Element e = new Element();
			e.id = (int)ParseD(row, "Area");
			// Console.WriteLine(string.Format("e.id = {0}",e.id));
			e.n = new Node[4]; //4th node is null if tri
			for (int j = 0; j < 4; j++)
			{
				string jid = "Joint" + (j+1);
				// Console.WriteLine(string.Format("jid = {0}",jid));
				if (row.ContainsKey(jid))
				{
					// Console.WriteLine("row contains key jid");
					Node n = new Node();
					n.id = (int)ParseD(row, jid);
					n.xyz = nodeDict[n.id].xyz;
					e.n[j] = n;
				}
			}
			e.nNodes = row.ContainsKey("Joint4") ? (byte)4 : (byte)3;
			elements.Add(e);
		}
		return elements;
	}

	// Build systems dict from the parser's coordinate systems table
	private static Dictionary<string, Sap2kCoordSystem> BuildSystems(Sap2kParser parser)
	{
		Dictionary<string, Sap2kCoordSystem> map = 
			new Dictionary<string, Sap2kCoordSystem>(StringComparer.OrdinalIgnoreCase);
			
		List<Dictionary<string, string>> rows = parser.GetTable("COORDINATE SYSTEMS");
		for (int i = 0; i < rows.Count; i++)
		{
			Dictionary<string, string> row = rows[i];
			string name;
			if (!row.TryGetValue("Name", out name))
			{
				continue;
			}
			double x = ParseD(row, "X");
			double y = ParseD(row, "Y");
			double z = ParseD(row, "Z");
			
			double az = ParseD(row, "AboutZ");
			double ay = ParseD(row, "AboutY");
			double ax = ParseD(row, "AboutX");
			map[name] = Sap2kCoordSystem.FromZYX(name, x, y, z, az, ay, ax);
		}
		return map;
	}
	
	private static double ParseD(Dictionary<string, string> row, string key)
	{
		string v;
		if (!row.TryGetValue(key, out v))
		{
			return 0.0;
		}
		double parsed;
		if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
		{
			return parsed;
		}
		return 0.0;
	}

	private class Sap2kCoordSystem
	{
		public string Name;
		public double Ox, Oy, Oz;
		public double R00, R01, R02;
		public double R10, R11, R12;
		public double R20, R21, R22;
		
		public static Sap2kCoordSystem FromZYX(
			string name,
			double ox, double oy, double oz,
			double aboutZdeg, double aboutYdeg, double aboutXdeg)
		{
			double az = aboutZdeg * Math.PI / 180.0;
			double ay = aboutYdeg * Math.PI / 180.0;
			double ax = aboutXdeg * Math.PI / 180.0;
			double cz = Math.Cos(az), sz = Math.Sin(az);
			double cy = Math.Cos(ay), sy = Math.Sin(ay);
			double cx = Math.Cos(ax), sx = Math.Sin(ax);
			
			// R = Rz * Ry * Rx (intrinsic Z-Y-X, matching SAP's AboutZ/AboutY/AboutX)
			double m00 = cy, m01 = 0, m02 = sy;
			double m10 = 0,  m11 = 1, m12 = 0;
			double m20 = -sy,m21 = 0, m22 = cy;
			
			// Ry * Rx
			double n00 = m00, n01 = m01 * cx + m02 * sx, n02 = m01 * -sx + m02 * cx;
			double n10 = m10, n11 = m11 * cx + m12 * sx, n12 = m11 * -sx + m12 * cx;
			double n20 = m20, n21 = m21 * cx + m22 * sx, n22 = m21 * -sx + m22 * cx;
			
			Sap2kCoordSystem s = new Sap2kCoordSystem();
			s.Name = name;
			s.Ox = ox; s.Oy = oy; s.Oz = oz;
			s.R00 = cz * n00 + -sz * n10; s.R01 = cz * n01 + -sz * n11; s.R02 = cz * n02+ -sz * n12;
			s.R10 = sz * n00 +  cz * n10; s.R11 = sz * n01 +  cz * n11; s.R12 = sz * n02+  cz * n12;
			s.R20 = n20;
			s.R21 = n21;
			s.R22 = n22;
			return s;
		}
		
		public void LocalToGlobal(double lx, double ly, double lz,
									out double gx, out double gy, out double gz)
		{
			gx = R00 * lx + R01 * ly + R02 * lz + Ox;
			gy = R10 * lx + R11 * ly + R12 * lz + Oy;
			gz = R20 * lx + R21 * ly + R22 * lz + Oz;
		}
	}

	// Only used for debugging. Superseded by GROUP logic
	public static List<int> GetBaseJointIds(List<Node> joints)
	{
		// Staad space here, so Y is up
		var minY = joints.Min(n => n.xyz.Y);
		var tol = 12; // inches
		var baseJointIds = joints
			.Where(n => Math.Abs(n.xyz.Y - minY) < tol)
			.Select(n => n.id)
			.ToList();
		
		return baseJointIds;
	}
}
	