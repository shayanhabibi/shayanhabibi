namespace Ranvier

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks

/// <summary>Identity tests on generic values.</summary>
module internal Identity =
#if FABLE_COMPILER
    /// <summary>True when <c>a</c> and <c>b</c> are <c>===</c>.</summary>
    let inline same (a: 'A) (b: 'A) : bool =
        obj.ReferenceEquals (a, b)

    /// <summary>True when <c>a</c> and <c>b</c> are <c>===</c> or both NaN.</summary>
    [<Fable.Core.Emit("($0 === $1 || ($0 !== $0 && $1 !== $1))")>]
    let unchanged (a: 'A) (b: 'A) : bool =
        obj.ReferenceEquals (a, b)
#else
    /// <summary>
    /// True when <c>a</c> and <c>b</c> are the same object. Always false for a value type, and allocates no box for one.
    /// </summary>
    let inline same (a: 'A) (b: 'A) : bool =
        not typeof<'A>.IsValueType
        && obj.ReferenceEquals (a, b)

    /// <summary>
    /// True when <c>b</c> is <c>a</c>: the same object, or an equal value for a value type or a string. NaN equals NaN.
    /// </summary>
    let inline unchanged (a: 'A) (b: 'A) : bool =
        if typeof<'A>.IsValueType then
            Collections.Generic.EqualityComparer<'A>.Default.Equals(a, b)
        else
            JsComparer<'A>.Instance.Equals(a, b)
#endif

/// <summary>A seed value with the number of its publication: 1 for the first, one more for each unequal value after it.</summary>
[<Sealed; AllowNullLiteral>]
type internal Stamp<'T>(version: int, value: 'T) =
    member _.Version = version
    member _.Value = value

