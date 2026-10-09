(**
---
title: "Ranvier: making reactive computation inspectable"
description: A reactive engine for .NET and Fable, with live signal maps showing propagation, batching and async recovery.
layout: case-study
---
*)

(**
# Ranvier: async and error aware reactive computation for .NET and Fable (JS)

I built Ranvier as a fine-grained reactive engine for .NET and Fable, drawing
heavily on Solid v2. I wanted to explore how its async-in-the-graph model
translates to F# and .NET: pending and failure propagate through computations as
state, while boundaries decide where they are handled. Ranvier has its own
scheduling and APIs; the model for representing pending in the graph comes from
Solid v2.

I also wanted the runtime to be inspectable enough to see why a computation ran.
That is why Ranvier includes trace-driven maps, not just the reactive engine.

## How?

The value-invalidation part of the model is straightforward: a source changes,
dependents are marked for checking, and the graph recomputes only when the new
value differs under its comparison rule.

*In simple terms:*

:::steps
### Value change

A source value changes.

### Equality Cut-off

If the new value is different to the old value,
then the source is marked `dirty`.

### Recompute

Dependent computations re-run.

### Equality Cut-off

Evaluate the new value against the previous value.
If it is different, then the computation is marked `dirty`.

Otherwise, the computation chain is *cut-off*.

### Repeat

*Dependent computations re-run.*
:::

For comparison, one common way to represent async state at the application
level is to put the status in the value:

```fs
type AsyncValue<'T> =
    | Loading of previous: 'T option
    | Loaded of value: 'T
```

The optional previous value handles both an initial load and a refresh. This
representation can work well, but it means consumers that care about different
parts of the state need to unwrap and interpret the wrapper themselves. Solid
v2's approach—and the one Ranvier explores—is to represent pending in the
reactive graph, so computations and boundaries can respond to it as runtime
state instead.

## The Async Axis

Solid v2's async design adds a *pending channel* alongside value invalidation.
These are related but distinct: `clean` and `dirty` describe whether a value
needs checking; `pending` means a read cannot yet produce a settled value.
Ranvier adopts this channel idea, without claiming identical scheduling.

> *A read can have a value, be pending, or fail; pending and failure are not
> placeholder values.*

:::steps
### Source pending

An async source starts pending until it is settled. A refresh can also make an
async computation pending while its previous value remains available through
inspection APIs.

### Notification

It notifies its dependents that the read outcome may have changed.

### Pending

When a computation reads a pending source, it cannot finish that run with a
value. It becomes pending too; it does not publish a made-up placeholder.

> *There is no settled value to compute with, but there is a state to propagate.*

### Repeat

Pending continues through computations that read the pending node until a
boundary handles it or the source settles.
:::

## The Error Axis

Failure is also state on a node, not a special value that every memo must
inspect. A read of a failed node rethrows its stored exception; dependent nodes
retain the dependencies they read, so a later input change can rerun them and
recover. An error boundary can turn a failure into a fallback result, while a
suspense boundary handles pending reads and lets failures continue outward.

## Why use exceptions?

I am not claiming this is free. In Ranvier, a pending or failed read aborts the
current computation. On .NET, that means unwinding with exceptions.

This is a deliberate choice, not a convenience hack. A pending read means the
current computation cannot finish with a settled value, so it has to stop at the
point of the read. The same is true for a failed read. Exceptions are a compact
way to represent that interruption without turning the whole computation into a
bunch of optional values and manual checks.

The tradeoff is real. The project's .NET 10 [suspension benchmarks](https://shayanhabibi.github.io/Ranvier/benchmarks/suspension/)
make that very explicit: one measured failing-recompute scenario records about 55
times as many retired instructions as its successful baseline. That is a result
for the documented benchmark setup, not a claim that newer runtimes made
exceptions cheap or that the same ratio applies to all applications or to
JavaScript. The point is not that exceptions are cheap; the point is that they
make interruption and recovery explicit and localized. A pending read stops the
current run at the point of the read; a boundary decides whether to suspend,
recover, or surface the error.

That is the trade. If the cost matters, the benchmark suite is there to measure
it.

## Ranvier

The async axis is one piece of a larger problem: a graph has to stay coherent
while its values, dependencies and work all change. I wanted that machinery to
be useful in ordinary application code, but also possible to inspect when
something behaves unexpectedly.

Ranvier gives the graph a small set of building blocks. A signal owns a value;
a memo is a cached value derived from other nodes; an effect runs side effects
when its inputs change. Reads discover dependencies as computations execute,
so the graph can change shape when a branch changes. The runtime then decides
what is invalid, what needs to run, and what state should travel downstream.

That last part is where the async model matters. A pending read is not the same
as a new value, and a failed read need not destroy the graph that produced it.
The runtime represents those transitions rather than leaving each caller to
coordinate them.

The maps are a way to look at that runtime in motion. They run the traced
engine compiled to JavaScript; the moving marks are driven by its trace events,
not by a separately scripted animation.

The repository includes F# and C# test suites and a reproducible benchmark
suite. The published benchmark results describe their runtime, machine and
limits; they are measurements of specific scenarios, not promises of
application-wide speedups.

## Follow a write through the graph

Start with the small graph below. `count` feeds two memos, and an effect reads
both results. Press **Increment** and follow the change through each branch to
the observer. The values show not just that the effect ran, but how the graph
connected its input to its output.

Then press **Batch two writes**. Both writes happen inside one batch, so the
graph can settle after the related changes rather than exposing an intermediate
combination to the effect.

Circles are signals, rounded rectangles are memos, and diamonds are effects.
Focus or hover a node for details; click it to inspect why it last ran.
*)
(*** hide ***)
#r "../ranvier-maps/engine/bin/Release/net10.0/Ranvier.dll"
#r "nuget: Partas.Solid, 3.0.0-local.e08ad85"
#r "../ranvier-maps/bin/Release/net10.0/Profile.RanvierMaps.dll"
open Partas.Solid
open Ranvier.Docs.Maps

