# Watchtower for Unity

Exceptions and logs from your game, sent to Watchtower over OTLP/HTTP, with an offline buffer and IL2CPP symbol upload.

Status: 0.x, early. APIs may change between minor versions.

## Install

Package Manager, "Add package from git URL": `https://github.com/watchtowerobservability/watchtower-unity.git`

## Quick start

```csharp
using Watchtower;

WatchtowerSdk.Init(new WatchtowerOptions {
    Key = "<publishable-key>", Endpoint = "https://<your-edge>", Service = "my-game" });
// Debug.LogError / Debug.LogException / unhandled exceptions are captured from here on.
WatchtowerSdk.CaptureException(ex);   // optional, explicit
```

Use a publishable key in the game, never a secret ingest key (Init refuses `wti_` keys).

## Integrations

| Feature | Notes |
| --- | --- |
| Unhandled exceptions, `Debug.LogException` | type, message, stack trace |
| `Debug.LogError/LogWarning/LogAssertion` | warnings and above; `CaptureInfo` adds `Debug.Log` |
| Trace link | `SetTraceContext(traceparent)` stamps trace and span ids on following records |
| Offline buffer | bounded in memory, spilled to disk on quit, sent next launch |
| IL2CPP / native symbols | post-build upload of `.so`, `.dSYM`, `.pdb`, `.sym` |

## Symbol upload

Set `WT_API_URL`, `WT_PROJECT` and a secret `WT_API_KEY` (never in the game) in your build environment;
after a player build the editor uploads symbols for the release (`WT_RELEASE`, default the app version).
Manual run: menu Watchtower, Upload symbols from build folder.

## Configuration

| Option | Env var | Default |
| --- | --- | --- |
| `Key` | `WT_PUBLISHABLE_KEY` | none |
| `Endpoint` | `WT_OTLP_ENDPOINT` | `http://localhost:4318` |
| `Service` | `WT_SERVICE` | `unity-game` |
| `DeploymentEnvironment` | `WT_ENVIRONMENT` | `production` |
| `Release` | `WT_RELEASE` | `Application.version` |
| `BeforeSend` | | redaction/drop hook |

The key is sent as `Authorization: Bearer <key>` and masked in any string output.

## Provenance

Every release artifact carries a GitHub build attestation. Verify one with
`gh attestation verify <file> --repo watchtowerobservability/watchtower-unity`.

## License

Apache-2.0. Source of truth is the Watchtower monorepo; this repository is a published mirror.
