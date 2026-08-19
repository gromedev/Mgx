# Identify files by content WITHOUT downloading them: ranged reads.
#
# Get-MgxContent: a metadata-extraction job
# over 20k drive files needed only each file's first 256 KB (magic bytes,
# EXIF, partial hash) - ranged reads moved ~5 GB instead of 63 GB.
#
# Requirements: Connect-MgGraph -Scopes "Files.Read.All"

Import-Module Mgx

$folderId = "01FOLDERID"   # a folder's driveItem id

Invoke-MgxRequest "/me/drive/items/$folderId/children" -All |
    Where-Object { $_.file } |
    ForEach-Object {
        $head = $_ | Get-MgxContent -First 262144

        [PSCustomObject]@{
            Name        = $_.name
            Size        = $_.size
            Magic       = [BitConverter]::ToString($head[0..([Math]::Min(7, $head.Length - 1))])
            PartialSha  = [BitConverter]::ToString(
                              [System.Security.Cryptography.SHA256]::HashData($head)
                          ).Replace('-', '').ToLowerInvariant()
            # Graph reports quickXorHash in BASE64; rclone and most tools print
            # HEX. Convert before comparing across tools:
            QuickXorHex = if ($_.file.hashes.quickXorHash) {
                              [System.Convert]::ToHexString(
                                  [System.Convert]::FromBase64String($_.file.hashes.quickXorHash)
                              ).ToLowerInvariant()
                          }
        }
    } | Format-Table -AutoSize

# Bytes moved vs. bytes identified:
(Get-MgxTelemetry).ContentBytesDownloaded
