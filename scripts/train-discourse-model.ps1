param(
    [string]$CorpusDirectory = "data/compiled-contextual",
    [string]$WorkingDirectory = "data/training",
    [ValidateRange(1, 260000)][int]$Until = 260000
)

$ErrorActionPreference = "Stop"

function Invoke-DotNet {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Arguments)

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet failed with exit code ${LASTEXITCODE}: $($Arguments -join ' ')"
    }
}

$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    $corpusFull = [IO.Path]::GetFullPath($CorpusDirectory)
    $workingFull = [IO.Path]::GetFullPath($WorkingDirectory)
    $full = Join-Path $workingFull "contextual-training.fbm"

    if (-not (Test-Path -LiteralPath (Join-Path $corpusFull "train.jsonl") -PathType Leaf)) {
        throw "Compiled corpus not found: $corpusFull"
    }

    New-Item -ItemType Directory -Path $workingFull -Force | Out-Null
    Invoke-DotNet build Fishbrain.slnx -c Release --no-restore
    # Resume only a matching contextual checkpoint. The first 40000 updates are MLM pretraining;
    # a semantic gate after 20000 updates would test an untrained planner.
    Invoke-DotNet run -c Release --no-build --project Fishbrain.LegacyCli -- teach `
        $corpusFull $full --planned 260000 --until $Until
    Write-Host "Checkpoint retained at $full. Completion does not promote a model."
}
finally {
    Pop-Location
}
