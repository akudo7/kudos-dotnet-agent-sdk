// Copyright (c) 2026 Akira Kudo. All rights reserved.
// Licensed under the kudos-dotnet-agent End User License Agreement. See LICENSE in the repository root.

using Kudos.Agent.Agents;
using Kudos.Agent.Config;
using Kudos.Agent.Safety;
using Microsoft.Extensions.AI;

namespace MinimalHost;

/// <summary>
/// The whole host in one method: assemble a session with <see cref="AgentSessionBuilder"/>, run one turn with
/// <see cref="CodingAgentSession.RunTurnEventsAsync"/>, and print the events. Only the SDK's public API is used.
/// </summary>
public static class MinimalHostApp
{
    public static async Task<string?> RunAsync(
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
            .BuildAsync(cancellationToken);

        output.WriteLine($"session {session.SessionId} — workdir {session.Layout.WorkDir}");
        prepareWorkDirectory?.Invoke(session.Layout.WorkDir);

        string? answer = null;
        await foreach (var evt in session.RunTurnEventsAsync(instruction, cancellationToken))
        {
            switch (evt)
            {
                case AgentToolCallStarted call:
                    output.WriteLine($"[tool] {call.Name}");
                    break;
                case AgentToolCallCompleted:
                    output.WriteLine("[tool] done");
                    break;
                case AgentCompacted:
                    output.WriteLine("[context compacted]");
                    break;
                case AgentGuardTriggered guard:
                    output.WriteLine($"[guard] {guard.Guard.Kind}");
                    break;
                case AgentTurnCompleted completed:
                    answer = completed.FinalText;
                    output.WriteLine(answer);
                    break;
                case AgentTurnFailed failed:
                    output.WriteLine($"[error] {failed.Message}");
                    break;
                case AgentTurnCancelled:
                    output.WriteLine("[cancelled]");
                    break;
            }
        }

        return answer;
    }
}
