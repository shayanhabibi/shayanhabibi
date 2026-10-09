module Profile.HeroMotion

open Browser.Dom
open Fable.Core.JsInterop
open Partas.AnimeJs

let start () : unit -> unit =
    let preference: obj = window?matchMedia("(prefers-reduced-motion: reduce)")
    match document.querySelector "#connecting-thread" |> Option.ofObj with
    | None -> ignore
    | Some _ when preference?matches -> ignore
    | Some diagram ->
        match diagram.querySelector "polyline" |> Option.ofObj with
        | None -> ignore
        | Some path ->
            let layer = document.createElementNS ("http://www.w3.org/2000/svg", "g")
            layer.setAttribute ("class", "hero-motion")
            layer.setAttribute ("aria-hidden", "true")
            let trail = document.createElementNS ("http://www.w3.org/2000/svg", "polyline")
            trail.setAttribute ("class", "hero-pulse-trail")
            let pulse = document.createElementNS ("http://www.w3.org/2000/svg", "path")
            pulse.setAttribute ("class", "hero-pulse")
            pulse.setAttribute ("d", "M3 0 C3 -2 0 -2.5 -2 -1.5 L-7 0 L-2 1.5 C0 2.5 3 2 3 0Z")
            layer.appendChild trail |> ignore
            layer.appendChild pulse |> ignore
            let progress = createObj [ "distance" ==> 0. ]
            let length: float = path?getTotalLength ()
            let randomDirection () = if Fable.Core.JS.Math.random () < 0.5 then 1. else -1.
            let mutable direction = randomDirection ()
            let move () =
                let elapsed: float = progress?distance
                let distance = if direction > 0. then elapsed else 1. - elapsed
                let position = distance * length
                let point = path?getPointAtLength position
                let before = path?getPointAtLength (max 0. (position - 2.))
                let after = path?getPointAtLength (min length (position + 2.))
                let angle = System.Math.Atan2 ((after?y - before?y) * direction, (after?x - before?x) * direction) * 180. / System.Math.PI
                pulse.setAttribute ("transform", $"translate({point?x} {point?y}) rotate({angle})")
                // Fade at either endpoint so the loop resets invisibly beneath the nodes.
                let visibility = min 1. (min (distance / 0.08) ((1. - distance) / 0.08))
                layer.setAttribute ("opacity", string (max 0. visibility))
                let points =
                    [ 0 .. 12 ]
                    |> List.map (fun step ->
                        let sample = path?getPointAtLength (max 0. (min length (position - direction * (14. - float step / 12. * 14.))))
                        string sample?x + "," + string sample?y)
                    |> String.concat " "
                trail.setAttribute ("points", points)
            move ()
            // Connections are emitted before nodes; insert here to preserve that paint order.
            diagram.insertBefore (layer, path.nextSibling) |> ignore
            let handle = Anime.animate (Target.``object`` (unbox progress)) (animation {
                Animation.property "distance" (Tween.fromTo 0. 1.)
                Animation.duration 1200.
                Animation.ease "linear"
                Animation.loop true
                Animation.loopDelay 3200.
                Animation.onLoop (fun _ -> direction <- randomDirection ())
                Animation.onUpdate (fun _ -> move ())
            })
            let stop () = handle.cancel () |> ignore; layer.remove ()
            let changed (_: Browser.Types.Event) = if preference?matches then stop ()
            preference?addEventListener ("change", changed)
            fun () ->
                preference?removeEventListener ("change", changed)
                stop ()
