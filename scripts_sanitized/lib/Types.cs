using System;
using System.IO;
using System.Collections;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using System.Linq;
using System.Text.RegularExpressions;


public class ProgressBar
{
	private int total;
	private int barWidth;
	private System.Diagnostics.Stopwatch sw;
	public int current;
	private double lastUpdate = 0;

	public ProgressBar(int total, int barWidth = 40, string title = "")
	{
		this.total = total;
		this.barWidth = barWidth;
		this.sw = System.Diagnostics.Stopwatch.StartNew();
		if (!string.IsNullOrEmpty(title)) { Console.Error.WriteLine(string.Format("{0}", title)); }
	}

	private string FormatTime(double seconds)
	{
		TimeSpan ts = TimeSpan.FromSeconds(seconds);
		if (ts.TotalHours >= 1) return string.Format("{0}:{1:D2}:{2:D2}", (int)ts.TotalHours, ts.Minutes, ts.Seconds);
		if (ts.TotalMinutes >= 1) return string.Format("{0}:{1:D2}", (int)ts.TotalMinutes, ts.Seconds);
		return string.Format("{0}s", (int)ts.TotalSeconds);
	}

	public void Tick()
	{
		current++;
		double pct = (double)current / total;
		int filled = (int)(pct * barWidth);
		string bar = new string('#', filled) + new string('-', barWidth - filled);
		double elapsed = sw.Elapsed.TotalSeconds;

		// Only update every 10ms
		if (elapsed - lastUpdate < 0.1 && current != total) return;
		lastUpdate = elapsed;
		double rate = current / elapsed;
		double eta = (total - current) / rate;
		Console.Error.Write(string.Format("\r[{0}] {1}/{2} ({3:F1}%) {4:F0}/s ETA:{5:F0}   ",
			bar, current, total, pct * 100, rate, FormatTime(eta)));
	}

	public void Finish()
	{
		int filled = barWidth;
		string bar = new string('#', filled);
		double elapsed = sw.Elapsed.TotalSeconds;
		double rate = total / elapsed;
		Console.Error.Write(string.Format("\r[{0}] {1}/{2} (100.0%) {3:F0}/s {4} total   ",
			bar, total, total, rate, FormatTime(elapsed)));
		Console.Error.WriteLine();
	}
}

// Debug helpers
public static class Debug
{
	public static void Printf(string msg, bool flag)
	{
		if (flag)
		{
			Console.Error.WriteLine(msg);
		}

	}
}

// Enums
public enum ElemType : byte
{
	Slab = 0,
	Wall = 1,
	Fake = 2
}

public enum CoordFrame : byte
{
	Local = 0,
	Reference = 1,
	Global = 2
}

// Indices for the StressRecord.Sf float[] field
public enum SC
{
	Sx = 0,
	Sxy = 1,
	SQx = 2,
	Sy = 3,
	SQy = 4,
	Mx = 5,
	Mxy = 6,
	My = 7
}

// Indices for the Disp.DR float[] field
public enum DR
{
	dx = 0,
	dy = 1,
	dz = 2,
	rx = 3,
	ry = 4,
	rz = 5
}

// Indices for the DsrRecord.Values float[] field
public enum DSR
{
	PMx,        // Simple triangle about the balance point
	PMy,
	IR_OOPx,    // OOP DSR (steel & concrete strength)
	IR_OOPy,
	IR_Asx, 	// Combined IP shear, Axial, & Moment Steel
	IR_Asy,
	IR_IPx, 	// IP DSR accounting for steel used by Moment & Axial
	IR_IPy,
	IR_IP_OOPx, // a^2 + b^2 <= 1 (capped at 2.0 for visuals)
	IR_IP_OOPy,
	Count		// Must stay last, used to allocate the float[]
}

// Indices for the StrRecord.Values float[] field
public enum STR
{
	Pnc_x,  // phi factors included (design strengths)
	Pnc_y,
	Pnt_x,
	Pnt_y,
	Pnb_x,
	Pnb_y,
	Mn_x,
	Mn_y,
	Mnb_x,
	Mnb_y,
	Vc,
	Vc_kx,
	Vc_ky,
	Vs_x,
	Vs_y,
	Vn_x,
	Vn_y,
	Count
}


public enum LengthUnit
{
	Inch,
	Foot,
	Meter,
	Millimeter,
	Centimeter
}

public enum ForceUnit
{
	Pound,
	Kip,
	Newton,
	KiloNewton,
	MetricTon
}

// Double based vector3 math structure
public struct Vec3
{
	public double X, Y, Z;
	public Vec3(double x, double y, double z)
	{
		X = x; Y = y; Z = z;
	}

	public static Vec3 operator -(Vec3 a, Vec3 b)
	{
		return new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
	}

	public static Vec3 operator +(Vec3 a, Vec3 b)
	{
		return new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
	}

	public static Vec3 operator *(Vec3 a, double s)
	{
		return new Vec3(a.X * s, a.Y * s, a.Z * s);
	}

	public static Vec3 operator /(Vec3 a, double s)
	{
		return new Vec3(a.X / s, a.Y / s, a.Z / s);
	}


	public double Length()
	{
		return Math.Sqrt(X * X + Y * Y + Z * Z);
	}

	public Vec3 Normalized()
	{
		double len = Length();
		return new Vec3(X / len, Y / len, Z / len);
	}

	public static double Dot(Vec3 a, Vec3 b)
	{
		return a.X * b.X + a.Y * b.Y + a.Z * b.Z;
	}

	public static Vec3 Cross(Vec3 a, Vec3 b)
	{
		return new Vec3(
			a.Y * b.Z - a.Z * b.Y,
			a.Z * b.X - a.X * b.Z,
			a.X * b.Y - a.Y * b.X);
	}
}

// Custom Data Classes
public class Node : IComparable<Node>, IEquatable<Node>
{
	public int id;
	public Vector3 xyz;

	// Sort by node centroid.
	public int CompareTo(Node other)
	{
		if (other == null) return 1;

		// Compare by X-coordinate first
		int xComp = this.xyz.X.CompareTo(other.xyz.X);
		if (xComp != 0) return xComp;
		// If X-coordinate is the same, compare by Z-coordinate
		int zComp = this.xyz.Z.CompareTo(other.xyz.Z);
		if (zComp != 0) return zComp;
		// If Z-coordinate is the same, compare by Y-coordinate
		return this.xyz.Y.CompareTo(other.xyz.Y);
	}
	// Implement the Equals method from IEquatable<Node>
	public bool Equals(Node other)
	{
		if (other == null) return false;
		if (ReferenceEquals(this, other)) return true;

		// Defining what makes two Nodes "equal"
		return id == other.id;
	}

	// Override GetHashCode
	public override int GetHashCode()
	{
		unchecked
		{
			int hash = 17;
			hash = hash * 31 + id.GetHashCode();
			return hash;
		}
	}
}

// Plate element
public class Element : IEquatable<Element>
{
	// Directly read from ANL file
	public int id;
	public byte nNodes;
	public ElemType elType; //Enum byte
	public float t;
	public Node[] n; //Nodes constructed from anl indices

	// Derivatative values
	public Vector3 rZ;
	public Matrix4x4 R_lr;
	public Matrix4x4 R_gr;
	public double angle;
	public Vector3 cg;

	// // Sort by element centroid.
	// public int CompareTo(Element other)
	// {
	// if (other == null) return 1;

	// Vector3 thisCenter = this.ComputeCentroid();
	// Vector3 otherCenter = other.ComputeCentroid();

	// // Compare by X-coordinate first
	// int xComp = thisCenter.X.CompareTo(otherCenter.X);
	// if (xComp != 0) return xComp;
	// // If X-coordinate is the same, compare by Z-coordinate
	// int zComp = thisCenter.Z.CompareTo(otherCenter.Z);
	// if (zComp != 0) return zComp;
	// // If Z-coordinate is the same, compare by Y-coordinate
	// return thisCenter.Y.CompareTo(otherCenter.Y);
	// }


	// Implement the Equals method from IEquatable<Element>
	public bool Equals(Element other)
	{
		if (other == null) return false;
		if (ReferenceEquals(this, other)) return true;

		// Defining what makes two Elements "equal"
		return id == other.id;
	}

	// Override GetHashCode
	public override int GetHashCode()
	{
		unchecked
		{
			int hash = 17;
			hash = hash * 31 + id.GetHashCode();
			return hash;
		}
	}
	public void ComputeCentroid()
	{
		Vector3 center = Vector3.Zero;
		for (int i = 0; i < this.nNodes; i++) { center += this.n[i].xyz; }
		this.cg = center / this.nNodes;
	}

