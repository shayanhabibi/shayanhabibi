namespace Ranvier

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks

#if FABLE_COMPILER
open Fable.Core
#else
open System.Collections.Concurrent
#endif

/// <summary>Primitive implementations that differ between .NET and JavaScript.</summary>
/// <remarks><para>
/// <c>docs/.ai/NOTES-fable-target.md</c> lists the conditionals outside this file.
/// <c>PlatformDispatcher.fs</c> holds the <c>SynchronizationContext</c> dispatcher in a
/// file of its own
/// </para></remarks>
module internal Platform =
#if FABLE_COMPILER
    /// <summary>
    /// Constant under Fable. With one thread the comparison is always true,
    /// which is exactly what <c>ThreadAffinity.Guarded</c> should conclude there.
    /// </summary>
    let inline currentThreadId () = 0
#else
    /// <summary>
    /// The calling thread's id, used only to compare a caller against the
    /// thread that constructed the graph.
    /// </summary>
    let inline currentThreadId () =
        Environment.CurrentManagedThreadId
#endif

    /// <summary>
    /// A task that has already finished, used as the seed of the queue a
    /// <c>FlightPolicy.Queue</c> node chains onto.
    /// </summary>
    let completedTask: Task =
#if FABLE_COMPILER
        // Fable's `Task` is a `Promise` with a five-function surface that does not
        // include `CompletedTask`, so this resolves one instead.
        Task.FromResult () :> Task
#else
        Task.CompletedTask
#endif

    /// <summary>
    /// How a flight ended.
    /// </summary>
    [<Struct; NoComparison; NoEquality>]
    type FlightOutcome<'T> =
        | Completed of value: 'T
        /// <summary>
        /// The exception the body threw, without its transport wrapper.
        /// </summary>
        | Faulted of error: exn
        /// <summary>
        /// The flight was cancelled. Under Fable, a rejection carrying fable-library's
        /// <c>OperationCanceledException</c>.
        /// </summary>
        | Canceled of reason: exn

#if FABLE_COMPILER
    [<Emit("$0.then($1, $2)")>]
    let private thenBoth (task: Task<'T>) (onValue: 'T -> 'U) (onReason: exn -> 'U) : Task<'U> = jsNative

    [<Emit("$0.then($1)")>]
    let private thenValue (task: Task<'T>) (onValue: 'T -> Task) : Task = jsNative

    [<Emit("$0.then($1, $1)")>]
    let private thenEither (tail: Task) (next: unit -> Task) : Task = jsNative

    [<Emit("String($0)")>]
    let private jsString (value: obj) : string = jsNative

    /// <summary>
    /// True when <c>value</c> is an object carrying a member called <c>name</c>. Stands in for an interface type
    /// test, which Fable compiles to false; interface members compile to plain members of that name.
    /// </summary>
    [<Emit("($0 != null && typeof $0 === 'object' && $1 in $0)")>]
    let hasMember (value: obj) (name: string) : bool = jsNative

    /// <summary>
    /// Sets the <c>InnerException</c> of <c>error</c> to <c>inner</c>. Fable's exception types take no inner
    /// exception in their constructors.
    /// </summary>
    [<Emit("$0.innerException = $1")>]
    let setInner (error: exn) (inner: exn) : unit = jsNative

    /// <summary>
    /// The outcome of a rejection. A reason that is not an exception fails with one whose
    /// message is the reason as a string.
    /// </summary>
    let private rejected<'T> (reason: exn) : FlightOutcome<'T> =
        match box reason with
        | null -> FlightOutcome.Faulted (Exception "The flight was rejected without a reason.")
        | :? OperationCanceledException -> FlightOutcome.Canceled reason
        | :? exn -> FlightOutcome.Faulted reason
        | other -> FlightOutcome.Faulted (Exception (jsString other))
