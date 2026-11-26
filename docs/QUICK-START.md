# Quick Start

## Contents

1. [Installation](#installation)
2. [Minimal Example](#minimal-example)
3. [Credentials for Remote Models](#credentials-for-remote-models)

---

## Installation

The SDK ships as four NuGet packages (version `0.6.0`, target `net10.0`):

| Package | Contents | When you need it |
|---|---|---|
| `Kudos.Agent.Sdk` | Agent loop (Microsoft Agent Framework), built-in tools, safety gate and approvals, skills, MCP client, context compaction, loop guards, session records | Always |
| `Kudos.Agent.Sdk.LlamaSharp` | In-process local models (LLamaSharp / llama.cpp GGUF) — model type `LlamaCpp` | Local models |
| `Kudos.Agent.Sdk.Providers` | Remote APIs — model types `Anthropic`, `OpenAI`, `AzureOpenAI`, `OpenAICompatible`, `Bedrock`, `Gemini` | Remote models |
| `Kudos.Agent.Sdk.Workflows` | Multi-agent workflows on Microsoft Agent Framework Workflows — sessions in parallel (fan-out / fan-in) or in sequence | Several agents in one run |

The core package contains **no model client**. Add one or both provider packages, or pass any `Microsoft.Extensions.AI` `IChatClient` of your own.

The packages are **not on nuget.org**. They are provided to evaluation and commercial customers on request (see [Getting the Packages](../README.md#getting-the-packages)). Put the files you received in a local folder feed and reference them from there.

**1. Copy them to a local feed** (any folder works):

```bash
mkdir -p ~/.nuget/local-feeds/kudos-agent-sdk
cp Kudos.Agent.Sdk*.0.6.0.nupkg ~/.nuget/local-feeds/kudos-agent-sdk/
```

**2. Register the feed** — either globally:

```bash
dotnet nuget add source ~/.nuget/local-feeds/kudos-agent-sdk --name kudos-local
```

or per solution, with a `nuget.config` next to your `.sln`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <add key="kudos-local" value="/home/you/.nuget/local-feeds/kudos-agent-sdk" />
  </packageSources>
</configuration>
```

**3. Reference the packages** from your application:

```bash
dotnet add package Kudos.Agent.Sdk --version 0.6.0
dotnet add package Kudos.Agent.Sdk.Providers --version 0.6.0     # remote models
dotnet add package Kudos.Agent.Sdk.LlamaSharp --version 0.6.0    # local models
dotnet add package Kudos.Agent.Sdk.Workflows --version 0.6.0     # multi-agent workflows (optional)
dotnet add package LLamaSharp.Backend.Cpu --version 0.30.0        # ...plus ONE native backend (see below)
```

### Requirements

- **.NET SDK 10** (the packages target `net10.0` only — a `net8.0` application cannot reference them).
- **Local models** (`Kudos.Agent.Sdk.LlamaSharp`): your application references exactly one LLamaSharp native backend of the same version as LLamaSharp (`0.30.0`) — `LLamaSharp.Backend.Cpu`, `LLamaSharp.Backend.Cuda12` or `LLamaSharp.Backend.Cuda13`. The SDK does not pick one for you. The model runs inside your process, so size the GGUF model and context for your application's memory.
- **Built-in tools** call external programs:
  - `bash_command` and `execute_code` (bash) run through the shell in `ToolContext.Shell` — `/bin/bash -c <command>` by default. On other platforms, replace it with `ConfigureToolContext` (see [API.md](API.md#toolcontext)).
  - `execute_code` (python) runs `python3`.
  - `glob_files` and `grep_search` run `rg` ([ripgrep](https://github.com/BurntSushi/ripgrep)).
  - Make sure these programs are on `PATH` for the process that hosts the agent, or remove the tools you cannot support (`RemoveTool`).
- **Dependencies** brought in by the packages: `Microsoft.Agents.AI` 1.28.0 and `ModelContextProtocol` 2.4.0 (core); `LLamaSharp` 0.30.0 (LlamaSharp); `Anthropic`, `AWSSDK.Extensions.Bedrock.MEAI`, `Google.GenAI`, `Microsoft.Extensions.AI.OpenAI` (Providers).

### License

Proprietary — installing or using the packages means accepting the [End User License Agreement](../LICENSE) (the packages require license acceptance).

**Every session needs a License Key** — an Evaluation key (30 days, `development` level: local models only) or a commercial key from the Licensor. Install it once per machine with `Host.Cli license set` (OS secret store), or pass it to your application with `AgentSessionBuilder.UseLicense(text)` / `KudosLicensing.UseLicense(text)`. Without one, `BuildAsync` throws `LicenseException` (`Missing`). Levels, concurrent sessions and errors: [LICENSING.md](LICENSING.md).

---

## Minimal Example

A `kudos-agent.json` with one model is enough. This one runs a local GGUF model in-process:

```json
{
  "models": [
    {
      "id": "agent",
      "type": "LlamaCpp",
      "config": { "model": "/models/Qwen3-8B-Q4_K_M.gguf", "contextSize": 32768, "gpuLayers": 999 }
    }
  ],
  "skills": { "enabled": false }
}
```

Every other section (`safety`, `contextCompression`, `verify`, `guard`, `mcpServers`, …) has defaults. See [CONFIGURATION.md](CONFIGURATION.md) for the full schema and [MODELS.md](MODELS.md) for each model type.

```csharp
using Kudos.Agent.Agents;
using Kudos.Agent.Config;
using Kudos.Agent.Llm;
using Kudos.Agent.Safety;

// Register the model providers you referenced (once per process).
LlamaSharpChatClientProvider.Register();   // Kudos.Agent.Sdk.LlamaSharp — "type": "LlamaCpp"
RemoteChatClientProvider.Register();       // Kudos.Agent.Sdk.Providers — Anthropic, OpenAI, ...

// Load kudos-agent.json and apply the environment overrides (MODEL_ID, SAFETY_*, ...).
var config = KudosAgentConfig.Load("kudos-agent.json")
    .WithEnvOverrides(ChatClientFactory.ProcessEnvironment());

// Create the model client for models[0]. The session does not dispose it.
using var client = await ChatClientFactory.CreateAsync(config.PrimaryModel);

await using var session = await new AgentSessionBuilder()
    .UseConfig(config)
    .UseChatClient(client)
    .UseResultsDirectory("results")        // results/<session-id>/{work/, rollout.jsonl, status.json}
    .UseApprovalHandler(AskUser)           // without a handler, operations that need approval are denied
    .BuildAsync();

await foreach (var evt in session.RunTurnEventsAsync(
    "Count the lines of each file in the current directory and report them as a Markdown table."))
{
    switch (evt)
    {
        case AgentTextDelta text: Console.Write(text.Text); break;
        case AgentToolCallStarted call: Console.WriteLine($"\n[tool] {call.Name}"); break;
        case AgentTurnCompleted: Console.WriteLine(); break;
        case AgentTurnFailed failed: Console.Error.WriteLine($"[error] {failed.Message}"); break;
    }
}

static Task<bool> AskUser(SafetyOperation op)
{
    Console.WriteLine($"Allow {op.Kind}: {op.Command ?? op.Path}?");
    if (!string.IsNullOrEmpty(op.Detail))
    {
        Console.WriteLine(op.Detail);      // e.g. the full generated script
    }

    Console.Write("[y/N] ");
    return Task.FromResult(Console.ReadLine()?.Trim().ToLowerInvariant() == "y");
}
```

What happens:

1. `BuildAsync` creates `results/<session-id>/work/` — the agent's working directory — assembles the system prompt (the prompts embedded in the SDK by default), connects any configured MCP servers, and records `session-start` in `rollout.jsonl`.
2. `RunTurnEventsAsync` runs one turn of the agent loop. The model plans, calls tools (here typically `execute_code` with a generated script), sees the results and answers. Generated scripts always go through your approval handler first.
3. The session keeps the conversation: call `RunTurnEventsAsync` / `RunTurnAsync` again for the next turn.

> **Note:** Without `UseApprovalHandler`, every operation that needs approval — including every `execute_code` script — is denied (fail-closed). See [SAFETY.md](SAFETY.md) for the approval policy × sandbox mode matrix.

To try the whole loop without a model, GPU or network, run the sample host with its scripted model:

```bash
dotnet run --project samples/MinimalHost -- --demo
```

See [EXAMPLES.md](EXAMPLES.md) for a walkthrough.

---

## Credentials for Remote Models

Remote model types read their credentials from **environment variables**, and a key that is not set there is read from the **OS secret store** (macOS Keychain / Linux libsecret / Windows Credential Manager) under the same name — see [SECRETS.md](SECRETS.md). Never put keys in `kudos-agent.json`; the SDK does not load `.env` files.

| Model type | Credential | Endpoint / region |
|---|---|---|
| `Anthropic` | `ANTHROPIC_API_KEY` or `ANTHROPIC_AUTH_TOKEN` (otherwise an `ant auth login` profile) | — |
| `OpenAI` | `OPENAI_API_KEY` | `serverUrl` (optional override) |
| `AzureOpenAI` | `AZURE_OPENAI_API_KEY` | `serverUrl` or `AZURE_OPENAI_ENDPOINT`; `model` = deployment name |
| `OpenAICompatible` | `OPENAI_API_KEY` (optional) | `serverUrl` (required) |
| `Bedrock` | `AWS_ACCESS_KEY_ID` / `AWS_SECRET_ACCESS_KEY` (/ `AWS_SESSION_TOKEN`), otherwise the AWS SDK default chain (profile / SSO / instance role) | `region` or `AWS_REGION` / `AWS_DEFAULT_REGION` |
| `Gemini` | `GEMINI_API_KEY` or `GOOGLE_API_KEY` | `serverUrl` (optional override) |

```bash
# Linux / macOS
export ANTHROPIC_API_KEY=sk-ant-...
```

```powershell
# Windows (PowerShell, current session)
$env:ANTHROPIC_API_KEY = "sk-ant-..."
```

Or store it once per machine in the OS secret store (any OS — the value is read from stdin, never from the command line):

```bash
printf '%s' 'sk-ant-...' | Host.Cli secrets set ANTHROPIC_API_KEY
```

Step-by-step instructions for macOS, Linux and Windows are in [SECRETS.md — Setting a key on each OS](SECRETS.md#setting-a-key-on-each-os).

> **Gemini:** a free-tier API key (no billing on its Google Cloud project) is limited to a few requests per minute and per day. On 2026-11-25 `gemini-3.6-flash` allowed 5 per minute and 20 per day, which an agent run uses up within one or two tasks. Enable billing for real use. See [MODELS.md — Google Gemini](MODELS.md#google-gemini).

Remote models send prompts and tool results (file contents, script output) off the machine. Keep them opt-in and tell the user, as the CLI host does:

```csharp
var model = config.PrimaryModel;
if (ChatClientFactory.IsRemote(model))
{
    Console.WriteLine(ChatClientFactory.RemoteNotice(model));
    if (ChatClientFactory.CredentialWarning(model, ChatClientFactory.ProcessEnvironment()) is { } warning)
    {
        Console.Error.WriteLine($"WARNING: {warning}");
    }
}

using var client = await ChatClientFactory.CreateAsync(model);
```

To choose a model at run time, list several entries under `models` and pick one by id with the `MODEL_ID` environment variable (applied by `WithEnvOverrides`, which moves that entry to `models[0]`):

```bash
MODEL_ID=claude dotnet run
```

---

## Next Steps

- [EXAMPLES.md](EXAMPLES.md) — the sample host, custom tools, approval handlers, event streams, cancellation
- [API.md](API.md) — public API reference for the consumer path
- [CONFIGURATION.md](CONFIGURATION.md) — `kudos-agent.json` schema and environment overrides
- [MODELS.md](MODELS.md) — local and remote model types
- [TOOLS.md](TOOLS.md) — built-in tools and writing your own
- [SAFETY.md](SAFETY.md) — sandbox, approvals, exec rules
- [SKILLS-GUIDE.md](SKILLS-GUIDE.md) — skills (`SKILL.md`)
- [SESSIONS.md](SESSIONS.md) — session directory, rollout, resume
- [ARCHITECTURE.md](ARCHITECTURE.md) — how the pieces fit together
- [TROUBLESHOOTING.md](TROUBLESHOOTING.md) — common problems
