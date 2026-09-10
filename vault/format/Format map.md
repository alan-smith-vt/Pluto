# Format map

*↑ [[vault/Pluto|Home]]*

Hub for `vault/format/` — the common model every arm converges on and the viewer consumes.
Rules that never move: the binary is results + geometry, write-once, C#-written in
production; everything a user *defines* goes in the sidecar JSON; identity strings go in
the `LABL` block, categories are sidecar groups.

- [[vault/format/v4-schema|v4-schema]] — **current target**: block directory, element domains (shells, beams), `LABL`, profiles (results / geometry-only), decisions log.
- [[vault/format/features-sidecar|features-sidecar]] — the sidecar: groups (explicit IDs or `predicateId`), predicates in the production dialect, section cuts, units and `worldOffset`.
- [[vault/format/raw-viewer-writer|raw-viewer-writer]] — the C# writer (`scripts/lib/writers/RawViewerWriter.cs`): two-phase Write / Append usage, beams and sections, labels, geometry-only, the append methods, the arms that call it.
- [[vault/format/v3-schema|v3-schema]] — legacy fixed-header format, frozen; loads through the viewer shim (`v3Reader.js`). Reference for old files only.
