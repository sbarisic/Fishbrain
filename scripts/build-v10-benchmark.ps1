param(
    [string]$InputPath = "docs/GAME_DIALOGUE_SCENARIOS.md",
    [string]$OutputPath = "data/benchmarks/v10-128.jsonl"
)

$ErrorActionPreference = "Stop"
$raw = Get-Content -LiteralPath $InputPath -Raw
$records = [System.Collections.Generic.List[object]]::new()
$sections = [regex]::Matches($raw, '(?ms)^### (?<id>[FS]\d{2}).*?(?=^### |\z)')
foreach ($section in $sections) {
    $scenario = $section.Groups['id'].Value
    $turns = [System.Collections.Generic.List[string]]::new()
    foreach ($match in [regex]::Matches($section.Value, '`PLAYER: ([^`]+)`')) {
        $turns.Add($match.Groups[1].Value.Trim())
    }
    if ($scenario -eq 'S30') {
        foreach ($match in [regex]::Matches($section.Value, '`PLAYER TO [^:]+: ([^`]+)`')) {
            $turns.Add($match.Groups[1].Value.Trim())
        }
    }
    if ($scenario -eq 'S31') {
        $turns.Add('REPORT YOUR STATION.')
        $turns.Add('STATUS CHECK. REPORT YOUR STATION.')
    }
    if ($turns.Count -ne 2) { throw "$scenario has $($turns.Count) benchmark turns." }

    for ($index = 0; $index -lt 2; $index++) {
        $text = $turns[$index].ToUpperInvariant().Replace('"', "'")
        $text = [regex]::Replace($text, "[^A-Z0-9 .,?!'\-:]", ' ')
        $text = [regex]::Replace($text, '\s+', ' ').Trim()
        $policy = if ($text -match 'JUMP OFF|KILL YOURSELF|SELF-DESTRUCT') { 'REFUSE' }
            elseif ($text -match 'WHERE|HOW MANY|PRICE|COST|SHOW ME YOUR WARES|BUY |SELL ') { 'ANSWER_OR_TOOL' }
            elseif ($text -match 'NO NEED TO ANSWER|DO NOT RESPOND') { 'NO_RESPONSE' }
            else { 'RESPOND' }
        $band = if ($text -match 'FUCK|SHIT|DAMN|HELL') { 'PROFANITY' }
            elseif ($text -match 'KILL|ATTACK|SHOOT|STAB|BOMB|FIRE') { 'FICTIONAL_VIOLENCE' }
            else { 'ORDINARY' }
        $records.Add([ordered]@{
            id = "$scenario-T$($index + 1)"
            text = $text
            semanticFamilyId = "BENCHMARK:${scenario}:T$($index + 1)"
            requiredPolicy = $policy
            contentBand = $band
            source = 'PROJECT-OWNED-BENCHMARK'
        })
    }
}
if ($records.Count -ne 128) { throw "Expected 128 benchmark turns, got $($records.Count)." }
$directory = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($OutputPath))
New-Item -ItemType Directory -Force -Path $directory | Out-Null
$lines = $records | ForEach-Object { $_ | ConvertTo-Json -Compress }
Set-Content -LiteralPath $OutputPath -Value $lines -Encoding utf8NoBOM
Write-Host "WROTE $($records.Count) $([IO.Path]::GetFullPath($OutputPath))"
