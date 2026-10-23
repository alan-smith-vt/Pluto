# Format map

*↑ [[vault/Pluto Home|Home]]*

Hub for `vault/format/` — the common model every arm converges on and the viewer consumes.
Rules that never move: the binary is results + geometry, write-once, C#-written in
production; everything a user *defines* goes in the sidecar JSON; identity strings go in
the `LABL` block, categories are sidecar groups.

- [[vault/format/v4-schema|v4-schema]] — **current target**: block directory, element domains (shells, beams; tapered beams via `ELEM` slot 4), `LABL`, META `upAxis`, profiles (results / geometry-only), decisions log.
- [[vault/format/features-sidecar|features-sidecar]] — the sidecar: groups (explicit IDs or `predicateId`), predicates in the production dialect, section cuts, supports (drawn as 3D symbols; exporter items merged by name), units and `worldOffset`.
- [[vault/format/pipe-model|pipe-model]] — the one pipe-stress model (`PipeModel`, CAESAR's terms) from a TOML, a `.cii` or a `.pipe.json`: lossless CAESAR import (overlay-and-carry: raw text per record, block and slot, only changed columns rewritten, carried sections travel with their element, an unedited job comes back byte for byte), joints, seeds, units, loads, the exact model JSON (`pluto-pipe-model` v2) and compare by meaning (branch `pipestress`).
- [[vault/format/caesar-neutral-loads|caesar-neutral-loads]] — the CAESAR II neutral file's load blocks (FORCMNT / DISPLMNT: node + one line per vector, 9999.99 = free; UNIFORM: 9 vectors with g flags; WIND: type + shape factor; one uniform / wind block per loaded element, no carry-forward), CAESAR's restraint codes 1–62, the bend radius codes (−1.0101 Long, −3.0303 3D, −4.0404 Short, −5.0505 5D; −2.0202 and −6.0606 … −9.0909 no radius, confirmed in CAESAR 15.01), and which of it CAESAR's documentation gives vs. CAESAR-written files confirm (FORCMNT, UNIFORM, WIND by a 15.01 export) (branch `pipestress`).
- [[vault/format/raw-viewer-writer|raw-viewer-writer]] — the C# writer (`scripts/lib/writers/RawViewerWriter.cs`): two-phase Write / Append usage, beams and sections, labels, geometry-only, the append methods, the arms that call it.
- [[vault/format/v3-schema|v3-schema]] — legacy fixed-header format, frozen; loads through the viewer shim (`v3Reader.js`). Reference for old files only.
