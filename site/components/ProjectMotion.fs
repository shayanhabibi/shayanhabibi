module Profile.ProjectMotion

open Browser.Dom
open Fable.Core.JsInterop
open Partas.AnimeJs

/// Animate only the visible rows after a category change; leave scroll geometry intact.
let start () : unit -> unit =
    let preference: obj = window?matchMedia("(prefers-reduced-motion: reduce)")
    if preference?matches then ignore
    else
        let mutable handles: Raw.JSAnimation list = []
        let mutable stopped = false
        let frame = window.requestAnimationFrame (fun _ ->
            if not stopped && not preference?matches then
                let rows = document.querySelectorAll "#project-window .project-row"
                for index in 0 .. min 2 (rows.length - 1) do
                    let handle = Anime.animate (Target.element (unbox (rows.item index))) (animation {
                        Animation.property "opacity" (Tween.fromTo 0. 1.)
                        Transform.translateY (Tween.fromTo 12. 0.)
                        Animation.duration 420.
                        Animation.delay (float index * 65.)
                        Animation.ease "outQuad"
                        Animation.onComplete (fun animation -> animation.revert () |> ignore)
                    })
                    handles <- handle :: handles)
        let stop () =
            stopped <- true
            window.cancelAnimationFrame frame
            handles |> List.iter (fun handle -> handle.revert () |> ignore)
            handles <- []
        let changed (_: Browser.Types.Event) = if preference?matches then stop ()
        preference?addEventListener ("change", changed)
        fun () ->
            preference?removeEventListener ("change", changed)
            stop ()
