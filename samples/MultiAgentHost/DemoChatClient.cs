// Copyright (c) 2026 Akira Kudo. All rights reserved.
// Licensed under the kudos-dotnet-agent End User License Agreement. See LICENSE in the repository root.

using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace MultiAgentHost;

/// <summary>
/// A scripted stand-in for a model (no GPU, no network) that plays both multi-agent flows:
/// a session that has the tool <c>agent_scripter</c> delegates the counting to it and turns its answer into a table;
/// any other session (the scripter, or a fan-out participant) runs a Python script for the metric its input asks for
/// (lines, words or characters of each .txt file) and answers with the script output.
/// </summary>
public sealed partial class DemoChatClient : IChatClient
{
    public const string ScripterQuery =
        "Count the lines of each .txt file in the work directory. Answer only with one line per file: <file name><TAB><number of lines>.";

    public static string Script(string metric) =>
        "import os\n" +
        "for name in sorted(os.listdir('.')):\n" +
        "    if name.endswith('.txt'):\n" +
        "        with open(name, encoding='utf-8') as f:\n" +
        metric switch
        {
            "words" => "            print(f'{name}\\t{len(f.read().split())}')\n",
            "chars" => "            print(f'{name}\\t{len(f.read())}')\n",
            _ => "            print(f'{name}\\t{sum(1 for _ in f)}')\n",
        };

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = messages.ToList();
        var lastUser = list.FindLastIndex(m => m.Role == ChatRole.User);
        var input = lastUser >= 0 ? list[lastUser].Text : string.Empty;
        var result = list.Skip(lastUser + 1).SelectMany(m => m.Contents).OfType<FunctionResultContent>().LastOrDefault();
        var isParent = options?.Tools?.Any(t => t.Name == "agent_scripter") == true;

        if (isParent)
        {
            return Task.FromResult(result is null
                ? Call("agent_scripter", new() { ["query"] = ScripterQuery })
                : Answer(ToTable(Strip(result.Result?.ToString()))));
        }

        var metric = input.Contains("Count the words", StringComparison.Ordinal) ? "words"
            : input.Contains("Count the characters", StringComparison.Ordinal) ? "chars"
            : "lines";
        return Task.FromResult(result is null
            ? Call("execute_code", new() { ["language"] = "python", ["code"] = Script(metric) })
            : Answer(ScriptOutput(result.Result?.ToString()).Trim()));
    }

    private static ChatResponse Call(string name, Dictionary<string, object?> arguments) =>
        new(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent($"call_{Guid.NewGuid():N}", name, arguments)]));

    private static ChatResponse Answer(string text) => new(new ChatMessage(ChatRole.Assistant, text));

    /// <summary>Drops the context-usage note the SDK appends to tool results.</summary>
    private static string Strip(string? text) =>
        text is null ? string.Empty
        : text.IndexOf("\n[context", StringComparison.Ordinal) is var i and >= 0 ? text[..i] : text;

    /// <summary>The "output" string of an execute_code result ({"ok":…,"exitCode":…,"output":"…"}).</summary>
    private static string ScriptOutput(string? toolResult)
    {
        var match = OutputPattern().Match(toolResult ?? string.Empty);
        return match.Success ? JsonSerializer.Deserialize<string>(match.Groups[1].Value) ?? string.Empty : string.Empty;
    }

    private static string ToTable(string lines)
    {
        var rows = lines
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('\t'))
            .Where(parts => parts.Length == 2)
            .Select(parts => $"| {parts[0].Trim()} | {parts[1].Trim()} |");
        return "| file | lines |\n|---|---|\n" + string.Join("\n", rows);
    }

    [GeneratedRegex("\"output\":(\"(?:[^\"\\\\]|\\\\.)*\")")]
    private static partial Regex OutputPattern();

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
