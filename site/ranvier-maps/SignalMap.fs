namespace Ranvier.Docs.Maps

open System
open System.Collections.Generic
open Fable.Core
open Fable.Core.JsInterop
open Browser
open Browser.Types
open Partas.Solid
open Ranvier
open Ranvier.Docs.Maps
open Partas.AnimeJs

module private Dom =
    let svgNs = "http://www.w3.org/2000/svg"

    let el (tag: string) (cls: string) : HTMLElement =
        let e = document.createElement tag
        e.className <- cls
        e

    let svg (tag: string) (cls: string) : Element =
        let e = document.createElementNS (svgNs, tag)
        e.setAttribute ("class", cls)
        e

    let attrs (e: Element) (pairs: (string * string) list) =
        for name, value in pairs do
            e.setAttribute (name, value)

    let toggle (e: Element) (cls: string) (on: bool) =
        if on then e.classList.add cls else e.classList.remove cls

    let button (label: string) (cls: string) (onClick: unit -> unit) =
        let b = el "button" cls
        b.setAttribute ("type", "button")
        b.textContent <- label
        b.addEventListener ("click", fun _ -> onClick ())
        b

    /// <summary>A labelled input: the caption, the widget, and the widget element.</summary>
    let field (label: string) (kind: string) : HTMLElement * HTMLInputElement =
        let wrap = el "label" $"rv-map__input rv-map__input--%s{kind}"
        let caption = el "span" "rv-map__input-label"
        caption.textContent <- label
        let input = el "input" "rv-map__input-field" :?> HTMLInputElement
        wrap.appendChild caption |> ignore
        wrap.appendChild input |> ignore
        wrap, input

    /// <summary>A class held for <c>ms</c> milliseconds, restarting its animation.</summary>
    let flash (e: Element) (cls: string) (ms: int) =
        e.classList.remove cls
        e?getBBox () |> ignore
        e.classList.add cls

        window.setTimeout ((fun () -> e.classList.remove cls), ms)
        |> ignore

/// <summary>A node's drawing on the stage.</summary>
type private NodeView =
    {
        Group: Element
        Shape: Element
        Name: Element
        Value: Element
        mutable At: float * float
    }

