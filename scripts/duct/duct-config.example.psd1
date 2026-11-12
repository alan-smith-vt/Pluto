# Pluto duct scripts: production-side settings. Copy to duct-config.psd1 (gitignored) beside the scripts and edit.
# Every runner (Run-DuctCheck.ps1, Run-ComboCheck.ps1, Run-ReleaseCheck.ps1) reads duct-config.psd1 unless
# -Config <path> is given. PowerShell data file: strings in quotes, lists as @('a', 'b'), $true / $false.
@{
    # ---- machine ----
    WorkDir = 'C:\Temp\hvac'          # every output goes under here (one sub-folder per runner)
    SapDir  = ''                      # '' = newest install; else e.g. 'C:\Program Files\Computers and Structures\SAP2000 22'
    # AddinDir = 'C:\Temp\hvac'       # files shared with the Navisworks / Revit add-ins (room boxes, service codes);
                                      # default C:\Temp\hvac, where the add-ins read them. Change only for a test copy.

    # ---- model ----
    # Primary duct systems (Export-DuctsViewer.ps1 -Revit): a part whose run / system name contains one of these
    # (ignoring case) belongs to it; the largest connected component per name is the system, the rest fragments.
    # Optional viewer colour per system as '<name>=#rrggbb' (its fragments get a desaturated variant).
    DuctSystems = @('SYSA=#4f8cff', 'SYSB=#ff8a3d', 'SYSC')
    # Room codes for Export-DuctsViewer.ps1 -Revit when -Room is not given (each also takes its variants: values that
    # start with it). -Room overrides; -Room '*' = the whole building. @() = no default (whole building).
    DuctRooms = @('R101', 'R102')
    # Second mains: '<name>=<Revit ElementId>' promotes the '<name> fragments' component holding that part to a main
    # of its own ('<name> 2', '<name> 3', ...), e.g. a run cut in two by the room scope. match.txt says what each did.
    DuctMains = @()
    # Scope cut points: the CSV of scripts\sap\Run-Sap.ps1 -Points <cut nodes> (read from the reviewed SAP model).
    # Export-DuctsViewer.ps1 -Revit cuts the clean model there (-Cuts overrides; -Cuts none = no cuts). '' = none.
    DuctCuts = ''
    # Revit categories left out of the Revit Pluto Mesh _norun viewer file (exact names as in its "Category:" groups,
    # ignoring case), e.g. @('Duct Insulations', 'Duct Linings'). @() = keep all.
    RevitMeshNoRunExclude = @()
    # Add-on: hand-picked clean-model drops, the lists the viewer's Cut picker downloads (Revit part ids, not the
    # cut points CSV). One file or several; every listed member is removed after the cuts. @() = off.
    # Utility rack steel for the clean model (Export-DuctsViewer.ps1 -Revit; -Steel overrides): the SteelToPluto CSV
    # (meters, the duct model's frame). '' = no steel. Snap: an end within DuctSteelSnapIn of another member's
    # centreline connects to it; open ends within DuctSteelNearIn of other steel get OPEN_NEAR (missed connection?).
    DuctSteel = ''                    # e.g. 'C:\Temp\hvac\steel\steel.csv'
    DuctSteelSnapIn = 6
    DuctSteelNearIn = 24
    # Walls drawn in the clean model (drawing only, never in SAP): the CSV of scripts\voyager\Export-Walls.ps1
    # (one box per wall, meters, the steel CSV's frame). Groups by shape (Walls: wall / slab / skew wall / skew slab /
    # small) and by level (Walls level: <band>: the bands between the big slabs; hide a level with its eye). Only walls
    # within DuctWallMarginFt of the model (ducts, steel, supports) are drawn; a negative margin draws every wall. '' = none.
    DuctWalls = ''                    # e.g. 'C:\Temp\walls.csv'
    DuctWallMarginFt = 10
    # Room regions from the walls (drawing only): per level, the floor area closed in by walls is a region, drawn as a
    # coloured floor patch (groups Room: <name>) and listed in <out>_clean.rooms.csv. Named by the room codes on the
    # ducts and steel inside it (most votes; 'mixed' under 60 %; 'unnamed' with none). Walls that stop up to
    # DuctRoomGapFt short of each other still close a room. $false = no regions.
    DuctRoomRegions = $true
    DuctRoomGapFt = 4
    # Wall thickness table (CSV, header Run,Size,Thk_in). When set it REPLACES the Navisworks thickness; a frame no row
    # matches gets the 0.001 in placeholder (fix it in the table or in SAP). Run = the system name shown in the viewer
    # (e.g. SYSA; its fragments match too). Size: WxH or ranges 13-24x0-60 (either orientation), D dia or 8-36 dia, * = any.
    # Exact size beats a range beats *. '' = Navisworks thickness.
    DuctThickness = ''                # e.g. 'C:\Temp\hvac\duct-thickness.csv'
    # Duct-to-steel supports: steel under a horizontal duct with a gap (duct bottom -> steel top) up to DuctSupportGapIn
    # is a support (TAP_STUB link, <out>.supports.csv); duct stretches longer than DuctSpanFt between supports get
    # UNSUPPORTED_SPAN. match.txt prints a gap histogram of every crossing to choose the gap.
    DuctSupportGapIn = 4.5
    DuctSpanFt = 10
    # Navisworks hanger check (report only): a hanger and a steel support within DuctHangerMatchIn (in) along the
    # duct match; HANGER_NO_STEEL_SUPPORT / HANGER_OFF_DUCT / SUPPORT_NO_HANGER otherwise.
    DuctHangerMatchIn = 24
    # Horizontal ducts with the larger side vertical: flagged LARGE_SIDE_VERTICAL and rotated 90 deg about the
    # centreline together with their same-size run (risers included). $false = flag only.
    DuctRotateLargeVertical = $true
    DuctDrop = @()   # e.g. @('C:\Temp\hvac\cuts\drop.txt', 'C:\Temp\hvac\cuts\drop-yellow.txt')
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
        NoShear        = $true        # V2 / V3 left out of the governing DCR (and the optimizer score); the report shows shear separately
        StiffenerSpacingIn = 24       # transverse stiffeners (in) for the shear check; delete = the sheet's shear as written
        Limit          = 1.0
    }

    # ---- expansion-joint release ----
    Release = @{
        Candidates      = @('1234')   # joint names to release (Run-ReleaseCheck.ps1: one or a few; validation)
        StiffnessFactor = 1000        # stiff link k = factor x the stiffer adjoining frame (EA/L, 4EI/L)
    }

    # ---- expansion-joint optimizer (Run-Optimizer.ps1) ----
    Optimize = @{
        Candidates     = 'auto'       # 'auto' = every inline, unloaded joint of the group whose span has 2+ support joints;
                                      # or @('12', '34', ...); or the path of a file with one joint per line (the author's list)
        MaxCandidates  = 20           # 0 = all; else an evenly spread sample of that many (trial runs: every candidate is a stiff
                                      # link + 6 unit-pair cases in ONE SAP run, so start small and read the report)
        MaxJoints      = 10           # greedy stops here, or earlier when every frame is within the limit
        OnePerSpan     = $true        # never two joints in one span between supports (two would leave a floating piece)
        Swap           = $true        # after greedy: try replacing each chosen joint by every unused candidate
        Removal        = $true        # then: drop any joint whose absence does not worsen the score
        MaxEvaluations = 0            # 0 = no cap on the number of sets scored
        SingularPivot  = 1e-8         # a set whose release system has a pivot ratio below this is a mechanism (the report histograms them)
    }
}