	public void ComputeRotationMat()
	{
		// Initializing vars------
		double sqrt2 = Math.Sqrt(2);
		Vector3 p1; Vector3 p2; Vector3 p3;
		Vector3 u; Vector3 v; Vector3 w;
		Vector3 localX; Vector3 localY; Vector3 localZ;
		Vector3 globalX; Vector3 globalY; Vector3 globalZ;
		Vector3 refX; Vector3 refY; Vector3 refZ;
		// -----------------------

		// Note: there is no assembly for 3x3 matrices, so 4x4 matrices are used, but
		// the 4th index of each is unused.

		// Defining three points in the plane of the element
		p1 = this.n[0].xyz;
		p2 = this.n[1].xyz;
		p3 = this.n[2].xyz;

		// Vectors defining plane of element
		// Local X-axis as defined per STAAD Help is taken to be in the direction from
		// the element's first node to its second node.
		// (https://docs.bentley.com/LiveContent/web/STAAD.Pro%20Help-v13/en/GUID-777CEFB1-4F20-457A-8594-C3F8288A1AF5.html)
		u = p2 - p1;

		// Secondary vector defining plane of element similar to STAAD Help.
		// STAAD help explains that this vector is taken to be in the direction from the
		// first node to the last node of the element. But since all nodes lie on the same
		// plane, the first and third nodes can be used, regardless of how many nodes
		// an element has.
		v = p3 - p1;

		// Convert the 'u' vector into a unit vector to obtain a vector representing the
		// element's local X-axis.
		localX = Vector3.Normalize(u); // Referred to as "lX" in comments.

		// Local Z-axis as defined per STAAD Help is in the direction of the first
		// vector cross the second vector; 'u' x 'v'.
		w = Vector3.Cross(u, v);

		// Convert the 'w' vector into a unit vector to obtain a vector representing the
		// element's local Z-axis.
		localZ = Vector3.Normalize(w); // Referred to as "lZ" in comments.

		// Alert the user if the local Z-axis vector caLCulates to 0. May mean there is
		// an issue with the mesh.
		if (localZ.Length() <= 1e-6)
		{
			Console.Error.WriteLine("Nodes 1, 2, and 3 are collinear or form a zero-area element.\r\nCheck element {this.id} mesh.");
		}

		// Local Y axis as defined per STAAD Help is in the direction of the local Z-axis
		// vector cross the local X-axis vector; 'Z' x 'X'.
		localY = Vector3.Cross(localZ, localX);
		// Convert the 'Y' vector into a unit vector to obtain a vector representing the
		// element's local Y-axis.
		localY = Vector3.Normalize(localY); // Referred to as "lY" in comments.

		// Definition of model global axes vectors.
		globalX = Vector3.UnitX; // Referred to as "gX" in comments.
		globalY = Vector3.UnitY; // Referred to as "gY" in comments.
		globalZ = Vector3.UnitZ; // Referred to as "gZ" in comments.

		// If the element's local Z-axis is mostly vertical (<= 45Â° with the global
		// Y-axis; |lZ * gY| >= cos(45Â°)), element is considered a slab element.
		// Otherwise, it is considered to be a wall element.
		if (Math.Abs(Vector3.Dot(localZ, globalY)) >= (sqrt2 / 2))
		{
			this.elType = ElemType.Slab;
		}
		else
		{
			this.elType = ElemType.Wall;
		}

		// Initializing variables for refernce strucutral component axes vectors to
		// rotate and align element to. These are recaLculated as needed based on various
		// criteria checked one-by-one below.
		refX = localX;
		refY = localY;
		refZ = localZ;

		// ---------------------------------------------------------------------------------------
		// Determine orientation of the reference structure's Z-axis
		// ---------------------------------------------------------------------------------------
		// If the element is a slab element with its local Z-axis pointing downwards
		// (lZ * gY is negative), then the reference Z-axis is equal to the flip of
		// the local Z-axis.
		if ((this.elType == ElemType.Slab) && (Vector3.Dot(localZ, globalY) < 0))
		{
			refZ = localZ * -1;
		}

		// Otherwise, if the element is a wall element, mainly spanning the east-west
		// direction (local Z makes an angle <= 45Â° with the global X) with local Z in
		// the opposite direciton of global X (lZ * gX <= -cos(45Â°)), then the reference
		// Z-axis is equal to the flip of the local Z-axis.
		else if ((this.elType == ElemType.Wall) && (Vector3.Dot(localZ, globalX) <= -(sqrt2 / 2)))
		{
			refZ = localZ * -1;
		}

		// Otherwise, if the element is a wall element, mainly in the north-south
		// direction (local Z makes an angle < 45Â° with the global Z), with local Z in
		// the opposite direciton of global Z (lZ * gZ < -cos(45Â°)), then the reference
		// Z-axis is equal to the flip of the local Z-axis.
		else if ((this.elType == ElemType.Wall) && (Vector3.Dot(localZ, globalZ) < -(sqrt2 / 2)))
		{
			refZ = localZ * -1;
		}
		// ---------------------------------------------------------------------------------------

		// ---------------------------------------------------------------------------------------
		// Determine orientation of the reference structure's X-axis
		// ---------------------------------------------------------------------------------------
		// If element is a slab element, the reference X-axis is determined by projecting
		// the global X-axis onto the plane of the element via vector rejection.
		if (this.elType == ElemType.Slab)
		{
			//refX = globalX - (Vector3.Dot(globalX, refZ) * refZ);
			//refX = new Vector3(refZ.Y, -refZ.X, 0);
			//refX = Vector3.Normalize(refX);
			refX = Vector3.Normalize(Vector3.Cross(-globalZ, refZ));
			// The reference Y-axis is in the direction of 'Z' cross 'X'
			refY = Vector3.Normalize(Vector3.Cross(refZ, refX));
			//refY = Vector3.Cross(refZ, refX);
			//refY = new Vector3(0, refZ.Z, -refZ.Y);
			//refY = Vector3.Normalize(refY);
		}

		// If element is a wall element with its local Z-axis pointing mostly in the
		// global X direction (|lZ * gX >= cos(45Â°)|), the reference X-axis is determined
		// by projecting the global negative Z-axis onto the plane of the element via
		// vector rejection.
		else if ((this.elType == ElemType.Wall) && (Math.Abs(Vector3.Dot(localZ, globalX)) >= (sqrt2 / 2)))
		{
			//refX = (-globalZ) - (Vector3.Dot(-globalZ, refZ) * refZ);
			//refX = new Vector3(refZ.Z, 0, -refZ.X);
			//refX = Vector3.Normalize(refX);
			refX = Vector3.Normalize(Vector3.Cross(globalY, refZ));
			// The reference Y-axis is in the direction of 'Z' cross 'X'
			refY = Vector3.Normalize(Vector3.Cross(refZ, refX));
			//refY = Vector3.Cross(refZ, refX);
			//refY = new Vector3(-refZ.Y, refZ.X, 0);
			//refY = Vector3.Normalize(refY);
		}

		// If element is a wall element with its local Z-axis pointing mostly in the
		// global Z direction (|lZ * gZ > cos(45Â°)|), the reference X-axis is determined
		// by projecting the global X-axis onto the plane of the element via
		// vector rejection.
		else if ((this.elType == ElemType.Wall) && (Math.Abs(Vector3.Dot(localZ, globalZ)) > (sqrt2 / 2)))
		{
			//refX = globalX - (Vector3.Dot(globalX, refZ) * refZ);
			//refX = new Vector3(refZ.Z, 0, -refZ.X);
			//refX = Vector3.Normalize(refX); ;
			refX = Vector3.Normalize(Vector3.Cross(globalY, refZ));
			// The reference Y-axis is in the direction of 'Z' cross 'X'
			refY = Vector3.Normalize(Vector3.Cross(refZ, refX));
			//refY = Vector3.Cross(refZ, refX);
			//refY = new Vector3(0, refZ.Z, -refZ.Y);
			//refY = Vector3.Normalize(refY);
		}

		this.rZ = refZ;

		// Assembling the rotation matrix for the element to transform from local axes
		// to the reference structural component axes.
		this.R_lr = new Matrix4x4(
			Vector3.Dot(refX, localX), Vector3.Dot(refX, localY), Vector3.Dot(refX, localZ), 0,
			Vector3.Dot(refY, localX), Vector3.Dot(refY, localY), Vector3.Dot(refY, localZ), 0,
			Vector3.Dot(refZ, localX), Vector3.Dot(refZ, localY), Vector3.Dot(refZ, localZ), 0,
			0, 0, 0, 0
		);
		// Assembling the rotation matrix for the element to transform from global axes
		// to the reference structural component axes.
		this.R_gr = new Matrix4x4(
			Vector3.Dot(refX, globalX), Vector3.Dot(refX, globalY), Vector3.Dot(refX, globalZ), 0,
			Vector3.Dot(refY, globalX), Vector3.Dot(refY, globalY), Vector3.Dot(refY, globalZ), 0,
			Vector3.Dot(refZ, globalX), Vector3.Dot(refZ, globalY), Vector3.Dot(refZ, globalZ), 0,
			0, 0, 0, 0
		);

		//this.angle = Math.Acos(Vector3.Dot(refY, localY) /
		//	(refY.Length() * localY.Length())) * 180 / Math.PI;
		this.angle = Math.Round(Math.Atan2(Vector3.Dot(refX, localY), Vector3.Dot(refX, localX)) * 180 / Math.PI, 7);
	}
}

public class Member
{
	// Directly read from ANL file
	public int id;
	public Node[] n; //Nodes constructed from anl indices
	public double YD; // Not read from ANL currently, only used in StaadWriter

	// Derivatative values
	public float[] sect;
	public Vector3 rZ;
	public Matrix4x4 R_lr;
	public Matrix4x4 R_gr;

