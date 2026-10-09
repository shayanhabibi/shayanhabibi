namespace Ranvier

open System
open System.Collections.Generic

/// <summary>The rows and membership journal of an editable collection.</summary>
type internal CollectionProjection<'K, 'V when 'K: equality>(graph: Graph) as this =
    inherit RowsOf<Signal<'V>, 'K, 'V>(graph, (fun item -> item.Value), Unchecked.defaultof<_>)

    let items = Platform.KeyMap<'K, Signal<'V>>()
    let order = ResizeArray<'K>()
    let keyEqual = HashIdentity.Structural<'K>
    let revision = Signal<int>(graph, 0, EqualityComparer<int>.Default)
    let mutable changes = KeyChanges<'K>()
    let mutable previous: 'K[] = Array.empty
    let mutable first = true
    let mutable stopped = false

    do
        this.ReadDelta <-
            fun () ->
                revision.Value |> ignore
                let current = order.ToArray ()
                let delta = ProjectionDelta (changes, current, previous, first)
                changes <- KeyChanges<'K>()
                previous <- current
                first <- false
                delta

        this.VisitDelta <- fun key -> this.Visit (key, items.Find key)

        graph.RunOwned (
            this.Scope,
            fun () ->
                this.Scope.OnCleanup (fun () ->
                    stopped <- true
                    items.Clear ()
                    order.Clear ()
                    changes.Clear ()
                    previous <- Array.empty)
        )

    member private _.EnsureLive() =
        if stopped then
            raise (ObjectDisposedException "The keyed collection was disposed.")

    member _.AddOrUpdate(key: 'K, value: 'V) =
        graph.RunBatch (fun () ->
            this.EnsureLive ()
            let item = items.Find key

            if isNull (box item) then
                items.Set (key, Signal<'V>(graph, value))
                order.Add key
                changes.Record (key, KeyChange.Added)
                revision.Value <- revision.Peek + 1
            else
                item.Value <- value)

    member _.Remove(key: 'K) =
        graph.RunBatch (fun () ->
            this.EnsureLive ()

            if isNull (box (items.Find key)) then
                false
            else
                items.Remove key
                let mutable index = 0

                while not (keyEqual.Equals (order[index], key)) do
                    index <- index + 1

                order.RemoveAt index
                changes.Record (key, KeyChange.Removed)
                revision.Value <- revision.Peek + 1
                true)

    member _.Clear() =
        graph.RunBatch (fun () ->
            this.EnsureLive ()

            if order.Count > 0 then
                for key in order do
                    changes.Record (key, KeyChange.Removed)

                items.Clear ()
                order.Clear ()
                revision.Value <- revision.Peek + 1)

    member _.Edit(body: unit -> unit) =
        graph.RunBatch (fun () ->
            this.EnsureLive ()
            body ())

    override _.Enumerate() =
        revision.Value |> ignore

        for key in order do
            let item = items.Find key
            let entry = this.Entries.Find key

            if
                not (isNull entry)
                && not (obj.ReferenceEquals ((entry :?> ItemRow<Signal<'V>, 'K, 'V>).Item.Peek, item))
            then
                this.RetireChanged key

            this.Visit (key, item)

/// <summary>An editable collection with insertion order and separately observable rows.</summary>
/// <remarks>Owned by the creating scope. Updates use the graph's value equality policy; keys use structural equality.</remarks>
type KeyedCollection<'K, 'V when 'K: equality> internal (graph: Graph, keyOf: 'V -> 'K) =
    let rows = new CollectionProjection<'K, 'V> (graph)

    /// <summary>The reactive view used by collection operators and readers.</summary>
    member _.Rows = rows :> Projection<'K, 'V>

    /// <summary>Adds a new key at the end, or updates its existing row without changing its position.</summary>
    member _.AddOrUpdate(value: 'V) =
        rows.Edit (fun () -> rows.AddOrUpdate (keyOf value, value))

    /// <summary>Removes the key. Returns false when it is absent.</summary>
    member _.Remove(key: 'K) =
        rows.Remove key

    /// <summary>Removes every key.</summary>
    member _.Clear() =
        rows.Clear ()

    /// <summary>Batches the synchronous edits made by the callback.</summary>
    /// <remarks>Applied edits survive a throwing callback. This operation does not roll back or span asynchronous work.</remarks>
    member this.Edit(edit: Action<KeyedCollection<'K, 'V>>) =
        if isNull edit then
            nullArg "edit"

        rows.Edit (fun () -> edit.Invoke this)

    /// <summary>Disposes the rows and resets existing readers to empty. Idempotent.</summary>
    member _.Dispose() =
        rows.Dispose ()

    interface IDisposable with
        member this.Dispose() =
            this.Dispose ()
