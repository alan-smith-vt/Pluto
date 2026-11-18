---
title: Viewer map
status: current
created: 2026-09-03
---

*↑ [[vault/Pluto Home|Home]]*

# Viewer map

Hub for `vault/viewer/` — the Three.js viewer in `viewer/` (plain JS, no build step). It
reads v4 and v3 through `viewer/scripts/format/pluto.js`, renders per-corner shell fields
and extruded beams, and hosts the user-authored features (groups, predicates, section cuts)
that round-trip through the sidecar. It also draws what the exporters put in the sidecar: supports, links, release
bits, loads, element properties and the build's checks.

- [[vault/viewer/Viewer overview|Viewer overview]] — what the viewer does today: predicates flyout, beam rendering, geometry-only mode, Color by groups, adaptive facets, Z-up, support symbols, reload, the pipe model's loads, checks, properties and node numbers, and a SAP frame model's releases, links, support kinds, loads by pattern / case / combination and frame properties; the viewer's tests.
- [[vault/viewer/model-rail|model-rail]] — several models in one scene: the Models rail, overlays at true coordinates with their own sidecar groups (stage 1 done 2026-10-02); stages 2–3 (active-model switching, cuts across models) as tasks; open: the `worldOffset` path bug.
- [[vault/viewer/old-viewer-merge-plan|old-viewer-merge-plan]] — porting the old production-side viewer's modules: Phase A predicates (done 2026-09-01) and Phase B section-cut persistence (done 2026-09-04); Phase C reconciliation items open.

Decision 2026-09-03: the SapViewer fork's tabbed-panel UI redesign was **not** adopted; the
existing layout stays.