	public void ComputeRotationMat()
	{
		// Initializing vars------
		double sqrt2 = Math.Sqrt(2);
		Vector3 p1; Vector3 p2;
		Vector3 u; Vector3 v; Vector3 w;
		Vector3 localX; Vector3 localY; Vector3 localZ;
		Vector3 globalX; Vector3 globalY; Vector3 globalZ;
		Vector3 refX; Vector3 refY; Vector3 refZ;
		// -----------------------

		// Note: there is no assembly for 3x3 matrices, so 4x4 matrices are used, but
		// the 4th index of each is unused.

		// Definition of model global axes vectors.
		globalX = Vector3.UnitX; // Referred to as "gX" in comments.
		globalY = Vector3.UnitY; // Referred to as "gY" in comments.
		globalZ = Vector3.UnitZ; // Referred to as "gZ" in comments.

		// Defining two points that form the beam
		p1 = this.n[0].xyz;
		p2 = this.n[1].xyz;
		// Vectors defining beam local X-axis
		// Local X-axis as defined per STAAD Help is taken to be in the direction from
		// the element's first node to its second node.
		// (https://docs.bentley.com/LiveContent/web/STAAD.Pro%20Help-v16/en/GUID-8B6FEDCC-F914-4787-BE94-C43E2CBF708D.html)
		u = p2 - p1;
		localX = Vector3.Normalize(u);

		// If the beam element is mostly vertical, then the initial secondary vector is taken
		// to be the positive global Z-axis.
		if (Math.Abs(Vector3.Dot(localX, globalY)) >= (sqrt2 / 2)) { v = globalZ; }
		// Otherwise, the beam's secondary vector is in the direction of the positive
		// global Y-axis
		else { v = Vector3.UnitY; }

		// Local Z-axis is in the direction of the first vector cross the second vector;
		// 'u' x 'v'.
		w = Vector3.Cross(u, v);

		// Convert the 'w' vector into a unit vector to obtain a vector representing the
		// beam's local Z-axis.
		localZ = Vector3.Normalize(w); // Referred to as "lZ" in comments.

		// Local Y axis  is in the direction of the local Z-axis vector cross the local
		// X-axis vector; 'Z' x 'X'.
		localY = Vector3.Cross(localZ, localX);

		// Convert the 'Y' vector into a unit vector to obtain a vector representing the
		// beam's local Y-axis.
		localY = Vector3.Normalize(localY); // Referred to as "lY" in comments.

		// Initializing variables for the beam refernce axes vectors to
		// rotate and align beam to. These are recaLCulated as needed based on various
		// criteria checked one-by-one below.
		refX = localX;
		refY = localY;
		refZ = localZ;

		// If the beam's local X-axis is oriented in the opposite direction of the
		// expected reference axis, then the reference axes for the beam are flipped.
		if (Vector3.Dot(localX, globalX) <= -(sqrt2 / 2) ||
			Vector3.Dot(localX, globalY) <= -(sqrt2 / 2) ||
			Vector3.Dot(localX, globalZ) <= -(sqrt2 / 2))
		{
			refX = -refX;
			refY = -refY;
			refZ = -refZ;
		}

		this.rZ = refZ;

		// Assembling the rotation matrix for the beam to transform from local axes
		// to the beam's reference axes.
		this.R_lr = new Matrix4x4(
			Vector3.Dot(refX, localX), Vector3.Dot(refX, localY), Vector3.Dot(refX, localZ), 0,
			Vector3.Dot(refY, localX), Vector3.Dot(refY, localY), Vector3.Dot(refY, localZ), 0,
			Vector3.Dot(refZ, localX), Vector3.Dot(refZ, localY), Vector3.Dot(refZ, localZ), 0,
			0, 0, 0, 0
		);
		// Assembling the rotation matrix for the beam to transform from global axes
		// to the beam's reference axes.
		this.R_gr = new Matrix4x4(
			Vector3.Dot(refX, globalX), Vector3.Dot(refX, globalY), Vector3.Dot(refX, globalZ), 0,
			Vector3.Dot(refY, globalX), Vector3.Dot(refY, globalY), Vector3.Dot(refY, globalZ), 0,
			Vector3.Dot(refZ, globalX), Vector3.Dot(refZ, globalY), Vector3.Dot(refZ, globalZ), 0,
			0, 0, 0, 0
		);
	}
}

public class GeometryResult
{
	public Dictionary<int, Node> NodeDict;
	public Dictionary<int, Element> ElementDict;
	public Dictionary<int, Member> MemberDict;
	public Dictionary<int, int> _elIDMap;
	public Dictionary<int, int> _nIDMap;

	public GeometryResult()
	{
		NodeDict = new Dictionary<int, Node>();
		ElementDict = new Dictionary<int, Element>();
		MemberDict = new Dictionary<int, Member>();
	}

	public void RenumberNodes()
	{
		// Assemble list of nodes sorted by position.
		List<Node> sortedNodes = NodeDict.Values.ToList();
		sortedNodes.Sort();
		// Create node ID map to map old node ID's to new ID's
		_nIDMap = new Dictionary<int, int>();

		for (int i = 0; i < sortedNodes.Count; i++)
		{
			int newID = i + 1;
			_nIDMap[sortedNodes[i].id] = newID;
			sortedNodes[i].id = newID;
		}
		// Update NodeDict
		NodeDict = sortedNodes.ToDictionary(n => n.id);

		// Update node ID numbers in element objects
		foreach (Element e in ElementDict.Values)
			for (int i = 0; i < e.nNodes; i++)
				e.n[i].id = _nIDMap[e.n[i].id];
	}

	public void RenumberPlatesByThickness(Dictionary<string, Group> thicknessGroups)
	{
		List<string> gKeys = thicknessGroups.Keys.ToList();

		//List<Element> elsToRenumber = new List<Element>();
		// Create element ID map to map old element ID's to new ID's
		_elIDMap = new Dictionary<int, int>();
		for (int i = 0; i < gKeys.Count; i++)
		{
			List<int> gElemIDs = thicknessGroups[gKeys[i]].Ids;

			// Thickness predicates must be titled with a space after the thickness in 'ft'. e.g. '1 ft'
			float thickness = float.Parse(gKeys[i].Split(' ')[0]);
			foreach (int e in gElemIDs) ElementDict[e].t = thickness * 12; // thickness assigned in inches.
		}
		List<Element> ElementList = ElementDict.Select(e => e.Value).ToList();
		// ElementList.Sort();

		foreach (Element e in ElementList)
		{
			e.ComputeCentroid();
		}

		ElementList = ElementList
			.OrderBy(e => e.cg.Y)
			.ThenBy(e => e.cg.Z)
			.ThenBy(e => e.cg.X)
			.ThenBy(e => e.t)
			.ToList();

		for (int i = 0; i < ElementList.Count; i++)
		{
			int oldID = ElementList[i].id;
			int newID = i + 1;
			_elIDMap[oldID] = newID;
			ElementList[i].id = newID;
		}
		ElementDict = ElementList.ToDictionary(e => e.id);
	}
	public Group ReassignGroupIds(Group group)
	{
		if (group.Type == GroupTargetType.Plates)
		{
			for (int i = 0; i < group.Ids.Count; i++) { group.Ids[i] = _elIDMap[group.Ids[i]]; }
		}
		if (group.Type == GroupTargetType.Joints)
		{
			for (int i = 0; i < group.Ids.Count; i++) { group.Ids[i] = _nIDMap[group.Ids[i]]; }
		}
		return group;
	}

