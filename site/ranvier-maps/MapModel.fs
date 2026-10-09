namespace Ranvier.Docs.Maps

#if RANVIER_TRACE
open Ranvier

/// <summary>The animation a frame plays on the map.</summary>
type Cue =
    /// <summary>Applied without animation.</summary>
    | Quiet
    /// <summary>A signal was written.</summary>
    | Flash of int
    /// <summary>A write reached an observer along its edge.</summary>
    | Pulse of source: int * target: int
    /// <summary>A run moved its node's value, and the change travels to every observer.</summary>
    | Surge of source: int * targets: int list
    | Ring of int
    | Rest of int
    | Flight of int
    | Drop of int
    | Settled of int
    | Failed of int
    | Waits of node: int * source: int
    /// <summary>A replayed input's write, logged before the events it causes.</summary>
    | Said

/// <summary>A node drawn as its collection: a projection's row, item, keys or summary, or a lookup's cell.</summary>
type Part =
    {
        Host: int
        /// <summary>The key's text, for a part that belongs to one key.</summary>
        Key: string option
    }

/// <summary>How a map draws a collection.</summary>
type Grouping =
    /// <summary>One node, with every part and key's nodes drawn as it.</summary>
    | Collapse
    /// <summary>A box holding the collection's node and a row per key.</summary>
    | Expand

/// <summary>Where a map draws a node.</summary>
type Place =
    /// <summary>Laid out on its own.</summary>
    | Top
    /// <summary>A key's row in the box of <c>host</c>.</summary>
    | Row of host: int
    /// <summary>Beside <c>row</c> in the box of <c>host</c>: a node created for the row's key.</summary>
    | Member of host: int * row: int
    /// <summary>Drawn as <c>other</c>.</summary>
    | Inside of other: int
    /// <summary>Undrawn, with its edges dropped: a projection's beacon or row watch.</summary>
    | Hidden

/// <summary>The map's state between frames.</summary>
type Scene =
    {
        Snapshot: TraceSnapshot
        /// <summary>The newest open flight number of each async node in flight.</summary>
        Flights: Map<int, int>
        /// <summary>The pending source each suspended reader waits on.</summary>
        Waiting: Map<int, int>
        /// <summary>The error text of each node whose last flight failed.</summary>
        Errors: Map<int, string>
        Parts: Map<int, Part>
        /// <summary>The collection and key each key's owner belongs to, by owner id.</summary>
        Scopes: Map<int, Part>
        Grouping: Grouping
    }

/// <summary>One event as the map plays it.</summary>
[<NoComparison; NoEquality>]
type Frame =
    {
        Event: TraceEvent
        Cue: Cue
        /// <summary>The event as one line of the map's log.</summary>
        Log: string
        /// <summary>The scene once the event applies.</summary>
        After: Scene
    }

