namespace Ranvier.Docs.Maps

#if RANVIER_TRACE
open System
open System.Threading.Tasks
open Ranvier

/// <summary>The widget a live map renders for a control, and the action its value is written through.</summary>
type Widget =
    | Button of press: (unit -> unit)
    /// <summary>A range over the integers from <c>min</c> to <c>max</c>, inclusive; <c>set</c> runs on every movement.</summary>
    | Slider of min: int * max: int * start: int * set: (int -> unit)
    /// <summary>A number field; <c>set</c> runs when a number is committed.</summary>
    | Number of start: float * set: (float -> unit)
    /// <summary>A text field; <c>set</c> runs when the text is committed.</summary>
    | Text of start: string * set: (string -> unit)
    | Toggle of start: bool * set: (bool -> unit)

/// <summary>One action of a replay, with the log line shown before it.</summary>
type Step =
    {
        Log: string option
        /// <summary>The explanation shown while the action's events play.</summary>
        Caption: string option
        Run: unit -> unit
        /// <summary>Checks evaluated after the action's queued continuations settle, in author order.</summary>
        Checks: (string * (unit -> bool)) list
    }

/// <summary>The current signal value displayed by a bound input.</summary>
[<RequireQualifiedAccess>]
type InputValue =
    | Integer of int
    | Number of float
    | Text of string
    | Toggle of bool

/// <summary>A control beneath a map: its widget on a live map, its steps on a replay.</summary>
type Control =
    {
        Label: string
        Widget: Widget
        Steps: Step list
        /// <summary>An untracked read of a bound input's signal, or <c>None</c> for callback controls.</summary>
        Binding: (unit -> InputValue) option
    }

