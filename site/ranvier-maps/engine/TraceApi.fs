namespace Ranvier

open System.Diagnostics

/// <summary>Trace calls that compile only into callers built with <c>RANVIER_TRACE</c>.</summary>
/// <remarks>A caller built without <c>RANVIER_TRACE</c> drops each call and evaluates none of its arguments.</remarks>
[<AbstractClass; Sealed>]
type Trace =
    /// <summary>Labels <c>node</c> in <c>graph</c>'s log with <c>text</c>, which may be computed.</summary>
    /// <remarks>
    /// The label replaces the node's path segment, and the paths beneath the node move with it.
    /// A caller built without <c>RANVIER_TRACE</c> drops the call, so a computed <c>text</c> costs nothing there.
    /// </remarks>
    [<Conditional("RANVIER_TRACE")>]
    static member label(graph: Graph, node: INode, text: string) : unit =
#if RANVIER_TRACE
        ((box graph) :?> ITraced).TraceLog.Append(TraceEventKind.Label, node.Id, 0, 1, 0, 0, text)
        |> ignore
#else
        ()
#endif

/// <summary>Provenance queries over a graph's event log, compiled in by <c>RanvierTrace=true</c>.</summary>
/// <remarks>An untraced build carries <c>Trace.named</c> and the <c>Conditional</c> <c>Trace.label</c>.</remarks>
[<RequireQualifiedAccess>]
module Trace =
#if RANVIER_TRACE
    /// <summary>Runs <c>f</c> and labels the first node or owner it creates on this thread.</summary>
    /// <remarks>
    /// A label <c>f</c> leaves unused is recorded as a <c>Label</c> event with <c>Node = 0</c> on the ambient graph.
    /// Untraced, <c>named</c> inlines to <c>f ()</c>, and a literal label costs nothing.
    /// </remarks>
    let named (label: string) (f: unit -> 'T) : 'T =
        Tracer.PushLabel label

        try
            f ()
        finally
            Tracer.PopLabel (
                match Graph.TryCurrent with
                | ValueSome graph -> box graph
                | ValueNone -> null
            )

    /// <summary>A copy of the events <c>graph</c> has recorded, oldest first.</summary>
    let events (graph: Graph) : TraceEvent[] =
        ((box graph) :?> ITraced).TraceLog.Events

    /// <summary>The creation record of <c>node</c> in <c>graph</c>'s log.</summary>
    /// <exception cref="T:System.ArgumentException"><c>graph</c>'s log holds no <c>NodeNew</c> for <c>node</c>.</exception>
    let origin (graph: Graph) (node: INode) : TraceOrigin =
        let events = events graph
        let id = node.Id

        match
            events
            |> Array.tryFindIndex (fun e -> e.Kind = TraceEventKind.NodeNew && e.Node = id)
        with
        | None ->
            raise (
                System.ArgumentException (
                    "The graph's trace log holds no NodeNew for node "
                    + string id
                    + ".",
                    nameof node
                )
            )
        | Some i ->
            let created = events[i]

            let relabel =
                events
                |> Array.tryFindBack (fun e ->
                    e.Kind = TraceEventKind.Label
                    && e.Arg = 1
                    && e.Node = id)

            let label =
                match relabel with
                | Some e -> Some (string e.Payload)
                | None when
                    i + 1 < events.Length
                    && events[i + 1].Kind = TraceEventKind.Label
                    && events[i + 1].Arg = 0
                    && events[i + 1].Node = id
                    ->
                    Some (string events[i + 1].Payload)
                | None -> None

            let snap = TraceModel.snapshot events

            let rec chain owner =
                match snap.Owners.TryFind owner with
                | Some o ->
                    owner
                    :: (if o.Parent = 0 then [] else chain o.Parent)
                | None -> []

            {
                Seq = created.Seq
                Node = id
                Kind = enum<TraceNodeKind> created.Arg
                Label = label
                Owner = created.Other
                Owners = chain created.Other
                Path = TraceModel.pathOf snap id
                Run = created.Cause
                Site = created.Payload
            }

    /// <summary>The cause chain of <c>node</c>'s last run in <c>graph</c>'s log.</summary>
    /// <exception cref="T:System.ArgumentException">The log holds no <c>RunStart</c> for <c>node</c>.</exception>
    let why (graph: Graph) (node: INode) : Why =
        TraceModel.why (events graph) null node.Id 0

    /// <summary>The cause chain of run number <c>run</c>, from 1, of <c>node</c> in <c>graph</c>'s log.</summary>
    /// <exception cref="T:System.ArgumentException">The log holds no such run.</exception>
    let whyAt (graph: Graph) (node: INode) (run: int) : Why =
        if run < 1 then
            raise (System.ArgumentException ("A run number starts at 1.", nameof run))

        TraceModel.why (events graph) null node.Id run

    /// <summary>The first <c>depth</c> steps of the cause chain of <c>node</c>'s last run in <c>graph</c>'s log.</summary>
    /// <exception cref="T:System.ArgumentException">The log holds no <c>RunStart</c> for <c>node</c>.</exception>
    let whyDepth (graph: Graph) (depth: int) (node: INode) : Why =
        TraceModel.whyDepth (events graph) null depth node.Id 0

    /// <summary>Why <c>node</c> has not run since its last run ended, or <c>None</c> when the log shows no reason.</summary>
    let whyNot (graph: Graph) (node: INode) : WhyNotReason option =
        TraceModel.whyNot (events graph) node.Id

    /// <summary>Every recorded run of <c>node</c> in <c>graph</c>'s log, with its status, movement, cause and flush.</summary>
    let history (graph: Graph) (node: INode) : TraceHistory =
        TraceModel.history (events graph) null node.Id

    /// <summary>
    /// The pending sources read by <c>node</c>'s last run in <c>graph</c>'s log, and the node's flights with their
    /// results.
    /// </summary>
    let waitingOn (graph: Graph) (node: INode) : TraceWaiting =
        TraceModel.waitingOn (events graph) node.Id

    /// <summary>
    /// The differences between <c>graph</c>'s live source lists and observer sets and those folded from its log, one
    /// line per node; empty when they match.
    /// </summary>
    let reconcile (graph: Graph) : string list =
        let log = ((box graph) :?> ITraced).TraceLog
        let events = log.Events
        let sources = TraceModel.sources events
        let observers = TraceModel.observers events

        let ids (opening: string) (items: seq<int>) (closing: string) =
            opening
            + System.String.Join ("; ", items)
            + closing

        [
            for set in log.EdgeSets do
                let live = set.Ids

                if set.IsSources then
                    let folded =
                        sources
                        |> Map.tryFind set.Owner
                        |> Option.defaultValue []

                    if List.ofArray live <> folded then
                        yield
                            "sources of "
                            + string set.Owner
                            + ": live "
                            + ids "[|" live "|]"
                            + ", folded "
                            + ids "[" folded "]"
                else
                    let folded =
                        observers
                        |> Map.tryFind set.Owner
                        |> Option.defaultValue Set.empty

                    if Set.ofArray live <> folded then
                        yield
                            "observers of "
                            + string set.Owner
                            + ": live "
                            + ids "[|" live "|]"
                            + ", folded "
                            + ids "set [" folded "]"
        ]

    let private logOf (graph: Graph) =
        ((box graph) :?> ITraced).TraceLog

    /// <summary>The state folded from every event in <c>graph</c>'s log.</summary>
    let snapshot (graph: Graph) : TraceSnapshot =
        TraceModel.snapshot (events graph)

    /// <summary>The state folded from the events in <c>graph</c>'s log up to and including seq <c>seq</c>.</summary>
    let snapshotAt (graph: Graph) (seq: int) : TraceSnapshot =
        TraceModel.snapshotAt (events graph) seq

    /// <summary>
    /// The node id at identity path <c>path</c> in <c>graph</c>'s log: the <c>@k</c>-th holder, else the live holder,
    /// else the latest.
    /// </summary>
    let resolve (graph: Graph) (path: string) : int option =
        TraceModel.resolve (snapshot graph) path

    /// <summary>
    /// <c>value</c> as text, with node ids shown as <c>graph</c>'s identity paths: a <c>Why</c>, a
    /// <c>WhyNotReason option</c>, a <c>TraceSnapshot</c>, a <c>TraceEvent[]</c> with repeated marks folded, a
    /// <c>TraceOrigin</c>, a <c>TraceHistory</c> or a <c>TraceWaiting</c>.
    /// </summary>
    /// <exception cref="T:System.ArgumentException"><c>value</c> is none of those types.</exception>
    let render (graph: Graph) (value: obj) : string =
        let snap = snapshot graph

        match value with
        | null -> TraceModel.renderWhyNot snap None
        | :? Why as why -> TraceModel.renderWhy snap why
        | :? option<WhyNotReason> as reason -> TraceModel.renderWhyNot snap reason
        | :? TraceSnapshot as s -> TraceModel.renderSnapshot s
        | :? TraceHistory as h -> TraceModel.renderHistory snap h
        | :? TraceWaiting as w -> TraceModel.renderWaiting snap w
        | :? (TraceEvent[]) as events -> TraceModel.renderEvents snap events
        | :? TraceOrigin as o ->
            let site = if isNull o.Site then "?" else string o.Site

            TraceModel.pathOf snap o.Node
            + " "
            + TraceNames.nodeKind o.Kind
            + " at "
            + site
            + " #"
            + string o.Seq
        | other ->
            raise (
                System.ArgumentException (
                    "Trace.render has no text form for "
                    + other.GetType().Name
                    + ".",
                    nameof value
                )
            )

    let private gate (graph: Graph) (operation: string) =
        if graph.Options.ThreadAffinity = Serialised then
            graph.AssertOnGraphThread operation
        elif not graph.IsOnGraphThread then
            raise (
                System.InvalidOperationException (
                    operation
                    + " ran off the graph's thread. Marshal it through Graph.Dispatch."
                )
            )

        if isNull (box graph.CurrentComputation) then
            Tracer.AbandonStale (logOf graph)

        if (logOf graph).Busy then
            raise (
                System.InvalidOperationException (
                    operation
                    + " ran inside a flush, a discharge or a computation's run. Call it between flushes."
                )
            )

    /// <summary>The JSONL dump of <c>graph</c>'s log, schema 1.</summary>
    /// <exception cref="T:System.InvalidOperationException">
    /// Called off the graph's thread, or while a flush, a discharge or a run is in progress. A <c>batch</c> is allowed.
    /// </exception>
    let dumpText (graph: Graph) : string =
        gate graph "Trace.dumpText"
        TraceModel.dumpText "net" null TraceModel.emptySnapshot (events graph)

#if !FABLE_COMPILER
    /// <summary>Writes the JSONL dump of <c>graph</c>'s log, schema 1, to <c>path</c>, and returns the full path.</summary>
    /// <remarks>Absent from a Fable build.</remarks>
    /// <exception cref="T:System.InvalidOperationException">As for <c>dumpText</c>.</exception>
    let dump (graph: Graph) (path: string) : string =
        gate graph "Trace.dump"
        let text = TraceModel.dumpText "net" null TraceModel.emptySnapshot (events graph)
        let full = System.IO.Path.GetFullPath path
        System.IO.File.WriteAllText (full, text)
        full
#endif

#else
    /// <summary>Runs <c>f</c>. A traced build also labels the first node or owner it creates on this thread.</summary>
    /// <remarks>Inlines to <c>f ()</c> on .NET and in Fable; a literal label costs nothing.</remarks>
    let inline named (_label: string) ([<InlineIfLambda>] f: unit -> 'T) : 'T = f ()
#endif
