# Model Backends

> One agent loop, many models: run a GGUF model inside your process, or call a remote API. The agent code sees a `Microsoft.Extensions.AI` `IChatClient` either way.

## Contents

1. [Overview](#overview)
2. [Model Types](#model-types)
3. [Provider Registration and `ChatClientFactory`](#provider-registration-and-chatclientfactory)
4. [Selecting a Model](#selecting-a-model)
5. [In-Process Local Models (`LlamaCpp`)](#in-process-local-models-llamacpp)
6. [Remote Providers](#remote-providers)
7. [Images and PDFs](#images-and-pdfs)

---

## Overview

The core package `Kudos.Agent.Sdk` contains **no model client**. Two optional packages add backends, and you can also pass any `IChatClient` you build yourself to `AgentSessionBuilder.UseChatClient`.

| Package | Adds | Where the model runs |
|---|---|---|
| `Kudos.Agent.Sdk.LlamaSharp` | `LlamaCpp` | Inside your process (LLamaSharp / llama.cpp). Prompts and files never leave the machine. |
| `Kudos.Agent.Sdk.Providers` | `Anthropic`, `OpenAI`, `AzureOpenAI`, `OpenAICompatible`, `Bedrock`, `Gemini` | On a remote service, or a server you run. |

All SDK packages are version 0.7.0 and target `net10.0`.

> **Remote models send data off the machine.** With every type except `LlamaCpp`, the prompt is sent to the configured service. That includes the system prompt, the conversation, and tool results such as file contents and script output. `OpenAICompatible` on a loopback address (`localhost`, `127.0.0.1`, `::1`) is the only remote type that stays on the machine. Remote models are **opt-in**: keep a `LlamaCpp` entry as `models[0]`, select a remote entry explicitly (for example with `MODEL_ID`), and tell the user before you use it (see [Remote notice and credential check](#remote-notice-and-credential-check)).

---

## Model Types

`models[].type` takes one of the `ModelType` values (namespace `Kudos.Agent.Config`). Spellings are matched case-insensitively and may be PascalCase, camelCase or kebab-case, with or without hyphens. See [CONFIGURATION.md](CONFIGURATION.md#parsing-rules).

| `ModelType` | Example JSON spellings | Package | Backend | `IsRemote` |
|---|---|---|---|---|
| `LlamaCpp` (default) | `"LlamaCpp"`, `"llama-cpp"` | LlamaSharp | In-process GGUF via LLamaSharp 0.30.0 | `false` |
| `OpenAICompatible` | `"OpenAICompatible"`, `"openai-compatible"` | Providers | OpenAI Chat Completions against any compatible server | `false` only on loopback |
| `Anthropic` | `"Anthropic"`, `"anthropic"` | Providers | Claude API, official `Anthropic` SDK | `true` |
| `OpenAI` | `"OpenAI"`, `"openai"` | Providers | OpenAI Chat Completions, `Microsoft.Extensions.AI.OpenAI` | `true` |
| `AzureOpenAI` | `"AzureOpenAI"`, `"azure-openai"` | Providers | Azure OpenAI v1 API through the OpenAI client | `true` |
| `Bedrock` | `"Bedrock"`, `"bedrock"` | Providers | Amazon Bedrock Converse, `AWSSDK.Extensions.Bedrock.MEAI` | `true` |
| `Gemini` | `"Gemini"`, `"gemini"` | Providers | Gemini Developer API, `Google.GenAI` SDK | `true` |

---

## Provider Registration and `ChatClientFactory`

Register each backend package once at startup, then create the client from a `models[]` entry:

```csharp
using Kudos.Agent.Config;
using Kudos.Agent.Llm;

LlamaSharpChatClientProvider.Register();   // Kudos.Agent.Sdk.LlamaSharp → LlamaCpp
RemoteChatClientProvider.Register();       // Kudos.Agent.Sdk.Providers  → the six remote types

var config = KudosAgentConfig.Load("kudos-agent.json")
    .WithEnvOverrides(ChatClientFactory.ProcessEnvironment());
IChatClient client = await ChatClientFactory.CreateAsync(config.PrimaryModel);
```

Both `Register()` calls are idempotent. Register only the packages you reference.

| `ChatClientFactory` member | Description |
|---|---|
| `Register(IChatClientProvider)` | Registers (or replaces) the provider for each type in `provider.ModelTypes`. Safe to call more than once. The last registration for a type wins. |
| `IsRegistered(ModelType)` | `true` when a provider is registered for the type. |
| `CreateAsync(model, httpClient = null, cancellationToken = default, environment = null)` | Creates the client through the registered provider. If no provider is registered for the type, it throws `NotSupportedException` with a message that names the package and the `Register()` call to add. `environment` defaults to `ProcessEnvironment()`. `httpClient` is used only by the Anthropic backend (useful in tests). |
| `IsRemote(ModelConfig)` | `false` for `LlamaCpp`, and for `OpenAICompatible` whose `serverUrl` is loopback. `true` otherwise. |
| `RemoteNotice(ModelConfig)` | A user-facing notice that data leaves the machine. For a host on a private network (RFC 1918, link-local, IPv6 ULA, `*.local`), the notice says the data stays on the local network. |
| `CredentialWarning(model, env)` | A warning string when the model's credential is not visible in `env`, otherwise `null`. |
| `MissingProviderMessage(ModelType)` | The text used in the `NotSupportedException` above. |
| `ProcessEnvironment()` | The process environment as a case-insensitive dictionary. |

**Custom backends.** Implement `IChatClientProvider`: the `ModelTypes` property, `CreateAsync(ModelConfig, ChatClientProviderContext, CancellationToken)` and `CredentialWarning(...)`. Then pass an instance to `ChatClientFactory.Register`. `ChatClientProviderContext` carries the optional `HttpClient` and the `Environment` dictionary. If you don't need config-driven selection, skip the factory and pass any `IChatClient` to `AgentSessionBuilder.UseChatClient`. The session does not dispose the client, so you own its lifetime. See [API.md](API.md).

### Remote notice and credential check

```csharp
var model = config.PrimaryModel;
if (ChatClientFactory.IsRemote(model))
{
    Console.WriteLine(ChatClientFactory.RemoteNotice(model));
    // the process environment with the model's keys filled from the OS secret store (SECRETS.md)
    var environment = SecretsLoader.Resolve(ChatClientFactory.ProcessEnvironment(), SecretsLoader.CredentialKeys(model.Type));
    if (ChatClientFactory.CredentialWarning(model, environment) is { } warning)
        Console.Error.WriteLine(warning);
}
```

---

## Selecting a Model

| Mechanism | Effect |
|---|---|
| `models[0]` | The primary model, returned by `config.PrimaryModel`. Hosts create their client from it. |
| `MODEL_ID` env var | Moves the entry with that `id` (exact, case-sensitive match) to position 0. If no entry matches, `WithEnvOverrides` throws `InvalidOperationException`. |
| `MODEL` env var | Replaces `config.model` of the (new) `models[0]`: a GGUF path for `LlamaCpp`, a model ID or deployment name otherwise. |
| `SERVER_URL` env var | Replaces `config.serverUrl` of the (new) `models[0]`. |
| `config.contextWindow` | The primary model's window becomes the compaction window. A valid `CONTEXT_WINDOW` env var takes precedence. |

The environment variables take effect only if your host calls `WithEnvOverrides`. Full rules: [CONFIGURATION.md](CONFIGURATION.md#environment-variable-overrides).

```bash
MODEL_ID=claude ./MyHost          # run the "claude" entry instead of models[0]
```

---

## In-Process Local Models (`LlamaCpp`)

`Kudos.Agent.Sdk.LlamaSharp` loads a GGUF model with LLamaSharp 0.30.0 inside your process. LLamaSharp has no tool-calling API, so this package adds a tool-call protocol layer: the prompt is rendered and tool calls are parsed in the Qwen chat format, which makes it designed for Qwen-family chat models. The package also reuses the KV cache across turns, so only the new part of the conversation is decoded each turn (see [KV-CACHE.md](KV-CACHE.md)).

### Setup

1. Reference `Kudos.Agent.Sdk.LlamaSharp`, **and one LLamaSharp native backend package** at the same version as `LLamaSharp` (0.30.0). This package does not pick a backend for you:

   | Backend package | Hardware |
   |---|---|
   | `LLamaSharp.Backend.Cpu` | CPU only |
   | `LLamaSharp.Backend.Cuda12` | NVIDIA GPU, CUDA 12 |
   | `LLamaSharp.Backend.Cuda13` | NVIDIA GPU, CUDA 13 |

2. Call `LlamaSharpChatClientProvider.Register()` once.
3. Add a `LlamaCpp` entry to `models[]`.

```json
{ "id": "agent", "type": "LlamaCpp",
  "config": { "model": "/models/<model>.gguf", "contextSize": 262144, "gpuLayers": 30, "mainGpu": 0, "temperature": 0.2, "maxTokens": 32768 } }
```

### Settings

| `config` key | Default | Description |
|---|---|---|
| `model` | — (required) | Path to the GGUF file. `Validate()` rejects an empty value. `CreateAsync` throws `FileNotFoundException` if the file does not exist. |
| `contextSize` | `4096` | llama.cpp context length in tokens. A prompt longer than this fails with a context-overflow exception. Keep `contextCompression.modelContextWindow` (or `config.contextWindow`) at or below this value so that compaction fires first. |
| `gpuLayers` | `999` | Layers offloaded to the GPU (`999` = all). Lower it when the model does not fit in VRAM. The remaining layers run on the CPU. |
| `mainGpu` | `0` | Index of the main GPU. |
| `temperature` | `0.2` | Sampling temperature. |
| `maxTokens` | `32768` | Maximum tokens generated per model call. |
| `seqMax` | `1` | Conversations the one context holds at once (1–256). `1`: sessions sharing the model take turns, and each switch processes the next conversation's whole prompt again. Greater than 1: each slot of `subAgentPolicy.slots` is its own sequence with its own KV cache — a parent resumes after a sub-agent with only the new tokens, and sub-agents or workflow participants on different slots run in parallel (their generation steps are decoded together). The slots in use must be below `seqMax` (`5` for the default slots 1–4). See [MULTI-AGENT.md](MULTI-AGENT.md#models-and-parallelism). |
| `kvUnified` | `true` | With `seqMax` > 1: `true` = the sequences share the `contextSize` KV cells (one conversation may use all of them; idle conversations are evicted when it runs out, and a step waits when all others are running); `false` = each gets `contextSize / seqMax`. A shared KV slows conversations that run at the same time on a large model — see [CONFIGURATION.md](CONFIGURATION.md#3-models). |
| `mtp` | unset (on when the model has an MTP head) | MTP speculative decoding — see [MTP speculative decoding](#mtp-speculative-decoding). |
| `effort` | `null` | **Ignored** for `LlamaCpp`. `Validate()` still checks the value. |
| `serverUrl`, `region`, `timeoutSeconds`, `maxRetries` | — | Not used. |

Settings that `kudos-agent.json` does not expose use fixed values when the client comes from `ChatClientFactory`: seed `42`, batch size `512`, reasoning effort `"xhigh"`, and the llama.cpp default GPU split. To change them, create the client directly:

```csharp
var client = await LlamaSharpChatClient.CreateAsync(
    modelPath, gpuLayers: 30, mainGpu: 0, contextSize: 131072,
    new LlamaSharpChatClientOptions
    {
        Temperature = 0.2f, MaxTokens = 16384, Seed = 42, BatchSize = 512,
        ReasoningEffort = "medium",                      // "xhigh" | "medium" | "low"; unknown values → "xhigh"
        SplitMode = LLama.Native.GPUSplitMode.None,      // single-GPU execution; null = llama.cpp default
        TensorSplit = [3f, 2f],                          // per-GPU layer ratio; null = by free VRAM
        SeqMax = 5, KvUnified = true,                    // several conversations (requests pick one by slot — LlamaServerSlot.OptionKey)
    });
```

### MTP speculative decoding

Some models carry an MTP (multi-token prediction) head next to their main layers — Qwen3.8-27B has one (`blk.64`).
The in-process client uses it to draft one token ahead — **by default**: when `mtp` is not set (or
`LlamaSharpChatClientOptions.Mtp` is null), MTP is on exactly when the loaded model has an MTP head, and a model without
one loads and runs as before. `"mtp": false` turns it off; `"mtp": true` requires the head.

1. The MTP head proposes the token after the one just sampled.
2. The model decodes both tokens in one step and samples the next position as it always does.
3. When the sampled token equals the draft, the draft is kept — two tokens for one step. Otherwise the draft's KV cell
   (and the recurrent state, for hybrid models) is rolled back and generation continues from the sampled token.

Every output token is the one the model itself samples, in the same order as without MTP, so the output is the
model's own. It can still differ from a run without MTP where two tokens are nearly tied: a two-token step uses
different GPU kernels than a one-token step, and the tiny difference can tip the tie (Qwen3.8-27B, 768 greedy tokens:
one such point, logits 21.980 vs 21.972). The speed-up depends on how often the draft is right: measured with
Qwen3.8-27B, 1.43–1.64× the tokens per second on single prompts and 1.48× across the agent E2E classes (91 % of drafts
kept; the classes took 0.9× the time).

```json
{ "id": "local", "type": "LlamaCpp",
  "config": { "model": "/models/Qwen3.8-27B-UD-Q4_K_XL.gguf", "contextSize": 16384 } }
```

This entry already uses MTP (the model has a head). Add `"mtp": false` to save the memory below or to get output that never
depends on a near tie.

- **Settings**: unset = on with an MTP head, off without; `true` = on (a model without an MTP head fails to load with
  `MTP needs a model with an MTP head`); `false` = off (the model and context are created exactly as without MTP).
- **Several sequences** (`seqMax` > 1 — a parent and its sub-agents on one model): every generating sequence drafts and
  checks in the same decodes (one draft decode for all of them, then one decode with two rows per sequence); a wrong
  draft is rolled back in its own sequence only. The draft context gets the same sequences (Qwen3.8-27B, `seqMax` 2:
  +0.7 GiB VRAM for the draft context; each sequence's rollback snapshot doubles its recurrent state).
- **Memory**: a draft context next to the model (+0.6 GiB VRAM at `contextSize` 16384, +2.6 GiB at 262144) and a second
  recurrent state to roll a wrong draft back (+150 MiB for a 27B model).
- **Prompt processing**: unchanged — prompts are decoded exactly as without MTP, and the draft sees only the tokens the
  model generates. (Giving the draft the end of each prompt raised the share of kept drafts by 2–3 points but did not
  change the generation speed or the time of the agent E2E classes, so it is not done.)
- **Images**: with a projector, image positions are not given to the draft. Drafts right after an image may be kept less
  often; the output is unaffected.
- `StatefulLlamaExecutor.GetSpeculativeStats()` returns the drafted and kept counts; `GetLastGenerationStats(sequence)`
  returns the tokens and time of the most recent generation (without its prompt).

### Memory and process notes

- Loading takes time: tens of seconds for a ~17 GB model. Load once and reuse the client across sessions.
- Memory use is roughly the GGUF weights (split between VRAM and RAM according to `gpuLayers`) plus a KV cache that grows with `contextSize`. A 262144-token context needs much more memory than the default 4096. Size both the model and the context for the host application's memory budget. With `seqMax` > 1 each sequence also gets its own recurrent state, allocated when the model loads (about 150 MiB per sequence for a 27B hybrid model; about 19 MiB for a 0.8B one). A model with an MTP head also gets MTP's draft context and rollback state by default ([MTP speculative decoding](#mtp-speculative-decoding) — `"mtp": false` saves them).
- The native library is loaded into your process, so a native crash takes the host process down with it.
- `LlamaSharpChatClient` implements `IDisposable`. Dispose it when you no longer need the model.
- `LlamaCpp` needs no credential (`CredentialWarning` always returns `null`).

---

## Remote Providers

`Kudos.Agent.Sdk.Providers` wraps each vendor's official .NET SDK behind `IChatClient`. **Credentials come only from environment variables (or, for Bedrock, the AWS credential chain). They are never read from `kudos-agent.json`.**

### Summary

| Type | Required `config` | Credential (environment) | Endpoint | `effort` sent as |
|---|---|---|---|---|
| `Anthropic` | `model` | `ANTHROPIC_API_KEY`, then `ANTHROPIC_AUTH_TOKEN`, then an `ant auth login` profile | Anthropic SDK default; `serverUrl` ignored | adaptive thinking + `output_config.effort` |
| `OpenAI` | `model` | `OPENAI_API_KEY` (required) | OpenAI SDK default; `serverUrl` optional override | `reasoning_effort` |
| `AzureOpenAI` | `model` (= deployment name) | `AZURE_OPENAI_API_KEY` (required) | `serverUrl`, else `AZURE_OPENAI_ENDPOINT` (required) | `reasoning_effort` |
| `OpenAICompatible` | `serverUrl` | `OPENAI_API_KEY` (optional) | `serverUrl` | `reasoning_effort` |
| `Bedrock` | `model` (model ID or inference profile ID) | `AWS_ACCESS_KEY_ID` + `AWS_SECRET_ACCESS_KEY` (+ `AWS_SESSION_TOKEN`), else the AWS SDK default chain | region from `region`, else `AWS_REGION`, else `AWS_DEFAULT_REGION` (required); `serverUrl` optional override | not allowed (`Validate()` rejects it) |
| `Gemini` | `model` | `GEMINI_API_KEY`, else `GOOGLE_API_KEY` (required) | Google.GenAI default; `serverUrl` optional override | `thinkingConfig.thinkingLevel` |

### Common settings

| `config` key | Default | Remote behaviour |
|---|---|---|
| `maxTokens` | `32768` | The default output cap for each request. `Anthropic` caps it at **16000** (`AnthropicChatClient.MaxNonStreamingOutputTokens`), because the agent issues non-streaming requests. The repository's sample config uses `16000` for remote models. |
| `effort` | `null` | `low`, `medium`, `high` or `xhigh` (case-sensitive). `null` uses the model's default. The value maps to the M.E.AI `ReasoningEffort` (`xhigh` → `ExtraHigh`). |
| `timeoutSeconds` | `null` | Per-request HTTP timeout. When `null`, `OpenAI`, `AzureOpenAI`, `OpenAICompatible`, `Bedrock` and `Gemini` use **1800 s** (`ModelSettings.DefaultTimeoutSeconds`), which replaces the vendor SDKs' 100 s default so that long generations and compaction summaries don't time out. `Anthropic` keeps the Anthropic SDK default. |
| `maxRetries` | `null` | Retries after a failed attempt. When `null`, the vendor SDK default applies (per the source: OpenAI 3, Anthropic 2, Bedrock 2, Gemini 4). Applied as the OpenAI `ClientRetryPolicy`, Anthropic `MaxRetries`, Bedrock `MaxErrorRetry`, and Gemini `RetryOptions.Attempts` (= `maxRetries + 1`). |
| `contextWindow` | `null` | Set it to the model's real window (for example `1000000`) so that compaction uses it when this model is primary. |
| `temperature` | `0.2` | Not sent by any remote adapter. |
| `contextSize`, `gpuLayers`, `mainGpu` | — | Not used. |

Every remote adapter also rewrites tool blocks that the remote API would reject (for example a tool result left without its call after compaction) as plain text, so the request stays valid.

### Anthropic

```json
{ "id": "claude", "type": "Anthropic", "config": { "model": "claude-sonnet-5-5", "maxTokens": 16000, "effort": "high", "contextWindow": 1000000 } }
```

- **Credentials:** `ANTHROPIC_API_KEY`, then `ANTHROPIC_AUTH_TOKEN`, from the environment dictionary (the process environment with the OS secret store filling missing keys — [SECRETS.md](SECRETS.md)); the one found is passed to the Anthropic SDK explicitly. With neither, the Anthropic SDK falls back to its own resolution (an `ant auth login` profile). If neither variable is set, `CredentialWarning` returns a warning that the SDK will rely on an `ant auth login` profile. Creating the client does not fail at that point.
- **Request rules applied on every call:**
  - `effort` is sent as adaptive thinking plus `output_config.effort`.
  - Automatic top-level prompt caching (`cache_control`) covers tools, the system prompt and the growing conversation prefix.
  - `temperature`, `top_p` and `top_k` are removed.
  - A forced tool choice is downgraded to `auto`.
- `serverUrl` and `region` are ignored.

### OpenAI

```json
{ "id": "openai", "type": "OpenAI", "config": { "model": "gpt-6-astra", "maxTokens": 16000, "effort": "high", "contextWindow": 1000000 } }
```

- Uses Chat Completions. `maxTokens` is sent as `max_completion_tokens` and `effort` as `reasoning_effort`.
- `OPENAI_API_KEY` is required. If it is missing, `CreateAsync` throws `InvalidOperationException: OPENAI_API_KEY is not set`.
- `serverUrl` (optional) replaces the API endpoint, for example to go through a proxy.

### Azure OpenAI

```json
{ "id": "azure-openai", "type": "AzureOpenAI", "config": { "model": "<deployment-name>", "serverUrl": "https://<resource>.openai.azure.com", "maxTokens": 16000 } }
```

- Uses the Azure OpenAI **v1 API** through the plain OpenAI client. `model` is the **deployment name**.
- Endpoint: `serverUrl`, else `AZURE_OPENAI_ENDPOINT`. If the endpoint does not already end in `/openai/v1`, that path is appended (`https://<resource>.openai.azure.com` → `https://<resource>.openai.azure.com/openai/v1/`). If neither source is set, `CreateAsync` throws `InvalidOperationException`.
- `AZURE_OPENAI_API_KEY` is required. If it is missing, `CreateAsync` throws `InvalidOperationException`.

### OpenAI-compatible servers

```json
{ "id": "local-server", "type": "OpenAICompatible", "config": { "model": "<served-model-name>", "serverUrl": "http://localhost:8001/v1/", "maxTokens": 16384, "contextWindow": 262144 } }
```

- Works with any server that speaks the OpenAI Chat Completions API (llama-server, vLLM, Ollama, ...).
- `serverUrl` is required. `model` is optional; if it is omitted, `"default"` is sent.
- No key is needed. If `OPENAI_API_KEY` is set, though, **it is sent to this server**. Unset it, or scope it per process, when the server is not one you trust with that key.
- On a loopback `serverUrl` the model counts as local (`IsRemote == false`). On a private-network host, data leaves the machine but stays on the local network, and `RemoteNotice` says so.

### Amazon Bedrock

```json
{ "id": "bedrock", "type": "Bedrock", "config": { "model": "global.anthropic.claude-sonnet-5-5", "region": "us-east-1", "maxTokens": 16000, "contextWindow": 1000000 } }
```

- Uses the Converse API. `model` is a model ID or an inference profile ID.
- **Region:** `config.region`, else `AWS_REGION`, else `AWS_DEFAULT_REGION`. If none is set, `CreateAsync` throws `InvalidOperationException: Bedrock region: set config.region or AWS_REGION`.
- **Credentials:**
  - If `AWS_ACCESS_KEY_ID` and `AWS_SECRET_ACCESS_KEY` are both set, they are used, together with `AWS_SESSION_TOKEN` when present.
  - Otherwise the AWS SDK default chain is used (profile, SSO or instance role).
  - `CredentialWarning` never warns for Bedrock, because the chain cannot be checked up front.
- `serverUrl` (optional) sets the service URL, for example a VPC endpoint, and signs requests for the configured region.
- **`effort` is rejected by `Validate()`.** The Converse adapter would send `thinking.budget_tokens`, which Claude 5 models reject.

### Google Gemini

> **A free-tier Gemini API key is not enough to run the agent.** A key from Google AI Studio on a project without billing has a small quota. On 2026-11-25 `gemini-3.6-flash` allowed **5 requests per minute and 20 per day**, per project and model. An agent turn makes several to dozens of model calls, so a run fails with `429 You exceeded your current quota` within the first turn or after one or two tasks, and the SDK does not retry it. Enable billing on the key's Google Cloud project before using Gemini with the agent (details below and in [TROUBLESHOOTING.md](TROUBLESHOOTING.md#gemini-you-exceeded-your-current-quota-429-freetier)).

```json
{ "id": "gemini", "type": "Gemini", "config": { "model": "gemini-3.6-flash", "maxTokens": 16000, "effort": "high", "contextWindow": 1000000 } }
```

- Uses the Gemini Developer API with an API key. The key is read from `GEMINI_API_KEY`, or from `GOOGLE_API_KEY` if the first is not set. If both are missing, `CreateAsync` throws `InvalidOperationException`.
- `maxTokens` is sent as `generationConfig.maxOutputTokens` and `effort` as `thinkingConfig.thinkingLevel` (`"high"` → `HIGH`).
- `serverUrl` (optional) replaces the API base URL.

#### Choosing the Gemini model (measured 2026-11-25)

| Model | Result |
|---|---|
| `gemini-3.6-flash` (stable) | `200` in 0.9–1.3 s (3 calls, `thinkingLevel` HIGH and LOW). The default in `json/kudos-agent.json`. |
| `gemini-3.5-flash` | `200` in 18.6 s (1 call) |
| `gemini-3.8-flash` (the newest Flash) | `503 UNAVAILABLE` — "This model is currently experiencing high demand." An earlier measurement on another project (2026-10-30) gave 27–76 s per call. |
| `gemini-flash-latest` (alias) | `503 UNAVAILABLE` |

- **Use a stable model name, not the newest one.** The newest model is often overloaded. Overload shows up as `503`, which the SDK retries (below).
- **Don't use the `-latest` aliases.** Google changes the model behind them when a new one ships, so answers can change without any change on your side.
- **A `503` makes the turn hang, then fail.** The Google.GenAI SDK retries `503` with backoff (4 attempts by default) inside the 1800-second request timeout, so the run sits with no output, waiting on an open connection. In the 2026-11-25 run that took 8 min 23 s (from the user input to the error in the session record), then the turn ended with `[error] turn failed: This model is currently experiencing high demand.` To fail fast, set `"maxRetries": 0` or `1` and a smaller `"timeoutSeconds"` on the model. The SDK has no automatic fallback to another model. Switch the model instead (edit `config.model`, or set `MODEL=<name>` for one run).
- **Models are retired.** A retired model stops answering, reportedly with `404 NOT_FOUND`, sometimes before the published date. The SDK does not read the `modelStatus` field that Google returns with every response (it carries `LEGACY` / `RETIRED` and a retirement time), so it gives no warning in advance. Check the [deprecations page](https://aistudio.google.com/docs/deprecations) when you pick a model. To see which models your key can call:

  ```bash
  curl -s -H "x-goog-api-key: $GEMINI_API_KEY" "https://generativelanguage.googleapis.com/v1beta/models?pageSize=200" | grep '"name"'
  ```

- **The free tier cannot run an agent for long.** Measured 2026-11-25 for `gemini-3.6-flash`: **5 generate requests per minute and 20 per day**, per project and model (`429 RESOURCE_EXHAUSTED`, quota ids `GenerateRequestsPerMinutePerProjectPerModel-FreeTier` / `…PerDay…`). An agent turn makes several to dozens of calls, a few seconds apart, so the per-minute limit is hit within the first turn and the daily one within one or two tasks. The Google.GenAI SDK does **not** retry a `429`. The turn fails at once with `Google.GenAI.ClientError: You exceeded your current quota`. Enable billing on the key's Google Cloud project for real use.
- **Tool calling.** Gemini is called through its own API (`functionDeclarations` / `functionCall` / `functionResponse` through the Google.GenAI SDK), not through an OpenAI-compatible endpoint. Built-in tools ran on 2026-11-25 (`execute_code`, shell commands). Skills, MCP tools, A2A agents and sub-agents use the same function-calling path, but they have not been run against Gemini yet: the free-tier quota ran out first. Gemini accepts only part of JSON Schema in tool definitions and limits tool-name length, so an MCP server's tool schema or a long `mcp_<server>_<tool>` name may be rejected. Test the MCP servers you use before relying on them.
- **Data.** Prompts and tool results are sent to Google. On the free tier, Google may use the content to improve its models, so use test data only, not real customer data.

### Credential environment variables

| Variable | Read by | Required |
|---|---|---|
| `ANTHROPIC_API_KEY` | `Anthropic` (Anthropic SDK) | One of the two, or an `ant auth login` profile |
| `ANTHROPIC_AUTH_TOKEN` | `Anthropic` (Anthropic SDK) | (see above) |
| `OPENAI_API_KEY` | `OpenAI` (required), `OpenAICompatible` (optional) | `OpenAI` only |
| `AZURE_OPENAI_API_KEY` | `AzureOpenAI` | yes |
| `AZURE_OPENAI_ENDPOINT` | `AzureOpenAI` when `serverUrl` is not set | if no `serverUrl` |
| `AWS_ACCESS_KEY_ID`, `AWS_SECRET_ACCESS_KEY`, `AWS_SESSION_TOKEN` | `Bedrock` (static credentials) | no; the AWS default chain is the fallback |
| `AWS_REGION`, `AWS_DEFAULT_REGION` | `Bedrock` when `region` is not set | if no `region` |
| `GEMINI_API_KEY`, `GOOGLE_API_KEY` | `Gemini` | one of the two |

They are looked up in the `environment` dictionary passed to `ChatClientFactory.CreateAsync`. Without one, the factory uses the process environment and fills the model type's missing credential keys from the OS secret store (non-secret settings such as `AZURE_OPENAI_ENDPOINT` and `AWS_REGION` come from the environment only) — see [SECRETS.md](SECRETS.md). Empty or whitespace values count as unset.

---

## Images and PDFs

What a model takes as input decides whether `view_image` is offered and what happens to attachments
([ATTACHMENTS.md](ATTACHMENTS.md)). Set it with `models[].config.inputModalities`; without it, the default per type:

| Type | Default input | Attachments (user message) | Images in tool results |
|---|---|---|---|
| `Anthropic` | text, image, pdf | `image` / `document` blocks | inside `tool_result` |
| `Bedrock` | text, image, pdf | `image` / `document` blocks. A document name may only contain letters, digits, single spaces, `-`, `(`, `)`, `[`, `]` — the SDK sends a cleaned copy (`spec.pdf` → `spec-pdf`) | inside `toolResult` |
| `Gemini` | text, image, pdf | `inlineData` parts | inside `functionResponse` |
| `OpenAI`, `AzureOpenAI` | text, image, pdf | `image_url` / `file` parts | moved to a user message right after the tool messages (a `role: tool` message cannot hold an image) |
| `OpenAICompatible` | **text** | as `OpenAI`, when the server takes them | as `OpenAI` |
| `LlamaCpp` | **text** (text + image with `mmproj`) | images in place in the prompt (the chat template's image position) | inside the tool response, after the image's reference sentence |

For an OpenAI-compatible llama-server, start the server with the model's vision projector (`--mmproj <mmproj>.gguf`;
`/props` then reports `modalities.vision = true`) and set `"inputModalities": ["text", "image"]`. A server without a
projector answers an image with HTTP 500 `image input is not supported - hint: … you may need to provide the mmproj`.
For a Qwen-VL projector, also pass `--image-min-tokens 1024` — the same floor as the in-process `imageMinTokens`. Without it,
llama-server processes a small image at the projector's default (a 256-pixel image = 64 tokens) and logs `Qwen-VL models
require at minimum 1024 image tokens`. With the default, the 27B misread a number on a 256-pixel card once in six reads;
with 1024 it read 6/6.

For the in-process model, set `models[].config.mmproj` to the model's projector GGUF (the `mmproj-F16.gguf` of the
model's distribution):

```json
{ "id": "local", "type": "LlamaCpp",
  "config": { "model": "/models/Qwen3.8-27B-UD-Q4_K_XL.gguf", "mmproj": "/models/mmproj-F16.gguf", "contextSize": 16384 } }
```

- The projector loads with the model (on the GPU when `gpuLayers` > 0 — about +1.1 GiB VRAM for a 27B model's F16
  projector, plus about 110 MiB while an image is evaluated).
- An image costs `imageMinTokens`–`imageMaxTokens` KV cells (default 1,024–1,500). Its position in the sequence advances by
  far less (about the image's longer side in 32-pixel units), so the context holds tokens, not positions — the KV cache
  limit is what `contextSize` caps.
- Images keep incremental KV reuse: a turn after an image processes only its new messages, and a new image in a turn
  (attachment or `view_image`) is evaluated after the existing KV ([KV-CACHE.md](KV-CACHE.md#images)). A full
  regeneration (compaction, resume, a changed history) evaluates every image again — about a second per 1,000-token
  image on a GPU.
- PDFs are not read directly: an attached PDF stays a file in the work directory that the agent can process with a tool
  ([ATTACHMENTS.md](ATTACHMENTS.md)).

---

## See Also

- [CONFIGURATION.md](CONFIGURATION.md) — the full `models[]` schema and environment overrides
- [KV-CACHE.md](KV-CACHE.md) — incremental KV-cache reuse for `LlamaCpp`
- [SESSIONS.md](SESSIONS.md) — context compaction and the context window
- [SECRETS.md](SECRETS.md) — API keys for remote providers
- [QUICK-START.md](QUICK-START.md) — installation and native backends
- [API.md](API.md) — `ChatClientFactory` and provider registration
- [ARCHITECTURE.md](ARCHITECTURE.md) — where model clients sit
- [TROUBLESHOOTING.md](TROUBLESHOOTING.md) — model loading and client errors
- Package READMEs — [Kudos.Agent.Sdk.LlamaSharp](packages/Kudos.Agent.Sdk.LlamaSharp.md) · [Kudos.Agent.Sdk.Providers](packages/Kudos.Agent.Sdk.Providers.md)
