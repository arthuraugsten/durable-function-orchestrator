# AGENTS.md

Guidance for engineers and coding agents contributing to this repository. Read it before changing code.

## What this repository is

A **pilot** for adding pipeline orchestration to an SDK. It has three projects:

| Path | What it is |
| --- | --- |
| `src/DurableFunctionOrchestrator.Sdk` | **The SDK.** `ActivityOrchestrator<TInput, TOutput>`: a base class for Durable Task orchestrators that run a pipeline of activities and sub-orchestrations. Concrete classes declare *which* steps run; the base class handles *how* (sequencing, fan-out/fan-in, conditions, retries, polling of long-running operations, early exit, progress, failure handling). |
| `src/DurableFunctionOrchestrator` | **Example Azure Functions app** (isolated worker) that uses the SDK: an order pipeline (`Orders/`) with a fulfillment sub-orchestration that polls a simulated asynchronous carrier HTTP API, compensation and an HTTP starter. |
| `tests/DurableFunctionOrchestrator.Sdk.Tests` | **SDK unit tests** (xUnit v3), run against a fake `TaskOrchestrationContext`. |
| `docs/` | **Contributor docs.** [`sdk-dependency-map.html`](docs/sdk-dependency-map.html) is an interactive map of every SDK class and the example app classes that use it, with each class's dependency tree. [`run-lease-renewal.html`](docs/run-lease-renewal.html) steps through how run leases are acquired, renewed and released, with a sequence diagram for each workflow. Open them in a browser. |

The SDK's user-facing documentation is [`src/DurableFunctionOrchestrator.Sdk/README.md`](src/DurableFunctionOrchestrator.Sdk/README.md). It is also the NuGet package readme. Read it to understand the public API.

## Prerequisites

