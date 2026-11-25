# DurableFunctionOrchestrator.Sdk (pilot)

A base class for Durable Task orchestrators that run a pipeline of activities and sub-orchestrations.
You declare **which** steps run; the base class handles **how** they run: sequencing, fan-out/fan-in,
conditions, retries, early exit, progress reporting and failure handling.

> **Pilot status:** the API may change based on pilot feedback. See [Open questions](#open-questions).

## Quick start

Abbreviated: the model types and `OrderResult` helpers are illustrative.

```csharp
public sealed class OrderProcessingOrchestrator : ActivityOrchestrator<OrderRequest, OrderResult>
{
  // 1. Declare a typed handle for each step.
  private static readonly StepHandle<ValidationResult> Validation = Steps.Activity<ValidationResult>("ValidateOrder");
  private static readonly StepHandle<InventoryReservation> Reservation = Steps.Activity<InventoryReservation>("ReserveInventory");
  private static readonly StepHandle<ShippingQuote> Shipping = Steps.Activity<ShippingQuote>("CalculateShipping");
  private static readonly StepHandle<PaymentReceipt> Payment = Steps.Activity<PaymentReceipt>("ProcessPayment");
  private static readonly StepHandle<FulfillmentResult> Fulfillment = Steps.SubOrchestration<FulfillmentResult>("FulfillmentOrchestrator");
  private static readonly StepHandle Confirmation = Steps.Activity("SendConfirmation");

  // 2. Expose it as an Azure Functions orchestration trigger (isolated worker).
  [Function(nameof(OrderProcessingOrchestrator))]
  public Task<OrderResult> Run([OrchestrationTrigger] TaskOrchestrationContext context) => ExecuteAsync(context);

  // 3. Build the pipeline from the handles.
  protected override void ConfigurePipeline(OrchestrationPipelineBuilder<OrderRequest> pipeline) =>
    pipeline
      .Step(Validation)
      .StopIf(state => !state.GetResult(Validation).IsValid, "Order failed validation.")
      .Parallel(parallel => parallel
        .Step(Reservation)
        .Step(Shipping))
      .Step(Payment, state => new PaymentRequest(state.Input.OrderId, state.GetResult(Shipping).Cost))
      .Step(Fulfillment, state => new FulfillmentRequest(state.GetResult(Reservation).ReservationId))
      .Step(Confirmation, configure: step => step.ContinueOnError());

  // 4. Read typed results through the same handles.
  protected override OrderResult BuildOutput(OrchestrationState<OrderRequest> state) =>
    state.IsStopped
      ? OrderResult.Rejected(state.Input.OrderId)
      : OrderResult.Completed(state.Input.OrderId, state.GetResult(Payment), state.GetResult(Fulfillment));
}
```

Start it like any other orchestration: `client.ScheduleNewOrchestrationInstanceAsync(nameof(OrderProcessingOrchestrator), order)`.

A complete example is in [`src/DurableFunctionOrchestrator/Orders`](../DurableFunctionOrchestrator/Orders).

## Concepts

- **Step handle (`StepHandle<TResult>` / `StepHandle`):** a typed reference to a step. It holds what the step calls (an
  activity or a sub-orchestration, and its registered name), the step name, and the result type. You use the same handle
  to add the step to the pipeline, read its result, and recognise it in a failure. The non-generic `StepHandle` is for steps with no result.
- **Pipeline:** an ordered list of **stages**, built in `ConfigurePipeline`.
- **Stage:** a single step, a `Parallel` group of steps, or a `StopIf` checkpoint.
- **State (`OrchestrationState<TInput>`):** the orchestration input plus the outcome of every step, keyed by handle.
  It's passed to input builders, conditions, `BuildOutput` and `OnStepFailedAsync`.

## Step handles

Create handles with `Steps`, usually as `static readonly` fields. Then `ConfigurePipeline`, `BuildOutput` and
`OnStepFailedAsync` can all use them.

| Factory | Handle |
| --- | --- |
| `Steps.Activity<TResult>(activityName, name?)` | `StepHandle<TResult>`: an activity that returns a value. |
| `Steps.Activity(activityName, name?)` | `StepHandle`: an activity with no result. |
| `Steps.SubOrchestration<TResult>(orchestratorName, name?)` | `StepHandle<TResult>`: a sub-orchestration that returns a value. |
| `Steps.SubOrchestration(orchestratorName, name?)` | `StepHandle`: a sub-orchestration with no result. |

- **The step name** defaults to the activity or orchestrator name. Pass `name:` to call the same target more than once:
  `Steps.Activity<int>("Square", name: "SquareAgain")`.
- **Handles compare by value:** handle type, result type, name, kind and target. Two separately created but identical
  handles are interchangeable, while a handle with the same name but a different result type never matches.
- **A handle's result type must match what the activity or orchestrator returns.** The result is deserialized into that type.

## What to override

| Member | Required | Purpose |
| --- | --- | --- |
| `ConfigurePipeline(pipeline)` | Yes | Declare the steps. |
| `BuildOutput(state)` | Yes | Build the orchestration output. Called when the pipeline finishes or is stopped by `StopIf`. |
| `DefaultTaskOptions` | No | Retry policy (or other `TaskOptions`) for steps that don't set their own. |
| `OnStepFailedAsync(context, state, failure)` | No | Handle a failed step: compensate, return a fallback output. The default rethrows, so the orchestration fails. |

## Pipeline API

| Method | What it does |
| --- | --- |
| `Step(handle, input?, configure?)` | Call the activity or sub-orchestration the handle points to, and record its outcome. |
| `Parallel(p => p.Step(...).Step(...))` | Run steps concurrently; the next stage starts when all finish. |
| `StopIf(predicate, reason)` | End the pipeline early (not a failure). `state.IsStopped` and `state.StopReason` are set. |

`input` builds the step's input from the state (`state => ...`). When omitted, the step receives the orchestration input.

### Step options (`configure: step => step...`)

| Option | Effect |
| --- | --- |
| `.When(state => ...)` | Run only if the condition holds; otherwise the step is recorded as skipped. |
| `.WithRetry(policy)` | Retry policy for this step, overriding `DefaultTaskOptions`. |
| `.WithOptions(options)` | Any `TaskOptions`; pass `SubOrchestrationOptions` to set a sub-orchestration's instance ID. |
| `.ContinueOnError()` | A failure is recorded in `state.Failures` and the pipeline continues. |

### Reading results

```csharp
PaymentReceipt receipt = state.GetResult(Payment);           // typed; throws if the step has no result
if (state.TryGetResult(Reservation, out var reservation)) { } // false if it hasn't completed successfully
state.Failures.ContainsKey(Confirmation);                     // failed but continued (ContinueOnError)
state.CompletedSteps / state.SkippedSteps / state.IsStopped / state.StopReason
```

A step has no result when it hasn't run yet, was skipped or failed, or isn't part of this pipeline.

## Failure handling

A step fails when its activity or sub-orchestration throws after all retries. Then:

1. If the step uses `.ContinueOnError()`, the failure is recorded and the pipeline continues.
2. Otherwise the pipeline stops and `OnStepFailedAsync` receives a `StepFailedException`. Its `Step` is the failed step's
   handle, and `FailureDetails` holds the error.

In a `Parallel` stage every branch finishes first, and successful branches are recorded before the failure is raised, so `OnStepFailedAsync` can compensate them.
If several branches fail, the first one in declaration order is reported.

```csharp
protected override async Task<OrderResult> OnStepFailedAsync(
  TaskOrchestrationContext context, OrchestrationState<OrderRequest> state, StepFailedException failure)
{
  if (failure.Step != Payment)
    return await base.OnStepFailedAsync(context, state, failure); // rethrow

  if (state.TryGetResult(Reservation, out var reservation))
    await context.CallActivityAsync("ReleaseInventory", reservation.ReservationId);

  return OrderResult.PaymentFailed(state.Input.OrderId, failure.FailureDetails.ErrorMessage);
}
```

## Progress

Before each stage and at the end, the orchestrator publishes an `OrchestrationProgress` as its
**custom status**: `Status` (`Running` / `Completed` / `Stopped` / `Failed`), the current stage, and
the names of the completed, skipped and failed steps. Clients see it through the standard status query endpoint.

## Rules to follow

Orchestrator code is replayed, so:

- **Keep `ConfigurePipeline`, input builders, conditions and `BuildOutput` deterministic.** No I/O,
  `DateTime.Now`, `Guid.NewGuid()` or random values. Do that work in activities. Use
  `context.CurrentUtcDateTime` / `context.NewGuid()` only in `OnStepFailedAsync`.
- **Steps in the same `Parallel` stage can't read each other's results.** Only results from earlier stages are available.
- **Step names must be unique** in a pipeline. Duplicates fail when the orchestration starts.

## Hosting

The SDK depends only on `Microsoft.DurableTask.Abstractions`, not on Azure Functions.

- **Azure Functions (isolated):** add the one-line `[Function]` method shown above. Activities are ordinary
  `[ActivityTrigger]` functions.
- **Class-based / Durable Task SDK:** `ActivityOrchestrator<TInput, TOutput>` derives from
  `TaskOrchestrator<TInput, TOutput>`, so it can be registered as a class-based orchestrator.

## Testing your orchestrators

`RunAsync(context, input)` is public, so you can unit-test an orchestrator against a fake
`TaskOrchestrationContext`. `tests/DurableFunctionOrchestrator.Sdk.Tests/Fakes` has one you can copy.
Note that a fake doesn't apply retry policies or replay.

## Open questions

Feedback wanted from the pilot:

1. **Registration boilerplate.** Is the one-line `[Function]` method acceptable, or should we target the
   Durable Task source generator (stable only at 1.0.0 today) to remove it?
2. **Missing primitives.** Fan-out over a collection (`ForEach`), timers / external events, and built-in compensation per step.

Resolved: step results used to be looked up by string name (`GetResult<T>("ProcessPayment")`), which was checked only at
runtime. They now use typed step handles.
