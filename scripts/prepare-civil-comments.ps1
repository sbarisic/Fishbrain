param(
    [string]$InputPath = "data/raw/civil-comments-validation.parquet",
    [string]$OutputPath = "data/raw/civil-comments-selected.jsonl"
)

$ErrorActionPreference = "Stop"
$inputFull = (Resolve-Path -LiteralPath $InputPath).Path
$outputFull = [IO.Path]::GetFullPath($OutputPath)
$outputDirectory = [IO.Path]::GetDirectoryName($outputFull)
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null

$env:FISHBRAIN_CIVIL_INPUT = $inputFull
$env:FISHBRAIN_CIVIL_OUTPUT = $outputFull
@'
import hashlib
import json
import os
import re
import unicodedata
import pyarrow.parquet as pq

source = os.environ["FISHBRAIN_CIVIL_INPUT"]
destination = os.environ["FISHBRAIN_CIVIL_OUTPUT"]
columns = ["text", "obscene", "threat", "insult", "identity_attack", "sexual_explicit"]
frame = pq.read_table(source, columns=columns).to_pandas()
frame["rowId"] = frame.index.astype(str)
def canonical(text):
    normalized = unicodedata.normalize("NFD", str(text))
    normalized = "".join(character for character in normalized if unicodedata.category(character) != "Mn")
    normalized = normalized.translate(str.maketrans({"\u2018": "'", "\u2019": "'", "\u2010": "-", "\u2011": "-", "\u2012": "-", "\u2013": "-", "\u2014": "-"})).upper()
    normalized = "".join(character if character in "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 .,?!'-:" else " " for character in normalized)
    normalized = re.sub(r"\s+", " ", normalized).strip()
    return normalized if 2 <= len(normalized) <= 220 and normalized[0].isalnum() else None
frame["canonical"] = frame.text.map(canonical)
frame = frame[frame.canonical.notna()].drop_duplicates("canonical").copy()
frame["stable"] = frame.apply(
    lambda row: hashlib.sha256((row["rowId"] + "|" + row["text"]).encode("utf-8")).hexdigest(), axis=1)

selected = []
used = set()
bands = [
    (frame[frame.identity_attack >= 0.5], 100),
    (frame[frame.threat >= 0.5], 100),
    (frame[frame.sexual_explicit >= 0.5], 100),
    (frame[(frame.obscene >= 0.5) | (frame.insult >= 0.5)], 300),
    (frame[(frame.obscene < 0.5) & (frame.threat < 0.5) & (frame.insult < 0.5) &
           (frame.identity_attack < 0.5) & (frame.sexual_explicit < 0.5)], 2420),
]
for band, quota in bands:
    added = 0
    for _, row in band.sort_values("stable").iterrows():
        if row.rowId in used:
            continue
        used.add(row.rowId)
        selected.append(row)
        added += 1
        if added == quota:
            break
    if added != quota:
        raise RuntimeError(f"Civil Comments band supplied {added} of {quota} rows")

selected.sort(key=lambda row: row.stable)
with open(destination, "w", encoding="utf-8", newline="\n") as output:
    for row in selected:
        output.write(json.dumps({
            "rowId": row.rowId,
            "text": row.text,
            "obscene": float(row.obscene),
            "threat": float(row.threat),
            "insult": float(row.insult),
            "identity_attack": float(row.identity_attack),
            "sexual_explicit": float(row.sexual_explicit),
        }, ensure_ascii=False, separators=(",", ":")) + "\n")
print(f"WROTE {len(selected)} {destination}")
'@ | python -
if ($LASTEXITCODE -ne 0) { throw "Civil Comments preparation failed with exit code $LASTEXITCODE." }

Get-FileHash -LiteralPath $outputFull -Algorithm SHA256
