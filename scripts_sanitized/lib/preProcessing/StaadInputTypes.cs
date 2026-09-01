using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Linq;
using System.Linq.Expressions;
using System.Text.RegularExpressions;
using System.Numerics;

// Helper class for holding soil spring data
public class SoilSpringRegion
{
	public string Name;
	public List<int> NodeIds;
	public Dictionary<int, double> AreaByNode;
	public double Kh;   // Horizontal stiffness per unit area (applied to KFX and KFZ)
	public double Kv;   // Vertical stiffness per unit area (applied to KFY)

	public SoilSpringRegion(string name, List<int> nodeIds, Dictionary<int, double> areaByNode, double kh, double kv)
	{
		this.Name = name;
		this.NodeIds = nodeIds;
		this.AreaByNode = areaByNode;
		this.Kh = kh;
		this.Kv = kv;
	}
}

public class TrenchGeometry
{
	public string Name;
	public int LoadCase;
	public double X;
	public double Z;
	public double Theta; // degrees, measured from due East
	
	public TrenchGeometry(string name, int loadCase, double x, double z, double theta)
	{
		this.Name = name;
		this.LoadCase = loadCase;
		this.X = x;
		this.Z = z;
		this.Theta = theta;
	}
}

public enum ModelArchetype
{
	RSA_MASS,
	PSDS,
	PSDS_STATIC,
	TMI,
	SEIS,
	SEIS_TORS
}

public enum SubgradeType
{
	LB,
	BE,
	UB
}

public class TorsionLoad
{
	public string id;
	public string title;
	public List<MemberLoadTrap> data;
}

public class MemberLoadTrap 
{
	public int memberId;
	public string dir;
	public float F0;
	public float F1;
}

public class ReferenceLoad
{
	public string Name; 		// R1
	public string GroupName; 	// DL_Roofing System
	public string Load; 		// value
	public string Units; 		// Kip / ksi
	public string Direction; 	// GY
	public GroupTargetType Type;//Plates / joints
	
	public ReferenceLoad(string name, string groupName, string load, 
					string units, string direction, GroupTargetType type)
	{
		this.Name = name;
		this.GroupName = groupName;
		this.Load = load;
		this.Units = units;
		this.Direction = direction;
		this.Type = type;
	}
}

public class Curve
{
	private readonly double X0;
	private readonly double Z0;
	private readonly double Theta;
	private readonly double Rad;
	
	public Curve(double x0, double z0, double theta)
	{
		X0 = x0;
		Z0 = z0;
		Theta = theta;
		Rad = theta * Math.PI / 180.0;
	}
	
	public double Eval_Liquifaction(double x, double z)
	{
		double dist = Dist(x,z);
		if (dist > (0)) // sanitized
		{
			return 0;
		}
		else
		{
			double liq_disp = 0; // sanitized
			return liq_disp;
		}
	}
	
	public double Eval_SoftZone(double x, double z)
	{
		double dist = Dist(x,z);
		
		double i = 0; // sanitized
		double soft_disp = 0; // sanitized
		return soft_disp;
	}
	
	public double Dist(double x, double z)
	{
		double dx = x - X0;
		double dz = z - Z0;
		double dist = Math.Abs(dx * Math.Cos(Rad) - dz * Math.Sin(Rad));
		return dist;
	}
}
