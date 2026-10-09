namespace Ranvier

open System
open System.Collections.Generic

/// <summary>
/// Pending channel flag axis.
/// </summary>
[<Flags>]
type Status =
    | None = 0uy
    | Pending = (1uy <<< 0)
    | Error = (1uy <<< 1)
    | Uninitialized = (1uy <<< 2)

/// <summary>
/// Whether the graph checks that mutation happens on the thread that owns it.
/// </summary>
type ThreadAffinity =
    /// <summary>
    /// Off-thread mutation raises.
    /// </summary>
    /// <remarks>
    /// <para>The check is a thread-id comparison against a field. Cheap.</para>
    /// <para>Converts the worst failure (two threads interleaving in an observer
    /// set, silently) into an exception that names it.</para>
    /// </remarks>
    | Guarded
    /// <summary>
    /// No check. For Fable, where there is one thread, and for a caller who has
    /// proven affinity some other way.
    /// </summary>
    | Unchecked
    /// <summary>
    /// Any thread may enter the graph, one at a time, on the synchronisation context current at construction. An entry
    /// from another context raises, and an entry while another thread is inside the graph raises.
    /// </summary>
    /// <remarks>
    /// For a Blazor Server circuit, whose work items run on its context but on varying pool threads. Work posted
    /// by a thread outside the graph, settles included, is queued to the dispatcher. Under Fable it behaves as
    /// <c>Unchecked</c>.
    /// </remarks>
    | Serialised

    /// <summary>The case name.</summary>
    override this.ToString() =
        match this with
        | Guarded -> "Guarded"
        | Unchecked -> "Unchecked"
        | Serialised -> "Serialised"

/// <summary>What an async memo does when a source changes while a flight is in progress.</summary>
type FlightPolicy =
    /// <summary>
    /// <para>Cancel superseded flight's token</para>
    /// <para>Discard superseded result</para>
    /// <para>Memo is pending until newest flight settles.</para>
    /// </summary>
    | CancelPrevious
    /// <summary>
    /// <para>Discard superseded result</para>
    /// <para>Memo is pending until newest flight settles.</para>
    /// </summary>
    /// <remarks>Does not cancel discarded flight</remarks>
    | KeepLatest
    /// <summary>
    /// Applies every flight's outcome, Ready or Failed, in start order, while later flights may still be in progress.
    /// </summary>
    /// <remarks>
    /// A new run makes the memo Pending until the next outcome is applied. While the newest run is suspended on a
    /// pending source, the memo stays Pending: a Ready outcome becomes the <c>Peek</c> value and a Failed outcome is discarded.
    /// </remarks>
    | Queue
    /// <summary>
    /// Lets the flight in progress finish, then runs the body once more against the current inputs. Every change during
    /// the flight folds into that one trailing run.
    /// </summary>
    /// <remarks>
    /// A change during a flight runs no body and starts no flight. When the flight settles with a change owed, a Ready
    /// outcome becomes the <c>Peek</c> value and the next <c>Previous</c>, a Failed outcome is discarded, and the memo
    /// stays Pending until the trailing flight settles. The trailing run starts at the memo's next read. A flight that
    /// settles with no change owed applies as under <c>KeepLatest</c>.
    /// </remarks>
    | FinishCurrent

    // AoT compat
    /// <summary>The case name.</summary>
    override this.ToString() =
        match this with
        | CancelPrevious -> "CancelPrevious"
        | KeepLatest -> "KeepLatest"
        | Queue -> "Queue"
        | FinishCurrent -> "FinishCurrent"

/// <summary>
/// Supplies the cutoff comparer for a node's value type.
/// </summary>
/// <remarks>
/// <para>
/// A policy rather than a comparer, because the comparer has to be typed. An
/// <c>IEqualityComparer&lt;obj></c> boxes on every write — 24 bytes of garbage per <c>int</c>
/// written, on the hottest path in the library, and a heap allocation where a
/// register comparison belongs. The generic member lets each node resolve its
/// own <c>IEqualityComparer&lt;'T></c> once, at construction.
/// </para>
/// <para>
/// A comparer that throws inside a computed node's cutoff fails the node with the comparer's exception, as a throwing body
/// would: a memo, boundary, projection row, fold row or lookup cell keeps its previous value, and its readers wake to
/// the failure. A boundary's <c>recover</c> does not see it. An effect split by <c>createEffectOn</c> records it and
/// skips <c>act</c>. A signal write raises it to the writer and leaves the value unchanged.
/// </para>
/// </remarks>
type IEqualityPolicy =
    abstract Comparer<'T> : unit -> IEqualityComparer<'T>

