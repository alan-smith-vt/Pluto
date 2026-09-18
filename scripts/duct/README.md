# scripts/duct + scripts/sap — duct DCRs and the expansion-joint optimizer

Checks the members of an HVAC duct model in SAP2000 (demand / capacity ratios), and searches for the set of
expansion joints (full disconnects at duct joints) that brings every member within the limit. SAP is run once
with every candidate joint cut and rejoined by a stiff link; every joint set after that is scored offline by
superposition, so the search costs seconds, not SAP runs.

All Windows PowerShell 5.1 + C# 5 compiled on the fly (`Add-Type`). No installs, no packages, no Python.

## Setup (once per machine)

1. **Get the code.** Clone or copy the repo so that `scripts\duct\` and `scripts\sap\` sit side by side (the
   duct runners call `..\sap\`). Later updates: `git pull`; your config is gitignored and is never touched.
2. **SAP2000** installed (verified on 22 and 26). The scripts compile against `SAP2000v1.dll` in the install
   folder: the newest `C:\Program Files\Computers and Structures\SAP2000 NN` unless `SapDir` in the config (or
   `$env:PLUTO_SAP_DIR`) says otherwise.
3. **Config.** Copy `duct-config.example.psd1` to `duct-config.psd1` beside it and edit:
   - `WorkDir`: an empty folder for all outputs. One `WorkDir` (and one config) per model.
   - `Group`: the SAP frame group holding **every** duct member (check tees: a group that misses a branch breaks the spans).
   - `Cases`: output case / combo names **copied from SAP** (Display > Show Tables, or `combos.tsv` from a survey), not from a spreadsheet.
   - `Dcr`, `Release`, `Optimize`: commented in the example file.
   A second model: a second config anywhere, passed as `-Config <path>` to any `Run-*.ps1` in `duct\`.
4. **Every console session:** Windows PowerShell (not `pwsh`), then

   ```powershell
   Set-ExecutionPolicy -Scope Process Bypass
   cd <repo>\scripts\duct
   ```

5. **SAP state.** Open the model in SAP and **run the analysis** before any step that reads SAP. The scripts
   attach to the running SAP. They never write your model: edits happen on save-as copies under `WorkDir`, and
   the original is reopened afterwards. Leave SAP alone while a step runs; if SAP shows a dialog the script waits for it.

## The runners, in the order you would use them on a new model

Each runner prints timestamped steps, runs each step in its own `powershell.exe` (so it can be rerun in the same
console), and stops at the first failure.

| # | command | SAP? | time | what it answers |
|---|---|---|---|---|
| 1 | `.\Run-DuctDcr.ps1 -Check` | no | s | does the capacity code still reproduce the Mathcad template (1e-9)? |
| 2 | `..\sap\Run-SapSurvey.ps1 -Survey` | yes, read-only | min | what is in the model: sections, materials, groups, cases, combos |
| 3 | `.\Run-DuctCheck.ps1` | yes, read-only | min | DCRs of the model as it is |
| 4 | `.\Run-ComboCheck.ps1` | no | s | is the SRSS combo rebuilt exactly from its cases? (must be OK before any release) |
| 5 | `.\Run-ReleaseCheck.ps1` | yes | 10s of min | does release-by-superposition equal a real disconnect, for the joints in `Release.Candidates`? |
| 6 | `.\Run-Optimizer.ps1 -MaxCandidates 20` | yes | 10s of min | trial search on a spread sample: read the report before scaling up |
| 7 | `.\Run-Optimizer.ps1` with `MaxCandidates = 0` | yes | hours | the full search + direct validation of the chosen set |

### Run-DuctCheck.ps1 — DCRs of the model as it is

```powershell
.\Run-DuctCheck.ps1                          # template check -> survey + forces -> section table -> DCRs
.\Run-DuctCheck.ps1 -ReadSap:$false          # reuse the SAP exports, redo the tables and DCRs
.\Run-DuctCheck.ps1 -ReadSap:$false -RebuildSections:$false    # keep a hand-edited duct-sections.csv
```

Outputs under `WorkDir\ductcheck\`: `sap\` (`sections.tsv`, `forces.tsv`, ...), `tables\duct-sections.csv` (one row
per section: dimensions, material, allowable overrides, stiffener spacing: **edit this to change a section's
inputs**), `dcr\` (`dcr-summary.txt`, `dcr-frames.tsv` = one row per frame with the envelope and shear columns,
`dcr-stations.tsv` = every station and combo, `shear-capacity.tsv`).

### Run-ComboCheck.ps1

```powershell
.\Run-ComboCheck.ps1 [-ForcesTsv <path>]     # default WorkDir\ductcheck\sap\forces.tsv
```

Rebuilds `Cases.Combo18` as Dead + Steel ± SRSS(Seismic) per force component and compares with SAP's own rows.

### Run-ReleaseCheck.ps1 — validate the method on one or a few joints

```powershell
.\Run-ReleaseCheck.ps1                       # joints from Release.Candidates
.\Run-ReleaseCheck.ps1 -Candidates "123,456"
.\Run-ReleaseCheck.ps1 -SkipSap              # redo the offline steps and the report from the existing exports
.\Run-ReleaseCheck.ps1 -TestModel            # builds a small synthetic duct in SAP first (no real model needed)
```

Outputs under `WorkDir\release\`; read `released\release-report.html`. Expect released vs direct ~1e-9 of peak
and identical DCRs; link model vs connected ~1e-4.

### Run-Optimizer.ps1 — the search

```powershell
.\Run-Optimizer.ps1                          # everything: steps 1-8 below
.\Run-Optimizer.ps1 -MaxCandidates 20 -MaxJoints 10     # trial; overrides the config for this run
.\Run-Optimizer.ps1 -SkipSap                 # reuse the SAP exports: tables, search, release, report (no validation run)
.\Run-Optimizer.ps1 -SkipSap -MaxJoints 60   # same, letting the search go further
.\Run-Optimizer.ps1 -SkipSap -SkipSearch     # reuse opt\chosen.txt too: release + report only (minutes)
.\Run-Optimizer.ps1 -NoValidate              # full run without the direct model of the chosen set
.\Run-Optimizer.ps1 -TestModel               # on the synthetic duct
```

| step | does | output under `WorkDir\optimize\` |
|---|---|---|
| 1 connected | survey, forces, joint classes and spans of the untouched model | `connected\` |
| 2 select | the candidate list (`Optimize.Candidates`, `MaxCandidates`) | `opt\selected.txt` |
| 3 link | save-as; every candidate disconnected + stiff link + 6 unit load pairs; one run; export. **The long step** (hours and GB for several hundred candidates) | `link.sdb`, `link\` |
| 4 tables | section table; DCRs of the connected and the link model | `tables\`, `dcr-connected\`, `dcr-base\` |
| 5 optimize | greedy → swap → removal, offline | `opt\chosen.txt`, `steps.tsv`, `evaluations.tsv` |
| 6 direct | save-as; the chosen joints truly disconnected; run; export. **`direct.sdb` is the released model** | `direct.sdb`, `direct\` |
| 7 release | the chosen set by superposition, compared with 6; DCRs of both | `final\`, `dcr-final\`, `dcr-direct\` |
| 8 report | | `opt\optimizer-report.html`, `opt\released-map.html` |

Things to know:

- **Copy `opt\` before a rerun** you may want to compare (`opt-41`, ...): each run overwrites it.
- **Score a given joint set without searching:** write the joint names, comma-separated on one line, into
  `opt\chosen.txt` (the format the search writes), then `-SkipSap -SkipSearch`. The joints must be among the
  candidates of the link model (`opt\selected.txt`).
- **Validate a set without redoing the link model.** Step 6 only runs in a full run. By hand, with the analysed model open:

  ```powershell
  $opt = "<WorkDir>\optimize"
  $set = (Get-Content "$opt\opt\chosen.txt" -Raw).Trim()
  ..\sap\Run-SapRelease.ps1 -DirectModel -Candidates $set -SavePath "$opt\direct.sdb" -OutDir "$opt\direct" -Group "<Group>"
  [IO.File]::WriteAllText("$opt\direct\set.txt", $set)
  .\Run-Optimizer.ps1 -SkipSap -SkipSearch
  ```

  The direct run is compared only when `direct\set.txt` equals the chosen set; otherwise the report says it was not compared.
- **Score** = Σ max(frame envelope − `Dcr.Limit`, 0); ties by the number of frames over, then the maximum. Shear is
  reported beside the score, not in it, while `Dcr.NoShear = $true`.
- **Mechanisms.** A set whose release system has a pivot ratio below `Optimize.SingularPivot` frees a piece of duct
  and is rejected ("singular" in the log). `OnePerSpan` forbids two joints between the same pair of anchoring supports.
- **Supports** are classed from the support members' end releases at the duct: anchor / pinned / guide, with the
  restrained directions; spans end at anchors and pinned supports. Re-classify after a model change with
  `..\sap\Run-SapSurvey.ps1 -Candidates -Group "<Group>"` (writes to `WorkDir\optimize\connected`), then `-SkipSap`.

## The building blocks (called by the runners; useful alone when tracing a number)

```powershell
# SAP side (scripts\sap). Read-only unless noted.
..\sap\Run-SapSurvey.ps1 -Survey      [-OutDir <dir>] [-ModelPath X.sdb]          # summary.txt + sections / materials / frames / links / patterns / cases / combos / groups .tsv
..\sap\Run-SapSurvey.ps1 -Candidates  -Group "<Group>" [-AngleTol 5] [-OutDir <dir>]   # duct-joints.tsv, spans.tsv, candidates.tsv, candidates-summary.txt
..\sap\Run-SapSurvey.ps1 -Forces      -Group "<Group>" [-IncludeCases] -OutDir <dir>   # forces.tsv, kip-in, full precision
..\sap\Run-SapSurvey.ps1 -Probe                                                   # builds two cantilevers: local-axis / sign conventions. Close your model first
..\sap\Run-SapRelease.ps1 -LinkModel   -Candidates "12,34" -SavePath <x.sdb> -OutDir <dir> -Group "<Group>" [-Factor 1000]   # writes a COPY
..\sap\Run-SapRelease.ps1 -DirectModel -Candidates "12,34" -SavePath <x.sdb> -OutDir <dir> -Group "<Group>"                  # writes a COPY
..\sap\Run-SapRelease.ps1 -TestModel   -SavePath <x.sdb>

