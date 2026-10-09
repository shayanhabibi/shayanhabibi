(**
---
title: "Partas.Build: workflows that describe their own CLI"
description: An F# build DSL where composed stages determine command inputs, inspection, and execution.
layout: case-study
---
*)

(**
# Partas.Build: workflows that describe their own CLI

Partas.Build is an F# DSL for build workflows and the command-line tools that
run them. Its central design choice is that a stage declares the inputs it
reads, and a command derives its options from the stages and pipelines it
composes. The workflow and its CLI are therefore one definition, rather than
two lists that have to be kept in sync.

Partas.Build is aimed at F#/.NET maintainers who want to reuse build work
across terminal commands, CI, and automation without separately wiring every
option to every stage. A stage declares what it needs; the command derives its
options from the stages it runs. That keeps the workflow and its CLI together,
but means all inputs must be known before execution starts.

Think of the build as the source model. The command-line interface is
calculated from the work selected for a command:

:::steps
### Declare work

A reusable stage says what value it needs, such as a build configuration.

### Compose stages

A command selects the stages that make up `build`, `test`, or `publish`.

### Derive the CLI

The command exposes the union of the selected stages' inputs, with shared
inputs registered once.

### Inspect or invoke

Help, schema, and execution all operate on that same composed command.
:::

## The mismatch it addresses

A CLI-first design declares and parses options, then passes values through a
handler to the work:

```text
options -> parser -> handler -> workflow
```

That explicit seam is flexible, but the option declaration, handler wiring,
and reusable stages can drift apart.

## Workflow-first, not CLI-first

Partas.Build reverses that direction: stages declare the inputs they consume,
then composition derives the command's options.

```text
CLI-first:     options -> parser -> handler -> stages
Partas.Build:  inputs + stages -> command -> parser / execution
```

`System.CommandLine` is still the parser underneath Partas.Build, alongside
the incorporated
[`FSharp.SystemCommandLine`](https://github.com/jordanmarr/FSharp.SystemCommandLine).
The difference is what feeds it: a composed workflow rather than a separately
maintained command declaration. Unlike general workflow/task DSLs such as
[`Fun.Build`](https://github.com/slaveOfTime/Fun.Build) and
[`FAKE`](https://fake.build/), Partas.Build's emphasis is that composition
also defines the typed CLI. Fun.Build influenced its pipeline implementation;
this is a distinction of focus, not a claim that those tools cannot accept
arguments.

This is most useful when stages are reused. Change the input where the stage
uses it, and commands that include that stage expose the updated option.
Runtime-dependent option discovery is deliberately out of scope: the full
input set has to be available before execution. Here is the pattern:

## Declare an input once, where the workflow reads it

Here, each `compile` stage binds `--configuration`. The command gets that option
from the stages in its pipeline; it does not register it separately.

*)
(*** hide ***)
#i "nuget: https://nuget.cloudsmith.io/shayanhabibi/shayanhabibi-partas-build/v3/index.json"
#r "nuget: Partas.Build, 0.8.1"
open Partas.Build

(*** show ***)
let configuration =
    Input.option<string> "--configuration"
    |> Input.alias "-c"
    |> Input.def "Release"
    |> Input.acceptOnlyFromAmong [ "Debug"; "Release" ]

let compile project = input {
    let! config = configuration
    return stage $"compile {project}" {
        run (cmd $"dotnet build {project} -c {config}")
    }
}

let verify project = input {
    let! config = configuration
    return stage $"test {project}" {
        run (cmd $"dotnet test {project} -c {config}")
    }
}

let projects = [ "src/Core/Core.fsproj"; "src/App/App.fsproj" ]

let root = Command.root {
    description "Repository build"
    command "build" {
        description "Restore and compile the projects"
        pipeline "compile" {
            stage "restore" { run "dotnet restore" }
            for project in projects do
                compile project
        }
    }
    command "test" {
        description "Test the core project"
        pipeline "test" {
            verify "src/Core/Core.fsproj"
        }
    }
}

let invoke args = Command.invoke args root

(**
Both commands use the same `configuration` input; it appears once in each
command's help, with the default and allowed values from its definition.
Remove the stages that use it, and the option disappears from that command.

`Command.root` builds the tree without parsing arguments or running stages.
`Command.invoke` parses an argument sequence and executes the selected
workflow. For a script or executable, the final entry point can pass its
arguments to that same root.

### A real command from the repository

This is not output from the `Core`/`App` teaching example. It is the
Partas.Build repository's own `build` command, captured at source revision
`6274d7a`. Rerun the command from that repository to inspect its current
output. The teaching sample references package 0.8.1; the transcript below
comes from the separate source snapshot:

```bash
dotnet run --project Build.fsproj -- build --help
```

```text
Options:
  -q, --quick                          Skips restores, installations, cleaning and formatting
  -c, --configuration <d|debug|r|release>
  --explain                            Print the resolved stage tree and exit
  --report <path>                      Write the run result as JSON to this file
  --json                               Write compact JSON output
  --schema                             Print this command and its options as JSON
```

The selected stages bind `--quick` and `--configuration`; no separate
registration list is needed.

The schema exposes the same interface as data. This excerpt is trimmed from
the JSON response at the same revision:

```bash
dotnet run --project Build.fsproj -- build --schema --json
```

```json
{
  "formatVersion": 1,
  "command": "build",
  "options": [
    { "name": "--quick", "aliases": ["-q"], "type": "boolean" },
    {
      "name": "--configuration",
      "aliases": ["-c"],
      "type": "string",
      "choices": ["d", "debug", "r", "release"]
    }
  ]
}
```

### Preview before running

The command's `--explain` output is a plan, not a build: it does not run
restore, clean, or compile. This abbreviated excerpt has machine-specific
paths removed:

```bash
dotnet run --project Build.fsproj -- build --explain --json false
```

```text
build
├─ restore
│  └─ $ dotnet restore …
├─ clean
│  └─ $ clean build outputs
└─ build
   ├─ build Partas.Build
   ├─ build Partas.Build.Baked
   ├─ …
   └─ build Partas.Build.ExternalAnnotations
```

The full plan has seven project-build stages. Add `--quick` and `--explain`
to see restore and clean marked as skipped.

## Inputs are known before execution

`input { }` returns an input specification together with the stage or pipeline
that uses those values. Its `let!` and `and!` bindings are applicative: all
inputs are gathered together before parsing. The computation expression
deliberately has no sequential `Bind`, because a later input reader depending
on an earlier parsed value would make the complete option set unknowable while
the command is being built.

This restriction keeps command construction separate from running work:
`--help` and `--explain` can inspect the command without triggering a build.
Values that depend on one another can be derived after binding; effects belong
in a stage or producer, not in an input reader. Input specifications travel
with reusable blocks through nested composition.

## Defer operations until the workflow runs

An `Operation<'T>` packages work for later execution under a stage's runtime
context. Building a workflow or operation does not run it. The stage decides
when to execute it, so work naturally participates in the stage's working
directory, environment, cancellation, output routing, and failure policy.

This seam is useful when a step needs more than a process exit code. Operations
can stream a command, capture its output as data, or return a `CommandResult`
even when the process exits unsuccessfully. They can be mapped, sequenced, or
run in parallel; a failure can retain its cause and captured evidence for the
workflow to report. Within one operation, sequencing orders work in that step.
It does not by itself declare a dependency between stages.

The operation stays deferred until its stage runs, so it uses that stage's
working directory, environment, output routing, and cancellation policy.
Blocking F# work still has to cooperate with cancellation. Captured process
output is raw application data and may contain secrets.

For example, `attemptCapture` lets a step inspect output and decide what an
exit code means. Unlike `executeCapture`, it returns a completed process result
even when the exit code is not accepted by the stage:

```fsharp
let inspectSdk =
    stage "inspect SDK" {
        runOperation (
            attemptCapture (cmd $"dotnet --version")
            |> Operation.map (fun result ->
                printfn "exit code: %d" result.ExitCode
                printfn "stdout: %s" (result.Stdout.Trim()))
        )
    }
```

For a process that starts and completes, the mapper receives stdout, stderr,
and the exit code. A start failure or cancellation still fails the operation.

## Share computed results with producers

Some work is neither a CLI input nor a single step's private operation. A
release workflow might fetch a manifest once, then use its typed result in
multiple stages. A `Producer<'T>` describes such deferred work with its own
inputs and prerequisites; consumers declare that they require the producer's
result.

A producer runs only when required, once per invocation, and consumers of the
same producer handle share its result. Prerequisites establish ordering:
unlisted producers are scheduled before their consumers, or can be placed
explicitly in a pipeline. If a producer fails or is skipped, dependent
consumers are skipped with a dependency reason instead of running with missing
data. Retrying a consumer does not repeat a successful producer outside the
retried scope.

This makes shared data flow explicit. There is one important scope rule:
producers used by parallel work must be placed before that parallel scope.

This small example declares one deferred version value and consumes the same
producer handle in two stages:

```fsharp
let version =
    Producer.emptyDefine "version" (
        Operation.ofAsync (async {
            printfn "resolve version"
            return "1.2.3"
        })
    )

let consumeVersion name =
    Stage.consuming name (DependencySpec.require version) (fun value ->
        Operation.ofAsync (async { printfn "%s: %s" name value }))

let release = pipeline "release" {
    consumeVersion "pack"
    consumeVersion "tag"
}
```

On one invocation, the producer supplies one result to both consumers:

```text
resolve version
pack: 1.2.3
tag: 1.2.3
```

The result is shared for that invocation, not cached across future runs.

## Inspect the command before execution

Inspection is part of the command model, not a parallel hand-maintained
description:

- `--help` shows the inputs accepted by the selected command.
- `--schema` serializes the command tree, options, and arguments so tools can
  discover the interface.
- `--explain` prints the stage and step plan without running those steps.
- `--json` emits structured run results, including outcomes, failures, reports,
  and timings; `--report` saves a run result to a file.

Because the command is assembled before it runs, people and tools can inspect
its inputs and plan first. JSON `--explain` uses a static plan and does not
evaluate effectful conditions; text `--explain` may evaluate conditions such
as a Git branch check. During execution, child processes may write to stdout
before the final JSON result. Consumers should read the result document or use
`--report`, rather than expect stdout to contain only JSON.

Detected AI environments default to JSON output. Explicit flags can select
text or JSON for an invocation.

## Report results in GitHub Actions

GitHub Actions reporting is built into execution rather than left to each
workflow author to reproduce. In Actions, active top-level stages open
collapsible log groups, while nested and parallel work stays inside the
current group. Command invocations append a Markdown stage-timing report to
`GITHUB_STEP_SUMMARY`, including outcomes and elapsed times for successful,
failed, and skipped stages. Structured annotations can also become native
runner diagnostics; locally, they remain readable diagnostics.

The same workflow can run locally or in Actions, where its stages show up as
log groups, a timing summary, and runner annotations. An error annotation
doesn't itself fail the stage; the workflow's failure policy still decides
that. Problems writing the summary are reported without changing the pipeline
result.

## The workflow still owns execution

The CLI describes and configures a workflow; stages do the work. A step can
run a process or F# code, stages can be composed into pipelines, and settings
can control conditions, working directories, environment variables, timeouts,
parallelism, retries, failure handling, and cleanup.

For process commands, `cmd` keeps interpolated values as individual arguments.
That matters for paths containing spaces and avoids turning an argument list
into a shell string. `runSensitive` masks interpolated values in the command
label, but does not redact output produced by the child process.

## What the design commits to

**Derive options from composition.** A command has no separate option list.
Its options depend on the stages it includes, so input declarations must
survive block reuse and nesting.

**Collect inputs before parsing.** Independent CLI values are available up
front; a later input cannot depend on an earlier parsed value. This lets the
command expose its interface before it does any work, but keeps runtime logic
out of input declarations.

**Separate operations from dependencies.** Operations compose work inside a
step. Producers pass typed results and prerequisites between steps. Use the
former for local sequencing, the latter when stages depend on shared work.

**Expose the command to tools.** `--explain`, `--schema`, and `--json` make
plans, inputs, and results available to scripts and agents. GitHub summaries
and annotations carry run details into CI; they don't decide whether a run
should fail.

**Keep stages and pipelines as F# values.** They can be reused across commands
and projects, while each workflow still chooses its own settings and failure
behavior.

**Validate inputs, not the world.** Types and validation constrain what the
CLI accepts; they cannot ensure an external tool is installed, a process
succeeds, or a deployment is safe.

**Compose existing ideas.** Partas.Build credits [Fun.Build](https://github.com/slaveOfTime/Fun.Build)
for much of its pipeline implementation and includes
[FSharp.SystemCommandLine](https://github.com/jordanmarr/FSharp.SystemCommandLine),
whose author is credited in the project README. Partas.Build brings those
foundations together around input-bearing stages and derived command options.

## Where it is used and tested

This site's `build`, `check`, `test`, and `serve` commands are themselves
Partas.Build workflows. They restore and compile the workspace, generate the
site, and run static or browser checks. The declarations are in the site's
`Build/Program.fs`, and GitHub workflows invoke the same CLI.

Partas.Build uses the same approach in its own
[`Build/Program.fs`](https://github.com/shayanhabibi/Partas.Build/blob/6274d7a/Build/Program.fs):
the CLI defines `build`, `test`, `publish`, `bump`, and `docs` commands using
Partas.Build workflows. The build and publish paths compose shared restore,
clean, and seven-project build work; test and publish also include the test
projects and suites. Concretely, the CLI options and inspectable plans come
from those same command definitions, rather than a separate option-wiring
layer. This shows the command structure and its scale; it isn't a measurement
of maintenance savings or evidence of wider adoption.

Tests cover the behaviors described above:

- [`CommandTests`](https://github.com/shayanhabibi/Partas.Build/blob/6274d7a/tests/Partas.Build.Tests/CommandTests.fs)
  verifies stage-declared options are registered and a shared option across
  pipelines is registered once.
- [`DependencyTests`](https://github.com/shayanhabibi/Partas.Build/blob/6274d7a/tests/Partas.Build.Tests/DependencyTests.fs)
  verifies two consumers share one producer execution in an invocation and
  that the next invocation executes it again.
- [`ExplainTests`](https://github.com/shayanhabibi/Partas.Build/blob/6274d7a/tests/Partas.Build.Tests/ExplainTests.fs)
  verifies explain renders without running steps or producer callbacks.
- [`ExecutionTests`](https://github.com/shayanhabibi/Partas.Build/blob/6274d7a/tests/Partas.Build.Tests/ExecutionTests.fs)
  distinguishes captured results for unaccepted exit codes from
  `executeCapture` failures that retain captured output as evidence.

## Explore the implementation

- [Overview](https://shayanhabibi.github.io/Partas.Build/build/)
- [Inputs and help](https://shayanhabibi.github.io/Partas.Build/build/inputs/)
- [Composition](https://shayanhabibi.github.io/Partas.Build/build/composition/)
- [Execution](https://shayanhabibi.github.io/Partas.Build/build/execution/)
- [Capabilities, operations, producers, and GitHub reporting](https://shayanhabibi.github.io/Partas.Build/build/capabilities/)
- [Agents and JSON](https://shayanhabibi.github.io/Partas.Build/build/agents/)
- [Project repository](https://github.com/shayanhabibi/Partas.Build)

[Discuss a build or workspace-automation project](../#contact).
*)
