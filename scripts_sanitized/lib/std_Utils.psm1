# =======================================================================================
# =======================================================================================
# STAAD PostP and Section Cut Tool
# STD: Utils
# Version: 1.0.2
# This module defines constants, assemblies and functions frequently used when processing
# all data from a FEA (STAAD) model.
# =======================================================================================
# =======================================================================================
#using module .\std_Types.psm1
# using module .\ps_funs.psm1

# =======================================================================================
# Helper Variables
# =======================================================================================
# String Split Option - Remove Empty Entries
# Used while parsing text output files.
#$Global:ssoree = [StringSplitOptions]::RemoveEmptyEntries

$Global:math = [Math]
#
# Initializing assemblies for vector and matrix operations
$Global:snv3 = [System.Numerics.Vector3]
$Global:snv4 = [System.Numerics.Vector4]
$Global:snm4 = [System.Numerics.Matrix4x4]

# Initializing class for Linq Enumerable operations
$Global:le = [System.Linq.Enumerable]
#
## Initializing class for type 1 of tuples used. Most frequently, tuples are used as
## dictionary keys.
#$Global:tup1 = [System.ValueTuple[int, object]]
#
## Defining common predicates to use with Linq Enumerables
#$Global:isLetter = [Func[char, bool]] { param($c) [char]::IsLetter($c) }
#$Global:isNumber = [Func[char, bool]] { param($c) [char]::IsDigit($c) }

# Defining string array of axes used in processing
$Script:Axes = @('X', 'Y', 'Z')
# =======================================================================================
# Helper Functions
# =======================================================================================
function Start-ScriptTime {
    $Script:startTime = Get-Date
    Write-Host "------------------------`nStart: $(Get-Date $startTime -Format "G")`n"
}

#function Get-ScriptTime {
#    $Script:currTime = Get-Date
#    Write-Host "Time Elapsed: $(($currTime-$startTime).Minutes)m $(($currTime-$startTime).seconds)s"
#}

function Stop-ScriptTime {
    $endTime = Get-Date
    Write-Host "`nEnd: $(Get-Date $endTime -Format "G")"
    Write-Host "Total Run Time: $(($endTime-$startTime).Hours)h $(($endTime-$startTime).Minutes)m $(($endTime-$startTime).seconds)s
------------------------"
    #    [Media.SystemSounds]::Asterisk.Play()
}

function Suspend-Message {
    param(
        [string]$message,
        [switch]$Warn = $false,
        [switch]$Err = $false
    )
    if ($Warn) {
        Write-Host $message -ForegroundColor Red
        Write-Host "Continuing...`n"
    }
    elseif ($Err) {
        if ($startTime) { Stop-ScriptTime }
        Write-Host $message -ForegroundColor Red
        [Media.SystemSounds]::Hand.Play()
        Write-Host "Press any key to exit." -ForegroundColor Yellow
        $null = $host.ui.RawUI.ReadKey("NoEcho,IncludeKeyDown")
        exit 1
    }
    else {
        Write-Host $message
        Write-Host "Press any key to continue." -ForegroundColor Yellow
        $null = $host.ui.RawUI.ReadKey("NoEcho,IncludeKeyDown")
    }

}


