namespace Ranvier

/// <summary>Creation sites for the trace log.</summary>
module internal TraceSite =
#if FABLE_COMPILER
    /// <summary>The creation site of the calling frame; null under Fable.</summary>
    let capture () : string = null
#else
    open System
    open System.Diagnostics
    open System.IO

    let private library = typeof<TraceEvent>.Assembly

    /// <summary>The frame's assembly name, or null.</summary>
#if NET5_0_OR_GREATER
    [<CodeAnalysis.UnconditionalSuppressMessage("Trimming",
                                                "IL2026",
                                                Justification = "A trimmed method yields null from GetMethod, which reads as no assembly.")>]
#endif
    let private assemblyOf (frame: StackFrame) =
        match frame.GetMethod () with
        | null -> null
        | m when isNull m.DeclaringType -> null
        | m when Object.ReferenceEquals (m.DeclaringType.Assembly, library) -> "Ranvier"
        | m -> m.DeclaringType.Assembly.GetName().Name

    let private excluded (name: string) =
        isNull name
        || name = "Ranvier"
        || name = "Ranvier.CSharp"
        || name = "FSharp.Core"
        || name = "System"
        || name.StartsWith "System."

    let private inLibrarySource (file: string) =
        let file = file.Replace (Path.DirectorySeparatorChar, '/')

        file.Contains "/src/Ranvier/"
        || file.Contains "/src/Ranvier.CSharp/"

    /// <summary>
    /// The <c>file:line</c> of the innermost frame with file info outside Ranvier, Ranvier.CSharp, FSharp.Core and
    /// <c>System.*</c> and outside <c>src/Ranvier</c> and <c>src/Ranvier.CSharp</c>. An FSI submission frame gives the script's
    /// <c>file:line</c>, or <c>stdin:line</c>, with line 0 when the submission carries no line info. <c>"?"</c> when
    /// no frame qualifies or the capture fails.
    /// </summary>
    let capture () : string =
        try
            let frames = StackTrace(1, true).GetFrames()
            let mutable site = "?"
            let mutable i = 0

            while i < frames.Length do
                let frame = frames[i]
                let file = frame.GetFileName ()
                let name = assemblyOf frame

                if
                    not (isNull name)
                    && name.StartsWith "FSI-ASSEMBLY"
                then
                    let script =
                        if String.IsNullOrEmpty file then
                            "stdin"
                        else
                            Path.GetFileName file

                    site <- String.Concat (script, ":", string (frame.GetFileLineNumber ()))
                    i <- frames.Length
                elif
                    not (String.IsNullOrEmpty file)
                    && not (excluded name)
                    && not (inLibrarySource file)
                then
                    site <- String.Concat (Path.GetFileName file, ":", string (frame.GetFileLineNumber ()))
                    i <- frames.Length
                else
                    i <- i + 1

            site
        with _ ->
            "?"
#endif
