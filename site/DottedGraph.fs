/// Composable SVG graphs rendered by Oxpecker.ViewEngine.
module Profile.DottedGraph

open System.Globalization
open Oxpecker.ViewEngine

type Point = { X: float; Y: float }
type Grid = { Spacing: float; Offset: Point; Radius: float; Opacity: float }
type Node = { Position: Point; Radius: float; Fill: string }
type Connection = { Points: Point list; Opacity: float }
type Label = { Position: Point; Text: string }
type Options =
    { Id: string
      Width: float
      Height: float
      ClassName: string
      AccessibleLabel: string
      Color: string
      StrokeWidth: float
      Grid: Grid option }

let point x y = { X = x; Y = y }
let grid = { Spacing = 16.; Offset = point 1. 1.; Radius = 1.; Opacity = 0.15 }
let defaults id accessibleLabel =
    { Id = id; Width = 340.; Height = 220.; ClassName = "connection-diagram"
      AccessibleLabel = accessibleLabel; Color = "currentColor"; StrokeWidth = 1.5; Grid = Some grid }
let node position = { Position = position; Radius = 8.; Fill = "currentColor" }
let hollowNode fill position = { node position with Fill = fill }
let connection points = { Points = points; Opacity = 1. }
let label position text = { Position = position; Text = text }

let private number (value: float) =
    if not (System.Double.IsFinite value) then invalidArg "value" "SVG coordinates and dimensions must be finite."
    value.ToString("G", CultureInfo.InvariantCulture)

let private positive name value =
    if value <= 0. then invalidArg name "Must be greater than zero."
    number value

let private opacity value =
    if value < 0. || value > 1. then invalidArg "opacity" "Must be between zero and one."
    number value

let private tag name attributes =
    let element = RegularNode(name)
    for key, value in attributes do
        element.AddAttribute { Name = key; Value = value }
    element

/// Render the pattern definition and its background rectangle together.
let dottedGrid (options: Options) (grid: Grid) =
    let patternId = options.Id + "-dots"
    Fragment() {
        tag "defs" [] {
            tag "pattern" [ "id", patternId; "width", positive "spacing" grid.Spacing; "height", positive "spacing" grid.Spacing; "patternUnits", "userSpaceOnUse" ] {
                tag "circle" [ "cx", number grid.Offset.X; "cy", number grid.Offset.Y; "r", positive "radius" grid.Radius; "fill", options.Color; "opacity", opacity grid.Opacity ]
            }
        }
        tag "rect" [ "width", positive "width" options.Width; "height", positive "height" options.Height; "fill", $"url(#{patternId})" ]
    }

/// A routed connection follows the supplied points in order.
let renderConnection (options: Options) (connection: Connection) =
    if connection.Points.Length < 2 then invalidArg "connection" "A connection needs at least two points."
    let points = connection.Points |> List.map (fun p -> number p.X + "," + number p.Y) |> String.concat " "
    tag "polyline" [ "points", points; "fill", "none"; "stroke", options.Color; "stroke-width", positive "strokeWidth" options.StrokeWidth; "opacity", opacity connection.Opacity ]

let renderNode (options: Options) (node: Node) =
    tag "circle" [ "cx", number node.Position.X; "cy", number node.Position.Y; "r", positive "radius" node.Radius; "fill", node.Fill; "stroke", options.Color; "stroke-width", positive "strokeWidth" options.StrokeWidth ]

let renderLabel (label: Label) =
    tag "text" [ "x", number label.Position.X; "y", number label.Position.Y ] { label.Text }

/// Returns a fresh tree on every call. Supply a unique ID for each graph on a page.
let render (options: Options) connections nodes labels =
    if System.String.IsNullOrWhiteSpace options.Id || (options.Id |> Seq.exists (fun c -> not (System.Char.IsLetterOrDigit c || c = '-' || c = '_'))) then
        invalidArg "id" "Use a nonempty graph ID containing letters, digits, hyphens or underscores."
    let viewBox = "0 0 " + positive "width" options.Width + " " + positive "height" options.Height
    tag "svg" [ "xmlns", "http://www.w3.org/2000/svg"; "id", options.Id; "class", options.ClassName; "viewBox", viewBox; "role", "img"; "aria-label", options.AccessibleLabel; "color", options.Color; "fill", "currentColor"; "font-family", "ui-monospace, monospace"; "font-size", "9" ] {
        match options.Grid with
        | Some grid -> dottedGrid options grid
        | None -> ()
        for connection in connections do renderConnection options connection
        for node in nodes do renderNode options node
        for label in labels do renderLabel label
    }
