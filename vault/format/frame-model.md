---
title: Frame model (lossless SAP2000 .s2k, typed by ownership, model JSON)
status: draft
created: 2026-11-05
---

*↑ [[vault/Pluto Home|Home]] › [[vault/format/Format map|Format map]]*

# Frame model

`FrameModel` (`scripts/arms/frame/FrameModel.cs`) is the SAP2000 frame model the frame workbook edits: piping,
ducting and steel as frames, with links, springs, releases, loads, cases and combinations. It is **the `.s2k` file
itself, typed**: every object owns the rows that describe it, and every value is held as the file's own text. Usage:
[[vault/arms/frame-workbook|frame-workbook]]. The decision: [[vault/decisions/2026-11-05-frame-model|2026-11-05-frame-model]].

> [!info]- S2kDoc: the file, lossless
> `S2kDoc` (`scripts/lib/readers/S2kDoc.cs`) keeps a `.s2k` exactly:
> - the preamble (`File ... was saved on ...`);
> - each `TABLE:  "NAME"` line;
> - each row: its physical lines (continued with ` _`), its fields in order, with their quoting;
> - every other line as written: blank lines, `;;;` banners, `$` comments, `END TABLE DATA` and whatever follows it;
> - each line's own terminator, and whether the file ends with one.
>
> Bytes are read as ISO-8859-1, so every byte maps to one char and back. An edited row is rewritten in SAP's style
> (three spaces, `Key=Value`, a value with a space, a comma or nothing in quotes), its fields in their order; every
> other row stays as its lines. `IS2kTables` gives the same row view as `Sap2kParser`, which now implements it too, so
> `Sap2kReader` and `SapToPluto` read either.
>
> **Checked (2026-11-05, Linux):** written back byte for byte, with the same rows as `Sap2kParser`, for
> - the tank golden `example.s2k` (614 KB, 27 tables, 10,558 rows);
> - the probe;
> - `scripts/frame/testdata/rack.s2k`;
> - a synthetic file with continuation lines, a banner, a comment, mixed line endings, a non-ASCII label and no final
>   newline (the label is the one row `Sap2kParser` reads differently: it reads the file as UTF-8).

> [!info]- Ownership: which object a row belongs to
> `FrameS2k.OwnerOf(table, row)` decides by the table and its first field. The object's **main** table makes it; the
> others attach to it by label.
>
> | Object | Main table | Also owns every row of |
> |---|---|---|
> | joint | `JOINT COORDINATES` | `JOINT …` tables keyed by `Joint` (restraints, springs, local axes, masses …) |
> | frame | `CONNECTIVITY - FRAME` | `FRAME …` tables keyed by `Frame` (sections, releases, local axes, insertion, modifiers, auto mesh, design …) |
> | section | `FRAME SECTION PROPERTIES 01 - GENERAL` | `FRAME SECTION PROPERTIES …` keyed by `SectionName` |
> | material | `MATERIAL PROPERTIES 01 - GENERAL` | `MATERIAL PROPERTIES …` keyed by `Material` |
> | link property | `LINK PROPERTY DEFINITIONS 01 - GENERAL` | `LINK PROPERTY DEFINITIONS …` keyed by `Link` |
> | link | `CONNECTIVITY - LINK` | `LINK …` keyed by `Link` (property assignment, local axes) |
> | group | `GROUPS 1 - DEFINITIONS` | `GROUPS …` keyed by `GroupName` (its members) |
> | load pattern | `LOAD PATTERN DEFINITIONS` | tables keyed by `LoadPat` that are not loads (auto wind / seismic) |
> | case | `LOAD CASE DEFINITIONS` | `CASE - …` keyed by `Case` |
> | combination | `COMBINATION DEFINITIONS` (all its rows) | n/a |
> | load | each row of `JOINT LOADS …` and `FRAME LOADS …` is an object of its own | n/a |
> | (control) | `PROGRAM CONTROL` | n/a |
>
> Rows nobody owns stay in the source and are written as they were: areas, solids, constraints, grid lines, analysis
> options, a row naming an object that does not exist. Uids are given in file order, so reading the same file twice
> gives the same uids. That is what lets the writer find the base of each object.

