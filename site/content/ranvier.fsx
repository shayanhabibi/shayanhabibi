(**
---
title: "Ranvier: making reactive computation inspectable"
description: A reactive engine for .NET and Fable, with live signal maps showing propagation, batching and async recovery.
layout: case-study
---
*)

(**
# Ranvier: making reactive computation inspectable

Ranvier is my fine-grained reactive computation engine for .NET and Fable.
I built it around a question: how do you make changing dependencies, asynchronous
work and resource lifetimes understandable as one system?

A signal holds a value. A memo derives another value. An effect performs work
when its inputs change. The interesting engineering lies between those small
APIs: discovering dependencies during reads, deciding which computations need to
run, and preserving coherent state when work is pending or fails.

The maps below run the real traced engine compiled to JavaScript. They are
adapted from Ranvier's documentation, with their model and renderer brought into
this repository. The moving marks follow trace events; they are not a scripted
illustration of what a reactive graph ought to do.

## Follow a write through the graph

Press **Increment**. `count` feeds two memos; an effect reads both. This diamond
makes propagation visible: one input change reaches two branches before their
results meet in the observer. **Batch two writes** groups two changes into one
batch so the effect sees the final inputs together.

Circles are signals, rounded rectangles are memos, and diamonds are effects.
Focus or hover a node to inspect it; click it to see why it last ran.
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
        SignalMap (Live propagationScenario) global.Ranvier.FlightPolicy.CancelPrevious [||] true Grouping.Expand
    }

(**
<noscript>The map needs JavaScript. Its running source is shown above: incrementing count from 1 to 2 makes doubled 4 and tripled 6; the observer reads their sum, 10.</noscript>

## Pending and failure belong to the graph

An asynchronous source starts without a settled value. Its readers can be pending
without inventing a placeholder number. Here `price` feeds `total`, while a
boundary chooses the visible result: **Loading…**, a settled total, or an error
message. The final effect reads that boundary.

Try **Settle 4**, **Fail**, then **Settle 5**. A failed read does not permanently
end the graph. The same nodes and dependencies can recover when the source
settles again. The manual controls make the sequence repeatable without a remote
service or timer deciding when to answer.
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

The timeline lets you pause, step, change playback speed and scrub through
recorded events. Reset reconstructs the live scenario. On narrow screens, the
graph scrolls horizontally so node labels remain readable.

## Design choices and tradeoffs

**Track reads, then reconcile dependencies.** A computation subscribes to the
sources it actually reads during a run. A conditional branch can therefore change
the graph's edges. This keeps dependencies close to the code that uses them,
but requires tracking to distinguish reads that should subscribe from untracked
inspection. Dynamic graphs are harder to reason about from source alone; tracing
makes the runtime relationships visible.

**Separate invalidation from recomputation.** A changed source marks its readers;
memos can validate their inputs when read, and effects run through the graph's
flush. Equality checks can stop an unchanged derived value from needlessly
propagating. Batching postpones the flush until related writes are complete.
This is fine-grained dependency management, with bookkeeping per node and edge,
rather than a full application update after every write.

**Give ownership a separate structure.** Dependency edges answer who reads a
value; ownership answers who disposes a computation. Ranvier attaches owned
nodes to a scope or the run that created them, and disposes them at defined
points. Signals and async sources are unowned. Pure memos reject owned nodes
created in their bodies; owning variants make that lifetime explicit. The cost
is an API distinction developers must learn. The benefit is that cleanup does
not depend on when the garbage collector happens to run.

**Represent pending and failure explicitly.** Boundaries decide where a pending
or failed read stops. Async flight policies address what should happen when
requests overlap: cancellation, keeping the latest result, queuing, or finishing
the current work. Those choices encode application semantics; one default cannot
answer every ordering requirement. The second map isolates boundary recovery;
it does not demonstrate cancellation or a real network request.

**Make the host contract explicit.** On .NET a graph has threading and dispatch
rules: ordinary graph work belongs to its owning thread, while dispatched work
and async completions can enter through an inbox. Serialised hosts have a
separate affinity mode. A reactive graph is not permission to mutate shared
state from arbitrary threads. These browser examples cannot prove the .NET
threading contract.

## Observability is part of the implementation

Ranvier's traced build records writes, marks, runs and asynchronous flights.
Queries such as `Trace.why` connect the last run to its causes. The maps consume
that log to reconstruct the graph's state and animate propagation.

Tracing ships as a separate package, so production users can choose an untraced
engine. Here a snapshot of the traced engine, map model and Partas.Solid renderer
is stored locally so Fable can compile their source. Build-time Fable compilation
produces the browser bundle from the same F# examples shown on this page.

The maps show the dependency graph, not the ownership tree. They are intended
for small examples: large graphs cross edges, labels abbreviate long values,
and JavaScript equality differs from .NET for some types. That is a useful
inspection tool with a defined boundary, not a replacement for engine tests.

## Inspect the implementation

- [Project repository](https://github.com/shayanhabibi/Ranvier)
- [Core engine](https://github.com/shayanhabibi/Ranvier/blob/main/src/Ranvier/Core.fs)
- [Threading, recovery and ownership contracts](https://shayanhabibi.github.io/Ranvier/concepts/contracts/)
- [Tracing guide](https://shayanhabibi.github.io/Ranvier/guide/tracing/)
- [Original signal maps](https://shayanhabibi.github.io/Ranvier/guide/signal-maps/)

[Discuss an engineering role or consulting project](/#contact).
*)
