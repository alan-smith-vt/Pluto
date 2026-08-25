# Viewer

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