/// <summary>
/// A participant in the dependency graph.
/// </summary>
type INode =
    abstract Id: int
    abstract Status: Status

// ~20% can be gained by using a preallocated exception which the CLR specially treats
// by injecting 0 frames. Unfortunately, we cannot register our own preallocated exception,
// and using a known preallocated exception is poor API.
/// <summary>
/// Thrown by a transparent read of a pending source, to abort the reading
/// computation non-locally — including from inside helpers that know nothing
/// about reactivity. Carries the source so the consumer can record it.
/// </summary>
/// <remarks>
/// Deliberately NOT cached. Caching the exception object saves nothing on .NET:
/// fresh throws measured cheaper than cached ones on .NET 9, 10 and 11 alike.
/// </remarks>
exception NotReadyException of source: INode with
    // Does not prevent CLR from evaluating stack
    // override this.StackTrace = null
    /// <summary><c>NotReadyException</c>, then the source's <c>ToString</c> text, or <c>null</c>.</summary>
    override this.Message =
        match box this.source with
        | null -> "NotReadyException null"
        | source -> "NotReadyException " + source.ToString ()


/// <summary>
/// Wakes the graph when work arrives from another thread: <c>Post</c> receives a request to drain the graph's inbox.
/// </summary>
/// <remarks>
/// The work stays in the inbox, and the dispatcher chooses the thread the drain runs on. Under <c>Guarded</c>, a drain
/// off the owning thread raises <c>InvalidOperationException</c> to the <c>Graph.Dispatch</c> caller and the work stays
/// queued. Under <c>Unchecked</c>, a dispatcher that drains off-thread, such as <c>ImmediateDispatcher</c>, runs graph code there.
/// Under <c>Serialised</c>, a drain that finds another thread inside the graph leaves the work for that thread's exit to post.
/// </remarks>
type IGraphDispatcher =
    abstract Post: drain: Action -> unit

/// <summary>
/// Drains the inbox on the thread that posted to it.
/// </summary>
/// <remarks>
/// UNSAFE unless every write already arrives on one thread. It was the default
/// and quietly let a task completing on the thread pool mutate the graph
/// underneath the thread that owns it. Keep it for Fable, where there is one
/// thread and the question does not arise, and for tests that have arranged
/// single-threaded settling themselves.
/// </remarks>
type ImmediateDispatcher() =
    interface IGraphDispatcher with
        member _.Post drain =
            drain.Invoke ()

/// <summary>
/// Does nothing. The inbox fills, and is drained when the owning thread calls
/// <c>Graph.Pump()</c>.
/// </summary>
/// <remarks>
/// The right default for a console app, a server, or a test: there is no
/// ambient loop to post to, and inventing a thread to run one is a bigger
/// decision than a reactive library gets to make on its caller's behalf. The
/// cost is that off-thread work is visible only after a pump — a stall, which
/// is diagnosable, rather than a race, which is not.
/// </remarks>
type ManualDispatcher() =
    interface IGraphDispatcher with
        member _.Post _ = ()

/// <summary>
/// Reference identity, for reference types only.
/// </summary>
/// <remarks>
/// <c>box</c> here is a representation-preserving cast, not an allocation: this is
/// only ever instantiated at types that are already pointers.
/// </remarks>
type internal ReferenceComparer<'T>() =
    interface IEqualityComparer<'T> with
        member _.Equals(x, y) =
            Object.ReferenceEquals (box x, box y)

        member _.GetHashCode(x) =
            LanguagePrimitives.PhysicalHash (box x)

