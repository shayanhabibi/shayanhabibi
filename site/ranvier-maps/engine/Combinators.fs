namespace Ranvier

/// <summary>A view's pending keys absent from its <c>Keys</c>, written by the view's pass.</summary>
type internal HeldOut<'K when 'K: equality>(graph: Graph) =
    let cell = Signal<'K[]>(graph, Array.empty)
    let pass = ResizeArray<'K>()

    /// <summary>The keys the last pass held out, in upstream order. A tracked read.</summary>
    member _.Keys = cell.Value

    /// <summary>The cell behind <c>Keys</c>.</summary>
    member _.Cell = cell

    /// <summary>Discards the keys a failed pass staged.</summary>
    member _.Begin() =
        pass.Clear ()

    /// <summary>Stages <c>key</c> for the pass in progress.</summary>
    member _.Add(key: 'K) =
        pass.Add key

    /// <summary>
    /// Stages the keys <c>upstream</c> holds out of its <c>Keys</c>, after those already staged, then writes the staged
    /// keys when they differ from the last pass.
    /// </summary>
    member _.Publish(upstream: Projection<'K, 'V>) =
        let inherited = upstream.PendingExtra

        if not (isNull (box inherited)) then
            pass.AddRange (inherited ())

        let current = cell.Peek

        if
            current.Length <> pass.Count
            || not (Seq.forall2 (=) current pass)
        then
            cell.Value <- pass.ToArray ()

        pass.Clear ()

/// <summary>
/// The keys of <c>upstream</c>, in upstream order, with rows from exactly one of <c>map</c> and <c>factory</c>, as on
/// <c>KeyedProjection</c>.
/// </summary>
type internal MapView<'K, 'V, 'U when 'K: equality>(graph: Graph, upstream: Projection<'K, 'V>, map: 'K -> 'U, factory: (unit -> 'K) -> (unit -> 'U)) as this
    =
    inherit
        RowsOf<RowEntry<'K, 'V>, 'K, 'U>(
            graph,
            (if isNull (box map) then
                 Unchecked.defaultof<_>
             else
                 fun entry -> map entry.Key),
            (if isNull (box factory) then
                 Unchecked.defaultof<_>
             else
                 fun read -> factory (fun () -> (read ()).Key))
        )

    let heldOut = HeldOut<'K> graph

    let reader = graph.RunOwned (this.Scope, upstream.NewKeyReader)

    do
        this.ReadDelta <-
            fun () ->
                heldOut.Begin ()
                let delta = reader.Read ()
                heldOut.Publish upstream
                delta

        this.VisitDelta <- fun key -> this.Visit (key, upstream.Entries.Find key)

    /// <summary>The keys <c>upstream</c> held out of its <c>Keys</c> at the last pass. A tracked read.</summary>
    member _.HeldOut = heldOut.Keys

    override this.Enumerate() =
        heldOut.Begin ()

        for key in upstream.Keys do
            let item = upstream.Entries.Find key
            let entry = this.Entries.Find key

            if
                not (isNull entry)
                && not (obj.ReferenceEquals ((entry :?> ItemRow<RowEntry<'K, 'V>, 'K, 'U>).Item.Peek, item))
            then
                this.RetireChanged key

            this.Visit (key, item)

        heldOut.Publish upstream

/// <summary>
/// The keys of <c>upstream</c> from position <c>offset ()</c>, at most <c>count ()</c> of them, in upstream order, each with
/// a row reading the upstream value.
/// </summary>
/// <remarks>
/// A null <c>offset</c> starts at position 0, and a null <c>count</c> runs to the last key. A negative offset is 0. An
/// offset past the last key or a negative count gives an empty window, and a count past the last key ends it at the last key.
/// </remarks>
type internal SliceView<'K, 'V when 'K: equality>(graph: Graph, upstream: Projection<'K, 'V>, offset: unit -> int, count: unit -> int) =
    inherit RowsOf<'K, 'K, 'V>(graph, (fun key -> upstream.Get key), Unchecked.defaultof<_>)

    let heldOut = HeldOut<'K> graph

    /// <summary>The keys <c>upstream</c> held out of its <c>Keys</c> at the last pass. A tracked read.</summary>
    member _.HeldOut = heldOut.Keys

    override this.Enumerate() =
        heldOut.Begin ()
        let offset = if isNull (box offset) then 0 else max 0 (offset ())

        let count =
            if isNull (box count) then
                System.Int32.MaxValue
            else
                count ()

        let keys = upstream.Keys
        let start = min offset keys.Length

        let stop =
            if count <= 0 then start
            elif count >= keys.Length - start then keys.Length
            else start + count

        for i in start .. stop - 1 do
            let key = keys[i]
            this.Visit (key, key)

        heldOut.Publish upstream

/// <summary>
/// The keys of <c>inclusion</c> whose predicate row holds <c>true</c>, each with the value of <c>read</c> at the key.
/// </summary>
/// <remarks>
/// A pending predicate keeps the key's last settled membership. A failed predicate leaves the key out of <c>Keys</c> and
/// keeps its row, which raises the failure. <c>read</c> raises when the key's predicate row is pending or failed.
/// </remarks>
type internal FilterView<'K, 'V, 'U when 'K: equality>
    (graph: Graph, upstream: Projection<'K, 'V>, inclusion: Projection<'K, bool> ref, read: 'K -> 'U) =
    inherit RowsOf<'K, 'K, 'U>(graph, read, Unchecked.defaultof<_>)

    let heldOut = HeldOut<'K> graph

    /// <summary>
    /// The upstream keys whose predicate has never settled, in upstream order, followed by the keys <c>upstream</c> holds
    /// out of its <c>Keys</c>. A tracked read.
    /// </summary>
    member _.HeldOut = heldOut.Keys

    override this.Enumerate() =
        heldOut.Begin ()
        let rows = inclusion.Value

        for key in rows.Keys do
            let entry = rows.Entries.Find key

            if not (isNull entry) then
                match entry.Row.TryValue with
                | Ready true -> this.Visit (key, key)
                | Ready false -> ()
                | Pending ->
                    if not entry.Settled then
                        heldOut.Add key
                    elif entry.Row.Peek then
                        this.Visit (key, key)
                | Failed _ ->
                    this.Visit (key, key)
                    this.HideLast ()

        heldOut.Publish upstream

/// <summary>
/// The keys of <c>sortKeys</c> whose sort key has settled, ascending by sort key and then by upstream position, each with
/// a row reading the upstream value.
/// </summary>
/// <remarks>
/// A pending sort key keeps the key's last settled sort key. A failed sort key leaves the key out of <c>Keys</c> and keeps
/// its row, which raises the failure. A <c>float</c> or <c>float32</c> NaN sort key orders after every other sort key.
/// </remarks>
type internal SortView<'K, 'V, 'S when 'K: equality and 'S: comparison>(graph: Graph, upstream: Projection<'K, 'V>, sortKeys: Projection<'K, 'S> ref)
    =
    inherit
        RowsOf<'K, 'K, 'V>(
            graph,
            (fun key ->
                sortKeys.Value.Get key |> ignore
                upstream.Get key),
            Unchecked.defaultof<_>
        )

    let heldOut = HeldOut<'K> graph

    // The placed keys and their sort keys in upstream order, for the current and the last pass.
    let mutable placed = ResizeArray<'K>()
    let mutable ranks = ResizeArray<'S>()
    let mutable lastPlaced = ResizeArray<'K>()
    let mutable lastRanks = ResizeArray<'S>()

    /// <summary>Positions into <c>lastPlaced</c>, in output order.</summary>
    let mutable order: int[] = Array.empty

    /// <summary>Whether <c>order</c> holds the result of a completed sort.</summary>
    let mutable sorted = false

    let isNaN (rank: 'S) =
        match box rank with
        | :? float as f -> System.Double.IsNaN f
        | :? float32 as f -> System.Single.IsNaN f
        | _ -> false

    /// <summary><c>compare</c>, with NaN equal to NaN and after every other value.</summary>
    let compareRanks (a: 'S) (b: 'S) =
        match isNaN a, isNaN b with
        | true, true -> 0
        | true, false -> 1
        | false, true -> -1
        | false, false -> compare a b

    /// <summary>
    /// Whether <c>order</c> applies to this pass: the last sort completed, and this pass placed the keys and sort keys of the
    /// last pass in the same upstream order.
    /// </summary>
    let unchanged () =
        let equalKeys = System.Collections.Generic.EqualityComparer<'K>.Default
        let mutable same = sorted && placed.Count = lastPlaced.Count
        let mutable i = 0

        while same && i < placed.Count do
            same <-
                equalKeys.Equals (placed[i], lastPlaced[i])
                && compareRanks ranks[i] lastRanks[i] = 0

            i <- i + 1

        same

    let sort () =
        if order.Length <> placed.Count then
            order <- Array.zeroCreate placed.Count

        for i in 0 .. order.Length - 1 do
            order[i] <- i

        order
        |> Array.sortInPlaceWith (fun i j ->
            let byRank = compareRanks ranks[i] ranks[j]
            if byRank <> 0 then byRank else compare i j)

    /// <summary>
    /// The upstream keys whose sort key has never settled, in upstream order, followed by the keys <c>upstream</c> holds
    /// out of its <c>Keys</c>. A tracked read.
    /// </summary>
    member _.HeldOut = heldOut.Keys

    override this.Enumerate() =
        heldOut.Begin ()
        let rows = sortKeys.Value
        placed.Clear ()
        ranks.Clear ()

        for key in rows.Keys do
            let entry = rows.Entries.Find key

            if not (isNull entry) then
                match entry.Row.TryValue with
                | Ready rank ->
                    placed.Add key
                    ranks.Add rank
                | Pending ->
                    if not entry.Settled then
                        heldOut.Add key
                    else
                        placed.Add key
                        ranks.Add entry.Row.Peek
                | Failed _ ->
                    this.Visit (key, key)
                    this.HideLast ()

        if not (unchanged ()) then
            sorted <- false
            sort ()
            sorted <- true

        for i in order do
            this.Visit (placed[i], placed[i])

        let swapKeys = lastPlaced
        lastPlaced <- placed
        placed <- swapKeys
        let swapRanks = lastRanks
        lastRanks <- ranks
        ranks <- swapRanks
        heldOut.Publish upstream

/// <summary>
/// A group of a <c>Grouping</c>: its members in upstream order and the inner view over them. The inner view's pass reads
/// <c>outerKeys</c> first, so it follows the upstream without a reader of the outer view.
/// </summary>
type internal Group<'K, 'V when 'K: equality>(graph: Graph, upstream: Projection<'K, 'V>, outerKeys: unit -> unit) =
    let members = Signal<'K[]>(graph, Array.empty)

    let view =
        new KeyedProjection<'K, 'K, 'V> (
            graph,
            id,
            (fun key -> upstream.Get key),
            Unchecked.defaultof<_>,
            fun () ->
                outerKeys ()
                members.Value
        )

    do view.GetRaisesDisposed <- true

    /// <summary>The members placed by the pass in progress, in upstream order.</summary>
    member val Pass = ResizeArray<'K>()

    member _.View = view :> Projection<'K, 'V>

    /// <summary>Writes the staged members when they differ from the last pass, then clears them.</summary>
    member this.Publish() =
        let current = members.Peek

        if
            current.Length <> this.Pass.Count
            || not (Seq.forall2 (=) current this.Pass)
        then
            members.Value <- this.Pass.ToArray ()

        this.Pass.Clear ()

/// <summary>
/// A projection keyed by group key, ordered by the upstream position of each group's first member, whose row is the inner
/// view of the group's keys in upstream order. Returned by <c>Projection.groupBy</c>.
/// </summary>
/// <remarks>
/// A pending group key keeps the key's last settled group. A key whose group key failed or has never settled is in
/// <c>UngroupedKeys</c> and in no group. An inner view is disposed once its group is empty.
/// </remarks>
type Grouping<'G, 'K, 'V when 'G: equality and 'K: equality> internal (graph: Graph, upstream: Projection<'K, 'V>, groupKeys: Projection<'K, 'G> ref) as this
    =
    inherit Projection<'G, Projection<'K, 'V>>(graph)

    let groups = Platform.KeyMap<'G, Group<'K, 'V>>()
    let order = ResizeArray<'G>()
    let emptied = ResizeArray<'G>()
    let heldOut = HeldOut<'K> graph
    let adds = ResizeArray<struct ('G * Group<'K, 'V>)>()
    let writes = ResizeArray<struct (Signal<Projection<'K, 'V>> * Projection<'K, 'V>)>()

    do this.ExtraObserved <- fun () -> heldOut.Cell.ObserverCount > 0

    member private this.Place(key: 'K, groupKey: 'G) =
        let mutable group = groups.Find groupKey

        if isNull (box group) then
            group <- graph.RunOwned (this.Scope, fun () -> Group<'K, 'V>(graph, upstream, (fun () -> this.Keys |> ignore)))
            groups.Set (groupKey, group)

        if group.Pass.Count = 0 then
            order.Add groupKey

        group.Pass.Add key

    /// <summary>Stages the row of <c>groupKey</c>: a write to a survivor's item source, or a row to create.</summary>
    member private this.Visit(groupKey: 'G, group: Group<'K, 'V>) =
        this.Seen.Add groupKey |> ignore
        this.PassKeys.Add groupKey
        let entry = this.Entries.Find groupKey

        if isNull entry then
            adds.Add (struct (groupKey, group))
        else
            writes.Add (struct ((entry :?> ItemRow<Projection<'K, 'V>, 'G, Projection<'K, 'V>>).Item, group.View))

    member private this.CreateAdded() =
        for struct (groupKey, group) in adds do
            let source = Signal<Projection<'K, 'V>>(graph, group.View)
            let entry = ItemRow<Projection<'K, 'V>, 'G, Projection<'K, 'V>>(groupKey, source)
            this.AddEntry (groupKey, entry)
            entry.Reader <- fun () -> source.Value
            entry.Row <- Memo<Projection<'K, 'V>>.Create(graph, (fun _ -> this.RunRow entry), ScopeMode.ValueRow)

    member private this.Enumerate() =
        heldOut.Begin ()
        let rows = groupKeys.Value
        order.Clear ()

        for key in rows.Keys do
            let entry = rows.Entries.Find key

            if not (isNull entry) then
                match entry.Row.TryValue with
                | Ready groupKey -> this.Place (key, groupKey)
                | Pending ->
                    if entry.Settled then
                        this.Place (key, entry.Row.Peek)
                    else
                        heldOut.Add key
                | Failed _ -> heldOut.Add key

        emptied.Clear ()

        groups.Iterate (fun groupKey group ->
            if group.Pass.Count = 0 then
                emptied.Add groupKey)

        for groupKey in order do
            let group = groups.Find groupKey
            group.Publish ()
            this.Visit (groupKey, group)

        for groupKey in emptied do
            let group = groups.Find groupKey
            groups.Remove groupKey
            group.View.Dispose ()

        heldOut.Publish upstream

    /// <summary>
    /// The upstream keys without a group: keys whose group key has never settled or has raised, in upstream order, then the
    /// pending keys held out of the upstream view's <c>Keys</c>.
    /// </summary>
    /// <remarks>
    /// Tracked; wakes its reader when the list changes. A read raises what the pass raised, as <c>Keys</c> does.
    /// </remarks>
    member this.UngroupedKeys: 'K[] = this.ReadAfterPass heldOut.Cell

    /// <summary>The group of <c>key</c>, as a tracked read of the key's group key.</summary>
    /// <remarks>
    /// While the key's group key is pending, returns the key's last settled group, or raises <c>NotReadyException</c> if it
    /// has never settled. A raising group key re-raises its exception, including for a key that settled before. Raises
    /// <c>KeyNotFoundException</c> for a key absent from the upstream <c>Keys</c>, including a pending key held out by the
    /// upstream view, as the upstream <c>Get</c> does.
    /// </remarks>
    member this.GroupOf(key: 'K) : 'G =
        groupKeys.Value.GetSettled key

    interface IProjectionPass with
        member this.Enumerate() =
            this.Enumerate ()

        member this.CreateAdded() =
            if adds.Count > 0 then
                graph.RunOwned (this.Scope, this.CreateAdded)

        member _.CommitWrites() =
            for struct (source, item) in writes do
                source.Value <- item

        member _.ClearStaged() =
            adds.Clear ()
            writes.Clear ()

/// <summary>
/// The reads of an aggregate's <c>add</c>, <c>subtract</c> or folder since its last full recompute. A change to any of them
/// runs <c>changed</c>.
/// </summary>
[<Sealed>]
type internal FoldReads(graph: Graph, changed: unit -> unit) =
    let id = graph.NextId ()
    let read = System.Collections.Generic.HashSet<ISource>(HashIdentity.Reference)
    let order = ResizeArray<ISource>()
    let mutable dirty = false
    let mutable check = false

    /// <summary>Whether a read source moved since the last call. Brings each read source current first.</summary>
    member _.Moved() =
        if check && not dirty then
            let mutable i = 0

            while not dirty && i < order.Count do
                order[i].UpdateIfNecessary()
                i <- i + 1

        let moved = dirty
        check <- false
        dirty <- false
        moved

    /// <summary>Drops every read edge.</summary>
    member this.Clear() =
        for source in order do
            source.RemoveObserver (this :> IComputation)

        read.Clear ()
        order.Clear ()
        check <- false
        dirty <- false

    interface INode with
        member _.Id = id
        member _.Status = Status.None

    interface IComputation with
        member this.AddSource source =
            if read.Add source then
                order.Add source
                source.AddObserver (this :> IComputation)

        member _.MarkDirty() =
            dirty <- true
            changed ()

        member _.MarkCheck() =
            check <- true
            changed ()

    interface IScopeHost with
        member _.Scope =
            raise (
                System.InvalidOperationException
                    "A projection aggregate's add, subtract or folder created an owned node. These functions re-run on every row change; create the node outside the aggregate."
            )

/// <summary>
/// One live key of an aggregate: the observer of the key's row and the row's last settled value.
/// </summary>
[<Sealed>]
type internal FoldRow<'K, 'V>(graph: Graph, entry: RowEntry<'K, 'V>, touched: FoldRow<'K, 'V> -> unit) =
    let id = graph.NextId ()

    member _.Entry = entry

    /// <summary>Whether the row waits in the aggregate's queue, or is being read.</summary>
    member val Queued = false with get, set

    /// <summary>Whether the aggregate observes the row.</summary>
    member val Attached = false with get, set

    /// <summary>Whether <c>Value</c> holds a settled value, contained in the aggregate state.</summary>
    member val HasValue = false with get, set

    member val Value = Unchecked.defaultof<'V> with get, set

    /// <summary>The row's exception while the row is failed, or null.</summary>
    member val Error: exn = null with get, set

    /// <summary>The last membership diff containing the key.</summary>
    member val Generation = 0 with get, set

    interface INode with
        member _.Id = id
        member _.Status = Status.None

    interface IComputation with
        member _.AddSource _ = ()

        member this.MarkDirty() =
            touched this

        member this.MarkCheck() =
            touched this

/// <summary>
/// The fold of the settled row values of <c>upstream</c>, kept current in O(changed rows) when <c>subtract</c> is non-null
/// and recomputed from the cached row values otherwise. Read through the memo it computes.
/// </summary>
/// <remarks>
/// A pending row keeps its last settled value in the state, and a row contributes from its first settled value. The state
/// returns to <c>zero</c> when the last row value leaves it. While a row is failed, the memo raises the row's exception. A throwing <c>add</c> or <c>subtract</c> makes the next
/// read recompute from the cached row values.
/// </remarks>
[<Sealed>]
type internal ProjectionFold<'K, 'V, 'S when 'K: equality>
    (graph: Graph, upstream: Projection<'K, 'V>, add: 'S -> 'V -> 'S, subtract: 'S -> 'V -> 'S, zero: 'S) as this =
    let id = graph.NextId ()
    let observers = ObserverSet ()
    let invertible = not (isNull (box subtract))
    let equal = graph.Options.Equality.Comparer<'V>()
    let rows = Platform.KeyMap<'K, FoldRow<'K, 'V>>()
    let queue = ResizeArray<FoldRow<'K, 'V>>()
    let added = ResizeArray<FoldRow<'K, 'V>>()
    let removed = ResizeArray<'K>()

    /// <summary>The <c>Keys</c> array of the last membership diff, or null before the first read.</summary>
    let mutable lastKeys: 'K[] = null

    let mutable generation = 0
    let mutable state = zero

    /// <summary>Set when <c>state</c> no longer follows from the cached row values.</summary>
    let mutable full = false

    /// <summary>The count of rows with <c>HasValue</c> set.</summary>
    let mutable valued = 0

    let mutable failed = 0
    let mutable failure: FoldRow<'K, 'V> = Unchecked.defaultof<_>

    /// <summary>Set when <c>state</c> or the failed rows change.</summary>
    let mutable moved = false

    let mutable disposed = false
    let mutable link: OwnerLink = null
    let mutable memo: Memo<'S> = Unchecked.defaultof<Memo<'S>>

    let touch () =
        if not disposed then
            observers.NotifyCheck ()

    let reads = FoldReads (graph, touch)

    let touched (row: FoldRow<'K, 'V>) =
        if not row.Queued then
            row.Queued <- true
            queue.Add row
            touch ()

    do link <- graph.CurrentOwner.AttachLinked this

    member _.Memo
        with get () = memo
        and set v = memo <- v

    /// <summary>Applies <c>step</c> to the state, or marks the state for a full recompute when <c>step</c> raises.</summary>
    member private _.Step(step: 'S -> 'S) =
        if not full then
            try
                state <- graph.RunHosted (reads :> IComputation, fun () -> step state)
                moved <- true
            with _ ->
                full <- true

    member private this.Fail(row: FoldRow<'K, 'V>, ex: exn) =
        if isNull row.Error then
            failed <- failed + 1

            if isNull (box failure) then
                failure <- row

        if not (obj.ReferenceEquals (row.Error, ex)) then
            row.Error <- ex
            moved <- true

    member private _.Unfail(row: FoldRow<'K, 'V>) =
        if not (isNull row.Error) then
            row.Error <- null
            failed <- failed - 1
            moved <- true

            if obj.ReferenceEquals (failure, row) then
                failure <- Unchecked.defaultof<_>

    /// <summary>Reads the row untracked and moves its contribution to the row's value.</summary>
    member private this.Read(row: FoldRow<'K, 'V>) =
        let reading = graph.RunUntracked (fun () -> row.Entry.Row.TryValue)
        row.Queued <- false

        match reading with
        | Ready v ->
            this.Unfail row

            if not row.HasValue then
                row.HasValue <- true
                row.Value <- v
                valued <- valued + 1

                if invertible then
                    this.Step (fun s -> add s v)
                else
                    full <- true
            else
                // A throwing comparer fails the row as a throwing row does.
                let mutable changed = false
                let mutable comparerError: exn = null

                try
                    changed <- not (equal.Equals (row.Value, v))
                with ex ->
                    comparerError <- ex

                if not (isNull comparerError) then
                    this.Fail (row, comparerError)
                elif changed then
                    let old = row.Value
                    row.Value <- v

                    if invertible then
                        this.Step (fun s -> add (subtract s old) v)
                    else
                        full <- true
        | Pending -> this.Unfail row
        | Failed ex -> this.Fail (row, ex)

    /// <summary>Stops observing the row and removes its contribution.</summary>
    member private this.Detach(row: FoldRow<'K, 'V>) =
        row.Attached <- false
        (row.Entry.Row :> ISource).RemoveObserver(row :> IComputation)
        this.Unfail row

        if row.HasValue then
            row.HasValue <- false
            let old = row.Value
            row.Value <- Unchecked.defaultof<'V>
            valued <- valued - 1

            if not invertible then
                full <- true
            elif valued = 0 && not full then
                state <- zero
                moved <- true
            else
                this.Step (fun s -> subtract s old)

    /// <summary>Attaches a row to every added key and detaches the row of every removed key. O(N) in <c>keys</c>.</summary>
    member private this.Diff(keys: 'K[]) =
        generation <- generation + 1
        added.Clear ()
        removed.Clear ()

        for key in keys do
            let entry = upstream.Entries.Find key

            if not (isNull entry) then
                let row = rows.Find key

                if
                    not (isNull (box row))
                    && obj.ReferenceEquals (row.Entry, entry)
                then
                    row.Generation <- generation
                else
                    if not (isNull (box row)) then
                        this.Detach row

                    let fresh = FoldRow<'K, 'V>(graph, entry, touched)
                    fresh.Generation <- generation
                    rows.Set (key, fresh)
                    added.Add fresh

        rows.Iterate (fun key row ->
            if row.Generation <> generation then
                removed.Add key)

        for key in removed do
            this.Detach (rows.Find key)
            rows.Remove key

        lastKeys <- keys

        for row in added do
            row.Queued <- true
            row.Attached <- true
            (row.Entry.Row :> ISource).AddObserver(row :> IComputation)
            this.Read row

        added.Clear ()
        removed.Clear ()

    /// <summary>Folds the cached row values in <c>Keys</c> order into a fresh state. O(N).</summary>
    member private _.Recompute() =
        reads.Clear ()

        let fold () =
            let mutable s = zero

            for key in lastKeys do
                let row = rows.Find key

                if not (isNull (box row)) && row.HasValue then
                    s <- add s row.Value

            s

        state <- graph.RunHosted (reads :> IComputation, fold)
        full <- false
        moved <- true

    /// <summary>
    /// Brings the state current: the membership of <c>keys</c> when non-null, then every queued row, then a full recompute
    /// if a read of the fold functions moved or the fold is non-invertible.
    /// </summary>
    member private this.Sync(keys: 'K[]) =
        if reads.Moved () then
            full <- true

        if
            not (isNull keys)
            && not (obj.ReferenceEquals (keys, lastKeys))
        then
            this.Diff keys

        let mutable i = 0

        while i < queue.Count do
            let row = queue[i]

            if row.Attached && row.Entry.Live then
                this.Read row
            else
                row.Queued <- false

            i <- i + 1

        queue.Clear ()

        if full then
            this.Recompute ()

    /// <summary>Raises the exception of the first failed row, with the row's origin when the row still holds it.</summary>
    member private _.RaiseFailure() =
        if isNull (box failure) then
            rows.Iterate (fun _ row ->
                if isNull (box failure) && not (isNull row.Error) then
                    failure <- row)

        let recorded = failure.Entry.Row.Failure

        if
            not (isNull recorded)
            && obj.ReferenceEquals (recorded.Error, failure.Error)
        then
            graph.Raise recorded
        else
            raise failure.Error

    /// <summary>The memo's body: tracks the upstream keys and this node, then returns the current state.</summary>
    member this.Compute(_: 'S voption) : 'S =
        if disposed then
            state
        else
            let keys = upstream.Keys
            graph.Track (this :> ISource)
            this.Sync keys
            moved <- false

            if failed > 0 then
                this.RaiseFailure ()

            state

    member this.Dispose() =
        if not disposed then
            disposed <- true

            if not (isNull link) then
                link.Detach ()
                link <- null

            rows.Iterate (fun _ row ->
                row.Attached <- false
                (row.Entry.Row :> ISource).RemoveObserver(row :> IComputation))

            rows.Clear ()
            queue.Clear ()
            reads.Clear ()

            if not (isNull (box memo)) then
                memo.Dispose ()

    interface INode with
        member _.Id = id
        member _.Status = Status.None

    interface ISource with
        member _.AddObserver c =
            observers.Add c

        member _.RemoveObserver c =
            observers.Remove c

        member this.UpdateIfNecessary() =
            if not disposed && not (isNull lastKeys) then
                moved <- false

                try
                    this.Sync null
                with _ ->
                    moved <- true

                if moved then
                    observers.NotifyDirty ()

    interface IOwned with
        member this.Release() =
            link <- null
            this.Dispose ()

/// <summary>Views over a <c>Projection</c> that re-run the user function only for keys whose upstream row changed.</summary>
/// <remarks>
/// A view is a <c>Projection</c> owned by the scope that creates it. A membership or order change costs O(N) per view, and O(window) for <c>take</c>, <c>skip</c> and <c>sub</c>.
/// A pending key a view holds out of <c>Keys</c> is in the <c>PendingKeys</c> of every view built on that view. A key excluded on failure
/// is absent from those views, and only <c>Get</c> and <c>TryGet</c> of the excluding view raise its error.
/// </remarks>
[<RequireQualifiedAccess>]
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Projection =
    /// <summary>The keys of <c>upstream</c> whose value satisfies <c>predicate</c>, in upstream order, with their values.</summary>
    /// <remarks>
    /// <c>predicate</c> re-runs for a key when its upstream row changes. A pending predicate keeps the key's last membership;
    /// a key whose predicate has never settled is absent from <c>Keys</c> and present in <c>PendingKeys</c>, while
    /// <c>AnyPending</c> counts only rows of keys in <c>Keys</c>. A throwing predicate excludes the key, and <c>Get</c> and
    /// <c>TryGet</c> of the key raise its exception.
    /// </remarks>
    /// <example>
    /// <code lang="fsharp">
    /// let rows = createProjection (fun t -> t.Id) id (fun () -> todos.Value)
    /// let active = rows |> Projection.filter (fun t -> not t.Done)
    /// </code>
    /// </example>
    let filter (predicate: 'V -> bool) (upstream: Projection<'K, 'V>) : Projection<'K, 'V> =
        let graph = upstream.Graph
        let inclusion = ref Unchecked.defaultof<Projection<'K, bool>>

        let read key =
            inclusion.Value.Get key |> ignore
            upstream.Get key

        let view = new FilterView<'K, 'V, 'V> (graph, upstream, inclusion, read)

        inclusion.Value <-
            graph.RunOwned (
                view.Scope,
                fun () ->
                    new KeyedProjection<'K, 'K, bool> (
                        graph,
                        id,
                        (fun key -> predicate (upstream.Get key)),
                        Unchecked.defaultof<_>,
                        fun () -> upstream.Keys
                    )
                    :> Projection<'K, bool>
            )

        view.PendingExtra <- fun () -> view.HeldOut
        view :> Projection<'K, 'V>

    /// <summary>
    /// The keys of <c>upstream</c> whose value <c>chooser</c> maps to <c>Some</c>, in upstream order, each with the value
    /// inside the <c>Some</c>.
    /// </summary>
    /// <remarks>
    /// <c>chooser</c> runs once per key when its upstream row changes, and a change between two <c>Some</c> values wakes only
    /// readers of the key's row. A pending <c>chooser</c> keeps the key's last membership; a key whose <c>chooser</c> has
    /// never settled is absent from <c>Keys</c> and present in <c>PendingKeys</c>, while <c>AnyPending</c> counts only rows
    /// of keys in <c>Keys</c>. A throwing <c>chooser</c> excludes the key, and <c>Get</c> and <c>TryGet</c> of the key raise
    /// its exception.
    /// </remarks>
    /// <example>
    /// <code lang="fsharp">
    /// let rows = createProjection (fun t -> t.Id) id (fun () -> todos.Value)
    /// let due = rows |> Projection.choose (fun t -> t.Due)
    /// </code>
    /// </example>
    let choose (chooser: 'V -> 'U option) (upstream: Projection<'K, 'V>) : Projection<'K, 'U> =
        let graph = upstream.Graph
        let choices = ref Unchecked.defaultof<Projection<'K, 'U option>>
        let inclusion = ref Unchecked.defaultof<Projection<'K, bool>>

        let read key =
            match choices.Value.Get key with
            | Some value -> value
            | None -> raise (System.Collections.Generic.KeyNotFoundException ("The projection has no key " + string key + "."))

        let view = new FilterView<'K, 'V, 'U> (graph, upstream, inclusion, read)

        graph.RunOwned (
            view.Scope,
            fun () ->
                choices.Value <-
                    new KeyedProjection<'K, 'K, 'U option> (
                        graph,
                        id,
                        (fun key -> chooser (upstream.Get key)),
                        Unchecked.defaultof<_>,
                        fun () -> upstream.Keys
                    )

                inclusion.Value <-
                    new KeyedProjection<'K, 'K, bool> (
                        graph,
                        id,
                        (fun key -> Option.isSome (choices.Value.Get key)),
                        Unchecked.defaultof<_>,
                        fun () -> upstream.Keys
                    )
        )

        view.PendingExtra <- fun () -> view.HeldOut
        view :> Projection<'K, 'U>

    /// <summary>The keys of <c>upstream</c>, in upstream order, each with <c>mapping</c> of its value.</summary>
    /// <remarks>
    /// <c>mapping</c> re-runs for a key when its upstream row changes. A throwing <c>mapping</c> keeps the key, and <c>Get</c>
    /// and <c>TryGet</c> of the key raise its exception. <c>PendingKeys</c> includes the pending keys <c>upstream</c> holds
    /// outside its <c>Keys</c>.
    /// </remarks>
    let map (mapping: 'V -> 'U) (upstream: Projection<'K, 'V>) : Projection<'K, 'U> =
        let view =
            new MapView<'K, 'V, 'U> (upstream.Graph, upstream, (fun key -> mapping (upstream.Get key)), Unchecked.defaultof<_>)

        if not (isNull (box upstream.PendingExtra)) then
            view.PendingExtra <- fun () -> view.HeldOut

        view :> Projection<'K, 'U>

    /// <summary>
    /// The keys of <c>upstream</c>, in upstream order, each row the reader <c>mapping</c> returns for the key and a
    /// tracked read of its upstream value.
    /// </summary>
    /// <remarks>
    /// <c>mapping</c> runs once per key, untracked, in a scope that owns the nodes it creates and is disposed with the key.
    /// The reader re-runs when a value it read changes. <c>PendingKeys</c> includes the pending keys <c>upstream</c> holds
    /// outside its <c>Keys</c>.
    /// </remarks>
    /// <example>
    /// <code lang="fsharp">
    /// let rows = createProjection (fun t -> t.Id) id (fun () -> todos.Value)
    /// let labels = rows |> Projection.mapWith (fun id todo -> fun () -> $"{id}: {(todo ()).Title}")
    /// </code>
    /// </example>
    let mapWith (mapping: 'K -> (unit -> 'V) -> (unit -> 'U)) (upstream: Projection<'K, 'V>) : Projection<'K, 'U> =
        let factory (key: unit -> 'K) =
            let k = key ()
            mapping k (fun () -> upstream.Get k)

        let view =
            new MapView<'K, 'V, 'U> (upstream.Graph, upstream, Unchecked.defaultof<_>, factory)

        if not (isNull (box upstream.PendingExtra)) then
            view.PendingExtra <- fun () -> view.HeldOut

        view :> Projection<'K, 'U>

    /// <summary>
    /// The keys of <c>upstream</c> with their values, ascending by <c>projection</c> of the value; equal sort keys keep upstream
    /// order.
    /// </summary>
    /// <remarks>
    /// A <c>float</c> or <c>float32</c> NaN sort key orders last; <c>None</c> orders first. A pending sort key keeps the key's last
    /// settled sort key; a key whose sort key has never settled is only in <c>PendingKeys</c>. A throwing sort key excludes the
    /// key, and <c>Get</c> and <c>TryGet</c> of the key raise its exception.
    /// </remarks>
    /// <example>
    /// <code lang="fsharp">
    /// let rows = createProjection (fun t -> t.Id) id (fun () -> todos.Value)
    /// let byDue = rows |> Projection.sortBy (fun t -> t.Due)
    /// </code>
    /// </example>
    let sortBy (projection: 'V -> 'S) (upstream: Projection<'K, 'V>) : Projection<'K, 'V> =
        let graph = upstream.Graph
        let sortKeys = ref Unchecked.defaultof<Projection<'K, 'S>>
        let view = new SortView<'K, 'V, 'S> (graph, upstream, sortKeys)

        sortKeys.Value <-
            graph.RunOwned (
                view.Scope,
                fun () ->
                    new KeyedProjection<'K, 'K, 'S> (
                        graph,
                        id,
                        (fun key -> projection (upstream.Get key)),
                        Unchecked.defaultof<_>,
                        fun () -> upstream.Keys
                    )
                    :> Projection<'K, 'S>
            )

        view.PendingExtra <- fun () -> view.HeldOut
        view :> Projection<'K, 'V>

    let private slice (offset: unit -> int) (count: unit -> int) (upstream: Projection<'K, 'V>) : Projection<'K, 'V> =
        let view = new SliceView<'K, 'V> (upstream.Graph, upstream, offset, count)

        if not (isNull (box upstream.PendingExtra)) then
            view.PendingExtra <- fun () -> view.HeldOut

        view :> Projection<'K, 'V>

    /// <summary>The first <c>count ()</c> keys of <c>upstream</c>, in upstream order, with their values.</summary>
    /// <remarks>
    /// <c>count</c> is a tracked read: the window follows a signal it reads, as well as membership and order changes
    /// upstream. A count past the last key selects every key, and a negative count gives an empty window,
    /// as <c>List.truncate</c> does. A key that stays in the window keeps its row. A pass costs O(window), and a throwing
    /// <c>count</c> fails the pass. <c>PendingKeys</c> includes the pending keys <c>upstream</c> holds outside its
    /// <c>Keys</c>.
    /// </remarks>
    /// <example>
    /// <code lang="fsharp">
    /// let pageSize = createSignal 20
    /// let rows = createProjection (fun t -> t.Id) id (fun () -> todos.Value)
    /// let firstPage = rows |> Projection.sortBy (fun t -> t.Due) |> Projection.take (fun () -> pageSize.Value)
    /// </code>
    /// </example>
    let take (count: unit -> int) (upstream: Projection<'K, 'V>) : Projection<'K, 'V> =
        slice Unchecked.defaultof<_> count upstream

    /// <summary>The keys of <c>upstream</c> after the first <c>count ()</c>, in upstream order, with their values.</summary>
    /// <remarks>
    /// <c>count</c> is a tracked read, as in <c>take</c>. A count past the last key gives an empty window, and a
    /// negative count selects every key. A key that stays in the window keeps its row. A pass costs O(window), and a throwing
    /// <c>count</c> fails the pass. <c>PendingKeys</c> includes the pending keys <c>upstream</c> holds outside its
    /// <c>Keys</c>.
    /// </remarks>
    /// <example>
    /// <code lang="fsharp">
    /// let rows = createProjection (fun t -> t.Id) id (fun () -> todos.Value)
    /// let rest = rows |> Projection.skip (fun () -> 1)
    /// </code>
    /// </example>
    let skip (count: unit -> int) (upstream: Projection<'K, 'V>) : Projection<'K, 'V> =
        slice count Unchecked.defaultof<_> upstream

    /// <summary>
    /// The keys of <c>upstream</c> from position <c>offset ()</c>, at most <c>count ()</c> of them, in upstream order, with
    /// their values.
    /// </summary>
    /// <remarks>
    /// <c>offset</c> and <c>count</c> are tracked reads, as in <c>take</c>. <c>offset</c> clamps as the count of
    /// <c>skip</c> does, and <c>count</c> as the count of <c>take</c>. A key that stays in the window keeps its row, so
    /// shifting the window by d positions creates and disposes at most d rows. A pass costs O(window), and a throwing
    /// <c>offset</c> or <c>count</c> fails the pass. <c>PendingKeys</c> includes the pending keys <c>upstream</c> holds
    /// outside its <c>Keys</c>.
    /// </remarks>
    /// <example>
    /// <code lang="fsharp">
    /// let page = createSignal 0
    /// let rows = createProjection (fun t -> t.Id) id (fun () -> todos.Value)
    /// let visible = rows |> Projection.sub (fun () -> page.Value * 20) (fun () -> 20)
    /// </code>
    /// </example>
    let sub (offset: unit -> int) (count: unit -> int) (upstream: Projection<'K, 'V>) : Projection<'K, 'V> =
        slice offset count upstream

    /// <summary>
    /// The groups of <c>upstream</c> by <c>projection</c> of each value, ordered by the upstream position of each group's
    /// first member, each an inner view of the group's keys in upstream order.
    /// </summary>
    /// <remarks>
    /// A pending group key keeps the key's last settled group. A key whose group key throws or has never settled is in
    /// <c>UngroupedKeys</c> and in no group. A moved key leaves its old group and joins its new one in the same pass. An
    /// emptied group's inner view is disposed: its <c>Keys</c> is empty and <c>Get</c> raises
    /// <c>ObjectDisposedException</c>.
    /// </remarks>
    /// <example>
    /// <code lang="fsharp">
    /// let rows = createProjection (fun t -> t.Id) id (fun () -> todos.Value)
    /// let byOwner = rows |> Projection.groupBy (fun t -> t.Owner)
    /// </code>
    /// </example>
    let groupBy (projection: 'V -> 'G) (upstream: Projection<'K, 'V>) : Grouping<'G, 'K, 'V> =
        let graph = upstream.Graph
        let groupKeys = ref Unchecked.defaultof<Projection<'K, 'G>>
        let view = new Grouping<'G, 'K, 'V> (graph, upstream, groupKeys)

        groupKeys.Value <-
            graph.RunOwned (
                view.Scope,
                fun () ->
                    new KeyedProjection<'K, 'K, 'G> (
                        graph,
                        id,
                        (fun key -> projection (upstream.Get key)),
                        Unchecked.defaultof<_>,
                        fun () -> upstream.Keys
                    )
                    :> Projection<'K, 'G>
            )

        view

    /// <summary>
    /// A memo of the settled values of <c>upstream</c> folded into <c>zero</c> with <c>add</c>, kept current by applying each
    /// row change: <c>add</c> for an added key, <c>subtract</c> of its last value for a removed key, and both for a changed value.
    /// </summary>
    /// <remarks>
    /// <c>subtract</c> must invert <c>add</c>. A row edit costs O(1) calls and reads the edited row alone; a key change costs
    /// an O(N) key diff. The fold covers settled values: a pending row keeps its last one, as in <c>Snapshot</c>, and
    /// <c>AnyPending</c> reports it. A failed row makes a read raise its exception. A value read by <c>add</c> or
    /// <c>subtract</c> re-folds the cached values on change. The memo is disposed with the calling scope.
    /// </remarks>
    /// <example>
    /// <code lang="fsharp">
    /// let rows = createProjection (fun t -> t.Id) (fun t -> t.Estimate) (fun () -> todos.Value)
    /// let total = rows |> Projection.foldGroup (+) (-) 0
    /// </code>
    /// </example>
    let foldGroup (add: 'S -> 'V -> 'S) (subtract: 'S -> 'V -> 'S) (zero: 'S) (upstream: Projection<'K, 'V>) : Memo<'S> =
        if isNull (box subtract) then
            raise (System.ArgumentNullException (nameof subtract))

        let graph = upstream.Graph
        let aggregate = ProjectionFold<'K, 'V, 'S>(graph, upstream, add, subtract, zero)
        aggregate.Memo <- Memo<'S>.Create(graph, aggregate.Compute, ScopeMode.Pure)
        aggregate.Memo

    /// <summary>A memo of the settled values of <c>upstream</c> folded with <c>folder</c> from <c>state</c>, in <c>Keys</c> order.</summary>
    /// <remarks>
    /// Any key or value change re-folds every row's cached value: O(N) calls of <c>folder</c>, reading only the changed rows.
    /// Use <c>foldGroup</c> when the fold has an inverse. Pending, failed and disposal behave as in <c>foldGroup</c>.
    /// </remarks>
    let fold (folder: 'S -> 'V -> 'S) (state: 'S) (upstream: Projection<'K, 'V>) : Memo<'S> =
        let graph = upstream.Graph

        let aggregate =
            ProjectionFold<'K, 'V, 'S>(graph, upstream, folder, Unchecked.defaultof<_>, state)

        aggregate.Memo <- Memo<'S>.Create(graph, aggregate.Compute, ScopeMode.Pure)
        aggregate.Memo

    /// <summary>A memo of the sum of <c>projection</c> over the values of <c>upstream</c>, kept current per row change.</summary>
    /// <remarks>
    /// <c>projection</c> re-runs for a key when its upstream row changes, in a view the memo reads as <c>foldGroup</c> does.
    /// A pending or throwing <c>projection</c> behaves as a pending or failed row of <c>foldGroup</c>. While the sum is
    /// infinite or NaN, each row change re-adds every cached value: O(N).
    /// </remarks>
    /// <example>
    /// <code lang="fsharp">
    /// let rows = createProjection (fun t -> t.Id) id (fun () -> todos.Value)
    /// let hours = rows |> Projection.sumBy (fun t -> t.Hours)
    /// </code>
    /// </example>
    [<NoDynamicInvocation>]
    let inline sumBy (projection: 'V -> ^N) (upstream: Projection<'K, 'V>) : Memo< ^N > =
        upstream
        |> map projection
        |> foldGroup
            (fun s n -> s + n)
            (fun s n ->
                let d = s - n

                // A throwing subtract re-folds from the cached values. d - d is non-zero exactly when d is infinite or NaN.
                if d - d <> LanguagePrimitives.GenericZero then
                    raise (System.ArithmeticException ())

                d)
            LanguagePrimitives.GenericZero

    /// <summary>A memo of how many values of <c>upstream</c> satisfy <c>predicate</c>, kept current per row change.</summary>
    /// <remarks>
    /// <c>predicate</c> re-runs for a key when its upstream row changes. A pending or throwing <c>predicate</c> behaves as a
    /// pending or failed row of <c>foldGroup</c>.
    /// </remarks>
    let countBy (predicate: 'V -> bool) (upstream: Projection<'K, 'V>) : Memo<int> =
        upstream
        |> map predicate
        |> foldGroup (fun n hit -> if hit then n + 1 else n) (fun n hit -> if hit then n - 1 else n) 0

    /// <summary>A memo of whether some value of <c>upstream</c> satisfies <c>predicate</c>: a <c>countBy</c> above zero.</summary>
    /// <remarks>Costs and pending and failed rows follow <c>countBy</c>. An empty projection reads <c>false</c>.</remarks>
    let exists (predicate: 'V -> bool) (upstream: Projection<'K, 'V>) : Memo<bool> =
        let count = countBy predicate upstream
        Memo<bool>.Create(upstream.Graph, (fun _ -> count.Value > 0), ScopeMode.Pure)

    /// <summary>A memo of whether every value of <c>upstream</c> satisfies <c>predicate</c>: a <c>countBy</c> of misses at zero.</summary>
    /// <remarks>Costs and pending and failed rows follow <c>countBy</c>. An empty projection reads <c>true</c>.</remarks>
    let forall (predicate: 'V -> bool) (upstream: Projection<'K, 'V>) : Memo<bool> =
        let misses = countBy (predicate >> not) upstream
        Memo<bool>.Create(upstream.Graph, (fun _ -> misses.Value = 0), ScopeMode.Pure)
