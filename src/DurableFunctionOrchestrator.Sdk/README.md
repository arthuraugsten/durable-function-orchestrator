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
| `Steps.Polling(start, check, intervalSeconds, timeoutSeconds?, backoffCoefficient?, maxIntervalSeconds?, name?)` | `StepHandle<TResult>`: start a long-running operation, then check it on an interval until it completes. See [Polling long-running operations](#polling-long-running-operations). |

- **The step name** defaults to the activity or orchestrator name. Pass `name:` to call the same target more than once:
  `Steps.Activity<int>("Square", name: "SquareAgain")`.
- **Handles compare by value:** handle type, result type, name, kind and target. Two separately created but identical
  handles are interchangeable, while a handle with the same name but a different result type never matches.
- **A handle's result type must match what the activity or orchestrator returns.** The result is deserialized into that type.

## Polling long-running operations

Many external APIs are asynchronous: the request returns at once (HTTP `202 Accepted` with a `Location` status URL)
and the work finishes later. A **polling step** handles this pattern, the asynchronous request-reply pattern:

1. It calls the **start** step with the step input. The start step sends the request and returns a reference to the
   operation, such as the status URL.
2. It waits on a durable timer. The orchestration is unloaded while it waits, so an operation can take hours.
3. It calls the **check** step with that reference. The check step returns a `PollStatus<TResult>`:
   - `Pending`: wait, then go back to step 3.
   - `Completed`: its result becomes the step result.
   - `Failed`: the step fails.

```csharp
private static readonly StepHandle<ShippingLabel> Label = Steps.Polling(
  start: Steps.Activity<LabelOperation>("RequestShippingLabel"),          // POST, returns the status URL
  check: Steps.Activity<PollStatus<ShippingLabel>>("CheckShippingLabel"), // GET the status URL
  intervalSeconds: 2,
  timeoutSeconds: 20,
  name: "ShippingLabel");

pipeline.Step(Label, state => new LabelRequest(...)); // used like any other step
ShippingLabel label = state.GetResult(Label);         // the result of the completed operation
```

The check step maps the API's response to a `PollStatus`:

```csharp
[Function("CheckShippingLabel")]
public async Task<PollStatus<ShippingLabel>> CheckShippingLabel([ActivityTrigger] LabelOperation operation)
{
  using HttpResponseMessage response = await _http.GetAsync(operation.StatusUrl);
  return response.StatusCode switch
  {
    HttpStatusCode.Accepted => PollStatus<ShippingLabel>.Pending(),
    HttpStatusCode.OK => PollStatus<ShippingLabel>.Completed((await response.Content.ReadFromJsonAsync<ShippingLabel>())!),
    HttpStatusCode.UnprocessableEntity => PollStatus<ShippingLabel>.Failed("The carrier rejected the label."),
    _ => throw new HttpRequestException($"Unexpected status {response.StatusCode}."), // transient: retried
  };
}
```

- **The HTTP calls happen in the start and check steps, never in orchestration code.** Start and check can be activities
  or sub-orchestrations. The SDK doesn't send HTTP requests itself, so it stays deterministic and host-independent.
- **Interval:** `intervalSeconds` is the wait before the first check: from 1 second to `Steps.MaxPollingIntervalSeconds` (480, 8 minutes).
- **Maximum interval of 8 minutes:** no wait between two checks is ever longer than 8 minutes. That covers the interval,
  `maxIntervalSeconds` and `Retry-After` hints, so a completed operation is always noticed within 8 minutes. Larger values are
  rejected when the handle is created, and longer `Retry-After` hints are capped.
- **Backoff:** after each pending check the interval is multiplied by `backoffCoefficient` (default 1, a fixed interval), up to
  `maxIntervalSeconds` (at most 480), which is required with a backoff. Use it for operations that may take hours: check often at first,
  then rarely.
- **Retry-After:** a check can return `Pending(retryAfterSeconds)`, for example from an HTTP `Retry-After` header. A hint
  longer than the current interval lengthens the next wait. A shorter one is ignored, as HTTP allows.
- **Timeout:** `timeoutSeconds` is measured from when the start step completes. The last wait is shortened so a final check
  happens exactly at the deadline. If the operation is still pending then, the step fails. Omit it to poll until the
  operation completes or fails.
- **Check budget:** a schedule that could make more than `Steps.MaxPollingChecks` (1,000) checks before its timeout is
  rejected when the handle is created. The error message suggests a backoff that fits. With checks at most 8 minutes apart,
  the longest timeout that fits is 480,000 seconds (about 5.5 days). Polling without a timeout has no budget.
- **Options:** step options apply to the polling step as a whole (`When`, `ContinueOnError`). Its task options (retries)
  apply to each individual start and check call, so a transient error on one check is retried without restarting the operation.
- **History size:** every check adds about four events to the orchestration history, which is replayed each time the
  orchestration wakes. That is why there is a check budget. For long polls, also run the polling step inside a
  sub-orchestration (as the example's `FulfillmentOrchestrator` does), so the parent's history stays small.
- **The step name** defaults to the start step's name. `Kind` is `StepKind.Polling` and `TargetName` is the start
  step's target.

### Operations that take hours

The step waits on durable timers, so the operation itself can take hours: the orchestration is unloaded between checks
and costs nothing while it waits. Only the start and check calls run code, and each should return quickly. On the
Azure Functions Consumption plan an activity is limited to 10 minutes; orchestrations have no such limit.

For an operation that can take up to 12 hours (43,200 seconds):

| Schedule | Checks in 12 hours | |
| --- | --- | --- |
| `intervalSeconds: 30` | 1,440 | Rejected: over the budget. |
| `intervalSeconds: 60` | 720 | Accepted. Simple, but checks every minute even at hour 11. |
| `intervalSeconds: 480` | 90 | Accepted. Every 8 minutes, the maximum, from the start. |
| `intervalSeconds: 30, backoffCoefficient: 2, maxIntervalSeconds: 480` | about 94 | Accepted. 30s, 1m, 2m, 4m, then every 8 minutes: quick for short runs, cheap for long ones. |

```csharp
private static readonly StepHandle<ReportResult> Report = Steps.Polling(
  start: Steps.Activity<ReportOperation>("StartReport"),
  check: Steps.Activity<PollStatus<ReportResult>>("CheckReport"),
  intervalSeconds: 30,
  timeoutSeconds: 12 * 60 * 60,
  backoffCoefficient: 2,
  maxIntervalSeconds: Steps.MaxPollingIntervalSeconds); // 8 minutes
```

Completion is noticed at most `maxIntervalSeconds` late, and never more than 8 minutes late.

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

For a polling step, the failure can come from its start or check step (that step's error), or from the polling
itself. Polling failures have a `FailureDetails.ErrorType` of:

- `PollingErrorTypes.OperationFailed`: the check returned `Failed`.
- `PollingErrorTypes.TimedOut`: the operation was still pending at the timeout.

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
2. **Missing primitives.** Fan-out over a collection (`ForEach`), external events (for example as an alternative to
   polling, when the API supports callbacks), and built-in compensation per step.
3. **Polling.**
   - Should the SDK ship ready-made start and check activities for the HTTP `202` + `Location` pattern? That needs a way
     to register SDK-provided activities in each host.
   - Should the check count appear in the custom status while a step polls?
   - Polling intervals are fixed when the handle is declared. Should they also be configurable per step, for example
     from app settings or the step input?
   - Are 1,000 checks and an 8-minute maximum interval the right limits? Together they cap a polling timeout at about 5.5 days.
     For polls that need more, the alternative is a dedicated polling orchestration
     that uses `ContinueAsNew` to reset its history. That needs the same activity-registration story as the first item.

Resolved: step results used to be looked up by string name (`GetResult<T>("ProcessPayment")`), which was checked only at
runtime. They now use typed step handles.