# =======================================================================================
# Function to query the plate element stress data dictionary loaded into memory
# ---------------------------------------------------------------------------------------
# NOTE: THIS FUNCTION CURRENTLY TAKES STRESS DATA AS A LIST, BUT THE PRIMARY SCRIPT
# STORES STRESS DATA INTO A DICTIONARY. THIS FUNCTION NEEDS TO BE REVISED. THERE IS NO
# IMPACT TO THE FUNCTIONALITY OF THE CURRENT SECTION CUT SCRIPTS.
function Get-ElemStresses {
    param (
        [Parameter(Mandatory, ParameterSetName = 'ElemList')]
        [int[]]$Elements,

        [Parameter(ParameterSetName = 'NodeList')]
        [System.Collections.Generic.Dictionary[int, Element]]$ElemData = $Global:elems,
        [Parameter(Mandatory, ParameterSetName = 'NodeList')]
        [int[]]$Nodes,

        [Parameter(ParameterSetName = 'LineThrough')]
        [System.Collections.Generic.Dictionary[int, Node]]$NodeData = $Global:nodes,
        [Parameter(Mandatory, ParameterSetName = 'LineThrough')]
        [string]$Axis,
        [Parameter(Mandatory, ParameterSetName = 'LineThrough')]
        [double]$Coord1,
        [Parameter(Mandatory, ParameterSetName = 'LineThrough')]
        [double]$Coord2,

        [System.Collections.Generic.List[string]]$LCs = $defLCs,
        #[System.Collections.Generic.Dictionary[
        #ValueTuple[int, object], object[]]]$StressData = $Global:stresses
        $StressData = $Global:stresses
    )

    if (!$StressData.Count) { Suspend-Message "Could not find element stress data. Try again." -Err }

    $sErrMsg = "No matching stresses found."

    ##elseif ($PSCmdlet.ParameterSetName.Equals('NodeList')) {
    if ($PSCmdlet.ParameterSetName.Equals('NodeList')) {
        if (!$ElemData.Count) { Suspend-Message "Could not find element data. Try again." -Err }
        $Elements = (Get-Element -ElemData $ElemData -Nodes $Nodes).where{ $_.type -ne "beam" }.id
        #$foundStress = $le::Where($StressData, $condition)

        #if (!$foundStress) { return (Write-Error $sErrMsg) } else { return [StressRecord[]]$foundStress }
    }
    elseif ($PSCmdlet.ParameterSetName.Equals('LineThrough')) {
        if (!$NodeData.Count) { Suspend-Message "Could not find node data. Try again." -Err }
        $Nodes = (Get-Node -NodeData $NodeData -Axis $Axis -Coord1 $Coord1 -Coord2 $Coord2).id
        $Elements = (Get-Element -ElemData $ElemData -Nodes $Nodes).where{ $_.type -ne "beam" }.id
        #$foundStress = $le::Where($StressData, $condition)

    }
    #else { Write-Error "Inputs may not be properly defined. Double check inputs and try again." }

    [ValueTuple[int, object][]]$foundStrKeys = $le::Where($StressData.Keys,
        [Func[ValueTuple[int, object], bool]] {
            param($sKey)
            $e = $sKey.Item1
            $n = $sKey.Item2
            return ($e -in $Elements) -or ($n -in $Nodes)
        })

    if (!$foundStrKeys.Count) { Write-Error $sErrMsg; exit }

    if ($StressData[$foundStrKeys][-1] -is [hashtable]) {
        $StressData = [StressRecord[]]$le::SelectMany([object[]]$StressData[$foundStrKeys].Values, [Func[Object, StressRecord[]]] {
                param($es)
                return [StressRecord]::new($es)
            })
    }
    elseif ($StressData[$foundStrKeys][-1] -is [StressRecord[]]) {
        $StressData = [StressRecord[]]$le::SelectMany([Object[]]$StressData[$foundStrKeys], [Func[Object, StressRecord[]]] {
                param($es)
                return $es
            })
    }

    $StressByLC = $le::ToLookup($StressData, [Func[StressRecord, object]] { return $args[0].LC })
    $LCs = $le::Intersect($LCs, [string[]]$StressByLC.Key)

    return [StressRecord[]]$le::SelectMany($LCs, [Func[string, StressRecord[]]] { $StressByLC[$args[0]] })
}

