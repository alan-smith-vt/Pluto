using System;
using System.Collections.Generic;
using System.Globalization;

// PipeExpr  --  arithmetic for the pipe builder's parameters: a config value may be a number or a string
// expression over the [parameters] names, e.g. length = "3*span + 2". C# 5 (one global batch).
//   + - * / ^ (power, right-associative), unary +/-, parentheses, numbers (1e3), names, pi;
//   functions: sqrt abs min max round floor ceil, sin cos tan asin acos atan atan2 (DEGREES), hypot.
// Names resolve through the resolver (PipeParameters: lazily, with cycle detection). Errors name the
// expression and the position.

public static class PipeExpr
{
    public static double Eval(string text, Func<string, double> resolve)
    {
        var p = new Parser(text ?? "", resolve);
        double v = p.Expression();
        p.SkipWs();
        if (!p.AtEnd) throw p.Error("unexpected '" + p.Peek + "'");
        if (double.IsNaN(v) || double.IsInfinity(v)) throw new FormatException("expression \"" + text + "\" does not give a finite number");
        return v;
    }

    sealed class Parser
    {
        readonly string s;
        readonly Func<string, double> resolve;
        int i;
        public Parser(string text, Func<string, double> r) { s = text; resolve = r; }
        public bool AtEnd { get { return i >= s.Length; } }
        public char Peek { get { return i < s.Length ? s[i] : '\0'; } }
        public void SkipWs() { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; }
        public FormatException Error(string msg) { return new FormatException("expression \"" + s + "\": " + msg + " at position " + (i + 1).ToString(CultureInfo.InvariantCulture)); }

        public double Expression()
        {
            double v = Term();
            while (true)
            {
                SkipWs();
                if (Peek == '+') { i++; v += Term(); }
                else if (Peek == '-') { i++; v -= Term(); }
                else return v;
            }
        }

        double Term()
        {
            double v = Power();
            while (true)
            {
                SkipWs();
                if (Peek == '*') { i++; v *= Power(); }
                else if (Peek == '/')
                {
                    i++;
                    double d = Power();
                    if (d == 0) throw Error("division by zero");
                    v /= d;
                }
                else return v;
            }
        }

        double Power()
        {
            double b = Unary();
            SkipWs();
            if (Peek == '^') { i++; return Math.Pow(b, Power()); }
            return b;
        }

        double Unary()
        {
            SkipWs();
            if (Peek == '-') { i++; return -Unary(); }
            if (Peek == '+') { i++; return Unary(); }
            return Primary();
        }

        double Primary()
        {
            SkipWs();
            if (AtEnd) throw Error("a value was expected");
            char c = Peek;
            if (c == '(')
            {
                i++;
                double v = Expression();
                SkipWs();
                if (Peek != ')') throw Error("')' expected");
                i++;
                return v;
            }
            if (char.IsDigit(c) || c == '.')
            {
                int start = i;
                while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.')) i++;
                if (i < s.Length && (s[i] == 'e' || s[i] == 'E'))
                {
                    int save = i;
                    i++;
                    if (i < s.Length && (s[i] == '+' || s[i] == '-')) i++;
                    if (i < s.Length && char.IsDigit(s[i])) { while (i < s.Length && char.IsDigit(s[i])) i++; }
                    else i = save;
                }
                double v;
                if (!double.TryParse(s.Substring(start, i - start), NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out v))
                    throw Error("bad number '" + s.Substring(start, i - start) + "'");
                return v;
            }
            if (char.IsLetter(c) || c == '_')
            {
                int start = i;
                while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_')) i++;
                string name = s.Substring(start, i - start);
                SkipWs();
                if (Peek == '(')
                {
                    i++;
                    var args = new List<double>();
                    SkipWs();
                    if (Peek != ')')
                    {
                        while (true)
                        {
                            args.Add(Expression());
                            SkipWs();
                            if (Peek == ',') { i++; continue; }
                            break;
                        }
                    }
                    if (Peek != ')') throw Error("')' expected after the arguments of " + name);
                    i++;
                    return Call(name, args);
                }
                if (name == "pi") return Math.PI;
                if (resolve == null) throw Error("unknown name '" + name + "'");
                return resolve(name);
            }
            throw Error("unexpected '" + c + "'");
        }

