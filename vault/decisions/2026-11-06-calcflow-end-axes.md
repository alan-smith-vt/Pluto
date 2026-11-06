---
title: A connection's demand is taken in its member end's own axes, x away from the joint
status: draft
created: 2026-11-06
---

*↑ [[vault/Pluto Home|Home]] › [[vault/decisions/Decisions map|Decisions map]]*

## Decision

**CalcFlow takes a member end's printed forces in the end's own axes:**
- x along the member, pointing away from the joint;
- y the member's local y (BETA included);
- z = x × y.

At the start joint (end A) these are STAAD's member axes. At the end joint (end B) they are STAAD's axes turned 180° about y: Fx, Fz, Mx and Mz change sign, while Fy and My keep theirs. The sign stays STAAD's (the force on the member at that end). A library frame maps these end axes to a template's axes, and `on_connection` negates them to give the force on the connection.

**SRSS and ABS combinations in scope are flagged** (`UNSIGNED_CASES`): their end forces are formed from magnitudes, so their signs and concurrency are not those of a real load.

## Why

- **A sign error made this necessary.** STAAD prints both ends of a member in the same member axes, with x running from A to B. In the fixture, one base plate column runs bottom-up and the other top-down. Through the same frame, the bottom-up one bore on its plate and the top-down one showed 41 kip of uplift under the same compression.
- **The alternative was worse.** Without end axes every frame would need an A and a B variant, and the register would have to know which way each member was drawn, which nobody controls in a STAAD model.
- **Choice of rotation axis.** Turning about y (not z) keeps y, the member's web direction, so "web up" for a beam and the BETA meaning for a column hold at both ends.

## Status

- **2026-11-06:** in the planner (`Planner.EndAxes`). Tested: both fixture columns bear down under the same compression combination.
- **Needs the engineer's sign-off**, with each template's frame, against one connection checked by hand. The example frames are marked unconfirmed until then.
