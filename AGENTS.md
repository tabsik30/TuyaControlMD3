# Agent guidance

This file must be kept up to date. When a rule here stops matching reality, or a new rule emerges from
work in this repository, update this file as part of that change rather than leaving it to drift.

This repository is the **starting point for a Macro Deck 3 out-of-process plugin**, and it is
simultaneously the content of the `dotnet new macrodeck-plugin` template package. The plugin under
`src/TuyaControl/` is deliberately minimal: one integration that starts, registers and
does nothing else. There is no sample code to delete - add what the plugin actually needs.

Worked examples of every capability live in the
[sample plugins repository](https://github.com/Macro-Deck-App/Macro-Deck-Sample-Plugins), not here.
Read from there rather than growing this template.

[README.md](README.md) is the human-facing guide: how to build, how to run against a real host, how to
pack. This file is the rule set for writing the plugin. Read it before changing code. The template
package itself is covered by
[packaging/README.md](https://github.com/Macro-Deck-App/Macro-Deck-Plugin-Template/blob/main/packaging/README.md).

## Orientation

```
src/TuyaControl/
  Program.cs             builder chain - three lines and a RunAsync
  manifest.json          identity, icon, per-platform entrypoints
  PluginIntegration.cs   the integration: lifecycle and capability opt-ins
  Assets/icon.svg        the icon the manifest declares
  Properties/launchSettings.json   the single real-host debug profile
tests/TuyaControl.Tests/
  PluginIntegrationTests.cs   the plugin builds and initializes
```

The template repository carries two more directories that a generated plugin does not:
`.template.config/` (the `dotnet new` definition) and `packaging/` (the template package project, kept
out of the solution on purpose).

Authoritative upstream documentation, in the
[Macro Deck 3 repository](https://github.com/Macro-Deck-App/Macro-Deck-3/tree/main/docs/plugin-development):
`sdk-reference.md` (every contract type), `plugin-hosting.md` (builder, registration modes, manifest,
artifact, environment variables), `capability-parity.md` (what behaves differently out of process),
`analyzers.md`, `conformance.md`, `testing-plugins.md`, `cli.md`. When a question is about SDK behaviour
rather than this template's own code, look there rather than guessing.

## Before you start on a fresh plugin

`dotnet new macrodeck-plugin -n <Name> --pluginId <id> --pluginName "<Display name>"` sets the identity
for you. A repository *cloned* from this template still carries the template's, so fix that first, in
one change:

1. `manifest.json` - `id` (reverse-domain, lowercase, at least two dot-joined kebab segments, e.g.
   `com.example.my-plugin`), `name`, `version`, `description`.
2. Rename the project, the test project, the solution file and the namespace.
3. Replace `Assets/icon.svg`. The manifest's `icon` path is the single source of truth and the host
   reads that file directly - there is no icon code to change.

`MacroDeck.Plugin.Analyzers` is already referenced with `PrivateAssets="all"` - 13 compile-time
diagnostics that catch most of the mistakes below while you type, plus the `[MacroDeckSdkUsage]`
attribute the host reads to report real deprecation usage instead of inferring it. Keep it.

## The rules that make a plugin clean

### Identity

- A plugin's identity is `manifest.json` and nothing else. `IPluginIntegration` carries no `Id`, `Name`,
  `Version` or `IsInitialized`, and it never implements `IIntegrationIconProvider` - both of those are
  the in-process `IIntegration`'s surface. The builder's old `WithId`/`WithName`/`WithVersion`/
  `WithDescription`/`WithIcon` are gone; do not reintroduce them.
- Ids you write in source are **local ids** - `^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$`, max 64 chars, never
  containing `::`. The host derives the qualified `integrationId::localId` form from your authenticated
  registration. Never build or submit a qualified id yourself.
- **Action ids must be unique across the whole plugin**, not just per integration. In process the key is
  (integration id, action id); over the wire the owner is the plugin. A collision fails `Build()`.
- Event definition ids and variable `DefinitionId`s are persisted in user data. Treat them as a public
  API: renaming one breaks every widget already bound to it.

### The builder and the process

- Leave `Program.cs` shaped as it is: `CreatePlugin(args)` → `.UseMacroDeckLogging()` →
  `.RegisterIntegration<T>()` → `Build()` → `RunAsync()`. It is a real `WebApplication` builder, so
  `IHttpClientFactory`, options binding, hosted services and DI are all available and preferred over
  hand-rolled equivalents. Extra registrations go on `builder.Services` before `Build()`.
- `RegisterIntegration<T>()` is the one door: it also registers the capability handler for every SDK
  interface the integration implements, so you never write a handler for a built-in capability kind. A
  genuinely new kind uses `ICapabilityHandler` + `RegisterCapabilityHandler<T>()`.
- `services.AddMacroDeckIntegration<T>()` is internal to the hosting package now and cannot be called
  from a plugin project. Registering the integration with `services.AddSingleton<T>()` is an analyzer
  warning, not a workaround.
- `Build()` constructs every integration and handler as part of validation, so a constructor must be
  side-effect-free and cheap.
- **Never set your own listener URL.** No `UseUrls`, no `Configuration["urls"]`, no `ASPNETCORE_URLS` in
  `launchSettings.json` or `appsettings.json`. The supervisor binds a port and starts probing
  `GET /_macrodeck/health` before your process starts; overriding it makes the health check fail silently
  and permanently, with nothing surfaced to explain why.
- `/_macrodeck/*` is reserved. Mapping a route under it fails `Build()`.
- The only writable location to rely on is `MACRO_DECK_PLUGIN_DATA_DIRECTORY`. Anything written next to
  the executable lives in an immutable version directory and disappears on the next update or rollback.
- `Build()` reports **every** local problem at once in one `PluginConfigurationException`. Read the whole
  message before fixing anything; do not iterate one error per run.

### Lifecycle

`InitializeAsync` does not run at process start. It is gated on the connection being established, and it
runs again after a non-resume reconnect and whenever the host reports a configuration change. Make it
idempotent and safe to run repeatedly against an already-initialized process.

### Actions

- `ActionResult` must be truthful. `Success()` claims the operation completed. Could not reach the
  provider, refused a permission, handed an unusable value → `Failed(code, message)` with the closest
  `ActionErrorCodes` value. A press that did nothing must never report success.
- A legitimate no-op *is* success: an optional parameter left blank, a repeat count of zero, a setting
  already in the requested state.
- `Accepted` is only for work the provider took but cannot confirm. Where the API *can* confirm, poll
  until it does rather than returning `Accepted`.
- Throwing works (the flow engine records a failure) but the caller only ever sees a generic code - an
  exception message is never sent to a client. Prefer an explicit `Failed`.
- A synchronous executor returns the cached `ActionResult.SucceededTask` rather than allocating.
- **Forward `context.CancellationToken`** into everything you await. Dropping it is MDP3001.
- Parameter visibility (`OnlyWhen`) is presentation only. The host still sends hidden parameters, so
  validate the combination in the executor - never infer anything from a field being hidden.

### Async and concurrency

- No `.Result`, `.Wait()`, `.GetAwaiter().GetResult()` or `Thread.Sleep` anywhere in a type implementing
  `ICapabilityHandler`, `IActionExecutor` or `IConfigFlow` - the rule is whole-type, not just the
  interface methods, because the dispatcher has 32 concurrent invocation slots and a block anywhere
  reachable starves the rest (MDP3002).
- No `async void` on an SDK contract type; an exception there kills the process instead of failing one
  call. The `(object? sender, EventArgs e)` handler shape is the one exception (MDP3003).
- Invocations dispatch **concurrently**, each in its own DI scope. Instance state touched from more than
  one invocation needs its own synchronization - the same discipline as any concurrently invoked ASP.NET
  Core endpoint.
- `ICapabilityInvocationContext` only resolves inside an invocation scope. A singleton must not depend on
  it (MDP4001).

### Fire-and-forget contracts

`IEventPublisher.Publish`, `IUserNotifier.Notify`/`Dismiss` and `IPluginCatalogNotifier.CatalogChanged`
never throw and are safe to call with no live session. Mirror that in your own wrappers: they are called
from websocket callbacks and poll loops where a throw would take the integration's own work down.

The round-trip host callbacks - `context.Variables`, `context.Config`, `context.UserVariables`,
`context.Deck`'s mutating members, `context.Scripts.RunAsync`, `context.Widgets.ApplyAsync` - are the
opposite: real network calls that can throw `HostInvocationException` on rate limiting, timeout or no
connection. Handle them like any networked call. `Deck.GetFolders()`, `Scripts.GetScripts()` and
`Widgets.GetWidgets()` read a host-pushed cache instead and return empty in the short window before the
first push.

### Catalogues go stale - say so

Every synchronous catalog-shaped member (`GetInstances`, `EventDefinitions`, `DeclaredVariables`,
`GetProfiles`, …) is served to the host from a cached `describe`, not a live call. When something outside
a host-initiated invocation changes what a later `describe` would answer - a config value
`InitializeAsync` just read, a device that appeared or vanished - inject `IPluginCatalogNotifier` and call
`CatalogChanged(kind, reason)`. Skipping it leaves the UI disagreeing with the plugin until the next
reconnect. The host describes capabilities concurrently with `InitializeAsync`, so a first describe can
capture a default before the config read finishes.

### Configuration and secrets

- A config flow produces the entries; `InitializeAsync` reads them back through
  `IIntegrationContext.Config`. Keep that division - the flow validates and persists, the integration
  consumes.
- Persist credentials as `ConfigFlowValue.Secret` so they land in the host's encrypted secret store.
  Never write a token to a plain string field, a log line, or a file of your own. Rotating credentials go
  back through `SetSecretAsync`.
- Never run your own OAuth redirect server. Return `ConfigFlowResult.External(url, resumeStepId)` and let
  the host own the redirect and the callback correlation.
- An integration that provides a config flow starts **disabled** until the user completes it. Everything
  else starts enabled.

### Logging

- Log through **Serilog**, not `Microsoft.Extensions.Logging`. Inject Serilog's `ILogger` (and
  `.ForContext<T>()`), or use `IntegrationLog`. `UseMacroDeckLogging()` forwards everything to the host's
  log viewer.
- The host stamps integration identity from the authenticated session - a plugin cannot set its own
  attribution, so do not try.
- Logging is rate-limited and a flood is dropped. For a poll loop that fails repeatedly, use
  `FailureEpisodeTracker` rather than a line per tick or a silent Debug-only degrade.
- Structured properties reach the live viewer but are not persisted to the log file - put anything that
  must survive into the message template.

### Comments and style

- `Directory.Build.props` sets `Nullable`, `ImplicitUsings`, `latest-recommended` analysis,
  `EnforceCodeStyleInBuild` and `CS8602` as an error. Build warning-free; do not relax these to make a
  build pass.
- C# in `src/` is tab-indented. Match the surrounding file rather than reformatting it.
- Suppress a diagnostic with the narrowest scope that fits and **always with a reason** on the
  `#pragma` or the `NoWarn` entry.
- Comments explain non-obvious constraints - a race, a protocol rule, why a shape was chosen - not what
  the code already says.
- Write comments in English, regardless of the language used in chat or commit discussion.
- No decorative comment formatting - no ASCII dividers, banners, box-drawing, or emoji. A comment is a
  plain sentence, not a header.
- Keep comments as short as the constraint allows, and prefer no comment at all. Only write one for
  genuinely non-obvious or complex logic (a race, a workaround, a protocol quirk) - never to restate what
  a well-named method or property already says.
- Method and property names must be self-explanatory. If a name needs a comment to be understood, rename
  it instead of documenting it.
- No em dashes in code, comments, commit messages or documentation - use a period, comma or parenthesis
  instead.

### Code quality

- Follow DRY: extract shared logic instead of duplicating it across actions, providers or config flow
  steps. Do not extract on the first occurrence of similar code; do so once a real duplication pattern
  emerges.
- Keep a clean architecture: respect the separation between the integration, actions, config flow and
  provider-specific code. Don't reach across those boundaries or leak provider-specific types into
  shared SDK-facing surfaces.
- Consider performance: avoid unnecessary allocations, synchronous blocking, or repeated expensive work
  in hot paths (invocation handlers, poll loops).
- Dispose everything that owns unmanaged or long-lived resources (`HttpClient` handlers, subscriptions,
  timers, cancellation token registrations, websocket connections). Prefer `IAsyncDisposable`/`IDisposable`
  and DI-managed lifetimes over manual lifecycle management. Watch for event-handler subscriptions that
  outlive their subscriber - a common source of memory leaks in long-running plugin processes.

## Verifying a change

Run the ones that apply, in this order, before calling a change done:

```bash
dotnet build
```

```bash
dotnet test
```

For an interactive verification, start the installed Macro Deck desktop app and debug the plugin with
the **Macro Deck - Real Host** `.NET` launch profile. Keep the profile secret-free; supply a first-run
enrollment token only through the project's local .NET User Secrets and remove it after registration.
Do not add a second run configuration or a CLI/executable startup path.

```bash
macrodeck-plugin test --project src/TuyaControl --report markdown --output conformance.md
```

The conformance suite drives a real session: capability contracts, invocation and cancellation semantics,
reconnect and resume, the reserved endpoints, logging limits. Exit `0` conformant, `1` the plugin is
wrong, `2` usage error, `3` input unreadable, `4` cancelled - `1` and `3` are deliberately distinct. Run
it after any change to capability shape, cancellation handling or the manifest, and treat a Required
check going from pass to fail as a blocking regression. Most checks `SKIP` until the plugin declares
capabilities.

The Macro Deck packages float to the newest published version, so the commands above need no version
argument. Only to test against SDK surface that is not published yet, pack it into `local-feed/` and
pass `-p:MacroDeckSdkVersion=<version>`.

Working in the template repository itself rather than in a plugin generated from it? Changing its shape
(files, names, `.template.config/template.json`, `packaging/`) also needs a generated-project check -
see
[packaging/README.md](https://github.com/Macro-Deck-App/Macro-Deck-Plugin-Template/blob/main/packaging/README.md).

## Packing a plugin release

```bash
dotnet build -c Release
macrodeck-plugin validate --manifest src/TuyaControl/bin/Release/net10.0/manifest.json
macrodeck-plugin pack --source src/TuyaControl/bin/Release/net10.0 --output <id>-<version>.macroDeckPlugin
macrodeck-plugin inspect --artifact <id>-<version>.macroDeckPlugin
```

`pack` validates first and recomputes every `files[]` digest from disk, discarding whatever the source
manifest declared - so never hand-maintain `files[]`. Signing happens *after* packing, against the packed
manifest; sign earlier and the digest will not match.

## Workflow

- Work on a branch, not directly on `main`. Use `feature/`, `fix/`, `refactor/`, `chore/`, `docs/` or
  `ci/` with a short kebab-case description, and an issue number where one exists.
- Publishing the template package requires a pushed semantic-version tag such as
  `v3.0.0-preview.3`. The publish workflow removes the leading `v` and uses the rest as the NuGet
  package version. A push to `main` alone never publishes.
- Keep changes focused; no unrelated reformatting.
- Do not push or open a pull request unless asked.
- Do not add AI attribution or co-author trailers.
- Update `README.md` when the build, run, packaging or capability story changes. Update this file when a
  rule here stops being true.
