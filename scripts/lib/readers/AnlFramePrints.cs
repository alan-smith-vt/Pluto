using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

// AnlFramePrints -- the tables a STAAD .anl prints besides the member end forces, the support reactions and the joint
// displacements (those three are in AnlFrameReader.cs): the code check, the member information, the member properties,
// the section displacements and the joint coordinates. Each parser takes the text of one output command and adds its
// rows to the model. Written from the output of scripts/lib/probes/anl-prints-probe.std (STAAD.Pro 2025); a layout
// this file has not seen is said as a problem, never guessed. C# 5 (Windows PowerShell 5.1 Add-Type, warnings are
// errors). vault/calcflow/calcflow-staad-frame-reader.md.
//
//   CHECK CODE                    three layouts of the result per member:
//                                   the box of AISC 360-16 / 360-22   |* Member No: 101  Profile: ST W10X49 (AISC SECTIONS)|
//                                   the table of 360-05 / 360-10 and of the older codes at TRACK 0 and 1
//                                                                     * 101 ST W10X49 (AISC SECTIONS) / FAIL Eq. H1-1b 1.071 104
//                                   the drawing of the older codes (AISC 9th, LRFD 3rd) at TRACK 2
//                                                                     |MEMBER 101 * | AISC SECTIONS | ... DESIGN SUMMARY
//                                 and, at TRACK 1 and 2, the checks behind the result (demand, capacity, ratio, clause)
//   PRINT MEMBER INFORMATION      joints, length, beta and releases as STAAD holds them: kept, and checked against the echo
//   PRINT MEMBER PROPERTIES       AX IZ IY IX AY AZ SZ SY per member
//   PRINT SECTION DISPLACEMENTS   global X Y Z at the stations along each member per case, and STAAD's largest local
//                                 displacement per member
//   PRINT JOINT COORDINATES       checked against the echo (nothing new to keep)

// PRINT MEMBER INFORMATION: one member as STAAD holds it after reading the input.
public sealed class AnlFrameMemberInfo
{
    public int Member, StartJoint, EndJoint;
    public double Length;           // as printed, in AnlFrameModel.MemberInfoLengthText
    public bool HasBeta;            // false: none printed (a truss member)
    public double Beta;             // degrees
    public string Releases = "";    // as printed: 12 digits (start FX FY FZ MX MY MZ, then end; 1 = released), TRUSS ...
}

// PRINT MEMBER PROPERTIES: one member's section properties, in powers of AnlFrameModel.MemberPropertyLengthText.
public sealed class AnlFrameMemberProperty
{
    public static readonly string[] Names = { "AX", "IZ", "IY", "IX", "AY", "AZ", "SZ", "SY" };
    public static readonly int[] Powers = { 2, 4, 4, 4, 2, 2, 3, 3 };
    public int Member;
    public string Profile = "";     // as printed: "ST W10X49", "LD L40404", "PRISMATIC"
    public readonly double[] V = new double[8];
}

// CHECK CODE: the result of one member under one CHECK CODE command.
public sealed class AnlFrameCodeCheck
{
    public int Print;               // AnlFramePrint.Order of the command
    public int Member;
    public string Code = "";        // the title STAAD gives the check: "AISC 360-16 LRFD (V1.2)"
    public string Profile = "";     // "ST W10X49"
    public string Table = "";       // "AISC SECTIONS"
    public string Status = "";      // PASS | FAIL | NOT DESIGNED
    public bool HasRatio;
    public double Ratio;
    public string Critical = "";    // the governing clause as printed: "Eq.H1-1b", "AISC- H1-3", "SLENDERNESS"
    public int LC;                  // the governing case (0: none)
    public double Location;         // along the member, in LengthText
    // the forces of the governing case in STAAD's names FX FY FZ MX MY MZ, as printed (FX without its sign: see Axial)
    public readonly double[] F = new double[6];
    public readonly bool[] Has = new bool[6];
    public string Axial = "";       // C | T
    public string ForceText = "", LengthText = "";   // the units of F and Location
    public bool Starred;            // STAAD's * before the member
    public string Note = "";        // STAAD's message about the member
}

// CHECK CODE at TRACK 1 or 2: one check behind a member's result.
public sealed class AnlFrameCodeCheckDetail
{
    public int Print, Member;
    public string Group = "";       // "CHECKS FOR AXIAL TENSION"
    public string Check = "";       // "TENSILE YIELDING", "Maj Buck", "CLAUSE H1"
    public bool HasDemand;
    public double Demand, Capacity;
    public double Ratio;
    public string Reference = "";   // "Cl.D2", "Eq. E3-1"
    public int LC;
    public double Location;
}

// PRINT SECTION DISPLACEMENTS: one member in one case.
public sealed class AnlFrameSectionDisplacement
{
    public int Member, LC;
    public double[] V;              // global X Y Z at each station from the start joint to the end joint (3 per station)
}

