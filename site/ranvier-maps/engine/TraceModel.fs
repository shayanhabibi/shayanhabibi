namespace Ranvier

open System
open System.Collections.Generic
open System.Text

/// <summary>Where a <c>Trace.why</c> chain ends.</summary>
type WhyRoot =
    /// <summary>A write from outside every run: the <c>Write</c> seq.</summary>
    | UserWrite of write: int
    /// <summary>The node's first run, with no dirty mark: the <c>NodeNew</c> seq, or 0 when it is not in the log.</summary>
    | Created of nodeNew: int
    /// <summary>A run with no dirty mark, started by a reader's pull: the reader's node id.</summary>
    | Pulled of reader: int
    /// <summary>A cause older than the events in the log: the checkpoint file that holds it, or null.</summary>
    | BeforeCheckpoint of file: string
    /// <summary>A run or mark whose cause the log does not record: the seq of the last step.</summary>
    | Unrecorded of last: int
    /// <summary>An async source's settle or failure: the <c>Settle</c> or <c>Fail</c> seq.</summary>
    | Settled of settle: int

/// <summary>One event of a <c>Trace.why</c> chain.</summary>
type WhyStep =
    {
        Seq: int
        Kind: TraceEventKind
        Node: int
        Other: int
        /// <summary>The value or exception recorded by a <c>Write</c>, <c>Moved</c>, <c>Settle</c> or <c>Fail</c>.</summary>
        Value: obj option
    }

/// <summary>The cause chain of one run, from its <c>RunStart</c> back to a <c>WhyRoot</c>.</summary>
type Why =
    {
        Node: int
        /// <summary>The run number explained.</summary>
        Run: int
        /// <summary>The steps from the run's <c>RunStart</c> outwards, newest first.</summary>
        Steps: WhyStep list
        /// <summary>The chain's end, or <c>None</c> when <c>whyDepth</c> stopped it first.</summary>
        Root: WhyRoot option
    }

/// <summary>Why a node has not run since its last run ended, in <c>Trace.whyNot</c>.</summary>
type WhyNotReason =
    /// <summary>The node was disposed at <c>seq</c>.</summary>
    | Disposed of seq: int
    /// <summary>The node is scheduled and its run has not started: a batch is open or the flush is not reached.</summary>
    | Queued of schedule: int
    /// <summary>The node was marked at <c>mark</c> and has no observer and no pull since.</summary>
    | Unobserved of mark: int
    /// <summary>A check walk resolved the node clean at <c>resolvedAt</c>; <c>upstream</c> are its sources then.</summary>
    | CheckedClean of resolvedAt: int * upstream: int list
    /// <summary>A source's notification skipped the node as the running reader, at <c>seq</c>.</summary>
    | SkippedAsRunningReader of seq: int
    /// <summary>
    /// Propagation stopped upstream at <c>stopAt</c>: a <c>Write</c> that left its value, or a <c>RunEnd</c> that did
    /// not move.
    /// </summary>
    | NotReached of stopAt: int

/// <summary>One run of a node, in <c>Trace.history</c>.</summary>
type TraceRun =
    {
        /// <summary>The run number, from 1.</summary>
        Run: int
        /// <summary>The seq of the run's <c>RunStart</c>.</summary>
        Start: int
        /// <summary>How the run ended, or <c>None</c> while it is open.</summary>
        Status: RunStatus option
        /// <summary>True when the run moved the node's value, during the run or at its flight's settle.</summary>
        Moved: bool
        /// <summary>The end of the run's cause chain.</summary>
        Root: WhyRoot
        /// <summary>The number of the flush the run started in, or 0 outside every flush.</summary>
        Flush: int
        /// <summary>The value recorded by the run's <c>Moved</c>, else by the latest earlier run's.</summary>
        Value: obj option
    }

/// <summary>A node's recorded runs, in <c>Trace.history</c>.</summary>
type TraceHistory =
    {
        Node: int
        /// <summary>The runs, oldest first.</summary>
        Runs: TraceRun list
    }

/// <summary>A flight's state, in <c>TraceFlight</c>.</summary>
[<RequireQualifiedAccess>]
type TraceFlightState =
    /// <summary>The log records the flight's start alone.</summary>
    | InFlight
    /// <summary>The flight settled at <c>seq</c>. <c>held</c> marks a value that left the node pending on a newer run.</summary>
    | Settled of seq: int * held: bool
    /// <summary>The flight failed at <c>seq</c>, cancelled when <c>cancelled</c> is true.</summary>
    | Failed of seq: int * cancelled: bool
    /// <summary>The flight's result was discarded at <c>seq</c>.</summary>
    | Dropped of seq: int * reason: TraceDropReason

/// <summary>One flight of an async memo, in <c>TraceWaiting</c>.</summary>
type TraceFlight =
    {
        /// <summary>The flight number, from 1.</summary>
        Flight: int
        /// <summary>The seq of the flight's <c>FlightStart</c>.</summary>
        Start: int
        /// <summary>The number of the run that started the flight, or 0 when the log does not record it.</summary>
        Run: int
        State: TraceFlightState
    }

/// <summary>What a node waits on, in <c>Trace.waitingOn</c>.</summary>
type TraceWaiting =
    {
        Node: int
        /// <summary>The pending sources read by the node's last run, in read order.</summary>
        Sources: int list
        /// <summary>The node's flights, newest first.</summary>
        Flights: TraceFlight list
    }

/// <summary>A node's state in a <c>TraceSnapshot</c>.</summary>
[<RequireQualifiedAccess>]
type TraceNodeStatus =
    /// <summary>Created and not yet run.</summary>
    | Fresh
    /// <summary>A run is open.</summary>
    | Running
    /// <summary>The last run ended with the <c>RunStatus</c>.</summary>
    | Ended of RunStatus
    | Disposed

/// <summary>A node in a <c>TraceSnapshot</c>.</summary>
type TraceSnapshotNode =
    {
        Id: int
        Kind: TraceNodeKind
        /// <summary>The owner id recorded by the node's <c>NodeNew</c>, or 0.</summary>
        Owner: int
        /// <summary>The identity path, without its <c>@k</c> suffix.</summary>
        Path: string
        /// <summary>The <c>k</c> of the node's <c>@k</c> suffix: 1 for the first node to hold the path.</summary>
        Incarnation: int
        Label: string option
        /// <summary>The creation site, or <c>"?"</c>.</summary>
        Site: string
        /// <summary>The seq of the node's <c>NodeNew</c>.</summary>
        Created: int
        /// <summary>The number of runs recorded.</summary>
        Runs: int
        /// <summary>The seq of the node's last <c>RunStart</c>, or 0.</summary>
        LastRun: int
        /// <summary>The first dirty <c>Mark</c> seq since the last <c>RunStart</c>, or 0.</summary>
        PendingMark: int
        Status: TraceNodeStatus
        /// <summary>
        /// The <c>valueText</c> of the latest value or exception recorded by a moving <c>Write</c>, a <c>Moved</c>, or
        /// an async source's <c>Settle</c> or <c>Fail</c>.
        /// </summary>
        Value: string option
    }

/// <summary>An owner in a <c>TraceSnapshot</c>.</summary>
type TraceSnapshotOwner =
    {
        Id: int
        /// <summary>The parent owner id, or 0 for the graph root.</summary>
        Parent: int
        /// <summary>The host node id of a run scope, or 0.</summary>
        Host: int
        /// <summary>True for a <c>createRoot</c> scope.</summary>
        Root: bool
        /// <summary>The identity path. A run scope shares its host's path; the graph root's is empty.</summary>
        Path: string
        Label: string option
        Disposed: bool
    }