- **.NET 11 SDK** (`11.0.100` or a later feature band; pinned in `global.json`).
- To **run** the Functions app locally (not needed to build or test):
  - [Azure Functions Core Tools](https://learn.microsoft.com/azure/azure-functions/functions-run-local) v4 (`func`).
  - [Azurite](https://learn.microsoft.com/azure/storage/common/storage-use-azurite), because `local.settings.json` uses `UseDevelopmentStorage=true`.

## Commands

Run from the repository root.

```sh
dotnet build                                                 # build everything
dotnet test                                                  # run all tests (Microsoft.Testing.Platform)
dotnet format whitespace DurableFunctionOrchestrator.slnx    # apply formatting
dotnet format whitespace DurableFunctionOrchestrator.slnx --verify-no-changes   # check formatting
dotnet build -p:EnforceCodeStyleInBuild=true --no-incremental  # check naming/style rules
dotnet pack src/DurableFunctionOrchestrator.Sdk               # build the SDK NuGet package
```

To run the example app: start Azurite, then run `func start` in `src/DurableFunctionOrchestrator`. Start an order with
`POST /api/orders` and an `OrderRequest` JSON body. The quickest way is the ready-made requests in
[`DurableFunctionOrchestrator.http`](src/DurableFunctionOrchestrator/DurableFunctionOrchestrator.http).

**Before finishing any change:** the build passes, `dotnet test` passes, and the formatting check is clean. If the change adds,
removes, renames or rewires a class, the [dependency map](#dependency-map-docssdk-dependency-maphtml) is updated too.

### Expected build warnings (safe to ignore)

- `NU1902` (`Microsoft.AspNetCore.WebUtilities` 2.3.0): comes from a dependency of a Functions extension, inside the
  generated `obj/azure_functions/azure_functions.g.csproj`. It can't be fixed from this repo; it will clear when the extension packages update.
- `AZFW0108` ("Functions extensions project was not restored prior to build"): appears on clean builds only.

## Repository setup you must not break

- **`global.json`** does three things, and all are required:
  - `sdk`: pins the .NET 11 SDK.
  - `msbuild-sdks` → `Azure.Functions.Sdk`: the Functions app uses the **`Azure.Functions.Sdk` MSBuild SDK**
    (`<Project Sdk="Azure.Functions.Sdk">`), not the `Microsoft.Azure.Functions.Worker.Sdk` package, which doesn't support .NET 11.
    The helper project the SDK generates names it without a version, so the version **must** live here. To upgrade the Functions SDK, change it here.
  - `test.runner`: xUnit v3 runs on Microsoft.Testing.Platform. The .NET 10+ SDK requires this opt-in for `dotnet test`.
    Don't add `Microsoft.NET.Test.Sdk` or `xunit.runner.visualstudio` back.
- **The SDK project must not depend on Azure Functions.** It references only `Microsoft.DurableTask.Abstractions`, so it
  can be hosted anywhere Durable Task runs. Functions-specific code belongs in the app.
- **`DurableFunctionOrchestrator.slnx`** lives at the root. Add new projects with `dotnet sln add --solution-folder src|tests <csproj>`.

## Code style

[`.editorconfig`](.editorconfig) encodes these rules. Follow them in all new code:

- **2-space indentation everywhere**, including continuation lines: wrapped parameters, chained calls and ternaries are
  indented +2, not +4. `dotnet format` fixes block indentation but not continuation offsets, so check those by hand.
- **LF line endings** (enforced by `.gitattributes`), UTF-8, final newline, file-scoped namespaces, braces on new lines.
- **No primary constructors** on classes or structs. Use a standard constructor that assigns `private readonly` fields.
  Positional `record`s are fine. (`.editorconfig` can only disable the IDE suggestion, so reviewers enforce this.)
- **Private instance fields are `_camelCase`** (`_steps = steps;`). Never use `this.` to access fields.
  Private static fields (e.g. step handles) and constants are **PascalCase**.
- **One-line guard clauses have no braces:**

  ```csharp
  if (!_stepNames.Add(step.Name))
    throw new InvalidOperationException(...);
  ```

- Nullable reference types are enabled, so keep the build free of nullable warnings.
- Validate public arguments with `ArgumentNullException.ThrowIfNull` / `ArgumentException.ThrowIfNullOrWhiteSpace`.

## SDK layout

All SDK code is under `src/DurableFunctionOrchestrator.Sdk/Orchestrator/`. Folders group code by role; every file holds
one type. **Namespaces are flat:** public types are in `DurableFunctionOrchestrator.Sdk` and internal types in
`DurableFunctionOrchestrator.Sdk.Internal`, whatever their folder (`.editorconfig` turns off IDE0130 for this).

| Folder | Visibility | Contents |
| --- | --- | --- |
| `Orchestrator/` | public | `ActivityOrchestrator`: the base class. Thin: builds the pipeline and hands it to `PipelineRunner`. |
| `Steps/` | public | Identifying steps: the `Steps` factory, `StepHandle` / `StepHandle<TResult>`, `StepKind`. Data only. |
| `Pipeline/` | public | Declaring a pipeline: `OrchestrationPipelineBuilder`, `ParallelStageBuilder`, `StepOptions`. |
| `Polling/` | public | What polling check steps return: `PollStatus`, `PollState`, `PollingErrorTypes`. |
| `Results/` | public | What you read: `OrchestrationState`, `OrchestrationProgress`, `StepFailedException`. |
| `Concurrency/` | public | Run leases that prevent concurrent runs: `RunLeaseOptions`, `IOrchestrationRunRepository` and its in-memory fake, `RunLeaseService` (runs inside activities), `RunLeaseLostException`, the record and activity payloads. |
| `Internal/Definitions/` | internal | The declared pipeline as data, with no behaviour: `IPipelineStage`, `PipelineStep`, `PollingDefinition`, `StepOutcome`. |
| `Internal/Building/` | internal | Validation when handles and pipelines are declared: `StepFactory` (unique names), `PollingValidator`. |
| `Internal/Execution/` | internal | **All run logic**, everything that calls Durable Task: `PipelineRunner` (stage loop, progress), `StepStage` / `StopStage`, `StepInvoker` (activity and sub-orchestration calls), `PollingRunner`, `PollingSchedule`, `RunLeaseGuard` (acquire and release), `RunLeaseKeeper` (renews the lease on a durable timer while each stage runs). |

**Logging in the SDK goes through `Internal/Execution/SdkLog`**, generated at compile time with `[LoggerMessage]`.
Orchestrator code replays on every wake-up, so plain `logger.LogInformation(...)` calls would allocate an argument array
and box values on every replay, even when the replay-safe logger discards the message. Add a method to `SdkLog` instead.
Keep LINQ out of code that runs per stage or per check.

Keep it that way: public types hold no run logic (`StepHandle.InvokeAsync` is only a one-line hook that supplies the
result type to `StepInvoker`), and anything that calls the `TaskOrchestrationContext` goes in `Internal/Execution/`.
The tests mirror the folders (`tests/.../Orchestrator/Polling/PollingStepTests.cs`).

### Dependency map (`docs/sdk-dependency-map.html`)

**Constraint: update the map whenever class dependencies change.** That means adding, removing or renaming a class in
`src/DurableFunctionOrchestrator.Sdk/Orchestrator/` or a mapped class in `src/DurableFunctionOrchestrator/Orders/`, or
adding or removing a reference from one mapped class to another. `DependencyMapTests` (`tests/.../Docs/`) enforces it:
`dotnet test` fails until the map matches the code, and its message lists each arrow to add or remove and each class to
add to or remove from the map.

To update the map, edit the page's script:

- **`DEPS`** (between `// <dependencies>` and `// </dependencies>`): one line per class, listing the mapped classes its
  source file references. The test checks this block, ignoring comments and string literals, as it does in the code.
- **`NODES`**: one entry per class, with its column (which follows its folder), kind, folder, visibility, a one-sentence
  role, and `dt: true` if it calls the `TaskOrchestrationContext` directly. The test checks only that every SDK class
  is listed, so keep the role and column accurate by hand.
- **"What the map shows"**: if the change adds or removes an arrow that a note mentions (back-references, Durable Task
  callers, Definitions → Execution), update the note.

Then open the page in a browser and select the changed class to check it. The page is self-contained apart from Google Fonts.

## SDK design rules

These keep the SDK correct and consistent. Breaking them usually causes bugs that only show up in production replays.

1. **Orchestration code must be deterministic.** Durable Task replays orchestrator code. Anything that runs inside the
   orchestration, including `ConfigurePipeline`, input builders, conditions, `BuildOutput` and SDK internals, must not do I/O,
   read the clock (`DateTime.Now`), create GUIDs or random values, or depend on thread timing. Use
   `context.CurrentUtcDateTime` / `context.NewGuid()` where needed. Do real work in activities.
2. **Keep parallel results deterministic.** `StepStage` (`Internal/Execution/`) awaits all branches, then applies outcomes **in declaration
   order**. Keep it that way: never record results in completion order.
3. **Steps are identified by typed handles, not strings.** `StepHandle<TResult>` / `StepHandle` are created with
   `Steps.Activity(...)` / `Steps.SubOrchestration(...)`. Results are read with `state.GetResult(handle)`, and failures
   are matched with `failure.Step == handle`. Don't reintroduce string-keyed lookups. Handles are records and compare by value.
4. **Fail at startup on bad configuration.** Invalid pipelines (no stages, an empty parallel stage, duplicate step names) throw
   `InvalidOperationException` when the pipeline is built, with a message that says how to fix it.
5. **Catch only `TaskFailedException`** around task calls. Other exceptions are bugs or runtime signals and must propagate.
6. **Keep the public API small.** Public types live in the `DurableFunctionOrchestrator.Sdk` namespace. Implementation types go in
   `Orchestrator/Internal/` and are `internal` (see [SDK layout](#sdk-layout)). Every public member needs XML docs (`GenerateDocumentationFile` is on).
7. **The base class owns execution.** `RunAsync` is `sealed`; extension points are `ConfigurePipeline`,
   `BuildOutput`, `DefaultTaskOptions`, `OnStepFailedAsync`, `RunLease` and `OnConcurrentRunRejectedAsync`. Add new behaviour as pipeline primitives or step options,
   not by opening up `RunAsync`.

### Adding a pipeline feature (e.g. a new stage type or step option)

1. Implement it in the SDK, following the [SDK layout](#sdk-layout):
   - A new stage implements `IPipelineStage<TInput>` (`Internal/Definitions/`); the class goes in `Internal/Execution/`.
   - A new step option goes on `StepOptions<TInput>` (`Pipeline/`), and its validation in `Internal/Building/`.
   - A new kind of step gets a factory method on `Steps`, a `StepKind` value, and a runner in `Internal/Execution/`
     that `StepInvoker` dispatches to (as polling does).
2. Expose it through `OrchestrationPipelineBuilder` and, if it applies to parallel stages, `ParallelStageBuilder`.
3. Add tests in `tests/DurableFunctionOrchestrator.Sdk.Tests` (see below).
4. Update the SDK [`README.md`](src/DurableFunctionOrchestrator.Sdk/README.md): the API tables, an example, and the
   "Open questions" list if the feature resolves or raises one. Update the
   [dependency map](#dependency-map-docssdk-dependency-maphtml) for any new or rewired class.
5. Use the feature in the example app if it helps show how the SDK is meant to be used.

### Breaking changes

This is a pilot, so breaking changes are allowed, but they must be deliberate. Update in the same change:

- the example app,
- the tests,
- the README.

Call out the break in your PR description.

## Testing

- SDK tests use `Fakes/FakeOrchestrationContext` and `Fakes/TestOrchestrator`:
  - **`FakeOrchestrationContext`:** register activity and sub-orchestration behaviour with `.Returns(name, value)`, `.Handle(name, input => ...)`
    or `.Fails(name, message)`. A handler that throws surfaces as a `TaskFailedException`, as in the real runtime. Inspect
    `Calls`, `CallNames` and `StatusHistory`.
  - **`TestOrchestrator<TInput>`:** takes the pipeline as a lambda, plus optional default task options and a failure handler. Its output
    is the final `OrchestrationState`, so tests can assert on results, skips and failures directly.
- The fake's **durable timers complete at once** and move `CurrentUtcDateTime` forward to their fire time. `TimerSeconds`
  lists them, in seconds after `FakeOrchestrationContext.Start`. Use it to test polling intervals and timeouts.
- **Virtual time:** run the orchestration with `context.Run(() => orchestrator.RunAsync(context, input))` to test anything
  that races tasks against timers, such as run-lease renewal. It runs on one thread like the real runtime, fires timers
  in order, lets activities take time (`.Takes(name, duration)`), and records when each call started (`CallSeconds`).
  `OrchestrationTimeProvider` gives activity code (such as `RunLeaseService`) the same clock.
- The fake **does not simulate retries or replay**. Test retry configuration by checking the `TaskOptions` passed to each call
  (see `Default_task_options_apply_unless_a_step_overrides_them`). Behaviour that depends on replay needs a real host.
- Test names describe behaviour in sentence form: `Parallel_failure_is_raised_after_every_branch_finishes_...`.
- Every new SDK behaviour needs tests for its success path and its failure/edge cases.
- The example app has no automated tests. If you change it, check the build and, when possible, run it with `func start`.

## Example app conventions (`src/DurableFunctionOrchestrator/Orders`)

- **Activity names** are constants in `OrderActivityNames`, shared by the `[Function(...)]` attributes and the step handles.
- **Step handles** are `private static readonly` fields on the orchestrator that uses them.
- **Each orchestrator** exposes itself with a one-line function:
  `[Function(nameof(X))] public Task<TOutput> Run([OrchestrationTrigger] TaskOrchestrationContext context) => ExecuteAsync(context);`
- **External systems are simulated.** Certain inputs trigger failures on purpose, so every pipeline outcome can be
  demonstrated. The trigger values are constants on `OrderActivities`:
  - a payment over 10,000 is declined (compensation);
  - a SKU starting with `OUT-OF-STOCK` fails `ReserveInventory` (unhandled failure: the orchestration fails);
  - an email ending with `@bounce.test` fails `SendConfirmation` (`ContinueOnError`: the order still completes).
- **The carrier is a real HTTP API, simulated in the same app.** `SimulatedCarrierApi` (`/api/carrier/labels`) answers
  `202` + `Location` and produces a label after a few seconds. `CarrierActivities` call it through the named
  `HttpClient` registered in `Program.cs` (`CarrierApiBaseUrl` setting, defaulting to the local host). They are the start
  and check steps of the `FulfillmentOrchestrator` polling step. Its trigger values are constants on `SimulatedCarrierApi`:
  - a SKU starting with `OVERSIZED` makes the carrier reject the label (the polling step fails; payment is refunded);
  - a SKU starting with `SLOW-SHIP` never finishes in time (the polling step times out; payment is refunded).
- **JSON is source-generated.** Add every type passed through Durable Task (orchestration or activity input/output)
  to `OrderJsonContext`, and every carrier HTTP body to `CarrierJsonContext`. A type that's missing still works
  through the reflection fallback, only more slowly. Computed properties get `[JsonIgnore]`, so they aren't stored
  in the orchestration history.
- **Run leases.** `CarrierRateSyncOrchestrator` sets `RunLease`, so only one rate sync runs at a time. The three lease
  activities (acquire, renew, release) are registered once, in `OrchestrationRunActivities`, and forward to the `RunLeaseService` registered in
  `Program.cs`. The repository there is `InMemoryOrchestrationRunRepository`, a fake that works within one host only.
  For production, implement `IOrchestrationRunRepository` over a shared, strongly consistent database.
- **Activities that need services** (such as `IHttpClientFactory`) live in a non-static class with a standard
  constructor, like `CarrierActivities`. Simple activities stay `static`.
- [`DurableFunctionOrchestrator.http`](src/DurableFunctionOrchestrator/DurableFunctionOrchestrator.http) has a demo request
  for every scenario, each with a follow-up status request. Keep it in sync when you add a scenario or change a trigger.

## Open work

See the "Open questions" section of the SDK README. At the time of writing these are:

- removing the per-orchestrator `[Function]` registration boilerplate,
- adding missing primitives: `ForEach` fan-out, external events, per-step compensation,
- run-lease follow-ups: a database repository, a configurable key (today it is the orchestrator name), and waiting for
  the lease instead of rejecting,
- polling follow-ups: ready-made HTTP `202` start and check activities, polling progress in the custom status, and
  `ContinueAsNew`-based polling for polls that need more than `Steps.MaxPollingChecks` checks.

Also not yet done: an automated check that rejects primary constructors, and running the example in the real Functions host.
