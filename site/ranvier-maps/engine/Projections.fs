namespace Ranvier

open System
open System.Collections.Generic
open System.Collections.ObjectModel

module internal Awaited =
    /// <summary>
    /// The node as a source, or <c>ValueNone</c> for a node that is not one.
    /// </summary>
    /// <remarks>
    /// Under Fable, which cannot type-test an interface, every node is taken
    /// as a source. Every node the library raises in <c>NotReadyException</c> is one.
    /// </remarks>
    let source (node: INode) : ISource voption =
#if FABLE_COMPILER
        ValueSome (unbox<ISource> node)
#else
        match node with
        | :? ISource as source -> ValueSome source
        | _ -> ValueNone
#endif

/// <summary>
/// Observes each pending row of a projection. A mark on a row marks the summary readers for a check and schedules a
/// refresh, so a row that settles or fails updates <c>AnyPending</c> and <c>PendingKeys</c> without a reader of its own.
/// </summary>
[<Sealed>]
type internal RowWatch(graph: Graph, refresh: unit -> unit, touched: unit -> unit) =
    let id = graph.NextId ()
    do Tracer.RowWatchNew (graph, id)
    let mutable queued = false
    let mutable running = false

    member this.Request() =
        if not queued && not running then
            queued <- true
            graph.Schedule (this :> IScheduled)

    interface INode with
        member _.Id = id
        member _.Status = Status.None

    interface IComputation with
        member _.AddSource _ = ()

        member this.MarkDirty() =
            touched ()
            this.Request ()

        member this.MarkCheck() =
            touched ()
            this.Request ()

    interface IScheduled with
        member _.Execute() =
            queued <- false
            running <- true

            try
                refresh ()
            finally
                running <- false

/// <summary>The projection behind a <c>ProjectionBeacon</c>.</summary>
type internal IBeaconHost =
    /// <summary>
    /// Refreshes the sources of a live projection whose last pass succeeded, then runs its pass if it is stale. A pass
    /// failure stays in <c>Error</c> and <c>Status</c> for the reader's own read to raise.
    /// </summary>
    abstract Refresh: unit -> unit

/// <summary>
/// A projection's dependency edge, tracked by every read of the projection alongside the row or summary node read.
/// </summary>
/// <remarks>
/// A pass that fails before it publishes anything still wakes its readers. The beacon carries no value and sends <c>Dirty</c>
/// only. Resolving <c>Check</c> through the beacon runs the stale passes of the projection and of the projections it
/// reads before the reader's body runs.
/// </remarks>
[<Sealed; NoEquality; NoComparison>]
type internal ProjectionBeacon(graph: Graph, host: IBeaconHost) =
    let id = graph.NextId ()
    let observers = ObserverSet ()
    do Tracer.Bind (observers, graph, id)
    do Tracer.BeaconNew (graph, id)

    interface INode with
        member _.Id = id
        member _.Status = Status.None

    interface ISource with
        member _.AddObserver c =
            observers.Add c

        member _.RemoveObserver c =
            observers.Remove c

        member _.UpdateIfNecessary() =
            host.Refresh ()

    /// <summary>
    /// Marks everything watching the projection dirty, except <c>running</c>: the
    /// reader the failure is raised to.
    /// </summary>
    member _.NotifyFailure(running: IComputation) =
        observers.NotifyDirtyExcept running

    member _.ObserverCount = observers.Count

/// <summary>
/// A row with its item source.
/// </summary>
[<Sealed; AllowNullLiteral>]
type internal ItemRow<'T, 'K, 'V>(key: 'K, item: Signal<'T>) =
    inherit RowEntry<'K, 'V>(key)

    member _.Item = item

