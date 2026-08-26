# Download a user's whole OneDrive to a local folder, preserving the tree.
#
# The enumeration is a delta call, which is the cheapest way to walk a drive:
# one paged sweep instead of a recursive /children crawl per folder. Each item
# carries parentReference.path, which is what rebuilds the directory structure
# locally.
#
# Downloads go through -OutFile, so bytes stream to disk and never sit in the
# pipeline. Where Graph gave the item a @microsoft.graph.downloadUrl, piping the
# item uses that pre-authenticated URL directly: no Graph round trip for the
# bytes, no resource units, and no bearer token sent to the download host.
#
# Re-running skips files whose local size already matches, so an interrupted run
# continues rather than starting over.
#
# Requirements: Connect-MgGraph -Scopes "Files.Read.All"  (delegated: Files.Read
#               for your own drive). Reading another user's drive needs the
#               application permission Files.Read.All.

param(
    [Parameter(Mandatory)][string]$User,          # UPN or object id
    [string]$Destination = './onedrive-backup'
)

Import-Module Mgx

$root = Join-Path $Destination $User
New-Item -ItemType Directory -Path $root -Force | Out-Null

$items = Sync-MgxDelta "/users/$User/drive/root/delta" `
    -DeltaPath (Join-Path $root '.delta.json') `
    -CheckpointPath (Join-Path $root '.delta.checkpoint')

# Folders and deletions drop out here; a removed item arrives as @removed, or as a
# 'deleted' facet if the call asked for -Prefer deltashowremovedasdeleted.
$files = @($items | Where-Object { $_.file -and -not $_.deleted -and -not $_.'@removed' })
Write-Host "$($files.Count) file(s) to consider under $User's drive"

$downloaded = 0
$skipped    = 0
foreach ($item in $files) {
    # parentReference.path looks like "/drive/root:/Documents/Reports" - everything
    # after the colon is the folder path inside the drive.
    $relative = ''
    if ($item.parentReference.path -match ':(?<p>.*)$') {
        $relative = $Matches.p.TrimStart('/')
    }
    $targetDir = if ($relative) { Join-Path $root $relative } else { $root }
    New-Item -ItemType Directory -Path $targetDir -Force | Out-Null
    $target = Join-Path $targetDir $item.name

    # Size is the cheap check. For a real integrity check compare against
    # $item.file.hashes.quickXorHash - Graph reports it in base64 where rclone
    # and most other tools print hex, so convert before comparing:
    #   [System.Convert]::ToHexString([System.Convert]::FromBase64String($h)).ToLowerInvariant()
    if ((Test-Path $target) -and (Get-Item $target).Length -eq $item.size) {
        $skipped++
        continue
    }

    try {
        if ($item['@microsoft.graph.downloadUrl']) {
            $item | Get-MgxContent -OutFile $target -ErrorAction Stop
        }
        else {
            # No downloadUrl on this item (delta does not always include one) -
            # fetch through Graph instead.
            Get-MgxContent "/users/$User/drive/items/$($item.id)/content" `
                -OutFile $target -ErrorAction Stop
        }
        $downloaded++
    }
    catch {
        Write-Warning "$($item.name): $($_.Exception.Message -replace '\s+', ' ')"
    }
}

$t = Get-MgxTelemetry
Write-Host ("downloaded {0}, already current {1}, {2:N1} MB transferred" -f `
    $downloaded, $skipped, ($t.ContentBytesDownloaded / 1MB))

# The delta token is kept next to the files, so the next run enumerates only what
# changed and downloads only that. To back up every mailbox-sized drive in the
# tenant, loop over /users and call this once per user.

<#
Expected output:

11 file(s) to consider under adelev@contoso.com's drive
WARNING: Locked-Budget.xlsx: The resource you are attempting to access is locked
downloaded 10, already current 0, 4.7 MB transferred

onedrive-backup/adelev@contoso.com/
  Documents/Q4-Revenue-Report.pdf
  Documents/Reports/Meeting-Notes.txt
  Team-Photo.png
#>