	// ==========================================================================================
	// ==========================================================================================
	// Everything below this point in GeometryResults should probably go somewhere else, but
	// no time to figure that out rn... (The 3 functions above this line too :) )
	// ==========================================================================================
	// ==========================================================================================
	public void SetDesignTool(
		Dictionary<string, Group> primaryGroups,
		string xlFilePath,
		string csvDirectory,
		string outDir,
		string outName,
		ElemType elType,
		Dictionary<string, Group> subsetGroups = null
	)
	{
		// ---------------------------------------------------------------------------------------
		// Assemble Subset groups, splitting by parent groups as needed for design tool
		// ---------------------------------------------------------------------------------------
		if (subsetGroups != null)
		{
			Dictionary<int, string> eToPrimaryGroup = primaryGroups
			.SelectMany(kv => kv.Value.Ids.Select(id => new { Id = id, Name = kv.Key }))
			.ToDictionary(x => x.Id, x => x.Name);

			List<Group> subsetList = new List<Group>();
			foreach (var kvp in subsetGroups)
			{
				Group grp = kvp.Value;
				subsetList.AddRange(SplitGroup(grp, eToPrimaryGroup));
			}
			subsetGroups = subsetList.ToDictionary(g => g.Name);
		}
		else if (subsetGroups == null) { subsetGroups = new Dictionary<string, Group>(); }
		// ---------------------------------------------------------------------------------------
		// ---------------------------------------------------------------------------------------
		// Establish primary file paths
		// ---------------------------------------------------------------------------------------
		string tName = Path.GetFileNameWithoutExtension(xlFilePath); // File name of template Excel
		string tExt = Path.GetExtension(xlFilePath); // File extension of template Excel
		string xlFileName = Path.GetFileName(xlFilePath);
		string baseDir = Path.GetDirectoryName(xlFilePath);
		// NOTE TO SELF: CONTINUE HERE. REVISIT HOW TO SEPARATE CSVS INTO PART GROUPS AND ASSEMBLE
		// DATA IN GROUPS
		Dictionary<int, Dictionary<string, string>> csvDict = CSVByPartNumber(csvDirectory);
		string[] csvPaths = Directory.GetFiles(csvDirectory, "*.csv");

		// ---------------------------------------------------------------------------------------
		// ---------------------------------------------------------------------------------------
		// Generate data to populate design tool (Geormetry, Subsets, Inputs, Results)
		// ---------------------------------------------------------------------------------------
		object[,] GeometryInputs = GetGeometryTab(primaryGroups);
		object[,] SubsetInputs = GetSubsetsTab(subsetGroups);
		object[,] ReinfInputs = GetInputsTab(primaryGroups, subsetGroups);
		Dictionary<string, object[,]> AnalysisData = GetAnalysisData(csvPaths, elType);
		// ---------------------------------------------------------------------------------------
		// ---------------------------------------------------------------------------------------
		// Write geometry, subsets and inputs to base copy of design tool
		// ---------------------------------------------------------------------------------------
		Console.WriteLine("Opening Design Tool Excel file");
		ExcelFile designTool = new ExcelFile(xlFileName, baseDir, false);

		designTool.OpenExcel();
		designTool.xl.Calculation = (Microsoft.Office.Interop.Excel.XlCalculation)(-4135);
		Console.WriteLine("Writing 'Geometry' tab");
		designTool.WriteBlock(designTool.GetSheetByName("Geometry"), 2, 1, GeometryInputs);
		Console.WriteLine("Writing 'Subsets' tab");
		designTool.WriteBlock(designTool.GetSheetByName("Subsets"), 3, 6, SubsetInputs);
		Console.WriteLine("Writing 'Inputs' tab");
		designTool.WriteBlock(designTool.GetSheetByName("Inputs"), 2, 21, ReinfInputs);

		designTool.xl.Calculation = (Microsoft.Office.Interop.Excel.XlCalculation)(-4105);
		designTool.CloseExcel();
		// ---------------------------------------------------------------------------------------
		// ---------------------------------------------------------------------------------------
		// Create copies of design tool and import data
		// ---------------------------------------------------------------------------------------
		// Dictionary<int, Dictionary<string, string>> csvDict
		// csvDict[part: _of_][STAAD group name] = file path to CSV file.
		int[] parts = csvDict.Keys.ToArray();

		int maxParts = parts.Max();

		// Iterate through each part to create copies of the Excel file, importing data
		// along the way
		for (int pi = 1; pi <= maxParts; pi++)
		{
			// File name of new Excel file
			string copyName = String.Format("{0}_{1}of{2}{3}", outName, pi, maxParts, tExt);
			string copyPath = Path.Combine(outDir, copyName); // full path
			File.Copy(xlFilePath, copyPath, true); // create copy

			// Get Dictionary<string, string> of CSV files keyed by STAAD group name
			Dictionary<string, string> partGroups;
			if (!csvDict.TryGetValue(pi, out partGroups)) { return; }

			var pb = new ProgressBar(partGroups.Keys.Count, 40,
				String.Format("Importing CSV data into design tool file {0} of {1}", pi, maxParts));
			// Open copy of the design tool
			designTool = new ExcelFile(copyName, outDir, false);
			designTool.OpenExcel();
			designTool.xl.Calculation = (Microsoft.Office.Interop.Excel.XlCalculation)(-4135);

			// For each group with data in this "part", import data to new sheet
			foreach (KeyValuePair<string, string> kvp in partGroups)
			{
				string grp = kvp.Key;
				string filePath = kvp.Value;
				string fileName = Path.GetFileNameWithoutExtension(filePath);

				object[,] data;
				if (!AnalysisData.TryGetValue(fileName, out data)) { continue; }

				designTool.AddSheet(grp);
				designTool.WriteBlock(designTool.GetSheetByName(grp), 1, 1, data);
				pb.Tick();
			}
			pb.Finish();
			designTool.xl.Calculation = (Microsoft.Office.Interop.Excel.XlCalculation)(-4105);
			designTool.CloseExcel(); // close file
		}
	}
	public object[,] GetGeometryTab(Dictionary<string, Group> groupDict)
	{
		// Dimensions of object[,] to be pasted into the 'Geometry' tab of the design tools
		int geomColsCount = 24;
		int geomRowsCount = 0;
		int gInp_r = 0;
		foreach (string key in groupDict.Keys)
		{
			geomRowsCount += groupDict[key].Ids.Count();
		}
		// --------------------------------------------------------

		const int c_Grp = 0;
		const int c_Plate = 1;
		const int c_nAx = 2;
		const int c_nBx = 3;
		const int c_nCx = 4;
		const int c_nDx = 5;
		const int c_nEx = 6;
		const int c_nFx = 7;
		const int c_nAy = 8;
		const int c_nBy = 9;
		const int c_nCy = 10;
		const int c_nDy = 11;
		const int c_nEy = 12;
		const int c_nFy = 13;
		const int c_nGx = 14;
		const int c_nGy = 15;
		const int c_GrpNum = 17;
		const int c_GrpName = 18;
		const int c_RebAngle = 19;
		const int c_MarkerSize = 20;
		//const int c_Plate2 = 22;
		//const int c_FinalIR = 23;

		string[] groupKeys = groupDict.Keys.ToArray();
		object[,] GeometryInputs = new object[geomRowsCount, geomColsCount];
		var pb = new ProgressBar(groupKeys.Count(), 40, "Assembling table of group plate geometry");

		for (int grp_i = 0; grp_i < groupKeys.Count(); grp_i++)
		{
			string key = groupKeys[grp_i];
			for (int el_i = 0; el_i < groupDict[key].Ids.Count(); el_i++)
			{
				int id = groupDict[key].Ids[el_i];
				Element e = this.ElementDict[id];
				e.ComputeRotationMat();
				e.ComputeCentroid();

				GeometryInputs[gInp_r, c_Grp] = grp_i + 1;
				GeometryInputs[gInp_r, c_Plate] = id;

				string axA, axB;
				if (Math.Abs(e.rZ.X) >= Math.Sqrt(2) / 2) { axA = "-Z"; axB = "Y"; }
				else if (Math.Abs(e.rZ.Y) >= Math.Sqrt(2) / 2) { axA = "X"; axB = "-Z"; }
				else { axA = "X"; axB = "Y"; }

				GeometryInputs[gInp_r, c_nAx] = _GetAxis(e.n[0].xyz, axA);
				GeometryInputs[gInp_r, c_nAy] = _GetAxis(e.n[0].xyz, axB);

				GeometryInputs[gInp_r, c_nBx] = _GetAxis(e.n[1].xyz, axA);
				GeometryInputs[gInp_r, c_nBy] = _GetAxis(e.n[1].xyz, axB);

				GeometryInputs[gInp_r, c_nCx] = _GetAxis(e.n[2].xyz, axA);
				GeometryInputs[gInp_r, c_nCy] = _GetAxis(e.n[2].xyz, axB);
				if (e.nNodes == 3)
				{
					GeometryInputs[gInp_r, c_nDx] = _GetAxis(e.n[0].xyz, axA);
					GeometryInputs[gInp_r, c_nDy] = _GetAxis(e.n[0].xyz, axB);

					GeometryInputs[gInp_r, c_nEx] = "#N/A";
					GeometryInputs[gInp_r, c_nEy] = "#N/A";
				}
				else if (e.nNodes == 4)
				{
					GeometryInputs[gInp_r, c_nDx] = _GetAxis(e.n[3].xyz, axA);
					GeometryInputs[gInp_r, c_nDy] = _GetAxis(e.n[3].xyz, axB);

					GeometryInputs[gInp_r, c_nEx] = _GetAxis(e.n[0].xyz, axA);
					GeometryInputs[gInp_r, c_nEy] = _GetAxis(e.n[0].xyz, axB);
				}
				GeometryInputs[gInp_r, c_nFx] = "#N/A";
				GeometryInputs[gInp_r, c_nFy] = "#N/A";

				GeometryInputs[gInp_r, c_nGx] = _GetAxis(e.cg, axA);
				GeometryInputs[gInp_r, c_nGy] = _GetAxis(e.cg, axB);

				// Unused for now (?)
				//GeometryInputs[grp_i, c_Plate2] = id;
				//GeometryInputs[grp_i, c_FinalIR] = 0.00;

				gInp_r++;
			}
			GeometryInputs[grp_i, c_GrpNum] = grp_i + 1; // Group number
			GeometryInputs[grp_i, c_GrpName] = key; // Group name
			GeometryInputs[grp_i, c_RebAngle] = 0; // Rebar angle always set to 0
			GeometryInputs[grp_i, c_MarkerSize] = 5; // Marker size default to 5 (?)
			pb.Tick();
		}
		pb.Finish();
		return GeometryInputs;
	}
	public object[,] GetSubsetsTab(Dictionary<string, Group> groupDict)
	{
		string[] keys = groupDict.Keys.ToArray();

		int subsColsCount = keys.Count();
		int subsRowsCount = 2;
		var pb = new ProgressBar(subsColsCount, 40, "Assembling table of element subsets");

		foreach (string k in keys)
		{
			if (groupDict[k].Ids.Count + 1 > subsRowsCount) subsRowsCount = groupDict[k].Ids.Count + 1;
		}

		object[,] SubsetInputs = new object[subsRowsCount, subsColsCount];