/// <summary>
/// IEEE equality for <c>float</c>, which is what <c>===</c> does and what
/// <c>EqualityComparer&lt;float>.Default</c> does not.
/// </summary>
/// <remarks>
/// <c>Double.Equals</c> says nan equals nan, so under it a nan write is cut off and
/// the update silently lost — on .NET only, since Fable's <c>===</c> propagates it.
/// That is the one primitive where the default comparer disagrees with
/// JavaScript, and it disagrees inside the policy whose whole contract is to be
/// <c>===</c>. <c>-0.0 = 0.0</c> is true either way, so the sign of zero stays
/// unobservable through the cutoff, exactly as it is in Solid.
/// </remarks>
type internal DoubleIeeeComparer() =
    interface IEqualityComparer<float> with
        member _.Equals(x, y) = x = y

        member _.GetHashCode(x) =
            x.GetHashCode ()

/// <summary>
/// <c>DoubleIeeeComparer</c> at single precision.
/// </summary>
type internal SingleIeeeComparer() =
    interface IEqualityComparer<float32> with
        member _.Equals(x, y) = x = y

        member _.GetHashCode(x) =
            x.GetHashCode ()

/// <summary>
/// Which comparer <c>JsIdentityPolicy</c> hands out, chosen once per closed type.
/// </summary>
/// <remarks>
/// Whole conditional bindings rather than an <c>#if</c> inside the expression: the
/// latter is not something fantomas can parse.
/// </remarks>
module internal ComparerResolution =
#if FABLE_COMPILER
    /// <summary>
    /// <c>===</c> at every type: value comparison at a primitive, reference comparison at every other.
    /// </summary>
    /// <remarks>
    /// Differs from the .NET binding at value types that compile to objects: structs, struct tuples,
    /// <c>DateTime</c>, <c>decimal</c> and <c>KeyValuePair</c> compare by reference. <c>Some x</c> erases to
    /// <c>x</c> and compares as <c>x</c> does.
    /// </remarks>
    let resolve<'T> () : IEqualityComparer<'T> =
        ReferenceComparer<'T>() :> IEqualityComparer<'T>
#else
    /// <summary>
    /// <c>===</c> at primitives, strings and reference types; value comparison at every other value type.
    /// </summary>
    let resolve<'T> () : IEqualityComparer<'T> =
        // The two casts run once per closed type, not per write: this is called
        // from a static field initialiser on a generic type.
        if typeof<'T> = typeof<float> then
            box (DoubleIeeeComparer ()) :?> IEqualityComparer<'T>
        elif typeof<'T> = typeof<float32> then
            box (SingleIeeeComparer ()) :?> IEqualityComparer<'T>
        elif
            typeof<'T>.IsValueType
            || typeof<'T> = typeof<string>
        then
            EqualityComparer<'T>.Default
        else
            ReferenceComparer<'T>() :> IEqualityComparer<'T>
#endif

/// <summary>
/// Resolved once per closed type. A static field on a generic type is
/// initialised per instantiation, so the <c>typeof</c> test runs once for <c>int</c>,
/// once for <c>string</c>, and never again.
/// </summary>
type internal JsComparer<'T>() =
    static member val Instance: IEqualityComparer<'T> = ComparerResolution.resolve<'T>()

/// <summary>
/// Equality matching JavaScript <c>===</c>, Solid's default cutoff: primitives compare by value, everything else by reference.
/// </summary>
/// <remarks>
/// On .NET every value type compares by value; under Fable a value type that compiles to an object compares by reference.
/// </remarks>
type JsIdentityPolicy() =
    interface IEqualityPolicy with
        member _.Comparer<'T>() =
            JsComparer<'T>.Instance

/// <summary>
/// Structural equality everywhere; two records with equal contents cut off.
/// </summary>
/// <remarks>
/// Costs deep comparison on every write and is wrong for types whose <c>Equals</c>
/// is expensive or surprising. Cuts off nan over nan on .NET and propagates it under Fable.
/// </remarks>
type StructuralPolicy() =
    interface IEqualityPolicy with
        member _.Comparer<'T>() =
            EqualityComparer<'T>.Default