# =======================================================================================
# Function to assemble the parametric vector form equation for a line
# ---------------------------------------------------------------------------------------
function Get-LineEq {
    param(
        # Used to form a line parallel to one of the global axes.
        [Parameter(Mandatory, ParameterSetName = 'Axis')]
        [string]$Axis,
        [Parameter(Mandatory, ParameterSetName = 'Axis')]
        [double]$Coord1,
        [Parameter(Mandatory, ParameterSetName = 'Axis')]
        [double]$Coord2,

        # Used to form a line between two identified existing nodes.
        [Parameter(Mandatory, ParameterSetName = 'Nodes')]
        [int]$Node1,
        [Parameter(Mandatory, ParameterSetName = 'Nodes')]
        [int]$Node2,

        [System.Collections.Generic.Dictionary[int, Node]]$NodeData = $nodes
    )

    # If the user inputs an axis and a set of coordinates, the line formed is through
    # coordinate 0 and 1 along the axis specified.
    if ($PSCmdlet.ParameterSetName.Equals('Axis')) {
        # Initializing the two points along the line.
        $p0 = $snv3::Zero
        $p1 = $snv3::One

        # Set of axes for which coordinates are input
        $ax = $Axes -ne $Axis

        # The 'ax[0]' coordinate for both position vectors is set to 'Coord1', and
        # the 'ax[1]' coordinate for both position vectors is set to 'Coord2'.
        $p0.($ax[0]) = $Coord1; $p0.($ax[1]) = $Coord2
        $p1.($ax[0]) = $Coord1; $p1.($ax[1]) = $Coord2

        # Parametric vector form:
        # p(t) = a + t*b
        $a = $p0
        $b = $p1 - $p0
    }
    # If the user inputs existing node IDs, the line formed is through both nodes.
    elseif ($PSCmdlet.ParameterSetName.Equals('Nodes')) {
        if (!$NodeData) { Write-Error "Could not find node data. Try again."; exit }
        # Position vectors formed directly from node data.
        $p0 = $NodeData[$Node1].xyz
        $p1 = $NodeData[$Node2].xyz

        # Parametric vector form:
        # p(t) = a + t*b
        $b = $p1 - $p0
        $a = $p0 - $b
    }
    # Direction vector for the line formed is normalized to be unitary.
    $b = $snv3::Normalize($b)
    return $a, $b
}


# =======================================================================================
# Function to calculate the distance between two nodes.
# ---------------------------------------------------------------------------------------
function Measure-DistP2P {
    param(
        [Parameter(Mandatory)]
        [int]$Node1,
        [Parameter(Mandatory)]
        [int]$Node2,

        [System.Collections.Generic.Dictionary[int, Node]]$NodeData = $nodes
    )
    if (!$NodeData) { Write-Error "Could not find node data. Try again."; exit }
    $n1 = $NodeData[$Node1].xyz
    $n2 = $NodeData[$Node2].xyz

    [double]$dist = $snv3::Distance($n1, $n2)

    return "$dist ft"
}

# =======================================================================================
# Function to open a new Excel COM object to create section cut summary file.
# ---------------------------------------------------------------------------------------
function Open-Excel {
    param(
        [Parameter(Mandatory)]
        [string]$FileName,
        [Parameter(Mandatory)]
        [string]$OutputDirectory
    )
    # Output directory path
    $outDir = $OutputDirectory
    # If the folder doesn't exist, create it
    if (!(Test-Path $outDir)) { $null = New-Item -ItemType Directory -Path $outDir }

    $Script:filePath = $outDir + $FileName

    # Start Excel
    $Script:excel = New-Object -ComObject Excel.Application
    # Get all active instances of Excel
    $script:Excls = [object[]](Get-Process -Name "Excel")
    # Get the process ID of the latest instance of Excel
    $script:xlID = $le::Where($Excls, [Func[object, bool]] { param($o)
            return $o.StartTime -eq $le::max([DateTime[]]($Excls.StartTime)) }).Id

    $excel.Visible = $false

    # Create a new workbook
    $Script:wb = $excel.Workbooks.Add()
    # Create a new worksheet
    $Script:ws = $wb.Worksheets.Item(1)
    return $xlID, $excel, $wb, $ws
}

# =======================================================================================
# Function to close and release Excel COM object
# ---------------------------------------------------------------------------------------
function Close-Excel {
    param(
        [Switch]$NoQuit = $false,
        [object[]]$WSs
    )
    $srim = [System.Runtime.Interopservices.Marshal]
    # Save file to specified file name
    $Script:wb.SaveAs($Script:filePath)
    # Option to keep Excel open.
    if (!$NoQuit) { $Script:excel.Quit() }

    # Release the object for each worksheet opened
    foreach ($ws in $wss) {
        $srim::FinalReleaseComObject($Script:ws) | Out-Null
    }
    # Release the object for the workbook
    $srim::FinalReleaseComObject($Script:wb) | Out-Null
    # Option to keep Excel open
    if (!$NoQuit) { $srim::FinalReleaseComObject($Script:excel) | Out-Null }
    [System.GC]::Collect()
    [System.GC]::WaitForPendingFinalizers()
    Stop-Process -Id $Script:xlID -Force
}
# =======================================================================================
# END OF STD: Utils
# =======================================================================================
