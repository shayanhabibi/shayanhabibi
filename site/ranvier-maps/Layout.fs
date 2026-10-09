namespace Ranvier.Docs.Maps

#if RANVIER_TRACE
/// <summary>Where each node of a map sits.</summary>
[<RequireQualifiedAccess>]
module Layout =

    /// <summary>The (layer, row) of each node: layers run from sources to observers, rows top to bottom.</summary>
    /// <remarks>
    /// A node's layer is the longest path from a node without sources. Rows within a layer follow the mean row of
    /// each node's sources in earlier layers, ties broken by id; a node sits level with that mean when the rows above
    /// leave room. A node takes <c>span</c> rows. The same graph always yields the same placement.
    /// </remarks>
    /// <param name="span">The rows each node takes.</param>
    /// <param name="nodes">The nodes to place.</param>
    /// <param name="sources">
    /// Each node's sources, with the rows below the source's own row its edge leaves from; sources outside
    /// <c>nodes</c> are ignored.
    /// </param>
    let placeSpanned (span: int -> int) (nodes: int list) (sources: Map<int, (int * float) list>) : Map<int, int * int> =
        let known = Set.ofList nodes

        let portsOf id =
            sources.TryFind id
            |> Option.defaultValue []
            |> List.filter (fun (s, _) -> s <> id && known.Contains s)

        let sourcesOf id =
            portsOf id |> List.map fst |> List.distinct

        let layers = System.Collections.Generic.Dictionary<int, int>()

        let rec layerOf (visiting: Set<int>) id =
            match layers.TryGetValue id with
            | true, layer -> layer
            | _ ->
                let layer =
                    sourcesOf id
                    |> List.filter (fun s -> not (visiting.Contains s))
                    |> List.map (layerOf (visiting.Add id) >> (+) 1)
                    |> List.fold max 0

                layers[id] <- layer
                layer

        let byLayer =
            known
            |> Set.toList
            |> List.groupBy (layerOf Set.empty)
            |> List.sortBy fst

        let rows = System.Collections.Generic.Dictionary<int, int>()

        for _, members in byLayer do
            let centre id =
                match
                    portsOf id
                    |> List.choose (fun (s, offset) ->
                        match rows.TryGetValue s with
                        | true, r -> Some (float r + offset)
                        | _ -> None)
                with
                | [] -> infinity
                | placed -> List.average placed

            members
            |> List.sortBy (fun id -> centre id, id)
            |> List.fold
                (fun next id ->
                    let row =
                        match centre id with
                        | c when System.Double.IsInfinity c -> next
                        | c -> max next (int (System.Math.Round c))

                    rows[id] <- row
                    row + max 1 (span id))
                0
            |> ignore

        known
        |> Seq.map (fun id -> id, (layers[id], rows[id]))
        |> Map.ofSeq

    /// <summary><c>placeSpanned</c> with one row per node.</summary>
    let place (nodes: int list) (sources: Map<int, int list>) : Map<int, int * int> =
        placeSpanned
            (fun _ -> 1)
            nodes
            (sources
             |> Map.map (fun _ -> List.map (fun s -> s, 0.0)))
#endif
