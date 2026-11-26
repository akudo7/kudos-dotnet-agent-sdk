# License Keys

Every session the SDK creates needs a valid **License Key** — an Evaluation key (30 days) or a commercial key from the Licensor (see [../LICENSE](../LICENSE)). Without one, `AgentSessionBuilder.BuildAsync` (and `CodingAgentSession.CreateAsync`) throws `LicenseException` and creates nothing: no session directory, no model call, no MCP server.

The check is offline. A License Key is a one-line JSON document signed by the Licensor (ECDSA P-256); the SDK verifies the signature with the public key built into the package. Nothing is sent anywhere.

## Contents

1. [What a Key Contains](#what-a-key-contains)
2. [Installing a Key](#installing-a-key)
3. [Levels](#levels)
4. [Concurrent Sessions](#concurrent-sessions)
5. [Errors](#errors)
6. [Integrity Check](#integrity-check)

---

## What a Key Contains

```json
{"exp":"2027-12-31","iat":1795674871,"iss":"kudos-ai","jti":"…","level":"professional","maxConcurrent":5,"product":"…","sig":"…","sub":"Example Corp"}
```

| Field | Meaning |
|---|---|
| `sub` | The licensee |
| `exp` | The last day the key works (UTC date) |
| `level` | What it allows — see [Levels](#levels) |
| `maxConcurrent` | How many sessions may be open at the same time for one OS user (`0` = no limit) |
| `product` | Optional: the product it was issued for (informational) |
| `jti`, `iat`, `iss` | Key id, issue time, issuer |
| `sig` | The signature over every other field. Changing any field — even whitespace inside a value — makes the key invalid |

Keep the key on one line. Reformatting (indentation, key order) is accepted; changing a value is not.

---

## Installing a Key

The SDK looks in this order and uses the **first key that verifies** (an expired key in the store does not hide a valid one baked into the application):

1. **The OS secret store** — service `kudos-dotnet-agent`, key `license` (macOS Keychain, Linux libsecret, Windows Credential Manager target `kudos-dotnet-agent/license`). This is the key of the machine's user — for example a seat license on an end-user machine.
2. **`AgentSessionBuilder.UseLicense(text)`** — a key for one builder.
3. **`KudosLicensing.UseLicense(text)`** — a key for every session in the process. `KudosLicensing.UseLicense(assembly)` reads it from an embedded resource of a given assembly (a plug-in's own assembly — the entry assembly of a plug-in host is the host application's).
4. **An embedded resource named `kudos-agent.license`** in the application's entry assembly.

Store a key in the OS secret store with the CLI host (the key is verified before it is stored; paste it on stdin):

```bash
Host.Cli license set < my.license
Host.Cli license status      # the key sessions would use, and where it was found
Host.Cli license remove
```

or with the OS tools (`security add-generic-password -a kudos-dotnet-agent -s license -w …` on macOS, `secret-tool store … service kudos-dotnet-agent key license` on Linux, `cmdkey /generic:kudos-dotnet-agent/license /user:kudos-dotnet-agent /pass:…` on Windows). `Host.Cli` also takes a key for one run from the environment variable `KUDOS_LICENSE`.

Bake a key into an application:

```csharp
using Kudos.Agent.Licensing;

KudosLicensing.UseLicense(File.ReadAllText("kudos-agent.license"));   // throws LicenseException if invalid or expired
// or per builder:
var session = await new AgentSessionBuilder().UseLicense(licenseText) /* … */ .BuildAsync();
```

```xml
<!-- or as an embedded resource of the application (entry assembly) -->
<ItemGroup>
  <EmbeddedResource Include="kudos-agent.license" LogicalName="kudos-agent.license" />
</ItemGroup>
```

Check a key without using it: `KudosLicensing.Parse(text)` returns a `KudosLicense` (`Subject`, `Expires`, `Level`, `MaxConcurrent`, …) or throws. `KudosLicensing.Resolve()` returns the key sessions would use now (its `Source` says where it was found).

---

## Levels

Each level includes the one before it.

| Level | Sessions (default `maxConcurrent`) | Allows |
|---|---|---|
| `development` | 1 | A model on this machine — the in-process model (`LlamaCpp`) or an OpenAI-compatible server on a loopback address — the built-in tools and skills. **Evaluation keys are this level.** |
| `professional` | 5 | + remote models (`Anthropic`, `OpenAI`, `AzureOpenAI`, `Bedrock`, `Gemini`, an `OpenAICompatible` server that is not local) and MCP servers |
| `enterprise` | 20 | + A2A (the `a2a_<name>` tools and the A2A server) and image / PDF input (attachments and the `view_image` tool) |
| `unlimited` | no limit | everything |

A model is remote when `ChatClientFactory.IsRemote(models[0])` says so, or when a client passed to `UseChatClient` reports a non-loopback endpoint through `ChatClientMetadata`. A sub-agent with its own model is checked the same way. Under a level without image input, `view_image` is not offered and a turn with attachments throws `LicenseException`.

---

## Concurrent Sessions

A key with `maxConcurrent` > 0 allows that many open sessions at the same time for one OS user, across every process. Each session takes a seat when it is created and gives it back when it is disposed (`await using`) — or when its process ends. Sub-agents run in their parent's seat. When every seat is taken, `BuildAsync` throws `LicenseException` with `Failure = ConcurrencyLimit`; it does not wait. Seats are lock files under `<temp>/kudos-agent-<user>/licenses/`.

---

## Errors

`LicenseException.Failure` says why:

| `Failure` | Cause | Fix |
|---|---|---|
| `Missing` | No key on any path | Install a key (above) |
| `Malformed` | Not a License Key (not JSON, a field missing or of the wrong type) | Copy the whole key, on one line |
| `InvalidSignature` | The key was changed, or was not issued for this build of the SDK | Use the key exactly as issued |
| `Expired` | Past its `exp` date | Get a renewed key |
| `FeatureNotLicensed` | The session uses a feature outside the key's level (the message names it) | A higher level, or a local model / no MCP |
| `ConcurrencyLimit` | `maxConcurrent` sessions are already open | Dispose a session first |
| `IntegrityFailure` | An SDK assembly differs from the one that was released | Restore the package files (see below) |

When several keys were found and none worked, the message lists each source with its reason.

---

## Integrity Check

The released packages record the SHA-256 of each `Kudos.Agent.Sdk*.dll`, signed by the Licensor. Before a session is created, the SDK compares the loaded SDK assemblies with that record and refuses to run (`IntegrityFailure`) when one has been modified. The assemblies must be loaded from files: **single-file publishing is not supported** (there is no file to compare). The check is meant to detect tampering; it does not stop someone who also rewrites the check itself — modifying the SDK or circumventing the license check is not permitted by the LICENSE.

---

## See Also

- [SECRETS.md](SECRETS.md) — the OS secret store that holds the key
- [QUICK-START.md](QUICK-START.md) — installing a key with the packages
- [API.md](API.md#kudosagentlicensing) — the `Kudos.Agent.Licensing` types
- [TROUBLESHOOTING.md](TROUBLESHOOTING.md#license-keys) — license errors
