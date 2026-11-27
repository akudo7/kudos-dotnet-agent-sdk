# Kudos Agent SDK v0.6.0

**Embed an AI coding agent *inside* your .NET application — it writes and runs code against your product's API, with a local model in your own process, and only after your user approves it.**

[![An AI agent built on Kudos Agent SDK, running inside Autodesk Revit 2027: the approval card shows the C# code the agent wrote](pics/revit-agent-approval.png)](https://www.youtube.com/watch?v=132ZBmiiJ1g)

**Demo video (YouTube):** https://www.youtube.com/watch?v=132ZBmiiJ1g — a local LLM writes Revit API C# code inside Revit, the user approves it, and the answer comes back as a table ([details](apps/revit/README.md))

[Overview](#overview) · [In-Process Model Engine](#in-process-model-engine) · [Multi-Agent and A2A](#multi-agent-and-a2a) · [Packages](#packages) · [Getting the Packages](#getting-the-packages) · [Quick Start](#quick-start) · [Demo](#demo) · [Documentation](#documentation) · [License](#license) · [Author](#author)

---

## Overview

**Kudos Agent SDK** puts an AI coding agent inside your .NET application. The SDK provides the agent loop, built-in tools, skills, MCP, LLM backends and approvals. The model can run in your own process, so no sidecar server is needed.

The core use case: an instruction goes in, the agent writes and runs a script, and the result comes back as a table.

### Why Kudos Agent SDK?

- **Runs inside your application** — the agent, and even the LLM, live in your process: no Python runtime, no sidecar server, and with a local model nothing leaves the machine. Proven inside Autodesk Revit 2027.
- **A local-model engine built for agents** — the SDK's own executor on top of LLaMASharp reuses the KV cache across calls, runs MTP speculative decoding with the model's own prediction head, and serves several agents from one loaded model ([details](#in-process-model-engine)).
- **Your product's tools, your approval UI** — replace the built-in tools with your own API, send every approval to your own dialog, stream the agent's progress into your UI, and ship product knowledge as Markdown skills.
- **Safe and auditable** — generated scripts never run without approval; approval policies, sandbox modes and command rules decide the rest. Every session is recorded, can be resumed or forked, and keeps a hash-chained audit log with secrets redacted.
- **One codebase, any model** — switch between local GGUF models and Anthropic, OpenAI, Azure OpenAI, Amazon Bedrock or Google Gemini by configuration, or mix them, such as a local agent with a cloud reviewer.

### Where It Fits

- **CAD, BIM and engineering tools** — ask about the open model and get a table back; the agent writes code against the product's API ([Revit demo](#demo))
- **Desktop data and analysis applications** — users query and transform their own data in plain language, without exporting it
- **Internal business applications** — multi-step tasks over in-house systems through your own tools, every step approved and recorded
- **Closed or regulated environments** — a local model in your process keeps source data and prompts on the machine

### What's Included

- Agent loop on Microsoft Agent Framework (`Microsoft.Agents.AI`), remote models through each vendor's official SDK (`Microsoft.Extensions.AI` `IChatClient`)
- Coding tools — script execution, shell commands, file read / write / edit / patch, glob and grep search, a todo list, image viewing
- `SKILL.md` skills loaded on demand; MCP servers through the official C# SDK
- Long tasks — context compaction, loop guards, a verify gate on the final answer
- Images and PDFs attached to a turn, for models that accept them
- Multi-agent — sub-agents as tools, swarm-style parallel sub-agents, fan-out / fan-in and sequential workflows, and A2A (preview) ([details](#multi-agent-and-a2a))

---

## In-Process Model Engine

`Kudos.Agent.Sdk.LlamaSharp` does not just call LLaMASharp (llama.cpp). It adds its own executor, built for the way an agent talks to a model: many short calls over one growing conversation, often from several agents at once.

| Feature | What it does |
|---|---|
| **Incremental KV-cache reuse** | Each model call decodes only what was appended since the previous one; the system prompt, tool definitions and earlier turns are not processed again. It also works with hybrid models whose recurrent state cannot be truncated, such as Qwen3.8. |
| **MTP speculative decoding** | The model's own multi-token-prediction (MTP) head drafts the next token, and the main model checks it in the same step; a wrong draft is rolled back, recurrent state included. On automatically for models with an MTP head, and every output token is still the one the model itself samples. |
| **Several agents on one model** | One loaded model serves several conversations as separate sequences, each with its own KV cache. Agents generating at the same time are decoded together in one batch, MTP included. |
| **Images in the same cache** | With the model's image projector (`mmproj`), images from attachments and `view_image` enter the same incremental cache. |
| **Tool calling for local models** | A Qwen-style prompt renderer and tool-call parser make a GGUF model a tool-using agent behind the standard `IChatClient`. |

See [docs/MODELS.md](docs/MODELS.md) and [docs/KV-CACHE.md](docs/KV-CACHE.md).

---

## Multi-Agent and A2A

Several agents can share one task, inside one process or across processes:

| Pattern | Who decides | How |
|---|---|---|
| **Sub-agents** | The parent's model | Each registered sub-agent becomes a tool `agent_<name>` (own model, prompts, skills or tools). Approvals go to the parent's handler, safety can only be narrowed, depth is limited. |
| **Swarm-style parallel sub-agents** | The parent's model | Several sub-agent calls in one response run at the same time (`subAgentPolicy.maxConcurrent`, default 3). With `allowSpawn`, the model writes the instructions of one-shot children itself (`spawn_agent`). |
| **Fan-out / fan-in workflow** | Your host | `AgentWorkflows.Concurrent` sends the same input to every participant at once (at most `maxFanOut`, default 4) and an aggregator combines the answers; failed or timed-out participants are reported, not waited for. |
| **Sequential workflow** | Your host | `AgentWorkflows.Sequential` — a pipeline where each participant gets the previous answer. A workflow can itself be registered as a sub-agent. |
| **A2A client** | The model | Remote agents speaking the [A2A protocol](https://a2a-protocol.org) (v1.0) become tools `a2a_<name>`. |
| **A2A server** | Other agents | A coding-agent session is served over A2A (HTTP+JSON and JSON-RPC, agent card, approvals, cancel, bearer-token auth, session records). |

Workflows ship in `Kudos.Agent.Sdk.Workflows`. A2A is off by default and is not a NuGet package yet, because its Microsoft Agent Framework dependencies are preview releases — ask the [author](#author) if you need it. On an in-process local model, parallel agents share one model and take turns unless it is loaded with several sequences (`seqMax` > 1); remote models and llama-server run them in parallel. See [docs/MULTI-AGENT.md](docs/MULTI-AGENT.md) and [docs/A2A.md](docs/A2A.md).

---

## Packages

The SDK ships as four NuGet packages (target `net10.0`):

| Package | Contents |
|---|---|
| [`Kudos.Agent.Sdk`](docs/packages/Kudos.Agent.Sdk.md) | Agent loop, built-in tools, safety gate and approvals, skills, MCP client, context compaction, loop guards, session records |
| [`Kudos.Agent.Sdk.LlamaSharp`](docs/packages/Kudos.Agent.Sdk.LlamaSharp.md) | In-process local models (LLaMASharp / llama.cpp GGUF) |
| [`Kudos.Agent.Sdk.Providers`](docs/packages/Kudos.Agent.Sdk.Providers.md) | Remote APIs — Anthropic, OpenAI, Azure OpenAI, OpenAI-compatible, Bedrock, Gemini |
| [`Kudos.Agent.Sdk.Workflows`](docs/packages/Kudos.Agent.Sdk.Workflows.md) | Multi-agent workflows — sessions in parallel (fan-out / fan-in) or in sequence |

---

## Getting the Packages

The packages are **not on nuget.org**, and they are not in this repository yet. They are provided to evaluation and commercial customers on request, together with a License Key and the reference command-line host (`Host.Cli`) that some documents use to store keys.

To evaluate the SDK, open an issue in this repository or contact the [author](#author). Once you have the `.nupkg` files, [docs/QUICK-START.md](docs/QUICK-START.md) shows how to reference them from a local folder feed.

**Requirements:** .NET SDK 10. Every session needs a License Key ([docs/LICENSING.md](docs/LICENSING.md)).

---

## Quick Start

```csharp
LlamaSharpChatClientProvider.Register();
var config = KudosAgentConfig.Load("kudos-agent.json");
using var client = await ChatClientFactory.CreateAsync(config.PrimaryModel);

await using var session = await new AgentSessionBuilder()
    .UseConfig(config)
    .UseChatClient(client)
    .UseApprovalHandler(op => Task.FromResult(false))   // deny everything that needs approval
    .BuildAsync();

await foreach (var evt in session.RunTurnEventsAsync("Count the lines of each file here as a Markdown table."))
    if (evt is AgentTextDelta text) Console.Write(text.Text);
```

See [docs/QUICK-START.md](docs/QUICK-START.md) for the complete example, [samples/](samples/README.md) for two runnable hosts and [json/](json/) for configuration examples.

---

## Demo

An application built on the SDK: an AI agent add-in **inside Autodesk Revit 2027**. A local LLM writes Revit API C# code, the user approves it, and the answer comes back as a table. See [apps/revit/README.md](apps/revit/README.md).

---

## Documentation

- **[docs/README.md](docs/README.md)** — SDK documentation index
- **[docs/QUICK-START.md](docs/QUICK-START.md)** — installation, requirements, a minimal config and host
- **[docs/EXAMPLES.md](docs/EXAMPLES.md)** — sample walkthroughs and focused snippets
- **[docs/API.md](docs/API.md)** — public API: `AgentSessionBuilder`, sessions, events, safety types, config loading
- **[docs/CONFIGURATION.md](docs/CONFIGURATION.md)** — the `kudos-agent.json` schema
- **[docs/MODELS.md](docs/MODELS.md)** — local and remote model backends
- **[docs/TOOLS.md](docs/TOOLS.md)** — built-in tools, your own tools, MCP
- **[docs/SAFETY.md](docs/SAFETY.md)** — approval policies, sandbox modes, the approval handler
- **[docs/SKILLS-GUIDE.md](docs/SKILLS-GUIDE.md)** — the `SKILL.md` format and discovery
- **[docs/SESSIONS.md](docs/SESSIONS.md)** — session records, resume and fork, compaction, loop guards
- **[docs/ATTACHMENTS.md](docs/ATTACHMENTS.md)** — images and PDFs
- **[docs/MULTI-AGENT.md](docs/MULTI-AGENT.md)** — sub-agents, parallel sub-agents and fan-out / fan-in or sequential workflows
- **[docs/A2A.md](docs/A2A.md)** — Agent2Agent: remote agents as tools, serving a session over A2A
- **[docs/KV-CACHE.md](docs/KV-CACHE.md)** — KV-cache reuse for in-process models
- **[docs/SECRETS.md](docs/SECRETS.md)** — API keys and the OS secret store
- **[docs/LICENSING.md](docs/LICENSING.md)** — License Keys
- **[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)** — package split, components, extension points
- **[docs/TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md)** — common failures with cause and fix
- **[CHANGELOG.md](CHANGELOG.md)** — release notes per package version

---

## License

Kudos Agent SDK is distributed under the End User License Agreement ([LICENSE](LICENSE)):

- **Evaluation** — free for up to 30 days; prototypes allowed, not in production, not distributed
- **Commercial license** — every other use (embedding the SDK in your own applications, production, commercial products) requires a commercial license and License Key from the Licensor

The sample code under `samples/` may be viewed, copied and modified for use in your applications. Third-party notices: [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).

---

## Author

**Hand-crafted by [Akira Kudo](https://www.linkedin.com/in/akira-kudo-4b04163/) in Tokyo, Japan**

<p align="center">Copyright © 2026 Akira Kudo</p>