[<RequireQualifiedAccess>]
module MapModel =

    let start: Scene =
        {
            Snapshot = TraceModel.emptySnapshot
            Flights = Map.empty
            Waiting = Map.empty
            Errors = Map.empty
            Parts = Map.empty
            Scopes = Map.empty
            Grouping = Expand
        }

    /// <summary>The key whose owner holds <c>node</c>, directly or through the owner's parents.</summary>
    let private scopeOf (scene: Scene) (node: int) : Part option =
        let rec up owner =
            match scene.Scopes.TryFind owner with
            | Some part -> Some part
            | None ->
                match scene.Snapshot.Owners.TryFind owner with
                | Some o when o.Parent <> 0 && o.Parent <> owner -> up o.Parent
                | _ -> None

        match scene.Snapshot.Nodes.TryFind node with
        | Some n when n.Owner <> 0 -> up n.Owner
        | _ -> None

    /// <summary>The row of <c>host</c>'s key <c>key</c>.</summary>
    let private rowOf (scene: Scene) (host: int) (key: string) : int option =
        scene.Parts
        |> Map.tryFindKey (fun id part ->
            part.Host = host
            && part.Key = Some key
            && (scene.Snapshot.Nodes.TryFind id
                |> Option.exists (fun n -> n.Kind <> TraceNodeKind.Signal)))

    let placeOf (scene: Scene) (node: int) : Place =
        let kind =
            scene.Snapshot.Nodes.TryFind node
            |> Option.map _.Kind

        match kind, scene.Parts.TryFind node with
        | Some TraceNodeKind.ProjectionBeacon, _
        | Some TraceNodeKind.RowWatch, _ -> Hidden
        | _, Some part when scene.Grouping = Collapse -> Inside part.Host
        | Some TraceNodeKind.Signal, Some part -> Inside part.Host
        | _, Some { Host = h; Key = Some _ } -> Row h
        | _, Some part -> Inside part.Host
        | _, None ->
            match scopeOf scene node with
            | Some { Host = h; Key = Some key } when scene.Grouping = Expand ->
                match rowOf scene h key with
                | Some row -> Member (h, row)
                | None -> Inside h
            | Some part -> Inside part.Host
            | None -> Top

    /// <summary>The node a map draws for <c>node</c>, or 0 for a hidden node.</summary>
    let rec drawnAs (scene: Scene) (node: int) : int =
        match placeOf scene node with
        | Inside other when other <> node -> drawnAs scene other
        | Hidden -> 0
        | _ -> node

    /// <summary>The node laid out for <c>node</c>: the collection for a row or member, else <c>drawnAs</c>.</summary>
    let laidOutAs (scene: Scene) (node: int) : int =
        let drawn = drawnAs scene node

        match placeOf scene drawn with
        | Row h
        | Member (h, _) -> h
        | _ -> drawn

    let private live (scene: Scene) (node: int) =
        scene.Snapshot.Nodes.TryFind node
        |> Option.exists (fun n -> n.Status <> TraceNodeStatus.Disposed)

    /// <summary>The live rows of <c>host</c>'s box by id, each with its live members by id.</summary>
    let rows (scene: Scene) (host: int) : (int * int list) list =
        let ids =
            scene.Snapshot.Nodes
            |> Map.toList
            |> List.map fst
            |> List.filter (live scene)

        [
            for id in ids do
                if placeOf scene id = Row host then
                    id,
                    [
                        for m in ids do
                            if placeOf scene m = Member (host, id) then
                                m
                    ]
        ]

    let private ownName (snapshot: TraceSnapshot) (node: int) =
        match snapshot.Nodes.TryFind node with
        | Some { Label = Some label } -> label
        | Some n ->
            // Fable prints an enum as its number.
            match n.Kind with
            | TraceNodeKind.Signal -> "signal"
            | TraceNodeKind.AsyncSource -> "async source"
            | TraceNodeKind.Memo -> "memo"
            | TraceNodeKind.Effect -> "effect"
            | TraceNodeKind.AsyncMemo -> "async memo"
            | TraceNodeKind.Boundary -> "boundary"
            | TraceNodeKind.Projection -> "projection"
            | _ -> "#" + string node
        | None -> "#" + string node

    /// <summary>
    /// The node's label, else its kind in lower case. A keyed part or a key's node adds its collection and key:
    /// <c>rows[tea]</c> for a row, <c>rows[tea] item</c>, <c>rows[tea] quote</c>. A part created before its collection
    /// takes its own name, any other part its collection's, and an unlabelled collection its owner's label.
    /// </summary>
    let rec name (scene: Scene) (node: int) : string =
        let snapshot = scene.Snapshot

        match scene.Parts.TryFind node with
        | Some { Host = h } when not (snapshot.Nodes.ContainsKey h) -> ownName snapshot node
        | Some { Host = h; Key = Some key } ->
            let item =
                match snapshot.Nodes.TryFind node with
                | Some { Kind = TraceNodeKind.Signal } -> " item"
                | _ -> ""

            $"%s{name scene h}[%s{key}]%s{item}"
        | Some { Host = h } -> name scene h
        | None ->
            match snapshot.Nodes.TryFind node, scopeOf scene node with
            | Some _, Some { Host = h; Key = Some key } -> $"%s{name scene h}[%s{key}] %s{ownName snapshot node}"
            | Some { Label = None; Owner = owner }, _ when
                scene.Parts
                |> Map.exists (fun _ part -> part.Host = node)
                ->
                snapshot.Owners.TryFind owner
                |> Option.bind _.Label
                |> Option.defaultWith (fun () -> ownName snapshot node)
            | _ -> ownName snapshot node

    /// <summary>The text under a drawn node: a row's key, a member's own name, else its <c>name</c>.</summary>
    let caption (scene: Scene) (node: int) : string =
        match placeOf scene node, scene.Parts.TryFind node with
        | Row _, Some { Key = Some key } -> key
        | Member _, _ -> ownName scene.Snapshot node
        | _ -> name scene node

    /// <summary>True for a node laid out on its own. A collection's box holds its rows and members.</summary>
    let visible (scene: Scene) (node: TraceSnapshotNode) : bool =
        placeOf scene node.Id = Top

    /// <summary>
    /// The (source, observer) pairs between visible nodes, in observer then slot order. An edge to or from a part is
    /// drawn to or from its collection, once.
    /// </summary>
    let edges (scene: Scene) : (int * int) list =
        let shown id =
            id <> 0 && live scene id

        [
            for KeyValue (observer, sources) in scene.Snapshot.Sources do
                let o = drawnAs scene observer

                if shown o then
                    for source in sources do
                        let s = drawnAs scene source

                        if s <> o && shown s then
                            s, o
        ]
        |> List.distinct

    let private dropReason (flag: int) =
        match enum<TraceDropReason> flag with
        | TraceDropReason.Superseded -> "superseded"
        | TraceDropReason.Disposed -> "disposed"
        | TraceDropReason.Suspended -> "suspended"
        | TraceDropReason.Trailing -> "trailing"
        | _ -> "dropped"

    let private payload (e: TraceEvent) =
        if isNull e.Payload then
            ""
        else
            TraceModel.valueText e.Payload

    let private cueOf (scene: Scene) (e: TraceEvent) =
        let node = drawnAs scene e.Node
        let other = drawnAs scene e.Other

        let observers () =
            scene.Snapshot.Observers.TryFind e.Node
            |> Option.map (
                Set.toList
                >> List.map (drawnAs scene)
                >> List.filter (fun o -> o <> node && o <> 0)
                >> List.distinct
            )
            |> Option.defaultValue []

        match e.Kind with
        | _ when node = 0 -> Quiet
        | TraceEventKind.Write -> Flash node
        | TraceEventKind.Mark when other = node || other = 0 -> Quiet
        | TraceEventKind.Mark -> Pulse (other, node)
        | TraceEventKind.RunStart -> Ring node
        | TraceEventKind.RunEnd -> Rest node
        | TraceEventKind.Moved -> Surge (node, observers ())
        | TraceEventKind.FlightStart -> Flight node
        | TraceEventKind.FlightDrop -> Drop node
        | TraceEventKind.Settle -> Settled node
        | TraceEventKind.Fail -> Failed node
        | TraceEventKind.Suspend -> Waits (node, other)
        | _ -> Quiet

    let private logOf (scene: Scene) (e: TraceEvent) =
        let name = name scene
        let node = name e.Node

        match e.Kind with
        | TraceEventKind.Write when isNull e.Payload -> $"write %s{node}"
        | TraceEventKind.Write -> $"write %s{node} = %s{payload e}"
        | TraceEventKind.Mark -> $"mark %s{node} from %s{name e.Other}"
        | TraceEventKind.RunStart -> $"run %s{node}"
        | TraceEventKind.RunEnd -> $"end %s{node} (%s{(string (enum<RunStatus> e.Arg)).ToLowerInvariant()})"
        | TraceEventKind.Moved when isNull e.Payload -> $"%s{node} moved"
        | TraceEventKind.Moved -> $"%s{node} = %s{payload e}"
        | TraceEventKind.FlightStart -> $"flight %s{node}"
        | TraceEventKind.FlightDrop -> $"drop %s{node} (%s{dropReason e.Flag})"
        | TraceEventKind.Settle -> $"settle %s{node} = %s{payload e}"
        | TraceEventKind.Fail -> $"fail %s{node}: %s{payload e}"
        | TraceEventKind.Suspend -> $"%s{node} waits on %s{name e.Other}"
        | TraceEventKind.NodeNew -> $"new %s{node}"
        | TraceEventKind.Part when e.Flag = 1 -> $"%s{name e.Other}[%s{payload e}] opens"
        | TraceEventKind.Part -> $"%s{node} joins %s{name e.Other}"
        | kind when e.Node = 0 -> (string kind).ToLowerInvariant()
        | kind -> $"%s{(string kind).ToLowerInvariant()} %s{node}"

    let private partOf (e: TraceEvent) : Part =
        {
            Host = e.Other
            Key =
                if isNull e.Payload then
                    None
                else
                    Some (TraceModel.valueText e.Payload)
        }

    let private settle (scene: Scene) (e: TraceEvent) =
        let flights =
            match scene.Flights.TryFind e.Node with
            | Some newest when newest = e.Arg || e.Arg = 0 -> scene.Flights.Remove e.Node
            | _ -> scene.Flights

        { scene with Flights = flights }

    let private advance (scene: Scene) (e: TraceEvent) =
        match e.Kind with
        | TraceEventKind.FlightStart ->
            { scene with
                Flights = scene.Flights.Add (e.Node, e.Arg)
            }
        | TraceEventKind.FlightDrop -> settle scene e
        | TraceEventKind.Settle ->
            { settle scene e with
                Errors = scene.Errors.Remove e.Node
            }
        | TraceEventKind.Fail ->
            { settle scene e with
                Errors = scene.Errors.Add (e.Node, payload e)
            }
        | TraceEventKind.Part when e.Flag = 1 ->
            { scene with
                Scopes = scene.Scopes.Add (e.Node, partOf e)
            }
        | TraceEventKind.Part ->
            { scene with
                Parts = scene.Parts.Add (e.Node, partOf e)
            }
        | TraceEventKind.Suspend ->
            { scene with
                Waiting = scene.Waiting.Add (e.Node, e.Other)
            }
        | TraceEventKind.RunStart ->
            { scene with
                Waiting = scene.Waiting.Remove e.Node
            }
        | TraceEventKind.RunEnd when enum<RunStatus> e.Arg = RunStatus.Error ->
            let error =
                scene.Snapshot.Nodes.TryFind e.Node
                |> Option.bind _.Value
                |> Option.defaultValue "failed"

            { scene with
                Errors = scene.Errors.Add (e.Node, error)
            }
        | TraceEventKind.RunEnd when enum<RunStatus> e.Arg <> RunStatus.Abandoned ->
            { scene with
                Errors = scene.Errors.Remove e.Node
            }
        | _ -> scene

    /// <summary>
    /// True for a <c>Moved</c> whose run ends <c>Pending</c>: its value is a placeholder, and readers keep the value
    /// from before the run.
    /// </summary>
    let private placeholder (events: TraceEvent[]) (i: int) =
        let e = events[i]

        e.Kind = TraceEventKind.Moved
        && (events[i + 1 ..]
            |> Array.tryFind (fun r -> r.Kind = TraceEventKind.RunEnd && r.Node = e.Node)
            |> Option.exists (fun r -> enum<RunStatus> r.Arg = RunStatus.Pending))

    /// <summary>One frame per event, played on from <c>scene</c>.</summary>
    /// <remarks>
    /// A <c>NodeNew</c> or <c>OwnerNew</c> folds together with the <c>Trace.named</c> label that follows it, so the node takes its
    /// name from its first frame. A part is drawn as its collection from its <c>NodeNew</c>.
    /// </remarks>
    let frames (scene: Scene) (events: TraceEvent[]) : Frame[] =
        let mutable scene = scene

        let parts =
            events
            |> Array.filter (fun e -> e.Kind = TraceEventKind.Part && e.Flag = 0)
            |> Array.map (fun e -> e.Node, partOf e)
            |> Map.ofArray

        [|
            for i in 0 .. events.Length - 1 do
                let e = events[i]

                let folded =
                    if
                        (e.Kind = TraceEventKind.NodeNew
                         || e.Kind = TraceEventKind.OwnerNew)
                        && i + 1 < events.Length
                        && events[i + 1].Kind = TraceEventKind.Label
                        && events[i + 1].Arg = 0
                        && events[i + 1].Node = e.Node
                    then
                        events[i .. i + 1]
                    else
                        [| e |]

                let before =
                    match parts.TryFind e.Node with
                    | Some part when e.Kind = TraceEventKind.NodeNew ->
                        { scene with
                            Parts = scene.Parts.Add (e.Node, part)
                        }
                    | _ -> scene

                let held = placeholder events i

                let snapshot =
                    if held then
                        before.Snapshot
                    else
                        TraceModel.fold before.Snapshot folded

                // The log names nodes as they stand after a creation or a label, and before a disposal.
                let named =
                    match e.Kind with
                    | TraceEventKind.NodeNew
                    | TraceEventKind.Label -> { before with Snapshot = snapshot }
                    | _ -> before

                scene <- advance { before with Snapshot = snapshot } e

                {
                    Event = e
                    Cue = if held then Quiet else cueOf scene e
                    Log =
                        if held then
                            $"%s{name named e.Node} holds its value"
                        else
                            logOf named e
                    After = scene
                }
        |]

    /// <summary>
    /// True while the node, a node drawn as it, anything in its box, or a member of its row has a flight in
    /// progress or waits on a pending source.
    /// </summary>
    let pending (scene: Scene) (node: int) : bool =
        let covers id =
            drawnAs scene id = node
            || laidOutAs scene id = node
            || placeOf scene id = Member (laidOutAs scene id, node)

        Seq.append (Map.keys scene.Flights) (Map.keys scene.Waiting)
        |> Seq.exists covers

    /// <summary>The scene after the frame at <c>index</c>; <c>scene</c> itself for an index before the first.</summary>
    let stateAt (scene: Scene) (frames: Frame[]) (index: int) : Scene =
        if index < 0 || frames.Length = 0 then
            scene
        else
            frames[min index (frames.Length - 1)].After

    /// <summary>
    /// The timeline position, from 0 to 1, of frame <c>index</c> of <c>count</c>; <c>None</c> for one of the first
    /// <c>setup</c> frames.
    /// </summary>
    let tickAt (setup: int) (count: int) (index: int) : float option =
        if index < setup then
            None
        else
            Some (float (index + 1 - setup) / float (count - setup))

    /// <summary>
    /// <c>index</c> held between the last setup frame and the last of <c>count</c> frames; -1 is the scene before
    /// the first frame.
    /// </summary>
    let clampCursor (setup: int) (count: int) (index: int) : int =
        max (setup - 1) (min index (count - 1))

    /// <summary>A frame that logs <c>text</c> and leaves <c>scene</c> as it is.</summary>
    let said (scene: Scene) (text: string) : Frame =
        {
            Event = Unchecked.defaultof<TraceEvent>
            Cue = Said
            Log = text
            After = scene
        }
#endif
