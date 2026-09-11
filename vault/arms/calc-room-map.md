---
title: Calc PDFs → room map (usage)
status: current
created: 2026-09-11
---

# Calc PDFs → room map — usage

*↑ [[vault/Pluto Home|Home]] › [[vault/arms/Arms map|Arms map]]*

Which pipe-stress calc PDFs are relevant to each room. Chain: calc PDF → run names listed under "Lines covered in this system:" → room(s) of each run from the v4.2 pipe CSV → room → calcs. Not a viewer arm: CSV in, CSV out. Design and verification record live in the Voyager vault (`40-join-paths/Calc to Room Map.md`); the code ships here because Voyager never leaves the D: machine.

Code: `scripts/voyager/Export-CalcRuns.ps1`, `Join-CalcRooms.ps1`, `PdfText.cs`. PowerShell 5.1, no packages: PDF text comes from the Windows PDF IFilter through ~100 lines of COM interop compiled by `Add-Type`.

## Run

```powershell
# 1. calc PDFs -> File,Run   (text cached in C:\Temp\calc_text; -ReuseCache to re-parse only)
.\scripts\voyager\Export-CalcRuns.ps1 -PdfRoot '<calc folder>' -Recurse -Out C:\Temp\calc_runs.csv

# 2. join to the v4.2 pipe CSV (RunName, Room)
.\scripts\voyager\Join-CalcRooms.ps1 -CalcRuns C:\Temp\calc_runs.csv -PipeCsv C:\Temp\pipe_v4_sized.csv -Out C:\Temp\room_calcs.csv
```

Outputs: `room_calcs.csv` (Room, File, Runs), `room_calcs_by_run.csv` (long), `room_calcs_unmatched.csv` (calc runs absent from the model), `calc_runs_noheader.txt` (PDFs without the header — scanned or worded differently).

## Knobs

- `-RunPattern` (default `^F-`): deliberately loose; names carry odd characters (a `"` was seen). The tail rule bounds the search, not the pattern.
- `-TailWords 40`: the filter returns each page as one run of words with no line breaks, so the table end is detected as 40 consecutive non-run tokens (page headers/footers interleave the table; a true gap is longer).
- `-ExcludeDir` (default `Archive, Superseded, Old`): skip a PDF if any folder name under the root **contains** one of these, case-insensitive. Pass your own list to replace the default.
- `-StageLocal`: for network folders. Copies each PDF to `%TEMP%\calc_stage`, extracts, deletes; the CSV keeps the network path. The filter seeks all over the file, so direct network reads ran ~8 s per file vs ~1 s local.
- `-ExtraPages 3` / `-MaxPages 10`: reading stops 3 pages after the one holding the header, hard cap 10 pages (calcs run to ~1000 pages; the table is near page 7). The header default has two leading spaces, which is how the filter renders the line break before it.
- Rooms `a/b/c` on straddling runs are split on `/`; a calc covers every room any listed run touches.

## Status

Verified 2026-09-11 on synthetic text and three local report PDFs under PS 5.1 and 7. Not yet run on the real calcs.