module private Look =
    let column = 150.0
    let row = 78.0
    let margin = 52.0
    /// <summary>The space between one layer's widest node and the next's.</summary>
    let gap = 98.0
    /// <summary>The distance from a collection's node to its first row, and between rows.</summary>
    let firstSlot = 64.0
    let slot = 50.0
    /// <summary>Half a row's width, and the distance between a box's member columns.</summary>
    let rowHalf = 38.0
    let memberGap = 44.0
    let boxPad = 14.0

    /// <summary>The distance from a node's centre to where its edges attach.</summary>
    let half (place: Place) =
        match place with
        | Row _ -> rowHalf
        | Member _ -> 14.0
        | _ -> 22.0

    let position (layer: int, row': int) =
        margin + float layer * column, margin + float row' * row

    let kindName (kind: TraceNodeKind) =
        match kind with
        | TraceNodeKind.Signal -> "signal"
        | TraceNodeKind.Memo -> "memo"
        | TraceNodeKind.Effect -> "effect"
        | TraceNodeKind.AsyncMemo -> "async memo"
        | TraceNodeKind.AsyncSource -> "async source"
        | TraceNodeKind.Boundary -> "boundary"
        | TraceNodeKind.Projection -> "projection"
        | _ -> "node"

    let shapeOf (kind: TraceNodeKind) =
        match kind with
        | TraceNodeKind.Signal -> "signal"
        | TraceNodeKind.Effect -> "effect"
        | TraceNodeKind.AsyncMemo
        | TraceNodeKind.AsyncSource -> "async"
        | _ -> "memo"

    let statusName (status: TraceNodeStatus) =
        match status with
        | TraceNodeStatus.Fresh -> "fresh"
        | TraceNodeStatus.Running -> "running"
        | TraceNodeStatus.Disposed -> "disposed"
        | TraceNodeStatus.Ended RunStatus.Ok -> "ok"
        | TraceNodeStatus.Ended RunStatus.Pending -> "pending"
        | TraceNodeStatus.Ended RunStatus.Error -> "error"
        | TraceNodeStatus.Ended _ -> "abandoned"

    let badgeTo (length: int) (text: string) =
        if text.Length > length then
            text.Substring (0, length - 1) + "…"
        else
            text

    let badge = badgeTo 16

    /// <summary>An orthogonal edge from <c>h1</c> right of a source's centre to <c>h2</c> left of an observer's.</summary>
    let edgePath (x1: float, y1: float) (h1: float) (x2: float, y2: float) (h2: float) =
        let x1 = x1 + h1
        let x2 = x2 - h2
        let mid = (x1 + x2) / 2.0
        $"M{x1} {y1} H{mid} V{y2} H{x2}"

[<AutoOpen>]
module SignalMapComponent =

    /// <summary>
    /// A traced graph with controls, a log and an optional timeline; playback starts at <c>initialSpeed</c>, from 0.25 to 4.
    /// Its graph runs under <c>policy</c>, and its collections draw as <c>grouping</c>.
    /// </summary>
    /// <remarks>
    /// Placed in a <c>partas-solid-card</c>, it highlights the lines of the binding whose node runs. A binding is
    /// (label, first line, last line), with lines counted from 1 in the card's code block.
    /// </remarks>
    /// <exception cref="T:System.ArgumentException">The initial speed is outside 0.25 to 4, or is not finite.</exception>
    let SignalMapWithSpeed
        (initialSpeed: float)
        (source: MapSource)
        (policy: FlightPolicy)
        (bindings: (string * int * int)[])
        (timeline: bool)
        (grouping: Grouping)
        : HtmlElement =
        if not (initialSpeed >= 0.25 && initialSpeed <= 4.0) then
            invalidArg (nameof initialSpeed) "Playback speed must be from 0.25 to 4."

        let mutable speed = initialSpeed

        let duration milliseconds =
            Replay.duration speed milliseconds

        let speedText (value: float) =
            value.ToString (Globalization.CultureInfo.InvariantCulture)

        let reduced: bool = window?matchMedia("(prefers-reduced-motion: reduce)")?matches

        let timeline =
            timeline
            || (match source with
                | Replayed _ -> true
                | Live _ -> false)

        let root = Dom.el "div" "rv-map"
        root.setAttribute ("style", "--rv-map-speed: " + speedText speed)
        let stageBox = Dom.el "div" "rv-map__stage"
        let stage = Dom.svg "svg" "rv-map__svg"
        Dom.attrs stage [ "role", "img"; "aria-label", "Signal map" ]

        let start =
            { MapModel.start with
                Grouping = grouping
            }

        let groupLayer = Dom.svg "g" "rv-map__groups"
        let edgeLayer = Dom.svg "g" "rv-map__edges"
        let dotLayer = Dom.svg "g" "rv-map__dots"
        let nodeLayer = Dom.svg "g" "rv-map__nodes"
        stage.appendChild groupLayer |> ignore
        stage.appendChild edgeLayer |> ignore
        stage.appendChild dotLayer |> ignore
        stage.appendChild nodeLayer |> ignore
        stageBox.appendChild stage |> ignore
        let inspect = Dom.el "div" "rv-map__inspect"
        let hint = "Hover a node for its state; click it for why it last ran."
        inspect.textContent <- hint
        stageBox.appendChild inspect |> ignore
        let bar = Dom.el "div" "rv-map__bar"
        let caption = Dom.el "p" "rv-map__caption"
        caption.setAttribute ("aria-live", "polite")
        let controlRow = Dom.el "div" "rv-map__controls"
        bar.appendChild controlRow |> ignore
        let log = Dom.el "ol" "rv-map__log"
        log.setAttribute ("aria-live", "polite")
        let why = Dom.el "pre" "rv-map__why"
        let error = Dom.el "div" "rv-map__error"
        error.setAttribute ("role", "alert")

        for e in [ stageBox; bar; caption; log; why; error ] do
            root.appendChild e |> ignore

        let nodes = Dictionary<int, NodeView>()
        let edges = Dictionary<string, Element>()
        let boxes = ResizeArray<Element>()
        let history = ResizeArray<Frame>()
        let captions = ResizeArray<int * string option>()
        let widgetRefresh = ResizeArray<unit -> unit>()
        let lit = Dictionary<int, Element list>()
        let mutable cursor = -1
        let mutable setup = 0
        let mutable playing = not timeline
        let mutable scheduled = false
        let mutable timer = 0.0
        let mutable layoutKey = ""
        let mutable shown = start
        let mutable tail = start
        let mutable read = 0
        let mutable graph: Graph option = None
        let mutable disposed = false

        let fail (text: string) =
            error.textContent <- text
            root.classList.add "rv-map--failed"
            playing <- false

        let codeLines () =
            match root.closest ".partas-solid-card" with
            | Some card -> card.querySelectorAll ".nacara-code__line"
            | None -> document.createDocumentFragment().querySelectorAll "x"

        let unlight (node: int) =
            match lit.TryGetValue node with
            | true, lines ->
                for l in lines do
                    l.classList.remove "rv-map-line--run"

                lit.Remove node |> ignore
            | _ -> ()

        let unlightAll () =
            for node in List.ofSeq lit.Keys do
                unlight node

        let light (node: int) =
            let name = MapModel.name shown node

            match
                bindings
                |> Array.tryFind (fun (b, _, _) -> b = name)
            with
            | Some (_, first, last) ->
                let all = codeLines ()

                let lines =
                    [
                        for i in first - 1 .. last - 1 do
                            if i >= 0 && i < all.length then
                                all.item i
                    ]

                for l in lines do
                    l.classList.add "rv-map-line--run"

                lit[node] <- lines
            | None -> ()

        let say (text: string) (cls: string) =
            why.textContent <- ""
            let item = Dom.el "li" cls
            item.textContent <- text
            log.appendChild item |> ignore

            while log.children.length > 8 do
                log.removeChild log.firstChild |> ignore

            log.scrollTop <- log.scrollHeight

        let describe (n: TraceSnapshotNode) =
            let path = TraceModel.pathOf shown.Snapshot n.Id

            let parts =
                [
                    path
                    Look.kindName n.Kind
                    match n.Status with
                    | _ when MapModel.pending shown n.Id -> "pending"
                    | _ when shown.Errors.ContainsKey n.Id -> "failed"
                    | _ when
                        n.Kind = TraceNodeKind.Signal
                        || n.Kind = TraceNodeKind.AsyncSource
                        ->
                        "source"
                    | TraceNodeStatus.Ended RunStatus.Pending -> "settled"
                    | status -> Look.statusName status
                    match n.Value with
                    | Some v -> "= " + v
                    | None -> ()
                    $"runs {n.Runs}"
                    match shown.Waiting.TryFind n.Id with
                    | Some s -> "waiting on " + TraceModel.pathOf shown.Snapshot s
                    | None -> ()
                    match shown.Errors.TryFind n.Id with
                    | Some e -> "error: " + e
                    | None -> ()
                ]

            String.Join (" · ", parts)

        let explain (id: int) =
            let events =
                [|
                    for i in 0..cursor do
                        history[i].Event
                |]

            why.textContent <-
                try
                    TraceModel.renderWhy shown.Snapshot (TraceModel.why events null id 0)
                with _ ->
                    match
                        shown.Snapshot.Nodes.TryFind id
                        |> Option.map _.Kind
                    with
                    | Some TraceNodeKind.Signal
                    | Some TraceNodeKind.AsyncSource ->
                        MapModel.name shown id
                        + " is a source: it changes when written or settled, and never runs."
                    | _ -> MapModel.name shown id + " has not run."

        let viewOf (n: TraceSnapshotNode) (at: Place) =
            let shape = Look.shapeOf n.Kind

            let role =
                match at with
                | Row _ -> " rv-map-node--row"
                | Member _ -> " rv-map-node--member"
                | _ -> ""

            let group = Dom.svg "g" ("rv-map-node rv-map-node--" + shape + role)
            group.setAttribute ("tabindex", "0")
            let small = role <> ""
            let ring = Dom.svg "circle" "rv-map-node__flight"
            Dom.attrs ring [ "r", (if small then "18" else "25") ]

            let body =
                match at, shape with
                | Row _, _ ->
                    let r = Dom.svg "rect" "rv-map-node__shape"
                    Dom.attrs r [ "x", "-38"; "y", "-12"; "width", "76"; "height", "24"; "rx", "12" ]
                    r
                | _, "signal" ->
                    let c = Dom.svg "circle" "rv-map-node__shape"
                    Dom.attrs c [ "r", (if small then "11" else "16") ]
                    c
                | _, "async" ->
                    let c = Dom.svg "circle" "rv-map-node__shape"
                    Dom.attrs c [ "r", (if small then "12" else "17") ]
                    c
                | _, "effect" ->
                    let p = Dom.svg "path" "rv-map-node__shape"

                    Dom.attrs
                        p
                        [
                            "d",
                            (if small then
                                 "M0 -13 L13 0 L0 13 L-13 0 Z"
                             else
                                 "M0 -19 L19 0 L0 19 L-19 0 Z")
                        ]

                    p
                | _ when small ->
                    let r = Dom.svg "rect" "rv-map-node__shape"
                    Dom.attrs r [ "x", "-14"; "y", "-10"; "width", "28"; "height", "20"; "rx", "6" ]
                    r
                | _ ->
                    let r = Dom.svg "rect" "rv-map-node__shape"
                    Dom.attrs r [ "x", "-23"; "y", "-15"; "width", "46"; "height", "30"; "rx", "9" ]
                    r

            let name = Dom.svg "text" "rv-map-node__name"
            let value = Dom.svg "text" "rv-map-node__value"

            match at with
            | Row _ ->
                Dom.attrs name [ "x", "-30"; "y", "4"; "text-anchor", "start" ]
                Dom.attrs value [ "x", "30"; "y", "4"; "text-anchor", "end" ]
            | _ ->
                Dom.attrs name [ "y", "36"; "text-anchor", "middle" ]
                Dom.attrs value [ "y", "-26"; "text-anchor", "middle" ]

            for e in [ ring; body; name; value ] do
                group.appendChild e |> ignore

            let id = n.Id

            group.addEventListener (
                "mouseenter",
                fun _ ->
                    match shown.Snapshot.Nodes.TryFind id with
                    | Some n -> inspect.textContent <- describe n
                    | None -> ()
            )

            group.addEventListener ("mouseleave", fun _ -> inspect.textContent <- hint)
            group.addEventListener ("click", fun _ -> explain id)

            group.addEventListener (
                "keydown",
                fun e ->
                    if (e :?> KeyboardEvent).key = "Enter" then
                        explain id
            )

            nodeLayer.appendChild group |> ignore

            {
                Group = group
                Shape = body
                Name = name
                Value = value
                At = 0.0, 0.0
            }

        let place (view: NodeView) (x: float, y: float) (animate: bool) =
            let fromX, fromY = view.At
            view.At <- x, y

            if
                animate
                && not reduced
                && (fromX, fromY) <> (0.0, 0.0)
            then
                let at = createObj [ "x" ==> fromX; "y" ==> fromY ]

                Anime.animate
                    (Target.``object`` (unbox at))
                    (animation {
                        Animation.property "x" (Tween.number x)
                        Animation.property "y" (Tween.number y)
                        Animation.duration 360.
                        Animation.ease "outQuart"
                        Animation.onUpdate (fun _ -> view.Group.setAttribute ("transform", $"translate({at?x} {at?y})"))
                    })
                |> ignore
            else
                view.Group.setAttribute ("transform", $"translate({x} {y})")

        /// <summary>Draws <c>scene</c>: nodes, edges, positions, values and state classes.</summary>
        let sync (scene: Scene) =
            shown <- scene
            let snapshot = scene.Snapshot

            let top =
                snapshot.Nodes.Values
                |> Seq.filter (fun n ->
                    MapModel.visible scene n
                    && n.Status <> TraceNodeStatus.Disposed)
                |> Seq.map _.Id
                |> Set.ofSeq

            let boxed =
                top
                |> Seq.map (fun id -> id, MapModel.rows scene id)
                |> Seq.filter (snd >> List.isEmpty >> not)
                |> Map.ofSeq

            let live =
                boxed
                |> Map.fold
                    (fun live _ rows ->
                        rows
                        |> List.fold (fun live (row, members) -> Set.union (live.Add row) (Set.ofList members)) live)
                    top

            let outer id =
                MapModel.laidOutAs scene id

            let links =
                MapModel.edges scene
                |> List.filter (fun (s, o) ->
                    live.Contains s
                    && live.Contains o
                    && not (outer s = outer o && (s = outer s || o = outer o)))

            for id in List.ofSeq nodes.Keys do
                if not (live.Contains id) then
                    nodes[id].Group.remove()
                    nodes.Remove id |> ignore
                    unlight id

            for id in live do
                if not (nodes.ContainsKey id) then
                    nodes[id] <- viewOf snapshot.Nodes[id] (MapModel.placeOf scene id)

            let key =
                String.Join (";", live)
                + "|"
                + String.Join (";", links)

            if key <> layoutKey then
                layoutKey <- key

                let slotOf = Dictionary<int, int>()

                for KeyValue (_, rows) in boxed do
                    rows
                    |> List.iteri (fun i (row, members) ->
                        for id in row :: members do
                            slotOf[id] <- i)

                let slotY i =
                    Look.firstSlot + float i * Look.slot

                let sources =
                    links
                    |> List.choose (fun (s, o) ->
                        let offset =
                            match slotOf.TryGetValue s with
                            | true, i -> slotY i / Look.row
                            | _ -> 0.0

                        if outer s = outer o then
                            None
                        else
                            Some (outer o, (outer s, offset)))
                    |> List.distinct
                    |> List.groupBy fst
                    |> List.map (fun (o, pairs) -> o, List.map snd pairs)
                    |> Map.ofList

                let span id =
                    match boxed.TryFind id with
                    | Some rows -> int (ceil ((68.0 + Look.slot * float rows.Length) / Look.row))
                    | None -> 1

                let memberReach rows =
                    match
                        rows
                        |> List.map (snd >> List.length)
                        |> List.fold max 0
                    with
                    | 0 -> 0.0
                    | columns -> 48.0 + float (columns - 1) * Look.memberGap

                let extent id =
                    match boxed.TryFind id with
                    | Some rows -> Look.rowHalf + memberReach rows + Look.boxPad, Look.rowHalf + Look.boxPad
                    | None -> 26.0, 26.0

                let placed = Layout.placeSpanned span (Set.toList top) sources
                let layers = placed.Values |> Seq.map fst |> Seq.fold max 0

                let reach pick layer =
                    placed
                    |> Seq.filter (fun (KeyValue (_, (l, _))) -> l = layer)
                    |> Seq.map (fun (KeyValue (id, _)) -> pick (extent id))
                    |> Seq.fold max 26.0

                let xs = Array.zeroCreate (layers + 1)

                for l in 0..layers do
                    xs[l] <-
                        if l = 0 then
                            Look.margin - 26.0 + reach fst 0
                        else
                            xs[l - 1]
                            + reach snd (l - 1)
                            + Look.gap
                            + reach fst l

                let at = Dictionary<int, float * float>()

                let memberX x j =
                    x - Look.rowHalf - 30.0 - float j * Look.memberGap

                let above id =
                    if boxed.ContainsKey id then 44.0 else 30.0

                let below id =
                    match boxed.TryFind id with
                    | Some rows -> slotY (rows.Length - 1) + 22.0
                    | None -> 40.0

                let inLayer layer =
                    placed
                    |> Seq.filter (fun p -> fst p.Value = layer)
                    |> Seq.sortBy (fun p -> snd p.Value)
                    |> Seq.map (fun p -> p.Key, snd p.Value)
                    |> List.ofSeq

                let sourcePorts id =
                    sources.TryFind id
                    |> Option.defaultValue []
                    |> List.choose (fun (s, offset) ->
                        match at.TryGetValue s with
                        | true, (_, y) -> Some (y + offset * Look.row)
                        | _ -> None)

                let observerPorts id =
                    links
                    |> List.filter (fun (s, o) -> outer s = id && outer o <> id)
                    |> List.map (fun (s, o) ->
                        let offset =
                            match slotOf.TryGetValue s with
                            | true, i -> slotY i
                            | _ -> 0.0

                        snd at[o] - offset)

                // A layer keeps the layout's order; each node sits level with the mean of its ports, lowered as far
                // as the node above it requires.
                let settle layer (ports: int -> float list) =
                    let mutable floor = Look.margin - 30.0

                    for id, row in inLayer layer do
                        let wanted =
                            match ports id with
                            | [] -> Look.margin + float row * Look.row
                            | ys -> List.average ys

                        let y = max wanted (floor + above id)
                        let x = xs[layer]
                        at[id] <- x, y
                        floor <- y + below id + 12.0

                        match boxed.TryFind id with
                        | Some rows ->
                            rows
                            |> List.iteri (fun i (row, members) ->
                                let rowY = y + slotY i
                                at[row] <- x, rowY

                                members
                                |> List.iteri (fun j m -> at[m] <- memberX x j, rowY))
                        | None -> ()

                for layer in 0..layers do
                    settle layer sourcePorts

                // A layer of roots centres on the nodes it feeds.
                for layer in 0..layers do
                    if
                        inLayer layer
                        |> List.forall (fst >> sourcePorts >> List.isEmpty)
                    then
                        settle layer observerPorts

                let bottom =
                    placed
                    |> Seq.map (fun (KeyValue (id, _)) ->
                        let _, y = at[id]

                        match boxed.TryFind id with
                        | Some rows -> y + slotY (rows.Length - 1)
                        | None -> y)
                    |> Seq.fold max 0.0

                let width = xs[layers] + reach snd layers + Look.margin - 26.0
                let height = bottom + Look.margin
                stage.setAttribute ("viewBox", $"0 0 {width} {height}")

                for KeyValue (id, xy) in at do
                    place nodes[id] xy true

                for b in boxes do
                    b.remove ()

                boxes.Clear ()

                for KeyValue (host, rows) in boxed do
                    let x, y = at[host]
                    let left, right = extent host
                    let box = Dom.svg "rect" "rv-map-group"

                    Dom.attrs
                        box
                        [
                            "x", string (x - left)
                            "y", string (y - 44.0)
                            "width", string (left + right)
                            "height", string (44.0 + slotY (rows.Length - 1) + 22.0)
                            "rx", "12"
                        ]

                    groupLayer.appendChild box |> ignore
                    boxes.Add box

                    match rows with
                    | (_, members) :: _ ->
                        members
                        |> List.iteri (fun j m ->
                            let column = Dom.svg "text" "rv-map-group__column"

                            Dom.attrs
                                column
                                [
                                    "x", string (memberX x j)
                                    "y", string (y + slotY 0 - 22.0)
                                    "text-anchor", "middle"
                                ]

                            column.textContent <- MapModel.caption scene m
                            groupLayer.appendChild column |> ignore
                            boxes.Add column)
                    | [] -> ()

                for e in edges.Values do
                    e.remove ()

                edges.Clear ()

                for s, o in links do
                    let path = Dom.svg "path" "rv-map-edge"

                    // An edge leaving a box from a member starts at the member's row.
                    let port =
                        match MapModel.placeOf scene s with
                        | Member (h, row) when outer o <> h -> row
                        | _ -> s

                    let hs = Look.half (MapModel.placeOf scene port)
                    let ho = Look.half (MapModel.placeOf scene o)
                    path.setAttribute ("d", Look.edgePath at[port] hs at[o] ho)
                    edgeLayer.appendChild path |> ignore
                    edges[$"{s}>{o}"] <- path

            for KeyValue (id, view) in nodes do
                let n = snapshot.Nodes[id]
                view.Name.textContent <- MapModel.caption scene id

                let badge =
                    match MapModel.placeOf scene id with
                    | Row _ -> Look.badgeTo 6
                    | _ -> Look.badge

                view.Value.textContent <-
                    n.Value
                    |> Option.map badge
                    |> Option.defaultValue ""

                Dom.toggle view.Group "is-running" (n.Status = TraceNodeStatus.Running)
                Dom.toggle view.Group "is-pending" (MapModel.pending scene id)

                Dom.toggle
                    view.Group
                    "is-fresh"
                    (n.Status = TraceNodeStatus.Fresh
                     && n.Kind <> TraceNodeKind.Signal
                     && n.Kind <> TraceNodeKind.AsyncSource)

                Dom.toggle view.Group "is-flight" (scene.Flights.ContainsKey id)
                Dom.toggle view.Group "is-waiting" (scene.Waiting.ContainsKey id)
                Dom.toggle view.Group "is-failed" (scene.Errors.ContainsKey id)
                view.Group.setAttribute ("aria-label", describe n)

        let travel (s: int) (t: int) (bright: bool) =
            match edges.TryGetValue $"{s}>{t}" with
            | true, path ->
                let length: float = path?getTotalLength ()

                let dot =
                    Dom.svg
                        "circle"
                        (if bright then
                             "rv-map-dot rv-map-dot--bright"
                         else
                             "rv-map-dot")

                dot.setAttribute ("r", (if bright then "4.5" else "3.5"))
                dotLayer.appendChild dot |> ignore
                let at = createObj [ "t" ==> 0.0 ]

                let move () =
                    let p = path?getPointAtLength (at?t * length)
                    Dom.attrs dot [ "cx", string p?x; "cy", string p?y ]

                move ()

                Anime.animate
                    (Target.``object`` (unbox at))
                    (animation {
                        Animation.property "t" (Tween.number 1.)
                        Animation.duration (float (duration (if bright then 460 else 380)))
                        Animation.ease "inOutSine"
                        Animation.onUpdate (fun _ -> move ())
                        Animation.onComplete (fun _ -> dot.remove ())
                    })
                |> ignore
            | _ -> ()

        let play (cue: Cue) =
            let shape id =
                match nodes.TryGetValue id with
                | true, v -> Some v
                | _ -> None

            match cue with
            | Flash id ->
                shape id
                |> Option.iter (fun v ->
                    Anime.animate
                        (Target.svg (unbox v.Shape))
                        (animation {
                            Transform.scale (Tween.values [ 1.; 1.35; 1. ])
                            Animation.duration (float (duration 420))
                            Animation.ease "outQuad"
                        })
                    |> ignore

                    Dom.flash v.Group "is-flash" (duration 700))
            | Pulse (s, t) -> travel s t false
            | Surge (s, targets) ->
                for t in targets do
                    travel s t true

                shape s
                |> Option.iter (fun v -> Dom.flash v.Value "is-swap" (duration 600))
            | Drop id ->
                shape id
                |> Option.iter (fun v -> Dom.flash v.Group "is-dropped" (duration 700))
            | Settled id ->
                shape id
                |> Option.iter (fun v -> Dom.flash v.Group "is-settled" (duration 800))
            | Failed id ->
                shape id
                |> Option.iter (fun v -> Dom.flash v.Group "is-failing" (duration 800))
            | _ -> ()

        let ticks = Dom.el "div" "rv-map__ticks"
        let scrub = Dom.el "input" "rv-map__scrub" :?> HTMLInputElement
        let playButton = Dom.button "Play" "rv-map__button" ignore

        let refresh () =
            caption.textContent <-
                Replay.captionAt captions cursor
                |> Option.defaultValue ""

            if timeline then
                scrub.min <- string setup
                scrub.max <- string history.Count
                scrub.value <- string (cursor + 1)
                playButton.textContent <- (if playing then "Pause" else "Play")

        let note (frame: Frame) =
            match frame.Cue with
            | Quiet -> ()
            | Failed _ -> say frame.Log "is-error"
            | _ -> say frame.Log ""

        let show (frame: Frame) =
            sync frame.After

            match frame.Cue with
            | Ring id -> light id
            | Rest id -> unlight id
            | _ -> ()

            if not reduced then
                play frame.Cue

            note frame

        let rec tick () =
            scheduled <- false

            try
                if playing && not disposed then
                    let mutable go = true

                    while go && cursor + 1 < history.Count do
                        cursor <- cursor + 1
                        let frame = history[cursor]
                        show frame

                        if frame.Cue <> Quiet && not reduced then
                            go <- false

                    if cursor + 1 >= history.Count && timeline then
                        playing <- false

                    refresh ()

                    if cursor + 1 < history.Count then
                        schedule ()
            with ex ->
                fail ("The map stopped: " + ex.Message)

        and schedule () =
            if not scheduled && not disposed then
                scheduled <- true
                let backlog = history.Count - cursor - 1

                let gap =
                    if reduced then
                        0
                    else
                        duration (max 40 (180 - 10 * backlog))

                timer <- window.setTimeout (tick, gap)

        let redrawTicks () =
            if timeline then
                ticks.innerHTML <- ""

                for i in 0 .. history.Count - 1 do
                    match MapModel.tickAt setup history.Count i with
                    | Some at when history[i].Cue <> Quiet ->
                        let t = Dom.el "span" "rv-map__tick"
                        t.setAttribute ("style", $"left: {at * 100.0}%%")
                        ticks.appendChild t |> ignore
                    | _ -> ()

        let append (frames: Frame[]) =
            if frames.Length > 0 then
                history.AddRange frames
                tail <- (Array.last frames).After
                redrawTicks ()
                refresh ()

                if playing then
                    schedule ()

        let jump (index: int) =
            playing <- false
            unlightAll ()
            cursor <- MapModel.clampCursor setup history.Count index
            sync (MapModel.stateAt start (history.ToArray ()) cursor)
            refresh ()

        let clear () =
            window.clearTimeout timer
            scheduled <- false
            history.Clear ()
            captions.Clear ()
            widgetRefresh.Clear ()
            caption.textContent <- ""
            cursor <- -1
            setup <- 0
            tail <- start
            read <- 0
            layoutKey <- ""
            unlightAll ()

            for v in nodes.Values do
                v.Group.remove ()

            nodes.Clear ()

            for e in edges.Values do
                e.remove ()

            edges.Clear ()
            log.innerHTML <- ""
            why.textContent <- ""
            error.textContent <- ""
            root.classList.remove "rv-map--failed"
            sync start

        /// <summary>Takes the frames recorded so far as setup: drawn at once, logged, and left off the timeline.</summary>
        let baseline (g: Graph) =
            let events = Trace.events g
            read <- events.Length
            let frames = MapModel.frames start events
            history.AddRange frames
            setup <- frames.Length
            cursor <- setup - 1
            tail <- MapModel.stateAt start frames cursor
            sync tail

            for frame in frames do
                note frame

            redrawTicks ()
            refresh ()

        /// <summary>A live map's element for a control; <c>attempt</c> runs a write with the graph active.</summary>
        let widget (attempt: string -> (unit -> unit) -> unit) (control: Control) : HTMLElement =
            let write run =
                playing <- true
                attempt control.Label run

            let follow display =
                control.Binding
                |> Option.iter (fun read -> widgetRefresh.Add (Controls.follow read display))

            match control.Widget with
            | Button press -> Dom.button control.Label "rv-map__button" (fun () -> write press)
            | Slider (min, max, start, set) ->
                let wrap, input = Dom.field control.Label "slider"
                Dom.attrs input [ "type", "range"; "min", string min; "max", string max; "step", "1" ]
                input.value <- string start
                let shown = Dom.el "output" "rv-map__input-value"
                shown.textContent <- string start
                wrap.appendChild shown |> ignore

                follow (function
                    | InputValue.Integer value ->
                        input.value <- string value
                        shown.textContent <- string value
                    | _ -> ())

                input.addEventListener (
                    "input",
                    fun _ ->
                        shown.textContent <- input.value
                        write (fun () -> set (int input.value))
                )

                wrap
            | Number (start, set) ->
                let wrap, input = Dom.field control.Label "number"
                Dom.attrs input [ "type", "number"; "step", "any" ]
                input.value <- string start

                follow (function
                    | InputValue.Number value -> input.value <- string value
                    | _ -> ())

                input.addEventListener (
                    "change",
                    fun _ ->
                        Controls.parseNumber input.value
                        |> Option.iter (fun v -> write (fun () -> set v))
                )

                wrap
            | Text (start, set) ->
                let wrap, input = Dom.field control.Label "text"
                Dom.attrs input [ "type", "text" ]
                input.value <- start

                follow (function
                    | InputValue.Text value -> input.value <- value
                    | _ -> ())

                input.addEventListener ("change", fun _ -> write (fun () -> set input.value))
                wrap
            | Toggle (start, set) ->
                let wrap, input = Dom.field control.Label "toggle"
                Dom.attrs input [ "type", "checkbox" ]
                input.``checked`` <- start

                follow (function
                    | InputValue.Toggle value -> input.``checked`` <- value
                    | _ -> ())

                input.addEventListener ("change", fun _ -> write (fun () -> set input.``checked``))
                wrap

        /// <summary>Reads the events recorded since the last read and appends their frames.</summary>
        let drain (g: Graph) =
            let events = Trace.events g

            if events.Length > read then
                let fresh = events[read..]
                read <- events.Length
                append (MapModel.frames tail fresh)

        let rec start () =
            clear ()
            controlRow.innerHTML <- ""

            graph
            |> Option.iter (fun g -> (g :> IDisposable).Dispose())

            let g =
                new Graph (
                    { GraphOptions.Default with
                        FlightPolicy = policy
                    }
                )

            graph <- Some g

            let attempt (label: string) (run: unit -> unit) =
                try
                    use _ = g.Activate ()
                    run ()
                with ex ->
                    say $"{label} threw: {ex.Message}" "is-error"

            try
                match source with
                | Replayed scenario ->
                    let steps =
                        scenario g
                        |> List.collect (fun c -> c.Steps |> List.map (fun s -> c.Label, s))

                    baseline g
                    playing <- false

                    // One step per task, in order; a step's log line follows every event recorded before it.
                    let rec stepFrom (rest: (string * Step) list) =
                        match rest with
                        | (label, step) :: rest when
                            not disposed
                            && graph
                               |> Option.exists (fun current -> obj.ReferenceEquals (current, g))
                            ->
                            drain g

                            captions.Add (history.Count, step.Caption)
                            append [| MapModel.said tail (step.Log |> Option.defaultValue label) |]

                            try
                                use _ = g.Activate ()
                                step.Run ()

                                window.setTimeout (
                                    (fun () ->
                                        if
                                            not disposed
                                            && graph
                                               |> Option.exists (fun current -> obj.ReferenceEquals (current, g))
                                        then
                                            try
                                                drain g
                                                use _ = g.Activate ()
                                                untrack (fun () -> Controls.check step)
                                                stepFrom rest
                                            with ex ->
                                                fail ($"{label}: {ex.Message}")),
                                    0
                                )
                                |> ignore
                            with ex ->
                                fail ($"{label}: {ex.Message}")
                        | _ -> ()

                    stepFrom steps
                | Live scenario ->
                    let controls = scenario g
                    baseline g
                    playing <- true

                    for control in controls do
                        controlRow.appendChild (widget attempt control)
                        |> ignore

                    controlRow.appendChild (Dom.button "Reset" "rv-map__button rv-map__button--reset" start)
                    |> ignore
            with ex ->
                fail ("The example threw: " + ex.Message)

        let rec poll () =
            if not disposed then
                try
                    graph |> Option.iter drain

                    for refresh in widgetRefresh do
                        refresh ()

                    window.requestAnimationFrame (fun _ -> poll ())
                    |> ignore
                with ex ->
                    fail ("The map stopped: " + ex.Message)

        if timeline then
            let row = Dom.el "div" "rv-map__timeline"
            let speedLabel = Dom.el "label" "rv-map__speed"
            let speedCaption = Dom.el "span" "rv-map__speed-label"
            speedCaption.textContent <- "Speed"
            let speedSelect = Dom.el "select" "rv-map__speed-select" :?> HTMLSelectElement
            speedSelect.setAttribute ("aria-label", "Speed")

            for value in
                [ 0.25; 0.5; 1.0; 1.5; 2.0; 4.0; initialSpeed ]
                |> List.distinct
                |> List.sort do
                let option = Dom.el "option" ""
                let text = speedText value
                option.setAttribute ("value", text)
                option.textContent <- text + "×"
                speedSelect.appendChild option |> ignore

            speedSelect.value <- speedText speed

            speedSelect.addEventListener (
                "change",
                fun _ ->
                    match Double.TryParse (speedSelect.value, Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture) with
                    | true, value when value >= 0.25 && value <= 4.0 ->
                        speed <- value
                        root.setAttribute ("style", "--rv-map-speed: " + speedText speed)
                        window.clearTimeout timer
                        scheduled <- false

                        if playing && cursor + 1 < history.Count then
                            schedule ()
                    | _ -> ()
            )

            speedLabel.appendChild speedCaption |> ignore
            speedLabel.appendChild speedSelect |> ignore

            playButton.addEventListener (
                "click",
                fun _ ->
                    if playing then
                        playing <- false
                    else
                        if cursor + 1 >= history.Count then
                            jump -1

                        playing <- true
                        schedule ()

                    refresh ()
            )

            let step =
                Dom.button "Step" "rv-map__button" (fun () ->
                    playing <- false
                    let mutable go = true

                    while go && cursor + 1 < history.Count do
                        cursor <- cursor + 1
                        show history[cursor]

                        if history[cursor].Cue <> Quiet then
                            go <- false

                    refresh ())

            Dom.attrs scrub [ "type", "range"; "min", "0"; "step", "1"; "aria-label", "Event" ]
            scrub.addEventListener ("input", fun _ -> jump (int scrub.value - 1))
            let track = Dom.el "div" "rv-map__track"
            track.appendChild ticks |> ignore
            track.appendChild scrub |> ignore

            for e in [ playButton; step; speedLabel; track ] do
                row.appendChild e |> ignore

            bar.appendChild row |> ignore

            match source with
            | Replayed _ ->
                bar.appendChild (Dom.button "Reset" "rv-map__button rv-map__button--reset" (fun () -> jump -1))
                |> ignore
            | Live _ -> ()

        try
            start ()
            poll ()
        with ex ->
            fail ("The map stopped: " + ex.Message)

        Partas.Solid.Bindings.onCleanup (fun () ->
            disposed <- true
            window.clearTimeout timer

            graph
            |> Option.iter (fun g -> (g :> IDisposable).Dispose()))

        unbox<HtmlElement> root

    /// <summary>A signal map starting at normal playback speed; the timeline's Speed control adjusts it.</summary>
    let SignalMap (source: MapSource) (policy: FlightPolicy) (bindings: (string * int * int)[]) (timeline: bool) (grouping: Grouping) : HtmlElement =
        SignalMapWithSpeed 1.0 source policy bindings timeline grouping