/// <summary>A graph's state folded from its trace events up to <c>Seq</c>.</summary>
type TraceSnapshot =
    {
        /// <summary>The seq of the last event folded, or 0.</summary>
        Seq: int
        /// <summary>The graph root's owner id, or 0 before <c>GraphNew</c>.</summary>
        Root: int
        Nodes: Map<int, TraceSnapshotNode>
        Owners: Map<int, TraceSnapshotOwner>
        /// <summary>Each computation's sources, in slot order. Empty lists are left out.</summary>
        Sources: Map<int, int list>
        /// <summary>Each source's observers. Empty sets are left out.</summary>
        Observers: Map<int, Set<int>>
        /// <summary>
        /// By parent path, the number of children created with each segment text since the parent's last run. The
        /// <c>createRoot</c> count is keyed <c>root#</c>.
        /// </summary>
        Siblings: Map<string, Map<string, int>>
        /// <summary>By path, the number of nodes that have held it.</summary>
        Incarnations: Map<string, int>
    }

/// <summary>A parsed JSONL dump: its header, the snapshot at <c>SeqFrom</c> and the events after it.</summary>
/// <remarks>Parsed event payloads are strings, or null.</remarks>
type TraceDump =
    {
        Target: string
        /// <summary>The graph's root owner id.</summary>
        Graph: int
        /// <summary>The checkpoint file holding earlier events, or null.</summary>
        Checkpoint: string
        /// <summary>The seq of the first event after the snapshot.</summary>
        SeqFrom: int
        Snapshot: TraceSnapshot
        Events: TraceEvent[]
    }