		for (int c = 0; c < keys.Count(); c++)
		{
			pb.Tick();
			string k = keys[c];
			SubsetInputs[0, c] = k;
			for (int r = 1; r < groupDict[k].Ids.Count + 1; r++)
			{
				SubsetInputs[r, c] = groupDict[k].Ids[r - 1];
			}
		}
		pb.Finish();
		return SubsetInputs;
	}
	public object[,] GetInputsTab(
		Dictionary<string, Group> primaryDict,
		Dictionary<string, Group> subsetDict
	)
	{
		Dictionary<string, List<string>> subsetsbyPrimary = new Dictionary<string, List<string>>();
		foreach (string pK in primaryDict.Keys) { subsetsbyPrimary[pK] = new List<string>(); }
		foreach (string sK in subsetDict.Keys)
		{
			foreach (string pK in primaryDict.Keys)
			{
				if (sK.StartsWith(pK + " ")) { subsetsbyPrimary[pK].Add(sK); break; }
			}
		}

		List<string> rows = new List<string>();
		foreach (string pK in primaryDict.Keys)
		{
			List<string> subs = subsetsbyPrimary[pK];
			if (subs.Count > 0) { rows.AddRange(subs); }
			else { rows.Add(pK + " All"); }
		}

		object[,] ReinfInputs = new object[rows.Count, 2];

		for (int grp_i = 0; grp_i < rows.Count; grp_i++)
		{
			string key = rows[grp_i];
			ReinfInputs[grp_i, 0] = grp_i + 1; // Group number
			ReinfInputs[grp_i, 1] = key; // Group name
		}
		return ReinfInputs;
	}

	public Dictionary<string, object[,]> GetAnalysisData(string[] csvPaths, ElemType elType)
	{
		if (elType == ElemType.Slab) { csvPaths = csvPaths.Where(p => p.Contains("SLAB")).ToArray(); }
		else if (elType == ElemType.Wall) { csvPaths = csvPaths.Where(p => p.Contains("WALL")).ToArray(); }

		var pb = new ProgressBar(csvPaths.Count(), 40, "Loading analysis results");
		Dictionary<string, object[,]> csvData = new Dictionary<string, object[,]>();
		foreach (string csv in csvPaths)
		{
			List<object> obj = new List<object>();
			obj = CsvReader.loadTableToObject(csv);
			csvData.Add((string)obj[0], (object[,])obj[1]);
			pb.Tick();
		}
		pb.Finish();
		return csvData;
	}

	public static List<Group> SplitGroup(
		Group composite,
		Dictionary<int, string> eToPrimary
		)
	{
		List<Group> result = new List<Group>();
		var splits = composite.Ids
			.Where(id => eToPrimary.ContainsKey(id))
			.GroupBy(id => eToPrimary[id]);

		foreach (var grp in splits)
		{
			Group sub = new Group();
			sub.Name = grp.Key + " " + composite.Name;
			sub.Type = composite.Type;
			sub.Ids = grp.ToList();
			result.Add(sub);
		}
		return result;
	}
	public Dictionary<int, Dictionary<string, string>> CSVByPartNumber(string csvDirectory)
	{
		Regex _partPattern = new Regex(@"^(?<base>.+)_(?<part>\d+)of(?<total>\d+)$",
			RegexOptions.IgnoreCase | RegexOptions.Compiled);

		Dictionary<int, Dictionary<string, string>> csvGroupings = new Dictionary<int, Dictionary<string, string>>();

		string[] csvPaths = Directory.GetFiles(csvDirectory, "*.csv");

		int maxParts = 1;
		for (int i = 0; i < csvPaths.Count(); i++)
		{
			string csvPath = csvPaths[i];
			string csvFile = Path.GetFileNameWithoutExtension(csvPath);
			Match m = _partPattern.Match(csvFile);
			string groupName;
			int part;
			int total;

			if (m.Success)
			{
				groupName = m.Groups["base"].Value;
				part = int.Parse(m.Groups["part"].Value);
				total = int.Parse(m.Groups["total"].Value);

				if (total > maxParts) maxParts = total;
			}
			else
			{
				groupName = csvFile;
				part = 1;
			}
			if (!csvGroupings.ContainsKey(part)) csvGroupings[part] = new Dictionary<string, string>();

			csvGroupings[part][groupName] = csvPath;
		}
		return csvGroupings;
	}
	private float _GetAxis(Vector3 v, string axis)
	{
		if (axis == "X") return v.X;
		if (axis == "Y") return v.Y;
		if (axis == "-Z") return -v.Z;
		return v.Z;
	}
}

public class MemForce : IComparable<MemForce>
{
	// Directly read from ANL file
	public int memID { get; set; }
	public int LC { get; set; }
	public int nodeID { get; set; }
	public float[] Ff { get; set; }

	// Derivative Values
	public CoordFrame State;    // assigned in header
	public Vector3 F;
	public Vector3 M;

	public MemForce() { }
	public MemForce(MemForce src)
	{
		memID = src.memID;
		nodeID = src.nodeID;
		LC = src.LC;
		Ff = src.Ff;
		State = src.State;
		F = src.F;
		M = src.M;
	}

	public int CompareTo(MemForce other)
	{
		if (other == null) return 1;
		// Compare by elemID first
		int result = this.memID.CompareTo(other.memID);
		// If elemID is the same, compare by node
		if (result == 0) { result = this.nodeID.CompareTo(other.nodeID); }
		// If node is the same, compare by LC
		if (result == 0) { result = this.LC.CompareTo(other.LC); }

		return result;
	}

	public void AssignMatrices()
	{
		this.F = new Vector3(Ff[0], Ff[1], Ff[2]);
		this.M = new Vector3(Ff[3], Ff[4], Ff[5]);
	}

	public void AssignFloats()
	{
		this.Ff = new float[6];
		this.Ff[0] = this.F.X; // Axial 	??
		this.Ff[1] = this.F.Y; // Shear Y 	??
		this.Ff[2] = this.F.Z; // Shear Z 	??
		this.Ff[3] = this.M.X; // Torsion 	??
		this.Ff[4] = this.M.Y; // Mom Y 	??
		this.Ff[5] = this.M.Z; // Mom Z 	??
	}

	public void TransformToReference(Dictionary<int, Member> mems)
	{
		if (this.State == CoordFrame.Reference) { return; }
		Matrix4x4 rot = (Matrix4x4)mems[this.memID].R_lr;
		// Vector3 transform with 4x4 returns a vector3
		this.F = Vector3.Transform(this.F, Matrix4x4.Transpose(rot));
		this.M = Vector3.Transform(this.M, Matrix4x4.Transpose(rot));

		AssignFloats();
		this.State = CoordFrame.Reference;
	}

	public void TransformToLocal(Dictionary<int, Member> mems)
	{
		//TODO
		throw new NotImplementedException();
	}
}

public class StressRecord : IComparable<StressRecord>, IEquatable<StressRecord>
{
	// Directly read from ANL file
	public int elemID;
	public int node;
	public int LC;
	public object modelID;
	public float[] Sf; //local or reference based on state enum at top of file

	// Derivatative values
	public CoordFrame State;    // assigned in header
	public float t;             // Used by section cut
	public Matrix4x4 S;         // assigned by helper function
	public Matrix4x4 M;

	public StressRecord() { }
	public StressRecord(StressRecord src)
	{
		elemID = src.elemID;
		node = src.node;
		LC = src.LC;
		modelID = src.modelID;
		Sf = src.Sf;
		State = src.State;
		t = src.t;
		S = src.S;
		M = src.M;
	}

	// Returns a copy of Sf converted to the requested force/length units
	public float[] StressView(ForceUnit toF, LengthUnit toL)
	{
		float[] v = new float[this.Sf.Length];

		// Force per unit thickness per unit length, both length units determined by the
		//	print block; thickness units not relevant here
		v[(int)SC.Sx] = (float)UnitConvert.StressFromCanonical(Sf[(int)SC.Sx], toF, toL);
		v[(int)SC.Sy] = (float)UnitConvert.StressFromCanonical(Sf[(int)SC.Sy], toF, toL);
		v[(int)SC.Sxy] = (float)UnitConvert.StressFromCanonical(Sf[(int)SC.Sxy], toF, toL);
		v[(int)SC.SQx] = (float)UnitConvert.StressFromCanonical(Sf[(int)SC.SQx], toF, toL);
		v[(int)SC.SQy] = (float)UnitConvert.StressFromCanonical(Sf[(int)SC.SQy], toF, toL);

		// Force - length per unit length, i.e. only a force conversion
		v[(int)SC.Mx] = (float)UnitConvert.ForceFromCanonical(Sf[(int)SC.Mx], toF);
		v[(int)SC.My] = (float)UnitConvert.ForceFromCanonical(Sf[(int)SC.My], toF);
		v[(int)SC.Mxy] = (float)UnitConvert.ForceFromCanonical(Sf[(int)SC.Mxy], toF);

		return v;
	}

