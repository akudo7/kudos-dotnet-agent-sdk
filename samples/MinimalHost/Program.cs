// Copyright (c) 2026 Akira Kudo. All rights reserved.
// Licensed under the kudos-dotnet-agent End User License Agreement. See LICENSE in the repository root.

using Kudos.Agent.Config;
using Kudos.Agent.Llm;
using Kudos.Agent.Safety;
using Microsoft.Extensions.AI;
using MinimalHost;

// usage:
//   MinimalHost --demo scripted model (no GPU / network) — the core use case end to end
//   MinimalHost --config <kudos-agent.json> "<instruction>"   models[0] of the config (in-process Qwen or a remote model)
//   add --yes to approve every operation without asking
//   add --license <file> to use that License Key (otherwise the one stored with `Host.Cli license set`)
var demo = args.Contains("--demo");
var autoApprove = args.Contains("--yes");
var configIndex = Array.IndexOf(args, "--config");
var configPath = configIndex >= 0 && configIndex + 1 < args.Length ? args[configIndex + 1] : null;
var licenseIndex = Array.IndexOf(args, "--license");
var instruction = args.Where((a, i) => !a.StartsWith("--", StringComparison.Ordinal) && (configIndex < 0 || i != configIndex + 1) && (licenseIndex < 0 || i != licenseIndex + 1)).LastOrDefault()
    ?? "Count the lines of each file in the current directory and report them as a Markdown table.";

if (!demo && configPath is null)
{
    Console.Error.WriteLine("usage: MinimalHost --demo | --config <kudos-agent.json> \"<instruction>\" [--yes] [--license <file>]");
    return 2;
}

// Every session needs a License Key (docs/LICENSING.md): the OS secret store is searched first; a key the application
// carries itself is "baked" in with KudosLicensing.UseLicense (or AgentSessionBuilder.UseLicense).
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
    // the core SDK has no model client — register the in-process and remote providers, then create models[0]
    LlamaSharpChatClientProvider.Register();
    RemoteChatClientProvider.Register();
    client = await ChatClientFactory.CreateAsync(config.PrimaryModel);
}

Task<bool> Approve(SafetyOperation operation)
{
    Console.WriteLine($"approve {operation.Kind}: {operation.Command ?? operation.Path}?{(autoApprove ? " (auto)" : " [y/N]")}");
    if (!string.IsNullOrEmpty(operation.Detail))
    {
        Console.WriteLine(operation.Detail);
    }

    return Task.FromResult(autoApprove || string.Equals(Console.ReadLine()?.Trim(), "y", StringComparison.OrdinalIgnoreCase));
}

string? answer;
try
{
    answer = await MinimalHostApp.RunAsync(
        client,
        config,
        resultsRoot: Path.Combine(Directory.GetCurrentDirectory(), "results"),
        instruction,
        Approve,
        Console.Out,
        prepareWorkDirectory: demo
            ? workDir =>
            {
                File.WriteAllText(Path.Combine(workDir, "one.txt"), "l1\nl2\nl3\n");
                File.WriteAllText(Path.Combine(workDir, "two.txt"), "x\ny\n");
            }
            : null);
}
catch (Kudos.Agent.Licensing.LicenseException ex)
{
    // no session without a valid License Key (missing, expired, level, concurrent sessions — ex.Failure says which)
    Console.Error.WriteLine($"license: {ex.Message}");
    return 3;
}

return answer is null ? 1 : 0;
