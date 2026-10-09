(**
---
title: "Partas.Build: CLI options from build stages"
description: A typed F# automation DSL that derives command-line options from the stages that consume them.
layout: case-study
---
*)

(**
# Partas.Build: CLI options from build stages

Build automation often employ some manner of a command-line interface, or numerous scripts.
CLI options are often defined on a command, and then passed to the stages that consume them.

This is a common source of mistakes, and maintenace burden, including:
- abstracting a stage into a reusable block, but forgetting to register its options on the command
- adding a new option to a command, but forgetting to pass it to the stage that consumes it
- removing an option from a command, but forgetting to update the stages that use it
- stale documentation or help text that does not reflect the current set of options
- missing or inconsistent defaults between the command and the stage that consumes an option
- complexity in enforcing best CLI practices, such as consistent naming, validation, parsing, orders, and help text

Keeping the consuming api in sync is a routine source of CLI and automation mistakes/friction.

## Solution: derive the CLI from the stages that consume it

Motivating work:
- [Fun.Build](https://github.com/slaveOfTime/Fun.Build) - an untyped (re: CLI consumption) F# DSL for build/work pipelines and stage composition
- [FSharp.SystemCommandLine](https://github.com/jordanmarr/FSharp.SystemCommandLine) - a typed F# DSL for `System.CommandLine`
- [Fake](https://fake.build/) - a strongly typed F# build DSL with a large ecosystem of tasks and helpers

Partas.Build takes inspiration from the stage/pipeline composition of `Fun.Build`, on the typed input and command-line parsing
of `FSharp.SystemCommandLine`.

## Declare an option where it is consumed

Partas.Build is a typed F# DSL for defining build stages and pipelines, and derives the command-line interface
from the arguments and options that the stages consume. This includes default values, validation, and help text.

The resulting CLI is statically analyzable without side effects.

This example binds `--configuration` inside the compile stage and reuses that
stage in two commands. Both commands receive the option; neither registers it
separately. The default is `Release`.

*)
(*** hide ***)
#i "nuget: https://nuget.cloudsmith.io/shayanhabibi/shayanhabibi-partas-build/v3/index.json"
#r "nuget: Partas.Build, 0.8.1"
open Partas.Build

(*** show ***)
let configuration: ActionInput<string> =
    Input.option<string> "--configuration"
    |> Input.def "Release"
    |> Input.description "The build configuration to use."

let compile: InputSpec<StageContext> = input {
    let! config: string = configuration
    return stage "compile" {
        run (cmd $"dotnet build -c {config}")
    }
}

let runTests: InputSpec<StageContext> = input {
    let! config: string = configuration
    return stage "test" {
        run (cmd $"dotnet test --no-build -c {config}")
    }
}

let main argsv = rootCommand argsv {
    description "Repository CLI automation"
    command "test" {
        description "Compile and test the solution"
        compile
        runTests
        // Stages outside a pipeline are automatically 
        // wrapped in a pipeline
    }
} 

(**
The key relationship is structural: removing `compile` from a command 
means it does not contribute its inputs (`--configuration`), so does not register in the CLI.

Inputs are strongly typed wrappers, and the value is accessed through the `input` computation expression.

## Additive computation expressions

`let!` and `and!` bind inputs, and provide their underlying values to the rest of the expression.
This enforces compile time guarantees: You cannot access an input that has not been bound, and you
cannot use an inputs binding in deriving other inputs.

The `input` computation expression is additive: 
The expression does not provide a way to create a secondary set of inputs, so this ensurers that all inputs
are known without executing any work.

## From stage input to command execution

1. **Describe typed inputs.** Define options or arguments, defaults and validation.
2. **Bind inputs in reusable blocks.** An `input` expression returns stages or pipelines together with their input requirements.
3. **Compose a command.** The command collects the inputs required by its blocks before parsing arguments.
4. **Execute stages.** Parsed values reach the stages that declared them; reports describe the resulting run.

The inputs must be known before parsing. Independent inputs can be bound with
`let!` and `and!`; runtime work belongs inside stages rather than input readers.

## Transparency for People and Agents

There are several options that are injected by default into every command, unless conflicting
with a user provided option.

#### `--explain`

Prints the resolved stage tree for the command line input passed. Does not execute any work. A 'dry' run.

#### `--json`

Writes compact JSON to stdout, for consumption by Agents.
Automatically switched on when the environment inherits AI agent variables.

#### `--report`

Writes a report of the run to a file, for consumption by Agents.

#### `--schema`

Writes schema of the entire command tree, its options and arguments, primarily for consumption by Agents.

## Design choices and tradeoffs

**Derive the CLI from composition.** This removes a separate registration step and
makes help follow the pipeline. It requires input requirements to survive wrapping
and nesting, not just direct stage composition.

**Keep stages as F# values.** Blocks can be reused between commands and scripts.
That makes shared automation practical, but defaults and inherited settings still
need deliberate design. Reusing a stage does not decide its execution policy for you.

**Typed configuration, external execution.** Input types constrain configuration;
they do not prove that a shell command exists or succeeds. Process failures,
cancellation and stage policies remain runtime concerns.

**One definition for local and CI use.** Workflows can invoke the same CLI as a
developer. This reduces duplicated orchestration, while CI still owns runner setup,
credentials and deployment policy.

## Evidence in this site

This repository's `build`, `check`, `test` and `serve` commands are Partas.Build
pipelines. They restore and compile the solution, generate the Nacara site, and
run static and browser checks. The GitHub workflows call that same CLI.

Those are working consumers of the library. They do not establish that every
workflow has run remotely or that this site has been deployed: publication remains
a separate step.

## Inspect the implementation

- [Inputs and help](https://shayanhabibi.github.io/Partas.Build/build/inputs/)
- [Composition](https://shayanhabibi.github.io/Partas.Build/build/composition/)
- [Capability reference](https://shayanhabibi.github.io/Partas.Build/build/capabilities/)
- [Project repository](https://github.com/shayanhabibi/Partas.Build)

[Discuss a build or workspace-automation project](/#contact).
*)
