namespace Ranvier.Docs.Maps

#if RANVIER_TRACE
open Ranvier

[<RequireQualifiedAccess>]
module Replay =
    /// <summary>The frame delay or animation duration in milliseconds at playback multiplier <c>speed</c>.</summary>
    /// <remarks>Speed is from 0.25 to 4; zero milliseconds remains immediate at every speed.</remarks>
    let duration (speed: float) (milliseconds: int) : int =
        int (float milliseconds / speed)

    /// <summary>The caption of the last action at or before the replay cursor.</summary>
    let captionAt (marks: (int * string option) seq) (cursor: int) : string option =
        marks
        |> Seq.filter (fun (at, _) -> at <= cursor)
        |> Seq.tryLast
        |> Option.bind snd

    /// <summary>Runs the scenario's controls in replay order and checks each action after queued continuations settle.</summary>
    /// <remarks>A failure includes the scenario's source location and the control's label.</remarks>
    let verify (location: string) (policy: FlightPolicy) (scenario: Graph -> Control list) : Async<unit> =
        async {
            use graph =
                new Graph (
                    { GraphOptions.Default with
                        FlightPolicy = policy
#if !FABLE_COMPILER
                        ThreadAffinity = Serialised
#endif
                    }
                )

            let active run =
                use _ = graph.Activate ()
                run ()

            let controls =
                try
                    active (fun () -> scenario graph)
                with ex ->
                    invalidOp ($"{location}: setup: {ex.Message}")

            do! Async.Sleep 1
            graph.Pump () |> ignore

            for control in controls do
                for step in control.Steps do
                    try
                        active step.Run
                        do! Async.Sleep 1
                        graph.Pump () |> ignore
                        active (fun () -> untrack (fun () -> Controls.check step))
                    with ex ->
                        return invalidOp ($"{location}: {control.Label}: {ex.Message}")
        }
#endif