/// <summary>
/// A read-only dictionary that enumerates in insertion order. Holds every key
/// of <c>'K</c>, <c>None</c> and <c>()</c> included.
/// </summary>
#if FABLE_COMPILER
[<Sealed; Fable.Core.AttachMembers>]
#else
[<Sealed>]
#endif
type internal RowSnapshot<'K, 'V when 'K: equality>(capacity: int) =
    let pairs = ResizeArray<KeyValuePair<'K, 'V>>(capacity)

    /// <summary>
    /// One past each key's index in <c>pairs</c>, and 0 for an absent key.
    /// </summary>
    let slots = Platform.KeyMap<'K, int>()

    /// <summary>
    /// Appends a row for a key not yet in the snapshot.
    /// </summary>
    member _.Add(key: 'K, value: 'V) =
        pairs.Add (KeyValuePair (key, value))
        slots.Set (key, pairs.Count)

    member private _.ValueAt(key: 'K) =
        let slot = slots.Find key

        if slot > 0 then
            pairs[slot - 1].Value
        else
            raise (KeyNotFoundException ("The snapshot has no key " + string key + "."))

    interface IReadOnlyDictionary<'K, 'V> with
        member _.Count = pairs.Count
        member _.Keys = pairs |> Seq.map _.Key
        member _.Values = pairs |> Seq.map _.Value

        member _.ContainsKey key =
            slots.Find key > 0

        member _.TryGetValue(key, value) =
            let slot = slots.Find key

            if slot > 0 then
                value <- pairs[slot - 1].Value
                true
            else
                value <- Unchecked.defaultof<'V>
                false

        member this.Item
            with get key = this.ValueAt key

        member _.GetEnumerator() : IEnumerator<KeyValuePair<'K, 'V>> =
            (pairs :> seq<_>).GetEnumerator()

        member _.GetEnumerator() : Collections.IEnumerator =
            (pairs :> Collections.IEnumerable).GetEnumerator()

#if FABLE_COMPILER
    // The read side of the JS `Map` API. The write members raise `NotSupportedException`.
    interface Fable.Core.JS.Map<'K, 'V> with
        member _.size = pairs.Count

        member _.has key =
            slots.Find key > 0

        member this.get key =
            this.ValueAt key

        member _.keys() =
            pairs |> Seq.map _.Key

        member _.values() =
            pairs |> Seq.map _.Value

        member _.entries() =
            pairs |> Seq.map (fun p -> p.Key, p.Value)

        member this.forEach(callback, _) =
            for p in pairs do
                callback p.Value p.Key this

        member _.set(_, _) =
            raise (NotSupportedException "The snapshot is read-only.")

        member _.delete _ =
            raise (NotSupportedException "The snapshot is read-only.")

        member _.clear() =
            raise (NotSupportedException "The snapshot is read-only.")
#endif

/// <summary>
/// A projection's pass steps, implemented by its subclasses.
/// </summary>
type internal IProjectionPass =
    /// <summary>
    /// Reads the source and calls the subclass's <c>Visit</c> once per item, in
    /// source order, inside the projection's tracking context.
    /// </summary>
    abstract Enumerate: unit -> unit

    /// <summary>
    /// Creates the rows for the keys this pass added and stores them in
    /// <c>Entries</c>. Runs untracked, inside the diff's batch.
    /// </summary>
    abstract CreateAdded: unit -> unit

    /// <summary>
    /// Writes the item source of every survivor this pass visited.
    /// </summary>
    abstract CommitWrites: unit -> unit

    /// <summary>
    /// Clears the adds and writes this pass staged.
    /// </summary>
    abstract ClearStaged: unit -> unit

/// <summary>
/// A keyed collection derived from a source collection: a key set, and one row
/// per live key.
/// </summary>
/// <remarks>
/// A row is a lazy computation over the key's item and whatever its reader
/// reads. A reader of a row wakes when the row's value changes or its key is
/// removed; a reader of <c>Keys</c> wakes when membership or order changes. Every
/// reader also wakes when a pass fails, suspends or recovers.
/// </remarks>
[<AbstractClass>]
type Projection<'K, 'V when 'K: equality> internal (graph: Graph) as this =
    let id = graph.NextId ()
    do Tracer.Reserve (graph, id)
    let sources = SourceList ()
    do Tracer.Bind (sources, graph, id)

    /// <summary>
    /// The beacons among <c>sources</c>: the sources that can be stale without marking the projection. Collected from
    /// <c>sources</c> by the first <c>Refresh</c> after each pass.
    /// </summary>
    let upstreamBeacons = ResizeArray<ISource>()
    let mutable beaconsStale = true

    /// <summary>
    /// Parent of every key scope, and of the rows in the value form.
    /// </summary>
    let scope = new Owner (graph.Root)

    /// <summary>
    /// Owner of the nodes the source and <c>keyOf</c> create, allocated by the
    /// first of them. Discharged before each pass.
    /// </summary>
    let mutable passScope: Owner = Unchecked.defaultof<Owner>

    /// <summary>
    /// The key set. Written only when order or membership moved.
    /// </summary>
    let keys = Signal<'K[]>(graph, Array.empty)

    let entries = Platform.KeyMap<'K, RowEntry<'K, 'V>>()

    /// <summary>The live key readers, and null while none exists.</summary>
    let mutable log: KeyLog<'K> = null

    let mutable values: ProjectionValues<'K, 'V> = Unchecked.defaultof<_>

    let beacon = ProjectionBeacon (graph, this)

    /// <summary>
    /// Starts <c>Dirty</c> so the first read runs a pass.
    /// </summary>
    let mutable freshness = Freshness.Dirty

    /// <summary>
    /// The last pass's failure, as <c>Effect.Error</c>.
    /// </summary>
    let mutable failure: Failure = null
    let mutable status = Status.None
    let mutable disposed = false
    let mutable link: OwnerLink = null
    let mutable runs = 0
    let mutable queued = false

    /// <summary>
    /// Set while the diff disposes removed keys. An invalidation arriving then
    /// is deferred to the end of the pass.
    /// </summary>
    let mutable inCleanup = false
    let mutable deferredDirty = false

    /// <summary>
    /// Set when a source marks the projection dirty, and cleared by the next
    /// pass. A pass run without it is a reader's retry of a failure.
    /// </summary>
    let mutable invalidated = false

    /// <summary>
    /// Set when a source marks the projection for a check, and cleared by the
    /// next pass.
    /// </summary>
    let mutable checkPending = false

    /// <summary>
    /// Whether anything observed a row, the key set or a pending summary as of
    /// the last pass or read. A write to the source schedules a pass only while
    /// this holds.
    /// </summary>
    let mutable observed = false

    /// <summary>
    /// This pass's keys, in source order.
    /// </summary>
    let passKeys = ResizeArray<'K>()
    let seen = Platform.KeySet<'K>()

    /// <summary>
    /// The keys of this pass that keep their row and stay out of <c>Keys</c>. Null until the first such key.
    /// </summary>
    let mutable hidden: Platform.KeySet<'K> = Unchecked.defaultof<_>

    /// <summary>The <c>hidden</c> keys of the last applied pass. Null exactly while <c>hidden</c> is.</summary>
    let mutable lastHidden: Platform.KeySet<'K> = Unchecked.defaultof<_>

    /// <summary>
    /// Keys whose row is pending.
    /// </summary>
    let inFlight = Platform.KeySet<'K>()

    /// <summary>
    /// The computation reading the result of the pass in progress, left unmarked by the pass's writes and its beacon.
    /// Null outside <c>Keys</c> and <c>Snapshot</c>.
    /// </summary>
    let mutable puller: IComputation = Unchecked.defaultof<IComputation>

    /// <summary>Whether <c>inFlight</c> changed since <c>anyPending</c> was last written.</summary>
    let mutable anyMoved = false

    /// <summary>Whether <c>inFlight</c> changed since <c>inFlightVersion</c> was last written.</summary>
    let mutable versionMoved = false

    /// <summary>
    /// Whether <c>inFlight</c> is non-empty. Read by <c>AnyPending</c>.
    /// </summary>
    let anyPending = Signal<bool>(graph, false, EqualityComparer<bool>.Default)

    /// <summary>
    /// Bumped on every change to <c>inFlight</c>. Read by <c>PendingKeys</c>.
    /// </summary>
    let inFlightVersion = Signal<int>(graph, 0, EqualityComparer<int>.Default)

    let removed = ResizeArray<'K>()
    let refreshing = ResizeArray<RowEntry<'K, 'V>>()

    /// <summary>Marks the readers of the summary for a check, which brings the pending rows current.</summary>
    let touchSummary () =
        Tracer.Unattributed graph
        anyPending.NotifyCheck ()
        inFlightVersion.NotifyCheck ()
        Tracer.Notified graph

    let watch = RowWatch (graph, (fun () -> this.RefreshSummary ()), touchSummary)

    do link <- graph.CurrentOwner.AttachLinked this
    do Tracer.ProjectionNew (graph, id, link.Owner)
    do Tracer.ScopeNew (scope, graph, id)
    do Tracer.Part (graph, (keys :> INode).Id, id, null)
    do Tracer.Part (graph, (beacon :> INode).Id, id, null)
    do Tracer.Part (graph, (watch :> INode).Id, id, null)
    do Tracer.Part (graph, (anyPending :> INode).Id, id, null)
    do Tracer.Part (graph, (inFlightVersion :> INode).Id, id, null)

    /// <summary>
    /// Links the source a suspended pass awaits into the running reader.
    /// </summary>
    let linkAwaited (ex: exn) =
        match ex with
        | NotReadyException node ->
            match Awaited.source node with
            | ValueSome awaited -> graph.Track awaited
            | ValueNone -> ()
        | _ -> ()

    /// <summary>
    /// Writes the keys cell when the key sequence differs from its content.
    /// </summary>
    let publishKeys () =
        let current = keys.Peek
        let mutable same = current.Length = passKeys.Count
        let mutable i = 0

        while same && i < passKeys.Count do
            if not (EqualityComparer<'K>.Default.Equals(current[i], passKeys[i])) then
                same <- false

            i <- i + 1

        if not same then
            Tracer.Moved (graph, id, null)
            keys.WriteExcept (passKeys.ToArray (), puller)
            Tracer.Notified graph

    /// <summary>
    /// Whether anything other than the projection's <c>RowWatch</c> reads the row.
    /// </summary>
    let rowObserved (entry: RowEntry<'K, 'V>) =
        entry.Row.ObserverCount > (if entry.Watched then 1 else 0)

    member private this.Pass = box this :?> IProjectionPass

    member internal _.Graph = graph
    member internal _.Entries = entries
    member internal _.Scope = scope

    member internal _.PassKeys = passKeys
    member internal _.Seen = seen

    /// <summary>The tracked input delta for a membership pass, or null for snapshot enumeration.</summary>
    member val internal ReadDelta: unit -> ProjectionDelta<'K> = Unchecked.defaultof<_> with get, set
    /// <summary>Stages an added or replaced key from the input delta.</summary>
    member val internal VisitDelta: 'K -> unit = Unchecked.defaultof<_> with get, set

    /// <summary>
    /// Leaves the last key of <c>PassKeys</c> out of <c>Keys</c> while keeping its row. The key readers see the key as
    /// removed until a pass includes it again.
    /// </summary>
    member internal _.HideLast() =
        let last = passKeys.Count - 1
        let key = passKeys[last]
        passKeys.RemoveAt last

        if isNull (box hidden) then
            hidden <- Platform.KeySet<'K>()
            lastHidden <- Platform.KeySet<'K>()

        hidden.Add key |> ignore

    /// <summary>
    /// Records for the key readers each key that entered or left the hidden keys with its row intact, then makes this
    /// pass's hidden keys the last applied ones.
    /// </summary>
    member private _.SettleHidden() =
        if not (isNull log) then
            // A key added by this pass and hidden records Added then Removed, which cancel.
            hidden.Iterate (fun key ->
                if not (lastHidden.Contains key) then
                    log.Record (key, KeyChange.Removed))

            lastHidden.Iterate (fun key ->
                if
                    not (hidden.Contains key)
                    && not (isNull (entries.Find key))
                then
                    log.Record (key, KeyChange.Added))

        let swap = lastHidden
        lastHidden <- hidden
        hidden <- swap
        hidden.Clear ()

    /// <summary>Stores the row of an added key, and records the addition for the key readers.</summary>
    member internal _.AddEntry(key: 'K, entry: RowEntry<'K, 'V>) =
        entries.Set (key, entry)

        if not (isNull log) then
            log.Record (key, KeyChange.Added)

    /// <summary>Whether a key reader is live.</summary>
    member internal _.HasKeyReaders = not (isNull log)

    /// <summary>
    /// The row computation's body: the reader, reported to the pending
    /// summary.
    /// </summary>
    member internal this.RunRow(entry: RowEntry<'K, 'V>) : 'V =
        try
            let value = entry.Reader ()

            if not entry.Row.Violated then
                entry.Settled <- true

            this.Report (entry, false)
            value
        with
        | NotReadyException _ ->
            this.Report (entry, true)
            reraise ()
        | ex ->
            // The row memo records the projection as the origin: the row is internal.
            let recorded = graph.FailureOf (ex, this, null)
            this.Report (entry, false)
            graph.Raised recorded
            reraise ()

    /// <summary>
    /// Records whether a live row is pending. A pending row is observed by
    /// <c>watch</c>, so its settle reaches the summary without a reader.
    /// </summary>
    member private _.Report(entry: RowEntry<'K, 'V>, pending: bool) =
        if entry.Live && pending <> entry.Watched then
            entry.Watched <- pending

            if pending then
                (entry.Row :> ISource).AddObserver watch
                inFlight.Add entry.Key |> ignore
            else
                (entry.Row :> ISource).RemoveObserver watch
                inFlight.Remove entry.Key

            this.MoveSummary ()
            watch.Request ()

    /// <summary>
    /// Records a change to <c>inFlight</c>: marks the summary readers for a check and schedules the summary write.
    /// </summary>
    member private _.MoveSummary() =
        anyMoved <- true
        versionMoved <- true
        touchSummary ()
        graph.RequestFlush ()

    /// <summary>Brings every pending row current, then publishes the summary.</summary>
    member private this.RefreshSummary() =
        if not disposed then
            this.RefreshRows ()
            this.PublishSummary ()

    /// <summary>Brings every pending row current, in the caller's tracking context.</summary>
    member private _.RefreshRows() =
        if inFlight.Count > 0 then
            inFlight.Iterate (fun key ->
                let entry = entries.Find key

                if not (isNull entry) then
                    refreshing.Add entry)

            Tracer.Walk (graph, id)

            for entry in refreshing do
                (entry.Row :> ISource).UpdateIfNecessary()

            Tracer.Walked (graph, id)
            refreshing.Clear ()

    /// <summary>Writes <c>anyPending</c> if it moved, leaving <c>running</c> unmarked.</summary>
    member private _.PublishAny(running: IComputation) =
        if anyMoved then
            anyMoved <- false
            anyPending.WriteExcept (inFlight.Count > 0, running)

    /// <summary>Bumps <c>inFlightVersion</c> if it moved, leaving <c>running</c> unmarked.</summary>
    member private _.PublishVersion(running: IComputation) =
        if versionMoved then
            versionMoved <- false
            inFlightVersion.WriteExcept (inFlightVersion.Peek + 1, running)

    /// <summary>Writes each summary cell that moved.</summary>
    member private this.PublishSummary() =
        if anyMoved || versionMoved then
            graph.RunBatch (fun () ->
                this.PublishAny Unchecked.defaultof<IComputation>
                this.PublishVersion Unchecked.defaultof<IComputation>)

    /// <summary>
    /// Drops a removed key's row from the summary and disposes the row with
    /// every node its factory created.
    /// </summary>
    member private _.Retire(entry: RowEntry<'K, 'V>) =
        entry.Live <- false

        if not (isNull (box values)) then
            values.Retire entry

        if
            not (isNull log)
            && (isNull (box lastHidden)
                || not (lastHidden.Contains entry.Key))
        then
            log.Record (entry.Key, KeyChange.Removed)

        if entry.Watched then
            entry.Watched <- false
            (entry.Row :> ISource).RemoveObserver watch
            inFlight.Remove entry.Key
            this.MoveSummary ()

        entry.Row.NotifyRemoved ()

        if isNull (box entry.Scope) then
            entry.Row.Dispose ()
        else
            entry.Scope.Dispose ()

    /// <summary>The reader the pass in progress leaves unmarked.</summary>
    member private _.Running =
        if isNull (box puller) then
            graph.CurrentComputation
        else
            puller

    /// <summary>Retires a row whose upstream identity changed during snapshot recovery.</summary>
    member internal this.RetireChanged(key: 'K) =
        let entry = entries.Find key

        if not (isNull entry) then
            graph.RunUntracked (fun () ->
                graph.RunBatch (fun () ->
                    inCleanup <- true

                    try
                        entries.Remove key
                        this.Retire entry
                    finally
                        inCleanup <- false))

    member private this.Run() =
        sources.BeginRun ()
        runs <- runs + 1
        Tracer.RunStart (graph, id, runs)
        freshness <- Freshness.Clean

        passKeys.Clear ()
        seen.Clear ()

        if not (isNull (box hidden)) then
            hidden.Clear ()

        this.Pass.ClearStaged ()
        removed.Clear ()
        let previousStatus = status
        let previousFailure = failure
        let retry = not invalidated
        invalidated <- false
        checkPending <- false
        failure <- null
        status <- Status.None
        let mutable delta: ProjectionDelta<'K> = Unchecked.defaultof<_>

        // The guard covers the diff as well as `Enumerate`: a failure in either
        // leaves the projection `Dirty`, and the next pass diffs against
        // whatever the failed one applied.
        try
            try
                graph.RunHosted (
                    this :> IComputation,
                    fun () ->
                        if not (isNull (box this.ReadDelta)) then
                            delta <- this.ReadDelta ()

                        if
                            isNull (box delta)
                            || delta.IsReset
                            || previousStatus <> Status.None
                        then
                            delta <- Unchecked.defaultof<_>
                            this.Pass.Enumerate ()
                )
            finally
                if disposed then
                    sources.Clear (this :> IComputation)
                else
                    sources.EndRun (this :> IComputation)

                beaconsStale <- true

            if not disposed then
                graph.RunUntracked (fun () ->
                    graph.RunBatch (fun () ->
                        if isNull (box delta) then
                            this.ApplyDiff ()
                        else
                            this.ApplyDelta delta))

                // Readers parked on a pending or failed pass wake when it
                // resolves, whether or not any row moved.
                if previousStatus <> Status.None then
                    Tracer.Moved (graph, id, null)
                    beacon.NotifyFailure this.Running
                    Tracer.Notified graph
                    Tracer.RunEnd (graph, id, status)
                else
                    Tracer.RunEnd (graph, id, status)
            else
                Tracer.RunEnd (graph, id, status)
        with
        | NotReadyException _ ->
            // Membership is unknown until the awaited source settles. The
            // reader that pulled links that source itself (see `linkAwaited`);
            // the beacon wakes every other reader on the transition into
            // pending.
            this.Pass.ClearStaged ()
            this.Abandon ()
            status <- Status.Pending

            if not (previousStatus.HasFlag Status.Pending) then
                Tracer.Moved (graph, id, null)
                beacon.NotifyFailure this.Running
                Tracer.Notified graph

            Tracer.RunEnd (graph, id, status)
            reraise ()
        | ex ->
            // The beacon wakes other readers on the transition into Error,
            // and on a new exception after a source change. Each reader's pull
            // re-runs a failed pass, so waking on a retry would bounce between
            // two readers indefinitely.
            let recorded = graph.FailureOf (ex, this, previousFailure)
            this.Pass.ClearStaged ()
            this.Abandon ()
            failure <- recorded
            status <- Status.Error

            if
                not (previousStatus.HasFlag Status.Error)
                || (not retry
                    && Failure.Moved (recorded, previousFailure))
            then
                Tracer.Moved (graph, id, null)
                beacon.NotifyFailure this.Running
                Tracer.Notified graph

            Tracer.RunEnd (graph, id, status)
            // The reader that pulled the pass catches the exception next.
            graph.Raised recorded
            reraise ()

    /// <summary>
    /// Leaves the projection <c>Dirty</c> after an unfinished pass. A projection
    /// whose beacon has a reader becomes eager, so a write to any of its
    /// sources schedules the retry.
    /// </summary>
    member private _.Abandon() =
        freshness <- Freshness.Dirty
        deferredDirty <- false

        if beacon.ObserverCount > 0 then
            observed <- true

    /// <summary>
    /// Disposes removed keys, creates added rows, writes changed items and
    /// publishes the key set. Runs inside one batch, and stops after the
    /// removals if a removed key's cleanup disposes the projection.
    /// </summary>
    member private this.ApplyDiff() =
        entries.Iterate (fun key _ ->
            if not (seen.Contains key) then
                removed.Add key)

        if removed.Count > 0 then
            inCleanup <- true

            try
                for key in removed do
                    let entry = entries.Find key

                    if not (isNull entry) then
                        entries.Remove key
                        this.Retire entry
            finally
                inCleanup <- false

        if not disposed then
            this.ApplyAdditions ()

    /// <summary>
    /// The diff after its removals.
    /// </summary>
    member private this.ApplyAdditions() =
        this.Pass.CreateAdded ()

        if not (isNull (box hidden)) then
            this.SettleHidden ()

        this.Pass.CommitWrites ()
        this.Pass.ClearStaged ()
        publishKeys ()

        this.ObservePass ()

    member private this.ApplyDelta(delta: ProjectionDelta<'K>) =
        inCleanup <- true

        try
            for change in delta.Changes do
                if
                    change.Value = KeyChange.Removed
                    || change.Value = KeyChange.Replaced
                then
                    let entry = entries.Find change.Key

                    if not (isNull entry) then
                        entries.Remove change.Key
                        this.Retire entry
        finally
            inCleanup <- false

        if not disposed then
            for change in delta.Changes do
                if
                    change.Value = KeyChange.Added
                    || change.Value = KeyChange.Replaced
                then
                    this.VisitDelta change.Key

            this.Pass.CreateAdded ()
            this.Pass.CommitWrites ()
            this.Pass.ClearStaged ()

            if not (obj.ReferenceEquals (keys.Peek, delta.Keys)) then
                keys.WriteExcept (delta.Keys, puller)

            this.ObservePass ()

    member private _.ObservePass() =

        observed <-
            keys.ObserverCount > 0
            || anyPending.ObserverCount > 0
            || inFlightVersion.ObserverCount > 0

        if
            not observed
            && not (isNull (box this.ExtraObserved))
        then
            observed <- this.ExtraObserved ()

        if not observed then
            observed <- entries.Exists (fun entry -> rowObserved entry)

        if deferredDirty then
            deferredDirty <- false
            (this :> IComputation).MarkDirty()

    member private this.ResolveCheck() =
        Tracer.CheckStart (graph, id)
        let mutable i = 0

        while freshness = Freshness.Check && i < sources.Count do
            sources.SourceAt(i).UpdateIfNecessary()
            i <- i + 1

        Tracer.CheckResolved (graph, id, (freshness = Freshness.Dirty))

        if freshness = Freshness.Check then
            freshness <- Freshness.Clean

    /// <summary>
    /// Brings the sources of a failed projection current, so a source that
    /// moved marks the projection dirty before the retry.
    /// </summary>
    member private _.ResolveFailedCheck() =
        Tracer.CheckStart (graph, id)
        let mutable i = 0

        while not invalidated && i < sources.Count do
            sources.SourceAt(i).UpdateIfNecessary()
            i <- i + 1

        Tracer.CheckResolved (graph, id, invalidated)

    member internal this.EnsureCurrent() =
        if freshness <> Freshness.Clean && not disposed then
            graph.EnterPull ()

            try
                if freshness = Freshness.Check then
                    this.ResolveCheck ()
                elif
                    checkPending
                    && not invalidated
                    && status = Status.Error
                then
                    this.ResolveFailedCheck ()

                if freshness = Freshness.Dirty then
                    if isNull (box passScope) then
                        this.Run ()
                    else
                        // A pass run by a cleanup's read replaces this one,
                        // and a write after that read starts over, as on
                        // `Memo.Recompute`.
                        let before = runs
                        freshness <- Freshness.Clean
                        graph.Discharge passScope

                        if not disposed then
                            if runs = before then
                                this.Run ()
                            elif freshness = Freshness.Dirty then
                                this.EnsureCurrent ()
            finally
                graph.ExitPull ()

    /// <summary><c>EnsureCurrent</c> for <c>reader</c>, which reads the result of the pass in its current run.</summary>
    member private this.PullFor(reader: IComputation) =
        let outer = puller
        puller <- reader

        try
            this.EnsureCurrent ()
        finally
            puller <- outer

    interface IScopeHost with
        member _.Scope =
            if isNull (box passScope) then
                passScope <- new Owner (graph.Root)
                Tracer.ScopeNew (passScope, graph, id)

                if disposed then
                    passScope.Dispose ()

            passScope

    interface IBeaconHost with
        member this.Refresh() =
            if status = Status.None && not disposed then
                try
                    // A projection stays `Clean` until an upstream projection
                    // republishes, so the upstream passes run first. Every other
                    // source marks the projection when it goes stale.
                    if beaconsStale then
                        beaconsStale <- false
                        upstreamBeacons.Clear ()

                        for i in 0 .. sources.Count - 1 do
                            match sources.SourceAt i with
                            | :? ProjectionBeacon as source -> upstreamBeacons.Add source
                            | _ -> ()

                    Tracer.Walk (graph, id)
                    let mutable i = 0

                    while freshness = Freshness.Clean
                          && i < upstreamBeacons.Count do
                        upstreamBeacons[i].UpdateIfNecessary()
                        i <- i + 1

                    Tracer.Walked (graph, id)
                    this.EnsureCurrent ()
                with _ ->
                    ()

            this.RefreshSummary ()

    interface INode with
        member _.Id = id
        member _.Status = status

    interface IComputation with
        member _.AddSource s =
            sources.Add (this :> IComputation, s)

        member this.MarkDirty() =
            if not disposed then
                if inCleanup then
                    deferredDirty <- true
                else
                    invalidated <- true
                    freshness <- Freshness.Dirty

                    if observed && not queued then
                        queued <- true
                        graph.Schedule (this :> IScheduled)

        member this.MarkCheck() =
            if not disposed then
                if inCleanup then
                    deferredDirty <- true
                else
                    checkPending <- true

                    if freshness = Freshness.Clean then
                        freshness <- Freshness.Check

                    if observed && not queued then
                        queued <- true
                        graph.Schedule (this :> IScheduled)

    interface IScheduled with
        member this.Execute() =
            queued <- false

            if not disposed then
                try
                    this.EnsureCurrent ()
                with _ ->
                    // `Graph.Flush` has no handler of its own, and one failing
                    // pass must not strand the items queued behind it. `Run`
                    // already recorded the failure in `Error` and `Status` and
                    // woke the beacon; the next pull retries the pass and
                    // raises there.
                    ()

    /// <summary>
    /// Links the reader to the row of <c>key</c>, if the key is live. Called when
    /// the pass raises, so the reader keeps an edge to the row it asked for.
    /// </summary>
    member private _.TrackRow(key: 'K) =
        let existing = entries.Find key

        if not (isNull existing) then
            graph.Track (existing.Row :> ISource)

    /// <summary>
    /// Links the reader of an absent key to the keys cell, so it wakes when
    /// membership changes.
    /// </summary>
    member private _.TrackAbsent() =
        graph.Track (keys :> ISource)

        if not observed && keys.ObserverCount > 0 then
            observed <- true

    /// <summary>
    /// A tracked read of a row. A pending row raises <c>NotReadyException</c>
    /// naming the source its reader awaits, and links the caller to that
    /// source as well as to the row.
    /// </summary>
    member private this.ReadRow(entry: RowEntry<'K, 'V>) : 'V =
        try
            try
                entry.Row.Value
            with NotReadyException _ ->
                let mutable awaited = entry.Row :> INode
                let mutable e = entry.Row.PendingSources.GetEnumerator ()

                if e.MoveNext () then
                    awaited <- e.Current

                match Awaited.source awaited with
                | ValueSome source -> graph.Track source
                | ValueNone -> ()

                raise (graph.NotReady awaited)
        finally
            if not observed && rowObserved entry then
                observed <- true

    /// <summary>
    /// The key set, in source order. A tracked read of the keys cell.
    /// </summary>
    member this.Keys: 'K[] =
        graph.Track (beacon :> ISource)

        try
            this.PullFor graph.CurrentComputation
        with ex ->
            // The pass ran inside this reader's tracked call, so the reader's
            // `EndRun` prunes any edge the throw skipped. A reader that ends
            // with no sources never wakes again. The edge is linked on the
            // failing path only: on success the keys write below it would
            // mark the reader dirty over the value it is about to read.
            graph.Track (keys :> ISource)
            linkAwaited ex
            reraise ()

        let result = keys.Value

        if not observed && keys.ObserverCount > 0 then
            observed <- true

        result

    /// <summary>
    /// How many keys are live. Tracked, through the keys cell.
    /// </summary>
    member this.Count: int = this.Keys.Length

    /// <summary>
    /// The value of the row at <c>key</c>, as a tracked read of that row, or of the
    /// key set when <c>key</c> is absent. Computes the row if it is stale.
    /// </summary>
    /// <remarks>
    /// A pending row raises <c>NotReadyException</c>; a failed row re-raises its
    /// reader's exception. Raises <c>KeyNotFoundException</c> for an absent key.
    /// </remarks>
    member this.Get(key: 'K) : 'V =
        this.GetRow (key, false)

    /// <summary>
    /// <c>Get</c>, returning the last settled value of a pending row that has settled before. A failed row raises as in
    /// <c>Get</c>.
    /// </summary>
    member internal this.GetSettled(key: 'K) : 'V =
        this.GetRow (key, true)

    /// <summary>
    /// Brings the pass current, then reads <c>cell</c> tracked. A read raises what the pass raised, as <c>Keys</c> does.
    /// </summary>
    member internal this.ReadAfterPass(cell: Signal<'T>) : 'T =
        graph.Track (beacon :> ISource)

        try
            this.EnsureCurrent ()
        with ex ->
            // See `Keys`.
            graph.Track (cell :> ISource)
            linkAwaited ex
            reraise ()

        let result = cell.Value

        if not observed && cell.ObserverCount > 0 then
            observed <- true

        result

    member private this.GetRow(key: 'K, keepSettled: bool) : 'V =
        if disposed && this.GetRaisesDisposed then
            raise (ObjectDisposedException "The projection was disposed.")

        graph.Track (beacon :> ISource)

        try
            this.EnsureCurrent ()
        with ex ->
            // See `Keys`.
            this.TrackRow key
            linkAwaited ex
            reraise ()

        let entry = entries.Find key

        if isNull entry then
            this.TrackAbsent ()
            raise (KeyNotFoundException ("The projection has no key " + string key + "."))

        if keepSettled && entry.Settled then
            match entry.Row.TryValue with
            | Pending ->
                if not observed && rowObserved entry then
                    observed <- true

                entry.Row.Peek
            | _ -> this.ReadRow entry
        else
            this.ReadRow entry

    /// <summary>
    /// <c>Get</c>, with <c>None</c> for an absent key. A pending or failed row raises as
    /// <c>Get</c> does.
    /// </summary>
    member this.TryGet(key: 'K) : 'V option =
        graph.Track (beacon :> ISource)

        try
            this.EnsureCurrent ()
        with ex ->
            // See `Keys`.
            this.TrackRow key
            linkAwaited ex
            reraise ()

        let entry = entries.Find key

        if isNull entry then
            this.TrackAbsent ()
            None
        else
            Some (this.ReadRow entry)

#if !FABLE_COMPILER
    /// <summary>
    /// <c>TryGet</c> in the <c>TryGetValue</c> shape: true with the value when <c>TryGet</c> returns one.
    /// </summary>
    member this.TryGetValue(key: 'K, [<System.Runtime.InteropServices.Out>] value: byref<'V>) : bool =
        match this.TryGet key with
        | Some found ->
            value <- found
            true
        | None ->
            value <- Unchecked.defaultof<'V>
            false
#endif

    /// <summary>
    /// Brings the projection and its pending rows current, then reads <c>summary</c> tracked. <c>publish</c> writes
    /// <c>summary</c>, leaving the reader unmarked.
    /// </summary>
    member private this.PullSummary(summary: Signal<'T>, publish: IComputation -> unit) : 'T =
        graph.Track (beacon :> ISource)

        try
            this.EnsureCurrent ()
        with ex ->
            // See `Keys`.
            graph.Track (summary :> ISource)
            linkAwaited ex
            reraise ()

        let reader = graph.CurrentComputation
        this.RefreshRows ()
        publish reader
        let result = summary.Value

        if not observed && summary.ObserverCount > 0 then
            observed <- true

        result

    /// <summary>Whether any computed row is pending. An unread row is not counted.</summary>
    /// <remarks>
    /// Tracked, and wakes its reader only when the answer changes. O(1) while no row is pending, and a pass over the
    /// pending rows otherwise. A row the reader's own run makes pending shows only in the run's later reads. A read
    /// raises what the pass raised, as <c>Keys</c> does.
    /// </remarks>
    member this.AnyPending: bool = this.PullSummary (anyPending, this.PublishAny)

    /// <summary>
    /// The computed rows that are pending, in key order, followed by any pending keys a derived view holds outside
    /// <c>Keys</c>.
    /// </summary>
    /// <remarks>
    /// Tracked, and wakes its reader when the set of pending rows changes, and while two or more rows are pending, when
    /// the key order changes. O(N) per read while any row is pending, against a pass over the pending rows for
    /// <c>AnyPending</c>. A read raises what the pass raised, as <c>Keys</c> does.
    /// </remarks>
    member this.PendingKeys: 'K[] =
        this.PullSummary (inFlightVersion, this.PublishVersion)
        |> ignore

        let own =
            match inFlight.Count with
            | 0 -> Array.empty
            | 1 -> keys.Peek |> Array.filter inFlight.Contains
            | _ -> keys.Value |> Array.filter inFlight.Contains

        if isNull (box this.PendingExtra) then
            own
        else
            let extra = this.PendingExtra ()

            if extra.Length = 0 then own
            elif own.Length = 0 then extra
            else Array.append own extra

    /// <summary>
    /// Pending keys absent from <c>Keys</c>, appended to <c>PendingKeys</c> once the pass is current, or null. Read in the
    /// caller's tracking context.
    /// </summary>
    member val internal PendingExtra: unit -> 'K[] = Unchecked.defaultof<unit -> 'K[]> with get, set

    /// <summary>
    /// Whether a derived view's own cell, read through <c>ReadAfterPass</c>, has a reader, or null. Keeps the projection
    /// observed across passes.
    /// </summary>
    member val internal ExtraObserved: unit -> bool = Unchecked.defaultof<unit -> bool> with get, set

    /// <summary>Whether <c>Get</c> on the disposed projection raises <c>ObjectDisposedException</c>.</summary>
    member val internal GetRaisesDisposed = false with get, set

    /// <summary>
    /// The rows with a settled value, in key order, read untracked. Computes
    /// every stale row.
    /// </summary>
    /// <remarks>
    /// A pending or failed row holds its last settled value, and a row that
    /// has never settled is absent. A read while the pass is suspended raises
    /// <c>NotReadyException</c>, as <c>Keys</c> does, and the reader wakes when the pass
    /// settles.
    /// </remarks>
    member this.Snapshot: IReadOnlyDictionary<'K, 'V> =
        let reader = graph.CurrentComputation

        try
            graph.RunUntracked (fun () -> this.PullFor reader)
        with ex ->
            // See `Keys`. Only the suspension edges are tracked.
            graph.Track (beacon :> ISource)
            linkAwaited ex
            reraise ()

        graph.RunUntracked (fun () ->
            let copy = RowSnapshot<'K, 'V>(entries.Count)
            Tracer.Walk (graph, id)

            for key in keys.Peek do
                let entry = entries.Find key

                if not (isNull entry) then
                    (entry.Row :> ISource).UpdateIfNecessary()

                    if entry.Settled then
                        copy.Add (key, entry.Row.Peek)

            Tracer.Walked (graph, id)
            copy :> IReadOnlyDictionary<'K, 'V>)

#if !FABLE_COMPILER
    /// <summary>
    /// A new <c>ObservableCollection</c> holding the rows' values in key order, kept current by an effect owned by the
    /// calling scope. Raises <c>InvalidOperationException</c> inside a pure body, as creating an effect does.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The first population raises one <c>Reset</c> and one <c>Add</c> per value. Each later change raises a <c>Remove</c> per
    /// departed row, an <c>Add</c> per new row, at most one <c>Move</c> per row outside the longest run of rows that kept their
    /// order, and a <c>Replace</c> per unequal value or replaced row. Value-only edits cost O(changed rows);
    /// membership, order and reset reconciliation cost O(N log N).
    /// </para>
    /// <para>
    /// Rows follow the rule <c>Snapshot</c> gives for pending and failed rows. While the pass is suspended, the collection
    /// keeps its last contents. The updates stop when the calling scope is disposed or re-runs, or when the projection is
    /// disposed.
    /// </para>
    /// </remarks>
    member this.AsObservableCollection() : ObservableCollection<'V> =
        if disposed then
            raise (ObjectDisposedException (this.GetType().Name))

        let view = ObservableCollection<'V>()
        let equal = graph.Options.Equality.Comparer<'V>()
        // The keys and values the view shows, and one more than each shown key's index.
        let shown = ref Array.empty<'K>
        let shownValues = ref Array.empty<'V>
        let shownIndex = ref (Platform.KeyMap<'K, int>())
        let nextIndex = ref (Platform.KeyMap<'K, int>())
        let populated = ref false

        let reader = this.NewValueReader ()

        let applyValues (delta: ProjectionDelta<'K>) =
            let mutable reconcile =
                not populated.Value
                || delta.IsReset
                || delta.OrderChanged

            if not reconcile then
                try
                    for change in delta.Changes do
                        let position = shownIndex.Value.Find change.Key
                        let accepted = values.TryAccepted change.Key

                        match accepted with
                        | ValueSome value when position > 0 ->
                            if
                                change.Value = KeyChange.Replaced
                                || not (equal.Equals (shownValues.Value[position - 1], value))
                            then
                                view[position - 1] <- value
                                shownValues.Value[position - 1] <- value
                        | ValueNone when position = 0 -> ()
                        | _ -> reconcile <- true
                with _ ->
                    populated.Value <- false
                    reraise ()

            reconcile

        Effect.Create (
            graph,
            fun () ->
                if not disposed then
                    let delta = reader.Read ()

                    if applyValues delta then
                        let current = delta.Keys
                        let replaced = Platform.KeySet<'K>()

                        for change in delta.Changes do
                            if change.Value = KeyChange.Replaced then
                                replaced.Add change.Key |> ignore

                        let visible = ResizeArray<'K>(current.Length)
                        let nextValues = ResizeArray<'V>(current.Length)

                        for key in current do
                            match values.TryAccepted key with
                            | ValueSome value ->
                                visible.Add key
                                nextValues.Add value
                            | ValueNone -> ()

                        let visible = visible.ToArray ()
                        let values = nextValues.ToArray ()
                        let positions = nextIndex.Value
                        positions.Clear ()

                        for i in 0 .. visible.Length - 1 do
                            positions.Set (visible[i], i + 1)

                        try
                            if not populated.Value then
                                populated.Value <- true
                                view.Clear ()

                                for v in values do
                                    view.Add v
                            else
                                for edit in Positional.diff shown.Value visible do
                                    match edit with
                                    | PositionalChange.RemoveAt index -> view.RemoveAt index
                                    | PositionalChange.InsertAt (index, key) -> view.Insert (index, values[positions.Find key - 1])
                                    | PositionalChange.Move (oldIndex, newIndex) -> view.Move (oldIndex, newIndex)

                                for i in 0 .. visible.Length - 1 do
                                    let previous = shownIndex.Value.Find visible[i]

                                    if
                                        previous > 0
                                        && (replaced.Contains visible[i]
                                            || not (equal.Equals (shownValues.Value[previous - 1], values[i])))
                                    then
                                        view[i] <- values[i]
                        with _ ->
                            // A view left partway through the edits is rebuilt on the next run.
                            populated.Value <- false
                            reraise ()

                        shown.Value <- visible
                        shownValues.Value <- values
                        nextIndex.Value <- shownIndex.Value
                        shownIndex.Value <- positions
        )
        |> ignore

        view

#endif
    /// <summary>
    /// How many passes the projection has run.
    /// </summary>
    member _.Runs = runs

    /// <summary>
    /// The exception from the last pass, or null. Describes the pass alone:
    /// the source, <c>keyOf</c> and duplicate keys. A failed row raises its own
    /// exception from <c>Get</c>.
    /// </summary>
    /// <remarks>
    /// A pass the scheduler runs has no reader to raise to, so its failure is
    /// visible here, as with <c>Effect.Error</c>.
    /// </remarks>
    member _.Error = Failure.ErrorOf failure

    /// <summary>The node the last pass's failure originated in, or null when the pass did not fail.</summary>
    /// <remarks>
    /// The projection itself when its source, <c>keyOf</c> or a duplicate key raised the exception; the upstream node when
    /// the source rethrew the exception of a failed read. A failed row reports its origin through the reader of the row:
    /// the projection when its reader or factory raised the exception.
    /// </remarks>
    member _.ErrorOrigin: INode = Failure.OriginOf failure

    /// <summary>
    /// <c>Pending</c> if the last pass suspended, <c>Error</c> if it failed, <c>None</c>
    /// otherwise. Describes the pass alone; <c>AnyPending</c> reports rows.
    /// </summary>
    member _.Status = status

    /// <summary>
    /// Disposes every row with the nodes its factory created, and the
    /// projection's own edges. A disposed projection reads as empty, and a
    /// reader of <c>Keys</c> wakes to the empty key set.
    /// </summary>
    member _.Dispose() =
        if not disposed then
            disposed <- true
            Tracer.NodeDispose (graph, id)

            if not (isNull link) then
                link.Detach ()
                link <- null

            sources.Clear (this :> IComputation)

            entries.Iterate (fun _ entry -> entry.Live <- false)

            entries.Clear ()
            inFlight.Clear ()

            if not (isNull (box hidden)) then
                hidden.Clear ()
                lastHidden.Clear ()

            if not (isNull log) then
                log.Reset ()

            if not (isNull (box values)) then
                values.Dispose ()
                values <- Unchecked.defaultof<_>

            scope.Dispose ()

            if not (isNull (box passScope)) then
                graph.Retire passScope

            if keys.Peek.Length > 0 then
                graph.RunBatch (fun () -> keys.Value <- Array.empty)

    /// <summary>
    /// A new reader of the projection's membership and order, owned by the calling scope. Its first <c>Read</c> reports a
    /// reset; each later one reports the keys added, removed or replaced since the previous read.
    /// </summary>
    /// <remarks>
    /// Each addition or removal costs one map update per live reader. A disposed projection gives a reader whose first read
    /// reports a reset with empty <c>Keys</c>.
    /// </remarks>
    member this.NewKeyReader() : ProjectionReader<'K> =
        let reader = new ProjectionReader<'K> (this)

        if not disposed then
            if isNull log then
                log <- KeyLog<'K>()

            log.Add reader

        reader.Link <- graph.CurrentOwner.AttachLinked reader
        reader

    /// <summary>A reader of membership, order and settled row-value changes, owned by the calling scope.</summary>
    /// <remarks>
    /// Observes visible rows while it lives. Pending and failed rows retain their last accepted value; their status changes
    /// alone do not report <c>Changed</c>. The first read and overflow report a reset, as <c>NewKeyReader</c> does.
    /// </remarks>
    member this.NewValueReader() : ProjectionReader<'K> =
        if not disposed && isNull (box values) then
            values <-
                ProjectionValues (
                    graph,
                    entries.Find,
                    (fun () -> this.Keys),
                    (fun key ->
                        if not (isNull log) then
                            log.RecordValue key)
                )

        let host =
            { new IKeyLogHost<'K> with
                member _.ReadKeys() =
                    let current = this.Keys

                    if not (isNull (box values)) then
                        values.Read current

                    current

                member _.LiveCount = entries.Count

                member _.Detach reader =
                    this.DetachReader reader
            }

        let reader = new ProjectionReader<'K> (host, values = true)

        if not disposed then
            if isNull log then
                log <- KeyLog<'K>()

            log.Add reader

        reader.Link <- graph.CurrentOwner.AttachLinked reader
        reader

    member private _.DetachReader(reader: ProjectionReader<'K>) =
        if not (isNull log) then
            log.Remove reader

            if
                reader.ReadsValues
                && log.ValueCount = 0
                && not (isNull (box values))
            then
                values.Dispose ()
                values <- Unchecked.defaultof<_>

            if log.Count = 0 then
                log <- null

    interface IKeyLogHost<'K> with
        member this.ReadKeys() = this.Keys
        member _.LiveCount = entries.Count

        member _.Detach reader =
            this.DetachReader reader

    interface IDisposable with
        member this.Dispose() =
            this.Dispose ()

    interface IOwned with
        member this.Release() =
            link <- null
            this.Dispose ()

/// <summary>
/// The rows of a projection over items of type <c>'T</c>: one item source per key,
/// written when the key's item changes.
/// </summary>
/// <remarks>
/// Exactly one of <c>map</c> and <c>factory</c> is non-null. With <c>map</c>, the reader is
/// <c>map</c> over the item and the row owns no scope. With <c>factory</c>, the factory
/// runs once per key inside the key's scope and returns the reader.
/// </remarks>
[<AbstractClass>]
type internal RowsOf<'T, 'K, 'V when 'K: equality>(graph: Graph, map: 'T -> 'V, factory: (unit -> 'T) -> (unit -> 'V)) =
    inherit Projection<'K, 'V>(graph)

    let valueForm = not (isNull (box map))

    let rowMode =
        if valueForm then
            ScopeMode.ValueRow
        else
            ScopeMode.FactoryRow

    let adds = ResizeArray<struct ('K * 'T)>()
    let writes = ResizeArray<struct (Signal<'T> * 'T)>()

    /// <summary>
    /// Stages one item of the pass: a write to a survivor's item source, or a
    /// row to create.
    /// </summary>
    member internal this.Visit(key: 'K, item: 'T) =
        if not (this.Seen.Add key) then
            // Two items, one key: one of them would silently disappear. The
            // throw fails the pass, and reaches the boundary around the read.
            raise (
                InvalidOperationException (
                    "The projection produced the key "
                    + string key
                    + " twice in one pass. Keys must be unique; check the keyOf function."
                )
            )

        this.PassKeys.Add key

        let entry = this.Entries.Find key

        if isNull entry then
            adds.Add (struct (key, item))
        else
            writes.Add (struct ((entry :?> ItemRow<'T, 'K, 'V>).Item, item))

    member private this.NewRow(key: 'K, item: 'T) =
        let entry = ItemRow<'T, 'K, 'V>(key, Signal<'T>(graph, item))
        Tracer.Part (graph, (entry.Item :> INode).Id, (this :> INode).Id, box key)
        this.AddEntry (key, entry)
        entry

    member private this.Compute(entry: RowEntry<'K, 'V>) : 'V voption -> 'V =
        fun _ -> this.RunRow entry

    member private this.CreateMapped() =
        for struct (key, item) in adds do
            let entry = this.NewRow (key, item)
            let source = entry.Item
            entry.Reader <- fun () -> map source.Value
            entry.Row <- Memo<'V>.Create(graph, this.Compute entry, rowMode)
            Tracer.Part (graph, (entry.Row :> INode).Id, (this :> INode).Id, box key)

    member private this.CreateFactored(key: 'K, item: 'T) =
        let entry = this.NewRow (key, item)
        let source = entry.Item
        let keyScope = new Owner (graph.Root)
        keyScope.SetParent (this.Scope.AttachLinked keyScope)
        entry.Scope <- keyScope
        Tracer.PartScope (graph, keyScope, (this :> INode).Id, box key)

        graph.RunOwned (
            keyScope,
            fun () ->
                entry.Reader <-
                    try
                        factory (fun () -> source.Value)
                    with
                    | NotReadyException _ as ex ->
                        let message =
                            "The projection's factory for key "
                            + string key
                            + " read a pending source. The factory runs once per key, untracked, and cannot wait for a source to settle. Read the source inside the reader the factory returns."

                        let failure =
#if FABLE_COMPILER
                            let failure = InvalidOperationException message
                            Platform.setInner failure ex
                            failure
#else
                            InvalidOperationException (message, ex)
#endif

                        fun () -> raise failure
                    | ex ->
                        let captured = Platform.captureFailure null ex
                        fun () -> Platform.rethrowStored captured

                entry.Row <- Memo<'V>.Create(graph, this.Compute entry, rowMode)
                Tracer.Part (graph, (entry.Row :> INode).Id, (this :> INode).Id, box key)
        )

    abstract Enumerate: unit -> unit

    interface IProjectionPass with
        member this.Enumerate() =
            this.Enumerate ()

        member this.CreateAdded() =
            this.CreateAdded ()

        member _.CommitWrites() =
            for struct (source, item) in writes do
                source.Value <- item

        member _.ClearStaged() =
            adds.Clear ()
            writes.Clear ()

    member private this.CreateAdded() =
        if adds.Count > 0 then
            if valueForm then
                graph.RunOwned (this.Scope, this.CreateMapped)
            else
                for struct (key, item) in adds do
                    this.CreateFactored (key, item)

/// <summary>
/// A projection keyed by a function of the item.
/// </summary>
type internal KeyedProjection<'T, 'K, 'V when 'K: equality>
    (graph: Graph, keyOf: 'T -> 'K, map: 'T -> 'V, factory: (unit -> 'T) -> (unit -> 'V), source: unit -> 'T seq) =
    inherit RowsOf<'T, 'K, 'V>(graph, map, factory)

    override this.Enumerate() =
        for item in source () do
            this.Visit (keyOf item, item)

/// <summary>
/// A projection keyed by position: Solid's <c>indexArray</c>. The row at a slot
/// survives its item changing, and a reorder writes the item source of every
/// slot whose item moved.
/// </summary>
type internal IndexProjection<'T, 'V>(graph: Graph, map: 'T -> 'V, factory: (unit -> 'T) -> (unit -> 'V), source: unit -> 'T seq) =
    inherit RowsOf<'T, int, 'V>(graph, map, factory)

    override this.Enumerate() =
        let mutable i = 0

        for item in source () do
            this.Visit (i, item)
            i <- i + 1

/// <summary>
/// A lookup's source-facing steps, implemented by its subclass.
/// </summary>
type internal ILookupSource<'K, 'V> =
    /// <summary>
    /// The value at <c>key</c> against the current source. Always called untracked.
    /// </summary>
    abstract Compute: key: 'K -> 'V

    /// <summary>
    /// Brings the lookup's state up to date with the source, recomputing the
    /// cells a change affects. Called at the start of every read.
    /// </summary>
    abstract Refresh: unit -> unit

/// <summary>
/// One live key of a <c>Lookup</c>: a value, the exception computing it raised, or
/// a suspension while its source is pending.
/// </summary>
/// <remarks>
/// <para>
/// A failed cell raises its exception on read; a suspended cell raises
/// <c>NotReadyException</c>. A write that clears a failure or a suspension notifies
/// readers even when the value compares equal.
/// </para>
/// <para>
/// Writes notify observers and request no flush: a lookup writes cells only
/// while a flush is pending or running, and that flush runs the readers.
/// </para>
/// <para>
/// <c>orphaned</c> runs each time the cell loses its last observer.
/// </para>
/// </remarks>
[<Sealed; NoEquality; NoComparison>]
type internal LookupCell<'V>(graph: Graph, equal: IEqualityComparer<'V>, orphaned: unit -> unit) =
    let id = graph.NextId ()
    let observers = ObserverSet ()
    do Tracer.Bind (observers, graph, id)
    do Tracer.LookupCellNew (graph, id)
    let mutable value = Unchecked.defaultof<'V>
    let mutable failure: Failure = null
    let mutable pending = false

    interface INode with
        member _.Id = id

        member _.Status =
            if pending then Status.Pending
            elif not (isNull failure) then Status.Error
            else Status.None

    interface ISource with
        member _.AddObserver c =
            observers.Add c

        member _.RemoveObserver c =
            observers.Remove c

            if observers.Count = 0 then
                orphaned ()

        member _.UpdateIfNecessary() = ()

    /// <summary>
    /// Tracked read. Raises <c>NotReadyException</c> if the cell is suspended, and
    /// the stored exception if the cell has failed.
    /// </summary>
    member this.Read() : 'V =
        graph.Track (this :> ISource)

        if pending then
            raise (graph.NotReady (this :> INode))

        if not (isNull failure) then
            graph.Raise failure

        value

    /// <summary>
    /// Stores <c>v</c>, notifying readers when it differs from the stored value. Returns false when the comparer throws:
    /// the cell then fails with the comparer's exception and keeps its value.
    /// </summary>
    member this.Write(v: 'V) : bool =
        let mutable moved = pending || not (isNull failure)
        let mutable comparerError: exn = null

        if not moved then
            try
                moved <- not (equal.Equals (value, v))
            with ex ->
                comparerError <- ex

        if not (isNull comparerError) then
            this.Fail (Failure (comparerError, (this :> INode)))
            false
        else
            if moved then
                pending <- false
                failure <- null
                value <- v
                Tracer.Moved (graph, id, box v)
                observers.NotifyDirty ()
                Tracer.Notified graph

            true

    member _.Fail(recorded: Failure) =
        pending <- false
        failure <- recorded
        Tracer.Moved (graph, id, Failure.Payload (recorded, null))
        observers.NotifyDirty ()
        Tracer.Notified graph

    /// <summary>
    /// Marks the cell as waiting on its source. Readers are notified on the
    /// transition into the suspended state only.
    /// </summary>
    member _.Suspend() =
        if not pending then
            pending <- true
            failure <- null
            Tracer.Moved (graph, id, null)
            observers.NotifyDirty ()
            Tracer.Notified graph

    member _.ObserverCount = observers.Count

/// <summary>
/// A pointwise derived collection over an open key domain.
/// </summary>
/// <remarks>
/// <para>
/// A source change costs O(affected): only live cells among the keys returned
/// by <c>affected prev next</c> are recomputed. For a selector that set is <c>{prev, next}</c>.
/// </para>
/// <para>
/// Cells exist only for keys that have been read through <c>Get</c>, so the lookup
/// exposes a cell count rather than a key set. An unobserved cell is evicted
/// at the lookup's next <c>Get</c> or transition.
/// </para>
/// <para>
/// A key whose computation throws holds the exception: a read of the key
/// raises it, and the key is recomputed on every later source change until it
/// succeeds. A source that throws fails every live cell the same way. A
/// pending source suspends every live cell: a read raises <c>NotReadyException</c>
/// until the source settles.
/// </para>
/// <para>
/// A key's computation is pure: a key whose computation creates an owned node
/// fails with <c>InvalidOperationException</c>.
/// </para>
/// </remarks>
[<AbstractClass>]
type Lookup<'K, 'V when 'K: equality> internal (graph: Graph) as this =
    let scope = new Owner (graph.Root)
    let equal = graph.Options.Equality.Comparer<'V>()
    let cells = Platform.KeyMap<'K, LookupCell<'V>>()

    /// <summary>
    /// Keys whose cell holds an exception or is suspended.
    /// </summary>
    let failed = Platform.KeySet<'K>()

    /// <summary>
    /// Keys whose cell has lost its last observer since the last eviction.
    /// </summary>
    let orphans = Platform.KeySet<'K>()

    let mutable disposed = false
    let mutable link: OwnerLink = null

    /// <summary>
    /// Set when the pure body in progress creates an owned node.
    /// </summary>
    let mutable violated = false

    /// <summary>
    /// The message the pure body in progress fails with.
    /// </summary>
    let mutable rule = ScopeMessages.lookup

#if RANVIER_TRACE
    let mutable traceHost = 0
#endif

    do link <- graph.CurrentOwner.AttachLinked this
    do Tracer.OwnerAdopt (link.Owner, scope, false)

    member private this.Source = box this :?> ILookupSource<'K, 'V>

    member internal _.Graph = graph
    member internal _.Scope = scope
    member internal _.IsDisposed = disposed

#if RANVIER_TRACE
    /// <summary>The node id the lookup's cells are recorded as parts of.</summary>
    member internal _.TraceHost
        with get () = traceHost
        and set value = traceHost <- value
#endif

    member private _.NewCell(key: 'K) =
        let cell =
            LookupCell<'V>(
                graph,
                equal,
                (fun () ->
                    if not disposed then
                        orphans.Add key |> ignore)
            )

#if RANVIER_TRACE
        Tracer.Part (graph, (cell :> INode).Id, traceHost, box key)
#endif
        cell

    /// <summary>
    /// Removes the cells of orphaned keys that are still unobserved.
    /// </summary>
    member private _.Evict() =
        if orphans.Count > 0 then
            orphans.Iterate (fun key ->
                let cell = cells.Find key

                if not (isNull (box cell)) && cell.ObserverCount = 0 then
                    cells.Remove key
                    failed.Remove key)

            orphans.Clear ()

    /// <summary>
    /// <c>Evict</c>, keeping the cell of <c>kept</c>. An orphaned <c>kept</c> stays orphaned,
    /// so the next eviction removes it if it is still unobserved.
    /// </summary>
    member private this.EvictExcept(kept: 'K) =
        if orphans.Count > 0 then
            let orphaned = orphans.Contains kept

            if orphaned then
                orphans.Remove kept

            this.Evict ()

            if orphaned then
                orphans.Add kept |> ignore

    /// <summary>
    /// Evaluates <c>body</c> untracked under the lookup's pure rule. Raises
    /// <c>InvalidOperationException</c> with <c>message</c> when <c>body</c> creates an owned
    /// node, even when <c>body</c> catches the exception. A nested call keeps its
    /// own verdict.
    /// </summary>
    member internal this.RunPure(message: string, body: unit -> 'T) : 'T =
        let outerViolated = violated
        let outerRule = rule
        violated <- false
        rule <- message

        try
            try
                let result = graph.RunUntrackedHosted (this :> IScopeHost, body)

                if violated then
                    raise (InvalidOperationException message)

                result
            with
            | ex when violated && ScopeMessages.isViolation message ex -> reraise ()
            | _ when violated -> raise (InvalidOperationException message)
        finally
            violated <- outerViolated
            rule <- outerRule

    member private this.Recompute(key: 'K, cell: LookupCell<'V>) =
        let mutable v = Unchecked.defaultof<'V>
        let mutable failure: exn = null

        let mutable suspended = false
        Tracer.RunStart (graph, (cell :> INode).Id, 0)

        try
            v <- this.RunPure (ScopeMessages.lookup, (fun () -> this.Source.Compute key))
        with
        | NotReadyException _ -> suspended <- true
        | ex -> failure <- ex

        if suspended then
            failed.Add key |> ignore
            cell.Suspend ()
            Tracer.RunEnd (graph, (cell :> INode).Id, (cell :> INode).Status)
        elif isNull failure then
            if cell.Write v then
                failed.Remove key
            else
                failed.Add key |> ignore

            Tracer.RunEnd (graph, (cell :> INode).Id, (cell :> INode).Status)
        else
            failed.Add key |> ignore
            cell.Fail (graph.FailureOf (failure, cell, null))
            Tracer.RunEnd (graph, (cell :> INode).Id, (cell :> INode).Status)

    /// <summary>
    /// Recomputes the live cells among <c>affectedKeys</c>, and every failed cell.
    /// Keys without a cell are skipped. A key that throws fails its own cell
    /// and the remaining keys are still recomputed.
    /// </summary>
    /// <remarks>
    /// Orphaned cells are evicted first. An unobserved cell among the keys is
    /// evicted instead of recomputed; its next <c>Get</c> builds a fresh cell
    /// against the current source.
    /// </remarks>
    member internal this.Invalidate(affectedKeys: 'K seq) =
        if not disposed then
            this.Evict ()

            if failed.Count = 0 then
                for key in affectedKeys do
                    this.Revisit key
            else
                let union = Platform.KeySet<'K>()
                failed.Iterate (fun key -> union.Add key |> ignore)

                for key in affectedKeys do
                    union.Add key |> ignore

                // Collected first: Fable miscompiles `Revisit` inlined into an `Iterate` callback.
                let keys = ResizeArray<'K>(union.Count)
                union.Iterate (fun key -> keys.Add key)

                for key in keys do
                    this.Revisit key

    /// <summary>
    /// Recomputes the cell of <c>key</c>, or evicts it when unobserved.
    /// </summary>
    member private this.Revisit(key: 'K) =
        let cell = cells.Find key

        if not (isNull (box cell)) then
            if cell.ObserverCount = 0 then
                cells.Remove key
                failed.Remove key
            else
                this.Recompute (key, cell)

    /// <summary>
    /// Fails every live cell with <c>recorded</c>.
    /// </summary>
    member internal _.FailAll(recorded: Failure) =
        if not disposed then
            cells.Iterate (fun key cell ->
                failed.Add key |> ignore
                cell.Fail recorded)

    /// <summary>
    /// Suspends every live cell until the next recompute.
    /// </summary>
    member internal _.SuspendAll() =
        if not disposed then
            cells.Iterate (fun key cell ->
                failed.Add key |> ignore
                cell.Suspend ())

    /// <summary>
    /// The value at <c>key</c>, as a tracked read of that key's cell. Creates the
    /// cell on first read. An unobserved cell is evicted at the lookup's next
    /// transition or <c>Get</c> of another key. Raises <c>ObjectDisposedException</c>
    /// once the lookup is disposed.
    /// </summary>
    /// <remarks>
    /// Effects woken while the read computes keys run after it returns.
    /// </remarks>
    member this.Get(key: 'K) : 'V =
        if disposed then
            raise (ObjectDisposedException (this.GetType().Name))

        graph.EnterPull ()

        try
            this.EvictExcept key
            this.Source.Refresh ()

            let existing = cells.Find key

            if not (isNull (box existing)) then
                existing.Read ()
            else
                let cell = this.NewCell key
                cells.Set (key, cell)
                this.Recompute (key, cell)

                try
                    cell.Read ()
                finally
                    if cell.ObserverCount = 0 then
                        orphans.Add key |> ignore
        finally
            graph.ExitPull ()

    /// <summary>
    /// The value at <c>key</c> if its cell is live, as a tracked read of that cell.
    /// A key without a live cell returns <c>None</c> untracked, and creates no cell.
    /// Raises <c>ObjectDisposedException</c> once the lookup is disposed.
    /// </summary>
    member this.TryGet(key: 'K) : 'V option =
        if disposed then
            raise (ObjectDisposedException (this.GetType().Name))

        graph.EnterPull ()

        try
            this.Source.Refresh ()

            let cell = cells.Find key

            if isNull (box cell) then None else Some (cell.Read ())
        finally
            graph.ExitPull ()

#if !FABLE_COMPILER
    /// <summary>
    /// <c>TryGet</c> in the <c>TryGetValue</c> shape: true with the value when <c>TryGet</c> returns one.
    /// </summary>
    member this.TryGetValue(key: 'K, [<System.Runtime.InteropServices.Out>] value: byref<'V>) : bool =
        match this.TryGet key with
        | Some found ->
            value <- found
            true
        | None ->
            value <- Unchecked.defaultof<'V>
            false
#endif

    /// <summary>
    /// The number of live cells, read untracked.
    /// </summary>
    member _.CellCount = cells.Count

    member internal _.Cells = cells

    member _.Dispose() =
        if not disposed then
            disposed <- true

            if not (isNull link) then
                link.Detach ()
                link <- null

            cells.Clear ()
            failed.Clear ()
            orphans.Clear ()
            scope.Dispose ()

    interface IScopeHost with
        member _.Scope =
            violated <- true
            raise (InvalidOperationException rule)

    interface IDisposable with
        member this.Dispose() =
            this.Dispose ()

    interface IOwned with
        member this.Release() =
            link <- null
            this.Dispose ()

/// <summary>
/// A lookup over a source of type <c>'S</c>. The source is held in a memo; an
/// effect over that memo invalidates the keys returned by <c>affected</c> on each change,
/// and every read brings the state current first, so a read after a write in
/// the same batch or flush sees the write.
/// </summary>
type internal LookupOf<'S, 'K, 'V when 'K: equality>(graph: Graph, f: 'S -> 'K -> 'V, affected: 'S -> 'S -> 'K seq, source: unit -> 'S) as this =
    inherit Lookup<'K, 'V>(graph)

    let stateEqual = graph.Options.Equality.Comparer<'S>()

    let state =
        graph.RunOwned (this.Scope, (fun () -> Memo<'S>.Create(graph, (fun _ -> source ()), ScopeMode.Owning)))

    /// <summary>
    /// The state the live cells were computed against. Meaningful once
    /// <c>primed</c> is set.
    /// </summary>
    let mutable previous = Unchecked.defaultof<'S>
    let mutable primed = false

    /// <summary>
    /// The source's failure, while the source is failing.
    /// </summary>
    let mutable sourceError: Failure = null

    /// <summary>
    /// Set while the source is pending.
    /// </summary>
    let mutable sourcePending = false

    do
        let refresh =
            graph.RunOwned (
                this.Scope,
                fun () ->
                    Effect.Create (
                        graph,
                        fun () ->
                            graph.Track (state :> ISource)
                            this.Refresh ()
                    )
            )

        Tracer.Part (graph, (refresh :> INode).Id, (state :> INode).Id, null)

#if RANVIER_TRACE
    do this.TraceHost <- (state :> INode).Id
#endif

    interface ILookupSource<'K, 'V> with
        member this.Compute key =
            this.Compute key

        member this.Refresh() =
            this.Refresh ()

    member private _.Compute(key: 'K) =
        if sourcePending then
            raise (graph.NotReady (state :> INode))

        if not (isNull sourceError) then
            graph.Raise sourceError

        f previous key

    member private this.Refresh() =
        if not this.IsDisposed then
            let mutable next = Unchecked.defaultof<'S>
            let mutable failure: exn = null
            let mutable pending = false
            let mutable moved = false

            try
                next <- graph.RunUntracked (fun () -> state.Value)
            with
            | NotReadyException _ -> pending <- true
            | ex -> failure <- ex

            // A throwing comparer fails every live cell as a throwing source does.
            if primed && not pending && isNull failure then
                if sourcePending || not (isNull sourceError) then
                    moved <- true
                else
                    try
                        moved <- not (stateEqual.Equals (previous, next))
                    with ex ->
                        failure <- ex

            if pending then
                if not sourcePending then
                    sourcePending <- true
                    sourceError <- null
                    this.SuspendAll ()
            elif not (isNull failure) then
                if
                    sourcePending
                    || not (obj.ReferenceEquals (failure, Failure.ErrorOf sourceError))
                then
                    sourcePending <- false
                    sourceError <- graph.FailureOf (failure, state, null)
                    this.FailAll sourceError
            elif not primed then
                primed <- true
                previous <- next
                sourcePending <- false
                sourceError <- null
                this.Invalidate Seq.empty
            elif moved then
                let prev = previous
                previous <- next
                sourcePending <- false
                sourceError <- null

                let mutable keys = Seq.empty
                let mutable affectedError: exn = null

                try
                    keys <- this.RunPure (ScopeMessages.lookupAffected, (fun () -> ResizeArray (affected prev next) :> 'K seq))
                with ex ->
                    affectedError <- ex

                if isNull affectedError then
                    this.Invalidate keys
                else
                    this.FailAll (graph.FailureOf (affectedError, state, null))