(*** solid render=PropagationMap ***)
open global.Ranvier.Docs.Maps
module Reactive = global.Ranvier.Api
module Trace = global.Ranvier.Trace
module MapControls = global.Ranvier.Docs.Maps.Helpers

let propagationScenario (graph: global.Ranvier.Graph) =
    use _ = graph.Activate ()
    let count = Trace.named "count" (fun () -> Reactive.createSignal 1)
    let doubled = Trace.named "doubled" (fun () -> Reactive.createMemo (fun _ -> count.Value * 2))
    let tripled = Trace.named "tripled" (fun () -> Reactive.createMemo (fun _ -> count.Value * 3))
    Trace.named "observer" (fun () ->
        Reactive.createEffect (fun () -> printfn "%d" (doubled.Value + tripled.Value)))
    controls [
        MapControls.button "Increment" (fun () -> count.Value <- count.Value + 1)
        MapControls.button "Batch two writes" (fun () ->
            Reactive.batch (fun () ->
                count.Value <- count.Value + 1
                count.Value <- count.Value + 1))
    ]

[<SolidComponent>]
let PropagationMap () =
    div (id = "ranvier-propagation", class' = "signal-map-example") {
        SignalMap (Live propagationScenario) Ranvier.FlightPolicy.CancelPrevious [||] true Grouping.Expand
    }

(**
<noscript>The map needs JavaScript. Its running source is shown above: incrementing count from 1 to 2 makes doubled 4 and tripled 6; the observer reads their sum, 10.</noscript>

## Pending and failure belong to the graph

The second graph adds an asynchronous source. `price` has no settled value at
first, so `total` cannot produce one either. A boundary turns that pending read
into a user-facing state; when the source fails, the same boundary handles the
error.

Use **Settle 4**, **Fail**, and **Settle 5** in sequence. The first action
produces a total, the second displays an error, and the last settles the source
again. Recovery happens in the same graph: the failure does not require
rebuilding the computation or its dependencies.
*)
(*** solid render=BoundaryMap ***)
let boundaryScenario (graph: global.Ranvier.Graph) =
    use _ = graph.Activate ()
    let price = Trace.named "price" (fun () -> Reactive.createAsyncSource<int> ())
    let total = Trace.named "total" (fun () -> Reactive.createMemo (fun _ -> price.Value * 3))
    let view = Trace.named "view" (fun () ->
        Reactive.createBoundary
            (fun _ -> "Loading…")
            (fun ex _ -> "Unavailable: " + ex.Message)
            (fun () -> sprintf "Total %d" total.Value))
    Trace.named "observer" (fun () -> Reactive.createEffect (fun () -> printfn "%s" view.Value))
    controls [
        MapControls.button "Settle 4" (fun () -> price.Settle 4)
        MapControls.button "Fail" (fun () -> price.Fail (System.Exception "feed offline"))
        MapControls.button "Settle 5" (fun () -> price.Settle 5)
    ]

