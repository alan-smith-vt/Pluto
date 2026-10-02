---
title: Model rail — several models in one scene (overlays)
status: current
created: 2026-10-02
---

*↑ [[vault/Pluto Home|Home]] › [[vault/viewer/Viewer map|Viewer map]]*

Several files in one scene, e.g. plant steel and pipes over a SAP duct model. The **Models** list in the Model section is the rail: one row per loaded model, with show/hide.

- **The active model** drives everything model-specific: the Groups tab, Save features, load case / component / legend, section cuts and predicates.
- **Inactive models** keep drawing as they last were.
- **Each model keeps its own sidecar**, bound by its own geometryHash. Nothing is merged.

## Stage 1: done 2026-10-02 (`viewer/scripts/overlays.js`)

- **Rail:** the loaded model is the active row (show/hide). Overlays are rows with show/hide, **ghost** (translucent) and remove (×). "Add overlay…" takes a `.bin` and its `.features.json` together, paired by base name. URL form: `&ov=<bin url>[,<features url>]`, repeatable.
- **What an overlay draws:**
  - Beams only. A file with shells draws its beams and says so.
  - Colours come from the overlay's own sidecar: every element group, in envelope order, last one wins. Unlike the Groups tab, it ignores `hidden`. Members in no group are grey.
  - Plant steel groups are near-white by the exporter's choice; pipes use the size ramp.
- **Placement:** true world coordinates.
  - For each file: world = local + its exporter recenter (`model.units.worldOffset`) + any viewer recenter.
  - Lengths are converted to the main model's unit (m / mm / cm / in / ft). An unknown unit is flagged on the row and left unconverted.
  - Overlay geometry is built about its own bbox centre and placed by `mesh.position`, so float32 stays small.
- **Picking:** the nearest visible member across all models wins. The readout names the overlay, member label, section and group, and gives World xyz in true coordinates.
- **Checked** on the synthetic duct shifted to survey coordinates (963000, 662000), with a synthetic plant steel file from `CombinedToPluto` in inches:
  - Placement matches by hand: s0 at survey x 963060 lands at scene −180 under the 963240 recenter.
  - The readout gives true coordinates.
  - Ghost and remove work; there are no console errors.
  - Viewer tests pass.

## Stage 2 (task)

- [ ] Click a rail row to make it active. Per-model contexts for the viewer's global state:
  - `viewer.js`: feaModel / feaSet / feaBuild / mesh, LC and legend state.
  - `viewerBeams.js`: view / build / material.
  - `features.js`: envelope / resolved / palette.
- [ ] Switching the active model swaps the Groups tab, Save features, load case / component / legend.
- [ ] Inactive models keep their last look: colours, field, LC.
- [ ] Clicking a member of another model offers to make that model active.
- [ ] Shells in overlays.

## Stage 3 (task)

- [ ] Section cuts clip every visible model. They are defined on the active model, in world coordinates.
- [ ] Predicates evaluate on the active model.

## Open: `FEAFeatures.worldOffset()` reads the wrong path

- It reads top-level `units.worldOffset`, but the spec and every exporter (`FeaturesSidecar.cs`) put it under **`model.units`**. So it has always returned null.
- **Effect:** for plant files, which are recentred by the exporter, the readout's World xyz and spatial predicates work in the exporter's local frame, not plant coordinates. The two agree with each other.
- **Fixing it** gives true coordinates but shifts any saved predicate whose coordinates were read off the old readout.
- `overlays.js` already reads the correct path for its own placement. **The user decides** whether to fix the main path.
