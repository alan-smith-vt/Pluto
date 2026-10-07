# Decisions map

*↑ [[vault/Pluto Home|Home]]*

Hub for `vault/decisions/` — one dated note per design decision, filed as `YYYY-MM-DD-<slug>.md`.
Older decisions are recorded inline: the v4 format decisions log in
[[vault/format/v4-schema|v4-schema]], the predicate dialect in
[[vault/format/features-sidecar|features-sidecar]], and the per-session decisions in the
handoff log (Notes vault, `Tools/Pluto Handoffs`).

- [[vault/decisions/2026-10-03-beam-taper|2026-10-03-beam-taper]] — beam `ELEM` slot 4 = `sectionIdxB + 1` (tapered members: reducers, valve halves; 0 = straight, old files unchanged) and optional META `upAxis` for Y-up models.
- [[vault/decisions/2026-10-03-imported-restraints|2026-10-03-imported-restraints]] — restraints imported from a solver model are sidecar `supports` items + node groups; their loads go in the binary as beam components of kind `restraint`. The binary `point` domain was considered and not chosen. Built in CAESAR stage 2 (2026-10-04), drawn by `supports.js`.
- [[vault/decisions/2026-10-07-pipe-builder|2026-10-07-pipe-builder]] — pipe-stress models from a TOML config by C# (`PipeBuilder`) into a solver-neutral `PipeModel` in CAESAR's terms; the CAESAR neutral file first, SAP `.s2k` later from the same model; temperatures as ΔT from the ambient; couplings as 6 CNODE restraints N → N+1 and free ties; CAESAR's own position conventions; no unconfirmed block layout guessed.
