using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

// PipeMaterials  --  the material data a CAESAR model does not carry, for the SAP / STAAD check model (PipeToFrame,
// 2026-11-12). A CAESAR element names a material number and takes its E, density and thermal expansion from CAESAR's
// database; the .cii holds them only where the input overrode them (and never the expansion). The check model needs
// them, so:
//   - the CAESAR workbook's Materials sheet holds them, one row per material the job uses (PipeModel.Materials);
//   - a material it does not list (a .cii read directly, a workbook from before the sheet) takes this file's
//     default set, by the material's class, and PipeToFrame warns that it did.
// The defaults are typical published values at room temperature, alpha the mean coefficient from 70 F to about
// 250 F: a starting point to check against CAESAR's material database, never the job's values. English units: psi,
// lb/cu.in., in/in/F. C# 5, one global batch (prefix Pipe).

public class PipeModelMaterial
{
    public int Number;                        // CAESAR material number
    public double E;                          // cold modulus (job modulus units)
    public double Poisson;
    public double Density;                    // pipe density (job pipe-density units)
    public double Alpha;                      // mean thermal expansion coefficient, per job temperature degree
    public string Note = "";                  // where the values came from (the Materials sheet's Note)

    public PipeModelMaterial Copy() { return new PipeModelMaterial { Number = Number, E = E, Poisson = Poisson, Density = Density, Alpha = Alpha, Note = Note }; }

    public bool SameAs(PipeModelMaterial o)
    {
        return o != null && Number == o.Number && E == o.E && Poisson == o.Poisson && Density == o.Density && Alpha == o.Alpha && (Note ?? "") == (o.Note ?? "");
    }
}

public static class PipeMaterialDefaults
{
    // a class: its name, then E (psi), Poisson, density (lb/cu.in.), alpha (in/in/F)
    sealed class Cls
    {
        public string Name; public double E, Nu, Rho, Alpha;
        public Cls(string n, double e, double nu, double rho, double a) { Name = n; E = e; Nu = nu; Rho = rho; Alpha = a; }
    }

    static readonly Cls Carbon = new Cls("carbon steel", 29.4e6, 0.3, 0.283, 6.5e-6);
    static readonly Cls CarbonMoly = new Cls("carbon-moly steel", 29.2e6, 0.3, 0.283, 6.5e-6);
    static readonly Cls LowChrome = new Cls("low chrome-moly steel", 29.7e6, 0.3, 0.283, 6.6e-6);
    static readonly Cls MedChrome = new Cls("5-9 chrome-moly steel", 30.9e6, 0.3, 0.283, 6.3e-6);
    static readonly Cls Austenitic = new Cls("austenitic stainless steel", 28.3e6, 0.3, 0.289, 9.2e-6);
    static readonly Cls Chromium = new Cls("straight chromium stainless steel", 29.2e6, 0.3, 0.280, 5.9e-6);
    static readonly Cls WroughtIron = new Cls("wrought iron", 27.0e6, 0.3, 0.280, 6.9e-6);
    static readonly Cls CastIron = new Cls("grey cast iron", 13.4e6, 0.21, 0.260, 5.9e-6);
    static readonly Cls Monel = new Cls("nickel-copper (Monel)", 26.0e6, 0.32, 0.319, 7.8e-6);
    static readonly Cls CuNi = new Cls("copper-nickel", 22.0e6, 0.33, 0.323, 9.0e-6);
    static readonly Cls Aluminum = new Cls("aluminum", 10.0e6, 0.33, 0.098, 13.0e-6);
    static readonly Cls Copper = new Cls("copper", 17.0e6, 0.33, 0.323, 9.5e-6);
    static readonly Cls Brass = new Cls("brass", 15.0e6, 0.33, 0.308, 10.2e-6);
    static readonly Cls Bronze = new Cls("bronze", 14.0e6, 0.33, 0.318, 10.0e-6);
    static readonly Cls Frp = new Cls("FRP (CAESAR's FRP defaults: E axial, alpha)", 3.2e6, 0.3, 0.060, 12.0e-6);

    // a CAESAR material number's class; known: false when the number is not one Pluto knows (carbon steel is assumed)
    static Cls ClassOf(int n, out bool known)
    {
        known = true;
        switch (n)
        {
            case 1: case 2: case 21: return Carbon;
            case 3: return CarbonMoly;
            case 4: return LowChrome;
            case 5: return MedChrome;
            case 6: case 8: return Austenitic;
            case 7: return Chromium;
            case 9: return WroughtIron;
            case 10: return CastIron;
            case 11: case 12: return Monel;
            case 13: return CuNi;
            case 14: return Aluminum;
            case 15: return Copper;
            case 16: return Brass;
            case 17: return Bronze;
            case 20: return Frp;
        }
        if (n >= 101 && n <= 110 || n >= 150 && n <= 152) return Carbon;                 // A53, A105, A106, A135, A181, A285
        if (n == 112) return CarbonMoly;                                                  // A182 F1
        if (n >= 113 && n <= 123) return LowChrome;                                       // A182 F11, F12, F2, F21, F22
        if (n >= 139 && n <= 143) return MedChrome;                                       // A182 F5, F5A, F9, F91
        if (n == 111 || n >= 124 && n <= 138 || n >= 153 && n <= 166) return Austenitic;  // A182 F3xx, A312
        if (n >= 144 && n <= 149) return Chromium;                                        // A268 TP4xx
        known = false;
        return Carbon;
    }

    // CAESAR's cold-spring "materials" (an element cut short or long): no material at all
    public static bool IsColdSpring(int n) { return n == 18 || n == 19; }

    // the default for a material number in the job's units (m's factors): E, Poisson, density, alpha per job degree
    public static PipeModelMaterial For(PipeModel m, int number)
    {
        bool known;
        var c = ClassOf(number, out known);
        double tScale = m.Factor(6);                      // job degrees per F
        if (Math.Abs(tScale) < 1e-12) tScale = 1;
        return new PipeModelMaterial
        {
            Number = number, E = c.E * m.Factor(9), Poisson = c.Nu, Density = c.Rho * m.Factor(10), Alpha = c.Alpha / tScale,
            Note = Mark + (known ? c.Name : c.Name + " assumed (material " + number.ToString(CultureInfo.InvariantCulture) + " is not one Pluto knows)")
        };
    }

    // the Note a default row carries: the workbook shows it, so a value never checked stays visibly Pluto's
    public const string Mark = "Pluto default: ";
    public static bool IsDefaultNote(string note) { return (note ?? "").StartsWith(Mark, StringComparison.Ordinal); }

    // the class's name, for messages
    public static string ClassName(int number) { bool k; return ClassOf(number, out k).Name; }

    // the materials a model's elements use, in first-use order (cold-spring numbers aside)
    public static List<int> Used(PipeModel m)
    {
        var o = new List<int>();
        foreach (var e in m.Elements) if (!o.Contains(e.Material) && !IsColdSpring(e.Material)) o.Add(e.Material);
        return o;
    }

    // the model's table for every material its elements use: its own entries, defaults for the others (in use order;
    // entries for materials no element uses kept after them)
    public static List<PipeModelMaterial> Complete(PipeModel m)
    {
        var o = new List<PipeModelMaterial>();
        foreach (int n in Used(m)) { var x = m.MaterialNumbered(n); o.Add(x != null ? x.Copy() : For(m, n)); }
        foreach (var x in m.Materials) if (!o.Any(q => q.Number == x.Number)) o.Add(x.Copy());
        return o;
    }
}