        double Call(string f, List<double> a)
        {
            const double D = Math.PI / 180.0;
            switch (f)
            {
                case "sqrt": Need(f, a, 1); if (a[0] < 0) throw Error("sqrt of a negative number"); return Math.Sqrt(a[0]);
                case "abs": Need(f, a, 1); return Math.Abs(a[0]);
                case "round": Need(f, a, 1); return Math.Round(a[0], MidpointRounding.AwayFromZero);
                case "floor": Need(f, a, 1); return Math.Floor(a[0]);
                case "ceil": Need(f, a, 1); return Math.Ceiling(a[0]);
                case "sin": Need(f, a, 1); return Math.Sin(a[0] * D);
                case "cos": Need(f, a, 1); return Math.Cos(a[0] * D);
                case "tan": Need(f, a, 1); return Math.Tan(a[0] * D);
                case "asin": Need(f, a, 1); return Math.Asin(a[0]) / D;
                case "acos": Need(f, a, 1); return Math.Acos(a[0]) / D;
                case "atan": Need(f, a, 1); return Math.Atan(a[0]) / D;
                case "atan2": Need(f, a, 2); return Math.Atan2(a[0], a[1]) / D;
                case "hypot": { double h = 0; foreach (double x in a) h += x * x; return Math.Sqrt(h); }
                case "min": if (a.Count == 0) throw Error("min() needs arguments"); { double m = a[0]; foreach (double x in a) m = Math.Min(m, x); return m; }
                case "max": if (a.Count == 0) throw Error("max() needs arguments"); { double m = a[0]; foreach (double x in a) m = Math.Max(m, x); return m; }
                default: throw Error("unknown function '" + f + "'");
            }
        }

        void Need(string f, List<double> a, int n)
        {
            if (a.Count != n) throw Error(f + "() takes " + n.ToString(CultureInfo.InvariantCulture) + " argument(s)");
        }
    }
}

// The [parameters] table: name -> number or expression, resolved lazily (any order, cycles reported),
// with overrides from the command line (Build-Pipe.ps1 -Set "span=25;T=400") taking precedence.
public class PipeParameters
{
    readonly Dictionary<string, object> defs = new Dictionary<string, object>(StringComparer.Ordinal);
    readonly Dictionary<string, double> values = new Dictionary<string, double>(StringComparer.Ordinal);
    readonly HashSet<string> busy = new HashSet<string>(StringComparer.Ordinal);
    public readonly List<string> Overridden = new List<string>();

    public void Define(string name, object value) { defs[name] = value; }

    public bool Has(string name) { return defs.ContainsKey(name); }

    public IEnumerable<string> Names { get { return defs.Keys; } }

    // "a=1;b=2*a" (or newline / comma separated)
    public void Override(string spec)
    {
        if (string.IsNullOrEmpty(spec)) return;
        foreach (string part in spec.Split(new[] { ';', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string t = part.Trim();
            if (t.Length == 0) continue;
            int eq = t.IndexOf('=');
            if (eq <= 0) throw new FormatException("-Set \"" + t + "\": expected name=value");
            string name = t.Substring(0, eq).Trim(), val = t.Substring(eq + 1).Trim();
            if (!defs.ContainsKey(name)) throw new FormatException("-Set: '" + name + "' is not in [parameters]");
            defs[name] = val;
            Overridden.Add(name + "=" + val);
        }
    }

    public double Get(string name)
    {
        double v;
        if (values.TryGetValue(name, out v)) return v;
        object def;
        if (!defs.TryGetValue(name, out def)) throw new FormatException("unknown parameter '" + name + "'");
        if (!busy.Add(name)) throw new FormatException("parameter '" + name + "' depends on itself");
        try { v = Value(def); }
        finally { busy.Remove(name); }
        values[name] = v;
        return v;
    }

    public double Value(object o)
    {
        if (o is double) return (double)o;
        var s = o as string;
        if (s != null) return PipeExpr.Eval(s, Get);
        if (o is bool) throw new FormatException("true / false where a number was expected");
        throw new FormatException("a number or an expression was expected");
    }
}
