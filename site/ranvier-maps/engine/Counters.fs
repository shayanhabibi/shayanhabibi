namespace Ranvier

#if RANVIER_COUNTERS
#if FABLE_COMPILER
/// <summary>
/// A JavaScript number: <c>int64</c> compiles to <c>bigint</c>, and every <c>bigint</c>
/// increment allocates.
/// </summary>
type internal Tally = float
#else
type internal Tally = int64
#endif

module internal Tallies =
    let one: Tally = LanguagePrimitives.GenericOne
    let zero: Tally = LanguagePrimitives.GenericZero

    let names =
        [|
            "SignalsCreated"
            "MemosCreated"
            "EffectsCreated"
            "OwnersCreated"
            "EdgesAdded"
            "EdgesRemoved"
            "ObserverInserts"
            "ObserverRemoves"
            "MemoRecomputes"
            "EffectRuns"
            "Flushes"
        |]

    /// <summary>
    /// One count per entry of <c>names</c>, at the same index.
    /// </summary>
    let counts: Tally[] = Array.create names.Length zero

    let inline bump (index: int) =
#if FABLE_COMPILER
        counts[index] <- counts[index] + one
#else
        System.Threading.Interlocked.Increment &counts[index]
        |> ignore
#endif

    let inline read (index: int) : int64 =
#if FABLE_COMPILER
        int64 counts[index]
#else
        System.Threading.Interlocked.Read &counts[index]
#endif

/// <summary>
/// Process-wide counts of graph operations, compiled in by the MSBuild
/// property <c>RanvierCounters=true</c>. On .NET each count is an atomic increment,
/// exact while several threads mutate their own graphs.
/// </summary>
[<AbstractClass; Sealed>]
type Counters =
    static member internal SignalCreated() =
        Tallies.bump 0

    static member internal MemoCreated() =
        Tallies.bump 1

    static member internal EffectCreated() =
        Tallies.bump 2

    static member internal OwnerCreated() =
        Tallies.bump 3

    static member internal EdgeAdded() =
        Tallies.bump 4

    static member internal EdgeRemoved() =
        Tallies.bump 5

    static member internal ObserverInserted() =
        Tallies.bump 6

    static member internal ObserverRemoved() =
        Tallies.bump 7

    static member internal MemoRecomputed() =
        Tallies.bump 8

    static member internal EffectRan() =
        Tallies.bump 9

    static member internal Flushed() =
        Tallies.bump 10

    /// <summary>
    /// Sets every count to zero.
    /// </summary>
    static member Reset() =
        for i in 0 .. Tallies.counts.Length - 1 do
            Tallies.counts[i] <- Tallies.zero

    /// <summary>
    /// Every count as a name-value pair, in a fixed order. <c>Owner</c> counts every
    /// scope: explicit owners, roots and the scopes of computation runs.
    /// <c>EdgesAdded</c> and <c>EdgesRemoved</c> count entries in a computation's source
    /// list; <c>ObserverInserts</c> and <c>ObserverRemoves</c> count entries in a
    /// source's observer set. A re-run that reads its sources in the same order
    /// leaves all four unchanged. <c>Flushes</c> counts drains of the effect queue.
    /// </summary>
    static member Snapshot() : (string * int64)[] =
        Array.init Tallies.names.Length (fun i -> Tallies.names[i], Tallies.read i)
#endif