# Offline (scripts\duct). No SAP.
.\Run-DuctDcr.ps1 -Check
.\Run-DuctDcr.ps1 -Skeleton -SectionsTsv <sections.tsv> [-ForcesTsv <forces.tsv>] [-Material 304L] [-CarbonSections A,B] [-CapacitiesCsv <csv>] [-StiffenerSpacing 24] -OutCsv duct-sections.csv
.\Run-DuctDcr.ps1 -Evaluate -ForcesTsv <forces.tsv> -TablesDir <dir> -OutDir <dir> [-Limit 1.0] [-EndsOnly] [-NoShear] [-Combos "a,b"] [-ComboLS C]
.\Run-DuctRelease.ps1  -Release | -Report ...        # argument lists in the file header
.\Run-DuctOptimize.ps1 -Select | -Optimize | -Report ...
```

Without `-OutDir` the survey writes to `%TEMP%\sapsurvey` (except `-Candidates`, see above). Every script's header
comment lists its full arguments.

## Conventions the numbers rest on

- Units kip-in throughout. P > 0 is tension. Box section: a = `t3` along local 2, b = `t2`; M_a = M2, V_a = V2.
- The check reproduces a spreadsheet workflow: the combos in `Cases`, allowables × 1.5 unless `ComboLS` is A or B,
  the two frame ends only (`EndsOnly`), DCR = axial + M2 + M3, frame envelope = max axial + max M2 + max M3.
  Compression uses KL/r with the `K` and `L_ft` of `duct-sections.csv` and SAP's r.
- Materials: `duct-materials.csv` (beside the scripts; a copy in a `tables\` folder wins). `Fy_lambda_ksi` is the
  yield used in λc only, kept separate to match the source sheet.
- Limits today: Box sections only (others are skipped by `-Skeleton`); one material for all sections plus a
  `CarbonSections` list (the SAP material is surveyed but not used); joints are full 6-DOF disconnects; all load
  cases must be linear static.

## When something fails

- **"running scripts is disabled"**: the `Set-ExecutionPolicy` line above, once per console.
- **Compile error naming `SAP2000v1.dll`** or a missing method: wrong or unexpected SAP install; set `SapDir`.
- **"missing ...\forces.tsv (run without -SkipSap)"**: that `WorkDir` has no SAP exports yet.
- **A step hangs**: look at SAP for a dialog. A script that *starts* SAP itself (`-TestModel`, `-Probe`) returns only when that SAP closes.
- **Combo check not OK / wild DCRs**: case names in the config do not match SAP's exactly (spaces count).
- **Report shows old supports or joints**: the survey went to another folder; check the folder the survey printed.
- **Most trials singular**: spans are wrong (group misses members, or supports mis-classed): read `candidates-summary.txt` and `spans.tsv`.
- Anything else: bring back the console text from the failing step's banner down, plus `WorkDir\...\summary.txt`.

## Files

`DuctDcr.cs` capacities and DCRs · `DuctEvaluate.cs` tables in, DCR files out · `DuctCombo.cs` SRSS combo rebuild ·
`DuctRelease.cs` Woodbury release + comparisons · `DuctOptimize.cs` the search · `DuctReport.cs`, `DuctOptReport.cs` HTML ·
`Read-DuctConfig.ps1` config loader + step runner · `..\sap\SapSurvey.cs` read-only survey · `..\sap\SapRelease.cs` link / direct / test models.
Files are CRLF.
