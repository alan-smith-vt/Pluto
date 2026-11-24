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

The reader (`scripts/lib/readers/AnlFrameReader.cs`, with the tables besides the forces in `AnlFramePrints.cs`) was
written against the real layout of each table, which this probe's `.anl` gives. It reads every one of the commands
above; an output command it does not know is named as "not read yet". The model is built to show what a table's
layout depends on:

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
- **The code checks** come in three layouts, all read (one row per member per `CHECK CODE` command: PASS / FAIL,
  ratio, governing clause, case and location, the forces of that case):
  - AISC 360-16 and 360-22 print a box per member (`Member No`, `Status`, `Ratio`, `Loadcase`, `Location`, `Ref`
    on fixed lines), with all six forces in the code's axis names;
  - AISC 360-05 and 360-10, and the older codes at TRACK 0 and 1, print a table (`MEMBER TABLE RESULT/ CRITICAL
    COND/ RATIO/ LOADING/`) with FX, MY and MZ only;
  - `CODE AISC` (ASD 9th) and `CODE LRFD` (LRFD 3rd) at TRACK 2 print a drawing of the member with a `DESIGN
    SUMMARY` in its own units (kip-feet while the table says kip-inch).

  At TRACK 1 and 2 the 360 codes also print the checks behind the result (demand, capacity, ratio, clause): read
  as detail rows. A member STAAD does not design (a prismatic section) gets a message line, in three wordings:
  read as NOT DESIGNED with the message.
- **MEMBER INFORMATION** is one row per member (start and end joint, length with the offsets taken off, BETA,
  releases as twelve digits, `TRUSS`). It is kept, and checked against what the echo was read as. **MEMBER
  PROPERTIES** is two rows per member (profile, AX IZ IY IX, then AY AZ SZ SY), three when the profile name is
  long, its unit in the title line.
- **SECTION DISPLACEMENTS** is a block per member and case: global X Y Z at 13 stations, then a `MAX LOCAL DISP`
  line per member with `L/DISP`. Its `UNITS ARE - INCH` line reads as a command to the indexer, so the frame reader
  now gives an output heading back to the command that printed it.
- **JOINT COORDINATES** as printed are checked against the echo (18 of 18 agree).
- STAAD writes a NUL character into the profile line of the `CODE LRFD` drawing; it is read as a space.
