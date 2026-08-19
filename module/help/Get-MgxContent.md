---
external help file: Mgx.Cmdlets.dll-Help.xml
Module Name: Mgx
online version: https://github.com/gromedev/mgx/blob/main/module/help/Get-MgxContent.md
schema: 2.0.0
---

# Get-MgxContent

## SYNOPSIS
Fetch content bytes from Graph $value / /content endpoints, whole or as a byte range.

## SYNTAX

### Uri (Default)
```
Get-MgxContent [-Uri] <String> [-First <Int64>] [-Offset <Int64>] [-Length <Int64>]
 [-OutFile <String>] [-ApiVersion <String>] [-Headers <Hashtable>]
 [-ProgressAction <ActionPreference>] [<CommonParameters>]
```

### InputObject
```
Get-MgxContent -InputObject <Object> [-First <Int64>] [-Offset <Int64>] [-Length <Int64>]
 [-OutFile <String>] [-ApiVersion <String>] [-Headers <Hashtable>]
 [-ProgressAction <ActionPreference>] [<CommonParameters>]
```

## DESCRIPTION
Get-MgxContent downloads file content from Microsoft Graph content endpoints - drive items (`/drives/{id}/items/{id}/content`), mail attachments (`.../attachments/{id}/$value`), profile photos (`/me/photo/$value`), and anything else that serves bytes.

Ranged reads are the point: `-First 262144` pulls the first 256 KB of a file (enough for format sniffing, EXIF, or partial hashing) instead of the whole thing. Across 20k files that is gigabytes moved instead of tens of gigabytes.

Under the hood, drive content is a two-hop flow: the authenticated Graph request (full resilience pipeline, adaptive pacing, rate limiting) answers with a redirect to a short-lived pre-authenticated download URL, which mgx fetches with a separate token-free HTTP client. The bearer token never reaches the download host, and the host is validated against a Microsoft-hosts allowlist (SharePoint/OneDrive download domains) before any bytes move - including on every mid-download redirect.

Output is a single `byte[]` to the pipeline (guarded at 100 MB - use -OutFile beyond that), or a file written via temp-plus-atomic-move so a failed download never truncates an existing file.

Piped DriveItems (from Invoke-MgxRequest or Sync-MgxDelta) use their `@microsoft.graph.downloadUrl` directly when present - no extra Graph round trip - falling back to `/drives/{driveId}/items/{id}/content` from the item's `id` and `parentReference.driveId`.

Some endpoints ignore the Range header and answer 200 with the full body (profile photos do this). Get-MgxContent detects that, keeps only the requested bytes, and aborts the rest of the transfer.

## EXAMPLES

### Example 1: Download a file
```powershell
Get-MgxContent "/me/drive/items/01ABC/content" -OutFile ./report.xlsx
```

Downloads the drive item to a local file.

### Example 2: First 256 KB of every file in a folder (format sniffing / partial hash)
```powershell
Invoke-MgxRequest "/me/drive/items/01FOLDER/children" -All |
    Where-Object { $_.file } |
    ForEach-Object {
        $bytes = $_ | Get-MgxContent -First 262144
        [PSCustomObject]@{ name = $_.name; header = [BitConverter]::ToString($bytes[0..7]) }
    }
```

Moves kilobytes per file instead of the full content - the metadata-extraction economy case.

### Example 3: A specific byte range
```powershell
Get-MgxContent "/me/drive/items/01ABC/content" -Offset 1048576 -Length 65536
```

Returns bytes 1,048,576 through 1,114,111 (Range: bytes=1048576-1114111).

### Example 4: A mail attachment
```powershell
Get-MgxContent "/me/messages/AAMk.../attachments/AAMk.../`$value" -OutFile ./attachment.pdf
```

Attachment bytes are served directly by Graph (no download-host hop).

### Example 5: Profile photo
```powershell
Get-MgxContent "/me/photo/`$value" -OutFile ./me.jpg
```

Note the backtick: `$value` would otherwise be expanded by PowerShell.

## PARAMETERS

### -Uri
Relative Graph path to a content endpoint (e.g., /me/drive/items/{id}/content). Warns when the path does not look like a content endpoint (/content or /$value).

```yaml
Type: String
Parameter Sets: Uri
Aliases: Resource

