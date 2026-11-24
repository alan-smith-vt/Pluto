# STAAD probes: the `.anl` reader, and STAAD round 4

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

## Used by

- `viewer/tests/readback_staad.js --probe` checks `StaadToPluto`'s export of `anl-prints-probe.anl` (the viewer arm,
  `scripts/staad/Run-StaadFrame.ps1`) against the numbers this file prints.
- For code ratios per case the arm wants one `LOAD LIST <case>` + `CHECK CODE` per case
  (vault/arms/staad-frame-to-viewer.md). The probe has none yet; the layout is the same, as the reader reads each
  `CHECK CODE` alone, but a real run of it is still to do.

## STAAD round 4: `staad-round4-probe.std` (2026-11-24)

The round for the triangle SAP <-> frame workbook <-> STAAD (vault/format/frame-to-staad.md, "A model made in STAAD" and
"Merging"). A small 3D frame with a plate. It settles three things STAAD has to judge:

1. **Units.** Every kind of number stands under a different `UNIT` from the first (FEET KIP):
   - materials in MMS KN;
   - table sections in INCHES, a PRIS strut in CM KN;
   - plate thickness, offsets and a spring release in INCHES;
   - support springs in METER KN;
   - member loads in INCHES and METER KN, joint loads in FEET, a support displacement in INCHES.

   The analysis runs under METER KN, so the first print after it is in those units. Pluto's copy has every number in
   FEET KIP, so **its results must be the original's, to the last printed digit**.
2. **Load envelopes** (TR.40). Three envelopes, with case 103 listed in two (STAAD keeps it in the last).
   `LOAD LIST ENV 1` then prints the reactions of the loads it holds. Strength and deflection (`DFF`) checks run under
   `LOAD LIST ENV 1 2`, and a check under the one-load envelope 3.
3. **A code check per case:** `LOAD LIST 101` + `CHECK CODE ALL`, and the same for 102 and 103.

Joint 8's springs are written `FIXED BUT MY KMX 2000 KMZ 2000`. The reader takes `FIXED BUT MX MY MZ KMX 2000 KMZ
2000` (a released DOF then its spring) as the same support.

### Running it (production machine)

`...` stands for `powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File`, run from the repo after a
pull. `<dir>` is a folder outside the repo.

1. Copy `staad-round4-probe.std` to `<dir>` (STAAD writes its output beside the file).
2. **STAAD -> workbook:** `... scripts\frame\Build-Frame.ps1 -In <dir>\staad-round4-probe.std -Xlsx -Out <dir>\round4`.
   - It should read with no `[refuse]` line and say "read exactly".
   - `round4.xlsx` has the STAAD loads sheet filled, and `round4.design.xlsx` (the design workbook) has the codes,
     parameters and run.
3. **Workbook -> STAAD:** `... -In <dir>\round4.xlsx -Std`, which writes `<dir>\round4.std` (it reads
   `round4.design.xlsx` beside it).
4. **STAAD.Pro:** run `staad-round4-probe.std` and `round4.std`. Note any error or warning that one has and the other
   has not.
5. **Workbook -> SAP:** `... -In <dir>\round4.xlsx -S2k` writes `<dir>\round4.s2k`.
   - In SAP2000, File > Import > SAP2000 .s2k; keep the import log.
   - Then File > Export > SAP2000 .s2k, as `<dir>\round4.sap-export.s2k`. No analysis is needed.
6. **SAP -> STAAD, with the merge:**
   `... -In <dir>\round4.sap-export.s2k -Merge <dir>\round4.xlsx -Std -Out <dir>\round4_fromsap`. The `[merge]` lines
   should name 26 STAAD commands and 13 load numbers carried.
7. **STAAD's own save:** open `round4.std` in STAAD.Pro's editor, change nothing, save. Copy it as
   `<dir>\round4_saved.std`, then `... -In <dir>\round4_saved.std -Merge <dir>\round4.xlsx -Xlsx -Out <dir>\round4_saved`.

### What to send back

- **The two `.anl` files** (`staad-round4-probe.anl`, `round4.anl`). Replace the licensee line and any folder path with
  `<removed>`, as for the probe above.
- **The runner output** of steps 2, 3, 5, 6 and 7 (the `[refuse]`, `[notice]`, `[merge]`, `[read]`, `[warn]` lines).
- **From SAP:** its import log, and `round4.sap-export.s2k`.
- **From the other steps:** `round4_fromsap.std`, and `round4_saved.std` (STAAD's save).

### What it settles

| Question | Settled by |
|---|---|
| Unit conversion of every kind of number | the two `.anl` agree on every displacement, reaction and member force, every case |
| The `UNIT` in force at the analysis carried | the first reaction print is in kN and m in both |
| `DEFINE ENVELOPE` where `-Std` writes it (after the combinations, before the analysis) | `round4.std` runs |
| A load in two envelopes | the cases `LOAD LIST ENV 1`'s reaction print lists: 101 102 (TR.40's rule), or 101 to 103 |
| Checks under envelopes and per case | the `.anl` reader takes `LOAD LIST ENV`; StaadToPluto puts envelope 3's and 101's to 103's checks into their cases |
| SAP takes the workbook's output | the import log names no Pluto table; the envelope combinations import |
| The merge from SAP's own export | `round4_fromsap.std` is `round4.std` as STAAD reads it |
| STAAD's own save of carried commands, load numbers and envelopes | step 7 reads exactly |

