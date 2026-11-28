# Kudos.Agent.Sdk.Providers v0.7.0

**Remote model APIs for the Kudos Agent SDK — each through its vendor's official .NET SDK**

[Features](#features) · [Installation](#installation) · [Quick Start](#quick-start) · [Documentation](#documentation) · [License](#license)

---

## Overview

**Kudos.Agent.Sdk.Providers** connects [Kudos.Agent.Sdk](Kudos.Agent.Sdk.md) to remote model APIs, each through its vendor's official .NET SDK behind `Microsoft.Extensions.AI` `IChatClient`.

Version 0.x: the API may still change between minor versions.

---

## Features

| Model type | Service | Credential and endpoint |
|---|---|---|
| `Anthropic` | Claude API | `ANTHROPIC_API_KEY` or `ANTHROPIC_AUTH_TOKEN` (or an `ant auth login` profile) |
| `OpenAI` | OpenAI API | `OPENAI_API_KEY` |
| `AzureOpenAI` | Azure OpenAI (v1 API; model = deployment name) | `AZURE_OPENAI_API_KEY`; endpoint = `serverUrl` in the config or `AZURE_OPENAI_ENDPOINT` |
| `OpenAICompatible` | Any OpenAI-compatible server (llama-server, vLLM, Ollama, …) | `OPENAI_API_KEY` optional; `serverUrl` required. With sub-agents, requests carry llama-server's `id_slot` (`subAgentPolicy.slots`). |
| `Bedrock` | Amazon Bedrock Converse | `AWS_ACCESS_KEY_ID` / `AWS_SECRET_ACCESS_KEY` (/ `AWS_SESSION_TOKEN`), otherwise the AWS SDK default chain (profile / SSO / instance role); region = `region` in the config or `AWS_REGION` |
| `Gemini` | Google Gemini API | `GEMINI_API_KEY` or `GOOGLE_API_KEY` |

Notes:

- Gemini: a free-tier API key (no billing on its Google Cloud project) is limited to a few requests per minute and per day (measured 2026-11-25 for `gemini-3.6-flash`: 5 per minute, 20 per day). An agent run uses that up within one or two tasks and then fails with `429`, which the SDK does not retry. Enable billing for real use.
- Remote models send prompts and tool results (file contents, script output) to the service. Keep them opt-in and announce them, as the Quick Start does.
- API keys are read from environment variables, and a key not set there from the OS secret store under the same name (macOS Keychain / Linux libsecret / Windows Credential Manager, service `kudos-dotnet-agent`; Bedrock can also use the AWS SDK default chain) — never put keys in the config file. `CreateAsync(model)` without an `environment` does the store lookup itself; turn it off with `KUDOS_SECRET_STORE=off`.

---

## Installation

```bash
dotnet add package Kudos.Agent.Sdk.Providers --version 0.7.0
```

**Requirements:** .NET SDK 10 (target `net10.0`). Remote models need a License Key of level `professional` or higher ([LICENSING.md](../LICENSING.md)).

---

## Quick Start

```csharp
using Kudos.Agent.Llm;
using Kudos.Agent.Secrets;

RemoteChatClientProvider.Register();
var model = config.PrimaryModel;
// the process environment, with the model's missing keys filled from the OS secret store
var environment = SecretsLoader.Resolve(ChatClientFactory.ProcessEnvironment(), SecretsLoader.CredentialKeys(model.Type));
if (ChatClientFactory.IsRemote(model))
{
    Console.WriteLine(ChatClientFactory.RemoteNotice(model));                 // tell the user the data leaves the machine
    if (ChatClientFactory.CredentialWarning(model, environment) is { } warning)
    {
        Console.Error.WriteLine(warning);
    }
}

var client = await ChatClientFactory.CreateAsync(model, environment: environment);
```

---

## Documentation

- **[MODELS.md](../MODELS.md)** — remote providers: credentials, endpoints, effort, timeouts
- **[SECRETS.md](../SECRETS.md)** — API keys and the OS secret store
- **[CONFIGURATION.md](../CONFIGURATION.md)** — the `models[]` schema and environment overrides
- **[TROUBLESHOOTING.md](../TROUBLESHOOTING.md)** — remote auth and timeout failures
- **[README.md](../README.md)** — every SDK document

---

## License

Kudos.Agent.Sdk.Providers is distributed under the End User License Agreement (the LICENSE file included in this package, also at the repository root):

- **Evaluation** — free for up to 30 days; prototypes allowed, not in production, not distributed
- **Commercial license** — every other use (embedding the SDK in your own applications, production, commercial products, standalone CLI use) requires a commercial license and License Key from the Licensor

The sample code under `samples/` may be viewed, copied and modified for use in your applications. Prompts derived from OpenAI Codex are under the Apache License 2.0 — see the THIRD-PARTY-NOTICES.txt file included in this package.

---

## Author

**Hand-crafted by [Akira Kudo](https://www.linkedin.com/in/akira-kudo-4b04163/) in Tokyo, Japan**

Copyright © 2026 Akira Kudo
