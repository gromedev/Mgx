# Triage a drive by reading 4 KB of each file, then download only what survives triage.
#
#
# Two properties of Get-MgxContent make this cheap and safe:
#
#   * A ranged read is a Range request. -First 4096 transfers 4 KB, not 4 KB of a 2 GB download
#     you abandon. Some endpoints ignore Range and answer 200 with the whole body (profile
#     photos do); mgx detects that and truncates client-side, so you never receive more than you
#     asked for either way.
#   * Piping a DriveItem uses its own @microsoft.graph.downloadUrl - a pre-authenticated URL -
#     so there is no Graph round trip at all for the bytes. No resource units, and no bearer
#     token sent to the download host. That URL is itself a credential for the file, which is
#     why mgx redacts it from -Debug output.
#
# Requirements: Connect-MgGraph -Scopes "Files.Read.All"
# Read-only against Graph. Writes only to the local -OutputDirectory.

param(
    [string]$DrivePath = '/me/drive',
    [int]$SniffBytes = 4096,
    [int]$MaxFiles = 40,
    [string]$OutputDirectory = './triaged'
)

Import-Module Mgx

# Magic-byte signatures. Enough to separate "worth fetching" from "skip" without a full download.
$Signatures = @(
    @{ Name = 'PNG';   Bytes = [byte[]](0x89,0x50,0x4E,0x47) }
    @{ Name = 'JPEG';  Bytes = [byte[]](0xFF,0xD8,0xFF) }
    @{ Name = 'PDF';   Bytes = [byte[]](0x25,0x50,0x44,0x46) }
    @{ Name = 'ZIP';   Bytes = [byte[]](0x50,0x4B,0x03,0x04) }   # also docx/xlsx/pptx
    @{ Name = 'GZIP';  Bytes = [byte[]](0x1F,0x8B) }
    @{ Name = 'SQLite';Bytes = [byte[]](0x53,0x51,0x4C,0x69,0x74,0x65) }
)

function Get-FileKind {
    param([byte[]]$Head)
    foreach ($sig in $Signatures) {
        if ($Head.Length -ge $sig.Bytes.Length) {
            $match = $true
            for ($i = 0; $i -lt $sig.Bytes.Length; $i++) {
                if ($Head[$i] -ne $sig.Bytes[$i]) { $match = $false; break }
            }
            if ($match) { return $sig.Name }
        }
    }
    # No signature: decide whether it is plausibly text by looking for NUL bytes, which almost
    # never appear in text and almost always appear in binary.
    if ($Head.Length -gt 0 -and ($Head | Where-Object { $_ -eq 0 }).Count -eq 0) { return 'TEXT' }
    return 'UNKNOWN'
}

Write-Host "Enumerating $DrivePath ..." -ForegroundColor Cyan
$items = @(Invoke-MgxRequest "$DrivePath/root/children" -All -PageSize 200 `
        -Property id,name,size,file |
    Where-Object { $_.ContainsKey('file') } |
    Select-Object -First $MaxFiles)

if (-not $items) { Write-Warning "No files found under $DrivePath/root."; return }
"  $($items.Count) file(s) to triage"

$before = (Get-MgxTelemetry).ContentBytesDownloaded
$results = [System.Collections.Generic.List[object]]::new()

foreach ($item in $items) {
    # Ranged read of the head only. On a 2 GB file this transfers 4 KB.
    try {
        $head = Get-MgxContent "$DrivePath/items/$($item.id)/content" -First $SniffBytes -ErrorAction Stop
    }
    catch {
        $results.Add([pscustomobject]@{
            Name = $item.name; Size = $item.size; Kind = 'ERROR'; Sniffed = 0
            Note = ($_.Exception.Message -replace '\s+', ' ')
        })
        continue
    }

    $kind = Get-FileKind -Head $head
    $results.Add([pscustomobject]@{
        Name = $item.name; Size = $item.size; Kind = $kind; Sniffed = $head.Length
        Id = $item.id; Note = ''
    })
}

Write-Host "`n=== Triage ===" -ForegroundColor Cyan
$results | Sort-Object Kind, Name |
    Format-Table Name, @{N='Size';E={'{0:N0}' -f $_.Size};A='Right'},
                 Kind, @{N='Read';E={$_.Sniffed};A='Right'}, Note -AutoSize

# --- fetch only what triage selected ---
# Change this predicate to whatever your triage actually is. The point is that the decision was
# made from 4 KB per file rather than from downloading everything first.
$wanted = $results | Where-Object { $_.Kind -in 'PDF','TEXT' -and $_.Size -gt 0 -and $_.Size -lt 8MB }

$sniffBytesTotal = ($results | Measure-Object Sniffed -Sum).Sum
$allBytes = ($results | Measure-Object Size -Sum).Sum

Write-Host "`n=== Fetching $($wanted.Count) selected file(s) whole ===" -ForegroundColor Cyan
if ($wanted) {
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    foreach ($w in $wanted) {
        $dest = Join-Path $OutputDirectory ($w.Name -replace '[\\/:*?"<>|]', '_')
        # -OutFile streams to a temp and moves atomically, so an interrupted download never
        # leaves a half-written file at the destination.
        Get-MgxContent "$DrivePath/items/$($w.Id)/content" -OutFile $dest
        "  {0,-44} {1,10:N0} bytes" -f $w.Name, (Get-Item $dest).Length
    }
}
else { "  nothing matched the triage predicate" }

$downloaded = (Get-MgxTelemetry).ContentBytesDownloaded - $before

Write-Host "`n=== What triage saved ===" -ForegroundColor Cyan
"  files examined      : {0:N0}" -f $results.Count
"  total size on drive : {0:N0} bytes" -f $allBytes
"  read for triage     : {0:N0} bytes ({1:P2} of the total)" -f $sniffBytesTotal, ($(if($allBytes){$sniffBytesTotal/$allBytes}else{0}))
"  transferred overall : {0:N0} bytes  (triage + the {1} full downloads)" -f $downloaded, $wanted.Count
"  resource units      : {0}" -f (Get-MgxTelemetry).ResourceUnitsConsumed
''
"  Scale that up: reading 4 KB of 112,000 files is ~460 MB regardless of whether those files"
"  total 60 GB or 600 GB. The saving is the ratio, and it grows with the library."

<#
Expected output:

Enumerating /me/drive ...
  6 file(s) to triage

=== Triage ===

Name                       Size Kind    Read Note
----                       ---- ----    ---- ----
Q4-Revenue-Report.pdf 2,418,912 PDF     4096
Contoso-Logo.png        148,326 PNG     4096
Meeting-Notes.txt        12,480 TEXT    4096
placeholder.txt               0 UNKNOWN    0
telemetry.bin            65,536 UNKNOWN 4096
Campaign-Assets.zip   5,242,880 ZIP     4096


=== Fetching 2 selected file(s) whole ===
  Q4-Revenue-Report.pdf                         2,418,912 bytes
  Meeting-Notes.txt                                12,480 bytes

=== What triage saved ===
  files examined      : 6
  total size on drive : 7,888,134 bytes
  read for triage     : 20,480 bytes (0.26% of the total)
  transferred overall : 2,451,872 bytes  (triage + the 2 full downloads)
  resource units      : 9

  Scale that up: reading 4 KB of 112,000 files is ~460 MB regardless of whether those files
  total 60 GB or 600 GB. The saving is the ratio, and it grows with the library.
#>
