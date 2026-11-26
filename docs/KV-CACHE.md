# KV Cache Reuse (In-Process Models)

This page explains how `Kudos.Agent.Sdk.LlamaSharp` avoids re-processing the whole conversation on every
model call, which prompt shapes keep that working, and how to find out why a run has become slow. It applies
to `ModelType.LlamaCpp` (the in-process LLaMASharp model). Remote providers are covered at the end.

If you only use `AgentSessionBuilder` and the built-in tools, the SDK already follows every rule below; the
page matters when you add middleware, inject context, write your own host loop around
`LlamaSharpChatClient`, or diagnose slow turns.

## Contents

1. [1. Why It Matters](#1-why-it-matters)
2. [2. The Append-Only Protocol](#2-the-append-only-protocol)
3. [3. Incremental vs Full Evaluation](#3-incremental-vs-full-evaluation)
4. [4. Prompt-Layout Rules for Hosts](#4-prompt-layout-rules-for-hosts)
5. [5. Verifying and Diagnosing](#5-verifying-and-diagnosing)
6. [6. Remote Providers](#6-remote-providers)

---

## 1. Why It Matters

The agent loop re-sends the **entire conversation** to the model on every call: system prompt, tool
definitions, every earlier message and every tool result. A local model has to turn all of those tokens into
its key/value cache (the "prefill") before it can generate a single token. Re-doing that on every call makes a
run quadratic in its length: before incremental reuse existed, an 18-call run re-processed more than 45,000
tokens and took over ten minutes.

`StatefulLlamaExecutor` keeps one LLaMASharp context alive across calls. When the new prompt is the old prompt
plus something appended at the end, it decodes **only the appended tokens**. A prompt-layout mistake does not
raise an error; it silently turns every call back into a full re-evaluation.

---

## 2. The Append-Only Protocol

After every successful call the KV cache holds exactly:

```
[full rendered prompt][generation prompt][the model's own raw output tokens + end-of-message token]
```

On the next call `LlamaSharpChatClient` checks (with `IncrementalPlanResolver`) whether the new message list
is an extension of the previous one. If it is, it renders only the new messages and decodes

```
"\n" + RenderDelta(new user / tool messages) + GenerationPrompt + "\n"
```

on top of the existing cache (`StatefulLlamaExecutor.ContinueAsync`). The rendered prompt looks like this
(Qwen chat template, rendered by `QwenPromptRenderer`):

```
<|im_start|>system
[reasoning instructions]

[tools block]

[instructions]<|im_end|>                     ┐
<|im_start|>user                              │ stable prefix: already in the KV cache
Count the lines of every file<|im_end|>       │
<|im_start|>assistant                         │
<think>                                       │
...raw tokens the model generated...<|im_end|>┘ kept as raw tokens, never re-rendered
<|im_start|>user                              ┐
<tool_response>                               │ delta: the only part decoded on this call
{"ok":true,"output":"..."}                    │
</tool_response><|im_end|>                    │
<|im_start|>assistant                         │
<think>                                       ┘
```

Key properties:

- **The model's own reply is never re-rendered.** Its raw tokens stay in the cache, so there is no risk that
  re-tokenizing the text would produce different tokens. When the agent loop hands the reply back as the first
  new message, the resolver only checks that it is unchanged.
- **The delta is only user and tool messages.** `QwenPromptRenderer.RenderDelta` throws for any other role,
  and the resolver falls back to a full evaluation before that can happen.
- **Generation stops after the end-of-message token is decoded into the cache**, so the seam between the
  cached reply and the next delta matches the template exactly.
- **There is no partial rollback.** The reference model architecture (qwen35) is a hybrid whose Gated
  DeltaNet layers keep a recurrent state that cannot be truncated like a plain KV cache. Any mismatch disposes the context,
  creates a fresh one and evaluates the full prompt. With `seqMax` > 1 only that conversation's sequence is
  cleared (its KV cells and its recurrent state) and evaluated again; the other sequences keep their caches.

---

## 3. Incremental vs Full Evaluation

`IncrementalPlanResolver.Resolve` chooses **incremental** only when every condition holds. Otherwise the call
is a **full** evaluation.

| Falls back to full when ... | Why |
|---|---|
| It is the first call of the client, or the previous call failed or was cancelled | No trustworthy cache state |
| The previous generation stopped at `MaxTokens` | The cache ends inside an unclosed reply (no end-of-message token), so a delta would not match the template |
| The configuration fingerprint changed: system prompt (`ChatOptions.Instructions`), the set of tool **names**, or the reasoning effort | The prefix itself changed |
| The new message list does not start with exactly the previous message list | Earlier history was edited, removed or reordered |
| The new list is not longer than the previous one | Nothing was appended (an identical retry is also evaluated from a clean state) |
| The first new message is an assistant message that differs from the client's last reply (text, function calls, call ids or arguments) | The cached raw tokens no longer represent it |
| Any remaining new message is not `user` or `tool` (for example a `system` message) | The delta renderer only supports user and tool blocks |

Messages are compared by a fingerprint of role, text, function calls (name, call id, sorted-JSON arguments),
function results and the SHA-256 of every image or file they carry (the same position with a different image is a
changed prompt). Before decoding, the executor also throws `ContextOverflowException` if the prompt
would exceed the loaded `contextSize`, rather than letting native decoding run into the context boundary.

### Images

With an image projector (`models[].config.mmproj` — [MODELS.md](MODELS.md#images-and-pdfs)) the protocol is the same; an
image is one unit of the cached prompt, identified by its SHA-256:

- The renderer writes each image at the chat template's image position (between `<|vision_start|>` and `<|vision_end|>`)
  and hands the prompt to the executor as text and image segments (`QwenPromptRenderer.RenderSegmentsWithGenerationPrompt`
  / `RenderDeltaSegments`). Without a projector the prompt is the plain text render, unchanged.
- A delta may carry images (an attachment in the new user message, the image of a `view_image` result): they are evaluated
  after the existing cache, and nothing before the delta is evaluated again.
- An image takes its tokens in KV cells (`imageMinTokens`–`imageMaxTokens`) but advances the position in the sequence only
  by its grid size (M-RoPE: a 1,024-token image moves the position by about 32). The executor tracks both
  (`StatefulLlamaExecutor.GetKvUsage`); the context-size checks count cells.
- A full evaluation re-encodes every image in the history, so compaction or a resume costs more in an image-heavy session.

`StatefulLlamaExecutor.GetLastPromptStats(sequence)` returns the cells of the last call's prompt that were reused and
decoded — a turn after an image should reuse everything before its delta.

### MTP speculative decoding

With MTP (`models[].config.mtp` — on by default for a model with an MTP head; [MODELS.md](MODELS.md#mtp-speculative-decoding)) the protocol is unchanged: the cache still
holds exactly the prompt and the tokens the model output, and the next turn decodes only its delta.

- Each generation step decodes the sampled token and a drafted one. A kept draft stays in the cache; a wrong one is
  removed right away (its KV cell and, for hybrid models such as Qwen3.8, the recurrent state — the context keeps one
  snapshot for this), so no cell of a rejected draft is ever left behind.
- The draft model keeps its own cache of the generated tokens. Prompts are not given to it, so the main cache is
  computed exactly as without MTP. With several sequences it keeps one cache per sequence. It is reset together with the
  main cache (full evaluation) and never needs a rollback of its own.
- A call that fails or is cancelled mid-step leaves the cache invalid as before: the next call is a full evaluation.

### Full evaluations the SDK causes on purpose

These are expected and rare. Each costs one full prefill:

| Situation | What happens |
|---|---|
| First call of a session, or the first call after `ResumeFrom` | Nothing cached yet for this history |
| Context compaction | The summary request is a different prompt (full evaluation), and the following agent call starts from rewritten history (full evaluation) |
| Guard nudges, verify / completion re-invokes, tool-budget or breaker aborts, steer messages | The call that carries the one-shot message is incremental; the **following** call no longer contains it, so it no longer extends the previous prompt |
| A cancelled or failed call | The cache state is invalidated |
| A reply cut off at `maxTokens` | The next call cannot continue the unclosed reply |

---

## 4. Prompt-Layout Rules for Hosts

1. **Keep the session configuration constant.** The system prompt, the tool set and the reasoning effort are
   fingerprinted together. Never put turn-varying values (timestamps, counters, task ids, remaining budgets,
   todo state) into instructions, prompt files, `AGENTS.md` content or tool descriptions.
   `SystemPromptBuilder` builds the prompt once per session for exactly this reason.
2. **Keep tool definitions stable in content and order.** The fingerprint covers only the sorted tool
   *names*, but the rendered tools block also contains each description and JSON schema, in list order.
   Changing a description or schema, or reordering tools, without changing a name is **not** detected: the
   cache keeps the old definitions and the model silently works from stale ones. The SDK fixes the tool list
   when the session is built (host tools and MCP tools included); if a definition must change, start a new
   session.
3. **History is append-only.** Never edit, reorder or remove earlier messages. Compaction and resume are the
   only deliberate exceptions.
4. **Per-turn context goes at the tail as a new user message.** Not into the system prompt, and not as a
   `system` message in the middle of the conversation (a `system` message in the delta forces a full
   evaluation). Use the next user turn, or `session.Steering.Enqueue(text)` for mid-turn input.
5. **Hand the model's reply back unchanged.** Middleware between the agent and `LlamaSharpChatClient` must not
   rewrite, strip or annotate assistant messages, including their tool-call arguments. A `DelegatingChatClient`
   that only observes (logging, timing) is safe.
6. **One conversation per sequence.** With `seqMax` 1 (the default) a `LlamaSharpChatClient` holds one cache:
   interleaving two sessions on the same client makes every switch a full evaluation, so serialize access (the
   SDK does). With `models[].config.seqMax` > 1 the client keeps one cache per sequence and picks the sequence
   from the request's slot (`subAgentPolicy.slots`; `LlamaServerSlot.OptionKey` in `ChatOptions.AdditionalProperties`
   when you call the client yourself) — give each concurrent conversation its own slot. Calls on one slot run
   one at a time. A second client loads the model weights again.
7. **Leave enough output room.** A reply cut off at `maxTokens` makes the next call a full evaluation. The
   default `maxTokens` is 32768.
8. **Keep the reasoning effort fixed.** It is part of the fingerprint and of the system block. Clients created
   through `ChatClientFactory` use the default `"xhigh"`; to choose another value, construct the client with
   `LlamaSharpChatClient.CreateAsync(..., new LlamaSharpChatClientOptions { ReasoningEffort = "low" })`.

---

## 5. Verifying and Diagnosing

### Time each model call

Turn 2 and later of the same session should cost roughly "new tokens + output", far less than turn 1. Wrap the
client in an observing `DelegatingChatClient` (it passes messages through unchanged, so it does not affect
reuse):

```csharp
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

sealed class TimingChatClient(IChatClient inner, Action<string> log) : DelegatingChatClient(inner)
{
    private int _calls;

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = messages.ToList();
        var sw = Stopwatch.StartNew();
        try { return await base.GetResponseAsync(list, options, cancellationToken); }
        finally { log($"llm call #{++_calls}: {list.Count} messages, {sw.ElapsedMilliseconds} ms"); }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var list = messages.ToList();
        var sw = Stopwatch.StartNew();
        await foreach (var update in base.GetStreamingResponseAsync(list, options, cancellationToken))
        {
            yield return update;
        }
        log($"llm call #{++_calls}: {list.Count} messages, {sw.ElapsedMilliseconds} ms");
    }
}

// builder.UseChatClient(new TimingChatClient(llamaClient, Console.Error.WriteLine));
```

A call that is about as slow as the first one, while the message count only grew by one or two, was a full
evaluation. One full call right after a `compaction` or `guard` event is expected (section 3).

### Find the first difference

Render the prompts of two consecutive calls and compare them. `QwenPromptRenderer` is public in the core
package:

```csharp
string before = QwenPromptRenderer.RenderWithGenerationPrompt(previousMessages, instructions, tools, "xhigh");
string after  = QwenPromptRenderer.RenderWithGenerationPrompt(currentMessages,  instructions, tools, "xhigh");
int i = 0;
while (i < before.Length && i < after.Length && before[i] == after[i]) i++;
Console.WriteLine($"first difference at {i}: ...{after.Substring(Math.Max(0, i - 80), Math.Min(160, after.Length - Math.Max(0, i - 80)))}...");
```

Everything before the assistant reply of the previous call must be identical. Where the first difference
lands tells you which rule was broken:

| First difference in ... | Likely cause |
|---|---|
| The system block | Turn-varying text in the prompt, prompt files or `AGENTS.md` (rule 1); a tool description, schema or order changed (rule 2) |
| An earlier user or tool message | History edited, trimmed or reordered by your code (rule 3) |
| The previous assistant reply | Middleware rewrote the reply (rule 5) |
| A `<|im_start|>system` block after the first message | A `system` message injected mid-conversation (rule 4), or a compaction summary (expected) |

Also check whether the previous call was cancelled, failed, or stopped at `maxTokens`.

### Context size

The whole prompt must fit the `contextSize` the model was loaded with. If it does not,
`ContextOverflowException` is thrown before decoding. The SDK's compaction only prevents this if its window
matches the loaded size; see [SESSIONS.md](SESSIONS.md#context-compression) and
[TROUBLESHOOTING.md](TROUBLESHOOTING.md#compaction-runs-on-every-model-call-or-never).

### For contributors

The protocol is pinned by tests in the repository; run them after changing anything that shapes the prompt
(renderer, `LlamaSharpChatClient`, executor, resolver, compaction, steering, guards, the base prompt):

```bash
dotnet test tests/Kudos.Agent.Sdk.Tests --filter "FullyQualifiedName~QwenPromptRenderer|FullyQualifiedName~GoldenTemplate|FullyQualifiedName~TurnLoop"
dotnet test tests/Kudos.Agent.Sdk.LlamaSharp.Tests --filter "FullyQualifiedName~IncrementalPlanResolver"
```

- `QwenPromptRendererDeltaTests`: the full render ends with `RenderDelta(delta) + GenerationPrompt + "\n"`.
- `GoldenTemplateTests`: byte equality with the Jinja chat template.
- `IncrementalPlanResolverTests`: one test per row of the table in section 3.
- `QwenPromptRendererSegmentTests`: with images, the full render ends with the delta segments, and a prompt without
  images is exactly the text render.
- `StatefulLlamaExecutorImageTests` (needs a real model and its projector — `KUDOS_TEST_GGUF` / `KUDOS_TEST_MMPROJ`): a
  delta with an image ends at the same position and cell count as a full evaluation of the same prompt.

---

## 6. Remote Providers

Remote clients do not use this mechanism, but the same layout pays off there too: the Anthropic client enables
automatic prompt caching of the growing conversation prefix, and OpenAI-compatible servers such as llama-server
reuse a cached prefix when the new request extends the previous one. Rules 1 to 5 keep those caches warm.
See [MODELS.md](MODELS.md).

---

## See Also

- [MODELS.md](MODELS.md) — the `LlamaCpp` backend and remote providers
- [SESSIONS.md](SESSIONS.md) — context compaction
- [TOOLS.md](TOOLS.md) — why tool definitions are fixed per session
- [TROUBLESHOOTING.md](TROUBLESHOOTING.md#performance) — slow turns
