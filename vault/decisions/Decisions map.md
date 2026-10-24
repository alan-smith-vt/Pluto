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
- [[vault/decisions/2026-10-23-pipe-model-in-viewer|2026-10-23-pipe-model-in-viewer]] — the viewer shows the whole pipe model before CAESAR sees it: loads, element properties, the build's checks (each with its place, click to fly) and the model's source are sidecar sections the CAESAR export owns and rewrites, from the runner's model or one imported from the `.cii`; hidden groups show where each temperature, pressure, material and insulation applies; load cases stay CAESAR's.
- [[vault/decisions/2026-10-24-uniform-wind-carry-forward|2026-10-24-uniform-wind-carry-forward]] — CAESAR carries a UNIFORM or WIND block forward to every later element until another changes it: the model holds what acts on each element, the reader carries a block onto the elements after it, the writer puts a block where a load starts or changes and a zero / off block where it stops; corrects the 2026-10-22 reading.
- [[vault/decisions/2026-10-24-hangers-nozzles-in-model|2026-10-24-hangers-nozzles-in-model]] — spring hangers and flexible nozzles move from the carried MISCEL_1 tail into the pipe model, made and edited in the workbook and the TOML; imports stay byte-identical (each keeps its lines), a tail Pluto cannot take apart is carried whole and refuses edits.
- [[vault/decisions/2026-10-22-lossless-pipe-model|2026-10-22-lossless-pipe-model]] — models are defined in Excel or TOML only (the viewer confirms, never edits); one `PipeModel` gives the `.cii`, the preview and later the `.s2k` (a check model of CAESAR's: same pipe, loads and equivalent supports); a `.cii` import carries everything losslessly (overlay-and-carry, byte-identical when unedited, checked on every import); an exact model JSON as the workbook's base; compare by meaning against CAESAR's re-export.
