using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

public enum GroupTargetType { Plates, Joints }

public class PredGroup
{
	public string Name;
	public GroupTargetType Type;
	public IPredicate Predicate;
}

public class Group
{
	public string Name;
	public GroupTargetType Type;
	public List<int> Ids;
}

public interface IPredicate
{
	bool MatchesPlate(Element plate, GeometryResult geom);
	bool MatchesNode(Node node, GeometryResult geom);
}

public class PlanePredicate : IPredicate
{
	public Vec3 Point;
	public Vec3 Normal;
	public double Tol;
	public double NormalTolDeg;
	
	private double _minCosAngle;
	
	public PlanePredicate(Vec3 point, Vec3 normal,
						double tol, double normalTolDeg)
	{
		Point = point;
		Normal = normal.Normalized();
		Tol = tol;
		NormalTolDeg = normalTolDeg;
		_minCosAngle = Math.Cos(normalTolDeg * Math.PI / 180.0);
	}
	
	public bool MatchesPlate(Element plate, GeometryResult geom)
	{
		// Compute centroid
		Vec3 centroid = new Vec3(0,0,0);
		for (int i = 0; i < plate.nNodes; i++)
		{
			Node n = geom.NodeDict[plate.n[i].id];
			centroid = centroid + new Vec3(n.xyz.X, n.xyz.Y, n.xyz.Z);
		}
		centroid = new Vec3(
					centroid.X / plate.nNodes, 
					centroid.Y / plate.nNodes, 
					centroid.Z / plate.nNodes);
		
		// Distance from cg to plane
		double dist = Math.Abs(Vec3.Dot(centroid - Point, Normal));
		if (dist > Tol)
		{
			return false;
		}
		
		// Plate normal: cross product of two edge vectors
		Node n0 = geom.NodeDict[plate.n[0].id];
		Node n1 = geom.NodeDict[plate.n[1].id];
		Node n2 = geom.NodeDict[plate.n[2].id];
		
		Vec3 p0 = new Vec3(n0.xyz.X, n0.xyz.Y, n0.xyz.Z);
		Vec3 p1 = new Vec3(n1.xyz.X, n1.xyz.Y, n1.xyz.Z);
		Vec3 p2 = new Vec3(n2.xyz.X, n2.xyz.Y, n2.xyz.Z);
		
		Vec3 plateNormalRaw = Vec3.Cross(p1 - p0, p2 - p0);
		if (plateNormalRaw.Length() < 1e-12)
		{
			return false; // degenerate plate
		}
		Vec3 plateNormal = plateNormalRaw.Normalized();
		
		// Alignment check
		double cosAngle = Math.Abs(Vec3.Dot(plateNormal, Normal));
		if (cosAngle < _minCosAngle)
		{
			return false;
		}
		
		return true;
	}
		
	public bool MatchesNode(Node node, GeometryResult geom)
	{
		Vec3 nodePos = new Vec3(node.xyz.X, node.xyz.Y, node.xyz.Z);
		double dist = Math.Abs(Vec3.Dot(nodePos - Point, Normal));
		return dist <= Tol;
	}
}

public class FinitePlanePredicate : IPredicate
{
	public Vec3 Point;
	public Vec3 Normal;
	public double Tol;
	public double NormalTolDeg;
	public double Width;	// full extent along U axis
	public double Length;	// full extent along V axis
	public double AngleDeg;	// rotation of UV basis around Normal
	
	private double _minCosAngle;
	private Vec3 _uAxis;
	private Vec3 _vAxis;
	
	public FinitePlanePredicate(Vec3 point, Vec3 normal,
						double tol, double normalTolDeg,
						double width, double length, double angleDeg)
	{
		Point = point;
		Normal = normal.Normalized();
		Tol = tol;
		NormalTolDeg = normalTolDeg;
		Width = width;
		Length = length;
		AngleDeg = angleDeg;
		_minCosAngle = Math.Cos(normalTolDeg * Math.PI / 180.0);
		
		BuildInPlaneBasis();
	}
	
