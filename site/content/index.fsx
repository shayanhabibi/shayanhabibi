(**
---
title: F# tooling across ecosystems
description: F# compiler integrations, typed bindings and workspace automation. Selected work for employment and consulting.
layout: bare
---
*)
(*** hide ***)
#r "nuget: Partas.Solid, 3.0.0-local.e08ad85"
#r "nuget: Xantham.Fable.Core.TS"
#i "nuget: https://nuget.cloudsmith.io/shayanhabibi/partas-animejs/v3/index.json"
#r "nuget: Partas.AnimeJs, 0.1.0"
#load "../Projects.fs"
#load "../components/HeroMotion.fs"
#load "../components/HeroTextMotion.fs"
#load "../components/ProjectMotion.fs"
#load "../components/ProjectRow.fs"

open Profile
open Profile.Components
open Partas.Solid
open Partas.Solid.Aria
open Fable.Core
open Fable.Core.TS
open Fable.Core.JsInterop


(*** solid render=ProjectExplorer show=inline ***)
[<Global>]
let private globalThis: GlobalThis = jsNative
let private document = globalThis.document
let private window = globalThis.window
let private hideFallback (hidden: bool) : unit =
    let fallback = document.getElementById "project-fallback"
    fallback
    |> Option.iter (fun e -> e.hidden <- if hidden then Dom.HTMLElement.Hidden.True else Dom.HTMLElement.Hidden.False)

let private scrollIndex () : float =
    match document.getElementById "project-window" with
    | Some w ->
        match w.querySelector ".project-row" with
        | Some row ->
            globalThis.Math.round(w.scrollTop / row.getBoundingClientRect().height)
        | _ -> 0.
    | _ -> 0.

let private scrollToProject (index: float) (smooth: bool) : unit =
    let scroll() =
        match
            document.getElementById "project-window"
            |> Option.bind (fun w -> w.querySelector ".project-row" |> Option.map (fun row -> w, row))
        with
        | Some (w, row) ->
            w.scrollTo(
                Dom.ScrollToOptions.Create(
                    top = index * row.getBoundingClientRect().height,
                    behavior =
                        if
                            smooth
                            && not(window.matchMedia("(prefers-reduced-motion: reduce)").matches)
                        then Dom.ScrollBehavior.Smooth
                        else Dom.ScrollBehavior.Instant
                    )
                )
        | None -> ()
    if smooth then scroll()
    else globalThis.requestAnimationFrame(ignore >> scroll) |> ignore



[<SolidComponent>]
let ProjectExplorer () =
    let selection, setSelection = createSignal (value = (All, 0))
    let category () = fst (selection ())
    let first () = snd (selection ())
    let capacity = 3
    let filtered = createMemo (fun (_: Project array option) -> Projects.filter (category ()))
    let lastStart () = max 0 (filtered().Length - capacity)
    let clamp index = max 0 (min (lastStart ()) index)
    let mutable destination = 0
    let goTo index =
        destination <- clamp index
        scrollToProject destination true
    let move direction = goTo (destination + direction)
    let syncPosition () = setSelection (category (), clamp (!! scrollIndex ()))
    let mutable stopProjects = ignore
    let selectCategory item =
        if item <> category () then
            stopProjects ()
            destination <- 0
            setSelection (item, 0)
            scrollToProject 0 false
            stopProjects <- ProjectMotion.start ()
    let handleKey (event: Dom.KeyboardEvent) =
        if event.target = event.currentTarget then
            match event.key with
            | "ArrowDown" -> event.preventDefault (); goTo (first () + 1)
            | "ArrowUp" -> event.preventDefault (); goTo (first () - 1)
            | "PageDown" -> event.preventDefault (); goTo (first () + capacity)
            | "PageUp" -> event.preventDefault (); goTo (first () - capacity)
            | "Home" -> event.preventDefault (); goTo 0
            | "End" -> event.preventDefault (); goTo (lastStart ())
            | _ -> ()
    let rangeText () =
        let total = filtered().Length
        if total = 1 then "1 project"
        elif total <= capacity then string total + " projects"
        else string (first () + 1) + "–" + string (min total (first () + capacity)) + " of " + string total + " projects"
    let mutable stopHero = ignore
    let mutable stopHeroText = ignore
    let mutable mounted = false
    onSettled (fun () ->
        hideFallback true
        if not mounted then
            mounted <- true
            stopHero <- HeroMotion.start ()
            stopHeroText <- HeroTextMotion.start ())
    onCleanup (fun () -> stopHero (); stopHeroText (); stopProjects (); hideFallback false)

    div (id = "project-explorer", role = "region", ariaLabel = "Selected projects", ariaRoleDescription = "carousel") {
        div (class' = "work-controls") {
            div (class' = "filters", role = "group", ariaLabel = "Filter projects") {
                For.Keyed(each = Projects.categories) {
                    yield fun item _ ->
                        button (
                            type' = "button",
                            class' = "filter",
                            ariaPressed = string (category () = item),
                            onClick = fun _ -> selectCategory item
                        ) { item.AsString }
                }
            }
            p (class' = "result-count", ariaLive = "polite", ariaAtomic = "true") {
                rangeText ()
            }
        }
        div (
            id = "project-window", class' = "project-list", tabIndex = 0,
            role = "group", ariaLabel = "Project list. Scroll or use arrow keys to browse.",
            onScroll = (fun _ -> syncPosition ()),
            onScrollEnd = (fun _ -> syncPosition (); destination <- first ()),
            onKeyDown = handleKey
        ) {
            For.Keyed(each = filtered ()) {
                yield fun project _ -> ProjectRow project
            }
        }
        div (class' = "carousel-controls", role = "group", ariaLabel = "Browse projects") {
            p (class' = "carousel-hint") { "Scroll or swipe to explore" }
            div (class' = "carousel-buttons") {
                button (type' = "button", class' = "carousel-button", ariaLabel = "Previous project", ariaControls = "project-window", disabled = (first () = 0), onClick = fun _ -> move -1) {
                    span (ariaHidden = true) { "↑" }
                    " Previous"
                }
                button (type' = "button", class' = "carousel-button", ariaLabel = "Next project", ariaControls = "project-window", disabled = (first () >= lastStart ()), onClick = fun _ -> move 1) {
                    "Next "
                    span (ariaHidden = true) { "↓" }
                }
            }
        }
    }