/// <summary>A pretend remote service whose requests stay pending until a control answers them.</summary>
/// <remarks>
/// By default a new request cancels the one before it, so a superseded flight drops. A queued desk keeps every
/// request, in the order made: <c>Settle</c> and <c>Fail</c> answer the oldest, <c>SettleNewest</c> and
/// <c>FailNewest</c> the newest.
/// </remarks>
type Desk<'T>(?queued: bool) =
    let queued = defaultArg queued false
    let pending = ResizeArray<TaskCompletionSource<'T>>()

    let answer (index: int) (complete: TaskCompletionSource<'T> -> unit) =
        if pending.Count > 0 then
            let i = if index < 0 then pending.Count - 1 else index
            let request = pending[i]
            pending.RemoveAt i
            complete request

    /// <summary>A request that completes when a control answers it.</summary>
    /// <remarks>The argument is read for its dependency only.</remarks>
    member _.Quote(_: 'R) : Task<'T> =
        let request = TaskCompletionSource<'T>()

        if not queued then
            for older in pending do
#if FABLE_COMPILER
                // fable-library's TaskCompletionSource has no TrySetCanceled.
                older.SetException (OperationCanceledException ())
#else
                older.TrySetCanceled () |> ignore
#endif

            pending.Clear ()

        pending.Add request
        request.Task

    member _.Settle(value: 'T) =
        answer 0 (fun request -> request.SetResult value)

    member _.Fail(message: string) =
        answer 0 (fun request -> request.SetException (Exception message))

    member _.SettleNewest(value: 'T) =
        answer -1 (fun request -> request.SetResult value)

    member _.FailNewest(message: string) =
        answer -1 (fun request -> request.SetException (Exception message))

    /// <summary>The requests awaiting an answer.</summary>
    member _.Pending = pending.Count

/// <summary>Where a map's events come from.</summary>
type MapSource =
    /// <summary>A scenario run against a live traced graph; it returns the map's controls.</summary>
    | Live of scenario: (Graph -> Control list)
    /// <summary>A scenario whose controls each run once, in order, before the map plays it back.</summary>
    | Replayed of scenario: (Graph -> Control list)

[<RequireQualifiedAccess>]
module Controls =

    /// <summary>Displays the current value once and returns a refresh that displays subsequent changes.</summary>
    /// <remarks>An unchanged source preserves an input's uncommitted text.</remarks>
    let follow (read: unit -> 'T) (display: 'T -> unit) : unit -> unit =
        let mutable previous = read ()
        display previous

        fun () ->
            let current = read ()

            if current <> previous then
                previous <- current
                display current

    /// <summary>Raises an error with the first failed expectation's message.</summary>
    let check (step: Step) : unit =
        for message, predicate in step.Checks do
            if not (predicate ()) then
                invalidOp ("Expectation failed: " + message)

    /// <summary>The number in a field's text, or <c>None</c> for empty or non-numeric text.</summary>
    let parseNumber (text: string) : float option =
        // The empty check guards Fable, where Number("") is 0.
        match text.Trim () with
        | "" -> None
        | trimmed ->
            match Double.TryParse trimmed with
            | true, v -> Some v
            | _ -> None

    let internal steps (label: string) (show: 'V -> string) (values: 'V list) (set: 'V -> unit) : Step list =
        values
        |> List.map (fun v ->
            {
                Log = Some $"set %s{label} = %s{show v}"
                Caption = None
                Run = fun () -> set v
                Checks = []
            })

[<AutoOpen>]
module Helpers =

    /// <summary>The controls of a map, in order: the row order on a live map and the step order on a replay.</summary>
    let controls (items: Control list) : Control list = items

    let button (label: string) (press: unit -> unit) : Control =
        {
            Label = label
            Widget = Button press
            Binding = None
            Steps =
                [
                    {
                        Log = None
                        Caption = None
                        Run = press
                        Checks = []
                    }
                ]
        }

    /// <summary>Shows <c>caption</c> throughout each replay action of the control.</summary>
    let describe (caption: string) (control: Control) : Control =
        { control with
            Steps =
                control.Steps
                |> List.map (fun step -> { step with Caption = Some caption })
        }

    /// <summary>Checks <c>predicate</c> after each replay action and reports <c>message</c> on failure.</summary>
    /// <remarks>Checks should inspect settled state without writing to the graph or forcing lazy computations.</remarks>
    let expect (message: string) (predicate: unit -> bool) (control: Control) : Control =
        { control with
            Steps =
                control.Steps
                |> List.map (fun step ->
                    { step with
                        Checks = step.Checks @ [ message, predicate ]
                    })
        }

    /// <summary>A slider over <c>min</c> to <c>max</c>, starting at <c>start</c>; a replay writes each of <c>replay</c>.</summary>
    let slider (label: string) (min: int, max: int) (start: int) (replay: int list) (set: int -> unit) : Control =
        {
            Label = label
            Widget = Slider (min, max, start, set)
            Binding = None
            Steps = Controls.steps label string replay set
        }

    /// <summary>A number field starting at <c>start</c>; a replay writes each of <c>replay</c>.</summary>
    let number (label: string) (start: float) (replay: float list) (set: float -> unit) : Control =
        {
            Label = label
            Widget = Number (start, set)
            Binding = None
            Steps = Controls.steps label string replay set
        }

    /// <summary>A text field starting at <c>start</c>; a replay writes each of <c>replay</c>.</summary>
    let text (label: string) (start: string) (replay: string list) (set: string -> unit) : Control =
        {
            Label = label
            Widget = Text (start, set)
            Binding = None
            Steps = Controls.steps label id replay set
        }

    /// <summary>A checkbox starting at <c>start</c>; a replay writes each of <c>replay</c>.</summary>
    let toggle (label: string) (start: bool) (replay: bool list) (set: bool -> unit) : Control =
        let show (b: bool) =
            if b then "true" else "false"

        {
            Label = label
            Widget = Toggle (start, set)
            Binding = None
            Steps = Controls.steps label show replay set
        }

    /// <summary>A slider bound to <c>signal</c>; a replay writes each of <c>replay</c>.</summary>
    /// <remarks>The widget follows the signal's current value without creating an observer.</remarks>
    let sliderSignal (label: string) (min: int, max: int) (signal: Signal<int>) (replay: int list) : Control =
        { slider label (min, max) signal.Peek replay (fun value -> signal.Value <- value) with
            Binding = Some (fun () -> InputValue.Integer signal.Peek)
        }

    /// <summary>A number field bound to <c>signal</c>; a replay writes each of <c>replay</c>.</summary>
    /// <remarks>The widget follows the signal's current value without creating an observer.</remarks>
    let numberSignal (label: string) (signal: Signal<float>) (replay: float list) : Control =
        { number label signal.Peek replay (fun value -> signal.Value <- value) with
            Binding = Some (fun () -> InputValue.Number signal.Peek)
        }

    /// <summary>A text field bound to <c>signal</c>; a replay writes each of <c>replay</c>.</summary>
    /// <remarks>The widget follows the signal's current value without creating an observer.</remarks>
    let textSignal (label: string) (signal: Signal<string>) (replay: string list) : Control =
        { text label signal.Peek replay (fun value -> signal.Value <- value) with
            Binding = Some (fun () -> InputValue.Text signal.Peek)
        }

    /// <summary>A checkbox bound to <c>signal</c>; a replay writes each of <c>replay</c>.</summary>
    /// <remarks>The widget follows the signal's current value without creating an observer.</remarks>
    let toggleSignal (label: string) (signal: Signal<bool>) (replay: bool list) : Control =
        { toggle label signal.Peek replay (fun value -> signal.Value <- value) with
            Binding = Some (fun () -> InputValue.Toggle signal.Peek)
        }
#endif
