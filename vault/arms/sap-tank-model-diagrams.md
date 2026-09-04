---
title: Tank model diagrams
status: current
created: 2026-09-04
---

*↑ [[vault/Home|Home]] › [[vault/arms/Arms map|Arms map]]*

Pictures of the SAP tank model, generated from the config by
`pythonTools/sap/doc_figures.py configs/example.toml` (rerun after a builder change).
Details: [[vault/arms/sap-tank-builder|sap-tank-builder]], [[vault/arms/ring-wall-load-path|ring-wall-load-path]],
[[vault/arms/settlement-profiles|settlement-profiles]].

## Loads

![[vault/arms/assets/tank-loads.svg|860]]

## Shell local axes

![[vault/arms/assets/tank-local-axes.svg|860]]

`M11` is carried by bars along 1. `run_sap.py` verifies the axes over the OAPI after import.

## Beam nodes

![[vault/arms/assets/tank-beam-nodes.svg|860]]

Eave: wall, roof and `ROOF_RING` share one joint. Rim: three coincident joints, two gap
links, both compression-only.
