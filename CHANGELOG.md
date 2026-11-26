# Changelog

Notable changes to the `Kudos.Agent.Sdk` packages.
Format: [Keep a Changelog](https://keepachangelog.com/en/1.1.0/). Versions follow SemVer; in 0.x a minor version may break.

## [Unreleased]

## [0.6.0] - 2026-11-26

### Added

- `Kudos.Agent.Licensing`: `KudosLicensing` (Resolve / Parse / UseLicense / Require), `KudosLicense`, `LicenseException`, and `AgentSessionBuilder.UseLicense` ([guide](docs/LICENSING.md)).
- License levels: remote models and MCP need `professional`, A2A and image / PDF input need `enterprise`; `maxConcurrent` limits open sessions per OS user ([guide](docs/LICENSING.md#levels)).
- The SDK checks its assemblies against a signed hash manifest recorded when the packages were built; single-file publishing is not supported ([guide](docs/LICENSING.md#integrity-check)).
- `Host.Cli license status|set|remove` stores a verified License Key in the OS secret store; `KUDOS_LICENSE` gives one key for a single run.
- Samples `MinimalHost` / `MultiAgentHost`: `--license <file>`; exit code 3 when there is no valid License Key ([examples](docs/EXAMPLES.md)).

### Changed

- **Breaking:** every session needs a valid License Key (Evaluation or commercial): `BuildAsync` and `CodingAgentSession.CreateAsync` throw `LicenseException` before creating anything ([guide](docs/LICENSING.md)).

## [0.5.0] - 2026-11-26

### Added

- `THIRD-PARTY-NOTICES.txt` in every package: attribution and the Apache License 2.0 for the prompts derived from OpenAI Codex.

### Changed

- `LICENSE` covers the `Kudos.Agent.Sdk.*` NuGet packages; the old list of three omitted `Kudos.Agent.Sdk.Workflows` (shipped since 0.2.0).
- `LICENSE` is about the SDK packages only: the Autodesk Plugin definition, clauses and trademark names are removed (an Autodesk plugin that embeds the SDK is still a commercial use).
- **Breaking (license):** `LICENSE` no longer names kudosflow2: free use is Evaluation only, every other use needs a commercial License Key; `samples/` code may be copied and modified.
- **Breaking (license):** Evaluation is limited to 30 days (prototypes allowed); governing law is Japan (Tokyo District Court).
- `LICENSE`: you keep your applications and the agent's outputs; AI agent and data section; decompiling as permitted by law; configuring is not modifying.
- `LICENSE`: new HIGH-RISK USE section (not for safety-critical systems) and INDEMNIFICATION section (you cover third-party claims arising from your use).
- NuGet package tags no longer include `autodesk` (the SDK is not specific to Autodesk products).

## [0.4.1] - 2026-11-25

### Changed

- Embedded base prompt and `output-format` skill: the 20-row cap applies to every table in an answer, including extra or derived tables.

## [0.4.0] - 2026-11-25

### Removed

- **Breaking:** the `net8.0` target. All packages target `net10.0` only; LLaMASharp cannot load local models on .NET 8.

## [0.3.0] - 2026-11-25

### Added

- In-process multiple sequences (`models[].config.seqMax`): agents sharing one local model keep their own KV state and run in parallel.
- In-process MTP speculative decoding (`models[].config.mtp`), on by default for a model with an MTP head; output unchanged.
- Image and PDF attachments, the `view_image` tool, and images for the in-process model (`mmproj`) with incremental KV.
- API keys missing from the environment are read from the OS secret store (Keychain / libsecret / Credential Manager).
- `a2a` configuration, A2A rollout events and `AgentSessionBuilder.UseMcpConnection` (shared MCP servers); client / server in the unpackaged `Kudos.Agent.Sdk.A2A`.

### Changed

- The packages ship obfuscated (public API kept); hosts must not rely on internal names or anonymous-type JSON.
- Default Gemini model in the sample config and docs: `gemini-3.6-flash` (was `gemini-3.8-flash`).

### Fixed

- In-process responses use unique tool-call ids, so compaction can remove old tool-call pairs; the audit chain stays intact when several agents record at once.

## [0.2.0] - 2026-11-24

### Added

- Sub-agents: `AddSubAgent` / `agent_<name>` tools, `subAgents[]` config, `spawn_agent`, parallel sub-agents.
- `Kudos.Agent.Sdk.Workflows` package: concurrent (fan-out / fan-in) and sequential workflows over sessions; sample `samples/MultiAgentHost`.
- English SDK documentation under [docs/](docs/README.md); the proprietary EULA ships in every package.

### Changed

- **Breaking:** `CodexAgentConfig` renamed to `KudosAgentConfig`, config files `json/codex-agent*.json` to `json/kudos-agent*.json` (JSON schema unchanged).

## [0.1.0] - 2026-11-23

### Added

- First packages `Kudos.Agent.Sdk`, `.LlamaSharp`, `.Providers` (net8.0 + net10.0): `AgentSessionBuilder`, tool plug-in, approvals, typed events, cancellation, embedded prompts.
- Models: in-process LLaMASharp, Anthropic, OpenAI, Azure OpenAI, Amazon Bedrock, Google Gemini; MCP client; skills; sample `samples/MinimalHost`.