#else
    /// <summary>The exception a cancelled task throws when awaited.</summary>
    let private cancellation (t: Task) : exn =
        try
            t.GetAwaiter().GetResult()
            OperationCanceledException () :> exn
        with ex ->
            ex

    let private settled (t: Task<'T>) =
        if t.IsCanceled then
            FlightOutcome.Canceled (cancellation t)
        elif t.IsFaulted then
            FlightOutcome.Faulted (t.Exception.GetBaseException ())
        else
            FlightOutcome.Completed t.Result
#endif

    /// <summary>
    /// Calls <c>settle</c> with the outcome of <c>flight</c>: inline on the completing thread
    /// on .NET, on a microtask under Fable. The returned task completes when
    /// <c>settle</c> returns.
    /// </summary>
    let whenSettled (flight: Task<'T>) (settle: FlightOutcome<'T> -> unit) : Task =
#if FABLE_COMPILER
        thenBoth flight (fun v -> settle (FlightOutcome.Completed v)) (fun reason -> settle (rejected reason))
#else
        flight.ContinueWith (Action<Task<'T>>(fun t -> settle (settled t)), TaskContinuationOptions.ExecuteSynchronously)
#endif

    /// <summary>
    /// The outcome of <c>flight</c>, as a task that always completes successfully.
    /// Observes the flight at once, so a rejection is handled even when the
    /// outcome is read later.
    /// </summary>
    let outcomeOf (flight: Task<'T>) : Task<FlightOutcome<'T>> =
#if FABLE_COMPILER
        thenBoth flight FlightOutcome.Completed rejected
#else
        flight.ContinueWith (Func<Task<'T>, FlightOutcome<'T>>(settled), TaskContinuationOptions.ExecuteSynchronously)
#endif

    /// <summary>
    /// Calls <c>settle</c> with <c>outcome</c> once it is available. The returned task completes when the task
    /// <c>settle</c> returns has completed.
    /// </summary>
    let apply (outcome: Task<FlightOutcome<'T>>) (settle: FlightOutcome<'T> -> Task) : Task =
#if FABLE_COMPILER
        thenValue outcome settle
#else
        outcome.ContinueWith(Func<Task<FlightOutcome<'T>>, Task>(fun t -> settle t.Result), TaskContinuationOptions.ExecuteSynchronously).Unwrap()
#endif

    /// <summary>
    /// A task that completes once <c>tail</c> has completed in any state and the task
    /// <c>next</c> returns has completed.
    /// </summary>
    let after (tail: Task) (next: unit -> Task) : Task =
#if FABLE_COMPILER
        thenEither tail next
#else
        tail.ContinueWith(Func<Task, Task>(fun _ -> next ()), TaskContinuationOptions.ExecuteSynchronously).Unwrap()
#endif

#if FABLE_COMPILER
    /// <summary>A task completed by hand, once.</summary>
    type Deferred<'T> = { Task: Task<'T>; Resolve: 'T -> unit }

    [<Emit("new Promise($0)")>]
    let private promiseOf (executor: ('T -> unit) -> unit) : Task<'T> = jsNative

    /// <summary>A task that completes when <c>resolve</c> is called. Its awaiters resume on a later microtask.</summary>
    let deferred<'T> () : Deferred<'T> =
        let resolver = ref Unchecked.defaultof<'T -> unit>
        let task = promiseOf (fun resolve -> resolver.Value <- resolve)

        {
            Task = task
            Resolve = resolver.Value
        }

    /// <summary>The task a <c>Deferred</c> completes.</summary>
    let deferredTask (d: Deferred<'T>) : Task<'T> = d.Task

    /// <summary>Completes <c>d</c> with <c>value</c>. A second call has no effect.</summary>
    let resolve (d: Deferred<'T>) (value: 'T) =
        d.Resolve value
#else
    /// <summary>A task completed by hand, once.</summary>
    type Deferred<'T> = TaskCompletionSource<'T>

    /// <summary>
    /// A task that completes when <c>resolve</c> is called. Its awaiters resume on the thread pool, never inside
    /// <c>resolve</c>.
    /// </summary>
    let deferred<'T> () : Deferred<'T> =
        TaskCompletionSource<'T> TaskCreationOptions.RunContinuationsAsynchronously

    /// <summary>The task a <c>Deferred</c> completes.</summary>
    let deferredTask (d: Deferred<'T>) : Task<'T> = d.Task

    /// <summary>Completes <c>d</c> with <c>value</c>. A second call has no effect.</summary>
    let resolve (d: Deferred<'T>) (value: 'T) =
        d.TrySetResult value |> ignore
#endif

    /// <summary>A task already completed with <c>value</c>.</summary>
    let completedWith (value: 'T) : Task<'T> =
        Task.FromResult value

#if !FABLE_COMPILER
    /// <summary>The synchronous part of a flight body: live until the body returns.</summary>
    [<AllowNullLiteral>]
    type private FlightFrame(enter: obj -> bool, leave: obj -> unit) =
        member val Live = true with get, set

        member this.Enter() =
            enter this

        member this.Leave() =
            leave this

    /// <summary>
    /// Flows with the execution context into every continuation of a flight
    /// body. The handler reports each switch into and out of a dead frame.
    /// </summary>
    /// <remarks>
    /// A switch between two dead frames is either a continuation nested inside another, or the
    /// end of the inner one. An <c>Enter</c> that returns true marks the nesting, and the outer
    /// frame stays entered.
    /// </remarks>
    let private flightFrame =
        AsyncLocal<FlightFrame>(fun args ->
            if args.ThreadContextChanged then
                let left = args.PreviousValue
                let entered = args.CurrentValue

                let nested =
                    not (isNull entered)
                    && not entered.Live
                    && entered.Enter ()

                if not nested && not (isNull left) && not left.Live then
                    left.Leave ())
#endif

    /// <summary>Runs the synchronous part of a flight body.</summary>
    /// <remarks>
    /// On .NET, <c>enter</c> runs before, and <c>leave</c> after, each continuation of the body,
    /// on the thread the continuation starts on. Both receive the body's frame; <c>enter</c>
    /// returns false when the frame is already entered, as on the return from a nested
    /// continuation. Under Fable neither runs.
    /// </remarks>
    let runFlightBody (enter: obj -> bool) (leave: obj -> unit) (body: unit -> 'T) : 'T =
#if FABLE_COMPILER
        ignore enter
        ignore leave
        body ()
#else
        let frame = FlightFrame (enter, leave)
        let previous = flightFrame.Value
        flightFrame.Value <- frame

        try
            body ()
        finally
            frame.Live <- false
            flightFrame.Value <- previous
#endif

    /// <summary>
    /// True when the runtime type of <c>ex</c> is exactly
    /// <c>InvalidOperationException</c>. Under Fable it is true for every
    /// exception; a caller that needs an exact match compares the message too.
    /// </summary>
    let inline isExactInvalidOperation (ex: exn) =
#if FABLE_COMPILER
        ignore ex
        true
#else
        ex.GetType () = typeof<InvalidOperationException>
#endif

    /// <summary>
    /// A stored failure, as accepted by <c>rethrowStored</c>: an <c>ExceptionDispatchInfo</c> on .NET, the exception
    /// itself under Fable.
    /// </summary>
#if FABLE_COMPILER
    type CapturedFailure = exn
#else
    type CapturedFailure = System.Runtime.ExceptionServices.ExceptionDispatchInfo
#endif

    /// <summary>
    /// The capture of <c>error</c>: <c>held</c> when <c>held</c> already captures the same instance, otherwise a
    /// new capture of <c>error</c> with its current stack trace.
    /// </summary>
    /// <remarks>
    /// A rethrow of a capture will carry the captured frames followed by the rethrowing reader's frames only,
    /// however many reads preceded it.
    /// </remarks>
    let captureFailure (held: CapturedFailure) (error: exn) : CapturedFailure =
#if FABLE_COMPILER
        ignore held
        error
#else
        if
            not (isNull held)
            && obj.ReferenceEquals (held.SourceException, error)
        then
            held
        else
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture error
#endif

    /// <summary>
    /// Raises the captured exception instance. On .NET its <c>StackTrace</c> keeps the frames of the original
    /// throw site, followed by the frames of this rethrow.
    /// </summary>
    let inline rethrowStored (captured: CapturedFailure) : 'T =
#if FABLE_COMPILER
        raise captured
#else
        captured.Throw ()
        Unchecked.defaultof<'T>
#endif

    /// <summary>
    /// Work handed to the graph from another thread, waiting for the graph's
    /// own thread to come and run it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>ConcurrentQueue</c> natively because enqueue is the only lock-free option
    /// in the BCL — every Channel and MailboxProcessor takes a lock on every
    /// write. See docs/.ai/RESEARCH-loony-synchronization.md §4.2. Under Fable
    /// nothing can enqueue concurrently, so the ordinary queue is not a
    /// weakening.
    /// </para>
    /// <para>
    /// <c>TryTake</c> returns a voption rather than taking a <c>byref</c>, because a
    /// <c>byref</c> out-parameter is one of the things Fable handles worst, and this
    /// is the cold path by construction — a voption of a reference costs
    /// nothing anyway.
    /// </para>
    /// </remarks>
    type Inbox<'T>() =
#if FABLE_COMPILER
        let queue = Queue<'T>()

        member _.Enqueue(item: 'T) =
            queue.Enqueue item

        member _.Count = queue.Count

        member _.IsEmpty = queue.Count = 0

        member _.TryTake() : 'T voption =
            if queue.Count = 0 then
                ValueNone
            else
                ValueSome (queue.Dequeue ())
#else
        let queue = ConcurrentQueue<'T>()

        member _.Enqueue(item: 'T) =
            queue.Enqueue item

        member _.Count = queue.Count

        member _.IsEmpty = queue.IsEmpty

        member _.TryTake() : 'T voption =
            let mutable item = Unchecked.defaultof<'T>

            if queue.TryDequeue &item then ValueSome item else ValueNone
#endif

    /// <summary>
    /// The dispatcher a graph gets when its options name none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Resolved per graph rather than once for the process: a static default
    /// would capture whichever synchronisation context happened to be current
    /// the first time it was touched.
    /// </para>
    /// <para>
    /// Under Fable there is no context to capture and no thread to marshal to,
    /// so a settle arriving from a promise continuation is already on the only
    /// thread there is. <c>ImmediateDispatcher</c> is then not a fallback but the
    /// right answer.
    /// </para>
    /// </remarks>
    let defaultDispatcher () : IGraphDispatcher =
#if FABLE_COMPILER
        ImmediateDispatcher () :> IGraphDispatcher
#else
        match SynchronizationContext.Current with
        | null -> ManualDispatcher () :> IGraphDispatcher
        | ctx -> SynchronizationContextDispatcher ctx :> IGraphDispatcher
#endif

    /// <summary>
    /// Drops the reference in <c>slots[index]</c>, where the caller is done with it
    /// but the list itself is being reused rather than cleared.
    /// </summary>
    /// <remarks>
    /// A no-op under Fable, and the one platform difference here that is about
    /// cost rather than capability. Storing null beside object references in a
    /// JavaScript array is something V8 charges for: measured on the scheduler
    /// queue it was ~10% of a 64-observer write, against a retention window
    /// bounded by the longest queue seen so far and closed by the next turn
    /// that reaches the same length. That is the trade Solid makes too. On
    /// .NET the store is a covariant array write, costs nothing measurable,
    /// and keeps a disposed node from being held alive by a spent slot.
    /// </remarks>
    let inline releaseSlot (slots: ResizeArray<'T>) (index: int) =
#if FABLE_COMPILER
        ignore slots
        ignore index
#else
        slots[index] <- Unchecked.defaultof<'T>
#endif

    /// <summary>
    /// <c>slots[index]</c>, without fable-library's bounds check.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Fable compiles an array read to <c>item(i, xs)</c> — a call into
    /// fable-library that re-checks a bound the caller has already
    /// established — while an array write of the same shape compiles to a
    /// raw <c>xs[i] = v</c>. On the scheduler queue, the observer list and the
    /// source list together, that asymmetry was 8.7% of a write-heavy profile
    /// under node, all of it in three loops that had just tested the index
    /// they were about to read.
    /// </para>
    /// <para>
    /// On .NET this is the indexer, which the JIT bounds-checks and often
    /// elides; nothing is given up there to gain the JavaScript.
    /// </para>
    /// <para>
    /// Every call site must have established <c>index &lt; count</c> itself. That is
    /// the trade, and it is why this is <c>internal</c>.
    /// </para>
    /// </remarks>
#if FABLE_COMPILER
    [<Emit("$1[$0]")>]
    let itemAt (index: int) (slots: 'T[]) : 'T = jsNative
#else
    let inline itemAt (index: int) (slots: 'T[]) =
        slots[index]
#endif

    /// <summary>
    /// <c>slots[index]</c> for a <c>ResizeArray</c>, without the bounds check. Same
    /// contract, same reason, same caller obligation as <c>itemAt</c>.
    /// </summary>
#if FABLE_COMPILER
    [<Emit("$1[$0]")>]
    let entryAt (index: int) (slots: ResizeArray<'T>) : 'T = jsNative
#else
    let inline entryAt (index: int) (slots: ResizeArray<'T>) =
        slots[index]
#endif

    /// <summary>
    /// A position index keyed by object identity. Under Fable, removing a key
    /// releases the key's entry. On .NET the table keeps its peak capacity.
    /// </summary>


#if FABLE_COMPILER
    type RefIndex<'K> = JS.Map<'K, int>

    [<Emit("new Map()")>]
    let createRefIndex<'K> (capacity: int) : RefIndex<'K> = jsNative

    /// <summary>
    /// The position of <c>key</c>, or -1.
    /// </summary>
    [<Emit("($0.get($1) ?? -1)")>]
    let refIndexFind (index: RefIndex<'K>) (key: 'K) : int = jsNative

    [<Emit("$0.set($1, $2)")>]
    let refIndexSet (index: RefIndex<'K>) (key: 'K) (position: int) : unit = jsNative

    [<Emit("$0.delete($1)")>]
    let refIndexRemove (index: RefIndex<'K>) (key: 'K) : unit = jsNative
#else
    type RefIndex<'K> = Dictionary<'K, int>

    let inline createRefIndex<'K when 'K: not struct> (capacity: int) : RefIndex<'K> =
        Dictionary<'K, int>(capacity, HashIdentity.Reference)

    /// <summary>
    /// The position of <c>key</c>, or -1.
    /// </summary>
    let inline refIndexFind (index: RefIndex<'K>) (key: 'K) =
        match index.TryGetValue key with
        | true, i -> i
        | _ -> -1

    let inline refIndexSet (index: RefIndex<'K>) (key: 'K) (position: int) =
        index[key] <- position

    let inline refIndexRemove (index: RefIndex<'K>) (key: 'K) =
        index.Remove key |> ignore
#endif

#if FABLE_COMPILER
    /// <summary>
    /// Whether <c>key</c> is a JavaScript primitive, whose native <c>Map</c> key equality
    /// agrees with structural equality.
    /// </summary>
    [<Emit("(typeof $0 !== 'object' || $0 === null)")>]
    let primitiveKey (key: 'K) : bool = jsNative

    [<Emit("new Map()")>]
    let newJsMap<'K, 'V> () : JS.Map<'K, 'V> = jsNative

    [<Emit("new Set()")>]
    let newJsSet<'K> () : JS.Set<'K> = jsNative

    /// <summary>
    /// The value at <c>key</c>, or null.
    /// </summary>
    [<Emit("($0.get($1) ?? null)")>]
    let jsMapFind (map: JS.Map<'K, 'V>) (key: 'K) : 'V = jsNative

    [<Emit("(() => { for (const v of $0.values()) if ($1(v)) return true; return false })()")>]
    let jsMapExists (map: JS.Map<'K, 'V>) (predicate: 'V -> bool) : bool = jsNative
#endif

    /// <summary>
    /// A map under structural key equality. Holds every key of <c>'K</c>, <c>None</c>
    /// and <c>()</c> included.
    /// </summary>
    [<Sealed>]
    type KeyMap<'K, 'V when 'K: equality>() =
#if FABLE_COMPILER
        let prims = newJsMap<'K, 'V>()
        let objs = Dictionary<'K, 'V>(HashIdentity.Structural)

        member _.Prims = prims
        member _.Objs = objs
        member _.Count = prims.size + objs.Count

        /// <summary>
        /// The value at <c>key</c>, or <c>Unchecked.defaultof&lt;'V></c> for an absent key.
        /// </summary>
        member _.Find(key: 'K) : 'V =
            if primitiveKey key then
                jsMapFind prims key
            else
                match objs.TryGetValue key with
                | true, value -> value
                | _ -> Unchecked.defaultof<'V>

        member _.Set(key: 'K, value: 'V) =
            if primitiveKey key then
                prims.set (key, value) |> ignore
            else
                objs[key] <- value

        member _.Remove(key: 'K) =
            if primitiveKey key then
                prims.delete key |> ignore
            else
                objs.Remove key |> ignore

        member _.Clear() =
            prims.clear ()
            objs.Clear ()

        /// <summary>
        /// Visits every entry: primitive keys first, then object keys.
        /// </summary>
        member inline this.Iterate([<InlineIfLambda>] f: 'K -> 'V -> unit) =
            this.Prims.forEach (fun value key _ -> f key value)

            for pair in this.Objs do
                f pair.Key pair.Value

        /// <summary>
        /// Whether <c>predicate</c> holds for some value. Stops at the first match.
        /// </summary>
        member _.Exists(predicate: 'V -> bool) =
            let mutable found = jsMapExists prims predicate
            let mutable e = objs.GetEnumerator ()

            while not found && e.MoveNext () do
                found <- predicate e.Current.Value

            found
#else
        let table = Dictionary<'K, 'V>(HashIdentity.Structural)
        let mutable hasNull = false
        let mutable nullValue = Unchecked.defaultof<'V>

        member _.Table = table
        member _.HasNull = hasNull
        member _.NullValue = nullValue
        member _.Count = if hasNull then table.Count + 1 else table.Count

        /// <summary>
        /// The value at <c>key</c>, or <c>Unchecked.defaultof&lt;'V></c> for an absent key.
        /// </summary>
        member _.Find(key: 'K) : 'V =
            if isNull (box key) then
                nullValue
            else
                match table.TryGetValue key with
                | true, value -> value
                | _ -> Unchecked.defaultof<'V>

        member _.Set(key: 'K, value: 'V) =
            if isNull (box key) then
                hasNull <- true
                nullValue <- value
            else
                table[key] <- value

        member _.Remove(key: 'K) =
            if isNull (box key) then
                hasNull <- false
                nullValue <- Unchecked.defaultof<'V>
            else
                table.Remove key |> ignore

        member _.Clear() =
            hasNull <- false
            nullValue <- Unchecked.defaultof<'V>
            table.Clear ()

        /// <summary>
        /// Visits every entry: the null key first, then the rest.
        /// </summary>
        member inline this.Iterate([<InlineIfLambda>] f: 'K -> 'V -> unit) =
            if this.HasNull then
                f Unchecked.defaultof<'K> this.NullValue

            for pair in this.Table do
                f pair.Key pair.Value

        /// <summary>
        /// Whether <c>predicate</c> holds for some value. Stops at the first match.
        /// </summary>
        member inline this.Exists([<InlineIfLambda>] predicate: 'V -> bool) =
            let mutable found = this.HasNull && predicate this.NullValue
            let mutable e = this.Table.GetEnumerator ()

            while not found && e.MoveNext () do
                found <- predicate e.Current.Value

            found
#endif

    /// <summary>
    /// A set under structural equality. Holds every value of <c>'K</c>, <c>None</c> and
    /// <c>()</c> included.
    /// </summary>
    [<Sealed>]
    type KeySet<'K when 'K: equality>() =
#if FABLE_COMPILER
        let prims = newJsSet<'K>()
        let objs = HashSet<'K>(HashIdentity.Structural)

        member _.Prims = prims
        member _.Objs = objs
        member _.Count = prims.size + objs.Count

        member _.Contains(key: 'K) =
            if primitiveKey key then
                prims.has key
            else
                objs.Contains key

        /// <summary>
        /// Adds <c>key</c>. Returns false if it was already present.
        /// </summary>
        member _.Add(key: 'K) =
            if primitiveKey key then
                if prims.has key then
                    false
                else
                    prims.add key |> ignore
                    true
            else
                objs.Add key

        member _.Remove(key: 'K) =
            if primitiveKey key then
                prims.delete key |> ignore
            else
                objs.Remove key |> ignore

        member _.Clear() =
            prims.clear ()
            objs.Clear ()

        /// <summary>
        /// Visits every key: primitive keys first, then object keys.
        /// </summary>
        member inline this.Iterate([<InlineIfLambda>] f: 'K -> unit) =
            this.Prims.forEach (fun key _ _ -> f key)

            for key in this.Objs do
                f key
#else
        let table = HashSet<'K>(HashIdentity.Structural)
        let mutable hasNull = false

        member _.Table = table
        member _.HasNull = hasNull
        member _.Count = if hasNull then table.Count + 1 else table.Count

        member _.Contains(key: 'K) =
            if isNull (box key) then hasNull else table.Contains key

        /// <summary>
        /// Adds <c>key</c>. Returns false if it was already present.
        /// </summary>
        member _.Add(key: 'K) =
            if isNull (box key) then
                let added = not hasNull
                hasNull <- true
                added
            else
                table.Add key

        member _.Remove(key: 'K) =
            if isNull (box key) then
                hasNull <- false
            else
                table.Remove key |> ignore

        member _.Clear() =
            hasNull <- false
            table.Clear ()

        /// <summary>
        /// Visits every key: the null key first, then the rest.
        /// </summary>
        member inline this.Iterate([<InlineIfLambda>] f: 'K -> unit) =
            if this.HasNull then
                f Unchecked.defaultof<'K>

            for key in this.Table do
                f key
#endif
