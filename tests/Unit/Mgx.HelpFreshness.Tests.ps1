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

    It 'carries the synopsis, description, outputs and examples its markdown gives' {
        # The parameter check below was the whole guard, so a SYNOPSIS saying the wrong thing
        # entirely, a replaced OUTPUTS section, or a renamed example all shipped green. Sections
        # are compared by their opening sentence: the generator reflows prose, so a prefix is
        # what can be compared honestly.
        $mismatches = [System.Collections.Generic.List[string]]::new()

        foreach ($md in Get-ChildItem $script:HelpDir -Filter '*.md') {
            $cmdlet = $md.BaseName
            $lines  = Get-Content $md.FullName
            $node = $script:MamlXml.helpItems.command |
                Where-Object { $_.details.name.Trim() -eq $cmdlet } | Select-Object -First 1
            if (-not $node) { $mismatches.Add("$cmdlet : absent from MAML"); continue }

            function SectionText([string[]] $l, [string] $heading) {
                $out = [System.Collections.Generic.List[string]]::new()
                $in = $false
                foreach ($line in $l) {
                    if ($line -match "^## $heading\s*$") { $in = $true; continue }
                    if ($in -and $line -match '^#{1,3} ') { break }
                    if ($in -and $line.Trim()) { $out.Add($line) }
                }
                Normalize ($out -join ' ')
            }
            function Head([string] $t, [int] $n = 50) {
                if ($t.Length -gt $n) { $t.Substring(0, $n) } else { $t }
            }

            $wantSyn = SectionText $lines 'SYNOPSIS'
            $gotSyn  = Normalize $node.details.description.para
            if ($wantSyn -and $gotSyn -notlike "*$(Head $wantSyn)*") {
                $mismatches.Add("$cmdlet SYNOPSIS : markdown '$(Head $wantSyn)...' MAML '$(Head $gotSyn)...'")
            }

            $wantOut = SectionText $lines 'OUTPUTS'
            $gotOut  = Normalize (($node.returnValues.returnValue.type.name | ForEach-Object { $_ }) -join ' ')
            if ($wantOut -and $gotOut) {
                # OUTPUTS opens with the type name under a ### heading; compare that token.
                $wantType = ($lines | Where-Object { $_ -match '^### ' } | ForEach-Object { $_ } |
                    Select-Object -First 0)
                $typeLine = $null
                $inOut = $false
                foreach ($line in $lines) {
                    if ($line -match '^## OUTPUTS\s*$') { $inOut = $true; continue }
                    if ($inOut -and $line -match '^## ') { break }
                    if ($inOut -and $line -match '^### (.+)$') { $typeLine = $Matches[1].Trim(); break }
                }
                if ($typeLine -and $gotOut -notlike "*$typeLine*") {
                    $mismatches.Add("$cmdlet OUTPUTS : markdown '$typeLine' MAML '$(Head $gotOut 60)'")
                }
            }

            $mdExamples = @($lines | Where-Object { $_ -match '^### Example' })
            $mamlExamples = @($node.examples.example)
            for ($i = 0; $i -lt [Math]::Min($mdExamples.Count, $mamlExamples.Count); $i++) {
                $want = Normalize ($mdExamples[$i] -replace '^### ', '')
                $got  = Normalize $mamlExamples[$i].title
                if ($got -notlike "*$want*") {
                    $mismatches.Add("$cmdlet example $($i + 1) : markdown '$want' MAML '$got'")
                }
            }
        }

        ($mismatches -join "`n") | Should -BeNullOrEmpty -Because 'run ./build.ps1 to regenerate the compiled help'
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

Describe 'Documented output types match the cmdlets' {
    # The guard above keeps the MAML matching its markdown. It cannot see markdown that is simply
    # wrong about the code - both files said PSObject while the cmdlets declared and emitted
    # their own types. This compares the documentation against [OutputType].
    It 'every cmdlet documents the type it declares' {
        $repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
        Import-Module "$repo/module/mgx.psd1" -Force
        $wrong = [System.Collections.Generic.List[string]]::new()

        foreach ($cmd in Get-Command -Module mgx -CommandType Cmdlet) {
            $declared = @($cmd.OutputType | ForEach-Object { $_.Type.FullName } | Where-Object { $_ })
            if (-not $declared) { continue }

            $md = Join-Path $repo "module/help/$($cmd.Name).md"
            if (-not (Test-Path $md)) { continue }

            $documented = $null
            $inOutputs = $false
            foreach ($line in Get-Content $md) {
                if ($line -match '^## OUTPUTS\s*$') { $inOutputs = $true; continue }
                if ($inOutputs -and $line -match '^## ')      { break }
                if ($inOutputs -and $line -match '^### (.+)$') { $documented = $Matches[1].Trim(); break }
            }
            if (-not $documented) { continue }

            if ($declared -notcontains $documented) {
                $wrong.Add("$($cmd.Name): documents '$documented', declares '$($declared -join ", ")'")
            }
        }

        ($wrong -join "`n") | Should -BeNullOrEmpty -Because 'module/help OUTPUTS should name the type the cmdlet emits'
    }
}
