module Profile.HeroTextMotion

open Browser.Dom
open Fable.Core.JsInterop
open Partas.AnimeJs

let start () : unit -> unit =
    let words = document.querySelectorAll "#hero-title .hero-word"
    let preference: obj = window?matchMedia("(prefers-reduced-motion: reduce)")
    if words.length < 2 || preference?matches then ignore
    else
        let mutable current = 0
        let mutable handles: Raw.JSAnimation list = []
        let clear () =
            handles |> List.iter (fun handle -> handle.revert () |> ignore)
            handles <- []
        let rotate () =
            clear ()
            let outgoing = words.item current
            current <- (current + 1) % words.length
            let incoming = words.item current
            let exit = Anime.animate (Target.element (unbox outgoing)) (animation {
                Animation.property "opacity" (Tween.fromTo 1. 0.)
                Transform.translateY (Tween.fromTo 0. -14.)
                Animation.duration 180.
                Animation.ease "inOutSine"
                Animation.onComplete (fun handle ->
                    outgoing.setAttribute ("data-active", "false")
                    handle.revert () |> ignore
                    incoming.setAttribute ("data-active", "true")
                    let enter = Anime.animate (Target.element (unbox incoming)) (animation {
                        Animation.property "opacity" (Tween.fromTo 0. 1.)
                        Transform.translateY (Tween.fromTo 14. 0.)
                        Animation.duration 220.
                        Animation.ease "outQuad"
                        Animation.onComplete (fun handle -> handle.revert () |> ignore)
                    })
                    handles <- enter :: handles)
            })
            handles <- [ exit ]
        let timer = Anime.createTimer (timer {
            Timer.duration 3400.
            Timer.loop true
            Timer.onLoop (fun _ -> rotate ())
        })
        let stop () =
            timer.cancel () |> ignore
            clear ()
            for index in 0 .. words.length - 1 do
                (words.item index).setAttribute ("data-active", if index = 0 then "true" else "false")
        let changed (_: Browser.Types.Event) = if preference?matches then stop ()
        preference?addEventListener ("change", changed)
        fun () ->
            preference?removeEventListener ("change", changed)
            stop ()