	private void BuildInPlaneBasis()
	{
		// Pick the world asxis least aligned with Normal.
		// Tie-break: X > Y > Z (smallest index wins on equal magnitudes)
		double ax = Math.Abs(Normal.X);
		double ay = Math.Abs(Normal.Y);
		double az = Math.Abs(Normal.Z);
		
		Vec3 refVec;
		if (ax <= ay && ax <= az)
		{
			refVec = new Vec3(1, 0, 0);
		}
		else if (ay <= az)
		{
			refVec = new Vec3(0, 1, 0);
		}
		else
		{
			refVec = new Vec3(0, 0, 1);
		}
		
		// U0 = refVec projected onto plane (Gram-Schmidt), then normalized
		double refDotN = Vec3.Dot(refVec, Normal);
		Vec3 u0Raw = refVec - Normal * refDotN;
		Vec3 u0 = u0Raw.Normalized();
		
		// Rodrigues' rotation around Normal, simplified since u0 perpendicular to N
		double rad = AngleDeg * Math.PI / 180.0;
		double c = Math.Cos(rad);
		double s = Math.Sin(rad);
		Vec3 nCrossU0 = Vec3.Cross(Normal, u0);
		_uAxis = u0 * c + nCrossU0 * s;
		_vAxis = Vec3.Cross(Normal, _uAxis);
	}
	
	public bool MatchesPlate(Element plate, GeometryResult geom)
	{
		// Compute centroid
		Vec3 centroid = new Vec3(0,0,0);
		for (int i = 0; i < plate.nNodes; i++)
		{
			Node n = geom.NodeDict[plate.n[i].id];
			centroid = centroid + new Vec3(n.xyz.X, n.xyz.Y, n.xyz.Z);
		}
		centroid = new Vec3(
					centroid.X / plate.nNodes, 
					centroid.Y / plate.nNodes, 
					centroid.Z / plate.nNodes);
		
		// Distance from cg to plane
		Vec3 d = centroid - Point;
		double dist = Math.Abs(Vec3.Dot(d, Normal));
		if (dist > Tol)
		{
			return false;
		}
		
		// Plate normal: cross product of two edge vectors
		Node n0 = geom.NodeDict[plate.n[0].id];
		Node n1 = geom.NodeDict[plate.n[1].id];
		Node n2 = geom.NodeDict[plate.n[2].id];
		
		Vec3 p0 = new Vec3(n0.xyz.X, n0.xyz.Y, n0.xyz.Z);
		Vec3 p1 = new Vec3(n1.xyz.X, n1.xyz.Y, n1.xyz.Z);
		Vec3 p2 = new Vec3(n2.xyz.X, n2.xyz.Y, n2.xyz.Z);
		
		Vec3 plateNormalRaw = Vec3.Cross(p1 - p0, p2 - p0);
		if (plateNormalRaw.Length() < 1e-12)
		{
			return false; // degenerate plate
		}
		Vec3 plateNormal = plateNormalRaw.Normalized();
		
		// Alignment check
		double cosAngle = Math.Abs(Vec3.Dot(plateNormal, Normal));
		if (cosAngle < _minCosAngle)
		{
			return false;
		}
		
		// In-plane extent check
		double u = Vec3.Dot(d, _uAxis);
		double v = Vec3.Dot(d, _vAxis);
		double halfW = Width * 0.5;
		double halfL = Length * 0.5;
		if (u < -halfW || u > halfW)
		{
			return false;
		}
		if (v < -halfL || v > halfL)
		{
			return false;
		}
		return true;
	}
		
	public bool MatchesNode(Node node, GeometryResult geom)
	{
		Vec3 nodePos = new Vec3(node.xyz.X, node.xyz.Y, node.xyz.Z);
		Vec3 d = nodePos - Point;
		double dist = Math.Abs(Vec3.Dot(d, Normal));
		if (dist > Tol)
		{
			return false;
		}
		
		// In-plane extent check
		double u = Vec3.Dot(d, _uAxis);
		double v = Vec3.Dot(d, _vAxis);
		double halfW = Width * 0.5;
		double halfL = Length * 0.5;
		if (u < -halfW || u > halfW)
		{
			return false;
		}
		if (v < -halfL || v > halfL)
		{
			return false;
		}
		return true;
	}
}

// Composition: NOT, AND, OR
public class NotPredicate : IPredicate
{
	public IPredicate Inner;
	