	// Returns a copy of Sf converted to the requested force/length units
	// 	Multiplied through by thickness
	public float[] StressViewForce(ForceUnit toF, LengthUnit toL)
	{
		float[] v = new float[this.Sf.Length];

		// Force per unit length
		// originally lb / in thickness / in length
		//	if we want the viewer kip / ft length
		//	we need to convert to kip / ft thickness / ft length
		//	then multiply by thickness / 12.0 to get kip / ft length since thickness is inches
		//  but to be generic, it should just be UnitConvert.LengthFromCanonical(t, toF, toL)

		float t_toL = (float)UnitConvert.LengthFromCanonical(t, toL); // ft typically
		v[(int)SC.Sx] = (float)UnitConvert.StressFromCanonical(Sf[(int)SC.Sx] * t_toL, toF, toL);
		v[(int)SC.Sy] = (float)UnitConvert.StressFromCanonical(Sf[(int)SC.Sy] * t_toL, toF, toL);
		v[(int)SC.Sxy] = (float)UnitConvert.StressFromCanonical(Sf[(int)SC.Sxy] * t_toL, toF, toL);
		v[(int)SC.SQx] = (float)UnitConvert.StressFromCanonical(Sf[(int)SC.SQx] * t_toL, toF, toL);
		v[(int)SC.SQy] = (float)UnitConvert.StressFromCanonical(Sf[(int)SC.SQy] * t_toL, toF, toL);

		// Force - length per unit length, i.e. only a force conversion
		v[(int)SC.Mx] = (float)UnitConvert.ForceFromCanonical(Sf[(int)SC.Mx], toF);
		v[(int)SC.My] = (float)UnitConvert.ForceFromCanonical(Sf[(int)SC.My], toF);
		v[(int)SC.Mxy] = (float)UnitConvert.ForceFromCanonical(Sf[(int)SC.Mxy], toF);

		return v;
	}

	public int CompareTo(StressRecord other)
	{
		if (other == null) return 1;
		// Compare by elemID first
		int result = this.elemID.CompareTo(other.elemID);
		// If elemID is the same, compare by node
		if (result == 0) { result = this.node.CompareTo(other.node); }
		// If node is the same, compare by LC
		if (result == 0) { result = this.LC.CompareTo(other.LC); }

		return result;
	}
	// Implement the Equals method from IEquatable<StressRecord>
	public bool Equals(StressRecord other)
	{
		if (other == null) return false;
		if (ReferenceEquals(this, other)) return true;

		// Defining what makes two StressRecords "equal"
		return elemID == other.elemID && node == other.node &&
			   Equals(modelID, other.modelID) && LC == other.LC && S == other.S &&
			   M == other.M;
	}

	// Override GetHashCode
	public override int GetHashCode()
	{
		unchecked
		{
			int hash = 17;
			hash = hash * 31 + elemID.GetHashCode();
			hash = hash * 31 + node.GetHashCode();
			hash = hash * 31 + (modelID != null ? modelID.GetHashCode() : 0);
			hash = hash * 31 + LC.GetHashCode();
			hash = hash * 31 + S.GetHashCode();
			hash = hash * 31 + M.GetHashCode();
			return hash;
		}
		// HashCode.Combine handles nulls and mixes hash codes efficiently
		//return HashCode.Combine(elemID, node, modelID, LC, S, M);
	}

	public static StressRecord Lerp(
	StressRecord s1,
	StressRecord s2,
	float t,
	int nID)
	{
		StressRecord resStr = new StressRecord(s1);

		resStr.S = Matrix4x4.Lerp(s1.S, s2.S, t);
		resStr.M = Matrix4x4.Lerp(s1.M, s2.M, t);
		resStr.node = nID;

		resStr.Sf[0] = resStr.S.M11;
		resStr.Sf[1] = resStr.S.M12;
		resStr.Sf[2] = resStr.S.M13;
		resStr.Sf[3] = resStr.S.M22;
		resStr.Sf[4] = resStr.S.M23;
		resStr.Sf[5] = resStr.M.M11;
		resStr.Sf[6] = resStr.M.M12;
		resStr.Sf[7] = resStr.M.M22;

		return resStr;
	}

	public void AssignMatrices()
	{
		this.S = new Matrix4x4(this.Sf[0], this.Sf[1], this.Sf[2], 0,
								this.Sf[1], this.Sf[3], this.Sf[4], 0,
								this.Sf[2], this.Sf[4], 0, 0,
								0, 0, 0, 0);

		this.M = new Matrix4x4(this.Sf[5], this.Sf[6], 0, 0,
								this.Sf[6], this.Sf[7], 0, 0,
								0, 0, 0, 0,
								0, 0, 0, 0);
	}

	public void AssignFloats()
	{
		this.Sf = new float[8];
		this.Sf[0] = this.S.M11; //Sx 	(Axial)
		this.Sf[1] = this.S.M12; //Sxy 	(IP)
		this.Sf[2] = this.S.M13; //SQx	(OOP)
		this.Sf[3] = this.S.M22; //Sy
		this.Sf[4] = this.S.M23; //SQy
		this.Sf[5] = this.M.M11; //Mx
		this.Sf[6] = this.M.M12; //Mxy
		this.Sf[7] = this.M.M22; //My
	}

	public void TransformToReference(Dictionary<int, Element> elems)
	{
		if (this.State == CoordFrame.Reference) { return; }
		Matrix4x4 rot = (Matrix4x4)elems[this.elemID].R_lr;
		this.S = rot * this.S * Matrix4x4.Transpose(rot);
		this.M = rot * this.M * Matrix4x4.Transpose(rot);
		AssignFloats();
		this.State = CoordFrame.Reference;
	}

	public void TransformToLocal(Dictionary<int, Element> elems)
	{
		if (State == CoordFrame.Local) { return; }
		Matrix4x4 rot = (Matrix4x4)elems[this.elemID].R_lr;
		this.S = Matrix4x4.Transpose(rot) * this.S * rot;
		this.M = Matrix4x4.Transpose(rot) * this.M * rot;
		AssignFloats();
		this.State = CoordFrame.Local;
	}
}

public class Disp : IComparable<Disp>
{
	public int node;
	public int LC;
	public float[] DR; //Combined displacements and rotations for fast read/write
	// dx, dy, dz, rx, ry, rz

	public Disp() { }
	public Disp(Disp src)
	{
		node = src.node;
		LC = src.LC;
		DR = src.DR;
	}

	public Disp Copy()
	{
		Disp c = (Disp)this.MemberwiseClone();
		c.DR = (float[])this.DR.Clone();
		return c;
	}


	public int CompareTo(Disp other)
	{
		if (other == null) return 1;
		// Compare by node first
		int result = this.node.CompareTo(other.node);
		// If node is the same, compare by LC
		if (result == 0) { result = this.LC.CompareTo(other.LC); }
		return result;
	}

}

public class DsrRecord
{
	// Used for RawViewerWriter's composite key
	public int elemID;
	public int node;
	public int LC;

	// Keyed to named indices via DSR enum
	public float[] Values;
}

// One capacity record: per element corner, no load-case dimension.
// Values[] order matches the capacityComponents list passed to the
// constructor (build it from the same enums as the DSR components).
public class StrRecord
{
	public int elemID;
	public int node;
	public float[] Values;

	public StrRecord(int id, int nid, float[] vals)
	{
		this.elemID = id;
		this.node = nid;
		this.Values = vals;
	}
}

// Rebar configuration properties
public class ElemProperties
{
	// Values from excel
	public readonly int FlexBarX;
	public readonly int FlexBarY;
	public readonly int ShearBarX;
	public readonly int ShearBarY;

	public readonly double Fx_spacing;
	public readonly double Fy_spacing;
	public readonly double Vx_spacing;
	public readonly double Vy_spacing;

	public readonly double CC;  // inches cover

	// Calculated values
	public readonly double FlexSteelAreaX; // All areas on a per foot basis
	public readonly double FlexSteelAreaY;
	public readonly double ShearSteelAreaX;
	public readonly double ShearSteelAreaY;

	// Design strength calculator (assigned outside of this class)
	public ElemStrength Strengths;

	// spacing in inches
	public ElemProperties(
		int flexBarX, double fx_spacing,
		int flexBarY, double fy_spacing,
		int shearBarX, double vx_spacing,
		int shearBarY, double vy_spacing,
		double cc)
	{
		this.FlexBarX = flexBarX; this.Fx_spacing = fx_spacing;
		this.FlexBarY = flexBarY; this.Fy_spacing = fy_spacing;
		this.ShearBarX = shearBarX; this.Vx_spacing = vx_spacing;
		this.ShearBarY = shearBarY; this.Vy_spacing = vy_spacing;

		this.CC = cc;
		// Ternary to block divide by zero when # is zero / spacing may be zero
		this.FlexSteelAreaX = (flexBarX != 0.0) ? RebarTable.AreaForBar(flexBarX) * 12.0 / fx_spacing : 0.0;
		this.FlexSteelAreaY = (flexBarY != 0.0) ? RebarTable.AreaForBar(flexBarY) * 12.0 / fy_spacing : 0.0;
		this.ShearSteelAreaX = (shearBarX != 0.0) ? RebarTable.AreaForBar(shearBarX) * 12.0 / vx_spacing : 0.0;
		this.ShearSteelAreaY = (shearBarY != 0.0) ? RebarTable.AreaForBar(shearBarY) * 12.0 / vy_spacing : 0.0;
	}


	// Calculate depth here:
	//	N/S slab steel outer (Y dir: h - cc - 0.5x bar diam, X dir: h - cc - 1.5x bar diam)
	//	Horiz wall steel outer (X dir: h - cc - 0.5x bar diam, Y dir: h - cc - 1.5x bar diam)
	//  Supply clear cover in spreadsheet
	public double Dx(double thickness, ElemType elType)
	{
		switch (elType)
		{
			case (ElemType.Slab):
				return thickness - CC - RebarTable.DiameterForBar(FlexBarY) -
					0.5 * RebarTable.DiameterForBar(FlexBarX);

			case (ElemType.Wall):
				return thickness - CC - 0.5 * RebarTable.DiameterForBar(FlexBarX);

			default:
				throw new ArgumentException("Invalid element type supplied: " + elType);
		}
	}

