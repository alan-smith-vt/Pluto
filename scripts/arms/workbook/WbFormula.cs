using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

// WbFormula  --  the workbook kit's formula evaluator (2026-11-05; from PipeWorkbookFormula, 2026-10-23).
// The formulas a workbook saved by a program that does not calculate (a script library) holds without their values:
// the node numbers and node references Pluto writes (live numbering) and plain arithmetic, worked out here so the
// workbook still reads. Numbers, "text", cell references (another sheet's too, $ or not), + - * / &, = <>, parentheses,
// ROW(), ROW(ref), IF(a,b,c), INDEX(column,n). Anything else stays a cell with no value (reported as before).
public static class WbFormula
{
    sealed class Fail : Exception { }

    // inputs: the sheets the reader takes values from (hidden ones calculate for views and checks only)
    public static void Resolve(List<CaesarSheet> sheets, IEnumerable<string> inputSheets)
    {
        var inputs = new HashSet<string>(inputSheets, StringComparer.OrdinalIgnoreCase);
        if (!sheets.Any(s => inputs.Contains(s.Name) && s.Formulas.Count > 0)) return;
        var memo = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        var busy = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in sheets.Where(q => inputs.Contains(q.Name)))
            foreach (var kv in s.Formulas.ToList())
            {
                object v;
                try { v = Cell(sheets, s, kv.Key, memo, busy); }
                catch (Fail) { continue; }
                int col, row;
                if (!Pos(kv.Key, out col, out row)) continue;
                s.Set(row, col, Parser.Str(v));
                s.Problems.Remove(kv.Key);
                s.Formulas.Remove(kv.Key);
            }
    }

    static bool Pos(string a, out int col, out int row)
    {
        col = 0; row = -1;
        int i = 0;
        while (i < a.Length && char.IsLetter(a[i])) { col = col * 26 + (char.ToUpperInvariant(a[i]) - 'A' + 1); i++; }
        col--;
        int r;
        if (i == 0 || i >= a.Length || !int.TryParse(a.Substring(i), NumberStyles.Integer, CultureInfo.InvariantCulture, out r)) return false;
        row = r - 1;
        return true;
    }

    // a cell's value: its saved value, else its formula worked out (a number as double, else text)
    static object Cell(List<CaesarSheet> sheets, CaesarSheet s, string at, Dictionary<string, object> memo, HashSet<string> busy)
    {
        string key = s.Name + "!" + at;
        object m;
        if (memo.TryGetValue(key, out m)) return m;
        string f;
        if (!s.Formulas.TryGetValue(at, out f))
        {
            int col, row;
            if (!Pos(at, out col, out row)) throw new Fail();
            if (s.Problems.ContainsKey(at)) throw new Fail();
            string t = s.Cell(row, col);
            double d;
            if (t == null) return "";
            return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? (object)d : t;
        }
        if (!busy.Add(key)) throw new Fail();
        int c0, r0;
        Pos(at, out c0, out r0);
        var p = new Parser { Src = f.StartsWith("=") ? f.Substring(1) : f, Sheets = sheets, Here = s, Row = r0 + 1, Memo = memo, Busy = busy };
        object v = p.Val(p.Expr());
        p.Ws();
        if (p.I != p.Src.Length) throw new Fail();
        busy.Remove(key);
        memo[key] = v;
        return v;
    }

    sealed class Ref { public CaesarSheet Sheet; public int Col, Row; public bool WholeColumn; }

    sealed class Parser
    {
        public string Src; public int I; public List<CaesarSheet> Sheets; public CaesarSheet Here; public int Row;
        public Dictionary<string, object> Memo; public HashSet<string> Busy;

        public void Ws() { while (I < Src.Length && Src[I] == ' ') I++; }
        bool Eat(string t) { Ws(); if (string.Compare(Src, I, t, 0, t.Length, StringComparison.OrdinalIgnoreCase) == 0) { I += t.Length; return true; } return false; }

        public object Val(object v)
        {
            var r = v as Ref;
            if (r == null) return v;
            if (r.WholeColumn) throw new Fail();
            return Cell(Sheets, r.Sheet, XlsxBook.Ref(r.Row, r.Col), Memo, Busy);
        }
        static double Num(object v) { if (v is double) return (double)v; if (v is bool) return (bool)v ? 1 : 0; if (v as string == "") return 0; throw new Fail(); }
        public static string Str(object v) { return v is double ? ((double)v).ToString("R", CultureInfo.InvariantCulture) : v is bool ? ((bool)v ? "TRUE" : "FALSE") : v as string ?? ""; }

        public object Expr()
        {
            object a = Concat();
            if (Eat("<>")) { object b = Concat(); return !Same(Val(a), Val(b)); }
            if (Eat("=")) { object b = Concat(); return Same(Val(a), Val(b)); }
            return a;
        }
        static bool Same(object a, object b)
        {
            if (a is double && b is double) return (double)a == (double)b;
            if (a is string && b is string) return string.Equals((string)a, (string)b, StringComparison.OrdinalIgnoreCase);
            if (a is bool && b is bool) return (bool)a == (bool)b;
            return false;   // a number never equals text (Excel)
        }
        object Concat()
        {
            object a = Add();
            while (Eat("&")) a = Str(Val(a)) + Str(Val(Add()));
            return a;
        }
        object Add()
        {
            object a = Mul();
            while (true)
            {
                if (Eat("+")) a = Num(Val(a)) + Num(Val(Mul()));
                else if (Eat("-")) a = Num(Val(a)) - Num(Val(Mul()));
                else return a;
            }
        }
        object Mul()
        {
            object a = Unary();
            while (true)
            {
                if (Eat("*")) a = Num(Val(a)) * Num(Val(Unary()));
                else if (Eat("/")) { double d = Num(Val(Unary())); if (d == 0) throw new Fail(); a = Num(Val(a)) / d; }
                else return a;
            }
        }
        object Unary()
        {
            if (Eat("-")) return -Num(Val(Unary()));
            if (Eat("+")) return Num(Val(Unary()));
            return Primary();
        }
        object Primary()
        {
            Ws();
            if (I >= Src.Length) throw new Fail();
            char c = Src[I];
            if (c == '(') { I++; object v = Expr(); if (!Eat(")")) throw new Fail(); return v; }
            if (c == '"')
            {
                var sb = new StringBuilder();
                I++;
                while (I < Src.Length) { if (Src[I] == '"') { if (I + 1 < Src.Length && Src[I + 1] == '"') { sb.Append('"'); I += 2; continue; } break; } sb.Append(Src[I++]); }
                if (I >= Src.Length) throw new Fail();
                I++;
                return sb.ToString();
            }
            if (char.IsDigit(c) || c == '.')
            {
                int j = I;
                while (I < Src.Length && (char.IsDigit(Src[I]) || Src[I] == '.' || ((Src[I] == 'E' || Src[I] == 'e') && I + 1 < Src.Length && (char.IsDigit(Src[I + 1]) || Src[I + 1] == '-' || Src[I + 1] == '+')) ||
                                          ((Src[I] == '-' || Src[I] == '+') && I > j && (Src[I - 1] == 'E' || Src[I - 1] == 'e')))) I++;
                double d;
                if (!double.TryParse(Src.Substring(j, I - j), NumberStyles.Float, CultureInfo.InvariantCulture, out d)) throw new Fail();
                return d;
            }
            if (Eat("ROW("))
            {
                if (Eat(")")) return (double)Row;
                var r = Reference();
                if (!Eat(")") || r.WholeColumn) throw new Fail();
                return (double)(r.Row + 1);
            }
            if (Eat("IF("))
            {
                object cond = Val(Expr());
                if (!Eat(",")) throw new Fail();
                bool yes = cond is bool ? (bool)cond : Num(cond) != 0;
                // both branches are parsed; only the one taken is worked out
                object a = null, b = null;
                if (yes) { a = Val(Expr()); if (!Eat(",")) throw new Fail(); SkipArg(); }
                else { SkipArg(); if (!Eat(",")) throw new Fail(); b = Val(Expr()); }
                if (!Eat(")")) throw new Fail();
                return yes ? a : b;
            }
            if (Eat("INDEX("))
            {
                var r = Reference();
                if (!r.WholeColumn || !Eat(",")) throw new Fail();
                double n = Num(Val(Expr()));
                if (!Eat(")") || n < 1 || n != Math.Floor(n)) throw new Fail();
                return Cell(Sheets, r.Sheet, XlsxBook.Ref((int)n - 1, r.Col), Memo, Busy);
            }
            return Reference();
        }

        // an argument skipped over (the IF branch not taken): to the next comma or close at this depth
        void SkipArg()
        {
            int depth = 0;
            bool q = false;
            while (I < Src.Length)
            {
                char c = Src[I];
                if (q) { if (c == '"') q = false; I++; continue; }
                if (c == '"') q = true;
                else if (c == '(') depth++;
                else if (c == ')') { if (depth == 0) return; depth--; }
                else if (c == ',' && depth == 0) return;
                I++;
            }
        }

        // [Sheet!]$A$1 or [Sheet!]$A:$A
        Ref Reference()
        {
            Ws();
            var sheet = Here;
            int j = I;
            string name = null;
            if (I < Src.Length && Src[I] == '\'')
            {
                int k = Src.IndexOf("'!", I + 1, StringComparison.Ordinal);
                if (k < 0) throw new Fail();
                name = Src.Substring(I + 1, k - I - 1).Replace("''", "'");
                I = k + 2;
            }
            else
            {
                int k = I;
                while (k < Src.Length && (char.IsLetterOrDigit(Src[k]) || Src[k] == '_' || Src[k] == ' ')) k++;
                if (k < Src.Length && Src[k] == '!') { name = Src.Substring(I, k - I); I = k + 1; }
            }
            if (name != null)
            {
                sheet = Sheets.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
                if (sheet == null) throw new Fail();
            }
            int col, row;
            if (!Part(out col, out row)) { I = j; throw new Fail(); }
            if (row < 0)
            {
                // a whole column: $A:$A
                if (!Eat(":")) throw new Fail();
                int col2, row2;
                if (!Part(out col2, out row2) || row2 >= 0 || col2 != col) throw new Fail();
                return new Ref { Sheet = sheet, Col = col, Row = -1, WholeColumn = true };
            }
            return new Ref { Sheet = sheet, Col = col, Row = row };
        }

        // $A$1 -> col 0, row 0; $A -> row -1
        bool Part(out int col, out int row)
        {
            col = 0; row = -1;
            if (I < Src.Length && Src[I] == '$') I++;
            int j = I;
            while (I < Src.Length && char.IsLetter(Src[I])) { col = col * 26 + (char.ToUpperInvariant(Src[I]) - 'A' + 1); I++; }
            if (I == j || I - j > 3) return false;
            col--;
            if (I < Src.Length && Src[I] == '$') I++;
            int k = I;
            while (I < Src.Length && char.IsDigit(Src[I])) I++;
            if (I > k) row = int.Parse(Src.Substring(k, I - k), CultureInfo.InvariantCulture) - 1;
            return true;
        }
    }
}
