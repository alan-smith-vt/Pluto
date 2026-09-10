# Viewer map

*↑ [[vault/Pluto Home|Home]]*

Hub for `vault/viewer/` — the Three.js viewer in `viewer/` (plain JS, no build step). It
reads v4 and v3 through `viewer/scripts/format/pluto.js`, renders per-corner shell fields
and extruded beams, and hosts the user-authored features (groups, predicates, section cuts)
that round-trip through the sidecar.

- [[vault/viewer/Viewer overview|Viewer overview]] — what the viewer does today: predicates flyout, beam rendering, geometry-only mode, Color by groups, adaptive facets, Z-up. Headless test recipes.
- [[vault/viewer/old-viewer-merge-plan|old-viewer-merge-plan]] — porting the old production-side viewer's modules: Phase A predicates (done 2026-09-01, awaiting the user's browser check), Phase B section-cut persistence (next), Phase C reconciliation items.

Decision 2026-09-03: the SapViewer fork's tabbed-panel UI redesign was **not** adopted; the
existing layout stays.
