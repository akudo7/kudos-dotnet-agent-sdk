# Configuration Reference — `kudos-agent.json`

> One JSON file configures the agent: which models it can run, how risky operations are gated, when the context is compacted, and which guards, skills and MCP servers are active.

## Contents

1. [Overview](#overview)
2. [File Structure](#file-structure)
3. [Parsing Rules](#parsing-rules)
4. [1. `safety`](#1-safety)
5. [2. `contextCompression`](#2-contextcompression)
6. [3. `models`](#3-models)
7. [4. `skills`](#4-skills)
8. [5. `verify`](#5-verify)
9. [6. `guard`](#6-guard)
10. [7. `mcpServers`](#7-mcpservers)
11. [8. `subAgents`](#8-subagents)
12. [9. `subAgentPolicy`](#9-subagentpolicy)
13. [10. `a2a`](#10-a2a)
14. [11. `attachments`](#11-attachments)
15. [Validation Rules](#validation-rules)
16. [Environment-Variable Overrides](#environment-variable-overrides)
17. [Complete Annotated Example](#complete-annotated-example)

---

## Overview

`kudos-agent.json` is deserialized into the immutable record `KudosAgentConfig` (namespace `Kudos.Agent.Config`, package `Kudos.Agent.Sdk`). Every section except `models` is optional and falls back to the defaults listed below.

```csharp
using Kudos.Agent.Config;
using Kudos.Agent.Llm;

var config = KudosAgentConfig.Load("kudos-agent.json")            // parse + Validate()
    .WithEnvOverrides(ChatClientFactory.ProcessEnvironment());    // optional: env overrides + ${VAR} expansion + Validate()
```

| API | What it does |
|---|---|
| `KudosAgentConfig.Load(path)` | Reads the file and calls `FromJson`. A missing file throws `FileNotFoundException`. |
| `KudosAgentConfig.FromJson(json)` | Deserializes, then runs `Validate()`. Parse errors throw `JsonException`; rule violations throw `InvalidOperationException`. |
| `config.Validate()` | Runs the [validation rules](#validation-rules). It is called for you by `FromJson` and `WithEnvOverrides`. |
| `config.WithEnvOverrides(env)` | Returns a new config with the [environment overrides](#environment-variable-overrides) applied, then validates it again. The SDK never calls it on its own; your host decides whether the environment may change the config. |
| `config.PrimaryModel` | `Models[0]`, the model the host runs. |
| `config.EffectiveContextWindow` | `PrimaryModel.Config.ContextWindow ?? ContextCompression.ModelContextWindow`. |

You can also build the config in code (`new KudosAgentConfig { Models = [...] }`). `AgentSessionBuilder.UseConfig` does **not** validate it, so call `config.Validate()` yourself.

Source: `../src/Kudos.Agent.Sdk/Config/KudosAgentConfig.cs`.

---

## File Structure

```json
{
  "safety":             { },
  "contextCompression": { },
  "models":             [ ],
  "skills":             { },
  "verify":             { },
  "guard":              { },
  "mcpServers":         [ ],
  "subAgents":          [ ],
  "subAgentPolicy":     { },
  "a2a":                { },
  "attachments":        { }
}
```

| Key | Type | Required | Section |
|---|---|---|---|
| `safety` | object | no | [1. safety](#1-safety) |
| `contextCompression` | object | no | [2. contextCompression](#2-contextcompression) |
| `models` | array | **yes**, at least one entry | [3. models](#3-models) |
| `skills` | object | no | [4. skills](#4-skills) |
| `verify` | object | no | [5. verify](#5-verify) |
| `guard` | object | no | [6. guard](#6-guard) |
| `mcpServers` | array | no | [7. mcpServers](#7-mcpservers) |
| `subAgents` | array | no | [8. subAgents](#8-subagents) |
| `subAgentPolicy` | object | no | [9. subAgentPolicy](#9-subagentpolicy) |
| `a2a` | object | no | [10. a2a](#10-a2a) |
| `attachments` | object | no | [11. attachments](#11-attachments) |

---

## Parsing Rules

| Rule | Behaviour |
|---|---|
| Property names | Case-insensitive (`approvalPolicy`, `ApprovalPolicy` and `APPROVALPOLICY` are all accepted). The documented spelling is camelCase. |
| Unknown keys | Ignored at every level, top-level included. For example, an extra top-level `"runner": { ... }` block loads without error and has no effect. |
| Comments | `//` and `/* */` comments are allowed and skipped. |
| Trailing commas | Allowed. |
| Enum values | Must be JSON strings. They match the enum name case-insensitively in PascalCase, camelCase or kebab-case, with or without hyphens. `"on-request"`, `"OnRequest"`, `"onRequest"` and `"onrequest"` are equivalent, and so are `"azure-openai"` and `"AzureOpenAI"`. Any other value throws `JsonException: Invalid value '<raw>' for <EnumType>`. |
| Required members | `models[].id`, `safety.execRules[].prefix`, `safety.execRules[].effect`, `mcpServers[].name` and `mcpServers[].command` are required. If one is missing, deserialization throws `JsonException`. |
| Free-form strings | `contextCompression.mode` and `models[].config.effort` are plain strings that are matched **case-sensitively** (lower case only). |

---

## 1. `safety`

Decides whether the agent may read, write and run commands, and when it must ask first. This section lists the keys only. For the decision matrix and approval flow, see [SAFETY.md](SAFETY.md).

| Key | Type | Default | Allowed values | Description |
|---|---|---|---|---|
| `approvalPolicy` | enum `ApprovalPolicy` | `"on-request"` | `never`, `on-request` | `on-request` asks the approval handler. `never` never asks: operations that would ask are auto-approved or rejected, depending on the matrix. |
| `sandboxMode` | enum `SandboxMode` | `"workspace-write"` | `read-only`, `workspace-write`, `danger-full-access` | Base policy for file writes and commands. |
| `execDefault` | enum `ExecEffect` or `null` | `null` | `allow`, `ask`, `deny`, `null` | Effect for a command that matches no `execRules` entry. `null` falls back to the sandbox matrix. |
| `execRules` | array of `{ prefix, effect }` | `[]` | — | Command-prefix rules (see below). |
| `writableRoots` | array of string | `[]` (= the session work directory) | absolute paths, or paths relative to the session work directory | Directories that file tools may write to. |
| `execArgScope` | object or `null` | `null` (= enabled with the default sensitive roots) | — | Argument-scope checks for commands (see below). |

**`execRules[]`**

| Key | Type | Required | Description |
|---|---|---|---|
| `prefix` | array of string | yes | Command prefix tokens, e.g. `["git", "pull"]`. |
| `effect` | enum `ExecEffect` | yes | `allow`, `ask` or `deny`. |

**`execArgScope`**

| Key | Type | Default | Description |
|---|---|---|---|
| `enabled` | bool or `null` | `null` (= enabled) | `false` turns argument-scope checks off. |
| `sensitiveReadRoots` | array of string or `null` | `null` (= `ArgScope.DefaultSensitiveReadRoots`) | Paths that command arguments must not read. Each entry is absolute or a `$HOME/...` template. The default list covers credential and privilege-escalation material such as `/etc/shadow`, `/etc/sudoers`, `/root`, `$HOME/.ssh` and `$HOME/.aws`. |

```json
"safety": {
  "approvalPolicy": "on-request",
  "sandboxMode": "workspace-write",
  "execDefault": null,
  "execRules": [ { "prefix": ["rm"], "effect": "ask" }, { "prefix": ["rm", "-rf"], "effect": "deny" } ],
  "writableRoots": ["/srv/agent-scratch"],
  "execArgScope": { "enabled": true }
}
```

---

## 2. `contextCompression`

Controls when the conversation is summarized to stay inside the model's context window. This section lists the keys only. For the backstop, the `compress_context` tool and how the summary is built, see [SESSIONS.md](SESSIONS.md).

| Key | Type | Default | Allowed values / validation | Description |
|---|---|---|---|---|
| `enabled` | bool | `true` | — | `false` disables compaction (both the backstop and the tool request are no-ops). |
| `mode` | string | `"autonomous"` | `autonomous`, `handoff`, `backstop` (case-sensitive) | `autonomous` and `handoff` behave the same: the backstop applies, and the model also gets the `compress_context` tool and usage directives. `backstop` means the backstop only: `compress_context` is not registered and its prompt section is dropped. |
| `trigger.fraction` | number | `0.7` | `0 < fraction ≤ 1` | Compaction fires when the estimated prompt tokens ÷ (`modelContextWindow` − `overheadTokens`) reach this fraction. |
| `keep.messages` | int | `6` | not validated | Number of most recent messages kept verbatim. Older messages are summarized. |
| `keep.maxTokens` | int | `20000` | not validated; `≤ 0` disables the budget | Token budget for the kept messages. When it is exceeded, the oldest kept messages' text is truncated. |
| `modelContextWindow` | int | `262144` | `> 0` | Context window used for the compaction budget. It is replaced by the primary model's `config.contextWindow` and by `CONTEXT_WINDOW` when you call `WithEnvOverrides`. |
| `overheadTokens` | int | `45000` | `≥ 0` | Tokens reserved for the system prompt, tool definitions and output. They are subtracted from the window before the fraction is applied. |
| `imageTokens` | int | `1500` | `> 0` | Token estimate of one image in the history (user attachments and tool results). The backstop, the context-usage display and the keep budget count an image as this many tokens. See [ATTACHMENTS.md](ATTACHMENTS.md). |
| `pdfPageTokens` | int | `1500` | `> 0` | Token estimate of one page of a PDF sent as a document. The page count is estimated from the file. |

The read-only computed property `ModelCompaction` is `true` unless `mode` is `backstop`.

---

## 3. `models`

The models the host can run. **`models[0]` is the primary model**: `config.PrimaryModel` returns it, and `MODEL_ID` moves another entry into that position. For backends, credentials and provider registration, see [MODELS.md](MODELS.md).

**`models[]` entry**

| Key | Type | Required | Default | Description |
|---|---|---|---|---|
| `id` | string | yes | — | Your name for the entry. `MODEL_ID` matches it exactly (case-sensitive). Uniqueness is not validated; `MODEL_ID` selects the first match. |
| `type` | enum `ModelType` | no | `"LlamaCpp"` | `LlamaCpp`, `OpenAICompatible`, `Anthropic`, `OpenAI`, `AzureOpenAI`, `Bedrock`, `Gemini`. |
| `config` | object `ModelSettings` | no | all defaults | One flat settings object shared by every type. See the next table. |

**`models[].config` (`ModelSettings`)**

| Key | Type | Default | Used by | Description |
|---|---|---|---|---|
| `model` | string | `null` | all | `LlamaCpp`: path to the GGUF file. `AzureOpenAI`: deployment name. `Bedrock`: model ID or inference profile ID. Others: model ID. Required for every type except `OpenAICompatible`. |
| `serverUrl` | string | `null` | `OpenAICompatible` (required), `AzureOpenAI`, `OpenAI`, `Bedrock`, `Gemini` | Endpoint. `AzureOpenAI` falls back to `AZURE_OPENAI_ENDPOINT`. For `OpenAI`, `Bedrock` and `Gemini` it is an optional override (proxy or VPC endpoint). `Anthropic` ignores it. |
| `region` | string | `null` | `Bedrock` | AWS region. Falls back to `AWS_REGION`, then `AWS_DEFAULT_REGION`. |
| `contextSize` | int | `4096` | `LlamaCpp` | llama.cpp context length in tokens (KV-cache size). |
| `gpuLayers` | int | `999` | `LlamaCpp` | Number of layers offloaded to the GPU. `999` offloads all layers. |
| `mainGpu` | int | `0` | `LlamaCpp` | Index of the main GPU. |
| `seqMax` | int | `1` | `LlamaCpp` | Conversations the in-process context holds at once (llama.cpp `n_seq_max`, 1–256). `1` = one: sessions sharing the model take turns, and each switch processes the next conversation's whole prompt again. Greater than 1: each slot of `subAgentPolicy.slots` becomes its own sequence with its own KV cache, so the parent resumes after a sub-agent call with only the new tokens and sub-agents run in parallel. The slots in use must be below `seqMax` (default slots 1–4 → `5`). Memory: one recurrent state per sequence, allocated when the model loads (about 150 MiB per sequence for a 27B hybrid model). See [MULTI-AGENT.md](MULTI-AGENT.md#models-and-parallelism). |
| `kvUnified` | bool | `true` | `LlamaCpp` with `seqMax` > 1 | `true`: the sequences share the `contextSize` KV cells — one conversation may use all of them, and when a new decode does not fit, idle conversations are evicted least recently used first (their next call processes the whole prompt again). `false`: each sequence gets `contextSize / seqMax` cells. **Speed:** with a shared KV every model step works across the cells of all sequences, so conversations running at the same time slow each other down — measured with a 27B hybrid model on a GPU (three workflow participants, `contextSize` 49152): shared 45.8 s, separate (`false`) 32.3 s, one at a time (`seqMax` 1) 37.0 s. Prefer `false` when agents run in parallel and `contextSize / seqMax` is enough for one conversation. |
| `mmproj` | string or `null` | `null` | `LlamaCpp` | Path to the model's image projector GGUF (for example `mmproj-F16.gguf` from the model's distribution). When set, the in-process model takes images: `inputModalities` defaults to text + image, `view_image` is offered, and images in user messages and tool results are shown to the model. The projector loads with the model, on the GPU when `gpuLayers` > 0 (about +1.1 GiB VRAM for a 27B model's F16 projector). See [MODELS.md](MODELS.md#images-and-pdfs). |
| `imageMinTokens` | int | `1024` | `LlamaCpp` with `mmproj` | Fewest tokens per image: smaller images are scaled up. With fewer (the projector default is about 256 for a 512-pixel image) a 27B model misread digits. |
| `imageMaxTokens` | int | `1500` | `LlamaCpp` with `mmproj` | Most tokens per image: larger images are scaled down, keeping the aspect ratio. Keep it at `contextCompression.imageTokens`, the per-image estimate compaction counts. |
| `mtp` | bool? | unset = on when the model has an MTP head | `LlamaCpp` | MTP speculative decoding: the model's own MTP head drafts the next token, and the model checks the draft in the same step. Every output token is still the one the model samples (a near tie between two tokens can occasionally resolve differently on a GPU) (measured with Qwen3.8-27B on a GPU: 1.59–1.64× the tokens per second on short prompts — real work depends on how often the draft is right). Unset (the default): on when the model has an MTP head (Qwen3.8-27B has one), off otherwise. `true`: on — a model without an MTP head is refused when it loads. `false`: off. Works with `seqMax` > 1: the sequences' drafts and checks share decodes. Costs: a draft context (+0.6 GiB VRAM at `contextSize` 16384, +2.6 GiB at 262144), a second recurrent state for rolling back a wrong draft (+150 MiB for a 27B model), no extra prompt time (prompts are decoded exactly as without MTP). See [MODELS.md](MODELS.md#mtp-speculative-decoding). |
| `temperature` | number | `0.2` | `LlamaCpp` | Sampling temperature. The remote adapters do not send it, and the Anthropic adapter strips sampling parameters. |
| `maxTokens` | int | `32768` | all | Output-token cap per model call. `Anthropic` caps it at 16000. |
| `effort` | string or `null` | `null` (model default) | `Anthropic`, `OpenAI`, `AzureOpenAI`, `OpenAICompatible`, `Gemini` | Reasoning effort: `low`, `medium`, `high` or `xhigh` (case-sensitive). Rejected for `Bedrock`. Accepted but ignored for `LlamaCpp`. |
| `contextWindow` | int or `null` | `null` | all | Per-model context window for compaction. When this model is primary, `WithEnvOverrides` copies it into `contextCompression.modelContextWindow`. |
| `timeoutSeconds` | int or `null` | `null` | remote types | Per-request HTTP timeout. When `null`: 1800 s (`ModelSettings.DefaultTimeoutSeconds`) for `OpenAI`, `AzureOpenAI`, `OpenAICompatible`, `Bedrock` and `Gemini`; the SDK default for `Anthropic`. |
| `maxRetries` | int or `null` | `null` (vendor SDK default) | remote types | Retries after a failed attempt. |
| `inputModalities` | string array or `null` | `null` (provider default) | all | What the model takes as input: `text`, `image`, `pdf` (`text` is always added). When `null`: `Anthropic`, `OpenAI`, `AzureOpenAI`, `Bedrock` and `Gemini` take text, images and PDFs; `OpenAICompatible` takes text only; `LlamaCpp` takes text, plus images when `mmproj` is set. Set `["text", "image"]` for an OpenAI-compatible server that has a vision projector (llama-server `--mmproj`). Decides whether `view_image` is offered and what happens to attachments; see [ATTACHMENTS.md](ATTACHMENTS.md). |

API keys are **never** read from this file. Each provider reads them from environment variables; see [MODELS.md](MODELS.md#remote-providers).

---

## 4. `skills`

| Key | Type | Default | Description |
|---|---|---|---|
| `enabled` | bool | `true` | `false` gives the model no skills index (`AgentSessionBuilder.LoadSkillsIndex` returns an empty string). |
| `skillsPath` | string | `"skills"` | Skills root directory. A relative path is resolved against the **process current directory**, not the config file. `AgentSessionBuilder.UseSkillsDirectory` overrides it. |

For the skill format, see [SKILLS-GUIDE.md](SKILLS-GUIDE.md).

---

## 5. `verify`

Completion verification. When the model gives a final answer, the gate checks that the answer has visible text. It then runs a test command detected in the work directory: `dotnet test` when a `*.sln` or `*.csproj` file is present, `npm test --silent` when `package.json` has a test script, or `node --test` for `*.test.js` / `*.test.ts` files. If no command is detected, the check is skipped. Details: [SESSIONS.md](SESSIONS.md).

| Key | Type | Default | Description |
|---|---|---|---|
| `enabled` | bool | `true` | Turns the gate on or off. The gate runs only while `guard.enabled` is `true`. |
| `maxAttempts` | int | `2` | Verification runs per turn. On the last failing run, the turn ends instead of re-prompting the model. Not validated. |
| `timeoutMs` | int | `60000` | Timeout for each verification command run. Not validated. |

---

## 6. `guard`

Loop guards at the model-call boundary. The repeat breakers are not configurable: their thresholds are fixed in code (nudge at 3 repeats, abort at 5). The keys below configure the other guards. Behaviour: [SESSIONS.md](SESSIONS.md).

| Key | Type | Default | Validation | Description |
|---|---|---|---|---|
| `enabled` | bool | `true` | — | Turns all guards on or off. `false` also bypasses the [`verify`](#5-verify) gate, which runs inside the guard layer. |
| `maxToolArgsKb` | int | `64` | `> 0` | Maximum size of one tool call's serialized arguments, in KB. |
| `maxToolCalls` | int | `0` | `≥ 0` | Maximum **total** tool calls per session. `0` means no limit. |
| `artifact` | string or `null` | `null` | — | Completion guard. If this file (relative to the work directory, or absolute) is missing or empty when the model gives a final answer, the model is re-prompted once. `null` or empty turns the guard off. |

---

## 7. `mcpServers`

MCP servers reached over the stdio transport. Each server's tools are exposed to the model as `mcp_<name>_<tool>`. A server that fails to start is skipped with a warning. See [TOOLS.md](TOOLS.md).

| Key | Type | Required | Default | Description |
|---|---|---|---|---|
| `name` | string | yes | — | Must match `^[A-Za-z0-9._-]+$`. Names must be unique (case-insensitive). |
| `command` | string | yes | — | Executable to launch. Must not be blank. |
| `args` | array of string | no | `[]` | Command-line arguments. |
| `env` | object (string → string) | no | `{}` | Extra environment variables for the server process. Values may contain `${VAR}` references (see below). |
| `enabled` | bool | no | `true` | `false` keeps the entry in the file but does not start the server. |

```json
"mcpServers": [
  { "name": "files", "command": "python3", "args": ["mcp_files.py"], "env": { "API_TOKEN": "${FILES_API_TOKEN}" } },
  { "name": "spare", "command": "node", "args": ["server.js"], "enabled": false }
]
```

**`${VAR}` expansion.** References are expanded **only in `mcpServers[].env` values**, and only by `WithEnvOverrides`. `command` and `args` are never expanded. Each `${NAME}` is replaced with that variable's value from the dictionary you pass in. A reference to a variable that is not in the dictionary is replaced with an empty string, and no error or warning is produced. An unterminated `${` is left as it is. If you never call `WithEnvOverrides`, the literal `${...}` text is passed to the server.

---

## 8. `subAgents`

Sub-agents of the top-level session — the config form of `AgentSessionBuilder.AddSubAgent` (see [API.md](API.md#sub-agents)). Each enabled entry becomes the tool `agent_<name>`; the model passes a `query` and gets the sub-agent's final answer. The list applies to the session built from this config only: sub-agents start from the same config but do not get these entries again (config sub-agents do not nest — use the API for nesting). An `AddSubAgent` call with the same name wins over the entry.

| Key | Type | Required | Default | Description |
|---|---|---|---|---|
| `name` | string | yes | — | `^[a-z0-9][a-z0-9_-]{0,39}$`, unique. The tool is `agent_<name>`. |
| `description` | string | yes | — | The tool description the model sees: when to use this sub-agent and what to give it. |
| `modelId` | string | no | the parent's model | A `models[].id`. That entry becomes the sub-agent's `models[0]` (its `contextWindow`, if set, becomes the compression window). The client is created on the first call and released with the parent session; the parent's own model id reuses the parent's client. A remote model is announced on the logger (requirement R4). |
| `promptsDirectory` | string | no | the parent's | Prompts directory (`base-prompt.md` + `permissions/`). |
| `skills` | object | no | the parent's | A [skills](#4-skills) section for the sub-agent. |
| `tools.allow` | array of string | no | all built-in tools | Only these built-in tools are kept. |
| `tools.deny` | array of string | no | `[]` | Built-in tools to leave out (after `allow`). |
| `sandboxMode` | string | no | the parent's | May only **narrow** the parent's sandbox mode (`read-only` < `workspace-write` < `danger-full-access`); a looser value is ignored with a warning. Every other safety setting is the parent's. |
| `persistConversation` | bool | no | `true` | `true`: one sub-agent session for the parent's lifetime (a second call continues its conversation). `false`: a new session per call. |
| `timeoutSeconds` | int | no | none | Upper bound of one call; exceeding it returns an `{"ok":false,"error":"timeout",...}` result to the model. |
| `enabled` | bool | no | `true` | `false` keeps the entry but does not offer the tool. |

Tool names in `tools.allow` / `tools.deny` must be built-in tools (`ToolRegistry.BuiltInToolNames`: `execute_code`, `bash_command`, `read_file`, `write_file`, `edit_file`, `apply_patch`, `glob_files`, `grep_search`, `write_todos`, `read_todos`, `load_skill`, `compress_context`). MCP tools follow `mcpServers`.

```json
"subAgents": [
  {
    "name": "reviewer",
    "description": "Reviews a change and lists concrete problems. Give it the file paths and what to check.",
    "modelId": "small-local",
    "tools": { "allow": ["read_file", "grep_search", "glob_files"] },
    "sandboxMode": "read-only",
    "timeoutSeconds": 300
  }
]
```

A sub-agent works in the parent's work directory, its approval requests go to the host's approval handler with `SafetyOperation.AgentPath` set, and its rollout is under `<session dir>/agents/<name>/<child session id>/` ([SESSIONS.md](SESSIONS.md)).

---

## 9. `subAgentPolicy`

Limits for every sub-agent of the session tree, and the dynamic `spawn_agent` tool. `AgentSessionBuilder.UseSubAgentPolicy` replaces this section.

| Key | Type | Default | Description |
|---|---|---|---|
| `maxDepth` | int | `2` | Deepest nesting level that still gets sub-agent tools (1 = sub-agents of the top-level session). `0` = no sub-agent tools at all. |
| `maxResultChars` | int | `20000` | A sub-agent's answer is truncated to this many characters before the parent sees it. |
| `allowSpawn` | bool | `false` | Offer the tool `spawn_agent` (arguments `instructions`, `query`): the model creates a one-shot sub-agent with instructions it writes itself. The spawned sub-agent uses the session's own model, prompts, skills and safety, gets no registered sub-agents, and follows `maxDepth` (it can spawn again while below it). |
| `spawnTimeoutSeconds` | int | none | Upper bound of one `spawn_agent` call. |
| `maxConcurrent` | int | `3` | Sub-agents of the top-level session that may run at the same time. When the model answers with several `agent_*` / `spawn_agent` calls, up to this many run in parallel and the rest wait; the session's other tools still run one at a time. Deeper sub-agents run in their caller's place. `1` = one at a time. |
| `maxFanOut` | int | `4` | Most participants of one concurrent workflow of the `Kudos.Agent.Sdk.Workflows` package (`AgentWorkflows.Concurrent`); building a larger one fails. The package reads it when you pass `SubAgentPolicy.FromConfig(config.SubAgentPolicy).MaxFanOut` as `AgentWorkflowOptions.MaxFanOut`. |
| `slots.enabled` | bool | `true` | Pin slots (`id_slot`) — when `models[0]` is `OpenAICompatible` (llama-server slots) or `LlamaCpp` with `seqMax` > 1 (in-process sequences). |
| `slots.parent` | int or null | `1` | Slot of the top-level session (`null` = not pinned). Slot 0 is left for other clients of the server. |
| `slots.subAgents` | array of int | the `maxConcurrent` slots after `parent` (`[2, 3, 4]`) | Slots handed to running sub-agents, at least `maxConcurrent` entries. A sub-agent called again prefers the slot it used last (its KV cache is still there). |

```json
"subAgentPolicy": {
  "maxDepth": 2, "maxResultChars": 20000, "allowSpawn": true, "spawnTimeoutSeconds": 600,
  "maxConcurrent": 3, "maxFanOut": 4,
  "slots": { "enabled": true, "parent": 1, "subAgents": [2, 3, 4] }
}
```

**llama-server slots.** With an `OpenAICompatible` model and at least one sub-agent tool, every request of the top-level session carries `id_slot = slots.parent`, and each running sub-agent gets one of `slots.subAgents` for the duration of its call (nested sub-agents use their caller's slot). The server keeps each agent's KV cache in its own slot and runs them in parallel; requests on the same slot are queued by the server. Start the server with enough slots: `--parallel` must be greater than the largest configured slot number (`--parallel 5` for the defaults). At build time the SDK reads `GET /props` (`total_slots`) and logs a warning when a configured slot does not exist. With `--kv-unified`, all slots share one KV pool of `--ctx-size` tokens: the contexts of the parent and of the sub-agents running at the same time must fit into it together, otherwise the server evicts other slots' caches (they are re-processed on their next request). Other remote backends ignore the slot settings.

**In-process sequences.** With a `LlamaCpp` model and `seqMax` > 1 the slots mean sequences of the one in-process context: the top-level session uses `slots.parent` (also without sub-agents), each running sub-agent one of `slots.subAgents`, and each sequence keeps its own KV cache. Requests on different slots run in parallel (their generation steps are decoded together in one batch); requests on the same slot wait for each other. A slot at or above `seqMax` is a configuration error. With `seqMax` 1 (the default) the in-process model runs one request at a time, and every switch between the parent's and a sub-agent's conversation rebuilds the KV cache ([KV-CACHE.md](KV-CACHE.md)); a smaller or remote model for sub-agents, or `seqMax` > 1, avoids that.

---

## 10. `a2a`

A2A (Agent2Agent protocol v1.0): remote agents the model can call, and the server that exposes this agent. Off by default — nothing is sent or served unless this section says so. Read by the `Kudos.Agent.Sdk.A2A` project (not by the core); see [A2A.md](A2A.md).

**`a2a.agents[]`** — each enabled entry becomes the tool `a2a_<name>`.

| Key | Type | Default | Description |
|---|---|---|---|
| `name` | string | **required** | `^[A-Za-z0-9._-]+$`, unique (case-insensitive). The tool is `a2a_<name>`. |
| `url` | string | **required** | Absolute `http(s)` base URL; the card is read from `<url>/.well-known/agent-card.json`. |
| `binding` | string | `auto` | `auto` (the card's preferred interface) \| `httpJson` \| `jsonRpc`. |
| `headers` | object | `{}` | HTTP headers sent with every request; values may use `${VAR}`. |
| `timeoutSeconds` | int | `600` | Upper bound of one call (the remote task is cancelled when it runs out). |
| `enabled` | bool | `true` | `false` keeps the entry without offering the tool. |

**`a2a.server`** — used by `Host.Cli serve-a2a` / `A2AServerHost`.

| Key | Type | Default | Description |
|---|---|---|---|
| `enabled` | bool | `false` | Must be `true` for the server to start. |
| `listen` | string | `http://127.0.0.1:8765` | Listen URL. A non-loopback address requires `token`. Port `0` picks a free port. |
| `path` | string | `/a2a/agent` | HTTP+JSON endpoint; JSON-RPC is served at `<path>-rpc`. |
| `bindings` | array | `["httpJson", "jsonRpc"]` | Which endpoints to serve (non-empty subset). |
| `token` | string | none | Bearer token every request must carry (the agent card stays public). May use `${VAR}`. |
| `approval` | string | `local` | Who approves operations that need approval: `local` (the server's own user), `requester` (the calling agent, through the A2A `input-required` state), `reject` (all denied). |
| `approvalTimeoutMinutes` | int | `30` | An unanswered approval is denied after this time. |
| `maxConcurrentTasks` | int | `1` | Turns that run at the same time; more requests wait as `submitted`. |
| `maxCallDepth` | int | `2` | A request that already went through more A2A hops is rejected. |
| `card.name` / `card.description` | string | `kudos-agent` / a short description | What the agent card says. |
| `card.url` | string | the listen address | Base URL written into the card — set it when listening on `0.0.0.0`. |

```json
"a2a": {
  "agents": [
    { "name": "estimator", "url": "http://127.0.0.1:8770", "headers": { "Authorization": "Bearer ${ESTIMATOR_TOKEN}" } }
  ],
  "server": { "enabled": true, "listen": "http://127.0.0.1:8765", "approval": "local", "token": "${A2A_SERVER_TOKEN}" }
}
```

The server also refuses to start unless `safety.approvalPolicy` is `on-request` (`A2AServerHost.StartupProblem`).

---

## 11. `attachments`

Limits for images and PDFs attached to a message or returned by a tool. They are checked before anything is saved or sent. See [ATTACHMENTS.md](ATTACHMENTS.md).

| Key | Type | Default | Validation | Description |
|---|---|---|---|---|
| `maxFileMb` | int | `20` | `> 0` | Largest attachment in megabytes (1 MB = 1,048,576 bytes). The computed property `MaxFileBytes` gives the value in bytes. |
| `maxPerMessage` | int | `8` | `> 0` | Most attachments in one user message. |

---

## Validation Rules

`Validate()` throws `InvalidOperationException` for each of the following. It runs inside `FromJson` / `Load` and again at the end of `WithEnvOverrides`.

| Rule | Message (abridged) |
|---|---|
| `contextCompression.trigger.fraction` outside `(0, 1]` | `contextCompression.trigger.fraction must be in (0, 1]` |
| `contextCompression.mode` not exactly `autonomous`, `handoff` or `backstop` | `contextCompression.mode must be autonomous \| handoff \| backstop` |
| `contextCompression.modelContextWindow ≤ 0` | `... must be > 0` |
| `contextCompression.overheadTokens < 0` | `... must be >= 0` |
| `contextCompression.imageTokens ≤ 0` or `pdfPageTokens ≤ 0` | `contextCompression.imageTokens and pdfPageTokens must be > 0` |
| `attachments.maxFileMb ≤ 0` or `maxPerMessage ≤ 0` | `attachments.maxFileMb and maxPerMessage must be > 0` |
| `guard.maxToolArgsKb ≤ 0` | `guard.maxToolArgsKb must be > 0` |
| `guard.maxToolCalls < 0` | `guard.maxToolCalls must be >= 0` |
| `models` empty or missing | `models must contain at least one entry` |
| `mcpServers[].name` not matching `^[A-Za-z0-9._-]+$` | `mcpServers[<name>]: name must match [A-Za-z0-9._-]+` |
| `mcpServers[].command` blank | `mcpServers[<name>]: command is required` |
| Duplicate `mcpServers[].name` (case-insensitive) | `mcpServers: duplicate name '<name>'` |
| `LlamaCpp` without `config.model` | `models[<id>].config.model is required for LlamaCpp` |
| `Anthropic`, `OpenAI`, `AzureOpenAI`, `Bedrock` or `Gemini` without `config.model` | `models[<id>].config.model ... is required for <Type>` |
| `OpenAICompatible` without `config.serverUrl` | `models[<id>].config.serverUrl is required for OpenAICompatible` |
| `config.timeoutSeconds ≤ 0` (when set) | `models[<id>].config.timeoutSeconds must be > 0` |
| `config.maxRetries < 0` (when set) | `models[<id>].config.maxRetries must be >= 0` |
| `config.effort` set on a `Bedrock` model | `models[<id>].config.effort is not supported for Bedrock ...` |
| `config.effort` not one of `low`, `medium`, `high`, `xhigh` | `models[<id>].config.effort must be one of low \| medium \| high \| xhigh` |
| `config.contextWindow ≤ 0` (when set) | `models[<id>].config.contextWindow must be > 0` |
| `config.inputModalities` with a value other than `text`, `image`, `pdf` | `models[<id>].config.inputModalities: unknown '<value>' (known: text, image, pdf)` |
| `config.mmproj` on a type other than `LlamaCpp` | `models[<id>].config.mmproj is only for LlamaCpp (in-process) — for an OpenAI-compatible server start it with --mmproj and set inputModalities` |
| `LlamaCpp` with `"image"` in `inputModalities` but no `mmproj` | `models[<id>].config.inputModalities has "image" but no mmproj — the in-process model takes images only with its projector (set config.mmproj)` |
| `LlamaCpp` with `mmproj`: `imageMinTokens < 1` or `imageMaxTokens < imageMinTokens` | `models[<id>].config: imageMinTokens (<n>) must be ≥ 1 and ≤ imageMaxTokens (<m>)` |
| `subAgents[].name` not matching `^[a-z0-9][a-z0-9_-]{0,39}$` | `subAgents[<name>]: name must match ...` |
| Duplicate `subAgents[].name` | `subAgents: duplicate name '<name>'` |
| `subAgents[].description` blank | `subAgents[<name>]: description is required ...` |
| `subAgents[].modelId` not a `models[].id` | `subAgents[<name>]: modelId '<id>' matches no models[] entry ...` |
| `subAgents[].timeoutSeconds ≤ 0` (when set) | `subAgents[<name>]: timeoutSeconds must be > 0` |
| `subAgents[].tools` naming an `agent_*` tool | `subAgents[<name>]: tools may not name sub-agent tools ... config sub-agents do not nest ...` |
| `subAgents[].tools` naming an unknown tool | `subAgents[<name>]: unknown built-in tool '<tool>' ...` |
| `subAgentPolicy.maxDepth < 0` | `subAgentPolicy.maxDepth must be >= 0` |
| `subAgentPolicy.maxResultChars ≤ 0` | `subAgentPolicy.maxResultChars must be > 0` |
| `subAgentPolicy.spawnTimeoutSeconds ≤ 0` (when set) | `subAgentPolicy.spawnTimeoutSeconds must be > 0` |
| `subAgentPolicy.maxConcurrent < 1` | `subAgentPolicy: maxConcurrent must be >= 1` |
| `subAgentPolicy.maxFanOut < 1` | `subAgentPolicy: maxFanOut must be >= 1` |
| `subAgentPolicy.slots.parent < 0` | `subAgentPolicy: slots.parent must be >= 0` |
| `subAgentPolicy.slots.subAgents` with a negative or repeated slot, or the parent's slot | `subAgentPolicy: slots.subAgents must be >= 0` / `must not repeat a slot` / `must not contain the parent's slot <n>` |
| `subAgentPolicy.slots.subAgents` shorter than `maxConcurrent` | `subAgentPolicy: slots.subAgents needs at least maxConcurrent (<n>) entries` |
| `models[].config.seqMax` outside 1–256 | `models[<id>].config.seqMax must be 1–256: <n>` |
| `config.mtp: true` on a type other than `LlamaCpp` (unset and `false` are accepted) | `models[<id>].config.mtp is only for LlamaCpp (in-process) — for llama-server use its --spec-type draft-mtp` |
| `LlamaCpp` with `seqMax` > 1 and a slot (`subAgentPolicy.slots`, when enabled) ≥ `seqMax` | `models[<id>].config.seqMax <n> has no sequence for slot <s> of subAgentPolicy.slots — set seqMax to at least <s+1> or change the slots` |
| `a2a.agents[].name` not matching `^[A-Za-z0-9._-]+$`, or a duplicate | `a2a.agents[<name>]: name must match ...` / `a2a.agents: duplicate name '<name>'` |
| `a2a.agents[].url` not an absolute http(s) URL | `a2a.agents[<name>]: url must be an absolute http(s) URL` |
| `a2a.agents[].binding` not `auto`, `httpJson` or `jsonRpc` | `a2a.agents[<name>]: binding must be auto \| httpJson \| jsonRpc` |
| `a2a.agents[].timeoutSeconds ≤ 0` | `a2a.agents[<name>]: timeoutSeconds must be > 0` |
| `a2a.server.listen` not an absolute http(s) URL / `path` not starting with `/` / `bindings` empty or unknown | `a2a.server.listen must be ...` / `a2a.server.path must start with '/'` / `a2a.server.bindings must be ...` |
| `a2a.server.approval` not `local`, `requester` or `reject` | `a2a.server.approval must be local \| requester \| reject` |
| `a2a.server.approvalTimeoutMinutes ≤ 0` / `maxConcurrentTasks < 1` / `maxCallDepth < 1` | `a2a.server.<key> must be ...` |
| `a2a.server.enabled` with a non-loopback `listen` and no `token` (checked again after `${VAR}` expansion) | `a2a.server.token is required when listening on a non-loopback address` |

`Validate()` does **not** check: `keep.*`, `verify.*`, `contextSize`, `gpuLayers`, `mainGpu`, `temperature`, `maxTokens`, duplicate model IDs, or whether a GGUF file exists. A missing GGUF file is reported when the client is created (`FileNotFoundException`, see [MODELS.md](MODELS.md)). `mcpServers` entries with `enabled: false` are still validated by `Load`.

---

## Environment-Variable Overrides

`WithEnvOverrides(IReadOnlyDictionary<string, string?> env)` reads only the dictionary you pass in. `ChatClientFactory.ProcessEnvironment()` returns the process environment as a case-insensitive dictionary. A key whose value is empty or whitespace counts as unset.

| Variable | Effect | Invalid value |
|---|---|---|
| `SAFETY_APPROVAL_POLICY` | Replaces `safety.approvalPolicy`. Same spellings as the JSON enum. | **Throws** `InvalidOperationException: Invalid value for SAFETY_APPROVAL_POLICY: '<raw>'` |
| `SAFETY_SANDBOX_MODE` | Replaces `safety.sandboxMode`. | **Throws** `InvalidOperationException: Invalid value for SAFETY_SANDBOX_MODE: '<raw>'` |
| `MODEL_ID` | Moves the `models[]` entry whose `id` matches exactly (case-sensitive) to position 0, making it primary. The other entries keep their order. | **Throws** `InvalidOperationException: MODEL_ID '<id>' matches no models[] entry (ids: ...)` |
| `MODEL` | Replaces `config.model` of `models[0]`, after `MODEL_ID` is applied. | — |
| `SERVER_URL` | Replaces `config.serverUrl` of `models[0]`, after `MODEL_ID` is applied. | — |
| `CONTEXT_WINDOW` | Replaces `contextCompression.modelContextWindow`. Takes precedence over the primary model's `config.contextWindow`. | Silently **ignored** if the value is not an integer or is ≤ 0 |
| `MCP_ENABLED` | `0`, `false`, `off` or `no` (trimmed, case-insensitive) removes every MCP server. Any other value has no effect. | — |

The overrides are applied in this order:

1. `SAFETY_APPROVAL_POLICY`, `SAFETY_SANDBOX_MODE`.
2. `MODEL_ID` reorders `models`.
3. `MODEL` and `SERVER_URL` patch the new `models[0]`.
4. If `models[0].config.contextWindow` is set, it becomes `contextCompression.modelContextWindow`.
5. A valid `CONTEXT_WINDOW` overrides step 4.
6. `mcpServers`: entries with `enabled: false` are dropped, all entries are dropped if `MCP_ENABLED` is off, and `${VAR}` is expanded in the remaining `env` values.
7. `a2a`: agents with `enabled: false` are dropped, and `${VAR}` is expanded in `agents[].headers` values and in `server.token`.
8. `Validate()` runs on the result.

**Fail-closed behaviour.** An unrecognised safety value or an unknown `MODEL_ID` stops startup with an exception. The SDK never falls back to a default policy or to another model. The exceptions are `CONTEXT_WINDOW`, where an invalid value is ignored and the file's value stays, and missing `${VAR}` references, which expand to an empty string.

`${VAR}` references are expanded from the dictionary passed to `WithEnvOverrides`. Pass `SecretsLoader.ResolveProcessEnvironment(config)` (the process environment with the referenced variables filled from the OS secret store — [SECRETS.md](SECRETS.md)) so a token kept in the store reaches the MCP server or A2A agent. The names of references found in neither place are in `MissingEnvRefs` of the result (not serialized); the bundled host prints a warning for each.

`WithEnvOverrides` does not read model credentials (`ANTHROPIC_API_KEY`, `OPENAI_API_KEY`, ...). The providers read those themselves when the client is created; see [MODELS.md](MODELS.md#remote-providers). It also does not read `TASK`, `SEED_FROM` or `SESSION_ID`, which are session inputs of the bundled CLI host and not part of the SDK config.

---

## Complete Annotated Example

The example below is derived from the repository's `json/kudos-agent.json`. Comments are allowed by the loader, so this file loads as written once you replace the placeholders (`<...>`).

```jsonc
{
  "safety": {
    "approvalPolicy": "on-request",        // ask the approval handler before risky operations
    "sandboxMode": "workspace-write",      // writes inside the work directory are automatic
    "execDefault": null,                   // commands without a matching rule follow the sandbox matrix
    "execRules": []                        // e.g. { "prefix": ["rm"], "effect": "ask" }
  },
  "contextCompression": {
    "enabled": true,
    "mode": "autonomous",                  // backstop + compress_context tool
    "trigger": { "fraction": 0.7 },        // compact at 70% of (window - overhead)
    "keep": { "messages": 6, "maxTokens": 20000 },
    "modelContextWindow": 262144,          // replaced by models[0].config.contextWindow via WithEnvOverrides
    "overheadTokens": 45000
  },
  "models": [
    {
      // models[0] = the primary model. Keep a local model first so that remote use stays opt-in.
      "id": "agent",
      "type": "LlamaCpp",
      "config": {
        "model": "<path-to-model>.gguf",   // placeholder: absolute path of your GGUF file
        "contextSize": 262144,             // llama.cpp context (KV-cache size); match modelContextWindow
        "gpuLayers": 30,                   // layers offloaded to the GPU (999 = all)
        "mainGpu": 0,
        "temperature": 0.2,
        "maxTokens": 32768
      }
    },
    {
      // Remote: select with MODEL_ID=claude. Credential: ANTHROPIC_API_KEY (environment only).
      "id": "claude",
      "type": "Anthropic",
      "config": { "model": "claude-sonnet-5-5", "maxTokens": 16000, "effort": "high", "contextWindow": 1000000 }
    },
    {
      // Remote: OPENAI_API_KEY
      "id": "openai",
      "type": "OpenAI",
      "config": { "model": "gpt-6-astra", "maxTokens": 16000, "effort": "high", "contextWindow": 1000000 }
    },
    {
      // Remote: AZURE_OPENAI_API_KEY + AZURE_OPENAI_ENDPOINT (or "serverUrl"); "model" = deployment name
      "id": "azure-openai",
      "type": "AzureOpenAI",
      "config": { "model": "gpt-6-astra", "maxTokens": 16000, "effort": "high", "contextWindow": 1000000 }
    },
    {
      // Remote: AWS credential chain; "effort" is not allowed for Bedrock
      "id": "bedrock",
      "type": "Bedrock",
      "config": { "model": "global.anthropic.claude-sonnet-5-5", "region": "us-east-1", "maxTokens": 16000, "contextWindow": 1000000 }
    },
    {
      // An OpenAI-compatible server on your network (llama-server, vLLM, Ollama, ...)
      "id": "lan-qwen",
      "type": "OpenAICompatible",
      "config": {
        "model": "unsloth/Qwen3.8",
        "serverUrl": "http://<your-server>:8001/v1/",   // placeholder
        "maxTokens": 16384,
        "contextWindow": 262144,
        "inputModalities": ["text", "image"]   // only when the server runs with --mmproj (see MODELS.md, "Images and PDFs")
      }
    },
    {
      // Remote: GEMINI_API_KEY or GOOGLE_API_KEY
      "id": "gemini",
      "type": "Gemini",
      "config": { "model": "gemini-3.6-flash", "maxTokens": 16000, "effort": "high", "contextWindow": 1000000 }
    }
  ],
  "skills": { "enabled": true, "skillsPath": "skills" },   // relative to the process current directory
  "verify": { "enabled": true, "maxAttempts": 2, "timeoutMs": 60000 },
  "guard": {
    "enabled": true,
    "maxToolArgsKb": 64,
    "maxToolCalls": 0,                     // 0 = no session-wide tool-call limit
    "artifact": null                       // e.g. "report.md" to require that file before a final answer
  }
}
```

### Minimal configuration

Only `models` is required. Everything else takes its default value.

```json
{ "models": [ { "id": "agent", "type": "LlamaCpp", "config": { "model": "<path-to-model>.gguf" } } ] }
```

---

## See Also

- [MODELS.md](MODELS.md) — model backends, credentials, provider registration
- [SAFETY.md](SAFETY.md) — the safety matrix, exec rules, approvals
- [SESSIONS.md](SESSIONS.md) — compaction, loop guards, verification, session records
- [ATTACHMENTS.md](ATTACHMENTS.md) — images and PDFs, `view_image`, input modalities
- [SKILLS-GUIDE.md](SKILLS-GUIDE.md) — skills discovery and the skills index
- [TOOLS.md](TOOLS.md) — built-in tools, MCP servers
- [API.md](API.md) — loading the config from code
- [QUICK-START.md](QUICK-START.md) — a minimal config
- [TROUBLESHOOTING.md](TROUBLESHOOTING.md) — validation errors
