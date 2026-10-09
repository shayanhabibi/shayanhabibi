namespace Ranvier

open System.Collections.Generic

/// <summary>One live key's row and factory scope.</summary>
/// <remarks>The scope is null in the value form, where the row is the key's only node.</remarks>
[<AllowNullLiteral>]
type internal RowEntry<'K, 'V>(key: 'K) =
    member _.Key = key
    member val Row: Memo<'V> = Unchecked.defaultof<_> with get, set
    member val Scope: Owner = Unchecked.defaultof<_> with get, set
    /// <summary>The mapper over the item in the value form, or the factory's returned reader.</summary>
    member val Reader: unit -> 'V = Unchecked.defaultof<_> with get, set
    /// <summary>Whether the row memo has committed a value.</summary>
    member val Settled = false with get, set
    /// <summary>Whether the pending row is observed by the projection's row watcher.</summary>
    member val Watched = false with get, set
    /// <summary>False after key removal or projection disposal.</summary>
    member val Live = true with get, set

[<Sealed>]
type internal ValueRow<'K, 'V>(graph: Graph, entry: RowEntry<'K, 'V>, touched: ValueRow<'K, 'V> -> unit) =
    let id = graph.NextId ()
    member _.Entry = entry
    member val Queued = false with get, set
    member val Attached = true with get, set
    member val HasValue = false with get, set
    member val Value = Unchecked.defaultof<'V> with get, set

    interface INode with
        member _.Id = id
        member _.Status = Status.None

    interface IComputation with
        member _.AddSource _ = ()

        member this.MarkDirty() =
            touched this

        member this.MarkCheck() =
            touched this

/// <summary>Shared settled-value observation, active only while value readers exist.</summary>
[<Sealed>]
type internal ProjectionValues<'K, 'V when 'K: equality>(graph: Graph, find: 'K -> RowEntry<'K, 'V>, readKeys: unit -> 'K[], changed: 'K -> unit) as this
    =
    let id = graph.NextId ()
    let observers = ObserverSet ()
    do Tracer.Bind (observers, graph, id)
    let equal = graph.Options.Equality.Comparer<'V>()
    let rows = Platform.KeyMap<'K, ValueRow<'K, 'V>>()
    let seen = Platform.KeySet<'K>()
    let removed = ResizeArray<'K>()
    let queue = ResizeArray<ValueRow<'K, 'V>>()
    let mutable keys: 'K[] = null
    let mutable disposed = false
    let mutable running = false

    let touched (row: ValueRow<'K, 'V>) =
        if not disposed && row.Attached && not row.Queued then
            row.Queued <- true
            queue.Add row
            observers.NotifyCheck ()

    member private _.Detach(row: ValueRow<'K, 'V>) =
        row.Attached <- false
        (row.Entry.Row :> ISource).RemoveObserver(row :> IComputation)

    member private this.Sync(current: 'K[]) =
        if not (obj.ReferenceEquals (keys, current)) then
            seen.Clear ()

            for key in current do
                seen.Add key |> ignore
                let entry = find key
                let old = rows.Find key

                if
                    not (isNull entry)
                    && (isNull (box old)
                        || not (obj.ReferenceEquals (old.Entry, entry)))
                then
                    if not (isNull (box old)) then
                        this.Detach old

                    let row = ValueRow (graph, entry, touched)

                    if entry.Settled then
                        row.HasValue <- true
                        row.Value <- entry.Row.Peek

                    rows.Set (key, row)
                    (entry.Row :> ISource).AddObserver(row :> IComputation)
                    touched row

            rows.Iterate (fun key _ ->
                if not (seen.Contains key) then
                    removed.Add key)

            for key in removed do
                this.Detach (rows.Find key)
                rows.Remove key

            removed.Clear ()
            keys <- current

    member private this.Refresh(current: 'K[]) =
        if not disposed && not running then
            running <- true
            let mutable processed = 0

            try
                this.Sync current

                try
                    while processed < queue.Count do
                        let row = queue[processed]
                        row.Queued <- false

                        try
                            if row.Attached && row.Entry.Live then
                                match graph.RunUntracked (fun () -> row.Entry.Row.TryValue) with
                                | Ready value ->
                                    if
                                        not row.HasValue
                                        || not (equal.Equals (row.Value, value))
                                    then
                                        changed row.Entry.Key
                                        row.Value <- value
                                        row.HasValue <- true
                                        observers.NotifyDirtyExcept graph.CurrentComputation
                                | Pending
                                | Failed _ -> ()
                        with _ ->
                            if row.Queued then
                                processed <- processed + 1
                            else
                                row.Queued <- true

                            reraise ()

                        processed <- processed + 1
                finally
                    if processed > 0 then
                        queue.RemoveRange (0, processed)
            finally
                running <- false

    member this.Read(current: 'K[]) =
        graph.Track (this :> ISource)
        this.Refresh current

    member this.Retire(entry: RowEntry<'K, 'V>) =
        let row = rows.Find entry.Key

        if
            not (isNull (box row))
            && obj.ReferenceEquals (row.Entry, entry)
        then
            this.Detach row
            rows.Remove entry.Key
            keys <- null
            observers.NotifyDirtyExcept graph.CurrentComputation

    member _.TryAccepted(key: 'K) =
        let row = rows.Find key

        if not (isNull (box row)) && row.HasValue then
            ValueSome row.Value
        else
            ValueNone

    member this.Dispose() =
        if not disposed then
            disposed <- true
            rows.Iterate (fun _ row -> this.Detach row)
            rows.Clear ()
            queue.Clear ()
            seen.Clear ()
            keys <- null

    interface INode with
        member _.Id = id
        member _.Status = Status.None

    interface ISource with
        member _.AddObserver c =
            observers.Add c

        member _.RemoveObserver c =
            observers.Remove c

        member _.UpdateIfNecessary() =
            if not disposed then
                try
                    graph.RunUntracked (fun () -> this.Refresh (readKeys ()))
                with _ ->
                    observers.NotifyDirty ()
