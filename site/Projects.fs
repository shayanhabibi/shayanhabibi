namespace Profile

open System

type Project =
    { Id: ProjectId
      Name: string
      Summary: string
      Detail: string
      Category: ProjectCategory
      GraphLabel: string
      Technologies: string array
      SourceUrl: string
      DocsUrl: string }
and ProjectId =
    | PartasSolid
    | Xantham
    | Ranvier
    | PartasBuild
    | Loony
    | Wrflock
    | FableElectron
    override this.ToString() =
        match this with
        | PartasSolid -> "partas-solid"
        | Xantham -> "xantham"
        | Ranvier -> "ranvier"
        | PartasBuild -> "partas-build"
        | Loony -> "loony"
        | Wrflock -> "wrflock"
        | FableElectron -> "fable-electron"
and ProjectCategory =
    | All
    | Compilers
    | Interop
    | ReactiveSystems
    | Automation
    | Concurrency
    member this.AsString =
        let asString = this.ToString()
        if asString |> Seq.exists Char.IsUpper then
            let sb = Text.StringBuilder()
            for char in asString do
                if Char.IsUpper(char) then
                    sb.Append(' ') |> ignore
                sb.Append(char) |> ignore
            sb.ToString()
        else asString

module Projects =
    let partasSolid =
           { Id = PartasSolid
             Name = "Partas.Solid"
             Summary = "F# components compiled to Solid JSX, with typed props and reactive control flow."
             Detail = "Building on Oxpecker.Solid, I developed the DSL and Fable AST transforms for typed components and JSX output. This site's interactive project explorer is compiled from an F# script."
             Category = Compilers
             GraphLabel = "F# components pass through a compiler and branch into Solid JSX output."
             Technologies = [| "F#"; "Fable"; "Solid" |]
             SourceUrl = "https://github.com/shayanhabibi/Partas.Solid"
             DocsUrl = "https://shayanhabibi.github.io/Partas.Solid/" }
    let xantham =
           { Id = Xantham
             Name = "Xantham"
             Summary = "Generate typed Fable bindings from TypeScript libraries through the compiler's own API."
             Detail = "I built an F# generator around the TypeScript 7 compiler API, with generated protocol bindings and batched requests. It also produces the DOM and Node bindings published alongside the tool."
             Category = Interop
             GraphLabel = "TypeScript and F# are connected through a shared binding layer."
             Technologies = [| "F#"; "TypeScript"; "Code generation" |]
             SourceUrl = "https://github.com/shayanhabibi/Xantham"
             DocsUrl = "https://shayanhabibi.github.io/Xantham/" }
    let ranvier =
           { Id = Ranvier
             Name = "Ranvier"
             Summary = "Fine-grained reactive computation for .NET, built around explicit dependencies."
             Detail = "I built a reactive graph engine for .NET and Fable with dynamic dependency tracking, deterministic ownership and explicit pending/error boundaries. Its trace log powers the live signal maps in this site's walkthrough."
             Category = ReactiveSystems
             GraphLabel = "A signal fans out through dependent computations and converges on an effect."
             Technologies = [| "F#"; ".NET"; "Reactive graphs" |]
             SourceUrl = "https://github.com/shayanhabibi/Ranvier"
             DocsUrl = "https://shayanhabibi.github.io/Ranvier/" }
    let partasBuild =
           { Id = PartasBuild
             Name = "Partas.Build"
             Summary = "Keep build stages and CLI options in sync by deriving each command's flags from its pipeline."
             Detail = "Building on Fun.Build and FSharp.SystemCommandLine, I designed a typed DSL where stages declare the options they use. This site's build, checks and GitHub workflows run through the same CLI."
             Category = Automation
             GraphLabel = "Build stages form a pipeline with branches for checks and reports."
             Technologies = [| "F#"; ".NET"; "CI/CD" |]
             SourceUrl = "https://github.com/shayanhabibi/Partas.Build"
             DocsUrl = "https://shayanhabibi.github.io/Partas.Build/" }
    let loony =
           { Id = Loony
             Name = "loony"
             Summary = "A lock-free queue for passing reference objects between concurrent producers and consumers."
             Detail = "A pure Nim implementation of the Giersch–Nolte concurrent FIFO algorithm, developed for CPS continuations, with deterministic memory reclamation and cache-aware queue layout."
             Category = Concurrency
             GraphLabel = "Multiple producers pass work through a FIFO queue to multiple consumers."
             Technologies = [| "Nim"; "Lock-free"; "MPMC queues" |]
             SourceUrl = "https://github.com/nim-works/loony"
             DocsUrl = "https://nim-works.github.io/loony/loony.html" }
    let wrflock =
           { Id = Wrflock
             Name = "wrflock"
             Summary = "Coordinate writing, concurrent reading and memory reclamation with a compact synchronisation primitive."
             Detail = "A pure Nim implementation of Mariusz Orlikowski's WRFLock: an eight-byte state machine with separate write, read and free capabilities, using futex waits or configurable yielding."
             Category = Concurrency
             GraphLabel = "Write, read and free states form a synchronisation cycle with parallel readers."
             Technologies = [| "Nim"; "Futexes"; "Synchronisation" |]
             SourceUrl = "https://github.com/shayanhabibi/wrflock"
             DocsUrl = "https://shayanhabibi.github.io/wrflock/wrflock.html" }

    let fableElectron =
        {
            Id = FableElectron
            Name = "Fable.Electron"
            Summary = "Electron-js bindings for Fable, with F# type safe IPC"
            Detail = "Bindings for the Electron-js API, with F# type safe IPC, with cron workflow regenerating the bindings for new Electron versions, and creating PRs on Major releases or errors."
            Category = Interop
            GraphLabel = "Compiles F# from the same source as electron TypeScript bindings"
            Technologies = [| "F#"; "electron-js"; "Fable"; (* "CI/CD"; "Generator" *) |]
            SourceUrl = "https://github.com/fable-hub/Fable.Electron"
            DocsUrl = "https://fable-hub.github.io/Fable.Electron/"
        }

    let all = [|
        partasSolid
        xantham
        partasBuild
        ranvier
        loony
        wrflock
        fableElectron
    |]

    let categories = [| All; Compilers; Interop; ReactiveSystems; Automation; Concurrency |]
    let graphUrl (project: Project) = "/graphs/" + project.Id.ToString() + ".svg"
    let caseStudyUrl (project: Project) =
        match project.Id with
        | PartasSolid -> Some "/partas-solid/"
        | Xantham -> Some "/xantham/"
        | PartasBuild -> Some "/partas-build/"
        | Ranvier -> Some "/ranvier/"
        | _ -> None
    let filter category =
        if category = All then all
        else all |> Array.filter (fun project -> project.Category = category)
