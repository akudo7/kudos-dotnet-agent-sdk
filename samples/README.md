# Samples

Two console hosts that use only the public API of Kudos Agent SDK 0.7.0:

| Sample | Shows |
|---|---|
| [MinimalHost](MinimalHost/) | One session: config, model client, approval handler, a turn streamed to the console |
| [MultiAgentHost](MultiAgentHost/) | Sub-agents as tools and a fan-out workflow |

Both reference the SDK as NuGet packages (`Kudos.Agent.Sdk*` 0.7.0). The packages are provided separately (see [Getting the Packages](../README.md#getting-the-packages)); put them in a local folder feed as described in [docs/QUICK-START.md](../docs/QUICK-START.md), then:

```bash
dotnet run --project samples/MinimalHost -- --license <your.license>
```

The walkthrough of each sample is in [docs/EXAMPLES.md](../docs/EXAMPLES.md). The sample code may be viewed, copied and modified for use in your applications ([LICENSE](../LICENSE)).
