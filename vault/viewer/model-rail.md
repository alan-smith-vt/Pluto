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
  - Beams only at stage 1; shells too since stage 2.
  - Colours come from the overlay's own sidecar: every element group, in envelope order, last one wins. Unlike the Groups tab, it ignores `hidden`. Members in no group are grey.
  - Plant steel groups are near-white by the exporter's choice; pipes use the size ramp.
- **Placement:** true world coordinates.
  - For each file: world = local + its exporter recenter (`model.units.worldOffset`) + any viewer recenter.
  - Lengths are converted to the main model's unit (m / mm / cm / in / ft). An unknown unit is flagged on the row and left unconverted.
  - Overlay geometry is built about its own bbox centre and placed by `mesh.position`, so float32 stays small.
- **Where things are (2026-10-02):**
  - Each row shows its world centre; overlays also show their exporter offset, or "NO offset".
  - Per-row zoom-to (which also resets near/far, so a distant overlay is not clipped) and "Fit all models".
  - The status line reports the overlay's and the main model's centres and their distance, and flags "far apart".
- **Plan rotation (2026-10-02):** each overlay has `rot 0/90/180/270` about Z, applied in world coordinates. It is remembered per overlay name in the browser.
  - When the centres are far apart, the status tries all four and names the one that brings them together.
  - **Found on the first real overlay:** the SAP duct model's axes are the plant's turned 90°. SAP (x, y) = plant (y, −x), so the plant overlay needs **rot 270**.
  - With a rotation or unit change, the readout also gives the point in the overlay's own coordinates.
- **Picking:** the nearest visible member across all models wins. The readout names the overlay, member label, section and group, and gives World xyz in true coordinates.
- **Checked** on the synthetic duct shifted to survey coordinates (963000, 662000), with a synthetic plant steel file from `CombinedToPluto` in inches:
  - Placement matches by hand: s0 at survey x 963060 lands at scene −180 under the 963240 recenter.
  - The readout gives true coordinates.
  - Ghost and remove work; there are no console errors.
  - Viewer tests pass.

## Stage 2: done 2026-10-08 (`models.js`, `modelMesh.js`, `overlays.js`, `features.js`)

Module contracts are in [[vault/viewer/Viewer modules|Viewer modules]].

- **Active model:** click a rail row's name to make that model active.
  - The Groups tab, its filter, eyes, colours and reorder, and Save features act on the active model.
  - The other models keep their last look.
- **Shells in overlays:** an overlay draws its shells and beams together, under one placed group.
  - Both are painted by its groups and pickable.
  - The readout says "overlay shell" or "overlay beam".
- **Colours:**
  - Every model is painted by its own groups.
  - An overlay's groups paint unless they say `hidden`. Exporter files carry no flag, so they look as they did in stage 1.
  - With group painting off, an overlay is drawn in its **model colour**, set by the swatch on its row. The primary keeps its field colouring.
- **X-ray:** a checkbox on every row (primary included) makes that model additive-translucent, shells and beams.
  - It replaces stage 1's "ghost".
  - The primary's x-ray covers its beams too (FEABeams.setXray); the beam panel's old X-ray checkbox is gone.
  - Dark field colours are lifted in x-ray so additive blending still shows them.
- **Not done:** clicking a member of another model does not offer to make that model active. Use the rail instead.
- **Checked 2026-10-08** in the browser, on synthetic duct files (centrelines as primary with a mesh overlay and the probe graph, then a mesh as primary):
  - The Groups tab and file name follow the active row, and Save targets the overlay's sidecar.
  - X-ray works on overlay and primary shells.
  - The model colour shows with painting off.
  - The eye hides overlay shells and marks only the overlay dirty.
  - Picking works on overlay shells and beams.
  - No console errors; all viewer tests pass.

## Stage 3 (task)

- [ ] Section cuts clip every visible model. They are defined on the active model, in world coordinates.
- [ ] Predicates evaluate on the active model.

## Open: `FEAFeatures.worldOffset()` reads the wrong path

- It reads top-level `units.worldOffset`, but the spec and every exporter (`FeaturesSidecar.cs`) put it under **`model.units`**. So it has always returned null.
- **Effect:** for plant files, which are recentred by the exporter, the readout's World xyz and spatial predicates work in the exporter's local frame, not plant coordinates. The two agree with each other.
- **Fixing it** gives true coordinates but shifts any saved predicate whose coordinates were read off the old readout.
- `overlays.js` already reads the correct path for its own placement. **The user decides** whether to fix the main path.