// PRINT SECTION DISPLACEMENTS: STAAD's "MAX LOCAL DISP" line of a member.
public sealed class AnlFrameSectionMax
{
    public int Member;
    public double Disp, At;         // the largest local displacement and where, in SectionDisplacementLengthText
    public int LC;
    public bool HasRatio;
    public double LOverDisp;        // L / DISP
}

public static class AnlFramePrintTables
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    const RegexOptions I = RegexOptions.IgnoreCase;
    static readonly Regex EchoLine = new Regex(@"^\s*\d+\.(?:\s+|$)");
    static readonly Regex NumbersOnly = new Regex(@"^(?=.*\d)[\s\d.+\-Ee]+$");     // numbers and nothing else (a rule of dashes is not one)

    static bool IsInt(string t, out int v) { return int.TryParse(t, NumberStyles.None, Inv, out v); }
    static bool IsNum(string t, out double v) { return double.TryParse(t, NumberStyles.Float, Inv, out v); }
    static string[] Tokens(string s) { return s.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries); }
    static string Squeeze(string s) { return Regex.Replace(s ?? "", @"\s+", " ").Trim(); }

    // the lines of an output: no echo lines, no page headers
    static IEnumerable<string> Lines(string text)
    {
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.IndexOf('\f') >= 0 || line.IndexOf("-- PAGE NO.", StringComparison.Ordinal) >= 0 || EchoLine.IsMatch(line)) continue;
            yield return line;
        }
    }

    // ================================================================= PRINT JOINT COORDINATES

    public static void JointCoordinates(string text, AnlFrameModel m, AnlFramePrint print)
    {
        Match mu = Regex.Match(text, @"COORDINATES\s+ARE\s+([A-Z]+)\s+UNIT", I);
        LengthUnit lu;
        if (!mu.Success || !AnlFrameReader.TryLengthWord(mu.Groups[1].Value, out lu))
        {
            m.Warn("JOINT_PRINT_UNITS", print.Command + ": no readable \"COORDINATES ARE <length> UNIT\" line; its table is not checked against the echo");
            return;
        }
        var differ = new List<int>();
        var extra = new List<int>();
        foreach (string line in Lines(text))
        {
            if (!NumbersOnly.IsMatch(line)) continue;
            string[] t = Tokens(line);
            int id; double x, y, z;
            if (t.Length != 4 || !IsInt(t[0], out id) || !IsNum(t[1], out x) || !IsNum(t[2], out y) || !IsNum(t[3], out z)) continue;
            print.Rows++;
            AnlFrameNode n;
            if (!m.Nodes.TryGetValue(id, out n)) { extra.Add(id); continue; }
            double[] echo = { UnitConvert.Length(n.X, LengthUnit.Inch, lu), UnitConvert.Length(n.Y, LengthUnit.Inch, lu), UnitConvert.Length(n.Z, LengthUnit.Inch, lu) };
            double[] printed = { x, y, z };
            for (int k = 0; k < 3; k++)
                if (Math.Abs(echo[k] - printed[k]) > 0.00051 + 1e-7 * Math.Abs(printed[k])) { differ.Add(id); break; }
        }
        print.Read = true;
        if (differ.Count > 0)
            m.Error("JOINT_PRINT_MISMATCH", print.Command + " gives other coordinates than the input echo was read as, at joints " + AnlFrameReader.Ids(differ));
        if (extra.Count > 0)
            m.Warn("JOINT_PRINT_EXTRA", print.Command + " lists joints the input echo did not give: " + AnlFrameReader.Ids(extra));
    }

    // ================================================================= PRINT MEMBER INFORMATION

    static readonly Regex InfoRow = new Regex(@"^\s*(\d+)\s+(\d+)\s+(\d+)\s+(-?\d*\.\d+(?:[Ee][+\-]?\d+)?)(?:\s+(-?\d*\.\d+))?(?:\s+(\S.*?))?\s*$");

    public static void MemberInformation(string text, AnlFrameModel m, AnlFramePrint print)
    {
        Match mu = Regex.Match(text, @"\(\s*([A-Z]+)\s*\)\s*\(\s*DEG\s*\)", I);
        LengthUnit lu;
        if (!mu.Success || !AnlFrameReader.TryLengthWord(mu.Groups[1].Value, out lu))
        {
            m.Warn("MEMBER_INFO_UNITS", print.Command + ": no readable \"(<length>) (DEG)\" heading; its table is not read");
            return;
        }
        string unit = AnlFrameModel.UnitText(lu);
        if (m.MemberInfo.Count > 0 && unit != m.MemberInfoLengthText)
        {
            m.Warn("MEMBER_INFO_UNITS", "the member information is printed twice in different units (" + m.MemberInfoLengthText + ", then " + unit + "): the first print is kept");
            return;
        }
        m.MemberInfoLengthText = unit;
        var index = m.MemberInfo.ToDictionary(x => x.Member);
        var extra = new List<int>();
        var joints = new List<int>();
        var betas = new List<int>();
        var releases = new List<int>();
        foreach (string line in Lines(text))
        {
            Match r = InfoRow.Match(line);
            if (!r.Success) continue;
            var row = new AnlFrameMemberInfo();
            row.Member = int.Parse(r.Groups[1].Value, Inv);
            row.StartJoint = int.Parse(r.Groups[2].Value, Inv);
            row.EndJoint = int.Parse(r.Groups[3].Value, Inv);
            row.Length = double.Parse(r.Groups[4].Value, NumberStyles.Float, Inv);
            row.HasBeta = r.Groups[5].Success;
            if (row.HasBeta) row.Beta = double.Parse(r.Groups[5].Value, NumberStyles.Float, Inv);
            row.Releases = r.Groups[6].Success ? Squeeze(r.Groups[6].Value) : "";
            AnlFrameMemberInfo old;
            if (index.TryGetValue(row.Member, out old)) m.MemberInfo.Remove(old);
            index[row.Member] = row;
            m.MemberInfo.Add(row);
            print.Rows++;

            AnlFrameMember mem;
            if (!m.Members.TryGetValue(row.Member, out mem)) { extra.Add(row.Member); continue; }
            if (mem.NodeA != row.StartJoint || mem.NodeB != row.EndJoint) { joints.Add(row.Member); continue; }
            if (row.HasBeta && mem.BetaKnown)
            {
                double d = Math.Abs(row.Beta - mem.Beta) % 360.0;
                if (Math.Min(d, 360.0 - d) > 0.0051) betas.Add(row.Member);
            }
            if (Regex.IsMatch(row.Releases, "^[01]{12}$") && mem.ReleaseA.Extra.Length == 0 && mem.ReleaseB.Extra.Length == 0)
            {
                bool same = true;
                for (int k = 0; k < 6 && same; k++)
                    same = (row.Releases[k] == '1') == mem.ReleaseA.Released[k] && (row.Releases[6 + k] == '1') == mem.ReleaseB.Released[k];
                if (!same) releases.Add(row.Member);
            }
        }
        print.Read = true;
        if (print.Rows == 0) m.Warn("MEMBER_INFO_ROWS", print.Command + ": no row of its table was read");
        if (joints.Count > 0)
            m.Error("MEMBER_INFO_MISMATCH", print.Command + " gives other start and end joints than the input echo was read as, for members " + AnlFrameReader.Ids(joints));
        if (extra.Count > 0)
            m.Warn("MEMBER_INFO_EXTRA", print.Command + " lists members the input echo did not give: " + AnlFrameReader.Ids(extra));
        if (betas.Count > 0)
            m.Warn("MEMBER_INFO_BETA", print.Command + " gives another BETA than the input echo was read as (the member's own axes follow the echo), for members " + AnlFrameReader.Ids(betas));
        if (releases.Count > 0)
            m.Warn("MEMBER_INFO_RELEASE", print.Command + " gives other releases than the input echo was read as, for members " + AnlFrameReader.Ids(releases));
    }

    // ================================================================= PRINT MEMBER PROPERTIES

    public static void MemberProperties(string text, AnlFrameModel m, AnlFramePrint print)
    {
        Match mu = Regex.Match(text, @"MEMBER\s+PROPERTIES\.?\s+UNIT\s*-\s*([A-Z]+)", I);
        LengthUnit lu;
        if (!mu.Success || !AnlFrameReader.TryLengthWord(mu.Groups[1].Value, out lu))
        {
            m.Warn("MEMBER_PROPERTY_UNITS", print.Command + ": no readable \"MEMBER PROPERTIES. UNIT - <length>\" heading; its table is not read");
            return;
        }
        string unit = AnlFrameModel.UnitText(lu);
        if (m.MemberProperties.Count > 0 && unit != m.MemberPropertyLengthText)
        {
            m.Warn("MEMBER_PROPERTY_UNITS", "the member properties are printed twice in different units (" + m.MemberPropertyLengthText + ", then " + unit + "): the first print is kept");
            return;
        }
        m.MemberPropertyLengthText = unit;
        var index = m.MemberProperties.ToDictionary(x => x.Member);
        var rows = new List<AnlFrameMemberProperty>();
        var unread = new List<int>();
        AnlFrameMemberProperty cur = null;
        int have = 0;
        Action close = () =>
        {
            if (cur == null) return;
            if (have == 8) rows.Add(cur); else unread.Add(cur.Member);
            cur = null; have = 0;
        };
        foreach (string line in Lines(text))
        {
            string[] t = Tokens(line);
            if (t.Length == 0) continue;
            int id; double v;
            if (t.Length >= 2 && IsInt(t[0], out id) && !IsNum(t[1], out v))
            {
                // a member's first line: its number, its profile, and its first four numbers when they fit on the line
                close();
                int first = t.Length;
                while (first > 2 && IsNum(t[first - 1], out v)) first--;
                cur = new AnlFrameMemberProperty();
                cur.Member = id;
                cur.Profile = string.Join(" ", t, 1, first - 1);
                for (int k = first; k < t.Length && have < 8; k++) { IsNum(t[k], out v); cur.V[have++] = v; }
                if (t.Length - first > 4) have = 99;
                continue;
            }
            if (cur == null || !NumbersOnly.IsMatch(line)) continue;
            bool ok = true;
            var vals = new double[t.Length];
            for (int k = 0; k < t.Length && ok; k++) ok = IsNum(t[k], out vals[k]);
            if (!ok || have + t.Length > 8) { have = 99; continue; }
            foreach (double x in vals) cur.V[have++] = x;
        }
        close();
        foreach (AnlFrameMemberProperty r in rows)
        {
            AnlFrameMemberProperty old;
            if (index.TryGetValue(r.Member, out old)) m.MemberProperties.Remove(old);
            index[r.Member] = r;
            m.MemberProperties.Add(r);
            print.Rows++;
        }
        print.Read = true;
        if (rows.Count == 0 && unread.Count == 0) m.Warn("MEMBER_PROPERTY_ROWS", print.Command + ": no row of its table was read");
        if (unread.Count > 0)
            m.Warn("MEMBER_PROPERTY_UNREAD", print.Command + ": the properties of some members are not eight numbers and were left out: members " + AnlFrameReader.Ids(unread));
    }

    // ================================================================= PRINT SECTION DISPLACEMENTS

    static readonly Regex MaxLocal = new Regex(@"MAX\s+LOCAL\s+DISP\s*=\s*(\S+)\s+AT\s+(\S+)\s+LOAD\s+(\d+)\s+L/DISP\s*=\s*(\S+)", I);

    public static void SectionDisplacements(string text, AnlFrameModel m, AnlFramePrint print)
    {
        Match mu = Regex.Match(text, @"MEMBER\s+SECTION\s+DISPLACEMENTS[\s\-]*UNITS\s+ARE\s*-\s*([A-Z]+)", I);
        LengthUnit lu;
        if (!mu.Success || !AnlFrameReader.TryLengthWord(mu.Groups[1].Value, out lu))
        {
            m.Error("SECTION_DISPLACEMENT_UNITS", print.Command + ": no readable \"UNITS ARE - <length>\" line in its table");
            return;
        }
        string unit = AnlFrameModel.UnitText(lu);
        if (m.SectionDisplacements.Count > 0 && unit != m.SectionDisplacementLengthText)
        {
            m.Error("SECTION_DISPLACEMENT_UNITS", "section displacements are printed twice in different units (" + m.SectionDisplacementLengthText + ", then " + unit + "): print them once");
            return;
        }
        Match mp = Regex.Match(text, @"AT\s+1/(\d+)TH\s+PTS", I);
        int parts = mp.Success ? int.Parse(mp.Groups[1].Value, Inv) : 12;
        int want = 3 * (parts + 1);
        if (m.SectionDisplacements.Count > 0 && parts + 1 != m.SectionStations)
        {
            m.Error("SECTION_DISPLACEMENT_UNITS", "section displacements are printed twice at different numbers of stations: print them once");
            return;
        }
        m.SectionDisplacementLengthText = unit;
        m.SectionStations = parts + 1;

        var index = new Dictionary<long, AnlFrameSectionDisplacement>();
        foreach (AnlFrameSectionDisplacement d in m.SectionDisplacements) index[((long)d.Member << 32) | (uint)d.LC] = d;
        var maxIndex = m.SectionMaxima.ToDictionary(x => x.Member);
        int member = -1, bad = 0;
        string sample = "";
        AnlFrameSectionDisplacement cur = null;
        var vals = new List<double>();
        Action close = () =>
        {
            if (cur == null) return;
            if (vals.Count == want)
            {
                cur.V = vals.ToArray();
                long key = ((long)cur.Member << 32) | (uint)cur.LC;
                AnlFrameSectionDisplacement old;
                if (index.TryGetValue(key, out old)) m.SectionDisplacements.Remove(old);
                index[key] = cur;
                m.SectionDisplacements.Add(cur);
                print.Rows++;
            }
            else { bad++; if (sample.Length == 0) sample = "member " + cur.Member + " case " + cur.LC + ": " + vals.Count + " numbers, not " + want; }
            cur = null; vals.Clear();
        };
        foreach (string line in Lines(text))
        {
            Match mx = MaxLocal.Match(line);
            if (mx.Success)
            {
                close();
                double disp, at, ratio; int lc;
                if (member >= 0 && IsNum(mx.Groups[1].Value, out disp) && IsNum(mx.Groups[2].Value, out at) && IsInt(mx.Groups[3].Value, out lc))
                {
                    var s = new AnlFrameSectionMax();
                    s.Member = member; s.Disp = disp; s.At = at; s.LC = lc;
                    s.HasRatio = IsNum(mx.Groups[4].Value, out ratio);
                    s.LOverDisp = s.HasRatio ? ratio : 0;
                    AnlFrameSectionMax old;
                    if (maxIndex.TryGetValue(member, out old)) m.SectionMaxima.Remove(old);
                    maxIndex[member] = s;
                    m.SectionMaxima.Add(s);
                }
                continue;
            }
            if (!NumbersOnly.IsMatch(line)) continue;
            string[] t = Tokens(line);
            if (t.Length == 0) continue;
            // a displacement always carries a decimal point; a member or case number never does
            int lead = 0;
            while (lead < t.Length && t[lead].IndexOf('.') < 0) lead++;
            var nums = new double[t.Length - lead];
            bool ok = lead <= 2;
            for (int k = lead; k < t.Length && ok; k++) ok = IsNum(t[k], out nums[k - lead]);
            int a = 0, b = 0;
            if (ok && lead == 2 && IsInt(t[0], out a) && IsInt(t[1], out b)) { close(); member = a; cur = new AnlFrameSectionDisplacement(); cur.Member = a; cur.LC = b; }
            else if (ok && lead == 1 && IsInt(t[0], out b) && member >= 0) { close(); cur = new AnlFrameSectionDisplacement(); cur.Member = member; cur.LC = b; }
            else if (!ok || lead != 0 || cur == null) { bad++; if (sample.Length == 0) sample = line.Trim(); continue; }
            vals.AddRange(nums);
        }
        close();
        print.Read = true;
        // an error, not a warning: numbers that are not placed would shift every station after them
        if (bad > 0)
            m.Error("SECTION_DISPLACEMENT_UNREAD", bad + " block(s) or line(s) of " + print.Command + " are not a member's " + (parts + 1) + " stations of X Y Z (touching columns? overflow?), e.g. \"" + sample + "\"");
        var unknown = m.SectionDisplacements.Select(x => x.Member).Distinct().Where(x => !m.Members.ContainsKey(x)).ToList();
        if (unknown.Count > 0) m.Warn("SECTION_DISPLACEMENT_MEMBER", print.Command + " lists members the input echo did not give: " + AnlFrameReader.Ids(unknown));
    }

    // ================================================================= CHECK CODE

    static readonly Regex Title = new Regex(@"STAAD\.PRO\s+CODE\s+CHECKING\s*-\s*(.+?)\s*$", I);
    static readonly Regex AllUnits = new Regex(@"ALL\s+UNITS\s+ARE\s*-+\s*([A-Z]+)\s+([A-Z]+)", I);
    static readonly Regex BoxMember = new Regex(@"^\|(\*?)\s*Member No:\s*(\d+)\s+Profile:\s*(.*?)\s*(?:\(([^)]*)\))?\s*\|\s*$", I);
    static readonly Regex BoxStatus = new Regex(@"^\|\s*Status:\s*(\S+)\s+Ratio:\s*(\S+)\s+Loadcase:\s*(\d+)", I);
    static readonly Regex BoxLocation = new Regex(@"^\|\s*Location:\s*(\S+)\s+Ref:\s*(.*?)\s*\|\s*$", I);
    static readonly Regex BoxPz = new Regex(@"^\|\s*Pz:\s*(\S+)\s+(?:([CT])\s+)?Vy:\s*(\S+)\s+Vx:\s*(\S+)", I);
    static readonly Regex BoxTz = new Regex(@"^\|\s*Tz:\s*(\S+)\s+My:\s*(\S+)\s+Mx:\s*(\S+)", I);
    static readonly Regex TabMember = new Regex(@"^\s*(\*?)\s*(\d+)\s+([A-Za-z].*?)\s+\(([^)]*)\)\s*$");
    static readonly Regex DrawMember = new Regex(@"^\s*\|MEMBER\s+(\d+)\s*\*\s*\|\s*(.*?)\s*\|", I);
    static readonly Regex DrawProfile = new Regex(@"^\s*\|\s*\*\s*\|\s*([A-Za-z].*?)\s*\|");
    static readonly Regex DrawSummary = new Regex(@"DESIGN\s+SUMMARY\s*\(\s*([A-Z]+)\s*-\s*([A-Z]+)\s*\)", I);
    static readonly Regex ResultLine = new Regex(@"^(PASS|FAIL)\s+(.*?)\s+(\S+)\s+(\d+)$", I);
    static readonly Regex ForcesLine = new Regex(@"^(\S+)\s+(?:([CT])\s+)?(\S+)\s+(\S+)\s+(\S+)$", I);
    static readonly Regex Message = new Regex(@"(WARNING|ERROR)\s*[:\-]\s*(.*?MEMBER\s*#?\s*(\d+).*?)\s*$", I);
    static readonly Regex Header1 = new Regex(@"^(DEMAND|FORCE)\s+CAPACITY\s+RATIO\s+(REFERENCE|CRITERIA)\s+L/C\s+LOC$", I);
    static readonly Regex Header2 = new Regex(@"^RATIO\s+CRITERIA\s+L/C\s+LOC$", I);

    // listed: the members the command names (null: not known), for the members it gave no result for
    public static void CodeCheck(string text, AnlFrameModel m, AnlFramePrint print, List<int> listed)
    {
        string code = "", fu = "", lu = "";
        var rows = new Dictionary<int, AnlFrameCodeCheck>();
        var order = new List<AnlFrameCodeCheck>();
        var unread = new List<int>();
        AnlFrameCodeCheck cur = null;
        // 0 nothing open; box: 11 status, 12 location, 13 Pz, 14 Tz; table: 21 result, 22 forces; drawing: 31 result, 32 forces
        int stage = 0;
        string group = "", lastText = "", pendingName = "";
        int detail = 0, detailUnread = 0;
        string detailSample = "";
        Func<int, AnlFrameCodeCheck> open = member =>
        {
            AnlFrameCodeCheck r;
            if (!rows.TryGetValue(member, out r))
            {
                r = new AnlFrameCodeCheck();
                r.Print = print.Order; r.Member = member;
                rows[member] = r; order.Add(r);
            }
            r.Code = code; r.ForceText = fu; r.LengthText = lu;
            return r;
        };
        Action fail = () => { if (cur != null && stage != 0) { unread.Add(cur.Member); cur.Status = ""; } stage = 0; };

        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.IndexOf("-- PAGE NO.", StringComparison.Ordinal) >= 0 || EchoLine.IsMatch(line)) continue;
            line = line.Replace("\f", "");
            if (line.Trim().Length == 0) continue;
            Match mt;
            if ((mt = Title.Match(line)).Success) { code = Squeeze(mt.Groups[1].Value); continue; }
            if ((mt = AllUnits.Match(line)).Success)
            {
                ForceUnit f; LengthUnit l;
                if (AnlFrameReader.TryForceWord(mt.Groups[1].Value, out f) && AnlFrameReader.TryLengthWord(mt.Groups[2].Value, out l))
                { fu = AnlFrameModel.UnitText(f); lu = AnlFrameModel.UnitText(l); }
                continue;
            }
            if ((mt = Message.Match(line)).Success && line.IndexOf('|') < 0)
            {
                int id = int.Parse(mt.Groups[3].Value, Inv);
                AnlFrameCodeCheck r = open(id);
                string note = Squeeze(mt.Groups[2].Value).TrimEnd('.');
                if (r.Status.Length == 0) r.Status = "NOT DESIGNED";
                if (r.Note.IndexOf(note, StringComparison.OrdinalIgnoreCase) < 0) r.Note = r.Note.Length == 0 ? note : r.Note + "; " + note;
                continue;
            }

            // ---- a member's result opens
            if ((mt = BoxMember.Match(line)).Success)
            {
                fail();
                cur = open(int.Parse(mt.Groups[2].Value, Inv));
                cur.Starred = mt.Groups[1].Value == "*";
                cur.Profile = Squeeze(mt.Groups[3].Value); cur.Table = Squeeze(mt.Groups[4].Value);
                stage = 11; detail = 0; group = ""; lastText = ""; pendingName = "";
                continue;
            }
            if ((mt = DrawMember.Match(line)).Success)
            {
                fail();
                cur = open(int.Parse(mt.Groups[1].Value, Inv));
                cur.Table = Squeeze(mt.Groups[2].Value); cur.Profile = "";
                stage = 31; detail = 0; group = ""; lastText = ""; pendingName = "";
                continue;
            }
            if (line.IndexOf('|') < 0 && (mt = TabMember.Match(line)).Success)
            {
                fail();
                cur = open(int.Parse(mt.Groups[2].Value, Inv));
                cur.Starred = mt.Groups[1].Value == "*";
                cur.Profile = Squeeze(mt.Groups[3].Value); cur.Table = Squeeze(mt.Groups[4].Value);
                stage = 21; detail = 0; group = ""; lastText = ""; pendingName = "";
                continue;
            }
            if (cur == null) continue;

            // ---- the lines of the result
            if (stage == 11)
            {
                double ratio; int lc;
                if (!(mt = BoxStatus.Match(line)).Success) { fail(); continue; }
                cur.Status = mt.Groups[1].Value.ToUpperInvariant();
                cur.HasRatio = IsNum(mt.Groups[2].Value, out ratio) && !double.IsNaN(ratio); cur.Ratio = cur.HasRatio ? ratio : 0;
                IsInt(mt.Groups[3].Value, out lc); cur.LC = lc;
                stage = 12; continue;
            }
            if (stage == 12)
            {
                double loc;
                if (!(mt = BoxLocation.Match(line)).Success || !IsNum(mt.Groups[1].Value, out loc)) { fail(); continue; }
                cur.Location = loc; cur.Critical = Squeeze(mt.Groups[2].Value);
                stage = 13; continue;
            }
            if (stage == 13)
            {
                // a box that ends after Location: no forces. STAAD 2025 prints one for a member with DFF under a load list
                // with no serviceability envelope: "Status: PASS  Ratio: NaN  Loadcase: 0", "Ref: Deflection" (round 4).
                // Nothing was checked, so it reads as not designed
                if (Regex.IsMatch(line, @"^\s*\|-+\|\s*$"))
                {
                    if (!cur.HasRatio && cur.LC == 0)
                    {
                        cur.Status = "NOT DESIGNED";
                        string note = (cur.Critical.Length > 0 ? cur.Critical + ": " : "") + "STAAD gives no ratio and no load case (nothing checked under this load list)";
                        if (cur.Note.IndexOf(note, StringComparison.OrdinalIgnoreCase) < 0) cur.Note = cur.Note.Length == 0 ? note : cur.Note + "; " + note;
                    }
                    stage = 0; continue;
                }
                if (!(mt = BoxPz.Match(line)).Success || !Put(cur, 0, mt.Groups[1].Value) || !Put(cur, 1, mt.Groups[3].Value) || !Put(cur, 2, mt.Groups[4].Value)) { fail(); continue; }
                cur.Axial = mt.Groups[2].Value.ToUpperInvariant();
                stage = 14; continue;
            }
            if (stage == 14)
            {
                if (!(mt = BoxTz.Match(line)).Success || !Put(cur, 3, mt.Groups[1].Value) || !Put(cur, 4, mt.Groups[2].Value) || !Put(cur, 5, mt.Groups[3].Value)) { fail(); continue; }
                stage = 0; continue;
            }
            string inner = Inner(line);
            if (stage == 31)
            {
                if (cur.Profile.Length == 0 && (mt = DrawProfile.Match(line)).Success) { cur.Profile = Squeeze(mt.Groups[1].Value); continue; }
                if ((mt = DrawSummary.Match(line)).Success)
                {
                    ForceUnit f; LengthUnit l;
                    if (AnlFrameReader.TryForceWord(mt.Groups[1].Value, out f) && AnlFrameReader.TryLengthWord(mt.Groups[2].Value, out l))
                    { cur.ForceText = AnlFrameModel.UnitText(f); cur.LengthText = AnlFrameModel.UnitText(l); }
                    continue;
                }
                if (Result(cur, inner)) stage = 32;
                continue;
            }
            if (stage == 21)
            {
                if (!Result(cur, inner)) { fail(); continue; }
                stage = 22; continue;
            }
            if (stage == 22 || stage == 32)
            {
                if (!Forces(cur, inner)) { fail(); continue; }
                stage = 0; continue;
            }

            // ---- the checks behind the result (TRACK 1 and 2): boxed lines
            if (line.TrimStart().Length == 0 || line.TrimStart()[0] != '|') continue;
            if (Regex.IsMatch(inner, @"^-+$")) { detail = 0; pendingName = ""; continue; }
            if (inner.Length == 0) continue;
            if (Header1.IsMatch(inner)) { detail = 1; pendingName = ""; continue; }
            if (Header2.IsMatch(inner)) { detail = 2; pendingName = ""; continue; }
            if (Regex.IsMatch(inner, @"^CHECKS?\s+FOR\b", I)) { group = inner; lastText = ""; detail = 0; continue; }
            if (detail == 0) { lastText = inner; continue; }
            if (inner.StartsWith("Intermediate", StringComparison.OrdinalIgnoreCase)) { detail = 0; continue; }
            string[] t = Tokens(inner);
            double v;
            int i0 = 0;
            while (i0 < t.Length && !IsNum(t[i0], out v)) i0++;
            if (i0 == t.Length) { pendingName = (pendingName + " " + inner).Trim(); continue; }
            int need = detail == 1 ? 6 : 4;
            var d = new AnlFrameCodeCheckDetail();
            double loc2; int lc2;
            bool fits = t.Length - i0 >= need && IsNum(t[t.Length - 1], out loc2) & IsInt(t[t.Length - 2], out lc2);
            if (fits && detail == 1)
            {
                double dem, cap, ratio;
                fits = IsNum(t[i0], out dem) & IsNum(t[i0 + 1], out cap) & IsNum(t[i0 + 2], out ratio);
                d.HasDemand = true; d.Demand = dem; d.Capacity = cap; d.Ratio = ratio;
                d.Reference = string.Join(" ", t, i0 + 3, t.Length - 2 - (i0 + 3));
            }
            else if (fits)
            {
                double ratio;
                fits = IsNum(t[i0], out ratio);
                d.Ratio = ratio;
                d.Reference = string.Join(" ", t, i0 + 1, t.Length - 2 - (i0 + 1));
            }
            if (!fits)
            {
                detailUnread++;
                if (detailSample.Length == 0) detailSample = inner;
                pendingName = "";
                continue;
            }
            IsNum(t[t.Length - 1], out loc2); IsInt(t[t.Length - 2], out lc2);
            string name = string.Join(" ", t, 0, i0);
            d.Print = print.Order; d.Member = cur.Member; d.Group = group;
            d.Check = (pendingName + " " + name).Trim();
            if (d.Check.Length == 0) d.Check = lastText;
            d.LC = lc2; d.Location = loc2;
            m.CodeCheckDetails.Add(d);
            pendingName = "";
        }
        fail();

        foreach (AnlFrameCodeCheck r in order)
        {
            if (r.Status.Length == 0) continue;     // a block that was not read: named below
            m.CodeChecks.Add(r);
            print.Rows++;
        }
        print.Read = order.Count > 0;      // an output with no member result in a layout known here stays "not read"
        unread = unread.Distinct().Where(x => rows[x].Status.Length == 0).ToList();
        // an error: a member whose result is not read would pass as a member with no result
        if (unread.Count > 0)
            m.Error("CODE_CHECK_UNREAD", print.Command + ": the result of some members is in a layout that is not read, members " + AnlFrameReader.Ids(unread));
        if (order.Count == 0)
            m.Warn("CODE_CHECK_ROWS", print.Command + ": no member result was read from its output" + (code.Length > 0 ? " (" + code + ")" : ""));
        else if (listed != null)
        {
            var missing = listed.Distinct().Where(x => !rows.ContainsKey(x)).ToList();
            if (missing.Count > 0)
                m.Warn("CODE_CHECK_MISSING", print.Command + " printed no result for member(s) " + AnlFrameReader.Ids(missing));
        }
        if (detailUnread > 0)
            m.Warn("CODE_CHECK_DETAIL_UNREAD", print.Command + ": " + detailUnread + " line(s) of the checks behind the results were not read (the results themselves are), e.g. \"" + detailSample + "\"");
    }

    static bool Put(AnlFrameCodeCheck r, int k, string text)
    {
        double v;
        if (!IsNum(text, out v)) return false;
        r.F[k] = v; r.Has[k] = true;
        return true;
    }

    // a line without its box: | text |, |* text *|
    static string Inner(string line)
    {
        string s = line.Trim();
        if (s.StartsWith("|", StringComparison.Ordinal)) s = s.Substring(1);
        if (s.EndsWith("|", StringComparison.Ordinal)) s = s.Substring(0, s.Length - 1);
        s = s.Trim();
        if (s.StartsWith("*", StringComparison.Ordinal) && s.EndsWith("*", StringComparison.Ordinal) && s.Length > 1) s = s.Substring(1, s.Length - 2);
        return Squeeze(s);
    }

    // FAIL  Eq. H1-1b  1.071  104
    static bool Result(AnlFrameCodeCheck r, string inner)
    {
        Match mt = ResultLine.Match(inner);
        if (!mt.Success) return false;
        double ratio; int lc;
        r.Status = mt.Groups[1].Value.ToUpperInvariant();
        r.Critical = Squeeze(mt.Groups[2].Value);
        r.HasRatio = IsNum(mt.Groups[3].Value, out ratio) && !double.IsNaN(ratio); r.Ratio = r.HasRatio ? ratio : 0;
        IsInt(mt.Groups[4].Value, out lc); r.LC = lc;
        return true;
    }

    // 7.44 C  1333.95  -42.04  0.00: FX with its sense, MY, MZ, the location
    static bool Forces(AnlFrameCodeCheck r, string inner)
    {
        Match mt = ForcesLine.Match(inner);
        double loc;
        if (!mt.Success || !Put(r, 0, mt.Groups[1].Value) || !Put(r, 4, mt.Groups[3].Value) || !Put(r, 5, mt.Groups[4].Value) || !IsNum(mt.Groups[5].Value, out loc)) return false;
        r.Axial = mt.Groups[2].Value.ToUpperInvariant();
        r.Location = loc;
        return true;
    }
}
