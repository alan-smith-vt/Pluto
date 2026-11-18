---
title: Old viewer → new viewer merge plan
status: current
created: 2026-09-01
---

*↑ [[vault/Pluto Home|Home]] › [[vault/viewer/Viewer map|Viewer map]]*

# Old viewer → new viewer merge plan

> **Outcome.** Decisions (user, 2026-09-01): the production dialect for the predicate schema; Phase A end to end
> first, then review.
> - **Phase A, predicates: done 2026-09-01.** `viewer/scripts/predicates.js` + panel (`index.html`,
>   `styles/predicates.css`), pointer routing and hooks in `viewer.js`, `disarm` cross-wiring in `sectionCut.js`,
>   the predicate handoff in `features.js`, the spec amended in [[vault/format/features-sidecar|features-sidecar]].
>   The 8 headless engine tests pass (`viewer/tests/test_predicates.js`).
> - **Phase B, section-cut persistence: done 2026-09-04** (below).
> - **Phase C, reconciliation: open.** Of the two port candidates the sanitized-lib audit (Notes vault) added, the v3
>   writer's `StressNames` / `DispNames` / `BuildComponents` catalogs are in the v4 writer since 2026-09-03
>   ([[vault/format/raw-viewer-writer|raw-viewer-writer]]). The old exporter's symmetric-clip contract (absMax
>   envelope appended as the last LC) is still open, and so is undo / redo.

Source: `archive/ViewerSource/` (the pre-Pluto
viewer, transcribed 2026-09-01, no `// sanitized` markers — appears intact).
Target: `viewer/` (the v4 viewer). Companion audit of the whole sanitized drop: the sanitized-lib audit, in the
Notes vault (`Tools/Pluto Sanitized Audit`).

## What the old viewer contains

| module | lines | what it is | disposition |
|---|---|---|---|
| `init.js` | 165 | scene/camera/lights + global state | **superseded** by `viewer.js` |
| `app.js` | 739 | `.bnl` parser, jet colors, morph-displacement animate, target orb, pick-to-focus, model/LC/component dropdowns | **superseded** (v4 reader, turbo LUT, deform section, focus orb/tween all exist) |
| `OrbitControls.js` | 1044 | controls | **superseded** (`viewer/lib/OrbitControls.js`) |
| `sectionCut.js` | 493 | **persistent cut definitions**: many named cuts (point+axis+length), groups, visibility, orb+line visuals, JSON export/import → consumed by C# `SectionCuts.cs` | **port as persistence layer** (Phase B) |
| `predicate.js` | 1388 | **predicate module**: expression list of AND/OR trees, plane / finite-plane leaves, negation, tree UI w/ drag-drop, inspector, highlight overlay, extents ghost, JSON export/import → consumed by C# `Groups.cs` | **port** (Phase A — the main event) |

The new viewer's `sectionCut.js` is a different tool (single interactive probe line:
samples the displayed field at ~200 stations, trapezoidal integral, isolate). Old and new
section-cut modules are complementary, not competing: new = probing UX, old = the
persistent definition list the C# analysis consumes.

## The binding constraint: what production C# parses

`scripts/lib/readers/Groups.cs:355-417` parses the OLD viewer's JSON dialect:
`kind: "plane" | "finitePlane"` (+ `and`/`or` ops), `tol`, `normal_tol_deg`, `negated`,
`width/length/angle_deg`. The [[vault/format/features-sidecar|features-sidecar]] draft
(2026-08-25) sketched a different leaf (`side: positive` half-space, no tolerances,
`not` as an op). **Neither side implements the draft.** Old dialect is what runs.

**Recommendation (decision 1):** amend the sidecar spec's `predicates` section to absorb
the production dialect — envelope/ids/name/target from the spec, node grammar from the old
viewer (`kind` plane/finitePlane/and/or, `negated` flag on any node, `tol`,
`normal_tol_deg`, `angle_deg`). Then the tree the C# side reads out of
`predicates.items[i].tree` is byte-compatible with what `Groups.cs` already parses; the
production-env change is just "read the tree from the sidecar envelope instead of
predicates.json". Slab semantics (|dist| ≤ tol) stay — they're what makes click-placement
forgiving; the spec's half-space `side` can be added later as another leaf kind if needed.

## Phase A — predicates module (`viewer/scripts/predicates.js`, `FEAPredicates`)

New IIFE module in the new viewer's house style (`FEAxxx`, DOM in `index.html`,
`fea-*` CSS, shared globals, `requestRender()`).

1. **Spec amendment** — update [[vault/format/features-sidecar|features-sidecar]]
   `predicates` section per decision 1; log in the v4 decisions log.
2. **Geometry caches** — element centroid + normal per domain, node positions.
   Old `buildPlateCache` logic, but built from `feaBuild`/`feaModel` views; lazily,
   invalidated in `onModelLoaded`/`onModelCleared`. Shell first; beams later
   (centroid-only test, skip the normal-angle check).
