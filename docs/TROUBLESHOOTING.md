# Troubleshooting

Common failures when embedding the SDK, with the message you will see, the cause and the fix. Messages in
quotes are copied from the source; `<...>` marks a value filled in at run time.

Before anything else, give the session a logger: `AgentSessionBuilder.UseLogger(logger)`. MCP startup
results, skill warnings, compaction fallbacks and single-writer warnings go **only** there; without a logger
they are dropped.

## Contents

1. [License Keys](#license-keys)
2. [Model Loading (In-Process)](#model-loading-in-process)
3. [Model Clients](#model-clients)
4. [Configuration](#configuration)
5. [Tools, MCP and Approvals](#tools-mcp-and-approvals)
6. [Performance](#performance)
7. [Attachments](#attachments)
8. [Sessions](#sessions)

---

## License Keys

### `LicenseException`: "No Kudos.Agent.Sdk License Key found. …"

No key on any path (OS secret store, `UseLicense`, embedded resource). Install one: `Host.Cli license set` (stdin), or
`AgentSessionBuilder.UseLicense(text)` / `KudosLicensing.UseLicense(text)`. An Evaluation key or a commercial key
from the Licensor — [LICENSING.md](LICENSING.md).

### `LicenseException`: "… needs a License Key of level <level> or higher; the license for <licensee> is <level>"

The session uses a feature outside the key's level: a remote model or MCP servers (`professional`), A2A or image /
PDF input (`enterprise`). Use a key of that level, or a local model / no MCP servers.

### `LicenseException`: "the license for <licensee> allows <n> concurrent session(s) and they are all in use …"

`maxConcurrent` sessions are open for this OS user (in any process). Dispose sessions you are done with
(`await using`); a crashed process gives its seats back when it ends.

### `LicenseException`: "Kudos.Agent.Sdk integrity check failed: …"

A `Kudos.Agent.Sdk*.dll` differs from the released package (restore the package files — clear the NuGet cache and
restore), or the application was published as a single file (not supported — the assemblies must be files).

---

## Model Loading (In-Process)

### "The native library cannot be correctly loaded"

Full message from LLaMASharp (thrown on the first native call, usually while loading the model):

```
The native library cannot be correctly loaded. It could be one of the following reasons:
1. No LLamaSharp backend was installed. Please search LLamaSharp.Backend and install one of them.
2. You are using a device with only CPU but installed cuda backend. Please install cpu backend instead.
3. One of the dependency of the native library is missed. ...
4. Try to compile llama.cpp yourself to generate a libllama library, then use `LLama.Native.NativeLibraryConfig.WithLibrary` ...
```

A variant is `Failed to load the native library. Please check the log for more information.`

**Cause**: `Kudos.Agent.Sdk.LlamaSharp` depends on `LLamaSharp` 0.30.0 but deliberately ships no native
backend. Either none is referenced, the wrong one is referenced for the machine, or a native dependency
(CUDA runtime, driver — on Windows also the Visual C++ runtime) is missing.

**Solution**:

- Reference exactly one backend **in the application project** (not in a class library that the application
  loads indirectly), at the same version as `LLamaSharp`:
  ```xml
  <PackageReference Include="LLamaSharp.Backend.Cuda13" Version="0.30.0" />  <!-- or Cuda12, or Cpu -->
  ```
- Match the CUDA package to the installed NVIDIA driver / CUDA runtime major version (`Cuda12` vs `Cuda13`).
  On a machine without an NVIDIA GPU use `LLamaSharp.Backend.Cpu`.
- Check that the native files were copied to the output folder (`runtimes/<rid>/native/...`) and that their
  dependencies resolve (`ldd` on Linux, `dumpbin /dependents` on Windows).
- **Windows**: install the Microsoft Visual C++ 2015-2022 Redistributable (x64). `llama.dll` and `ggml*.dll` import
  `MSVCP140.dll`, `VCRUNTIME140.dll`, `VCRUNTIME140_1.dll` and the OpenMP runtime `VCOMP140.DLL`, none of which a
  clean Windows install has (the CPU backend fails the same way as a CUDA one). An application that embeds the SDK
  should install the redistributable or list it as a prerequisite.
- **Plug-ins** (the SDK loaded into another application's process): the process base directory is the host
  application's folder, so the default search can miss the plug-in's `runtimes/` folder. Call
  `LLama.Native.NativeLibraryConfig.All.WithSearchDirectory(<plug-in folder>)` before the first model is created.

See [MODELS.md](MODELS.md) for backend selection.

### "Model file not found: <path>"

**Cause**: `models[].config.model` of a `LlamaCpp` entry does not point to an existing GGUF file
(`FileNotFoundException` from `LlamaSharpChatClientProvider`).

**Solution**: use an absolute path, or a path relative to the process's current directory. The `MODEL`
environment variable overrides the path of `models[0]` when you call `config.WithEnvOverrides(env)`.

### Out of memory while loading the model

Typical messages: `Failed to load model '<path>'` (LLaMASharp `LoadWeightsFailedException`) while loading the
weights, or `Failed to create context from model` when the KV cache for `contextSize` cannot be allocated.
On CUDA the native log usually shows an allocation failure first.

**Cause**: the weights plus the KV cache do not fit in VRAM. `gpuLayers` defaults to 999 (offload every
layer), and the KV cache grows linearly with `contextSize`.

**Solution**:

- Lower `models[].config.gpuLayers` so part of the model stays in system RAM (slower, but it loads).
- Lower `contextSize`. Remember to keep the compression window in line with it
  ([below](#compaction-runs-on-every-model-call-or-never)).
- Pick the GPU with `mainGpu`, or use a smaller quantization of the model.
- Multi-GPU split ratio, split mode and batch size are not config keys. Create the client yourself:
  ```csharp
  var client = await LlamaSharpChatClient.CreateAsync(path, gpuLayers: 66, mainGpu: 0, contextSize: 32768,
      new LlamaSharpChatClientOptions { TensorSplit = [3f, 2f], BatchSize = 512 });
  builder.UseChatClient(client);
  ```

### "MTP needs a model with an MTP head (none loaded — LayerCountMtp 0)"

**Cause**: `models[].config.mtp` (or `LlamaSharpChatClientOptions.Mtp`) is explicitly `true`, but the GGUF has no MTP head
(left unset, MTP simply stays off on such a model). A draft
context on such a model would crash the process, so loading stops before creating one.

**Solution**: remove `mtp` (or set it to `false`) for this model, or use a model whose GGUF includes an MTP head
(Qwen3.8-27B does). The validation error for `mtp: true` on another model type is listed in
[CONFIGURATION.md](CONFIGURATION.md#validation-rules).

### `ContextOverflowException` on the first or a later call

A variant (from LLaMASharp, reported as a failed turn): `The context window is full and the current strategy is
set to ThrowException. To automatically truncate and manage context, set InferenceParams.OverflowStrategy to
ContextOverflowStrategy.TruncateAndReprefill.` — do not change the overflow strategy; the cause is the same.

**Cause**: the rendered prompt is longer than the `contextSize` the model was loaded with.
`StatefulLlamaExecutor` throws before decoding instead of letting native decoding hit the context boundary.
The default `contextSize` is only **4096**, which leaves no room for the built-in system prompt (about 26 KB
of text) plus the tool definitions — even **8192** overflows on the first turn.

**Solution**: set `contextSize` to what the model and your VRAM support (for example 32768 or more), and set
the compression window to the same value so compaction runs before the limit
([next section](#compaction-runs-on-every-model-call-or-never)).

With `seqMax` > 1 the limit is per conversation: `contextSize / seqMax` when `kvUnified` is `false`. With
`kvUnified` `true` the conversations share `contextSize`: a step that does not fit evicts an idle conversation
or waits for a running one to finish its call, and fails only when one conversation alone exceeds `contextSize`
or every other running conversation is waiting too.

### Compaction runs on every model call, or never

**Cause**: the compression window is independent of the loaded `contextSize`. Effective window =
`max(modelContextWindow - overheadTokens, 1)`, with defaults 262144 and 45000.

- With a local `contextSize` of, say, 16384 and the defaults, the backstop would fire at about 152,000
  estimated tokens, so it never fires and the run ends in `ContextOverflowException`.
- With `modelContextWindow: 16384` but `overheadTokens` left at 45000, the effective window is 1 token and
  **every** model call triggers a compaction (`compaction` events on every step, very slow runs).

**Solution**: for an in-process model set `contextCompression.modelContextWindow` to the loaded `contextSize`
(or set the model's `config.contextWindow` and call `config.WithEnvOverrides(env)`, which copies it over) and
choose `overheadTokens` comfortably below it. Then check `trigger.fraction`. See
[SESSIONS.md](SESSIONS.md#context-compression).

---

## Model Clients

### "Model type '<type>' has no registered chat client provider — ..."

Thrown as `NotSupportedException` by `ChatClientFactory.CreateAsync`. The exact text depends on the type:

```
Model type 'LlamaCpp' has no registered chat client provider — reference Kudos.Agent.Sdk.LlamaSharp and call LlamaSharpChatClientProvider.Register().
Model type 'Anthropic' has no registered chat client provider — reference Kudos.Agent.Sdk.Providers and call RemoteChatClientProvider.Register().
```

(The second form is used for every remote type: `OpenAI`, `AzureOpenAI`, `OpenAICompatible`, `Bedrock`,
`Gemini`, `Anthropic`.)

**Cause**: the core package contains no model client. Providers live in the model packages and must be
registered once per process before the first `ChatClientFactory.CreateAsync`.

**Solution**:

```csharp
LlamaSharpChatClientProvider.Register();   // Kudos.Agent.Sdk.LlamaSharp — ModelType.LlamaCpp
RemoteChatClientProvider.Register();       // Kudos.Agent.Sdk.Providers — all remote types
var client = await ChatClientFactory.CreateAsync(config.PrimaryModel);
```

Registration is idempotent. `ChatClientFactory.IsRegistered(type)` tells you whether a provider is present.

### Remote provider: missing credentials

Thrown as `InvalidOperationException` when the client is created:

| Type | Message |
|---|---|
| `OpenAI` | `OPENAI_API_KEY is not set` |
| `AzureOpenAI` | `AZURE_OPENAI_API_KEY is not set`, or `Azure OpenAI endpoint: set config.serverUrl or AZURE_OPENAI_ENDPOINT` |
| `Bedrock` | `Bedrock region: set config.region or AWS_REGION` |
| `Gemini` | `GEMINI_API_KEY / GOOGLE_API_KEY is not set` |

**Cause**: credentials never live in `kudos-agent.json`; they are read from environment variables, and a key
missing there from the OS secret store ([SECRETS.md](SECRETS.md)). By default `ChatClientFactory.CreateAsync`
reads the process environment plus the store; you can pass your own dictionary through its `environment`
parameter (used as given — no store lookup).

**Solution**: set the variable for the process, store it once (`printf '%s' '<key>' | Host.Cli secrets set
OPENAI_API_KEY`), or pass it in `environment`. `Host.Cli secrets list` shows where each key is set. If the key is
stored but still not found: the store may be off (`KUDOS_SECRET_STORE=off`, or `SecretStores.Default` set to
`None`), the Linux keyring may be locked (for example over SSH), or the key was stored under another name (the
name must be the variable name exactly). Notes per provider:

- `Anthropic`: creation does not fail. `ANTHROPIC_API_KEY`, then `ANTHROPIC_AUTH_TOKEN`, come from the
  environment dictionary (environment, then store); with neither, the Anthropic SDK falls back to an
  `ant auth login` profile. `ChatClientFactory.CredentialWarning(model, env)` returns
  `ANTHROPIC_API_KEY / ANTHROPIC_AUTH_TOKEN not set — relying on an `ant auth login` profile.` when no
  variable is visible.
- `Bedrock`: `AWS_ACCESS_KEY_ID` / `AWS_SECRET_ACCESS_KEY` (and `AWS_SESSION_TOKEN`) when present, otherwise
  the AWS SDK's default credential chain.
- `OpenAICompatible`: the key is optional (`OPENAI_API_KEY` if set); `config.serverUrl` is required.
- Call `ChatClientFactory.CredentialWarning(model, SecretsLoader.Resolve(ChatClientFactory.ProcessEnvironment(),
  SecretsLoader.CredentialKeys(model.Type)))` at startup to warn early, and `ChatClientFactory.RemoteNotice(model)` to tell the user that prompts and tool results leave the
  machine.

A key that is present but wrong fails on the first model call instead: the vendor SDK's exception ends the
turn (`RunTurnAsync` throws; `RunTurnEventsAsync` ends with `AgentTurnFailed(message)`; the rollout gets an
`error` event).

### Gemini: the run shows no output for minutes, then "This model is currently experiencing high demand"

**Cause**: the model is overloaded and answers `503 UNAVAILABLE`. The Google.GenAI SDK retries with backoff (4
attempts by default) within the request timeout (1800 s by default), so nothing is printed until the retries run
out: 8 min 23 s for `gemini-3.8-flash` on 2026-11-25. The connection to Google stays open the whole time,
and the session record shows `user-input` and nothing after it.

**Solution**: use a stable, less busy model (`gemini-3.6-flash` answered in about 1 s the same day, see
[MODELS.md — Choosing the Gemini model](MODELS.md#choosing-the-gemini-model-measured-2026-11-25)). For one run,
set `MODEL=gemini-3.6-flash`. To fail fast instead of waiting, set `"maxRetries": 0` (or `1`) and a smaller
`"timeoutSeconds"` on the model. The SDK does not switch to another model by itself.

### Gemini: "You exceeded your current quota" (`429`, `…FreeTier`)

**Cause**: the key's project is on the free tier. Measured 2026-11-25 for `gemini-3.6-flash`: 5 requests per minute
and 20 per day, per project and model. The quota id in the error's details says which limit was hit
(`GenerateRequestsPerMinutePerProjectPerModel-FreeTier` or `GenerateRequestsPerDayPerProjectPerModel-FreeTier`).
The SDK does not retry a `429`, so the turn fails at once.

**Solution**: enable billing on the key's Google Cloud project. Waiting helps only for the per-minute limit: the
error carries a `retryDelay`, about 40 s. The daily limit resets the next day. Every model has its own quota, so
another model (for example `MODEL=gemini-3.5-flash-lite`) works until its quota runs out as well.

### Gemini: "API key not valid" (`API_KEY_INVALID`)

**Cause**: the key that reached Google is not a valid key. It may be a typo or an old key. It may also be
something other than the key, pasted into the hidden prompt of `Host.Cli secrets set`. `CredentialWarning` does not
catch this, because a value is present.

**Solution**: check where the key comes from with `Host.Cli secrets list`. An environment variable wins over the
store, so a stale `GEMINI_API_KEY` in your shell profile hides the stored one. Compare the stored value's length
with your key without printing it ([SECRETS.md — Check what you stored](SECRETS.md#check-what-you-stored)), then
store it again with `Host.Cli secrets set GEMINI_API_KEY`.

### Remote provider: timeouts

**Cause**: a long non-streaming generation, or the large summary request of a compaction, exceeds the HTTP
timeout.

**Solution**: per model, set `config.timeoutSeconds` and `config.maxRetries`. When `timeoutSeconds` is unset,
`OpenAI`, `AzureOpenAI`, `OpenAICompatible`, `Bedrock` and `Gemini` use 1800 seconds (the vendor SDK defaults
of about 100 seconds are too short for agent runs); `Anthropic` keeps its SDK default. For Claude the output
cap of a request is limited to 16000 tokens (`AnthropicChatClient.MaxNonStreamingOutputTokens`) regardless
of `maxTokens`. Both values must be positive / non-negative or validation fails (next section).

---

## Configuration

### Validation errors

`KudosAgentConfig.Load`, `FromJson` and `WithEnvOverrides` call `Validate()`, which throws
`InvalidOperationException` with one of these messages. `AgentSessionBuilder` does not validate; for a config
built in code, call `config.Validate()` yourself.

| Message | Fix |
|---|---|
| `contextCompression.trigger.fraction must be in (0, 1]: <value>` | Use a fraction such as `0.7`. |
| `contextCompression.mode must be autonomous \| handoff \| backstop: <value>` | Use one of the three, in lowercase. |
| `contextCompression.modelContextWindow must be > 0` | Set a positive window. |
| `contextCompression.overheadTokens must be >= 0` | Use 0 or more. |
| `guard.maxToolArgsKb must be > 0: <value>` | Use a positive size in KB (default 64). |
| `guard.maxToolCalls must be >= 0: <value>` | Use 0 (off) or a positive budget. |
| `models must contain at least one entry` | Add a `models[]` entry; `models[0]` is the one the session runs. |
| `mcpServers[<name>]: name must match [A-Za-z0-9._-]+` | Rename the server (the name becomes part of `mcp_<server>_<tool>`). |
| `mcpServers[<name>]: command is required` | Set `command`. |
| `mcpServers: duplicate name '<name>'` | Server names must be unique (case-insensitive). |
| `models[<id>].config.model is required for LlamaCpp` | Set the GGUF path. |
| `models[<id>].config.serverUrl is required for OpenAICompatible` | Set the server's base URL. |
| `models[<id>].config.timeoutSeconds must be > 0` | Remove it or use a positive value. |
| `models[<id>].config.maxRetries must be >= 0` | Remove it or use 0 or more. |
| `models[<id>].config.model (Claude model id) is required for Anthropic` | Set the model id. |
| `models[<id>].config.model is required for <type>` | `OpenAI`, `AzureOpenAI` (deployment name), `Bedrock` (model or inference profile id), `Gemini`. |
| `models[<id>].config.effort is not supported for Bedrock (the Converse adapter sends thinking.budget_tokens, which Claude 5 models reject)` | Remove `effort` from the Bedrock entry. |
| `models[<id>].config.effort must be one of low \| medium \| high \| xhigh: '<value>'` | Use one of the four values. |
| `models[<id>].config.contextWindow must be > 0` | Remove it or use a positive value. |

Other configuration errors:

| Message | Where | Fix |
|---|---|---|
| `Invalid value '<value>' for <EnumName>` | JSON parsing (`JsonException`) | Enum values accept exact, camelCase or kebab-case names, e.g. `on-request`, `workspace-write`, `LlamaCpp`, `azure-openai`. |
| `Config JSON was empty` | `FromJson` | The file contains `null` or nothing. |
| `MODEL_ID '<id>' matches no models[] entry (ids: <list>)` | `WithEnvOverrides` | Use one of the listed ids. |
| `Invalid value for SAFETY_APPROVAL_POLICY: '<value>'` (or `SAFETY_SANDBOX_MODE`) | `WithEnvOverrides` | Use a valid policy / mode name. |
| `UseConfig is required` / `UseChatClient is required` | `AgentSessionBuilder.BuildAsync` | Call both before building. |
| `Duplicate tool name: '<name>'` | Building the session | A host tool has the name of a default tool; call `RemoveTool(name)` first. |

See [CONFIGURATION.md](CONFIGURATION.md) for every key.

---

## Tools, MCP and Approvals

### MCP server fails to start

The session continues without that server's tools. The logger shows one of:

```
[mcp:<name>] startup failed (<ExceptionType>: <message>) — server skipped
[mcp:<name>] stderr: <line>
[mcp:<name>] connected: <n> tools
```

**Causes and fixes**:

| Cause | Fix |
|---|---|
| No logger, so the warning is invisible | `UseLogger(...)`. |
| `command` is not on the `PATH` of the host process | Use an absolute path, or fix `PATH` for the process. |
| A relative path in `command` or `args` | Servers start with the **session work directory** (`<resultsRoot>/<sessionId>/work`) as their working directory. Use absolute paths. |
| `${VAR}` references in `env` were not expanded | Expansion happens only in `config.WithEnvOverrides(env)`. A variable missing from the environment is replaced by an empty string. |
| The server is disabled | `enabled: false` on the entry, or `MCP_ENABLED=0` (also `false`, `off`, `no`) applied through `WithEnvOverrides`. |
| The server takes too long or exits during the handshake | Run the same command in a terminal and check its stderr (forwarded as `[mcp:<name>] stderr:` lines). |

Only `IOException`, `OperationCanceledException` and `InvalidOperationException` are turned into the warning;
any other exception during startup propagates out of `BuildAsync()`.

At call time, an MCP tool error comes back to the model as a failed tool result, for example
`mcp tool call failed: <message>`. Under `approvalPolicy: on-request` every MCP call needs approval; without an
approval handler it is denied with
`approval required but no approval handler available (fail-closed): mcp-tool`.

### Approval prompts block the turn

**Symptoms**: the turn stops making progress while a prompt is open, never resumes, or a cancelled turn does
not stop.

**Cause**: the approval handler (`UseApprovalHandler`) is awaited inside tool execution, and it receives no
cancellation token. The turn waits for as long as the handler does. Common reasons:

- The handler waits for UI input that never arrives (dialog hidden, window closed, no user present).
- The handler blocks the thread that must show the prompt (for example synchronously waiting on the UI thread
  from the UI thread), so it deadlocks.
- The handler waits for something that only happens after the host takes the next event from
  `RunTurnEventsAsync`; with that method the next step runs only when the previous event was consumed.
- The policy simply asks a lot: under `on-request` every command without an exec rule, every write outside the
  writable roots, and every MCP call asks; `execute_code` **always** asks, in every mode.

**Solution**:

- Make the handler return `false` on timeout and on turn cancellation (for example with
  `Task.WhenAny(dialogTask, Task.Delay(timeout, turnToken))`).
- Marshal UI work properly (`await dispatcher.InvokeAsync(...)`, never `.Result` / `.Wait()` on the UI thread).
- Reduce prompts with policy instead of clicking: `execRules` with `allow` for known-safe command prefixes,
  `execDefault`, or `writableRoots`. See [SAFETY.md](SAFETY.md).

Related denials, returned to the model as failed tool results:

| Result text | Meaning |
|---|---|
| `approval required but no approval handler available (fail-closed): <reason>` | An `Ask` decision with no handler configured. |
| `rejected by user (approval denied): <reason>` | The handler returned `false`. |
| `rejected by safety gate: <reason>` | The policy rejected the operation (deny rule, read-only sandbox, `never` policy, argument scope). |

Note that `approvalPolicy: never` does not make `execute_code` run unattended: an automatic approval is
escalated to the ask path (and in `read-only` mode the script is rejected outright), so without a handler
every script is denied with
`script execution approval required but no approval handler available (fail-closed): execute_code is always the ask path`.
To let scripts run without a person present, install a handler that approves according to your own rules.

---

## Performance

### Parallel agents are slower with `seqMax` > 1 than one at a time

**Symptoms**: with the in-process model and `models[].config.seqMax` > 1, sub-agents or workflow participants
run at the same time, but the whole run takes longer than with `seqMax` 1.

**Cause**: with `kvUnified` `true` (the default) the conversations share one KV pool, and every model step works
across the cells of all of them — the more conversations are held at once, the heavier each step. Measured on a
27B hybrid model on a GPU (three workflow participants, `contextSize` 49152): shared KV 45.8 s, separate KV
32.3 s, one at a time (`seqMax` 1) 37.0 s. Each conversation's prompt is also still processed one at a time.
A small `contextSize` adds evictions: when the running conversations do not fit together, they wait for each
other and evicted ones process their prompt again.

**Solution**: set `kvUnified` to `false` when `contextSize / seqMax` is enough for one conversation, and size
`contextSize` so the conversations that run together fit.

### Slow turns: every call re-processes the whole prompt

**Symptoms**: with the in-process model, turn 2 and later of a session take about as long as turn 1, and the
time per call grows with the length of the conversation.

**Cause**: the incremental KV-cache path was not taken, so the whole prompt was evaluated again. Usual
reasons: turn-varying text in the system prompt, prompt files or `AGENTS.md`; middleware that edits history or
rewrites assistant messages; a `system` message injected mid-conversation; one client shared by several
sessions; the previous call was cancelled, failed or stopped at `maxTokens`. One full evaluation right after a
compaction, a resume or a guard nudge is expected.

**Solution**: time each call and diff consecutive prompts as described in
[KV-CACHE.md](KV-CACHE.md#5-verifying-and-diagnosing), then fix the rule that was broken. Also rule out the
non-cache causes: the CPU backend, a low `gpuLayers`, or a compression window that triggers a compaction on
every call ([above](#compaction-runs-on-every-model-call-or-never)).

### A turn ends without a final answer after many tool calls

**Symptoms**: the turn completes (`turn-complete`, status `complete`) but the final text is empty, and the
last thing in the transcript is a tool call.

**Cause**: the per-turn tool loop reached `CodingAgentFactory.MaxToolLoopIterations` (500 model round trips).
At its cap the underlying `FunctionInvokingChatClient` stops invoking tools without throwing. The cap is a
constant, not a config key.

**Solution**:

- Look for `guard` events first: a model stuck in a loop is normally stopped much earlier by the repeat
  breakers. If `guard.enabled` is `false`, turn it back on.
- To end long turns cleanly with a final answer, set `guard.maxToolCalls` (a session-wide budget). On reaching
  it the model is told to stop calling tools and answer (`tool-budget-abort` event).
- Split very long tasks into several turns.

See [SESSIONS.md](SESSIONS.md#tool-loop-iteration-cap).

---

## Attachments

### `AttachmentException`: "the model '<id>' cannot take images ('<name>') …"

**Cause**: an image was attached for a model whose input modalities do not include `image`. `OpenAICompatible` and
`LlamaCpp` models are text-only unless configured otherwise.

**Solution**: use a model that takes images; for the in-process `LlamaCpp` model set `config.mmproj` to the model's
projector GGUF ([MODELS.md](MODELS.md#images-and-pdfs)); for an OpenAI-compatible server that has a vision projector set
`"inputModalities": ["text", "image"]` on the model. See [ATTACHMENTS.md](ATTACHMENTS.md#which-models-take-images-and-pdfs).

### HTTP 500 "image input is not supported - hint: if this is unexpected, you may need to provide the mmproj"

**Cause**: the model entry declares `image`, but the llama-server behind it runs without a vision projector
(`/props` reports `modalities.vision = false`).

**Solution**: start llama-server with `--mmproj <projector>.gguf` for the model, or remove `image` from
`inputModalities`.

### `NotSupportedException`: "The prompt has an image, but the model was loaded without an image projector"

**Cause**: a `LlamaSharpChatClient` or `StatefulLlamaExecutor` created without a projector received an image prompt —
for example a host that builds the client directly and calls the segment overloads.

**Solution**: create the client with `LlamaSharpChatClientOptions.Mmproj` (or set `config.mmproj` and use
`ChatClientFactory`). Without a projector the client renders text only and leaves images out.

### In-process image turns are slow after compaction or resume

**Cause**: a full evaluation (compaction, resume, an edited history) evaluates every image in the history again —
about a second per 1,000-token image on a GPU, much longer on a CPU. Turns that only append (including a new image)
stay incremental.

**Solution**: expected. Lower `imageMaxTokens` / `imageMinTokens` for images that do not need fine detail, or keep fewer
images in the kept history (compaction replaces the oldest images with reference sentences first).

### "unsupported attachment '<name>': the content is not PNG / JPEG / WebP / GIF / PDF …"

**Cause**: the file is not one of the supported types. The type is read from the content, so a renamed file
(`report.png` that is really a text file) is refused.

**Solution**: convert the file to PNG / JPEG / WebP / GIF or PDF.

### `[attachment missing: <name>]` after resuming

**Cause**: the resumed session's `work/attachments/` file is gone or changed since it was recorded (its SHA-256 no
longer matches). The logger has a `[resume] …` warning with the path.

**Solution**: restore the file from a copy of the session's work directory, or continue without it.
`AuditVerify.VerifySession` lists every affected reference (check `attachment`).

---

## Sessions

### "audit chain out of sync with rollout: ..." when building or resuming a session

Full message: `audit chain out of sync with rollout: <n> chain entries vs <m> rollout lines — refusing to
continue (tampering or torn write)`. A variant is `audit chain is corrupt: <detail>`. Both are
`InvalidDataException`s from `BuildAsync()`.

**Cause**: `rollout.jsonl` and `audit-chain.jsonl` in the session directory disagree: a file was edited or
truncated, or the process died between writing a rollout line and its chain entry.

**Solution**: inspect the session with `AuditVerify.VerifySession(sessionDir)`. To continue the conversation
anyway, fork it (`RolloutFork.ForkRollout(src, newId, resultsRoot)`) and resume the fork under the new id; the
fork starts a fresh chain from the copied lines. See [SESSIONS.md](SESSIONS.md#audit-chain).

### "Invalid session id: '<id>' (allowed: [A-Za-z0-9._-]+, max 64 chars)"

**Cause**: the id passed to `UseResultsDirectory` / `SessionLayout.Create` contains other characters or is too
long.

**Solution**: use letters, digits, `.`, `_` and `-` only, at most 64 characters, or omit the id to get a
generated one.

---

## See Also

- [QUICK-START.md](QUICK-START.md) — requirements and installation
- [MODELS.md](MODELS.md) — model backends and credentials
- [CONFIGURATION.md](CONFIGURATION.md#validation-rules) — validation rules
- [SAFETY.md](SAFETY.md) — approvals and the safety gate
- [LICENSING.md](LICENSING.md) — License Keys
- [KV-CACHE.md](KV-CACHE.md#5-verifying-and-diagnosing) — diagnosing slow turns