	public double Dy(double thickness, ElemType elType)
	{
		switch (elType)
		{
			case (ElemType.Slab):
				return thickness - CC - 0.5 * RebarTable.DiameterForBar(FlexBarY);

			case (ElemType.Wall):
				return thickness - CC - RebarTable.DiameterForBar(FlexBarX) -
					0.5 * RebarTable.DiameterForBar(FlexBarY);

			default:
				throw new ArgumentException("Invalid element type supplied: " + elType);
		}
	}
}

// ASTM Standard bar areas (in2) keyed by bar number, fixed reference data
public static class RebarTable
{
	private static readonly Dictionary<int, double> areaByBar = BuildAreaTable();
	private static readonly Dictionary<int, double> diameterByBar = BuildDiameterTable();

	private static Dictionary<int, double> BuildAreaTable()
	{
		Dictionary<int, double> d = new Dictionary<int, double>();
		d.Add(0, 0.00); // Used for when we don't supply rebar (i.e. ties)
		d.Add(3, 0.11);
		d.Add(4, 0.20);
		d.Add(5, 0.31);
		d.Add(6, 0.44);
		d.Add(7, 0.60);
		d.Add(8, 0.79);
		d.Add(9, 1.00);
		d.Add(10, 1.27);
		d.Add(11, 1.56);
		d.Add(14, 2.25);
		d.Add(18, 4.00);
		return d;
	}

	private static Dictionary<int, double> BuildDiameterTable()
	{
		Dictionary<int, double> d = new Dictionary<int, double>();
		d.Add(0, 0.0);
		d.Add(3, 0.375);
		d.Add(4, 0.500);
		d.Add(5, 0.625);
		d.Add(6, 0.750);
		d.Add(7, 0.875);
		d.Add(8, 1.000);
		d.Add(9, 1.128);
		d.Add(10, 1.270);
		d.Add(11, 1.410);
		d.Add(14, 1.693);
		d.Add(18, 2.257);
		return d;
	}

	public static double AreaForBar(int barNumber)
	{
		double area;
		if (!areaByBar.TryGetValue(barNumber, out area))
		{
			throw new ArgumentException(string.Format(
				"Unknown rebar number: {0}", barNumber));
		}
		return area;
	}

	public static double DiameterForBar(int barNumber)
	{
		double dia;
		if (!diameterByBar.TryGetValue(barNumber, out dia))
		{
			throw new ArgumentException(string.Format(
				"Unknown rebar number: {0}", barNumber));
		}
		return dia;
	}
}

// Converter for normalizing units during ANL read pass
public static class UnitConvert
{
	// raw length to inches (TODO generalize)
	public static double ToInch(double raw, LengthUnit fromUnit)
	{
		return raw * LengthFactorToIn(fromUnit);
	}

	// raw force to lb (parse path)
	public static double ToLb(double raw, ForceUnit force)
	{
		return Force(raw, force, ForceUnit.Pound);
	}

	public static double Force(double raw, ForceUnit fromF, ForceUnit toF)
	{
		return raw * ForceFactorToLb(fromF) / ForceFactorToLb(toF);
	}

	public static double ForceFromCanonical(double raw, ForceUnit toF)
	{
		return Force(raw, ForceUnit.Pound, toF);
	}

	public static double Length(double raw, LengthUnit fromL, LengthUnit toL)
	{
		return raw * LengthFactorToIn(fromL) / LengthFactorToIn(toL);
	}

	public static double LengthFromCanonical(double raw, LengthUnit toL)
	{
		return Length(raw, LengthUnit.Inch, toL);
	}

	// General: from any units to any units (for stresses specifically)
	public static double Stress(double raw,
								ForceUnit fromF, LengthUnit fromL,
								ForceUnit toF, LengthUnit toL)
	{
		double fromFactor = ForceFactorToLb(fromF) / Sq(LengthFactorToIn(fromL));
		double toFactor = ForceFactorToLb(toF) / Sq(LengthFactorToIn(toL));
		return raw * fromFactor / toFactor;
	}

	// stress = force/length^2
	public static double ToStress(double raw, ForceUnit force, LengthUnit length)
	{
		return Stress(raw, force, length, ForceUnit.Pound, LengthUnit.Inch);
	}

	public static double StressFromCanonical(double raw, ForceUnit toF, LengthUnit toL)
	{
		return Stress(raw, ForceUnit.Pound, LengthUnit.Inch, toF, toL);
	}

	private static double Sq(double x) { return x * x; }

	public static void ConvertStressRecord(StressRecord rec, ForceUnit force, LengthUnit length)
	{
		// Force per unit thickness per unit length, both length units determined by the
		//	print block; thickness units not relevant here
		rec.Sf[(int)SC.Sx] = (float)ToStress(rec.Sf[(int)SC.Sx], force, length);
		rec.Sf[(int)SC.Sy] = (float)ToStress(rec.Sf[(int)SC.Sy], force, length);
		rec.Sf[(int)SC.Sxy] = (float)ToStress(rec.Sf[(int)SC.Sxy], force, length);
		rec.Sf[(int)SC.SQx] = (float)ToStress(rec.Sf[(int)SC.SQx], force, length);
		rec.Sf[(int)SC.SQy] = (float)ToStress(rec.Sf[(int)SC.SQy], force, length);

		// Force - length per unit length, i.e. only a force conversion
		rec.Sf[(int)SC.Mx] = (float)ToLb(rec.Sf[(int)SC.Mx], force);
		rec.Sf[(int)SC.My] = (float)ToLb(rec.Sf[(int)SC.My], force);
		rec.Sf[(int)SC.Mxy] = (float)ToLb(rec.Sf[(int)SC.Mxy], force);
	}

	public static void ConvertForceRecord(MemForce rec, ForceUnit force, LengthUnit length)
	{
		Console.WriteLine("WARNING: MEMBER FORCE UNIT NORMALIZATION NOT SUPPORTED");
	}

	private static double LengthFactorToIn(LengthUnit u)
	{
		switch (u)
		{
			case LengthUnit.Inch: return 1.0;
			case LengthUnit.Foot: return 12.0;
			case LengthUnit.Meter: return 39.37;
			case LengthUnit.Millimeter: return 0.03937;
			case LengthUnit.Centimeter: return 0.3937;
			default:
				throw new ArgumentException("Unhandled length unit" + u);
		}
	}

	private static double ForceFactorToLb(ForceUnit u)
	{
		switch (u)
		{
			case ForceUnit.Pound: return 1.0;
			case ForceUnit.Kip: return 1000.0;
			case ForceUnit.Newton: return 0.2248;
			case ForceUnit.KiloNewton: return 224.8089;
			case ForceUnit.MetricTon: return 2204.6226;
			default:
				throw new ArgumentException("Unhandled force unit" + u);
		}
	}
}

//Class for holding model meta data for json dict write
public class ModelMeta
{
	public string Name;
	public Dictionary<int, string> LoadCaseNames;
	public int StressStartIndex;
}

// Class for holding a group of members and querying forces at a given offset from end
public class ColumnStack
{
	public float X;
	public float Z;
	public List<int> MemberIds;
	public int GridRow;
	public int GridCol;

	public Dictionary<int, Member> _members;
	private float _totalHeight;
	private float _baseY;
	private float _topY;

	// TODO: Add to SectionCuts.cs ColumnGrouper class as helper
	// forces[memberId][nodeId][lcId] = float[6]
	public Dictionary<int, Dictionary<int, Dictionary<int, float[]>>> _forces;

	public float TotalHeight { get { return _totalHeight; } }
	public float BaseY { get { return _baseY; } }
	public float TopY { get { return _topY; } }

	public ColumnStack() { }

	public void PopulateMembers(Dictionary<int, Member> allMembers)
	{
		_members = new Dictionary<int, Member>();
		float minY = float.MaxValue;
		float maxY = float.MinValue;

		for (int i = 0; i < MemberIds.Count; i++)
		{
			Member mem = allMembers[MemberIds[i]];
			_members[MemberIds[i]] = mem;

			float y0 = mem.n[0].xyz.Y;
			float y1 = mem.n[1].xyz.Y;
			if (y0 < minY) minY = y0;
			if (y1 < minY) minY = y1;
			if (y0 > maxY) maxY = y0;
			if (y1 > maxY) maxY = y1;
		}

		_baseY = minY;
		_topY = maxY;
		_totalHeight = maxY - minY;

		SortMembersByY();
	}

	public void PopulateForces(Dictionary<int, Dictionary<int, Dictionary<int, float[]>>> forces)
	{
		_forces = forces;
	}

	public float[] GetForcesFromTop(float dist, int loadCase)
	{
		return GetForcesAtY(_topY - dist, loadCase);
	}

	public float[] GetForcesFromBot(float dist, int loadCase)
	{
		return GetForcesAtY(_baseY + dist, loadCase);
	}