Required: True
Position: 0
Default value: None
Accept pipeline input: False
Accept wildcard characters: False
```

### -InputObject
A DriveItem-shaped object from the pipeline. Uses `@microsoft.graph.downloadUrl` when present (validated against the download-host allowlist), else `/drives/{parentReference.driveId}/items/{id}/content`.

```yaml
Type: Object
Parameter Sets: InputObject
Aliases:

Required: True
Position: Named
Default value: None
Accept pipeline input: True (ByValue)
Accept wildcard characters: False
```

### -First
First N bytes (Range: bytes=0..N-1). Mutually exclusive with -Offset/-Length.

```yaml
Type: Int64
Parameter Sets: (All)
Aliases:

Required: False
Position: Named
Default value: None
Accept pipeline input: False
Accept wildcard characters: False
```

### -Offset
Range start in bytes; requires -Length.

```yaml
Type: Int64
Parameter Sets: (All)
Aliases:

Required: False
Position: Named
Default value: 0
Accept pipeline input: False
Accept wildcard characters: False
```

### -Length
Range length in bytes, starting at -Offset (default 0).

```yaml
Type: Int64
Parameter Sets: (All)
Aliases:

Required: False
Position: Named
Default value: None
Accept pipeline input: False
Accept wildcard characters: False
```

### -OutFile
Write the content to this file (temp + atomic move) instead of emitting byte[] to the pipeline. Required for content larger than 100 MB.

```yaml
Type: String
Parameter Sets: (All)
Aliases:

Required: False
Position: Named
Default value: None
Accept pipeline input: False
Accept wildcard characters: False
```

### -ApiVersion
Graph API version. Default: v1.0.

```yaml
Type: String
Parameter Sets: (All)
Aliases:
Accepted values: v1.0, beta

Required: False
Position: Named
Default value: v1.0
Accept pipeline input: False
Accept wildcard characters: False
```

### -Headers
Custom request headers for the Graph request (hop 1 only; never sent to the download host).

```yaml
Type: Hashtable
Parameter Sets: (All)
Aliases:

Required: False
Position: Named
Default value: None
Accept pipeline input: False
Accept wildcard characters: False
```

### -ProgressAction
Determines how the cmdlet responds to progress updates.

```yaml
Type: ActionPreference
Parameter Sets: (All)
Aliases: proga

Required: False
Position: Named
Default value: None
Accept pipeline input: False
Accept wildcard characters: False
```

### CommonParameters
This cmdlet supports the common parameters: -Debug, -ErrorAction, -ErrorVariable, -InformationAction, -InformationVariable, -OutVariable, -OutBuffer, -PipelineVariable, -Verbose, -WarningAction, and -WarningVariable. For more information, see [about_CommonParameters](http://go.microsoft.com/fwlink/?LinkID=113216).

## INPUTS

### System.Object
DriveItem-shaped Hashtables or PSCustomObjects (from Invoke-MgxRequest or Sync-MgxDelta).

## OUTPUTS

### System.Byte[]
The content bytes as a single array (not enumerated). Nothing is emitted when -OutFile is used.

## NOTES
Content downloads require the mgx-owned HTTP transport. If mgx had to fall back to the Graph SDK's client (rare - it means the clean client could not be built), Get-MgxContent refuses with ContentRequiresOwnedTransport rather than let a transport that auto-follows redirects fetch from an unvalidated host.

Graph reports drive-item hashes as quickXorHash in base64; rclone and many tools print hex. Convert before comparing:
`[System.Convert]::ToHexString([System.Convert]::FromBase64String($item.file.hashes.quickXorHash))`

The pre-authenticated download URL on piped items is short-lived (~1 hour). On expiry mgx re-requests it automatically for the /content path; for stale piped `@microsoft.graph.downloadUrl` values, re-fetch the item.

Intune report export jobs are not a /content flow: the completed job's `url` property is a pre-authenticated Azure Blob URL - download it directly with Invoke-WebRequest; it needs nothing from mgx.

## RELATED LINKS
[Invoke-MgxRequest](Invoke-MgxRequest.md)
[Sync-MgxDelta](Sync-MgxDelta.md)
[Get-MgxTelemetry](Get-MgxTelemetry.md)
