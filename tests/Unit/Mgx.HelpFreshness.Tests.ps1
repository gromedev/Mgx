<#
    The compiled MAML must say what its markdown source says.

    module/en-US/Mgx.Cmdlets.dll-Help.xml is generated from module/help/*.md, and for two
    releases nothing regenerated it. Get-Help went on describing PSObjects and an ODataType
    property that 2.0.0 replaced with hashtables, denied that Sync-MgxDelta has -CheckpointPath
    while that was a headline 2.1.0 feature, and gave -BatchChunkConcurrency a range the cmdlet
    rejects. The existing surface tests compare example COUNT and parameter PRESENCE, so none of
    it registered.

    This compares the text. It is deliberately coarse - descriptions are reflowed and links are
    rendered differently by the generator - so it checks that the MAML contains no parameter
    description that the markdown has since changed, using a normalized comparison.
#>

BeforeAll {
    $repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
    $script:Maml = Join-Path $repo 'module/en-US/Mgx.Cmdlets.dll-Help.xml'
    $script:HelpDir = Join-Path $repo 'module/help'

    function Normalize([string] $t) {
        if (-not $t) { return '' }
        # markdown link -> its text; code ticks and whitespace collapse
        $t = [regex]::Replace($t, '\[([^\]]+)\]\([^)]+\)', '$1')
        $t = $t -replace '`', ''
        ($t -replace '\s+', ' ').Trim()
    }

    $script:MamlXml = if (Test-Path $script:Maml) { [xml](Get-Content $script:Maml -Raw) } else { $null }
}

Describe 'Compiled help matches its markdown source' {
    It 'the MAML exists' {
        Test-Path $script:Maml | Should -BeTrue
    }

    It 'carries no description the markdown has replaced' {
        # Phrases that were true of an older release and are now wrong. Each one shipped in
        # Get-Help while the markdown already said otherwise.
        $stale = @(
            @{ Text = 'ODataType';  Why = '2.0.0 replaced PSObjects with hashtables; @odata.type is a key now' }
            @{ Text = 'instead of PSObjects'; Why = '-Raw returns raw JSON instead of hashtables' }
            @{ Text = 'ephemeral and deleted on success'; Why = 'Sync-MgxDelta gained -CheckpointPath in 2.1.0' }
        )
        $raw = Get-Content $script:Maml -Raw
        $hits = $stale | Where-Object { $raw -match [regex]::Escape($_.Text) }
        ($hits | ForEach-Object { "$($_.Text) -- $($_.Why)" }) -join '; ' |
            Should -BeNullOrEmpty -Because 'the MAML is generated from module/help; run ./build.ps1 to regenerate it'
    }

    It 'documents every parameter with the description its markdown gives' {
        $mismatches = [System.Collections.Generic.List[string]]::new()

        foreach ($md in Get-ChildItem $script:HelpDir -Filter '*.md') {
            $cmdlet = $md.BaseName
            $lines = Get-Content $md.FullName

            # ### -Name  followed by prose until the ```yaml block
            for ($i = 0; $i -lt $lines.Count; $i++) {
                if ($lines[$i] -notmatch '^### -(\w+)$') { continue }
                $param = $Matches[1]
                $desc = [System.Collections.Generic.List[string]]::new()
                for ($j = $i + 1; $j -lt $lines.Count; $j++) {
                    if ($lines[$j] -match '^```' -or $lines[$j] -match '^### ' -or $lines[$j] -match '^## ') { break }
                    if ($lines[$j].Trim()) { $desc.Add($lines[$j]) }
                }
                $want = Normalize ($desc -join ' ')
                if (-not $want) { continue }

                $node = $script:MamlXml.helpItems.command |
                    Where-Object { $_.details.name.Trim() -eq $cmdlet } |
                    ForEach-Object { $_.parameters.parameter } |
                    Where-Object { $_.name -eq $param } |
                    Select-Object -First 1
                if (-not $node) { $mismatches.Add("$cmdlet -$param : absent from MAML"); continue }

                $got = Normalize (($node.description.para | ForEach-Object { $_ }) -join ' ')
                # Coarse: the MAML must open with what the markdown says.
                $head = if ($want.Length -gt 60) { $want.Substring(0, 60) } else { $want }
                if ($got -notlike "*$head*") {
                    $mismatches.Add("$cmdlet -$param : markdown says '$head...' MAML says '$($got.Substring(0,[Math]::Min(60,$got.Length)))...'")
                }
            }
        }

        ($mismatches -join "`n") | Should -BeNullOrEmpty -Because 'run ./build.ps1 to regenerate the compiled help'
    }
}
