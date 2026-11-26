// Copyright (c) 2026 Akira Kudo. All rights reserved.
// Licensed under the kudos-dotnet-agent End User License Agreement. See LICENSE in the repository root.

using Kudos.Agent.Agents;
using Kudos.Agent.Config;
using Kudos.Agent.Safety;
using Kudos.Agent.Workflows;
using Microsoft.Extensions.AI;

namespace MultiAgentHost;

/// <summary>
/// Two multi-agent hosts in a few lines each, using only the SDK's public API:
/// <see cref="RunSubAgentAsync"/> — the model delegates to a sub-agent tool (<c>agent_scripter</c>);
/// <see cref="RunFanOutAsync"/> — three sessions measure the same files in parallel and one table joins their answers.
/// </summary>
public static class MultiAgentHostApp
{
    public const string ScripterDescription =
        "Writes and runs Python scripts in the work directory and returns their output. Tell it exactly what to compute and in which format to answer.";

    /// <summary>The fan-out participants: name → role (each answers with "file&lt;TAB&gt;number" lines).</summary>
    public static readonly IReadOnlyList<(string Name, string Instructions)> Measurements =
    [
        ("lines", "Count the lines of each .txt file in the work directory with a Python script (execute_code). Answer only with one line per file: <file name><TAB><number of lines>."),
        ("words", "Count the words (whitespace-separated) of each .txt file in the work directory with a Python script (execute_code). Answer only with one line per file: <file name><TAB><number of words>."),
        ("chars", "Count the characters of each .txt file in the work directory with a Python script (execute_code) — len() of the whole file content read as text, newlines included. Answer only with one line per file: <file name><TAB><number of characters>."),
    ];

    /// <summary>
    /// A session with the sub-agent <c>scripter</c>: the model hands the scripting to it and answers with the result.
    /// The scripter's approval requests reach <paramref name="approve"/> with <see cref="SafetyOperation.AgentPath"/> set.
    /// </summary>
    public static async Task<string?> RunSubAgentAsync(
        IChatClient chatClient,
        KudosAgentConfig config,
        string resultsRoot,
        string instruction,
        Func<SafetyOperation, Task<bool>> approve,
        TextWriter output,
        Action<string>? prepareWorkDirectory = null,
        CancellationToken cancellationToken = default)
    {
        await using var session = await new AgentSessionBuilder()
            .UseConfig(config)
            .UseChatClient(chatClient)
            .UseResultsDirectory(resultsRoot)
            .UseApprovalHandler(approve)
            .AddSubAgent("scripter", ScripterDescription, options: new SubAgentOptions { Timeout = TimeSpan.FromMinutes(10) })
            .BuildAsync(cancellationToken);

        output.WriteLine($"session {session.SessionId} — workdir {session.Layout.WorkDir}");
        prepareWorkDirectory?.Invoke(session.Layout.WorkDir);

        string? answer = null;
        await foreach (var evt in session.RunTurnEventsAsync(instruction, cancellationToken))
        {
            switch (evt)
            {
                case AgentSubAgentStarted started:
                    output.WriteLine($"[sub-agent] {started.Agent} started (session {started.ChildSessionId})");
                    break;
                case AgentSubAgentCompleted completed:
                    output.WriteLine($"[sub-agent] {completed.Agent} {completed.Status}");
                    break;
                case AgentToolCallStarted call when !call.Name.StartsWith("agent_", StringComparison.Ordinal):
                    output.WriteLine($"[tool] {call.Name}");
                    break;
                case AgentTurnCompleted done:
                    answer = done.FinalText;
                    output.WriteLine(answer);
                    break;
                case AgentTurnFailed failed:
                    output.WriteLine($"[error] {failed.Message}");
                    break;
            }
        }

        return answer;
    }