3. **Core evaluation** — port `evaluateLeaf`, `intersectChildren` / `unionChildren` /
   `effectiveSet` (negation = universe minus), `buildPredAxes` (u/v from world axis +
   `angle_deg`). Near-mechanical; the math is sound.
4. **`FEAPredicates.resolveMembers(predicateId, envelope)`** — the hook
   `viewer/scripts/features.js` already calls (`resolvePredicateMembers`). Returns `[{domain, ids}]` /
   `[{domain:'nodes', nodeIds}]` with **real IDs** (spec: never indices) — translate
   matched indices through `elemIds`/`nodeIds`. Groups referencing `predicateId` then
   color live, unlocking predicate → group → **Color by groups** in one session.
5. **UI** — right-side tab panel (pattern of `scTab`/`scPanel`): action bar
   (New / Adjust / Delete / AND / OR / ¬), tree pane with drag-drop (into / before /
   after / drop-on-empty = new expression), inspector (leaf editor: position w/
   scroll-nudge, tolerances, finite extents; op editor: AND↔OR, dissolve), match-count
   badges, highlight overlay (plates fill / node points), finite-extents ghost plane.
6. **Placement modes** — armed-tool registry shared with `FEASectionCut` so
   Ctrl+click routes to exactly one armed tool (old code cross-called
   `clearPredModes`/`clearSectionCutModes`; do it once, properly).
7. **Sidecar integration** — predicates live in the features envelope
   (`predicates.items`); load populates the module, edits mutate the envelope,
   plus an **Export features** button (writes the whole envelope via
   `FEAFeatures.exportJson`, which exists but has no UI). "Group from predicate"
   button appends a group with `source.predicateId` + runtime member.
8. **Legacy import shim** — accept old `predicates.json` (v3 nested + v1 flat) and
   convert into the envelope, so anything saved in the production env imports cleanly.

**Bugs in the old module that will NOT be ported** (transcription would reproduce them):
- `predicate.js:434-435` — `var idk` / `if (idx …)`: tree multi-select (ctrl-click) throws.
- `predicate.js:754` — sets `cosNormalTol` but eval reads `cosNormTol`: editing angle
  tolerance in the inspector silently never takes effect. **Results-affecting.**
- `predicate.js:1137` — `model.elements[i][0]` vs `.nodeCount` in `buildPlateOverlay`
  tri counting: quad overlay under-allocates (`[0]` is undefined → always 2 tris path…
  actually always falsy → miscounts tris for 3-node plates).
- `predicate.js:1382` — `err.essage` in import error alert.
- `app.js:622-634` — `undo()` / `redo()` are wired to Ctrl+Z/Y but **defined nowhere**
  (undo stack state exists in `init.js`, implementation was never written or was lost).
  Undo/redo is therefore a fresh feature, not a port — deferred, own backlog item.

## Phase B — section-cut persistence — DONE 2026-09-04

Kept the new probe UX; the old module's management layer now lives in `sectionCut.js`
on the sidecar `sectionCuts` section ([[vault/format/features-sidecar|features-sidecar]]):
named cut list with groups + visibility toggles, a marker for every shown cut (selected
one haloed), select re-attaches probe / plot / isolate, Adjust (Ctrl+click) and Delete,
numeric centre entry, sidecar round trip with unknown keys preserved, legacy
`section_cuts.json` import shim (inches → model unit, panel normal by probing). Stored as
plane `{point, normal}` + `bounds.up` (panel normal), direction derived, `axis` key when
it is a global axis (list colour). Test: `viewer/tests/test_sectioncuts.js`. Sloped cuts: one
toggle cycles Straight → Sloped (axis bent onto the panel once) → **Wrapped** (the probe
marches over the surface, turning corners; same day, checked in Chrome on TANK-A). C# `SectionCuts.cs` (2074 lines,
audit pending) consumes the definitions.

## Phase C — cross-check and polish

Open. The sanitized-lib audit is in (Notes vault, `Tools/Pluto Sanitized Audit`): diff its ViewerSource feature
inventory against this plan (anything missed), reconcile with
[[vault/viewer/Viewer overview|Viewer overview]], and record the outcome in
[[vault/handoffs/handoff-current|handoff-current]]. Candidates already visible: the symmetric-clip contract and
undo / redo (top); nothing else — shell features all have v4 equivalents.

## Verification

- Headless Node recipe (`three.min.js` under `vm`; now `viewer/tests/test_predicates.js`, run as in
  [[vault/viewer/Viewer overview|Viewer overview]] › Tests): build
  `FEASample.buildSampleBlobV4()`, define predicate trees in code, assert matched
  real IDs per domain — including negation, AND/OR nesting, finite extents, angle.
- Round-trip test: envelope → resolveMembers → export → re-import → identical.
- Browser check by user (the Chrome extension did not connect until 2026-09-04).

## Sequencing

A1 (spec) → A2-A4 (engine + hook, headless-testable) → A5-A6 (UI) → A7-A8 (sidecar +
shims) → B → C. Engine-before-UI keeps every step verifiable without the browser.
