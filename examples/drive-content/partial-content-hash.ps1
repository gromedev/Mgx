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

<#
Expected output:

Name                     Size Magic                   PartialSha                                                       QuickXorHex
----                     ---- -----                   ----------                                                       -----------
Employee-Handbook.pdf 1204224 25-50-44-46-2D-31-2E-37 fcdec4adc77794abd24402810879c26beaee6f95f515b42be79fc032e69b6f5c e3bc4fcdfa266aadc7e2148a472720e3877b5de6
README.txt               5120 61-62-63-64-65-66-67-68 a31ba217553a0e1b431276ab8ee0d0df43b958686c77041ec6d2eb859943e02d f9e21c1a070d2467204274dcec5d93d20613645d
Team-Photo.png         742912 89-50-4E-47-0D-0A-1A-0A bc3891906da735372e276ea14e9edcf82ee78e402a19ad70872b2b410d7fb2bf 51c7556a48958d776b8ff3ac36d2b5a5e8071fec

529408
#>
