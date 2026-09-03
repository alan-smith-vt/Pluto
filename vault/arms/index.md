# Arms

- [[vault/arms/plant-combined-to-viewer|plant-combined-to-viewer]] — **START HERE for the combined workflow**: pipes + steel + bbox fallback -> one CombinedToPluto file, end to end
- [[vault/arms/pipe-csv-to-viewer|pipe-csv-to-viewer]] — SP3D piping: v4 CSV -> PipeCsvReader (room filter, sizing, progress bar) -> PipeToPluto -> viewer
- [[vault/arms/sap-s2k-to-viewer|sap-s2k-to-viewer]] — SAP2000: `.s2k` model + results -> SapToPluto (C#, reuses SapReader) -> v4 shells + sidecar groups -> viewer
