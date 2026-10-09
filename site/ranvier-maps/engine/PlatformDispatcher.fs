namespace Ranvier

open System.Threading

// Split out of `Platform.fs` for one reason: fantomas cannot merge a file that
// conditionally *declares a type* with one that conditionally binds values

#if !FABLE_COMPILER
/// <summary>
/// Posts the drain to a captured synchronisation context.
/// </summary>
/// <remarks>
/// This is the case that needs no thought from the caller: on WPF, WinForms,
/// Avalonia or a browser, the context is the thread that owns the graph, and
/// settles marshal back to it the same way any other UI work does.
/// </remarks>
type SynchronizationContextDispatcher(context: SynchronizationContext) =
    member _.Context = context

    interface IGraphDispatcher with
        member _.Post drain =
            context.Post ((fun _ -> drain.Invoke ()), null)
#endif
