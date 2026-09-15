param(
    [Parameter(Mandatory = $true)]
    [string]$TrainingCheckpoint,

    [Parameter(Mandatory = $true)]
    [string]$ReviewedConversationFile,

    [string]$CorpusDirectory = "data/compiled-contextual",
    [string]$CandidateModel = "data/training/model-candidate.fbm",
    [string]$ArtifactsPath = "data/logs/discourse-release-artifacts"
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
    $trainingFull = [IO.Path]::GetFullPath($TrainingCheckpoint)
    $reviewFull = [IO.Path]::GetFullPath($ReviewedConversationFile)
    $corpusFull = [IO.Path]::GetFullPath($CorpusDirectory)
    $candidateFull = [IO.Path]::GetFullPath($CandidateModel)
    $artifactsFull = [IO.Path]::GetFullPath($ArtifactsPath)

    if (-not (Test-Path -LiteralPath $trainingFull -PathType Leaf)) {
        throw "Training checkpoint not found: $trainingFull"
    }
    if (-not (Test-Path -LiteralPath $reviewFull -PathType Leaf)) {
        throw "Two-reviewer conversation file not found: $reviewFull"
    }

    Invoke-DotNet format Fishbrain.slnx --no-restore --verify-no-changes
    Invoke-DotNet build Fishbrain.slnx -c Release --artifacts-path $artifactsFull

    $brain = Join-Path $artifactsFull "bin/Fishbrain/release/Fishbrain.dll"
    $runtimeTests = Join-Path $artifactsFull "bin/Fishbrain.Tests/release/Fishbrain.Tests.dll"
    $generator = Join-Path $artifactsFull "bin/Fishbrain.DataGenerator/release/Fishbrain.DataGenerator.dll"
    $generatorTests = Join-Path $artifactsFull "bin/Fishbrain.DataGenerator.Tests/release/Fishbrain.DataGenerator.Tests.dll"

    Invoke-DotNet $brain export $trainingFull $candidateFull $corpusFull
    Invoke-DotNet $brain selftest

    $env:FISHBRAIN_TEST_MODEL = $candidateFull
    try {
        Invoke-DotNet $runtimeTests
    }
    finally {
        Remove-Item Env:FISHBRAIN_TEST_MODEL -ErrorAction SilentlyContinue
    }

    Invoke-DotNet $generatorTests
    Invoke-DotNet $generator audit --input $corpusFull --manifest data/sources.json
    Invoke-DotNet $brain evaluate (Join-Path $corpusFull "validation.jsonl") $candidateFull --gate stage
    Invoke-DotNet $brain evaluate (Join-Path $corpusFull "test.jsonl") $candidateFull --gate release

    $reviewSample = Join-Path (Split-Path -Parent $reviewFull) "conversation-sample.jsonl"
    Invoke-DotNet $brain conversation-sample $candidateFull data/benchmarks/conversation-scenarios.jsonl $reviewSample
    Invoke-DotNet $brain conversation-gate $reviewSample $reviewFull
    Invoke-DotNet $brain inspect $candidateFull
    Invoke-DotNet $brain artifact-smoke $candidateFull
    Invoke-DotNet $brain profile-contextual $candidateFull 32
    Invoke-DotNet $brain acceptance-contextual $candidateFull (Join-Path $artifactsFull "contextual-acceptance.json")
    Invoke-DotNet $brain compare-contextual $corpusFull $candidateFull (Join-Path $artifactsFull "contextual-comparison.json")

    @(
        "who are you?",
        "i am not a traveler.",
        "i am a scientist.",
        "what do you mean?",
        ""
    ) | & dotnet $brain chat $candidateFull
    if ($LASTEXITCODE -ne 0) {
        throw "Live CLI conversation failed with exit code $LASTEXITCODE."
    }

    $modelHash = (Get-FileHash -LiteralPath $candidateFull -Algorithm SHA256).Hash.ToLowerInvariant()
    $package = Join-Path $root "data/releases/contextual-$($modelHash.Substring(0, 12))"
    Invoke-DotNet publish Fishbrain/Fishbrain.csproj -c Release --no-build --artifacts-path $artifactsFull -o $package
    $packagedModels = Join-Path $package "data/models"
    New-Item -ItemType Directory -Path $packagedModels -Force | Out-Null
    Copy-Item -LiteralPath $candidateFull -Destination (Join-Path $packagedModels "model-latest.fbm")
    Invoke-DotNet (Join-Path $package "Fishbrain.dll") artifact-smoke (Join-Path $packagedModels "model-latest.fbm")
    Write-Host "ALL DISCOURSE RELEASE GATES PASSED. MATCHING RUNTIME AND MODEL PACKAGED AT $package"
}
finally {
    Pop-Location
}
