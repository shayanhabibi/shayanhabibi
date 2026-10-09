namespace Ranvier

/// <summary>An edit to a positional list of keys, valid when applied in sequence.</summary>
[<RequireQualifiedAccess>]
type PositionalChange<'K> =
    /// <summary>Removes the key at <c>index</c>.</summary>
    | RemoveAt of Index: int
    /// <summary>Inserts <c>key</c> so that it lands at <c>index</c>.</summary>
    | InsertAt of Index: int * Key: 'K
    /// <summary>Removes the key at <c>oldIndex</c>, then inserts it at <c>newIndex</c> of the shortened list.</summary>
    | Move of OldIndex: int * NewIndex: int

module internal Positional =
    /// <summary>Adds <c>delta</c> at zero-based <c>index</c> of a Fenwick tree.</summary>
    let private addAt (tree: int[]) (index: int) (delta: int) =
        let mutable i = index + 1

        while i < tree.Length do
            tree[i] <- tree[i] + delta
            i <- i + (i &&& -i)

    /// <summary>The sum of a Fenwick tree's entries at zero-based indices below <c>index</c>.</summary>
    let private sumBelow (tree: int[]) (index: int) =
        let mutable i = index
        let mutable sum = 0

        while i > 0 do
            sum <- sum + tree[i]
            i <- i - (i &&& -i)

        sum

    /// <summary>
    /// Marks the members of one longest strictly increasing subsequence of <c>values</c>. O(N log N).
    /// </summary>
    let longestIncreasing (values: int[]) : bool[] =
        let n = values.Length
        let tails = Array.zeroCreate<int> n
        let previous = Array.create n -1
        let mutable length = 0

        for i in 0 .. n - 1 do
            let mutable lo = 0
            let mutable hi = length

            while lo < hi do
                let mid = (lo + hi) >>> 1

                if values[tails[mid]] < values[i] then
                    lo <- mid + 1
                else
                    hi <- mid

            if lo > 0 then
                previous[i] <- tails[lo - 1]

            tails[lo] <- i

            if lo = length then
                length <- length + 1

        let members = Array.zeroCreate<bool> n
        let mutable i = if length > 0 then tails[length - 1] else -1

        while i >= 0 do
            members[i] <- true
            i <- previous[i]

        members

    /// <summary>
    /// The edits that turn <c>previous</c> into <c>next</c>: removals from back to front, then the moves and inserts in
    /// the order of <c>next</c>. Both arrays hold distinct keys.
    /// </summary>
    /// <remarks>
    /// One longest run of survivors that keeps its relative order stays in place. The moves number at most the survivors
    /// minus that run's length. O(N log N).
    /// </remarks>
    let diff (previous: 'K[]) (next: 'K[]) : ResizeArray<PositionalChange<'K>> =
        let edits = ResizeArray<PositionalChange<'K>>()
        // One more than the key's index in `next`; absent keys read as 0, or null under Fable, and fail `> 0`.
        let target = Platform.KeyMap<'K, int>()

        for j in 0 .. next.Length - 1 do
            target.Set (next[j], j + 1)

        for i = previous.Length - 1 downto 0 do
            if not (target.Find previous[i] > 0) then
                edits.Add (PositionalChange.RemoveAt i)

        let survivorTargets = ResizeArray<int>(previous.Length)

        for key in previous do
            let t = target.Find key

            if t > 0 then
                survivorTargets.Add (t - 1)

        let positions = survivorTargets.ToArray ()
        let survivors = positions.Length
        let staying = longestIncreasing positions
        // The survivor index of each key of `next`, or -1 for an inserted key.
        let survivorAt = Array.create next.Length -1

        for s in 0 .. survivors - 1 do
            survivorAt[positions[s]] <- s

        // The target of the first staying survivor after each survivor, or the length of `next` when none follows.
        let stayingAfter = Array.zeroCreate<int> survivors
        let mutable upcoming = next.Length

        for s = survivors - 1 downto 0 do
            stayingAfter[s] <- upcoming

            if staying[s] then
                upcoming <- positions[s]

        // Survivors not yet placed, by survivor index. Starts all ones: each node holds its range's length.
        let unplaced = Array.zeroCreate<int>(survivors + 1)

        for i in 1..survivors do
            unplaced[i] <- i &&& -i

        // The survivor index of the last staying key placed; the keys placed since it follow it contiguously.
        let mutable anchor = 0

        for j in 0 .. next.Length - 1 do
            let s = survivorAt[j]

            if s < 0 then
                edits.Add (PositionalChange.InsertAt (j + sumBelow unplaced anchor, next[j]))
            elif staying[s] then
                addAt unplaced s -1
                anchor <- s
            else
                let from = min j stayingAfter[s] + sumBelow unplaced s
                addAt unplaced s -1
                let into = j + sumBelow unplaced anchor

                if from <> into then
                    edits.Add (PositionalChange.Move (from, into))

        edits