[<SolidComponent>]
let BoundaryMap () =
    div (id = "ranvier-boundary", class' = "signal-map-example") {
        SignalMap (Live boundaryScenario) global.Ranvier.FlightPolicy.CancelPrevious [||] true Grouping.Expand
    }

(**
<noscript>The map needs JavaScript. Settling price at 4 produces Total 12; failure produces Unavailable: feed offline; settling at 5 recovers to Total 15.</noscript>

The timeline lets you pause, step, change playback speed and scrub through the
recorded trace. Reset creates a fresh scenario. On narrow screens, the graph
scrolls horizontally to keep its labels legible.

## Design choices and tradeoffs

**Discover dependencies from actual reads.** A computation subscribes to the
sources it reads while it runs. That keeps the dependency declaration beside
the code that uses it, and lets a conditional branch replace one set of edges
with another. The tradeoff is that the graph is dynamic: reading the source
code alone cannot always tell you which nodes depend on which. That is one
reason to make the graph inspectable.

**Invalidate first; run work at a defined point.** A write marks affected
computations, while the graph's flush determines what to recompute and when
effects run. Memos can stop propagation when their output remains equal, and a
batch groups related writes before the graph settles. This avoids treating
every assignment as a request to rerun the whole application, at the cost of
maintaining state and relationships for individual nodes.

**Keep dependency and lifetime relationships distinct.** A dependency edge
answers “who reads this value?” Ownership answers “who is responsible for
disposing this computation?” They are different questions, so Ranvier models
them separately. That means learning an ownership API as well as a dependency
API; in return, cleanup happens at explicit lifecycle boundaries rather than
depending on garbage collection.

**Let the application choose async semantics.** A boundary decides how pending
and failed reads become visible. A flight policy—the rule for overlapping
asynchronous requests—decides whether to request cancellation of earlier work,
keep only the latest result, queue results, or finish current work before
rerunning. Cancellation is cooperative; it does not guarantee that the
operation or its external side effects stop. These are product decisions, not
interchangeable runtime defaults. This example demonstrates pending and
recovery, not a network request or a particular overlap policy.

**Respect the host's threading rules.** In .NET, ordinary graph work belongs
to its owning thread; work and async completions from elsewhere are queued for
that thread to process. A serialized host can instead allow access from
different threads one at a time on its synchronization context. Reactivity
does not make shared mutable state safe to touch from arbitrary threads, and
these browser examples are not a demonstration of the .NET threading contract.

## Observability is part of the implementation

The trace is part of the explanation, not decoration added after the fact.
Ranvier's traced build records writes, invalidation, runs and asynchronous
flights. A query such as `Trace.why` can connect a computation's last run to
the events that caused it. The map turns those records into a view of what the
engine actually did.

Tracing is a separate package, so an application can use an untraced engine
when it does not need that extra visibility. For this page, the traced engine,
map model and Partas.Solid renderer are available to Fable at build time; the
examples shown above are the code that drives the interactive scenarios.

There are limits to the view. These maps show dependency relationships, not
ownership; they are most useful on small graphs, where edges and labels remain
readable. They also run in JavaScript, whose equality behavior differs from
.NET for some types. The maps help answer “what just happened?” They do not
replace tests of the engine or its host-specific contracts.

## Inspect the implementation

The implementation and deeper contracts are documented here:

- [Project repository](https://github.com/shayanhabibi/Ranvier)
- [Core engine](https://github.com/shayanhabibi/Ranvier/blob/main/src/Ranvier/Core.fs)
- [Threading, recovery and ownership contracts](https://shayanhabibi.github.io/Ranvier/concepts/contracts/)
- [Tracing guide](https://shayanhabibi.github.io/Ranvier/guide/tracing/)
- [Original signal maps](https://shayanhabibi.github.io/Ranvier/guide/signal-maps/)

[Discuss an engineering role or consulting project](../#contact).
*)
