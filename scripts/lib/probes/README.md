# Probes for the STAAD `.anl` reader

`anl-prints-probe.std` is a small made-up frame (32 members, 18 joints, 6 primary cases, 8 combinations) that ends
with the usual output commands, in the usual order, and then checks the code again under other design parameters:

```
CHECK CODE ALL
PRINT JOINT COORDINATES
PRINT MEMBER INFORMATION
PRINT MEMBER PROPERTIES
UNIT INCH KIP
LOAD LIST <strength cases>
PRINT SUPPORT REACTIONS
PRINT MEMBER FORCES
LOAD LIST <service cases>
PRINT JOINT DISPLACEMENTS
PRINT SECTION DISPLACEMENTS
```

The reader (`scripts/lib/readers/AnlFrameReader.cs`) reads the member forces today and names the other tables as
"not read yet". Their parsers are to be written against the real layout of each table, which this probe's `.anl`
gives. The model is built to show what a table's layout depends on:

- two six-digit joint and member numbers (the column widths);
- enough rows for page breaks inside the force and displacement tables;
- one shape of each kind (W, channel, tee, double angle, single angle, tube, pipe, prismatic);
- a group name, a wrapped list and comment lines in the input (how STAAD echoes them);
- releases, an offset, BETA, truss members, three kinds of support;
- two load lists;
- a code check with members at TRACK 2, TRACK 1 and TRACK 0, and one of each shape at TRACK 2;
- after the usual prints, **seven more code checks** on twelve of the members, because design parameters vary from
  model to model and the layout follows them:

  | Extra | Code and method | Under |
  |---|---|---|
  | (main) | AISC 360-16 LRFD, with FU, KY, KZ, LY, LZ, UNB, UNT, CB, NSF, RATIO, MAIN | every case |
  | 1 | AISC 360-16 ASD | the strength list |
  | 2 | AISC 360-16 ASD with a deflection check (DFF) | the service list |
  | 3 | AISC 360-10 LRFD | the strength list |
  | 4 | AISC ASD 9th edition (`CODE AISC`) | the strength list |
  | 5 | AISC LRFD 3rd edition (`CODE LRFD`) | the strength list |
  | 6 | AISC 360-05 LRFD | the strength list |
  | 7 | AISC 360-22 LRFD (newer versions of STAAD only) | the strength list |

## Running it (production machine)

1. Open `anl-prints-probe.std` in STAAD.Pro and run the analysis.
   - If STAAD stops at one of the extra code checks (a code this version does not have), delete that block,
     from its `* ==== extra` line to the next one, and run again. What was printed before it is still good.
     Note which block it was: that is a finding too.
   - If none of the checks is like the one your models use, add yours as one more extra at the end, before
     `FINISH`: a `PARAMETER` block with your usual lines, then `CHECK CODE` on a few members.
2. Open the `.anl` in a text editor and **remove what names the licensee or the machine**, and nothing else:
   - the banner on the first page: the line naming who the program is licensed to;
   - any line with a folder path (the input file's path).

   Replace each with `<removed>` so the line count stays the same.
3. Save it beside the probe as `anl-prints-probe.anl`, commit it to this branch and push.
   - If STAAD saved the `.std` again in its own form (for example the section database form), commit that file
     too, as `anl-prints-probe-saved.std`.

## What the run showed (STAAD.Pro 2025, 2026-11-24)

`anl-prints-probe.anl` is the probe's output as STAAD wrote it, with the licensee line and the input file's path
replaced by `<removed>`. Every block ran, all eight code checks included.

- **The reader took it as it is:** 18 joints, 32 members, 14 load cases, all 384 member end-force rows, every
  section and family, no errors.
- **The echo** is in capitals, and comment lines are echoed with a line number.
- **Support reactions and joint displacements** are joint tables: the joint and the case on a joint's first row,
  the case alone on the rest, units in the table's title line (`SUPPORT REACTIONS -UNIT KIP  INCH`,
  `JOINT DISPLACEMENT (INCH RADIANS)`). Both are read now: 36 and 90 rows, supports x cases and joints x cases
  of their load lists. A six-digit joint number fits its column.
- **The AISC 360 code checks** (2005, 2010, 2016, 2022; LRFD and ASD) print a boxed block per member at every
  TRACK level, with `Status`, `Ratio`, `Loadcase`, `Location` and `Ref` on fixed lines. `CODE AISC` and
  `CODE LRFD` print their own, older layouts. Not read yet.
- **MEMBER INFORMATION** is one row per member (start and end joint, length, BETA, releases); **MEMBER
  PROPERTIES** two rows per member (profile, AX IZ IY IX, then AY AZ SZ SY), its unit in the title line.
  **SECTION DISPLACEMENTS** and the printed **JOINT COORDINATES** are there too. Not read yet.
