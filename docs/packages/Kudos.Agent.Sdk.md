# Kudos.Agent.Sdk v0.7.0

**The core of the Kudos Agent SDK — an AI coding agent inside your .NET application**

[Features](#features) · [Installation](#installation) · [Quick Start](#quick-start) · [Documentation](#documentation) · [License](#license)

---

## Overview

**Kudos.Agent.Sdk** embeds an AI coding agent in your application. The agent plans, calls tools, observes the results, and iterates until the task is done — for example: *instruction → the agent writes and runs a script → it answers with a table of the results*.

The core package contains the agent loop (Microsoft Agent Framework), the built-in tools, the safety gate and approvals, skills (`SKILL.md`), MCP client support, context compaction, loop guards, and the session record (rollout + audit chain). It contains **no model client**: add [Kudos.Agent.Sdk.LlamaSharp](Kudos.Agent.Sdk.LlamaSharp.md) (in-process local models) and/or [Kudos.Agent.Sdk.Providers](Kudos.Agent.Sdk.Providers.md) (remote APIs), or pass any `Microsoft.Extensions.AI` `IChatClient`. For several agents in one run (fan-out / fan-in, pipelines), add [Kudos.Agent.Sdk.Workflows](Kudos.Agent.Sdk.Workflows.md).

Version 0.x: the API may still change between minor versions.

---

## Features

### The Session Builder

`AgentSessionBuilder` assembles a `CodingAgentSession`:

| Method | Purpose |
|---|---|
| `UseConfig`, `UseChatClient` | Required: the agent configuration and the model client |
| `UseResultsDirectory` / `UseSessionLayout` | Where the session's work directory and records live |
| `UseApprovalHandler` | Called for operations that need approval; without a handler they are denied (fail-closed) |
| `UseLicense` | The License Key for this builder's sessions (after the OS secret store, before `KudosLicensing.UseLicense`) |
| `AddTool` / `AddTools` / `RemoveTool` | Add your own tools (`ITool`) or replace a built-in one (remove it first) |
| `UsePromptsDirectory` | Your own system prompt files; by default the prompts embedded in the SDK are used |
| `UseSkillsDirectory` / `UseSkillsIndex` | Skills shown to the model (`AgentSessionBuilder.LoadSkillsIndex` renders an index once for a long-lived host) |
| `UseLogger` | An `ILogger` for warnings (MCP, skills, compaction); none by default |
| `ResumeFrom` | Continue a previous session from its `rollout.jsonl` |
| `AddSubAgent` | A child session the model calls as the tool `agent_<name>` (own model / prompts / tools; approvals go to your handler). Also from config (`subAgents[]`); `SubAgentPolicy.AllowSpawn` adds `spawn_agent` for one-shot sub-agents the model defines itself. `AddSubAgent(name, description, AIAgent)` registers an existing agent (for example a workflow) the same way |

### Running a Turn

- `RunTurnEventsAsync` — typed events for a UI: `AgentTextDelta`, `AgentReasoningDelta`, `AgentToolCallStarted`, `AgentToolCallCompleted`, `AgentCompacted`, `AgentGuardTriggered`, `AgentSubAgentStarted`, `AgentSubAgentCompleted`, and finally `AgentTurnCompleted`, `AgentTurnFailed` or `AgentTurnCancelled`. The consumer drives the turn, so a tool call is reported before its approval is requested.
- `RunTurnAsync` — the final `AgentResponse` only.
- `RunTurnStreamingAsync` — the raw Microsoft Agent Framework updates.

All three take a `CancellationToken`, and each has an overload that attaches images or PDFs (`session.Attachments.ImportFile(path)` → `RunTurnAsync(text, [attachment])`). Attachments are checked by size and content, saved in the work directory and recorded by reference with their SHA-256; what is sent depends on the model's input modalities (`models[].config.inputModalities`; the in-process model takes images when its projector is set — `config.mmproj`).

Between turns, `CompactAsync(instructions?)` compacts the conversation now (a `/compact` command): everything before the latest turn becomes a summary, also when automatic compaction is off. It returns the `CompactionEvent` (tokens before / after), or null when there is nothing to compact ([manual compaction](../SESSIONS.md#manual-compaction)).

### Built-In Tools

`execute_code`, `bash_command`, `read_file`, `write_file`, `edit_file`, `apply_patch`, `glob_files`, `grep_search`, `write_todos`, `read_todos`, `load_skill`, `compress_context`, `view_image` (only for a model that takes images), plus `mcp_<server>_<tool>` for each configured MCP server, `agent_<name>` for each sub-agent, and `spawn_agent` when enabled.

### API Keys

Keys come from environment variables first, then from the OS secret store under the same name (macOS Keychain / Linux libsecret / Windows Credential Manager, service `kudos-dotnet-agent`) — `Kudos.Agent.Secrets.SecretsLoader`. Session records and the audit chain are redacted (`Redactor`). Pass `SecretsLoader.ResolveProcessEnvironment(config)` to `WithEnvOverrides` so `${VAR}` refs for MCP servers / A2A agents can come from the store too. Turn the store off with `KUDOS_SECRET_STORE=off` or `SecretStores.Default = SecretStores.None`.

### License Keys

Every session checks a License Key, offline, before anything is created (`LicenseException` otherwise). The SDK uses the first key that verifies from: the OS secret store (service `kudos-dotnet-agent`, key `license` — `Host.Cli license set`), `AgentSessionBuilder.UseLicense`, `KudosLicensing.UseLicense`, or an embedded resource `kudos-agent.license` in your application. The key's level decides what a session may use — `development` (Evaluation keys: local models), `professional` (+ remote models, MCP), `enterprise` (+ A2A, image / PDF input), `unlimited` — and `maxConcurrent` limits open sessions per OS user. The SDK assemblies are checked against the hashes recorded when the packages were built; single-file publishing is not supported.

---

## Installation

```bash
dotnet add package Kudos.Agent.Sdk --version 0.7.0
dotnet add package Kudos.Agent.Sdk.LlamaSharp --version 0.7.0   # local models (+ one LLamaSharp native backend)
dotnet add package Kudos.Agent.Sdk.Providers --version 0.7.0    # remote models
```

**Requirements:** .NET SDK 10 (target `net10.0`). Every session needs a License Key (Evaluation or commercial): install it with `Host.Cli license set`, or add `.UseLicense(licenseText)` to the builder.

The packages are not published on nuget.org — see [QUICK-START.md](../QUICK-START.md) for packing them into a local feed.

---

## Quick Start

```csharp
using Kudos.Agent.Agents;
using Kudos.Agent.Config;
using Kudos.Agent.Llm;

LlamaSharpChatClientProvider.Register();          // from Kudos.Agent.Sdk.LlamaSharp
RemoteChatClientProvider.Register();              // from Kudos.Agent.Sdk.Providers

var config = KudosAgentConfig.Load("kudos-agent.json");
var client = await ChatClientFactory.CreateAsync(config.PrimaryModel);

await using var session = await new AgentSessionBuilder()
    .UseConfig(config)
    .UseChatClient(client)
    .UseResultsDirectory("results")                // results/<session-id>/{work/, rollout.jsonl, status.json}
    .UseApprovalHandler(op => AskUserAsync(op))    // ask before running generated code or writing files
    .BuildAsync();

await foreach (var evt in session.RunTurnEventsAsync("Count the lines of each file and report a Markdown table."))
{
    switch (evt)
    {
        case AgentTextDelta text: Console.Write(text.Text); break;
        case AgentToolCallStarted call: Console.WriteLine($"[tool] {call.Name}"); break;
        case AgentTurnCompleted done: Console.WriteLine(); break;
        case AgentTurnFailed failed: Console.WriteLine($"[error] {failed.Message}"); break;
    }
}
```

A complete, runnable host is in [`samples/MinimalHost`](../../samples/MinimalHost/Program.cs) — `MinimalHost --demo` runs the whole loop with a scripted model (no GPU, no network).

---

## Documentation

- **[QUICK-START.md](../QUICK-START.md)** — installation, requirements, a minimal config and host
- **[API.md](../API.md)** — public API: `AgentSessionBuilder`, sessions, events, safety types, config loading
- **[CONFIGURATION.md](../CONFIGURATION.md)** — the `kudos-agent.json` schema
- **[TOOLS.md](../TOOLS.md)** — built-in tools, your own tools, MCP
- **[SAFETY.md](../SAFETY.md)** — approval policies, sandbox modes, the approval handler
- **[SESSIONS.md](../SESSIONS.md)** — session records, resume and fork, compaction, loop guards
- **[SECRETS.md](../SECRETS.md)** — API keys and the OS secret store
- **[LICENSING.md](../LICENSING.md)** — License Keys
- **[README.md](../README.md)** — every SDK document

---

## License

Kudos.Agent.Sdk is distributed under the End User License Agreement (the LICENSE file included in this package, also at the repository root):

- **Evaluation** — free for up to 30 days; prototypes allowed, not in production, not distributed
- **Commercial license** — every other use (embedding the SDK in your own applications, production, commercial products, standalone CLI use) requires a commercial license and License Key from the Licensor

The sample code under `samples/` may be viewed, copied and modified for use in your applications. Prompts derived from OpenAI Codex are under the Apache License 2.0 — see the THIRD-PARTY-NOTICES.txt file included in this package.

---

## Author

**Hand-crafted by [Akira Kudo](https://www.linkedin.com/in/akira-kudo-4b04163/) in Tokyo, Japan**

Copyright © 2026 Akira Kudo
