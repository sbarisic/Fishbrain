# Fishbrain

Fishbrain is a deliberately tiny, hackable character-level GPT written in C# and
.NET 10 for short video-game NPC conversations. It is CPU-only, dependency-free,
and keeps the model, scalar autograd engine, tokenizer, training loop, checkpoint
format, and local tool calling small enough to read and modify.

This project is based on the ideas demonstrated by
[martinskuta/microgpt](https://github.com/martinskuta/microgpt), adapted for
uppercase NPC dialogue and simple registered C# tools rather than name generation.

## Features

- One-layer, four-head character Transformer with 32-dimensional embeddings
- Scalar reverse-mode autograd and Adam training
- `A-Z`, `0-9`, and space tokenizer with internal control tokens
- Rolling 256-token context and replies capped at 256 characters
- Focused training on authoritative NPC response tokens
- Checkpointed response vocabulary and exact dialogue memory
- One synchronous, reflected C# tool call per reply
- Intent-conditioned Markov training-data generator
- No external NuGet packages

## Try the included checkpoint

```powershell
dotnet run --project Fishbrain -- chat model.json
```

Example:

```text
> PLAYER HELLO
HELLO TRAVELER
> PLAYER HOW ARE YOU DOING
I AM DOING WELL
> PLAYER CAN YOU HELP ME
TELL ME WHAT YOU NEED
```

Input is normalized to uppercase. Only letters, digits, and whitespace are
accepted; punctuation and other symbols are intentionally unsupported.

## Generate training data

Generate the default 2,000-row catalog:

```powershell
dotnet run --project Fishbrain.DataGenerator -- generate
```

Generate the smaller 300-row corpus used by the included checkpoint:

```powershell
dotnet run --project Fishbrain.DataGenerator -- generate --output data.jsonl --count 300 --seed 42
```

The generator writes JSON Lines records such as:

```json
{"input":"PLAYER HELLO","response":"HELLO TRAVELER"}
```

Questions are varied with intent-conditioned second-order word Markov chains.
Answers use a deliberately small canonical vocabulary suited to this tiny model.

## Train

```powershell
dotnet run --project Fishbrain -- train data.jsonl model.json 3000
```

Training refuses to overwrite an existing checkpoint. Move the old checkpoint,
choose another filename, or use `resume` with a new total step count.

## Register a tool

```csharp
sealed class PlayerTools(PlayerState player)
{
    [GameTool("GETGOLD")]
    public int GetGold(string playerId) => player.Gold;
}

var brain = Brain.Load("model.json");
brain.Tools.Register(new PlayerTools(player));
var reply = brain.Reply("PLAYER HOW MUCH GOLD");
```

Only explicitly attributed public instance methods can be invoked. Tool-backed
training rows bypass exact dialogue memory so inference must use the registered
live tool rather than replaying a stale training result.

## Self-tests

```powershell
dotnet run --project Fishbrain -- selftest
dotnet run --project Fishbrain.DataGenerator -- selftest
```

The tests cover autograd, tokenization, causal behavior, checkpoint round trips,
focused training data, tool validation, Markov generation, deterministic output,
and atomic JSONL replacement.

## Limits

Fishbrain is an educational toy, not a general-purpose language model. Exact
training inputs return their authoritative stored response. Unseen inputs use the
GPT to choose among trained valid responses, so intent selection remains
best-effort. Dynamic game facts should come from registered tools.