/// <summary>
/// Construction-time policy/configuration.
/// </summary>
/// <remarks>
/// Contains target-divergent concerns rather than filling core with <c>#if FABLE_COMPILER</c>
/// directives. Native builds may want structural equality.
/// </remarks>
type GraphOptions =
    {
        Equality: IEqualityPolicy

        /// <summary>
        /// What an async memo does when a source changes
        /// while a flight/compute is in progress.
        /// </summary>
        FlightPolicy: FlightPolicy

        /// <summary>
        /// Whether the graph guards mutation on its owning thread.
        /// </summary>
        ThreadAffinity: ThreadAffinity

        /// <summary>
        /// <c>None</c> lets the graph choose between the ambient synchronisation context
        /// or the given manual pump otherwise.
        /// </summary>
        Dispatcher: IGraphDispatcher option
    }

    /// <summary>
    /// <c>===</c> equality, cancel-previous, thread-guarded.
    /// The semantics a Solid user would expect, plus the one .NET has to add
    /// </summary>
    static member Default =
        {
            Equality = JsIdentityPolicy ()
            FlightPolicy = CancelPrevious
            ThreadAffinity = Guarded
            Dispatcher = None
        }

    /// <summary>These options, with <c>equality</c> deciding when a value has moved.</summary>
    member this.WithEquality(equality: IEqualityPolicy) =
        { this with Equality = equality }

    /// <summary>These options, with <c>policy</c> deciding what a change during a flight does.</summary>
    member this.WithFlightPolicy(policy: FlightPolicy) =
        { this with FlightPolicy = policy }

    /// <summary>These options, with <c>affinity</c> deciding how a call off the graph's thread is treated.</summary>
    member this.WithThreadAffinity(affinity: ThreadAffinity) =
        { this with ThreadAffinity = affinity }

    /// <summary>These options, with <c>dispatcher</c> draining the graph's inbox.</summary>
    member this.WithDispatcher(dispatcher: IGraphDispatcher) =
        { this with
            Dispatcher = Some dispatcher
        }

    // AoT compat
    /// <summary>The record's fields, one per line, in F# record syntax.</summary>
    override this.ToString() =
        let text (value: obj) =
            match value with
            | null -> "null"
            | value -> value.ToString ()

        let dispatcher =
            match this.Dispatcher with
            | Some dispatcher -> "Some " + text dispatcher
            | None -> "None"

        "{ Equality = "
        + text this.Equality
        + "\n  FlightPolicy = "
        + this.FlightPolicy.ToString ()
        + "\n  ThreadAffinity = "
        + this.ThreadAffinity.ToString ()
        + "\n  Dispatcher = "
        + dispatcher
        + " }"

/// <summary>
/// Engine's internal read path and user's opt-in escape hatch.
/// </summary>
[<Struct; NoComparison>]
type Reading<'T> =
    | Ready of value: 'T
    | Pending
    /// <summary>The node failed. <c>error</c> is a non-null exception on both targets.</summary>
    | Failed of error: exn

    // AoT compat
    /// <summary>The case name and its payload</summary>
    override this.ToString() =
        let text (value: obj) =
            match value with
            | null -> "null"
            | :? string as s -> "\"" + s + "\""
            | value -> string value

        match this with
        | Ready value ->
            let payload = text (box value)

            if
                payload.Contains " "
                && "([{\"".IndexOf payload[0] < 0
            then
                "Ready (" + payload + ")"
            else
                "Ready " + payload
        | Pending -> "Pending"
        | Failed error -> "Failed " + text error

#if !FABLE_COMPILER
    /// <summary>True with the value when the reading is <c>Ready</c>.</summary>
    member this.TryGetValue([<System.Runtime.InteropServices.Out>] value: byref<'T>) : bool =
        match this with
        | Ready ready ->
            value <- ready
            true
        | _ ->
            value <- Unchecked.defaultof<'T>
            false

    /// <summary>True with the error when the reading is <c>Failed</c>.</summary>
    member this.TryGetError([<System.Runtime.InteropServices.Out>] error: byref<exn>) : bool =
        match this with
        | Failed failed ->
            error <- failed
            true
        | _ ->
            error <- null
            false
#endif

module internal TestAccess =
    [<assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Ranvier.Tests")>]
    [<assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Ranvier.Tests.Fable")>]
    [<assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Ranvier.Benchmarks")>]
    [<assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Ranvier.Counters")>]
    do ()
