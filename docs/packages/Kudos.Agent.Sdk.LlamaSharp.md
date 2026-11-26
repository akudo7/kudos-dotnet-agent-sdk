# Kudos.Agent.Sdk.LlamaSharp v0.6.0

**In-process local models for the Kudos Agent SDK — prompts and files never leave the machine**

[Features](#features) · [Installation](#installation) · [Quick Start](#quick-start) · [Documentation](#documentation) · [License](#license)

---

## Overview

**Kudos.Agent.Sdk.LlamaSharp** runs the model of [Kudos.Agent.Sdk](Kudos.Agent.Sdk.md) inside your process, using [LLamaSharp](https://github.com/SciSharp/LLamaSharp) (llama.cpp).

It adds the `LlamaCpp` model type: a GGUF model with a Qwen-style tool-call protocol layer and incremental KV-cache reuse across turns (only the new part of the conversation is decoded each turn).

Version 0.x: the API may still change between minor versions.

---

## Features

The model entry in `kudos-agent.json` sets:

- **Model and hardware** — the GGUF path (`model`), `gpuLayers`, `mainGpu`, `contextSize`, `temperature`, `maxTokens`
- **Several conversations in one context** — `seqMax` / `kvUnified`: a parent and its sub-agents keep their own KV caches and run in parallel (see [CONFIGURATION.md](../CONFIGURATION.md))
- **Images** — `mmproj` (the model's image projector GGUF): the model then takes images in attachments and `view_image` results, with incremental KV reuse; `imageMinTokens` / `imageMaxTokens` bound the tokens per image
- **Faster generation** — `mtp` (MTP speculative decoding with the model's own MTP head — same output, faster generation): on by default when the model has an MTP head, such as Qwen3.8-27B, off otherwise; `false` turns it off; works with `seqMax` > 1 too

Notes:

- The native library is loaded into your process; a native crash takes the process down. Size the model (and the context) for the host application's memory.
- Without `Register()`, creating a `LlamaCpp` model fails with a message that names this package.

---

## Installation

Reference this package **and one LLamaSharp native backend** of your choice — this package does not pick one for you:

```bash
dotnet add package Kudos.Agent.Sdk.LlamaSharp --version 0.6.0
dotnet add package LLamaSharp.Backend.Cpu --version 0.30.0       # CPU only
# or: LLamaSharp.Backend.Cuda12 / LLamaSharp.Backend.Cuda13       # NVIDIA GPU
```

Use the same backend version as `LLamaSharp` (0.30.0).

**Requirements:** .NET SDK 10 (target `net10.0`).

---

## Quick Start

Register the provider once at startup:

```csharp
using Kudos.Agent.Llm;

LlamaSharpChatClientProvider.Register();
var client = await ChatClientFactory.CreateAsync(config.PrimaryModel);   // models[0] with "type": "LlamaCpp"
```

---

## Documentation

- **[MODELS.md](../MODELS.md)** — the `LlamaCpp` backend: GGUF, native backend packages, memory
- **[KV-CACHE.md](../KV-CACHE.md)** — incremental KV-cache reuse and the prompt-layout rules a host must respect
- **[CONFIGURATION.md](../CONFIGURATION.md)** — the `models[]` schema
- **[ATTACHMENTS.md](../ATTACHMENTS.md)** — images with the in-process projector
- **[TROUBLESHOOTING.md](../TROUBLESHOOTING.md)** — native backend and model-load failures
- **[README.md](../README.md)** — every SDK document

---

## License

Kudos.Agent.Sdk.LlamaSharp is distributed under the End User License Agreement (the LICENSE file included in this package, also at the repository root):

- **Evaluation** — free for up to 30 days; prototypes allowed, not in production, not distributed
- **Commercial license** — every other use (embedding the SDK in your own applications, production, commercial products, standalone CLI use) requires a commercial license and License Key from the Licensor

The sample code under `samples/` may be viewed, copied and modified for use in your applications. Prompts derived from OpenAI Codex are under the Apache License 2.0 — see the THIRD-PARTY-NOTICES.txt file included in this package.

---

## Author

**Hand-crafted by [Akira Kudo](https://www.linkedin.com/in/akira-kudo-4b04163/) in Tokyo, Japan**

Copyright © 2026 Akira Kudo
