// Copyright (c) 2026 Akira Kudo. All rights reserved.
// Licensed under the kudos-dotnet-agent End User License Agreement. See LICENSE in the repository root.

using Kudos.Agent.Config;
using Kudos.Agent.Llm;
using Kudos.Agent.Safety;
using Kudos.Agent.Workflows;
using Microsoft.Extensions.AI;
using MultiAgentHost;

// usage:
//   MultiAgentHost subagent|fanout --demo                        scripted model (no GPU / network)
//   MultiAgentHost subagent|fanout --config <kudos-agent.json>   models[0] of the config (in-process Qwen or a remote model)
//   add --yes to approve every operation without asking, and "<instruction>" to change the task
//   add --license <file> to use that License Key (otherwise the one stored with `Host.Cli license set`);
//   fanout opens several sessions at once — a key with maxConcurrent 1 (development / Evaluation) allows one
var mode = args.FirstOrDefault(a => a is "subagent" or "fanout");
var demo = args.Contains("--demo");
var autoApprove = args.Contains("--yes");
var configIndex = Array.IndexOf(args, "--config");
var configPath = configIndex >= 0 && configIndex + 1 < args.Length ? args[configIndex + 1] : null;
var licenseIndex = Array.IndexOf(args, "--license");
var instruction = args.Where((a, i) => !a.StartsWith("--", StringComparison.Ordinal) && a != mode && (configIndex < 0 || i != configIndex + 1) && (licenseIndex < 0 || i != licenseIndex + 1)).LastOrDefault();

if (mode is null || (!demo && configPath is null))
{
    Console.Error.WriteLine("usage: MultiAgentHost subagent|fanout --demo | --config <kudos-agent.json> [\"<instruction>\"] [--yes] [--license <file>]");
    return 2;
}

// Every session needs a License Key (docs/LICENSING.md) — see samples/MinimalHost/Program.cs
if (licenseIndex >= 0 && licenseIndex + 1 < args.Length)
{
    Kudos.Agent.Licensing.KudosLicensing.UseLicense(File.ReadAllText(args[licenseIndex + 1]));
}

var config = configPath is null
    ? new KudosAgentConfig { Models = [new ModelConfig { Id = "demo" }], Skills = new SkillsConfig { Enabled = false } }
    : KudosAgentConfig.Load(configPath);

IChatClient client;
if (demo)
{
    client = new DemoChatClient();
}
else
{
    LlamaSharpChatClientProvider.Register();
    RemoteChatClientProvider.Register();
    client = await ChatClientFactory.CreateAsync(config.PrimaryModel);
}

var consoleLock = new SemaphoreSlim(1, 1);

async Task<bool> Approve(SafetyOperation operation)
{
    // sub-agents and fan-out participants may ask at the same time — one prompt at a time
    await consoleLock.WaitAsync();
    try
    {
        var who = operation.AgentPath is null ? "" : $" (agent {operation.AgentPath})";
        Console.WriteLine($"approve {operation.Kind}{who}: {operation.Command ?? operation.Path}?{(autoApprove ? " (auto)" : " [y/N]")}");
        return autoApprove || string.Equals(Console.ReadLine()?.Trim(), "y", StringComparison.OrdinalIgnoreCase);
    }
    finally
    {
        consoleLock.Release();
    }
}

static void Seed(string workDir)
{
    File.WriteAllText(Path.Combine(workDir, "alpha.txt"), "one two\nthree\nfour five six\n");
    File.WriteAllText(Path.Combine(workDir, "beta.txt"), "seven\neight nine\n");
}

var resultsRoot = Path.Combine(Directory.GetCurrentDirectory(), "results");
try
{
    if (mode == "subagent")
    {
        var answer = await MultiAgentHostApp.RunSubAgentAsync(
            client,
            config,
            resultsRoot,
            instruction ?? "Use the scripter sub-agent to count the lines of each .txt file in the work directory, then answer with a Markdown table (file | lines).",
            Approve,
            Console.Out,
            Seed);
        return answer is null ? 1 : 0;
    }

    var result = await MultiAgentHostApp.RunFanOutAsync(
        client,
        config,
        resultsRoot,
        instruction ?? "Measure the .txt files in the work directory.",
        Approve,
        Console.Out,
        Seed);
    return result.Status == WorkflowStatus.Failed ? 1 : 0;
}
catch (Kudos.Agent.Licensing.LicenseException ex)
{
    // no session without a valid License Key (missing, expired, level, concurrent sessions — ex.Failure says which)
    Console.Error.WriteLine($"license: {ex.Message}");
    return 3;
}
