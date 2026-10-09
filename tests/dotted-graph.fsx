// The build supplies these assemblies; no separate script restore is needed.
#r "../site/bin/Release/net10.0/Microsoft.Extensions.ObjectPool.dll"
#r "../site/bin/Release/net10.0/Oxpecker.ViewEngine.dll"
#load "../site/DottedGraph.fs"
#load "../site/Projects.fs"
#load "../site/ProjectGraphs.fs"

open System.Globalization
open System.Xml.Linq
open Oxpecker.ViewEngine
open Profile.DottedGraph

let require condition message = if not condition then failwith message
let attribute name (element: XElement) = element.Attribute(XName.Get name).Value
let elements name (element: XElement) = element.Descendants() |> Seq.filter (fun child -> child.Name.LocalName = name) |> Seq.toList
let options =
    { defaults "test-graph" "Tools & systems <connected>" with
        Width = 240.5; Height = 120.25; Color = "#2449bd"; StrokeWidth = 2.5
        Grid = Some { grid with Spacing = 12.5; Radius = 0.75; Opacity = 0.2 } }
let links = [ { connection [ point 20.5 30.25; point 90.5 30.25; point 90.5 80. ] with Opacity = 0.4 } ]
let nodes = [ { hollowNode "white" (point 20.5 30.25) with Radius = 6.5 }; node (point 90.5 80.) ]
let labels = [ label (point 10.5 15.25) "F# & <Nim>" ]
let renderGraph options = render options links nodes labels |> Render.toString
let originalCulture = CultureInfo.CurrentCulture
try
    CultureInfo.CurrentCulture <- CultureInfo.InvariantCulture
    let first = renderGraph options
    CultureInfo.CurrentCulture <- CultureInfo.GetCultureInfo "fr-FR"
    require (first = renderGraph options) "Output changed with culture or repeated rendering."
    let svg = XElement.Parse first
    require (attribute "viewBox" svg = "0 0 240.5 120.25") "Custom dimensions were lost."
    require (attribute "aria-label" svg = options.AccessibleLabel) "Accessible label was not escaped correctly."
    require ((elements "text" svg).Head.Value = "F# & <Nim>") "Label text was not escaped correctly."
    let pattern = (elements "pattern" svg).Head
    require (attribute "width" pattern = "12.5") "Grid spacing was lost."
    require (attribute "fill" (elements "rect" svg).Head = "url(#test-graph-dots)") "Grid reference is incorrect."
    require (attribute "points" (elements "polyline" svg).Head = "20.5,30.25 90.5,30.25 90.5,80") "Connection routing was lost."
    require (attribute "stroke-width" (elements "polyline" svg).Head = "2.5") "Custom stroke width was lost."
    let second = XElement.Parse(renderGraph { options with Id = "second-graph" })
    require (attribute "id" (elements "pattern" second).Head <> attribute "id" pattern) "Two graphs share a pattern ID."
    let plain = XElement.Parse(renderGraph { options with Grid = None })
    require (List.isEmpty (elements "pattern" plain)) "Optional grid was not removed."
    require ((elements "circle" plain).Length = 2) "Graph nodes were lost."
    let rejects action =
        try action (); false
        with :? System.ArgumentException -> true
    require (rejects (fun () -> renderGraph { options with Width = 0. } |> ignore)) "Invalid dimensions accepted."
    require (rejects (fun () -> renderGraph { options with Id = "invalid id" } |> ignore)) "Invalid graph ID accepted."
    require (rejects (fun () -> renderConnection options (connection [ point 0. 0. ]) |> ignore)) "Incomplete connection accepted."
    let projectGraphs =
        Profile.Projects.all |> Array.map (fun project ->
            let output = Profile.ProjectGraphs.render project |> Render.toString
            require (output = (Profile.ProjectGraphs.render project |> Render.toString)) "Project image output is not deterministic."
            let image = XElement.Parse output
            require (image.Name.NamespaceName = "http://www.w3.org/2000/svg") "Project SVG is not a standalone SVG document."
            require (attribute "viewBox" image = "0 0 160 104") "Project accent dimensions changed."
            require (attribute "color" image = "#666666") "Project accents must remain greyscale."
            require (attribute "aria-label" image = project.GraphLabel) "Project graph description is missing."
            require ((elements "polyline" image).Length > 0) "Project graph has no connections."
            require ((elements "circle" image).Length > 1) "Project graph has no nodes."
            output)
    require ((Array.distinct projectGraphs).Length = Profile.Projects.all.Length) "Projects do not have independent graphs."
    printfn "Dotted graph determinism, customisation, escaping and independent IDs verified."
finally
    CultureInfo.CurrentCulture <- originalCulture
