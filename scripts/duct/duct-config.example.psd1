# Pluto duct scripts: production-side settings. Copy to duct-config.psd1 (gitignored) beside the scripts and edit.
# Every runner (Run-DuctCheck.ps1, Run-ComboCheck.ps1, Run-ReleaseCheck.ps1) reads duct-config.psd1 unless
# -Config <path> is given. PowerShell data file: strings in quotes, lists as @('a', 'b'), $true / $false.
@{
    # ---- machine ----
    WorkDir = 'C:\Temp\hvac'          # every output goes under here (one sub-folder per runner)
    SapDir  = ''                      # '' = newest install; else e.g. 'C:\Program Files\Computers and Structures\SAP2000 22'

    # ---- model ----
    Group = 'DUCT_ALL'                # SAP frame group holding every duct member

    # Output case names exactly as SAP lists them (copy from SAP, not from Excel).
    Cases = @{
        Dead    = '1 DEAD'
        Steel   = 'Steel_Loading'
        Seismic = @('3 Seismic X', '4 Seismic Z', '5 Seismic Y - vert')   # the terms of the nested SRSS combo
        Combo18 = '18 BLC 7B'         # = Dead + Steel +/- SRSS(Seismic), rebuilt from the cases after a release
        Combo19 = '19 BLC 7C'         # linear additive: one load vector, corrected as it is
        Combo20 = '20 BLC 7D'
    }

    # ---- DCR check (the Excel workflow) ----
    Dcr = @{
        ComboLS        = 'C'          # A or B = no increase; anything else = 1.5 x allowables on every check
        Material       = '304L'       # row of duct-materials.csv for every section ...
        CarbonSections = @()          # ... except these, which get CARBON, e.g. @('DUCT_6x6')
        EndsOnly       = $true        # the two frame ends only
        NoShear        = $true        # V2 / V3 left out of the governing DCR
        Limit          = 1.0
    }

    # ---- expansion-joint release ----
    Release = @{
        Candidates      = @('1234')   # joint names to release (Run-ReleaseCheck.ps1: one or a few; validation)
        StiffnessFactor = 1000        # stiff link k = factor x the stiffer adjoining frame (EA/L, 4EI/L)
    }
}