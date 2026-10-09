module Profile.Site

open System.IO
open Nacara.Core
open Nacara.Plugins
open Partas.Nacara.Theme

let solid options =
    options
    |> SolidExamples.partasVersion "3.0.0-local.e08ad85"
    |> SolidExamples.fableVersion "5.20.0"
    |> SolidExamples.feed (Path.Combine(__SOURCE_DIRECTORY__, "feed"))
    |> SolidExamples.targetFramework "net10.0"
    |> SolidExamples.project "components/Profile.Components.fsproj"
    |> SolidExamples.npm "animejs" "4.5.0"
    |> SolidExamples.property "DefineConstants" "RANVIER_TRACE"
    |> fun options ->
        { options with Prelude = options.Prelude @ [ "open Profile"; "open Profile.Components"; "open Partas.Solid.Aria"; "open Fable.Core.TS"; "open System"  ] }

let site =
    Site.create "Shayan Habibi"
    |> Site.baseUrl "/shayanhabibi/"
    |> Site.origin "https://shayanhabibi.github.io"
    |> Site.output "output"
    |> Site.staticFiles "static"
    |> Markdown.register
    |> TextMate.register
    |> Literate.registerWith (fun options -> { options with Extensions = [ ".fsx" ] })
    |> SolidExamples.registerWith solid
    |> Theme.register Theme.defaults
    |> Site.collection (Theme.docs Theme.defaults "content" |> Collection.layout Profile.Presentation.layout)

[<EntryPoint>]
let main args =
    ProjectGraphs.writeAssets (Path.Combine(__SOURCE_DIRECTORY__, "static", "graphs"))
    Nacara.run site args
