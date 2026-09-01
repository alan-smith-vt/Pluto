# Viewer

Predicates (2026-09-01, ported from the old viewer — plan:
[[vault/viewer/old-viewer-merge-plan|old-viewer-merge-plan]]):
- `scripts/predicates.js` (`FEAPredicates`), flyout tab under the section-cut tab.
- Trees of plane / finite-plane slab tests (AND/OR, per-node negate), evaluated over
  shell centroids+normals, beam midpoints (no normal test; a beam pick takes its axis
  as the plane normal), and nodes. Drag-drop tree editing, inspector with scroll-nudge,
  match highlighting, finite-extents ghost.
- Stored in the features sidecar `predicates` section in the **production dialect**
  (`kind`/`tol`/`normal_tol_deg`/`negated` — what C# `Groups.cs` parses); points are
  world/plant coordinates (both recenter offsets subtracted at eval).
- `resolveMembers(predicateId)` feeds `features.js` groups → **Group from pred.** button
  colors a predicate's members via Color by groups; Export downloads the whole sidecar;
  Import accepts a sidecar or a legacy `predicates.json` (v1/v3).
- Known old-viewer bugs fixed at port (idx typo, cosNormTol write, overlay tri count);
  undo/redo was never implemented in the old code — still open.

Beam support (2026-08-25, first cut):
- Beam domain renders as solid extruded sections (`beamGeometry.js`), linear end-A→end-B color interpolation (`FEAShaders.beamFragment`).
- Own component dropdown + auto range in the **Beams** panel; shares colormap / abs / alarm with shells.
- Envelope, strength and Global-DSR views draw beams neutral grey — no beam envelopes yet.
- Hover/pick reports beam ID, section, `t` along the axis, end node IDs.
- Deformed shape: beam ends follow the beam domain's displacementVector; shares the shell scale.
- Hollow sections (BOX, PIPE) render outer outline only.

Not yet: beam envelopes / min-max folds, a beam calc card, line-only mode for large models, `nStations > 2` rendering (format supports it; updater reads slots 0 and 1).

Geometry-only / beam-only files (2026-08-25): a file with no shell domain loads through a
synthetic empty shell view; zero load cases short-circuits `selectLC` (beams draw neutral,
groups/labels still work). Pipe facet count is adaptive (24 → 12 → 8 → 6 by beam count) so
~150k pipes stay renderable; a line-only mode is still the next step for very large models.
