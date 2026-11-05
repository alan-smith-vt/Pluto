---
title: A frame model goes to STAAD.Pro exactly or not at all
status: current
created: 2026-11-05
---

*↑ [[vault/Pluto Home|Home]] › [[vault/decisions/Decisions map|Decisions map]]*

## Decision

Any frame model (a workbook, an `.s2k`, a `.frame.json`) can be written as a STAAD.Pro input file,
`Build-Frame -Std` (`FrameStd`). The rule is **exact or refused**:

- What STAAD holds exactly is written.
- What it cannot hold exactly refuses the whole file. Every refusal is reported at once, each naming its objects.
- What leaving out cannot change in the structure is listed as not written: modal cases, envelopes.

The mapping is in [[vault/format/frame-to-staad|frame-to-staad]]. The choices made with the user:

| Question | Chosen |
|---|---|
| Up axis | **Rotate to STAAD's Y up**: SAP (X, Y, Z) is written (X, Z, −Y). A BETA per member keeps SAP's local 2 / 3 as STAAD's y / z. |
| Sections | **STAAD's AISC table where the name matches, else exact numbers.** W shapes and whole-weight channels go by name; everything else is `PRIS` with SAP's own properties. SAP's property modifiers are folded in, so a modified table section is written as numbers. |
| Links | **A nonlinear link refuses the model** (gap, hook, friction, multilinear, damper), naming each. Linear links are written: a grounded link as support springs, a two-joint link acting only along its axis as a truss member with EA / L = KE. Any other two-joint link is refused. |
| Loads | **Patterns are reference loads, cases are primary loads.** A case that continues another (OPE from SUS) is one primary load holding both cases' loads, from zero. With linear links that is the same result. Linear Add, SRSS and Abs Add combinations are written. Envelope and Range Add are listed, not written. |
| Joints with local axes | **STAAD inclined supports.** |
| Offsets | **Only where exact.** Cardinal points, joint offsets, and end lengths with a rigid-zone factor of 1 become STAAD member offsets. A factor below 1 is refused. |
| End of file | **The analysis and Pluto's prints.** `PERFORM ANALYSIS` (`PDELTA 30 ANALYSIS SMALLDELTA` when the cases ask for P-Delta), the joint, member and support prints Pluto's `.anl` tools read, then `FINISH`. |

> [!info]- Why exact or refused
> Analyses that differ for a silent reason are worse than none. A refusal names the object and the reason, and the
> user fixes the model or chooses to leave the item out.
>
> A notice covers only what cannot change the structure:
> - a load case STAAD is not given;
> - a combination it cannot express;
> - a SAP table about design or output.

> [!info]- Considered and not chosen
> - **Keep Z up with `SET Z UP`.** Every BETA and default axis would follow STAAD's Z-up rules, and STAAD's own
>   documentation treats that as a special case. Rotating keeps STAAD's ordinary conventions.
> - **Every section as numbers (`PRIS`).** Exact, but a STAAD user loses the catalog section for design. The table
>   is used where the name is confirmed.
> - **Gaps as tension/compression-only members.** Not the same element as SAP's gap (no stiffness of its own, no
>   opening). Refused instead.
> - **Partly rigid end zones (factor between 0 and 1).** STAAD offsets are rigid. Refused.
> - **A primary load per pattern, combinations for cases.** The cases would no longer be what STAAD analyses, and a
>   P-Delta case cannot be a combination.

> [!info]- Open until STAAD has run the probe
> The Technical Reference was read through excerpts only (its site was not reachable from the development machine).
> `-StdProbe` writes one model for SAP and STAAD alike, so their results can confirm what the excerpts leave open:
> - the BETA sign;
> - the TEMP gradient signs;
> - spring units per degree;
> - the inclined support's axes;
> - offsets and the loads on rigid end zones.
>
> Until then [[vault/format/frame-to-staad|frame-to-staad]] marks these as unconfirmed.
