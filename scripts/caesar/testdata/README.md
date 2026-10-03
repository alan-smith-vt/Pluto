# scripts/caesar/testdata: generic CAESAR II test job

The reference fixture for the CAESAR II arm. It is a small made-up job, built for testing only. It is never a project model.

| file | what |
|---|---|
| `generic.cii` | CAESAR II neutral file of the test job |
| `generic.xlsx` | CAESAR II output written to Excel, one report per tab (displacements, restraints, restraint summary, ...) |
| `generic-coords.*` | optional: CAESAR's node coordinate report, used to check the rebuilt geometry |

## Contents of the job

The job should have about 30 nodes and include:
- a long-radius 90 deg bend;
- a concentric reducer and an eccentric reducer;
- a valve, a flange pair and an expansion joint;
- a welding tee;
- one of each restraint type in use:
  - ANC, +Y, Y;
  - GUI on a horizontal pipe and GUI on a vertical pipe;
  - LIM with a gap;
  - a skewed restraint;
  - a spring hanger;
  - an imposed-displacement anchor;
- OPE, SUS and EXP load cases.

## Scrub before committing (Pluto rule: nothing that identifies a project, person or employer)

- **.cii:**
  - Blank the free-text lines under `#$ VERSION`: project, user, date, paths. Keep the first, numeric line.
  - Blank the units-file name line under `#$ UNITS`.
  - Element names, line numbers, node names and restraint tags must be made-up.
- **.xlsx:** Excel File > Info > Check for Issues > Inspect Document > Document Properties and Personal Information > Remove All. This clears the author, last-modified-by, company and manager fields. Then check each tab's title block for a job path, a "Licensed to" line or a date.
- **File names:** generic, as in the table above.