    /// <summary>
    /// Three sessions (lines / words / characters) run in parallel, each on its own copy of the files
    /// (<paramref name="prepareWorkDirectory"/> seeds every work directory); their answers are joined into one table. The
    /// run is recorded in a coordinator session (<c>workflow-*</c> events).
    /// </summary>
    public static async Task<AgentWorkflowResult> RunFanOutAsync(
        IChatClient chatClient,
        KudosAgentConfig config,
        string resultsRoot,
        string instruction,
        Func<SafetyOperation, Task<bool>> approve,
        TextWriter output,
        Action<string>? prepareWorkDirectory = null,
        CancellationToken cancellationToken = default)
    {
        AgentSessionBuilder Builder(KudosAgentConfig? sessionConfig = null) => new AgentSessionBuilder()
            .UseConfig(sessionConfig ?? config)
            .UseChatClient(chatClient)
            .UseResultsDirectory(resultsRoot)
            .UseApprovalHandler(approve);

        // The in-process model with several sequences (models[].config.seqMax > 1): each
        // participant gets its own slot (= sequence), so their model calls run in parallel instead of taking turns.
        KudosAgentConfig ParticipantConfig(int index)
        {
            var model = config.Models[0];
            var policy = config.SubAgentPolicy;
            var slotCount = model.Config.SeqMax - 1;   // slots 1 … seqMax-1 (0 stays free)
            if (model.Type != ModelType.LlamaCpp || !policy.Slots.Enabled || slotCount - 1 < policy.MaxConcurrent)
            {
                return config;
            }

            var parent = 1 + index % slotCount;
            var others = Enumerable.Range(1, slotCount).Where(s => s != parent).ToList();
            return config with { SubAgentPolicy = policy with { Slots = policy.Slots with { Parent = parent, SubAgents = others } } };
        }

        // the coordinator only records the run (its rollout gets workflow-start / -step / -end)
        await using var coordinator = await Builder().UseTask("fan-out coordinator").BuildAsync(cancellationToken);
        output.WriteLine($"coordinator {coordinator.SessionId}");

        var sessions = new List<CodingAgentSession>();
        try
        {
            var participants = new List<WorkflowParticipant>();
            foreach (var (name, instructions) in Measurements)
            {
                var session = await Builder(ParticipantConfig(participants.Count)).UseTask($"fan-out participant {name}").BuildAsync(cancellationToken);
                sessions.Add(session);
                prepareWorkDirectory?.Invoke(session.Layout.WorkDir);
                participants.Add(new WorkflowParticipant(name, session) { Instructions = instructions });
            }

            var workflow = AgentWorkflows.Concurrent(
                participants,
                (_, results, _) => ValueTask.FromResult(JoinTable(results)),
                new AgentWorkflowOptions
                {
                    Name = "measure",
                    Recorder = coordinator.Recorder,
                    MaxFanOut = SubAgentPolicy.FromConfig(config.SubAgentPolicy).MaxFanOut,
                    ParticipantTimeout = TimeSpan.FromMinutes(10),
                });
            var result = await workflow.RunAsync(instruction, cancellationToken);
            foreach (var participant in result.Results)
            {
                output.WriteLine($"[{participant.Participant}] {participant.Status} in {participant.DurationMs} ms");
            }

            output.WriteLine(result.Output);
            return result;
        }
        finally
        {
            foreach (var session in sessions)
            {
                await session.DisposeAsync();
            }
        }
    }

    /// <summary>The aggregator: "file&lt;TAB&gt;number" lines of every participant → one Markdown table (file | lines | words | chars).</summary>
    public static string JoinTable(IReadOnlyList<ParticipantResult> results)
    {
        var columns = results.Select(r => r.Participant).ToList();
        var rows = new SortedDictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var result in results)
        {
            foreach (var line in result.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = line.Trim('|', ' ').Split(['\t', '|', ':'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length == 2 && parts[0].EndsWith(".txt", StringComparison.Ordinal) && long.TryParse(parts[1], out _))
                {
                    (rows.TryGetValue(parts[0], out var row) ? row : rows[parts[0]] = [])[result.Participant] = parts[1];
                }
            }
        }

        var table = new System.Text.StringBuilder();
        table.Append("| file | ").Append(string.Join(" | ", columns)).Append(" |\n|---|").Append(string.Concat(columns.Select(_ => "---|"))).Append('\n');
        foreach (var (file, values) in rows)
        {
            table.Append("| ").Append(file).Append(" | ")
                .Append(string.Join(" | ", columns.Select(c => values.TryGetValue(c, out var v) ? v : "?")))
                .Append(" |\n");
        }

        return table.ToString().TrimEnd('\n');
    }
}