/// <summary>Pure queries over an array of trace events, oldest first.</summary>
/// <remarks>
/// An array may start after seq 1 when earlier events were written to a checkpoint; a cause before the first event
/// resolves to <c>BeforeCheckpoint</c>.
/// </remarks>
[<RequireQualifiedAccess>]
module TraceModel =
    let private find (events: TraceEvent[]) (seq: int) : TraceEvent voption =
        if events.Length = 0 then
            ValueNone
        else
            let i = seq - events[0].Seq

            if i >= 0 && i < events.Length && events[i].Seq = seq then
                ValueSome events[i]
            else
                match events |> Array.tryFind (fun e -> e.Seq = seq) with
                | Some e -> ValueSome e
                | None -> ValueNone

    /// <summary>
    /// A value as one line of at most 60 characters: an exception as its type name and message, a string as itself,
    /// and any other value as its <c>%A</c> text. On .NET an anonymous record reads <c>{| ... |}</c> under every
    /// FSharp.Core version.
    /// </summary>
    let valueText (value: obj) : string =
        let lines (text: string) =
            text.Split ([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.map _.Trim()
            |> Array.filter (fun p -> p.Length > 0)

        let line =
            match value with
#if FABLE_COMPILER
            | :? exn as ex -> String.Join (" ", lines ex.Message)
#else
            | :? exn as ex -> String.Join (" ", lines (ex.GetType().Name + ": " + ex.Message))
#endif
            | :? string as s -> String.Join (" ", lines s)
#if FABLE_COMPILER
            | :? float as f -> string f
#else
            | :? float as f -> f.ToString ("R", Globalization.CultureInfo.InvariantCulture)
#endif
            | v ->
                // %A puts each record field on its own line; the join restores the "; " separator.
                let parts = lines (sprintf "%A" v)
                let joined = StringBuilder ()

                for i in 0 .. parts.Length - 1 do
                    if i > 0 then
                        let prev = parts[i - 1]
                        let opens = "[{(;,".IndexOf(prev[prev.Length - 1]) >= 0
                        let closes = "]})|".IndexOf(parts[i][0]) >= 0

                        joined.Append (if opens || closes then " " else "; ")
                        |> ignore

                    joined.Append parts[i] |> ignore

                let text = joined.ToString ()

#if FABLE_COMPILER
                text
#else
                // FSharp.Core releases differ in whether %A brackets an anonymous record with "{|".
                if
                    v.GetType().Name.StartsWith("<>f__AnonymousType", StringComparison.Ordinal)
                    && text.StartsWith ("{ ", StringComparison.Ordinal)
                    && text.EndsWith (" }", StringComparison.Ordinal)
                then
                    "{| "
                    + text.Substring (2, text.Length - 4)
                    + " |}"
                else
                    text
#endif

        if line.Length > 60 then
            line.Substring (0, 59) + "…"
        else
            line

    /// <summary>
    /// The source lists folded from <c>EdgeAdd</c>/<c>EdgeRemove</c>, by computation id, in slot order. Lists that
    /// fold to empty are left out.
    /// </summary>
    let sources (events: TraceEvent[]) : Map<int, int list> =
        let lists = Dictionary<int, ResizeArray<int>>()

        let listOf node =
            match lists.TryGetValue node with
            | true, list -> list
            | _ ->
                let list = ResizeArray ()
                lists[node] <- list
                list

        for e in events do
            match e.Kind with
            | TraceEventKind.EdgeAdd ->
                let list = listOf e.Node

                if list.Count > e.Arg then
                    list.RemoveRange (e.Arg, list.Count - e.Arg)

                list.Add e.Other
            | TraceEventKind.EdgeRemove ->
                let list = listOf e.Node

                if list.Count > e.Arg then
                    list.RemoveRange (e.Arg, list.Count - e.Arg)
            | _ -> ()

        lists
        |> Seq.filter (fun kv -> kv.Value.Count > 0)
        |> Seq.map (fun kv -> kv.Key, List.ofSeq kv.Value)
        |> Map.ofSeq

    /// <summary>
    /// The observer sets folded from <c>ObserverAdd</c>/<c>ObserverRemove</c>, by source id. Sets that fold to empty
    /// are left out.
    /// </summary>
    let observers (events: TraceEvent[]) : Map<int, Set<int>> =
        let sets = Dictionary<int, HashSet<int>>()

        for e in events do
            match e.Kind with
            | TraceEventKind.ObserverAdd ->
                match sets.TryGetValue e.Node with
                | true, set -> set.Add e.Other |> ignore
                | _ -> sets[e.Node] <- HashSet [ e.Other ]
            | TraceEventKind.ObserverRemove ->
                match sets.TryGetValue e.Node with
                | true, set -> set.Remove e.Other |> ignore
                | _ -> ()
            | _ -> ()

        sets
        |> Seq.filter (fun kv -> kv.Value.Count > 0)
        |> Seq.map (fun kv -> kv.Key, Set.ofSeq kv.Value)
        |> Map.ofSeq

    /// <summary>
    /// The cause chain of run number <c>run</c> of <c>node</c>, or of its last run when <c>run</c> is 0, stopped after
    /// <c>depth</c> steps when <c>depth</c> is positive. <c>checkpoint</c> names the file holding earlier events, or is
    /// null.
    /// </summary>
    /// <exception cref="T:System.ArgumentException">The events hold no matching <c>RunStart</c> for <c>node</c>.</exception>
    let whyDepth (events: TraceEvent[]) (checkpoint: string) (depth: int) (node: int) (run: int) : Why =
        let start =
            events
            |> Array.tryFindBack (fun e ->
                e.Kind = TraceEventKind.RunStart
                && e.Node = node
                && (run = 0 || e.Arg = run))

        match start with
        | None ->
            raise (
                ArgumentException (
                    "The trace log holds no RunStart for node "
                    + string node
                    + " run "
                    + string run
                    + ".",
                    nameof run
                )
            )
        | Some start ->
            let steps = ResizeArray<WhyStep>()

            let step (e: TraceEvent) =
                steps.Add
                    {
                        Seq = e.Seq
                        Kind = e.Kind
                        Node = e.Node
                        Other = e.Other
                        Value =
                            match e.Kind with
                            | TraceEventKind.Write
                            | TraceEventKind.Moved
                            | TraceEventKind.Settle
                            | TraceEventKind.Fail -> Option.ofObj e.Payload
                            | _ -> None
                    }

            let rec walk (e: TraceEvent) : WhyRoot option =
                if depth > 0 && steps.Count >= depth then
                    None
                else
                    step e

                    let next (cause: int) (orElse: unit -> WhyRoot) =
                        if cause = 0 then
                            Some (orElse ())
                        else
                            match find events cause with
                            | ValueSome c -> walk c
                            | ValueNone when events.Length > 0 && cause < events[0].Seq -> Some (BeforeCheckpoint checkpoint)
                            | ValueNone -> Some (Unrecorded e.Seq)

                    match e.Kind with
                    | TraceEventKind.RunStart ->
                        next e.Cause (fun () ->
                            if e.Arg = 1 then
                                events
                                |> Array.tryFind (fun n -> n.Kind = TraceEventKind.NodeNew && n.Node = e.Node)
                                |> Option.map (fun n -> n.Seq)
                                |> Option.defaultValue 0
                                |> Created
                            elif e.Other <> 0 then
                                Pulled e.Other
                            else
                                Unrecorded e.Seq)
                    | TraceEventKind.Write ->
                        if e.Other = 0 then
                            Some (UserWrite e.Seq)
                        else
                            next e.Cause (fun () -> Unrecorded e.Seq)
                    | TraceEventKind.Settle
                    | TraceEventKind.Fail -> next e.Cause (fun () -> Settled e.Seq)
                    | _ -> next e.Cause (fun () -> Unrecorded e.Seq)

            let root = walk start

            {
                Node = node
                Run = start.Arg
                Steps = List.ofSeq steps
                Root = root
            }

    /// <summary>The full cause chain of run number <c>run</c> of <c>node</c>, or of its last run when <c>run</c> is 0.</summary>
    let why (events: TraceEvent[]) (checkpoint: string) (node: int) (run: int) : Why =
        whyDepth events checkpoint 0 node run

    /// <summary>
    /// The run number a <c>Moved</c> belongs to: the run that started the settled flight when a <c>Settle</c> caused
    /// it, else its <c>Arg</c>.
    /// </summary>
    let private movedRun (events: TraceEvent[]) (moved: TraceEvent) =
        let fromFlight =
            match find events moved.Cause with
            | ValueSome settle when
                settle.Kind = TraceEventKind.Settle
                || settle.Kind = TraceEventKind.Fail
                ->
                match find events settle.Cause with
                | ValueSome flight when flight.Kind = TraceEventKind.FlightStart ->
                    match find events flight.Cause with
                    | ValueSome start when start.Kind = TraceEventKind.RunStart -> ValueSome start.Arg
                    | _ -> ValueNone
                | _ -> ValueNone
            | _ -> ValueNone

        defaultValueArg fromFlight moved.Arg

    /// <summary>
    /// Every recorded run of <c>node</c>, oldest first. <c>checkpoint</c> names the file holding earlier events, or is
    /// null.
    /// </summary>
    let history (events: TraceEvent[]) (checkpoint: string) (node: int) : TraceHistory =
        let ends = Dictionary<int, RunStatus>()
        let moved = Dictionary<int, obj>()
        let flushes = ResizeArray<int>()
        let starts = ResizeArray<struct (TraceEvent * int)>()

        for e in events do
            match e.Kind with
            | TraceEventKind.FlushStart -> flushes.Add e.Arg
            | TraceEventKind.FlushEnd when flushes.Count > 0 -> flushes.RemoveAt (flushes.Count - 1)
            | TraceEventKind.RunStart when e.Node = node ->
                let flush = if flushes.Count = 0 then 0 else flushes[flushes.Count - 1]
                starts.Add (struct (e, flush))
            | TraceEventKind.RunEnd when e.Node = node -> ends[e.Cause] <- enum<RunStatus> e.Arg
            | TraceEventKind.Moved when e.Node = node -> moved[movedRun events e] <- e.Payload
            | _ -> ()

        let mutable value = None

        let runs =
            [
                for struct (start, flush) in starts do
                    match moved.TryGetValue start.Arg with
                    | true, payload -> value <- Option.ofObj payload
                    | _ -> ()

                    {
                        Run = start.Arg
                        Start = start.Seq
                        Status =
                            match ends.TryGetValue start.Seq with
                            | true, status -> Some status
                            | _ -> None
                        Moved = moved.ContainsKey start.Arg
                        Root =
                            (why events checkpoint node start.Arg).Root
                            |> Option.defaultValue (Unrecorded start.Seq)
                        Flush = flush
                        Value = value
                    }
            ]

        { Node = node; Runs = runs }

    /// <summary>The pending sources read by <c>node</c>'s last run, and the node's flights.</summary>
    let waitingOn (events: TraceEvent[]) (node: int) : TraceWaiting =
        let lastRun =
            events
            |> Array.tryFindBack (fun e -> e.Kind = TraceEventKind.RunStart && e.Node = node)
            |> Option.map _.Seq
            |> Option.defaultValue -1

        let sources =
            events
            |> Array.filter (fun e ->
                e.Kind = TraceEventKind.Suspend
                && e.Node = node
                && e.Cause = lastRun)
            |> Array.map _.Other
            |> Array.distinct
            |> List.ofArray

        let states = Dictionary<int, TraceFlightState>()

        for e in events do
            if e.Node = node && e.Cause <> 0 then
                match e.Kind with
                | TraceEventKind.Settle -> states[e.Cause] <- TraceFlightState.Settled (e.Seq, e.Flag <> 0)
                | TraceEventKind.Fail -> states[e.Cause] <- TraceFlightState.Failed (e.Seq, e.Flag = 1)
                | TraceEventKind.FlightDrop -> states[e.Cause] <- TraceFlightState.Dropped (e.Seq, enum<TraceDropReason> e.Flag)
                | _ -> ()

        let flights =
            [
                for e in Array.rev events do
                    if
                        e.Kind = TraceEventKind.FlightStart
                        && e.Node = node
                    then
                        {
                            Flight = e.Arg
                            Start = e.Seq
                            Run =
                                match find events e.Cause with
                                | ValueSome start when start.Kind = TraceEventKind.RunStart -> start.Arg
                                | _ -> 0
                            State =
                                match states.TryGetValue e.Seq with
                                | true, state -> state
                                | _ -> TraceFlightState.InFlight
                        }
            ]

        {
            Node = node
            Sources = sources
            Flights = flights
        }

    /// <summary>
    /// The first <c>WhyNotReason</c> matching the events after <c>node</c>'s last <c>RunEnd</c>, or after its
    /// <c>NodeNew</c> when it has not run; <c>None</c> when none matches.
    /// </summary>
    /// <remarks>
    /// Reasons are tried in the order <c>Disposed</c>, <c>Queued</c>, <c>Unobserved</c>, <c>CheckedClean</c>,
    /// <c>SkippedAsRunningReader</c>, <c>NotReached</c>. <c>SkippedAsRunningReader</c> also matches a skip inside
    /// the last run.
    /// </remarks>
    let whyNot (events: TraceEvent[]) (node: int) : WhyNotReason option =
        let from =
            let last =
                events
                |> Array.tryFindIndexBack (fun e ->
                    (e.Kind = TraceEventKind.RunEnd
                     || e.Kind = TraceEventKind.NodeNew)
                    && e.Node = node)

            defaultArg (Option.map ((+) 1) last) 0

        let window = events[from..]

        let on kind (e: TraceEvent) =
            e.Kind = kind && e.Node = node

        let first kind =
            window |> Array.tryFind (on kind)

        let disposed () =
            first TraceEventKind.Dispose
            |> Option.map (fun e -> Disposed e.Seq)

        let answered (e: TraceEvent) =
            on TraceEventKind.RunStart e
            || (on TraceEventKind.CheckResolved e && e.Flag = 0)
            || e.Kind = TraceEventKind.FlushEnd

        let queued () =
            window
            |> Array.tryFindIndexBack (on TraceEventKind.Schedule)
            |> Option.bind (fun i ->
                if window[i + 1 ..] |> Array.exists answered then
                    None
                else
                    Some (Queued window[i].Seq))

        let pulled (e: TraceEvent) =
            on TraceEventKind.RunStart e
            || on TraceEventKind.Schedule e
            || on TraceEventKind.CheckStart e
            || on TraceEventKind.CheckResolved e

        let unobserved () =
            match first TraceEventKind.Mark with
            | Some mark when
                not (observers events |> Map.containsKey node)
                && not (window |> Array.exists pulled)
                ->
                Some (Unobserved mark.Seq)
            | _ -> None

        let checkedClean () =
            window
            |> Array.tryFindBack (fun e -> on TraceEventKind.CheckResolved e && e.Flag = 0)
            |> Option.map (fun e ->
                let upstream =
                    sources events[.. e.Seq - events[0].Seq]
                    |> Map.tryFind node
                    |> Option.defaultValue []

                CheckedClean (e.Seq, upstream))

        let skipped () =
            let lastRun =
                events
                |> Array.tryFindIndexBack (on TraceEventKind.RunStart)
                |> Option.defaultValue from

            events[min lastRun from ..]
            |> Array.tryFind (on TraceEventKind.MarkSkip)
            |> Option.map (fun e -> SkippedAsRunningReader e.Seq)

        let notReached () =
            let lists = sources events
            let seen = HashSet<int>()
            let upstream = ResizeArray<int>()

            let rec visit n =
                for s in lists |> Map.tryFind n |> Option.defaultValue [] do
                    if seen.Add s then
                        upstream.Add s
                        visit s

            visit node

            window
            |> Array.tryFindBack (fun e ->
                seen.Contains e.Node
                && e.Flag = 0
                && (e.Kind = TraceEventKind.Write
                    || e.Kind = TraceEventKind.RunEnd))
            |> Option.map (fun e -> NotReached e.Seq)

        // A clean check with no moving write since a skip is the skip's consequence.
        let skippedThenClean () =
            match skipped (), checkedClean () with
            | Some (SkippedAsRunningReader skip as reason), Some (CheckedClean (resolved, _)) when
                skip < resolved
                && not (
                    events
                    |> Array.exists (fun e ->
                        e.Seq > skip
                        && e.Seq < resolved
                        && e.Kind = TraceEventKind.Write
                        && e.Flag = 1)
                )
                ->
                Some reason
            | _ -> None

        [
            disposed
            queued
            unobserved
            skippedThenClean
            checkedClean
            skipped
            notReached
        ]
        |> List.tryPick (fun reason -> reason ())

    // -----------------------------------------------------------------------------------------------------------
    // Identity paths and snapshots

    /// <summary>The fold state before any event.</summary>
    let emptySnapshot: TraceSnapshot =
        {
            Seq = 0
            Root = 0
            Nodes = Map.empty
            Owners = Map.empty
            Sources = Map.empty
            Observers = Map.empty
            Siblings = Map.empty
            Incarnations = Map.empty
        }

    /// <summary><c>text</c> with each of <c>/ # [ ] @ \</c> prefixed by a backslash.</summary>
    let escape (text: string) : string =
        let sb = StringBuilder text.Length

        for c in text do
            match c with
            | '/'
            | '#'
            | '['
            | ']'
            | '@'
            | '\\' -> sb.Append('\\').Append c |> ignore
            | c -> sb.Append c |> ignore

        sb.ToString ()

    [<Literal>]
    let private RootKey = "root#"

    let private siteOf (payload: obj) =
        match payload with
        | :? string as s when s <> "" -> s
        | _ -> "?"

    /// <summary><c>state</c> advanced by <c>events</c>, which follow <c>state.Seq</c> in order.</summary>
    /// <remarks>
    /// A path segment is the node's label, else its site, else its kind; a <c>createRoot</c> scope is <c>root#n</c>
    /// and a run scope takes its host's path. A repeated segment under one parent takes <c>#n</c>, counted since the
    /// parent's last run.
    /// </remarks>
    let fold (state: TraceSnapshot) (events: TraceEvent[]) : TraceSnapshot =
        let mutable s = state

        let parentPath owner =
            let owner = if owner = 0 then s.Root else owner

            match s.Owners.TryFind owner with
            | Some o -> o.Path
            | None -> ""

        let next (parent: string) (text: string) =
            let perParent =
                s.Siblings.TryFind parent
                |> Option.defaultValue Map.empty

            let n = perParent.TryFind text |> Option.defaultValue 0

            s <-
                { s with
                    Siblings = s.Siblings.Add (parent, perParent.Add (text, n + 1))
                }

            n

        let child parent (text: string) =
            match next parent text with
            | 0 -> parent + "/" + text
            | n -> parent + "/" + text + "#" + string n

        let segment (label: string option) (site: string) (fallback: string) =
            match label with
            | Some label -> escape label
            | None when site <> "?" -> escape site
            | None -> fallback

        let labelAt i id =
            if i + 1 < events.Length then
                let l = events[i + 1]

                if
                    l.Kind = TraceEventKind.Label
                    && l.Arg = 0
                    && l.Node = id
                    && id <> 0
                then
                    Some (string l.Payload)
                else
                    None
            else
                None

        let updateNode id f =
            match s.Nodes.TryFind id with
            | Some n -> s <- { s with Nodes = s.Nodes.Add (id, f n) }
            | None -> ()

        let valueOf (payload: obj) =
            Option.ofObj payload |> Option.map valueText

        for i in 0 .. events.Length - 1 do
            let e = events[i]

            match e.Kind with
            | TraceEventKind.GraphNew ->
                let root =
                    {
                        Id = e.Other
                        Parent = 0
                        Host = 0
                        Root = false
                        Path = ""
                        Label = None
                        Disposed = false
                    }

                s <-
                    { s with
                        Root = e.Other
                        Owners = s.Owners.Add (e.Other, root)
                    }
            | TraceEventKind.NodeNew ->
                let kind = enum<TraceNodeKind> e.Arg
                let label = labelAt i e.Node
                let site = siteOf e.Payload

                let path =
                    child (parentPath e.Other) (segment label site (TraceNames.nodeKind kind))

                let k =
                    (s.Incarnations.TryFind path
                     |> Option.defaultValue 0)
                    + 1

                let node =
                    {
                        Id = e.Node
                        Kind = kind
                        Owner = e.Other
                        Path = path
                        Incarnation = k
                        Label = label
                        Site = site
                        Created = e.Seq
                        Runs = 0
                        LastRun = 0
                        PendingMark = 0
                        Status = TraceNodeStatus.Fresh
                        Value = None
                    }

                s <-
                    { s with
                        Incarnations = s.Incarnations.Add (path, k)
                        Nodes = s.Nodes.Add (e.Node, node)
                    }
            | TraceEventKind.OwnerNew ->
                let parent = parentPath e.Other
                let label = if e.Arg = 0 then labelAt i e.Node else None

                let path =
                    if e.Arg <> 0 then
                        match s.Nodes.TryFind e.Arg with
                        | Some host -> host.Path
                        | None -> parent
                    elif e.Flag = 1 then
                        parent
                        + "/"
                        + RootKey
                        + string (next parent RootKey)
                    else
                        child parent (segment label (siteOf e.Payload) "owner")

                let owner =
                    {
                        Id = e.Node
                        Parent = e.Other
                        Host = e.Arg
                        Root = (e.Flag = 1)
                        Path = path
                        Label = label
                        Disposed = false
                    }

                s <-
                    { s with
                        Owners = s.Owners.Add (e.Node, owner)
                    }
            | TraceEventKind.Dispose ->
                updateNode e.Node (fun n ->
                    { n with
                        Status = TraceNodeStatus.Disposed
                    })
            | TraceEventKind.OwnerDispose ->
                match s.Owners.TryFind e.Node with
                | Some o ->
                    s <-
                        { s with
                            Owners = s.Owners.Add (e.Node, { o with Disposed = true })
                        }
                | None -> ()
            | TraceEventKind.Write when e.Flag = 1 -> updateNode e.Node (fun n -> { n with Value = valueOf e.Payload })
            | TraceEventKind.Moved when not (isNull e.Payload) -> updateNode e.Node (fun n -> { n with Value = valueOf e.Payload })
            | TraceEventKind.Settle
            | TraceEventKind.Fail when e.Arg = 0 -> updateNode e.Node (fun n -> { n with Value = valueOf e.Payload })
            | TraceEventKind.Mark when e.Arg = 2 ->
                updateNode e.Node (fun n ->
                    if n.PendingMark = 0 then
                        { n with PendingMark = e.Seq }
                    else
                        n)
            | TraceEventKind.RunStart ->
                match s.Nodes.TryFind e.Node with
                | Some n ->
                    let n =
                        { n with
                            Runs = n.Runs + 1
                            LastRun = e.Seq
                            PendingMark = 0
                            Status =
                                (if n.Status = TraceNodeStatus.Disposed then
                                     TraceNodeStatus.Disposed
                                 else
                                     TraceNodeStatus.Running)
                        }

                    s <-
                        { s with
                            Siblings = s.Siblings.Remove n.Path
                            Nodes = s.Nodes.Add (e.Node, n)
                        }
                | None -> ()
            | TraceEventKind.RunEnd ->
                updateNode e.Node (fun n ->
                    if n.Status = TraceNodeStatus.Disposed then
                        n
                    else
                        { n with
                            Status = TraceNodeStatus.Ended (enum<RunStatus> e.Arg)
                        })
            | TraceEventKind.EdgeAdd
            | TraceEventKind.EdgeRemove ->
                let kept =
                    s.Sources.TryFind e.Node
                    |> Option.defaultValue []
                    |> List.truncate e.Arg

                let list =
                    if e.Kind = TraceEventKind.EdgeAdd then
                        kept @ [ e.Other ]
                    else
                        kept

                s <-
                    { s with
                        Sources =
                            (if list.IsEmpty then
                                 s.Sources.Remove e.Node
                             else
                                 s.Sources.Add (e.Node, list))
                    }
            | TraceEventKind.ObserverAdd
            | TraceEventKind.ObserverRemove ->
                let set =
                    s.Observers.TryFind e.Node
                    |> Option.defaultValue Set.empty

                let set =
                    if e.Kind = TraceEventKind.ObserverAdd then
                        set.Add e.Other
                    else
                        set.Remove e.Other

                s <-
                    { s with
                        Observers =
                            (if set.IsEmpty then
                                 s.Observers.Remove e.Node
                             else
                                 s.Observers.Add (e.Node, set))
                    }
            | TraceEventKind.Label when e.Arg = 1 ->
                match s.Nodes.TryFind e.Node with
                | Some n ->
                    let label = string e.Payload
                    let old = n.Path

                    let path =
                        child (parentPath n.Owner) (segment (Some label) n.Site (TraceNames.nodeKind n.Kind))

                    let k =
                        (s.Incarnations.TryFind path
                         |> Option.defaultValue 0)
                        + 1

                    let moved (p: string) =
                        if p = old then
                            path
                        elif p.StartsWith (old + "/", StringComparison.Ordinal) then
                            path + p.Substring old.Length
                        else
                            p

                    s <-
                        { s with
                            Incarnations = s.Incarnations.Add (path, k)
                            Nodes =
                                s.Nodes
                                |> Map.map (fun id m ->
                                    if id = e.Node then
                                        { m with
                                            Path = path
                                            Label = Some label
                                            Incarnation = k
                                        }
                                    else
                                        { m with Path = moved m.Path })
                            Owners =
                                s.Owners
                                |> Map.map (fun _ o -> { o with Path = moved o.Path })
                            Siblings =
                                s.Siblings
                                |> Map.toSeq
                                |> Seq.map (fun (p, m) -> moved p, m)
                                |> Map.ofSeq
                        }
                | None -> ()
            | _ -> ()

            s <- { s with Seq = e.Seq }

        s

    /// <summary>The state folded from the events up to and including seq <c>seq</c>.</summary>
    let snapshotAt (events: TraceEvent[]) (seq: int) : TraceSnapshot =
        fold emptySnapshot (events |> Array.filter (fun e -> e.Seq <= seq))

    /// <summary>The state folded from every event.</summary>
    let snapshot (events: TraceEvent[]) : TraceSnapshot =
        fold emptySnapshot events

    /// <summary>
    /// The node id at <c>path</c>. A <c>@k</c> suffix selects the k-th node to hold the path; a bare path selects the
    /// live holder, else the latest.
    /// </summary>
    let resolve (snapshot: TraceSnapshot) (path: string) : int option =
        let at = path.LastIndexOf '@'

        let bare, k =
            if at > 0 && path[at - 1] <> '\\' then
                match Int32.TryParse (path.Substring (at + 1)) with
                | true, k -> path.Substring (0, at), Some k
                | _ -> path, None
            else
                path, None

        let holders =
            snapshot.Nodes.Values
            |> Seq.filter (fun n -> n.Path = bare)
            |> Seq.sortBy (fun n -> n.Incarnation)
            |> List.ofSeq

        match k with
        | Some k ->
            holders
            |> List.tryFind (fun n -> n.Incarnation = k)
            |> Option.map (fun n -> n.Id)
        | None ->
            holders
            |> List.tryFindBack (fun n -> n.Status <> TraceNodeStatus.Disposed)
            |> Option.orElse (List.tryLast holders)
            |> Option.map (fun n -> n.Id)

    // -----------------------------------------------------------------------------------------------------------
    // Rendering

    /// <summary>The node's path, with its <c>@k</c> suffix when more than one node has held the path.</summary>
    let pathOf (snapshot: TraceSnapshot) (node: int) : string =
        match snapshot.Nodes.TryFind node with
        | Some n ->
            if
                snapshot.Incarnations.TryFind n.Path
                |> Option.exists (fun k -> k > 1)
            then
                n.Path + "@" + string n.Incarnation
            else
                n.Path
        | None when node = 0 -> "-"
        | None -> "#" + string node

    let private kindPlural (kind: TraceNodeKind) =
        match kind with
        | TraceNodeKind.Memo -> "memos"
        | TraceNodeKind.Effect -> "effects"
        | TraceNodeKind.Signal -> "signals"
        | TraceNodeKind.AsyncSource -> "async sources"
        | TraceNodeKind.AsyncMemo -> "async memos"
        | TraceNodeKind.Boundary -> "boundaries"
        | TraceNodeKind.Projection -> "projections"
        | TraceNodeKind.ProjectionBeacon -> "beacons"
        | TraceNodeKind.RowWatch -> "row watches"
        | TraceNodeKind.LookupCell -> "lookup cells"
        | _ -> "nodes"

    let private eventLine (snapshot: TraceSnapshot) (e: TraceEvent) =
        let other =
            match e.Kind with
            | TraceEventKind.Mark
            | TraceEventKind.MarkSkip
            | TraceEventKind.EdgeAdd
            | TraceEventKind.EdgeRemove
            | TraceEventKind.ObserverAdd
            | TraceEventKind.ObserverRemove
            | TraceEventKind.RunStart
            | TraceEventKind.Write
            | TraceEventKind.CheckResolved when e.Other <> 0 -> " <- " + pathOf snapshot e.Other
            | _ -> ""

        let node = if e.Node = 0 then "" else " " + pathOf snapshot e.Node

        "#"
        + string e.Seq
        + " "
        + TraceNames.eventKind e.Kind
        + node
        + other

    /// <summary>
    /// One line per event. Within a run of consecutive <c>Mark</c>, <c>MarkSkip</c> and <c>Schedule</c> events, the
    /// marks sharing a cause, a mark kind, a node kind and a site fold into one line at the first of them.
    /// </summary>
    let renderEvents (snapshot: TraceSnapshot) (events: TraceEvent[]) : string =
        let lines = ResizeArray<string>()

        let inBurst (e: TraceEvent) =
            e.Kind = TraceEventKind.Mark
            || e.Kind = TraceEventKind.MarkSkip
            || e.Kind = TraceEventKind.Schedule

        let keyOf (e: TraceEvent) =
            match snapshot.Nodes.TryFind e.Node with
            | Some n -> struct (e.Cause, e.Arg, n.Kind, n.Site)
            | None -> struct (e.Cause, e.Arg, enum 0, "#" + string e.Node)

        let mutable i = 0

        while i < events.Length do
            if events[i].Kind = TraceEventKind.Mark then
                let mutable j = i

                while j < events.Length && inBurst events[j] do
                    j <- j + 1

                let burst = events[i .. j - 1]

                let counts =
                    burst
                    |> Array.filter (fun e -> e.Kind = TraceEventKind.Mark)
                    |> Array.countBy keyOf
                    |> dict

                let shown = HashSet<struct (int * int * TraceNodeKind * string)>()

                for e in burst do
                    if
                        e.Kind = TraceEventKind.Mark
                        && counts[keyOf e] > 1
                    then
                        let key = keyOf e

                        if shown.Add key then
                            let struct (cause, arg, kind, site) = key
                            let mark = if arg = 2 then "dirty" else "check"

                            lines.Add (
                                "#"
                                + string e.Seq
                                + " × "
                                + string counts[key]
                                + " "
                                + kindPlural kind
                                + " marked "
                                + mark
                                + " via "
                                + site
                                + " (cause #"
                                + string cause
                                + ")"
                            )
                    else
                        lines.Add (eventLine snapshot e)

                i <- j
            else
                lines.Add (eventLine snapshot events[i])
                i <- i + 1

        String.Join ("\n", lines)

    let private rootText (snapshot: TraceSnapshot) (root: WhyRoot option) =
        match root with
        | None -> "stopped at depth"
        | Some (UserWrite seq) -> "user write #" + string seq
        | Some (Created seq) -> "created #" + string seq
        | Some (Pulled reader) -> "pulled by " + pathOf snapshot reader
        | Some (BeforeCheckpoint null) -> "before the checkpoint"
        | Some (BeforeCheckpoint file) -> "before checkpoint " + file
        | Some (Unrecorded seq) -> "unrecorded after #" + string seq
        | Some (Settled seq) -> "settle #" + string seq

    /// <summary>The cause chain as text: a heading, one line per step with its site, and the root.</summary>
    let renderWhy (snapshot: TraceSnapshot) (why: Why) : string =
        let lines = ResizeArray<string>()

        lines.Add (
            "why "
            + pathOf snapshot why.Node
            + " run "
            + string why.Run
        )

        for step in why.Steps do
            let other =
                if step.Other = 0 then
                    ""
                else
                    " <- " + pathOf snapshot step.Other

            let site =
                match snapshot.Nodes.TryFind step.Node with
                | Some n when n.Site <> "?" -> " (" + n.Site + ")"
                | _ -> ""

            let value =
                match step.Value with
                | Some v -> " = " + valueText v
                | None -> ""

            lines.Add (
                "  #"
                + string step.Seq
                + " "
                + TraceNames.eventKind step.Kind
                + " "
                + pathOf snapshot step.Node
                + other
                + site
                + value
            )

        lines.Add ("  root: " + rootText snapshot why.Root)
        String.Join ("\n", lines)

    /// <summary>The reason as one line.</summary>
    let renderWhyNot (snapshot: TraceSnapshot) (reason: WhyNotReason option) : string =
        match reason with
        | None -> "no reason recorded"
        | Some (Disposed seq) -> "disposed #" + string seq
        | Some (Queued seq) -> "queued #" + string seq
        | Some (Unobserved seq) -> "unobserved since mark #" + string seq
        | Some (CheckedClean (seq, upstream)) ->
            "checked clean #"
            + string seq
            + " over "
            + String.Join (", ", upstream |> List.map (pathOf snapshot))
        | Some (SkippedAsRunningReader seq) -> "skipped as the running reader #" + string seq
        | Some (NotReached seq) ->
            "not reached: propagation stopped at #"
            + string seq

    let private runStatusText (status: RunStatus) =
        match status with
        | RunStatus.Ok -> "ok"
        | RunStatus.Pending -> "pending"
        | RunStatus.Error -> "error"
        | RunStatus.Abandoned -> "abandoned"
        | other -> string (int other)

    /// <summary>The runs as text: a heading, then one line per run with its status, flush and root.</summary>
    let renderHistory (snapshot: TraceSnapshot) (history: TraceHistory) : string =
        let lines = ResizeArray<string>()
        lines.Add ("history " + pathOf snapshot history.Node)

        for r in history.Runs do
            let status =
                match r.Status with
                | Some status -> runStatusText status
                | None -> "running"

            let moved = if r.Moved then " moved" else ""
            let flush = if r.Flush = 0 then "" else " flush " + string r.Flush

            let value =
                match r.Status, r.Value with
                | Some (RunStatus.Ok | RunStatus.Error), Some v -> " = " + valueText v
                | _ -> ""

            lines.Add (
                "  run "
                + string r.Run
                + " #"
                + string r.Start
                + " "
                + status
                + moved
                + flush
                + value
                + " root: "
                + rootText snapshot (Some r.Root)
            )

        String.Join ("\n", lines)

    /// <summary>The suspension sources and flights as text: a heading, then one line per source and per flight.</summary>
    let renderWaiting (snapshot: TraceSnapshot) (waiting: TraceWaiting) : string =
        let lines = ResizeArray<string>()
        lines.Add ("waiting " + pathOf snapshot waiting.Node)

        for source in waiting.Sources do
            lines.Add ("  suspended on " + pathOf snapshot source)

        for f in waiting.Flights do
            let state =
                match f.State with
                | TraceFlightState.InFlight -> "in flight"
                | TraceFlightState.Settled (seq, false) -> "settled #" + string seq
                | TraceFlightState.Settled (seq, true) -> "settled #" + string seq + ", held pending"
                | TraceFlightState.Failed (seq, false) -> "failed #" + string seq
                | TraceFlightState.Failed (seq, true) -> "cancelled #" + string seq
                | TraceFlightState.Dropped (seq, reason) ->
                    "dropped #"
                    + string seq
                    + " "
                    + (TraceNames.dropReason reason).ToLowerInvariant()

            lines.Add (
                "  flight "
                + string f.Flight
                + " #"
                + string f.Start
                + " run "
                + string f.Run
                + " "
                + state
            )

        if waiting.Sources.IsEmpty && waiting.Flights.IsEmpty then
            lines.Add "  nothing recorded"

        String.Join ("\n", lines)

    let private statusText (status: TraceNodeStatus) =
        match status with
        | TraceNodeStatus.Fresh -> "fresh"
        | TraceNodeStatus.Running -> "running"
        | TraceNodeStatus.Ended RunStatus.Ok -> "ok"
        | TraceNodeStatus.Ended RunStatus.Pending -> "pending"
        | TraceNodeStatus.Ended RunStatus.Error -> "error"
        | TraceNodeStatus.Ended RunStatus.Abandoned -> "abandoned"
        | TraceNodeStatus.Ended other -> string (int other)
        | TraceNodeStatus.Disposed -> "disposed"

    /// <summary>
    /// The owner tree as indented text: each owner's nodes with path, kind, status, run count and sources.
    /// </summary>
    let renderSnapshot (snapshot: TraceSnapshot) : string =
        let lines = ResizeArray<string>()

        let children =
            snapshot.Owners.Values
            |> Seq.filter (fun o -> o.Id <> snapshot.Root && o.Host = 0)
            |> Seq.groupBy (fun o -> if o.Parent = 0 then snapshot.Root else o.Parent)
            |> Map.ofSeq

        let scopes =
            snapshot.Owners.Values
            |> Seq.filter (fun o -> o.Host <> 0)
            |> Seq.groupBy (fun o -> o.Host)
            |> Map.ofSeq

        let nodesOf owner =
            snapshot.Nodes.Values
            |> Seq.filter (fun n -> (if n.Owner = 0 then snapshot.Root else n.Owner) = owner)

        let rec owner (indent: string) (id: int) =
            for n in nodesOf id do
                let sources =
                    match snapshot.Sources.TryFind n.Id with
                    | Some list ->
                        " <- "
                        + String.Join (", ", list |> List.map (pathOf snapshot))
                    | None -> ""

                lines.Add (
                    indent
                    + pathOf snapshot n.Id
                    + " "
                    + TraceNames.nodeKind n.Kind
                    + " "
                    + statusText n.Status
                    + " runs "
                    + string n.Runs
                    + sources
                )

                for scope in
                    scopes.TryFind n.Id
                    |> Option.defaultValue Seq.empty do
                    if not scope.Disposed then
                        owner (indent + "  ") scope.Id

            for o in
                children.TryFind id
                |> Option.defaultValue Seq.empty do
                lines.Add (
                    indent
                    + o.Path
                    + (if o.Disposed then " disposed" else "")
                )

                owner (indent + "  ") o.Id

        lines.Add "/"
        owner "  " snapshot.Root
        String.Join ("\n", lines)

    // -----------------------------------------------------------------------------------------------------------
    // JSONL dump, schema 1

    let private jsonString (sb: StringBuilder) (text: string) =
        if isNull text then
            sb.Append "null" |> ignore
        else
            sb.Append '"' |> ignore

            for c in text do
                match c with
                | '"' -> sb.Append "\\\"" |> ignore
                | '\\' -> sb.Append "\\\\" |> ignore
                | '\n' -> sb.Append "\\n" |> ignore
                | '\r' -> sb.Append "\\r" |> ignore
                | '\t' -> sb.Append "\\t" |> ignore
                | c when c < ' ' ->
                    sb.Append("\\u").Append((int c).ToString "x4")
                    |> ignore
                | c -> sb.Append c |> ignore

            sb.Append '"' |> ignore

    let private jsonInts (sb: StringBuilder) (values: int seq) =
        sb.Append '[' |> ignore
        let mutable first = true

        for v in values do
            if not first then
                sb.Append ',' |> ignore

            first <- false
            sb.Append v |> ignore

        sb.Append ']' |> ignore

    /// <summary>
    /// A payload's canonical text: an <c>int</c>, <c>string</c>, <c>bool</c> or <c>float</c> (round-trip) as its
    /// value, null as null, and any other value as its <c>valueText</c>.
    /// </summary>
    let payloadText (payload: obj) : string =
        match payload with
        | null -> null
        | :? string as s -> s
        | :? int as i -> string i
        | :? bool as b -> (if b then "true" else "false")
#if FABLE_COMPILER
        | :? float as f -> string f
#else
        | :? float as f -> f.ToString ("R", Globalization.CultureInfo.InvariantCulture)
#endif
        | p -> valueText p

    let private statusOf (text: string) =
        match text with
        | "fresh" -> TraceNodeStatus.Fresh
        | "running" -> TraceNodeStatus.Running
        | "ok" -> TraceNodeStatus.Ended RunStatus.Ok
        | "pending" -> TraceNodeStatus.Ended RunStatus.Pending
        | "error" -> TraceNodeStatus.Ended RunStatus.Error
        | "abandoned" -> TraceNodeStatus.Ended RunStatus.Abandoned
        | "disposed" -> TraceNodeStatus.Disposed
        | other -> TraceNodeStatus.Ended (enum<RunStatus>(int other))

    let private snapshotJson (sb: StringBuilder) (s: TraceSnapshot) =
        sb.Append("{\"snapshot\":{\"seq\":").Append(s.Seq).Append(",\"root\":").Append(s.Root).Append ",\"nodes\":["
        |> ignore

        let mutable first = true

        for n in s.Nodes.Values do
            if not first then
                sb.Append ',' |> ignore

            first <- false

            sb
                .Append("{\"id\":")
                .Append(n.Id)
                .Append(",\"kind\":\"")
                .Append(TraceNames.nodeKind n.Kind)
                .Append("\",\"owner\":")
                .Append(n.Owner)
                .Append
                ",\"path\":"
            |> ignore

            jsonString sb n.Path

            sb.Append(",\"incarnation\":").Append(n.Incarnation).Append ",\"label\":"
            |> ignore

            jsonString sb (Option.toObj n.Label)
            sb.Append ",\"site\":" |> ignore
            jsonString sb n.Site

            sb
                .Append(",\"created\":")
                .Append(n.Created)
                .Append(",\"runs\":")
                .Append(n.Runs)
                .Append(",\"lastRun\":")
                .Append(n.LastRun)
                .Append(",\"pendingMark\":")
                .Append(n.PendingMark)
                .Append(",\"status\":\"")
                .Append(statusText n.Status)
                .Append
                "\",\"value\":"
            |> ignore

            jsonString sb (Option.toObj n.Value)
            sb.Append '}' |> ignore

        sb.Append "],\"owners\":[" |> ignore
        first <- true

        for o in s.Owners.Values do
            if not first then
                sb.Append ',' |> ignore

            first <- false

            sb
                .Append("{\"id\":")
                .Append(o.Id)
                .Append(",\"parent\":")
                .Append(o.Parent)
                .Append(",\"host\":")
                .Append(o.Host)
                .Append(",\"root\":")
                .Append((if o.Root then "true" else "false"))
                .Append
                ",\"path\":"
            |> ignore

            jsonString sb o.Path
            sb.Append ",\"label\":" |> ignore
            jsonString sb (Option.toObj o.Label)

            sb.Append(",\"disposed\":").Append((if o.Disposed then "true" else "false")).Append '}'
            |> ignore

        sb.Append "],\"sources\":[" |> ignore
        first <- true

        for KeyValue (node, list) in s.Sources do
            if not first then
                sb.Append ',' |> ignore

            first <- false
            sb.Append('[').Append(node).Append ',' |> ignore
            jsonInts sb list
            sb.Append ']' |> ignore

        sb.Append "],\"observers\":[" |> ignore
        first <- true

        for KeyValue (node, set) in s.Observers do
            if not first then
                sb.Append ',' |> ignore

            first <- false
            sb.Append('[').Append(node).Append ',' |> ignore
            jsonInts sb set
            sb.Append ']' |> ignore

        sb.Append "],\"siblings\":[" |> ignore
        first <- true

        for KeyValue (parent, counts) in s.Siblings do
            if not first then
                sb.Append ',' |> ignore

            first <- false
            sb.Append '[' |> ignore
            jsonString sb parent
            sb.Append ",[" |> ignore
            let mutable inner = true

            for KeyValue (text, n) in counts do
                if not inner then
                    sb.Append ',' |> ignore

                inner <- false
                sb.Append '[' |> ignore
                jsonString sb text
                sb.Append(',').Append(n).Append ']' |> ignore

            sb.Append "]]" |> ignore

        sb.Append "],\"incarnations\":[" |> ignore
        first <- true

        for KeyValue (path, k) in s.Incarnations do
            if not first then
                sb.Append ',' |> ignore

            first <- false
            sb.Append '[' |> ignore
            jsonString sb path
            sb.Append(',').Append(k).Append ']' |> ignore

        sb.Append "]}}" |> ignore

    let private eventJson (sb: StringBuilder) (e: TraceEvent) =
        sb
            .Append("{\"seq\":")
            .Append(e.Seq)
            .Append(",\"kind\":\"")
            .Append(TraceNames.eventKind e.Kind)
            .Append("\",\"node\":")
            .Append(e.Node)
            .Append(",\"other\":")
            .Append(e.Other)
            .Append(",\"arg\":")
            .Append(e.Arg)
            .Append(",\"flag\":")
            .Append(e.Flag)
            .Append(",\"cause\":")
            .Append(e.Cause)
            .Append
            ",\"payload\":"
        |> ignore

        jsonString sb (payloadText e.Payload)
        sb.Append '}' |> ignore

    /// <summary>
    /// The JSONL dump, schema 1: a header line, a snapshot line holding <c>before</c>, then one line per event.
    /// </summary>
    /// <remarks>
    /// <c>before</c> is the state ahead of the first event; <c>checkpoint</c> names the file holding earlier events, or
    /// is null. Lines end in <c>\n</c>. The same events and state give the same text.
    /// </remarks>
    let dumpText (target: string) (checkpoint: string) (before: TraceSnapshot) (events: TraceEvent[]) : string =
        let sb = StringBuilder ()
        let seqFrom = if events.Length = 0 then before.Seq + 1 else events[0].Seq
        sb.Append "{\"schema\":1,\"target\":" |> ignore
        jsonString sb target

        let graph =
            if before.Root <> 0 then
                before.Root
            else
                events
                |> Array.tryPick (fun e ->
                    if e.Kind = TraceEventKind.GraphNew then
                        Some e.Other
                    else
                        None)
                |> Option.defaultValue 0

        sb.Append(",\"graph\":").Append(graph).Append ",\"checkpoint\":"
        |> ignore

        jsonString sb checkpoint

        sb.Append(",\"seqFrom\":").Append(seqFrom).Append "}\n"
        |> ignore

        snapshotJson sb before
        sb.Append '\n' |> ignore

        for e in events do
            eventJson sb e
            sb.Append '\n' |> ignore

        sb.ToString ()

#if !FABLE_COMPILER
    /// <summary>The header, snapshot and events of a JSONL dump in schema 1.</summary>
    /// <exception cref="T:System.FormatException">The text is not a schema 1 dump.</exception>
    let parseDump (text: string) : TraceDump =
        let lines = text.Split ([| '\n' |], StringSplitOptions.RemoveEmptyEntries)

        if lines.Length < 2 then
            raise (FormatException "A trace dump holds a header line and a snapshot line.")

        use header = Text.Json.JsonDocument.Parse lines[0]
        let h = header.RootElement

        if h.GetProperty("schema").GetInt32() <> 1 then
            raise (FormatException "The trace dump is not schema 1.")

        let str (e: Text.Json.JsonElement) (name: string) =
            let p = e.GetProperty name

            if p.ValueKind = Text.Json.JsonValueKind.Null then
                null
            else
                p.GetString ()

        let int' (e: Text.Json.JsonElement) (name: string) =
            e.GetProperty(name).GetInt32()

        let bool' (e: Text.Json.JsonElement) (name: string) =
            e.GetProperty(name).GetBoolean()

        let ints (e: Text.Json.JsonElement) =
            [ for i in e.EnumerateArray () -> i.GetInt32 () ]

        use snap = Text.Json.JsonDocument.Parse lines[1]
        let s = snap.RootElement.GetProperty "snapshot"

        let nodes =
            [
                for n in s.GetProperty("nodes").EnumerateArray() ->
                    let id = int' n "id"

                    id,
                    {
                        Id = id
                        Kind = Enum.Parse<TraceNodeKind>(str n "kind")
                        Owner = int' n "owner"
                        Path = str n "path"
                        Incarnation = int' n "incarnation"
                        Label = Option.ofObj (str n "label")
                        Site = str n "site"
                        Created = int' n "created"
                        Runs = int' n "runs"
                        LastRun = int' n "lastRun"
                        PendingMark = int' n "pendingMark"
                        Status = statusOf (str n "status")
                        Value = Option.ofObj (str n "value")
                    }
            ]

        let owners =
            [
                for o in s.GetProperty("owners").EnumerateArray() ->
                    let id = int' o "id"

                    id,
                    {
                        Id = id
                        Parent = int' o "parent"
                        Host = int' o "host"
                        Root = bool' o "root"
                        Path = str o "path"
                        Label = Option.ofObj (str o "label")
                        Disposed = bool' o "disposed"
                    }
            ]

        let pairs (name: string) =
            [ for p in s.GetProperty(name).EnumerateArray() -> p[0], p[1] ]

        let snapshot =
            {
                Seq = int' s "seq"
                Root = int' s "root"
                Nodes = Map.ofList nodes
                Owners = Map.ofList owners
                Sources =
                    pairs "sources"
                    |> List.map (fun (k, v) -> k.GetInt32 (), ints v)
                    |> Map.ofList
                Observers =
                    pairs "observers"
                    |> List.map (fun (k, v) -> k.GetInt32 (), Set.ofList (ints v))
                    |> Map.ofList
                Siblings =
                    pairs "siblings"
                    |> List.map (fun (k, v) ->
                        k.GetString (),
                        [ for c in v.EnumerateArray () -> c[0].GetString(), c[1].GetInt32() ]
                        |> Map.ofList)
                    |> Map.ofList
                Incarnations =
                    pairs "incarnations"
                    |> List.map (fun (k, v) -> k.GetString (), v.GetInt32 ())
                    |> Map.ofList
            }

        let events =
            [|
                for line in lines[2..] ->
                    use doc = Text.Json.JsonDocument.Parse line
                    let e = doc.RootElement

                    {
                        Seq = int' e "seq"
                        Kind = Enum.Parse<TraceEventKind>(str e "kind")
                        Node = int' e "node"
                        Other = int' e "other"
                        Arg = int' e "arg"
                        Flag = int' e "flag"
                        Cause = int' e "cause"
                        Payload = box (str e "payload")
                    }
            |]

        {
            Target = str h "target"
            Graph = int' h "graph"
            Checkpoint = str h "checkpoint"
            SeqFrom = int' h "seqFrom"
            Snapshot = snapshot
            Events = events
        }
#endif