	private float[] GetForcesAtY(float queryY, int loadCase)
	{
		//Clamp to top and bottom
		if (queryY < _baseY) queryY = _baseY;
		if (queryY > _topY) queryY = _topY;

		// Find which element this lands on
		for (int i = 0; i < MemberIds.Count; i++)
		{
			Member mem = _members[MemberIds[i]];

			//Defend against flipped members with the power of ternary
			bool zeroIsBot = mem.n[0].xyz.Y < mem.n[1].xyz.Y;
			Node botNode = zeroIsBot ? mem.n[0] : mem.n[1];
			Node topNode = zeroIsBot ? mem.n[1] : mem.n[0];

			if (queryY < botNode.xyz.Y || queryY > topNode.xyz.Y) continue;

			//Defend against zero height members by assigning zero to the interpolation location t
			float memHeight = topNode.xyz.Y - botNode.xyz.Y;
			float t = (memHeight > 0.0001f) ? (queryY - botNode.xyz.Y) / memHeight : 0f;

			// Look up forces
			Dictionary<int, Dictionary<int, float[]>> lcDict;
			if (!_forces.TryGetValue(MemberIds[i], out lcDict)) return null; // No force data for this member

			Dictionary<int, float[]> nodeDict;
			if (!lcDict.TryGetValue(loadCase, out nodeDict)) return null; //No force data for this load case

			float[] botF;
			float[] topF;
			if (!nodeDict.TryGetValue(botNode.id, out botF)) return null; // No force data for this node
			if (!nodeDict.TryGetValue(topNode.id, out topF)) return null; // No force data for this node

			float[] result = new float[6];
			for (int j = 0; j < 6; j++)
			{
				float botVal = -botF[j];
				float topVal = topF[j];
				result[j] = botVal * (1f - t) + topVal * t;
			}
			return result;
		}
		return null;
	}

	private void SortMembersByY()
	{
		for (int i = 1; i < MemberIds.Count; i++)
		{
			int idKey = MemberIds[i];
			float yKey = Math.Min(_members[idKey].n[0].xyz.Y, _members[idKey].n[1].xyz.Y);
			int j = i - 1;
			while (j >= 0 && Math.Min(_members[MemberIds[j]].n[0].xyz.Y,
									_members[MemberIds[j]].n[1].xyz.Y) > yKey)
			{
				MemberIds[j + 1] = MemberIds[j];
				j--;
			}
			MemberIds[j + 1] = idKey;
		}
	}
}

// =======================================================================================
// Element edge class storing data for edges of an element in relation to a section cut
// being processed. This includes the element which the edge corresponds to,
// the two nodes comprising the edge, the direction vector 'b' from node 0 to node 1 of
// the edge, and the interpolation scalar factor 't' caLCulated depending on the section
// cut line being processed. The interpolation factor represents the point at which the
// element edge and the section cut line intersect, if at all.
// ---------------------------------------------------------------------------------------
public class ElEdge
{
	public int elemID;
	public Element e;
	public Node[] n;
	public Vector3 b;
	public float[] t;

	//	// Constructor for an empty element edge instance
	//	public ElEdge() { }
}

// =======================================================================================
// Segment class storing data associated with an arbitrary straight line segment through
// a plate element corresponding to a section cut being processed. Data includes the
// element object which is being crossed, two node objects representing either end of the
//line segment, and stress and moment data at either end of the line segment by load
// case.
// ---------------------------------------------------------------------------------------
public class Segment
{
	public int elemID; // ID of element which is being crossed
	public Element element; // Element object for easy reference
	public Node p1; // Fake node 1 along first edge of element
	public Node p2; // Fake node 2 along second edge of element
	public int LC; // Load combination
	public object modelID;
	public string units;
	public Matrix4x4 S1; // Stresses at fake node 1
	public Matrix4x4 S2; // Stresses at fake node 2
	public Matrix4x4 M1; // Moments at fake node 1
	public Matrix4x4 M2; // Moments at fake node 2

	// Constructor for an empty segment instance
	public Segment() { }

	// Constructor used to assemble a line segment corresponding to a section cut taken
	// through an element. The inputs it takes are two element edge objects representing
	// the two edges of the element crossed by the section cut line, and four element
	// stress objects corresponding to the element stress data available at each node
	// of the two element edges.
	public void InterpolateStresses(ElEdge sectEdge, List<StressRecord> elStrs, string units)
	{
		//ElEdge ee1 = elEdges[0];
		//ElEdge ee2 = elEdges[1];

		StressRecord s1 = elStrs[0];
		StressRecord s2 = elStrs[1];

		StressRecord sNew = StressRecord.Lerp(s1, s2, sectEdge.t[1], 2);

		//StressRecord s3 = elStrs[2];
		//StressRecord s4 = elStrs[3];

		this.elemID = sectEdge.elemID;
		this.element = sectEdge.e;

		// p1 is calculated as the linear interpolation between the two position vectors
		// of each node on the first element edge at point 't', where the section cut
		// line intersects the element edge.
		this.p1 = sectEdge.n[0];
		//this.p1.id = 1;
		//this.p1.xyz = Vector3.Lerp(ee1.n[0].xyz, ee1.n[1].xyz, ee1.t[0]);

		// p2 is calculated as the linear interpolation between the two position vectors
		// of each node on the second element edge at point 't', where the section cut
		// line intersects the element edge.
		this.p2 = sectEdge.n[1];
		//this.p2.id = 2;
		//this.p2.xyz = Vector3.Lerp(ee2.n[0].xyz, ee2.n[1].xyz, ee2.t[0]);
		// NOTE TO SELF: PASS MODELID AND UNITS AS ARGS TO CONSTRUCTOR TO HARD CODE FOR NOW.
		this.LC = s1.LC;
		this.modelID = s1.modelID;
		this.units = units;

		// Sig1 is calculated as the linear interpolation between the stress tensors at
		// each node on the first element edge at point 't'.
		//this.S1 = Matrix4x4.Lerp(s1.S, s2.S, sectEdge.t[0]);
		this.S1 = s1.S;

		// Sig2 is calculated as the linear interpolation between the stress tensors at
		// each node on the second element edge at point 't'.
		//this.S2 = Matrix4x4.Lerp(s1.S, s2.S, sectEdge.t[1]);
		this.S2 = sNew.S;

		// Mom1 is calculated as the linear interpolation between the moment matrix at
		// each node on the first element edge at point 't'.
		//this.M1 = Matrix4x4.Lerp(s1.M, s2.M, sectEdge.t[0]);
		this.M1 = s1.M;

		// Mom2 is calculated as the linear interpolation between the moment matrix at
		// each node on the second element edge at point 't'.
		//this.M2 = Matrix4x4.Lerp(s1.M, s2.M, sectEdge.t[1]);
		this.M2 = sNew.M;
	}
}

public static class UnitUtils
{
	// =============================================================================
	// UNIT HELPERS
	// =============================================================================
	// â”€â”€ Length-unit conversion â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
	private static float _ToMeters(string unit)
	{
		if (unit == "mm") return 0.001f;
		if (unit == "cm") return 0.01f;
		if (unit == "in") return 0.0254f;
		if (unit == "ft") return 0.3048f;
		if (unit == "m") return 1.0f;
		throw new System.ArgumentException("Unknown length unit: " + unit);
	}

	/// <summary>
	/// Returns the factor k such that  value_in_fromUnit Ã— k = value_in_toUnit.
	/// e.g. LengthConversionFactor("in","ft") = 1/12 â‰ˆ 0.0833
	///      LengthConversionFactor("ft","in") = 12
	///      LengthConversionFactor("m","mm")  = 1000
	/// Returns 1 when fromUnit == toUnit.
	/// </summary>
	public static float LengthConversionFactor(string fromUnit, string toUnit)
	{
		if (fromUnit == toUnit) return 1f;
		return _ToMeters(fromUnit) / _ToMeters(toUnit);
	}
}

public struct UnitSystem
{
	public string ForceUnit;
	public string LengthUnit;
	public string ThickUnit;

	// â”€â”€ Unit label strings â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
	/// <summary>
	/// Stress = force / lengthÂ² label.
	/// Returns named forms for common structural unit systems (psi, ksi, kPa â€¦),
	/// falls back to "F/L^2" notation for others.
	/// </summary>

	//	public UnitSystem() { }
	public UnitSystem(string fu, string lu, string tu)
	{
		ForceUnit = fu; LengthUnit = lu; ThickUnit = tu;
	}
	public string StressLabel()
	{
		if (ForceUnit == "lbf" && LengthUnit == "in") return "psi";
		if (ForceUnit == "lbf" && LengthUnit == "ft") return "psf";
		if (ForceUnit == "kip" && LengthUnit == "in") return "ksi";
		if (ForceUnit == "kip" && LengthUnit == "ft") return "ksf";
		if (ForceUnit == "N" && LengthUnit == "m") return "Pa";
		if (ForceUnit == "N" && LengthUnit == "mm") return "MPa";
		if (ForceUnit == "kN" && LengthUnit == "m") return "kPa";
		if (ForceUnit == "kN" && LengthUnit == "mm") return "GPa";
		return ForceUnit + "/" + LengthUnit + "^2";
	}

	/// <summary>
	/// Moment per unit width label  (e.g. "lbf-in/in", "kip-ft/ft", "kN-m/m").
	/// This is the unit STAAD reports for Mx, My, Mxy.
	/// </summary>
	public string MomentPerWidthLabel()
	{
		return ForceUnit + "-" + LengthUnit + "/" + LengthUnit;
	}

	/// <summary>
	/// Resultant section force label  (same as ForceUnit, e.g. "kip").
	/// </summary>
	public string ForceLabel()
	{
		return ForceUnit;
	}

	/// <summary>
	/// Resultant section moment label  (e.g. "lbf-in", "kip-ft", "kN-m").
	/// </summary>
	public string MomentLabel()
	{
		return ForceUnit + "-" + LengthUnit;
	}

}