> [!info]- Typed access and editing
> - **Typed accessors read and write through the text.** Examples: `FrameJoint.Xyz` / `SetXyz` (SAP's `GlobalX/Y/Z`
>   when the row has them, else worked out from `COORDINATE SYSTEMS`, cartesian or cylindrical; a moved joint in a
>   user system is written in GLOBAL), `Restraint`, `Spring`, `LocalAngles`; `FrameMember.Section`, `Angle`,
>   `Releases`, `PartialFixity`, `ReleaseMask`, `CardinalPoint`, `PropertyModifiers`; `FrameLink.JointJ` ("" for a
>   one-joint link).
> - **A number set is written in the shortest form that reads back exactly** (G15, else G17), and a number set to its
>   own value keeps its text (`FrameRec.SetNum`).
> - **New rows are complete:** the key, then the table's other fields at SAP's defaults in SAP's order
>   (`FrameModel.NewRec`, templates for the tables the model writes).
> - **`FrameS2k.Rename(model, object, label)`** sets the label in the object's rows and in every row of the model that
>   names it: frames' JointI / JointJ, links, a case's monitor joint, group members (`ObjectType` + `ObjectLabel`),
>   loads, combinations naming a case or another combination.
> - **`FrameS2k.Delete(model, object)`** removes the object and what only describes it: a joint's or frame's loads, its
>   group memberships. It refuses, naming them, while another object still needs it (a frame on a joint, frames of a
>   section, a case of a pattern).

> [!info]- Writing: overlay and carry
> `FrameS2k.ToDoc` reads the source again as the base. Each object is matched to its base by uid, and each of its rows
> to the base row at the same place in the same table.
> - **Unchanged text is not touched.** The row stays as written, continuation lines and all.
> - **A changed field** is rewritten in its row.
> - **A new row** goes after the object's other rows in that table. A new table goes where SAP's order puts it:
>   PROGRAM CONTROL first, then alphabetical.
> - **A row the object no longer has** is removed, and a deleted object takes all its rows with it.
> - **Rows nobody owns** follow renames. A row whose key names a deleted object goes too. A row that only uses a
>   deleted object (an area on a deleted joint) refuses the write, naming the table.
>
> An unedited model writes its source back byte for byte.
>
> **Checked (2026-11-05)** on `rack.s2k`, each edit changing only its own lines:
> - a moved joint: one line, its `GlobalX/Y/Z` too;
> - a renamed joint: 5 rows across four tables (frames, link, case monitor joint, the joint);
> - a deleted frame: 6 rows in five tables, including auto-mesh, its loads and its group member;
> - a deleted joint still in use: refused;
> - releases added, removed and partly fixed (the partial-fixity table inserted in order);
> - a section changed (SectionType follows) and a dimension edited;
> - a frame angle cleared and set;
> - a new joint, frame, one-joint link and point load (complete rows);
> - a joint under the wall area deleted: refused;
> - a cylindrical joint moved (rewritten in GLOBAL).

> [!info]- JSON (`pluto-frame-model` v1)
> `FrameModelJson` stores the source's text and every object with its uid and rows. Everything is text, so the JSON is
> exact by construction. Non-ASCII is escaped, so the file stays ASCII. The base is not stored: the source read again
> is the base.
>
> `Hash` (SHA-256 of the JSON) is the model's fingerprint.
>
> **Checked:** write → read → write is identical, the `.s2k` from the JSON is byte-identical to the source, and an
> edit survives the trip. Covered: rack, the tank golden (1.4 MB of JSON), the probe, the synthetic file.

> [!info]- Not yet
> - Typed views of link-type tables beyond their DOF rows; combinations and cases typed beyond their rows.
> - Changing the model's unit set (Phase 6).
> - The confirmed / unconfirmed flag per table and field, set from the SAP probe ([[vault/arms/frame-workbook|frame-workbook]]).