/// <summary>
/// Equality of two edits: both absent, or equal stamps whose values are equal under <c>equal</c>.
/// </summary>
[<Sealed>]
type internal EditComparer<'T>(equal: Collections.Generic.IEqualityComparer<'T>) =
    interface Collections.Generic.IEqualityComparer<struct (int * 'T) voption> with
        member _.Equals(a, b) =
            match a, b with
            | ValueNone, ValueNone -> true
            | ValueSome (struct (va, xa)), ValueSome (struct (vb, xb)) -> va = vb && equal.Equals (xa, xb)
            | _ -> false

        member _.GetHashCode(edit) =
            match edit with
            | ValueNone -> 0
            | ValueSome (struct (version, _)) -> version

/// <summary>
/// A value seeded from upstream that accepts local edits. <c>Value</c> holds the edit while one is in force and the seed's
/// value otherwise. Owned by the scope current at creation.
/// </summary>
/// <remarks>
/// <para>
/// An editable made by <c>createEditable</c> drops its edit once the seed publishes a value unequal to its previous one,
/// under the graph's equality policy. Every such publication counts, so A → B → A drops the edit when B was published.
/// Writes that end at an equal value within one seed run (a batch, or a seed nothing read in between) keep the edit.
/// A draft, made by <c>createDraft</c>, keeps its edit until <c>Reset</c>.
/// </para>
/// <para>
/// An edit made while the seed is pending belongs to the seed's last settled value. While the seed is pending an edit in
/// force stays in force; a seed failure replaces the value of an editable, and a draft's edit hides it.
/// </para>
/// </remarks>
[<Sealed>]
type Editable<'T> internal (graph: Graph, seed: 'T voption -> 'T, draft: bool) =
    let equal = graph.Options.Equality.Comparer<'T>()

    /// <summary>The version of the seed's last published value; 0 before the first.</summary>
    let mutable settled = 0

    let stamped =
        Memo.Create (
            graph,
            (fun (previous: Stamp<'T> voption) ->
                let next =
                    seed (
                        match previous with
                        | ValueSome stamp -> ValueSome stamp.Value
                        | ValueNone -> ValueNone
                    )

                match previous with
                | ValueSome stamp when
                    Identity.same stamp.Value next
                    || equal.Equals (stamp.Value, next)
                    ->
                    stamp
                | _ ->
                    let version =
                        match previous with
                        | ValueSome stamp -> stamp.Version + 1
                        | ValueNone -> 1

                    settled <- version
                    Stamp (version, next)),
            ScopeMode.Pure
        )

    let edit =
        Signal<struct (int * 'T) voption>(graph, ValueNone, EditComparer<'T> equal)

    /// <summary>True when an edit made against seed version <c>version</c> is still in force. A tracked read of the seed.</summary>
    let inForce (version: int) =
        draft
        || match stamped.TryValue with
           | Ready stamp -> stamp.Version = version
           | Pending -> version = settled
           | Failed _ -> false

    let value =
        Memo.Create (
            graph,
            (fun _ ->
                match edit.Value with
                | ValueSome (struct (version, x)) when inForce version -> x
                | _ -> stamped.Value.Value),
            ScopeMode.Pure
        )

    /// <summary>
    /// A tracked read of the edit in force, or of the seed's value. Setting it records an edit and wakes readers.
    /// </summary>
    /// <remarks>
    /// The getter raises <c>NotReadyException</c> while the seed is pending with no edit in force, and the seed's
    /// exception while it has failed. The setter brings the seed current, untracked, and records the edit against the
    /// seed's last settled value.
    /// </remarks>
    /// <exception cref="T:System.InvalidOperationException">Set off the graph's thread under a guarded graph.</exception>
    member _.Value
        with get () = value.Value
        and set (v: 'T) =
            graph.AssertOnGraphThread "An editable write"
            (stamped :> ISource).UpdateIfNecessary()
            edit.Value <- ValueSome (struct (settled, v))

    /// <summary>A non-throwing, tracked read of <c>Value</c>.</summary>
    member _.TryValue: Reading<'T> = value.TryValue

    /// <summary>An untracked read of the last computed <c>Value</c>, without recomputing.</summary>
    member _.Peek = value.Peek

    /// <summary>The status of <c>Value</c> after its last computation.</summary>
    member _.Status = value.Status

    /// <summary>True while an edit is in force. A tracked read.</summary>
    member _.IsEdited =
        match edit.Value with
        | ValueSome (struct (version, _)) -> inForce version
        | ValueNone -> false

    /// <summary>A tracked read of the seed's current value, whether or not an edit is in force.</summary>
    /// <remarks>Raises <c>NotReadyException</c> while the seed is pending and the seed's exception while it has failed.</remarks>
    member _.Upstream = stamped.Value.Value

    /// <summary>Drops the edit, so <c>Value</c> reads the seed.</summary>
    /// <exception cref="T:System.InvalidOperationException">Called off the graph's thread under a guarded graph.</exception>
    member _.Reset() =
        edit.Value <- ValueNone

    /// <summary>Detaches both derived nodes from their sources. Idempotent; the enclosing owner calls it at disposal.</summary>
    member _.Dispose() =
        value.Dispose ()
        stamped.Dispose ()

/// <summary>
/// The functions most code should use. Every node type can be constructed
/// directly against an explicit <c>Graph</c> — that is what the tests and the
/// benchmarks do, because they run several graphs in one process — but ordinary
/// code has exactly one graph per thread and should not thread it through every
/// call.
/// </summary>
/// <remarks>
/// <para>
/// These resolve <c>Graph.Current</c>, so they throw outside an active graph rather
/// than silently creating one nobody disposes.
/// </para>
/// <para>
/// Two departures from Solid's names, both because F# can express what
/// JavaScript could not:
/// </para>
/// <para>
/// - <c>createSignal</c> hands back the signal itself, not a getter/setter pair.
///   <c>count.Value</c> reads and <c>count.Value &lt;- 2</c> writes, which is the same two
///   operations without the two closures, and it keeps <c>Peek</c>, <c>TryValue</c> and
///   <c>Status</c> reachable.
/// - <c>createEffect</c> returns unit. The effect attaches itself to the enclosing
///   scope and is disposed with it, so the handle is noise at the call site.
///   Construct <c>Effect</c> directly on the rare occasion you want to dispose one
///   early.
/// </para>
/// </remarks>
[<AutoOpen>]
module Api =
    /// <summary>An empty editable collection with insertion order and reactive rows.</summary>
    let createKeyedCollection (keyOf: 'V -> 'K) : KeyedCollection<'K, 'V> =
        if isNull (box keyOf) then
            nullArg "keyOf"

        new KeyedCollection<'K, 'V> (Graph.Current, keyOf)

    let private requireComparer (comparer: IEqualityComparer<'T>) =
        if isNull comparer then
            nullArg "comparer"

    /// <summary>
    /// A settable source.
    /// </summary>
    let createSignal (initial: 'T) =
        Signal (Graph.Current, initial)

    /// <summary>A settable source whose write cutoff uses <c>comparer</c> instead of the graph's equality policy.</summary>
    /// <exception cref="T:System.ArgumentNullException"><c>comparer</c> is null.</exception>
    let createSignalWithComparer (comparer: IEqualityComparer<'T>) (initial: 'T) =
        requireComparer comparer
        Signal (Graph.Current, initial, comparer)

    /// <summary>
    /// A derived value, recomputed on read once something it read has changed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>compute</c> is a pure derivation. Creating an owned node in it (a memo,
    /// effect, async value, boundary, root, projection, lookup or <c>onCleanup</c>),
    /// <c>untrack</c> blocks included, raises <c>InvalidOperationException</c>, and the
    /// run fails even when <c>compute</c> catches the exception. A memo that creates
    /// nodes is <c>createMemoWith</c>.
    /// </para>
    /// <para>
    /// <c>compute</c> receives the value last published, <c>ValueNone</c> before the first. After a run that suspends or
    /// fails, the next run receives the same value. While the memo holds a value, returning the value inside
    /// <c>prev</c> keeps dependents clean.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code lang="fsharp">
    /// let total = createMemo (fun prev -> ValueOption.defaultValue 0 prev + amount.Value)
    /// </code>
    /// </example>
    let createMemo (compute: 'T voption -> 'T) =
        Memo.Create (Graph.Current, compute, ScopeMode.Pure)

    /// <summary>A pure memo whose value cutoff uses <c>comparer</c> instead of the graph's equality policy.</summary>
    /// <remarks>Pending and failed state changes still propagate. The purity and previous-value rules of <c>createMemo</c> apply.</remarks>
    /// <exception cref="T:System.ArgumentNullException"><c>comparer</c> is null.</exception>
    let createMemoWithComparer (comparer: IEqualityComparer<'T>) (compute: 'T voption -> 'T) =
        requireComparer comparer
        Memo.CreateWithComparer (Graph.Current, compute, ScopeMode.Pure, comparer)

    /// <summary>
    /// A derived value that owns the nodes and cleanups <c>compute</c> creates. A run's nodes and cleanups are disposed
    /// before the next run and with the memo; the cleanups run untracked, and <c>compute</c> runs once per discharge.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A read of the memo from one of its cleanups returns the previous value, unless the cleanup first wrote a source of
    /// the memo: that read re-runs <c>compute</c> and its result replaces the pending run. An async value created and read
    /// in <c>compute</c> restarts its flight on every settle and never settles: create it outside and read it in <c>compute</c>.
    /// </para>
    /// <para>
    /// <c>compute</c> receives the value last published, <c>ValueNone</c> before the first. Nodes created by the previous
    /// run are disposed before <c>compute</c> runs, including any held in that value.
    /// </para>
    /// </remarks>
    let createMemoWith (compute: 'T voption -> 'T) =
        Memo.Create (Graph.Current, compute, ScopeMode.Owning)

    /// <summary>An owning memo whose value cutoff uses <c>comparer</c> instead of the graph's equality policy.</summary>
    /// <remarks>Each run's nodes and cleanups are disposed before the next run and with the memo, including after an equal result.</remarks>
    /// <exception cref="T:System.ArgumentNullException"><c>comparer</c> is null.</exception>
    let createOwningMemoWithComparer (comparer: IEqualityComparer<'T>) (compute: 'T voption -> 'T) =
        requireComparer comparer
        Memo.CreateWithComparer (Graph.Current, compute, ScopeMode.Owning, comparer)

    /// <summary>
    /// A side effect, run once now and again whenever something it read changes. Disposed with the enclosing scope.
    /// </summary>
    /// <remarks>
    /// Its cleanups run untracked with the effect's scope as the owner, before a re-run and at disposal; a node created by a
    /// cleanup belongs to that scope, including when a pure memo disposes the effect. An async value created and read in
    /// <c>body</c> restarts its flight on every settle and the effect never runs past the read: create it outside.
    /// </remarks>
    let createEffect (body: unit -> unit) =
        Effect.Create (Graph.Current, body) |> ignore

    /// <summary>
    /// A side effect split in two: <c>compute</c> reads the dependencies and <c>act</c> performs the effect with its
    /// result. Disposed with the enclosing scope.
    /// </summary>
    /// <remarks>
    /// <c>compute</c> is a pure derivation, as <c>createMemo</c>'s is. <c>act</c> runs untracked, owns its cleanups, and
    /// runs only once <c>compute</c> has settled on a value unequal to the last value acted on; a pending read or a
    /// failure in <c>compute</c> leaves the previous action in place.
    /// </remarks>
    let createEffectOn (compute: unit -> 'T) (act: 'T -> unit) =
        EffectOn<'T>.Create(Graph.Current, compute, act)
        |> ignore

    /// <summary>A split effect whose action cutoff uses <c>comparer</c> instead of the graph's equality policy.</summary>
    /// <remarks>Equal values keep the previous action's resources. Compute remains pure; act remains untracked and owning.</remarks>
    /// <exception cref="T:System.ArgumentNullException"><c>comparer</c> is null.</exception>
    let createEffectOnWithComparer (comparer: IEqualityComparer<'T>) (compute: unit -> 'T) (act: 'T -> unit) =
        requireComparer comparer

        EffectOn<'T>.CreateWithComparer(Graph.Current, compute, act, comparer)
        |> ignore

    /// <summary>
    /// A derived value computed asynchronously. Reads of it raise <c>NotReadyException</c>, which a boundary catches, until
    /// the first result arrives; a change to something it read starts a new run.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>compute</c> is a pure derivation, as <c>createMemo</c>'s is; an async value that creates nodes is
    /// <c>createAsyncWith</c>. The purity check covers <c>compute</c> up to its first <c>await</c> that suspends; an
    /// <c>await</c> on an already-completed task does not suspend.
    /// </para>
    /// <para>
    /// <c>compute</c> receives the value last published as a <c>Previous</c>. Read every input, then await
    /// <c>Settled</c>: tracking stops at the first <c>await</c> that suspends, and under <c>FlightPolicy.Queue</c>
    /// awaiting <c>Settled</c> suspends until the flight started before it is applied.
    /// </para>
    /// </remarks>
    let createAsync (compute: Previous<'T> -> CancellationToken -> Task<'T>) =
        AsyncMemo<'T>.Create(Graph.Current, compute, ScopeMode.PureAsync)

    /// <summary>
    /// An async value that owns the nodes <c>compute</c> creates up to its first <c>await</c> that suspends, disposed before
    /// the next flight starts and with the async value. An <c>await</c> on an already-completed task does not suspend.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A node created after that <c>await</c> by a continuation on the graph thread belongs to the graph's root; to keep it in
    /// the flight, create it inside <c>runWithOwner</c> with <c>getOwner ()</c> captured before the <c>await</c>. An async value
    /// created and read in <c>compute</c> before that <c>await</c> restarts its flight on every settle and never settles.
    /// </para>
    /// <para>
    /// <c>compute</c> receives the value last published as a <c>Previous</c>, as <c>createAsync</c>'s does: read every
    /// input, then await <c>Settled</c>.
    /// </para>
    /// </remarks>
    let createAsyncWith (compute: Previous<'T> -> CancellationToken -> Task<'T>) =
        AsyncMemo<'T>.Create(Graph.Current, compute, ScopeMode.Owning)

    /// <summary>
    /// A source whose value arrives later, settled by hand rather than computed.
    /// </summary>
    let createAsyncSource<'T> () =
        AsyncSource<'T>(Graph.Current)

    /// <summary>
    /// Substitutes <c>fallback</c> for as long as <c>body</c> is suspended, so the suspension stops here instead of
    /// propagating to everything downstream.
    /// </summary>
    /// <remarks>
    /// <c>fallback</c> receives the boundary's last value, <c>ValueNone</c> before its first; returning it keeps the
    /// last value shown while <c>body</c> reloads. Owns the nodes <c>body</c> creates and replaces them on every re-run.
    /// An async value created and read in <c>body</c> never settles: create it outside and read it in <c>body</c>.
    /// </remarks>
    let createSuspense (fallback: 'T voption -> 'T) (body: unit -> 'T) =
        Boundary<'T>.Create(Graph.Current, body, ValueSome fallback, ValueNone)

    /// <summary>A suspense boundary whose value cutoff uses <c>comparer</c> instead of the graph's equality policy.</summary>
    /// <remarks>Waiting and failure state changes still propagate; the fallback and ownership rules of <c>createSuspense</c> apply.</remarks>
    /// <exception cref="T:System.ArgumentNullException"><c>comparer</c> is null.</exception>
    let createSuspenseWithComparer (comparer: IEqualityComparer<'T>) (fallback: 'T voption -> 'T) (body: unit -> 'T) =
        requireComparer comparer
        Boundary<'T>.CreateWithComparer(Graph.Current, body, ValueSome fallback, ValueNone, comparer)

    /// <summary>Substitutes <c>recover ex last</c> when <c>body</c> throws.</summary>
    /// <remarks>
    /// <c>last</c> is the boundary's last value, <c>ValueNone</c> before its first. Owns the nodes <c>body</c> creates
    /// and replaces them on every re-run. An async value created and read in <c>body</c> never settles: create it
    /// outside and read it in <c>body</c>.
    /// </remarks>
    let createErrorBoundary (recover: exn -> 'T voption -> 'T) (body: unit -> 'T) =
        Boundary<'T>.Create(Graph.Current, body, ValueNone, ValueSome recover)

    /// <summary>An error boundary whose value cutoff uses <c>comparer</c> instead of the graph's equality policy.</summary>
    /// <remarks>A throwing comparer fails the boundary without invoking <c>recover</c>.</remarks>
    /// <exception cref="T:System.ArgumentNullException"><c>comparer</c> is null.</exception>
    let createErrorBoundaryWithComparer (comparer: IEqualityComparer<'T>) (recover: exn -> 'T voption -> 'T) (body: unit -> 'T) =
        requireComparer comparer
        Boundary<'T>.CreateWithComparer(Graph.Current, body, ValueNone, ValueSome recover, comparer)

    /// <summary>Substitutes <c>fallback last</c> while <c>body</c> is suspended and <c>recover ex last</c> when it throws.</summary>
    /// <remarks>
    /// <c>last</c> is the boundary's last value, <c>ValueNone</c> before its first. Owns the nodes <c>body</c> creates
    /// and replaces them on every re-run. An async value created and read in <c>body</c> never settles: create it
    /// outside and read it in <c>body</c>.
    /// </remarks>
    let createBoundary (fallback: 'T voption -> 'T) (recover: exn -> 'T voption -> 'T) (body: unit -> 'T) =
        Boundary<'T>.Create(Graph.Current, body, ValueSome fallback, ValueSome recover)

    /// <summary>A pending/error boundary whose value cutoff uses <c>comparer</c> instead of the graph's equality policy.</summary>
    /// <remarks>Waiting, caught errors and failure state changes still propagate.</remarks>
    /// <exception cref="T:System.ArgumentNullException"><c>comparer</c> is null.</exception>
    let createBoundaryWithComparer
        (comparer: IEqualityComparer<'T>)
        (fallback: 'T voption -> 'T)
        (recover: exn -> 'T voption -> 'T)
        (body: unit -> 'T)
        =
        requireComparer comparer
        Boundary<'T>.CreateWithComparer(Graph.Current, body, ValueSome fallback, ValueSome recover, comparer)

    /// <summary>
    /// Runs <c>body</c> without recording anything it reads.
    /// </summary>
    let untrack (body: unit -> 'T) =
        Graph.Current.RunUntracked body

    /// <summary>
    /// Runs <c>body</c> with effects deferred, so a group of writes produces one run
    /// of each effect rather than one per write.
    /// </summary>
    let batch (body: unit -> 'T) =
        Graph.Current.RunBatch body

    /// <summary>
    /// Registers a cleanup with the innermost enclosing scope. Inside the body
    /// of an effect, owning memo, owning async value or boundary that is the
    /// computation's own scope, so it runs before the next re-run as well as at
    /// disposal. On a disposed scope the cleanup runs immediately, untracked
    /// and with effects deferred until it returns.
    /// </summary>
    /// <remarks>
    /// Raises <c>InvalidOperationException</c> inside a pure body: <c>createMemo</c>,
    /// <c>createAsync</c>, a projection row's reader and a lookup's <c>f</c> or
    /// <c>affected</c>.
    /// </remarks>
    let onCleanup (f: unit -> unit) =
        Graph.Current.AddCleanup f

    /// <summary>
    /// The innermost enclosing scope: the scope <c>onCleanup</c> registers with and
    /// new nodes attach to. Captured before an <c>await</c>, it is the scope
    /// <c>runWithOwner</c> needs after it.
    /// </summary>
    /// <remarks>
    /// Raises <c>InvalidOperationException</c> inside a pure body, as <c>onCleanup</c>
    /// does.
    /// </remarks>
    let getOwner () : Owner =
        Graph.Current.CurrentOwner

    /// <summary>
    /// Runs <c>body</c> with <c>owner</c> as the scope new nodes and cleanups attach to.
    /// Tracking is unchanged. A node created under a disposed owner is disposed
    /// as it attaches.
    /// </summary>
    /// <remarks>
    /// Must run on the graph's thread. Raises <c>InvalidOperationException</c>
    /// inside a pure body, as <c>onCleanup</c> does.
    /// </remarks>
    let runWithOwner (owner: Owner) (body: unit -> 'T) : 'T =
        let graph = Graph.Current
        graph.CurrentOwner |> ignore
        graph.RunOwned (owner, body)

    /// <summary>
    /// Creates a nested scope and runs <c>body</c> in it, handing back the owner so
    /// the whole subtree can be disposed at once.
    /// </summary>
    let createRoot (body: Owner -> 'T) =
        Graph.Current.RunRoot body

    /// <summary>
    /// Runs the effects already queued on the current graph, including inside a batch.
    /// </summary>
    /// <remarks>
    /// Work posted from another thread stays in the inbox; <see cref="M:Ranvier.Graph.Pump"/>
    /// applies it and then flushes.
    /// </remarks>
    let flush () =
        Graph.Current.Flush ()

    /// <summary>
    /// A keyed collection derived from a source collection: a key set, and one
    /// row per key, separately observable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>keyOf</c> chooses identity. By id gives keyed reuse; by reference gives
    /// Solid's unkeyed semantics. Duplicate keys fail the pass.
    /// </para>
    /// <para>
    /// <c>factory</c> runs once per key, untracked, when the key enters, inside a
    /// scope that lives until the key is removed. It receives an accessor for
    /// the key's latest item and returns the row's reader. The row's value is
    /// the reader's result. The reader runs when the row is read and is stale;
    /// it re-runs when the key's item changes or a value it read changes.
    /// </para>
    /// <para>
    /// A survivor wakes its readers only when its item changes under the
    /// graph's equality policy. A reader that creates an owned node (a memo, effect,
    /// async value, boundary, root, projection, lookup or <c>onCleanup</c>) raises
    /// <c>InvalidOperationException</c>, and the exception becomes the row's error.
    /// Create such nodes in the factory body. A node created inside an owning
    /// memo, owning async value or boundary read by the reader belongs to that
    /// node's scope.
    /// </para>
    /// <para>
    /// The pass that evaluates <c>source</c> and <c>keyOf</c> owns the nodes they
    /// create: they are disposed before the next pass and with the projection.
    /// </para>
    /// </remarks>
    let createProjectionWith (keyOf: 'T -> 'K) (factory: (unit -> 'T) -> (unit -> 'V)) (source: unit -> 'T seq) : Projection<'K, 'V> =
        new KeyedProjection<'T, 'K, 'V> (Graph.Current, keyOf, Unchecked.defaultof<'T -> 'V>, factory, source) :> Projection<'K, 'V>

    /// <summary>
    /// <c>createProjectionWith keyOf (fun item -> fun () -> map (item ())) source</c>,
    /// with one fewer node per key. <c>map</c> re-runs when the key's item changes or
    /// a value it read changes, and raises <c>InvalidOperationException</c> if it
    /// creates an owned node; per-key nodes belong in <c>createProjectionWith</c>'s
    /// factory.
    /// </summary>
    let createProjection (keyOf: 'T -> 'K) (map: 'T -> 'V) (source: unit -> 'T seq) : Projection<'K, 'V> =
        new KeyedProjection<'T, 'K, 'V> (Graph.Current, keyOf, map, Unchecked.defaultof<_>, source) :> Projection<'K, 'V>

    /// <summary>
    /// <c>createProjectionWith</c> keyed by position: Solid's <c>indexArray</c>. The
    /// factory runs once per slot, and the accessor returns the slot's current
    /// item.
    /// </summary>
    let createIndexProjectionWith (factory: (unit -> 'T) -> (unit -> 'V)) (source: unit -> 'T seq) : Projection<int, 'V> =
        new IndexProjection<'T, 'V> (Graph.Current, Unchecked.defaultof<'T -> 'V>, factory, source) :> Projection<int, 'V>

    /// <summary>
    /// <c>createProjection</c> keyed by position: the row at a slot survives its
    /// item changing.
    /// </summary>
    let createIndexProjection (map: 'T -> 'V) (source: unit -> 'T seq) : Projection<int, 'V> =
        new IndexProjection<'T, 'V> (Graph.Current, map, Unchecked.defaultof<_>, source) :> Projection<int, 'V>

    /// <summary>
    /// A pointwise derived collection over an open key domain: a cell is built
    /// for each key read, and a source change recomputes only the keys
    /// returned by <c>affected</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>affected prev next</c> must name every key whose value can differ between
    /// the two states. A key left out keeps its stale value; an extra key costs
    /// one recomputation.
    /// </para>
    /// <para>
    /// <c>f</c> is pure: a key whose <c>f</c> creates an owned node fails with
    /// <c>InvalidOperationException</c>. <c>affected</c> is pure too: a node it creates
    /// fails every live key. <c>source</c> owns the nodes it creates, as
    /// <c>createMemoWith</c> does. Effects woken while a read computes keys run
    /// after the read returns.
    /// </para>
    /// </remarks>
    let createLookup (f: 'S -> 'K -> 'V) (affected: 'S -> 'S -> 'K seq) (source: unit -> 'S) : Lookup<'K, 'V> =
        new LookupOf<'S, 'K, 'V> (Graph.Current, f, affected, source) :> Lookup<'K, 'V>

    /// <summary>
    /// Membership in a single-valued selection: <c>selector.Get k</c> is true for
    /// the selected key and false for every other. A selection change wakes
    /// the readers of the previous and the next key only.
    /// </summary>
    /// <remarks>
    /// Solid's <c>createSelector</c>: <c>createLookup</c> with
    /// <c>affected = fun prev next -> [ prev; next ]</c>. Keys compare structurally,
    /// as the lookup's cell map does.
    /// </remarks>
    let createSelector (source: unit -> 'K) : Lookup<'K, bool> =
        let comparer = HashIdentity.Structural<'K>
        createLookup (fun (s: 'K) (k: 'K) -> comparer.Equals (s, k)) (fun prev next -> [ prev; next ]) source

    /// <summary>
    /// A memo holding <c>select ()</c>. While the inner value stays equal under the graph's equality policy, the memo
    /// keeps its previous <c>Some</c> instance and its dependents stay asleep.
    /// </summary>
    /// <remarks>
    /// A hand-written <c>createMemo</c> returning <c>Some</c> allocates a new wrapper per run and, on .NET, wakes its
    /// dependents on every run. A re-run that selects the same instance performs no inner comparison. Under
    /// <c>StructuralPolicy</c> the memo's own cutoff adds one deep compare of the inner value per re-run.
    /// </remarks>
    /// <exception cref="T:System.InvalidOperationException">Called inside a pure body, such as a <c>createMemo</c> body.</exception>
    /// <example>
    /// <code lang="fsharp">
    /// let todo3 = createOptionMemo (fun () -> state.Value.Todos |> List.tryFind (fun t -> t.Id = 3))
    /// </code>
    /// </example>
    let createOptionMemo (select: unit -> 'A option) : Memo<'A option> =
        let graph = Graph.Current
        let equal = graph.Options.Equality.Comparer<'A>()

        let compute (last: 'A option voption) =
            let next = select ()

            match last, next with
            | ValueSome (Some previous as kept), Some current when
                Identity.same previous current
                || equal.Equals (previous, current)
                ->
                kept
            | _ -> next

        Memo.Create (graph, compute, ScopeMode.Pure)

    /// <summary>
    /// A value seeded by <c>seed</c> that accepts local edits. An edit is dropped once the seed publishes an unequal value.
    /// </summary>
    /// <remarks>
    /// <c>seed</c> is a pure derivation, as <c>createMemo</c>'s is, and receives the seed's own last published value,
    /// <c>ValueNone</c> before the first. Costs one signal and two memos.
    /// </remarks>
    /// <exception cref="T:System.InvalidOperationException">Called inside a pure body, such as a <c>createMemo</c> body.</exception>
    /// <example>
    /// <code lang="fsharp">
    /// let quantity = createEditable (fun _ -> order.Value.Quantity)
    /// quantity.Value &lt;- 3        // local edit
    /// order.Value &lt;- reloaded    // an unequal quantity drops the edit
    /// </code>
    /// </example>
    let createEditable (seed: 'T voption -> 'T) : Editable<'T> =
        Editable<'T>(Graph.Current, seed, false)

    /// <summary>
    /// A value seeded by <c>seed</c> that accepts local edits. An edit stays in force until <c>Reset</c>, whatever the
    /// seed publishes.
    /// </summary>
    /// <remarks>
    /// <c>seed</c> is a pure derivation, as <c>createMemo</c>'s is, and receives the seed's own last published value,
    /// <c>ValueNone</c> before the first. <c>Upstream</c> reads the seed while an edit is in force.
    /// </remarks>
    /// <exception cref="T:System.InvalidOperationException">Called inside a pure body, such as a <c>createMemo</c> body.</exception>
    /// <example>
    /// <code lang="fsharp">
    /// let name = createDraft (fun _ -> user.Value.Name)
    /// name.Value &lt;- "Ada"   // kept when user changes
    /// name.Reset ()          // reads user.Value.Name again
    /// </code>
    /// </example>
    let createDraft (seed: 'T voption -> 'T) : Editable<'T> =
        Editable<'T>(Graph.Current, seed, true)

/// <summary>Writes to a <c>Signal</c> computed from its current value.</summary>
[<RequireQualifiedAccess>]
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Signal =
    /// <summary>
    /// Writes <c>f signal.Peek</c> to <c>signal</c>. Readers wake only when the result differs from the current value
    /// under the signal's cutoff comparer.
    /// </summary>
    /// <remarks>The read is untracked.</remarks>
    /// <exception cref="T:System.InvalidOperationException">Called off the graph's thread under a guarded graph.</exception>
    /// <example>
    /// <code lang="fsharp">
    /// Signal.update store (fun s -> { s with Owner.Home.City = "Oslo" })
    /// </code>
    /// </example>
    let update (signal: Signal<'S>) (f: 'S -> 'S) : unit =
        signal.Value <- f signal.Peek

/// <summary>Keyed copy-and-update over F# lists.</summary>
[<RequireQualifiedAccess>]
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module List =
    /// <summary>Replaces the first element <c>x</c> of <c>xs</c> whose key equals <c>key</c> with <c>f x</c>.</summary>
    /// <returns>
    /// <c>xs</c> itself when no key matches or <c>f x</c> is <c>x</c> (an equal value for a value type or a string, NaN included);
    /// otherwise a new list whose tail after the match is the tail of <c>xs</c>.
    /// </returns>
    /// <remarks>
    /// Keys compare under <c>HashIdentity.Structural</c>, the comparer of projection row identity. Allocates one cell
    /// per element up to and including the match, plus a scratch array of the prefix.
    /// </remarks>
    /// <example>
    /// <code lang="fsharp">
    /// todos |> List.updateBy (fun t -> t.Id) 3 (fun t -> { t with Title = "three" })
    /// </code>
    /// </example>
    let updateBy (keyOf: 'T -> 'K) (key: 'K) (f: 'T -> 'T) (xs: 'T list) : 'T list =
        let keys = HashIdentity.Structural<'K>
        let mutable rest = xs
        let mutable index = 0

        while not rest.IsEmpty
              && not (keys.Equals (keyOf rest.Head, key)) do
            rest <- rest.Tail
            index <- index + 1

        match rest with
        | [] -> xs
        | x :: tail ->
            let y = f x

            if Identity.unchanged x y then
                xs
            else
                let prefix = Array.zeroCreate index
                let mutable walk = xs

                for i in 0 .. index - 1 do
                    prefix[i] <- walk.Head
                    walk <- walk.Tail

                let mutable result = y :: tail

                for i in index - 1 .. -1 .. 0 do
                    result <- prefix[i] :: result

                result

/// <summary>Keyed copy-and-update over arrays.</summary>
[<RequireQualifiedAccess>]
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Array =
    /// <summary>Replaces the first element <c>x</c> of <c>xs</c> whose key equals <c>key</c> with <c>f x</c>.</summary>
    /// <returns>
    /// <c>xs</c> itself when no key matches or <c>f x</c> is <c>x</c> (an equal value for a value type or a string, NaN included);
    /// otherwise a copy of <c>xs</c>.
    /// </returns>
    /// <remarks>Keys compare under <c>HashIdentity.Structural</c>, the comparer of projection row identity.</remarks>
    /// <example>
    /// <code lang="fsharp">
    /// todos |> Array.updateBy (fun t -> t.Id) 3 (fun t -> { t with Title = "three" })
    /// </code>
    /// </example>
    let updateBy (keyOf: 'T -> 'K) (key: 'K) (f: 'T -> 'T) (xs: 'T array) : 'T array =
        let keys = HashIdentity.Structural<'K>
        let mutable index = 0

        while index < xs.Length
              && not (keys.Equals (keyOf xs[index], key)) do
            index <- index + 1

        if index = xs.Length then
            xs
        else
            let y = f xs[index]

            if Identity.unchanged xs[index] y then
                xs
            else
                let copy = Array.copy xs
                copy[index] <- y
                copy

[<AutoOpen>]
module GraphExtensions =
    type Graph with

        /// <summary>
        /// Activates the graph, runs <c>body</c>, and restores the previous ambient
        /// graph — the whole of the common case in one call.
        /// </summary>
        /// <remarks>
        /// The graph is not disposed afterwards: it outlives the call, which is
        /// what makes the effects created inside <c>body</c> keep running.
        /// </remarks>
        member this.Run(body: unit -> 'T) : 'T =
            use _ = this.Activate ()
            body ()
