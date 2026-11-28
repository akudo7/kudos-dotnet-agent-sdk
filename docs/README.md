# Kudos Agent SDK Documentation

<p align="center">
  <strong>Documentation for the Kudos Agent SDK v0.7.0 (target <code>net10.0</code>)</strong>
</p>

<p align="center">
  <a href="#start-here">Start Here</a> ·
  <a href="#reference">Reference</a> ·
  <a href="#understand-the-system">Understand the System</a> ·
  <a href="#license">License</a> ·
  <a href="../README.md">Project README</a>
</p>

---

## Overview

The **Kudos Agent SDK** is four NuGet packages that embed an AI coding agent into a .NET application: the agent loop (Microsoft Agent Framework), built-in tools, skills, MCP, approvals, multi-agent sub-agents and workflows, in-process local models (LLaMASharp / GGUF) and remote model providers.

The core use case: an instruction goes in, the agent writes and runs a script, and the result comes back — typically as a table.

| Package | Contents |
|---|---|
| [`Kudos.Agent.Sdk`](packages/Kudos.Agent.Sdk.md) | Agent loop, built-in tools, safety gate and approvals, skills, MCP client, context compaction, loop guards, session records |
| [`Kudos.Agent.Sdk.LlamaSharp`](packages/Kudos.Agent.Sdk.LlamaSharp.md) | In-process local models (LLaMASharp / llama.cpp GGUF) — model type `LlamaCpp` |
| [`Kudos.Agent.Sdk.Providers`](packages/Kudos.Agent.Sdk.Providers.md) | Remote APIs — `Anthropic`, `OpenAI`, `AzureOpenAI`, `OpenAICompatible`, `Bedrock`, `Gemini` |
| [`Kudos.Agent.Sdk.Workflows`](packages/Kudos.Agent.Sdk.Workflows.md) | Multi-agent workflows — sessions in parallel (fan-out / fan-in) or in sequence, a workflow as a sub-agent |

---

## Start Here

- **[QUICK-START.md](QUICK-START.md)** — install the packages, requirements, a minimal config and host, credentials for remote models
- **[EXAMPLES.md](EXAMPLES.md)** — the `samples/MinimalHost` and `samples/MultiAgentHost` walkthroughs plus focused snippets (custom tools, approval handler, event stream, cancellation, resume)

---

## Reference

- **[API.md](API.md)** — public API for the consumer path: `AgentSessionBuilder`, `CodingAgentSession`, run methods, the `AgentEvent` types, safety types, config loading, provider registration, the tool plug-in surface
- **[CONFIGURATION.md](CONFIGURATION.md)** — the `kudos-agent.json` / `KudosAgentConfig` schema: every section, key, default, validation rule, environment-variable override and `${VAR}` expansion
- **[MODELS.md](MODELS.md)** — model backends: local `LlamaCpp` (GGUF, native backend packages, memory) and remote providers (credentials, endpoints, effort, timeouts); provider registration and `ChatClientFactory`
- **[TOOLS.md](TOOLS.md)** — built-in tools, adding / removing your own tools, MCP integration, how tool results and errors reach the model
- **[SAFETY.md](SAFETY.md)** — the approval / safety model: policies, sandbox modes, exec rules, operation kinds, the approval-handler contract, recommended settings for hosts
- **[SKILLS-GUIDE.md](SKILLS-GUIDE.md)** — skills: the `SKILL.md` format, discovery, the skills index, `load_skill`, writing a good skill
- **[SESSIONS.md](SESSIONS.md)** — session lifecycle: rollout recording, resume and fork, context compression, loop guards, the tool-loop cap, verify, audit, cancellation
- **[ATTACHMENTS.md](ATTACHMENTS.md)** — images and PDFs: attaching files to a turn, input modalities per model, `view_image`, images from host tools, the OpenAI-format tool-result rewrite, records / verify / resume, token estimates and compaction
- **[A2A.md](A2A.md)** — Agent2Agent: remote A2A agents as `a2a_<name>` tools, and serving a coding-agent session over A2A (`serve-a2a`, approvals, cancel, auth, records) — project `Kudos.Agent.Sdk.A2A`
- **[MULTI-AGENT.md](MULTI-AGENT.md)** — several agents for one task: sub-agents (`agent_*`, `spawn_agent`, approvals, depth, parallel calls) and workflows (`Kudos.Agent.Sdk.Workflows` — fan-out / fan-in, pipelines, a workflow as a sub-agent); models and parallelism; records and limits
- **[SECRETS.md](SECRETS.md)** — API keys and tokens: environment variables first, then the OS secret store (macOS Keychain / Linux libsecret / Windows Credential Manager); storing keys (`Host.Cli secrets`), `SecretsLoader`, `${VAR}` refs for MCP / A2A, redaction, turning the store off
- **[LICENSING.md](LICENSING.md)** — License Keys: every session needs one (Evaluation or commercial); where the SDK looks, levels and what each allows, concurrent sessions, `LicenseException` reasons, the integrity check
- **[KV-CACHE.md](KV-CACHE.md)** — incremental KV-cache reuse for the in-process model: the append-only protocol, what keeps a turn incremental, prompt-layout rules a host must respect, images with the in-process projector, diagnosis

---

## Understand the System

- **[ARCHITECTURE.md](ARCHITECTURE.md)** — package split and dependencies, component overview, one turn end to end, key files, extension points
- **[TROUBLESHOOTING.md](TROUBLESHOOTING.md)** — common failures with cause and fix (License Keys, native backend, model load, provider registration, config validation, remote auth / timeout, MCP startup, approvals, slow turns, attachments, sessions)

---

## License

The SDK is distributed under the End User License Agreement ([../LICENSE](../LICENSE)):

- **Evaluation** — free for up to 30 days; prototypes allowed, not in production, not distributed
- **Commercial license** — every other use (embedding the SDK in your own applications, production, commercial products, standalone CLI use) requires a commercial license and License Key from the Licensor

The SDK enforces it: every session needs a valid License Key — an Evaluation key or a commercial one ([LICENSING.md](LICENSING.md)). The sample code under `samples/` may be viewed, copied and modified for use in your applications. Prompts derived from OpenAI Codex are under the Apache License 2.0 ([../THIRD-PARTY-NOTICES.txt](../THIRD-PARTY-NOTICES.txt)).