	public NotPredicate(IPredicate inner)
	{
		Inner = inner;
	}
	
	public bool MatchesPlate(Element plate, GeometryResult geom)
	{
		return !Inner.MatchesPlate(plate, geom);
	}
	
	public bool MatchesNode(Node node, GeometryResult geom)
	{
		return !Inner.MatchesNode(node, geom);
	}
}

public class AndPredicate : IPredicate
{
	public List<IPredicate> Children;
	
	public AndPredicate(List<IPredicate> children)
	{
		Children = children;
	}
	
	public bool MatchesPlate(Element plate, GeometryResult geom)
	{
		// False if any children return false, otherwise true
		for (int i = 0; i < Children.Count; i++)
		{
			if (!Children[i].MatchesPlate(plate, geom))
			{
				return false;
			}
		}
		return true;
	}
	
	public bool MatchesNode(Node node, GeometryResult geom)
	{
		// False if any children return false, otherwise true
		for (int i = 0; i < Children.Count; i++)
		{
			if (!Children[i].MatchesNode(node, geom))
			{
				return false;
			}
		}
		return true;
	}
}

public class OrPredicate : IPredicate
{
	public List<IPredicate> Children;
	
	public OrPredicate(List<IPredicate> children)
	{
		Children = children;
	}
	
	public bool MatchesPlate(Element plate, GeometryResult geom)
	{
		// True if any children return true, otherwise false
		for (int i = 0; i < Children.Count; i++)
		{
			if (Children[i].MatchesPlate(plate, geom))
			{
				return true;
			}
		}
		return false;
	}
	
	public bool MatchesNode(Node node, GeometryResult geom)
	{
		// True if any children return true, otherwise false
		for (int i = 0; i < Children.Count; i++)
		{
			if (Children[i].MatchesNode(node, geom))
			{
				return true;
			}
		}
		return false;
	}
}

public static class PredicateFactory
{
	public static IPredicate FromDict(Dictionary<string, object> json)
	{
		string kind = (string)json["kind"];
		IPredicate predicate;
		switch(kind)
		{
			case "plane":
				predicate = ParsePlane(json);
				break;
			case "finitePlane":
				predicate = ParseFinitePlane(json);
				break;
			case "and":
				predicate = ParseAnd(json);
				break;
			case "or":
				predicate = ParseOr(json);
				break;				
			default:
				throw new Exception("Unknown predicate kind: " + kind);
		}
		
		bool negated = false;
		if (json.ContainsKey("negated"))
		{
			negated = (bool)json["negated"];
		}
		if (negated)
		{
			predicate = new NotPredicate(predicate);
		}
		return predicate;
	}
	
	public static PlanePredicate ParsePlane(Dictionary<string, object> json)
	{
		Vec3 point = ParseVec3((object[])json["point"]);
		Vec3 normal = ParseVec3((object[])json["normal"]);
		double tol = ToDouble(json["tol"]);
		double normalTol = 5.0;
		if (json.ContainsKey("normal_tol_deg"))
		{
			normalTol = ToDouble(json["normal_tol_deg"]);
		}
		return new PlanePredicate(point, normal, tol, normalTol);
	}
	
	public static FinitePlanePredicate ParseFinitePlane(Dictionary<string, object> json)
	{
		Vec3 point = ParseVec3((object[])json["point"]);
		Vec3 normal = ParseVec3((object[])json["normal"]);
		double tol = ToDouble(json["tol"]);
		double normalTol = 5.0;
		if (json.ContainsKey("normal_tol_deg"))
		{
			normalTol = ToDouble(json["normal_tol_deg"]);
		}
		double width = ToDouble(json["width"]);
		double length = ToDouble(json["length"]);
		double angleDeg = 0.0;
		if (json.ContainsKey("angle_deg"))
		{
			angleDeg = ToDouble(json["angle_deg"]);
		}
		return new FinitePlanePredicate(point, normal, tol, normalTol,
									width, length, angleDeg);		
	}
	
	private static AndPredicate ParseAnd(Dictionary<string, object> json)
	{
		return new AndPredicate(ParseChildren(json));
	}
	
