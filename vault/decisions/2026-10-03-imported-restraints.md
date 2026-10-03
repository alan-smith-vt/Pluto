---
title: Imported restraints in the sidecar, their loads in the binary
status: current
created: 2026-10-03
---

*↑ [[vault/Pluto Home|Home]] › [[vault/decisions/Decisions map|Decisions map]]*

## Decision

Restraints imported from a solver model (CAESAR II first) are written by the exporter into the sidecar.
- **Symbols** come from items in the `supports` section ([[vault/format/features-sidecar|features-sidecar]]).
  - Planned shape: `{id, name, nodeIds, dof, stiffness}`.
  - Extension keys: `kind`, `direction`, `gap`, `friction`, `cnode`, `tags`, `source`.
  - Items are built from the model file alone.
- **Type colours and show/hide** come from node groups, one per restraint combination at a node.
- **Loads on the restraints** per load case are results, so they go in the binary.
  - They are beam components of kind `restraint` (`FX FY FZ |F| MX MY MZ |M|`), written at every beam end that touches a restraint node.
  - Elsewhere they are NaN.
  - Values are per-node totals.
  - The kind is hidden from the beam contour list. A viewer layer (`supports.js`) reads them to colour the symbols and fill the readout.

## Why

- **No format change.** No new domain family; [[vault/format/v4-schema|v4-schema]] §3.1 already puts categories in the sidecar.
- **Hash independence.** The binary's `geometryHash` does not depend on the results workbook.
- **Rule kept.** Results stay in the binary.
- **Considered and not chosen:** a binary `point` domain (node, DOF mask, direction, gap, stiffness, per-load-case loads).
  - It would draw symbols even without the sidecar, at the cost of a new family and a reader contract.
  - The user chose the sidecar (2026-10-03).
- **Cost:**
  - Symbols need the `.features.json` loaded beside the `.bin`.
  - Several restraints at one node share one load total; CAESAR's Restraint Summary reports the same.

## Status

- Stage 1 writes the node groups.
- The `supports` items, the load components and `supports.js` are stage 2 of the CAESAR arm ([[vault/arms/caesar-to-viewer|caesar-to-viewer]]).
