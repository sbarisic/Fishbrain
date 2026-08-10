param(
    [string]$CorpusDirectory = "data/compiled",
    [string]$WorkingDirectory = "data/training"
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
    $pilot = Join-Path $workingFull "pilot.fbm"
    $full = Join-Path $workingFull "training-checkpoint.fbm"

    if (-not (Test-Path -LiteralPath (Join-Path $corpusFull "train.jsonl") -PathType Leaf)) {
        throw "Compiled corpus not found: $corpusFull"
    }
    if (Test-Path -LiteralPath $pilot -PathType Leaf) {
        throw "Pilot checkpoint already exists and must not be resumed: $pilot"
    }
    if (Test-Path -LiteralPath $full -PathType Leaf) {
        throw "Full checkpoint already exists: $full"
    }

    New-Item -ItemType Directory -Path $workingFull -Force | Out-Null
    Invoke-DotNet build Fishbrain.slnx -c Release --no-restore
    Invoke-DotNet run -c Release --no-build --project Fishbrain -- teach `
        $corpusFull $pilot --planned 260000 --until 20000
    Invoke-DotNet run -c Release --no-build --project Fishbrain -- evaluate `
        (Join-Path $corpusFull "validation.jsonl") $pilot --gate pilot

    Remove-Item -LiteralPath $pilot -Force
    $pilotGeneration = Join-Path $workingFull "pilot-best-generation.fbm"
    if (Test-Path -LiteralPath $pilotGeneration -PathType Leaf) {
        Remove-Item -LiteralPath $pilotGeneration -Force
    }

    Invoke-DotNet run -c Release --no-build --project Fishbrain -- teach `
        $corpusFull $full --planned 260000 --until 260000
}
finally {
    Pop-Location
}
