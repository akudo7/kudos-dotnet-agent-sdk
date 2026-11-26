// Copyright (c) 2026 Akira Kudo. All rights reserved.
// Licensed under the kudos-dotnet-agent End User License Agreement. See LICENSE in the repository root.

using Microsoft.Extensions.AI;

namespace MinimalHost;

/// <summary>
/// A scripted stand-in for a model (no GPU, no network) that plays the core use case:
/// 1st call → asks to run a Python script that counts the lines of each file in the work directory;
/// 2nd call → reads the script output from the tool result and answers with a Markdown table.
/// </summary>
public sealed class DemoChatClient : IChatClient
{
    public const string Script =
        "import os\n" +
        "for name in sorted(os.listdir('.')):\n" +
        "    if os.path.isfile(name):\n" +
        "        with open(name, encoding='utf-8') as f:\n" +
        "            print(f'{name}\\t{sum(1 for _ in f)}')\n";

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var result = messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().LastOrDefault();
        if (result is null)
        {
            var call = new FunctionCallContent("call_demo_1", "execute_code", new Dictionary<string, object?>
            {
                ["language"] = "python",
                ["code"] = Script,
            });
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, [call])));
        }

        // the tool result is the ToolResult JSON ({"ok":…,"exitCode":…,"output":"…"}), possibly followed by a
        // context-usage note — take the "output" string and turn "name<TAB>count" lines into table rows
        var match = System.Text.RegularExpressions.Regex.Match(result.Result?.ToString() ?? string.Empty, "\"output\":(\"(?:[^\"\\\\]|\\\\.)*\")");
        var scriptOutput = match.Success ? System.Text.Json.JsonSerializer.Deserialize<string>(match.Groups[1].Value) ?? string.Empty : string.Empty;
        var rows = scriptOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('\t'))
            .Where(parts => parts.Length == 2)
            .Select(parts => $"| {parts[0]} | {parts[1].Trim()} |");
        var table = "| file | lines |\n|---|---|\n" + string.Join("\n", rows);
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, table)));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (var update in response.ToChatResponseUpdates())
        {
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
