module Workspace

open System.IO
open Partas.Build

let root = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, ".."))
module Files =
    let private (</>) a b = Path.Combine(a, b)
    let solution = "Profile.slnx"
    module Site =
        let project = "site" </> "site.fsproj"
    module Scripts =
        let prepareSolid = "scripts" </> "prepare-solid.mjs"
    module Tests =
        let dottedGraph = "tests" </> "dotted-graph.fsx"
        let checkStatic = "tests" </> "check-static.mjs"

let restore = Baked.Stages.restore Files.solution
let compile = stage "compile" { run (cmd $"dotnet build {Files.solution} -c Release --no-restore") }
let prepareSolid = stage "prepare browser dependencies" { run (cmd $"node {Files.Scripts.prepareSolid}") }
let generate = stage "generate site" { 
        run (cmd $"dotnet run --project {Files.Site.project} -c Release --no-build -- build --root site") 
    }
let checkGraph = stage "check SVG graph" { run (cmd $"dotnet fsi {Files.Tests.dottedGraph}") }
let checkStatic = stage "check static output" { run (cmd $"node {Files.Tests.checkStatic}") }
let installNpm = stage "install npm dependencies" { run "npm ci" }
let installBrowser = stage "install test browser" { run "npx playwright install chromium" }
let browserChecks = stage "browser checks" { run "npm test" }

let docs = 
    input {
        let! watch = Input.option<bool> "--watch"
        return
            if not watch then
                stage "serve site" { 
                    run (cmd $"dotnet run --project {Files.Site.project} -c Release --no-build -- serve --root site --port 8080") 
                }
            else stage "watch site" { 
                    run (cmd $"dotnet watch --project {Files.Site.project} -c Release --no-hot-reload -- watch") 
                }
    }

[<EntryPoint>]
let main args =
    rootCommand args {
        description "Professional profile workspace"
        workingDir root
        command "build" {
            description "Restore, compile and generate the complete static site"
            restore
            compile
            prepareSolid
            generate
        }
        command "serve" {
            description "Build and serve the design locally"
            restore
            compile
            prepareSolid
            docs
        }
        command "check" {
            description "Build the site and check generated static content"
            restore
            compile
            checkGraph
            prepareSolid
            generate
        }
        command "test" {
            description "Build and verify static content and browser interactions"
            restore
            compile
            checkGraph
            prepareSolid
            generate
            checkStatic
            installNpm
            installBrowser
            browserChecks
        }
    }
