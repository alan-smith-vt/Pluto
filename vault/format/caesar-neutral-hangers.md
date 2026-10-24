---
title: CAESAR II neutral file - spring hangers (MISCEL_1)
status: current
created: 2026-10-24
---

*↑ [[vault/Pluto Home|Home]] › [[vault/format/Format map|Format map]]*

# CAESAR II neutral file: spring hangers

Spring hangers are not element blocks. They sit in MISCEL_1, after the material numbers and the nozzles and before the four execution-option lines, and CONTROL line 1 field 3 counts them. Pluto reads them into the pipe model (`PipeModel.Hangers`, [[vault/format/pipe-model|pipe-model]]), so the workbook and the TOML can make and edit them. Layout in our own words, from CAESAR II's user guide and CAESAR-written files.

**What CAESAR does with a hanger** follows from its values:
- **No rate and no constant load:** CAESAR **designs** it. It picks a spring from the hanger table after its hanger-design load cases (weight, then operating), and says so on import (notes 69E, 70E).
- **A spring rate and an installed (cold) load:** a **spring as given**. CAESAR does not design it.
- **A constant-effort load:** a **constant support**.

> [!info]- The layout
> All lines start with 2 blanks; integer fields are 13 wide, reals 13 wide (`0.000000E+00` in 15.01, `25.0000` in 11.00). "6 per line" lists wrap after six values.
>
> | Lines | What |
> |---|---|
> | 1 | defaults: hanger table (integer), allowed load variation %, rigid support displacement criteria, maximum travel, short-range springs (1 = yes), multi-load design option. CAESAR 15.01 writes `1 25 0 9999.99 1 1` |
> | 1 | design options (integers): operating cases for the design (1), compute actual cold loads (0 / 1), hanger operating loads (0–3), the case of the actual cold loads, multi-load option (1–7). 15.01 writes `1 0 0 0 0` |
> | (n + 5) / 6 | the hanger nodes, 6 per line, in CAESAR's order |
> | 4 per hanger | 2 lines of its 11 values (rate, load variation, rigid criteria, available space, cold load, hot load / user operating load, maximum travel, multi-load option, hardware weight, constant-effort load), then 2 text rows: tag and GUID (length, then the text; `0` when blank) |
> | 1 per hanger | anchors to free while it is designed: node 1, node 2, what each frees (1 Y, 2 XY, 3 ZY, 4 XYZ, 5 all) |
> | 4 × (n + 5) / 6 | four lists, 6 per line: hangers at the location (0 = one), hanger table, short-range springs allowed (0 / 1), connecting node (0 = ground) |
>
> 9999.99 is CAESAR's "not given" for the travel limit (and the space, in the guide's words). With no hangers the section is absent and CONTROL says 0.

> [!info]- How Pluto reads and writes it
> - **Reading** (`CaesarPipeImport`): the tail is taken apart into nozzle lines, the hanger section and the 4 option lines. When the parts add up, each hanger becomes a `PipeModelHanger` with its lines as written; otherwise the tail is carried whole and its hangers cannot be edited (the workbook and `-Cii` say so).
> - **Writing** (`CaesarNeutralWriter.WriteHangers`): the defaults lines as written (a new model gets 15.01's); every list over its own lines while the count is unchanged; each hanger from its own lines with only changed fields rewritten; a new hanger in the job's layout, with the job's default table written out as CAESAR 15.01 does. CONTROL counts the model's hangers. An unedited job comes back byte for byte.
> - **Compare** matches hangers by meaning (node, CNODE, kind, the design data given), a table of 0 as the job's default.
> - **The viewer** shows each hanger as an arrow under the pipe; the readout says whether CAESAR designs it or takes it as given.

> [!info]- Checks
> - `hangers` (info): the hangers CAESAR will design, and that it asks for its hanger-design cases first. `hangers-given` (info): the springs and constant supports it takes as given.
> - `hanger-twice` (warning): two entries at one node. CAESAR's way is one entry with the number of hangers at the location.
> - `hanger-rate-only` (warning): a rate without its installed load.
> - `hanger-free` (warning): an anchor to free that nothing holds to ground.
> - `off-model`, `cnode-off-model`: a hanger node, or its connecting node, on no element.
> - The writer refuses a node outside 1–8,000,000, a free code outside 1–5, a node to free without its code, and hanger edits on a job whose tail was carried whole.

> [!info]- What is confirmed (2026-10-24)
> - **Read and written back byte for byte:** three public CAESAR 11.00 jobs (three designed hangers with freed anchors; two springs as given on table 16, metric; one with the multi-load options) and a CAESAR 15.01 job with one designed hanger. Edits to them read back the same.
> - **Not yet seen in CAESAR:** a hanger section Pluto wrote (new hangers, a spring as given, a constant support, a freed anchor, a CNODE, a count above one). The combined probe job asks CAESAR to import one.
> - **From the guide only:** what the defaults and design options mean beyond their names; 0 hangers at a location read as one.
