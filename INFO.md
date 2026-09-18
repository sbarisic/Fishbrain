# Fishbrain engineering boundaries

> These sections describe the preserved contextual runtime. Current production
> ownership, tool validation, memory and release boundaries are documented in
> [CAUSAL_MODEL.md](CAUSAL_MODEL.md). The causal runtime has no reducer or agenda.

## Authority

The host owns persona, approved player profiles and world state. The state reducer
owns bounded session memory and agenda changes. Generated text never creates an
approved fact and is not used as a session-fact source in the contextual runtime.

`IGameTool` implementations remain responsible for transactionality and idempotency.
Invocation keys are deterministic from conversation and turn IDs. If a tool returns
malformed output after an invocation, the runtime preserves that attempted invocation
and will not send another action that turn. It cannot roll back arbitrary host code.

## Model lifetime

A loaded contextual brain shares immutable float32 inference weights. Each reply owns
scratch tensors, its decoder cache and RNG. Training owns a different parameter copy,
gradient arrays and optimizer moments. Decoder polishing freezes encoder/planner state.
Callers must not mutate their request collections concurrently with a reply.

Inference reuses exact-length float buffers from a pool capped at 128 MiB. Layer
scopes return intermediate buffers after their output is retained by the reply.
Concurrent replies hold separate live buffers; the pool is included in resource checks.

Inference config and domain definitions are immutable. Domain bindings snapshot
schemas, aliases and typed response definitions. Loading requires a matching domain
fingerprint; tool registration alone grants no learned semantic coverage.

## Validation

Use `dotnet run -c Release --project Fishbrain.Tests -- --unit` for implementation
checks without claiming the shipped artifact works. Default runtime tests include the
shipped-model smoke and must fail while the checked-in artifact is incompatible.
The release script repeats artifact smoke on the packaged runtime/model pair.

New finite-difference checks cover bidirectional and causal attention, encoder-decoder
gradient flow, scalar/SIMD parity, and decoder-cache equivalence. Small learning tests
cover identical final utterances with different histories and a distinct observatory
domain. They demonstrate trainability, not held-out conversational quality.

## Release status and remaining work

See [CONTEXTUAL_IMPLEMENTATION.md](CONTEXTUAL_IMPLEMENTATION.md) for measured results.
The requested redesign is not fully accepted or released. Complete training, held-out
behavioral evaluation, ablations and two-human conversation review are still required.
Long-context latency currently fails the engineering target.

Runtime, training, CLI and demo-domain responsibilities now have separate assemblies.
The runtime references no other Fishbrain project. Clause-level fact updates and
four-entry agenda predictions have numerical and reducer coverage. Broader learned
behavior, including pending-action continuations, remains subject to acceptance gates.

Preserve failed candidates and their reports. Do not lower thresholds, migrate old
weights, or overwrite the shipped artifact to make a development run appear complete.
