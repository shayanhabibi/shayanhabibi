/// Illustrations built with the shared dotted graph renderer.
module Profile.ProjectGraphs

open System.IO
open Oxpecker.ViewEngine
open Profile.DottedGraph

type Illustration =
    { Connections: Connection list
      Nodes: Node list
      Labels: Label list }

let private outlined position = { hollowNode "#f7f5ef" position with Radius = 4. }
let private filled position = { node position with Radius = 4. }
let private faint points = { connection points with Opacity = 0.3 }

let illustration projectId =
    match projectId with
    | PartasSolid ->
        let source, compiler, output = point 24. 52., point 76. 52., point 132. 52.
        { Connections = [ connection [ source; compiler; output ]; connection [ compiler; point 104. 52.; point 104. 24.; point 132. 24. ]; faint [ compiler; point 104. 52.; point 104. 80.; point 132. 80. ] ]
          Nodes = [ outlined source; filled compiler; outlined output; filled (point 132. 24.); outlined (point 132. 80.) ]
          Labels = [ label (point 16. 92.) "F#"; label (point 119. 100.) "JSX" ] }
    | Xantham -> 
        let binding = point 80. 52.
        { Connections = [
            connection [ point 28. 28.; point 52. 28.; point 52. 52.; binding ]
            faint [ point 28. 76.; point 52. 76.; point 52. 52.; binding ]
            connection [ binding; point 108. 52.; point 108. 28.; point 132. 28. ]
            faint [ binding; point 108. 52.; point 108. 76.; point 132. 76. ] ]
          Nodes = [ outlined (point 28. 28.); outlined (point 28. 76.); filled binding; outlined (point 132. 28.); outlined (point 132. 76.) ]
          Labels = [ label (point 20. 100.) "TS"; label (point 124. 100.) "F#" ] }
    | Ranvier ->
        let signal, upper, lower, effect = point 24. 52., point 80. 28., point 80. 76., point 136. 52.
        { Connections = [ connection [ signal; point 48. 52.; point 48. 28.; upper ]; connection [ signal; point 48. 52.; point 48. 76.; lower ]; connection [ upper; point 112. 28.; point 112. 52.; effect ]; faint [ lower; point 112. 76.; point 112. 52.; effect ] ]
          Nodes = [ filled signal; outlined upper; outlined lower; filled effect ]
          Labels = [] }
    | PartasBuild ->
        let start, stage1, stage2, finish = point 20. 28., point 60. 28., point 100. 28., point 140. 28.
        { Connections = [ connection [ start; stage1; stage2; finish ]; connection [ stage1; point 60. 76.; point 100. 76. ]; faint [ stage2; point 100. 76.; point 140. 76. ] ]
          Nodes = [ outlined start; filled stage1; filled stage2; outlined finish; outlined (point 60. 76.); filled (point 100. 76.); outlined (point 140. 76.) ]
          Labels = [] }
    | Loony ->
        let first, last = point 56. 52., point 104. 52.
        { Connections = [
            connection [ point 16. 24.; point 36. 24.; point 36. 52.; first ]
            connection [ point 16. 52.; first ]
            faint [ point 16. 80.; point 36. 80.; point 36. 52.; first ]
            connection [ first; point 80. 52.; last ]
            connection [ last; point 124. 52.; point 124. 24.; point 144. 24. ]
            connection [ last; point 144. 52. ]
            faint [ last; point 124. 52.; point 124. 80.; point 144. 80. ] ]
          Nodes = [ for y in [ 24.; 52.; 80. ] do outlined (point 16. y)
                    filled first; outlined (point 80. 52.); filled last
                    for y in [ 24.; 52.; 80. ] do outlined (point 144. y) ]
          Labels = [] }
    | Wrflock ->
        let write, read, free = point 28. 28., point 132. 28., point 80. 80.
        { Connections = [ connection [ write; read ]; connection [ read; point 132. 80.; free ]; faint [ free; point 28. 80.; write ]; faint [ read; point 132. 52.; point 108. 52. ] ]
          Nodes = [ filled write; outlined read; outlined (point 108. 52.); filled free ]
          Labels = [ label (point 15. 16.) "W"; label (point 126. 16.) "R"; label (point 75. 100.) "F" ] }
    | FableElectron ->
        let binding = point 80. 52.
        { Connections = [
            connection [ point 28. 28.; point 52. 28.; point 52. 52.; binding ]
            faint [ point 28. 76.; point 52. 76.; point 52. 52.; binding ]
            faint [ binding; point 108. 52.; point 108. 28.; point 132. 28. ]
            connection [ binding; point 108. 52.; point 108. 76.; point 132. 76. ] ]
          Nodes = [ outlined (point 28. 28.); outlined (point 28. 76.); filled binding; outlined (point 132. 28.); outlined (point 132. 76.) ]
          Labels = [ ] }
        
let render (project: Project) =
    let graph = illustration project.Id
    let options =
        { defaults ("project-" + project.Id.ToString()) project.GraphLabel with
            Width = 160.; Height = 104.; ClassName = "project-graph"; Color = "#666666"; StrokeWidth = 1.25
            Grid = Some { grid with Spacing = 12.; Radius = 0.65; Opacity = 0.16 } }
    render options graph.Connections graph.Nodes graph.Labels

/// Write only changed assets so generation does not trigger needless watcher updates.
let writeAssets directory =
    Directory.CreateDirectory directory |> ignore
    for project in Projects.all do
        let path = Path.Combine(directory, project.Id.ToString() + ".svg")
        let svg = render project |> Render.toString
        if not (File.Exists path) || File.ReadAllText path <> svg then
            File.WriteAllText(path, svg)
