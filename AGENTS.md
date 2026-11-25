# AGENTS.md

Guidance for engineers and coding agents contributing to this repository. Read it before changing code.

## What this repository is

A **pilot** for adding pipeline orchestration to an SDK. It has three projects:

| Path | What it is |
| --- | --- |
| `src/DurableFunctionOrchestrator.Sdk` | **The SDK.** `ActivityOrchestrator<TInput, TOutput>`: a base class for Durable Task orchestrators that run a pipeline of activities and sub-orchestrations. Concrete classes declare *which* steps run; the base class handles *how* (sequencing, fan-out/fan-in, conditions, retries, early exit, progress, failure handling). |
| `src/DurableFunctionOrchestrator` | **Example Azure Functions app** (isolated worker) that uses the SDK: an order pipeline (`Orders/`) with a fulfillment sub-orchestration, compensation and an HTTP starter. |
| `tests/DurableFunctionOrchestrator.Sdk.Tests` | **SDK unit tests** (xUnit v3), run against a fake `TaskOrchestrationContext`. |

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

**Before finishing any change:** the build passes, `dotnet test` passes, and the formatting check is clean.

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
- **CRLF line endings**, UTF-8, final newline, file-scoped namespaces, braces on new lines.
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

## SDK design rules

These keep the SDK correct and consistent. Breaking them usually causes bugs that only show up in production replays.

1. **Orchestration code must be deterministic.** Durable Task replays orchestrator code. Anything that runs inside the
   orchestration, including `ConfigurePipeline`, input builders, conditions, `BuildOutput` and SDK internals, must not do I/O,
   read the clock (`DateTime.Now`), create GUIDs or random values, or depend on thread timing. Use
   `context.CurrentUtcDateTime` / `context.NewGuid()` where needed. Do real work in activities.
2. **Keep parallel results deterministic.** `StepStage` awaits all branches, then applies outcomes **in declaration
   order**. Keep it that way: never record results in completion order.
3. **Steps are identified by typed handles, not strings.** `StepHandle<TResult>` / `StepHandle` are created with
   `Steps.Activity(...)` / `Steps.SubOrchestration(...)`. Results are read with `state.GetResult(handle)`, and failures
   are matched with `failure.Step == handle`. Don't reintroduce string-keyed lookups. Handles are records and compare by value.
4. **Fail at startup on bad configuration.** Invalid pipelines (no stages, an empty parallel stage, duplicate step names) throw
   `InvalidOperationException` when the pipeline is built, with a message that says how to fix it.
5. **Catch only `TaskFailedException`** around task calls. Other exceptions are bugs or runtime signals and must propagate.
6. **Keep the public API small.** Public types live in the `DurableFunctionOrchestrator.Sdk` namespace. Implementation types go in
   `Internal/` and are `internal`. Every public member needs XML docs (`GenerateDocumentationFile` is on).
7. **The base class owns execution.** `RunAsync` is `sealed`; extension points are `ConfigurePipeline`,
   `BuildOutput`, `DefaultTaskOptions` and `OnStepFailedAsync`. Add new behaviour as pipeline primitives or step options,
   not by opening up `RunAsync`.

### Adding a pipeline feature (e.g. a new stage type or step option)

1. Implement it in the SDK. A new stage implements `Internal/IPipelineStage<TInput>`; a new step option goes on `StepOptions<TInput>`.
2. Expose it through `OrchestrationPipelineBuilder` and, if it applies to parallel stages, `ParallelStageBuilder`.
3. Add tests in `tests/DurableFunctionOrchestrator.Sdk.Tests` (see below).
4. Update the SDK [`README.md`](src/DurableFunctionOrchestrator.Sdk/README.md): the API tables, an example, and the
   "Open questions" list if the feature resolves or raises one.
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
- **Activities are simulated.** They stand in for external systems. Certain inputs trigger failures on purpose, so every
  pipeline outcome can be demonstrated. The trigger values are constants on `OrderActivities`:
  - a payment over 10,000 is declined (compensation);
  - a SKU starting with `OUT-OF-STOCK` fails `ReserveInventory` (unhandled failure: the orchestration fails);
  - an email ending with `@bounce.test` fails `SendConfirmation` (`ContinueOnError`: the order still completes).
- [`DurableFunctionOrchestrator.http`](src/DurableFunctionOrchestrator/DurableFunctionOrchestrator.http) has a demo request
  for every scenario, each with a follow-up status request. Keep it in sync when you add a scenario or change a trigger.

## Open work

See the "Open questions" section of the SDK README. At the time of writing these are:

- removing the per-orchestrator `[Function]` registration boilerplate,
- adding missing primitives: `ForEach` fan-out, timers and external events, per-step compensation.

Also not yet done: an automated check that rejects primary constructors, and running the example in the real Functions host.