	private static OrPredicate ParseOr(Dictionary<string, object> json)
	{
		return new OrPredicate(ParseChildren(json));
	}
	
	private static List<IPredicate> ParseChildren(Dictionary<string, object> json)
	{
		var childArray = (object[])json["children"];
		var children = new List<IPredicate>();
		foreach (var c in childArray)
		{
			children.Add(FromDict((Dictionary<string, object>)c));
		}
		return children;
	}
		
	private static Vec3 ParseVec3(object[] arr)
	{
		return new Vec3(ToDouble(arr[0]), ToDouble(arr[1]), ToDouble(arr[2]));
	}
	
	private static double ToDouble(object o)
	{
		// JavaScriptSErializer returns int or double
		if (o is int) {return (int)o; }
		if (o is double) { return (double)o; }
		return Convert.ToDouble(o);
	}
}

// ----------------------------------------------------------------------------
// GroupsLoader: parses groups.json -> List<Group>
// Accepts:
//	v1 legacy: 		{ groups:	[ { name, type, predicate: {...} } ] }
//	v2/v3 current:	{ version:N, expressions: [ { name, type, root: {...} } ] }
// ----------------------------------------------------------------------------
public static class GroupsLoader
{
	public static List<PredGroup> LoadGroupsJson(string path)
	{
		string text = File.ReadAllText(path);
		var ser = new JavaScriptSerializer();
		ser.MaxJsonLength = int.MaxValue;
		var root = (Dictionary<string, object>)ser.DeserializeObject(text);
		
		int version = 1;
		if (root.ContainsKey("version"))
		{
			version = (int)root["version"];
		}
		if (version > 3)
		{
			throw new Exception("Unsupported groups.json version: " + version
				+ " (this loader supports up to v3)");
		}
		
		object[] entries;
		string predicateKey;
		if (root.ContainsKey("expressions"))
		{
			entries = (object[])root["expressions"];
			predicateKey = "root";
		}
		else if (root.ContainsKey("groups"))
		{
			entries = (object[])root["groups"];
			predicateKey = "predicate";
		}
		else
		{
			throw new Exception("groups JSON missing 'expressions' or 'groups' key");
		}
		
		var predGroups = new List<PredGroup>();
		var seenNames = new HashSet<string>();
		foreach (var e in entries)
		{
			var ed = (Dictionary<string, object>)e;
			var grp = new PredGroup();
			grp.Name = (string)ed["name"];
			
			if (string.IsNullOrEmpty(grp.Name) || grp.Name.Trim().Length == 0)
			{
				throw new Exception("group has empty or whitespace name");
			}
			if (seenNames.Contains(grp.Name))
			{
				throw new Exception(string.Format("duplicate group name: {0}", grp.Name));
			}
			seenNames.Add(grp.Name);
			
			string typeStr = (string)ed["type"];
			if (typeStr == "nodes" || typeStr == "joints")
			{
				grp.Type = GroupTargetType.Joints;
			}
			else if (typeStr == "plates")
			{
				grp.Type = GroupTargetType.Plates;
			}
			else
			{
				throw new Exception("Unknown group type: " + typeStr);
			}
			grp.Predicate = PredicateFactory.FromDict(
				(Dictionary<string, object>)ed[predicateKey]);
			predGroups.Add(grp);
		}
		return predGroups;
	}
}

public class GroupEvaluator
{
	public static Dictionary<string, Group> Evaluate(
		List<PredGroup> predGroups, GeometryResult geom)
	{
		var groups = new Dictionary<string, Group>();
		foreach (var pg in predGroups)
		{
			var ids = new List<int>();
			if (pg.Type == GroupTargetType.Plates)
			{
				foreach (Element e in geom.ElementDict.Values)
				{
					if (pg.Predicate.MatchesPlate(e, geom))
					{
						ids.Add(e.id);
					}
				}
			}
			else
			{
				foreach (Node n in geom.NodeDict.Values)
				{
					if (pg.Predicate.MatchesNode(n, geom))
					{
						ids.Add(n.id);
					}
				}
			}
			var result = new Group();
			result.Name = pg.Name;
			result.Type = pg.Type;
			result.Ids = ids;
			groups[result.Name] = result;
		}
		return groups;
	}
}


// Something else