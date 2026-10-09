(**
---
title: "Xantham: from TypeScript declarations to F# bindings"
description: Built to replace hand-written TypeScript bindings with customizable, traceable F# APIs generated from package graphs.
layout: case-study
---
*)

(**
# Xantham: from TypeScript declarations to F# bindings

Xantham turns npm packages' TypeScript declarations into F# bindings for Fable,
so consumers can work with typed package APIs without hand-maintaining each
binding. It is used in production by FSharp.CloudEdge and by this website.

The problem that led me to Xantham was the work of getting TypeScript
declarations into usable F# bindings. The options were to write and maintain
those bindings by hand, or use Glutinum. When I was working with it, Glutinum
could generate from a single declaration file, but did not reliably follow a
package's cross-file and cross-package type relationships: references could
stop at placeholders, and modern TypeScript shapes often did not map
successfully. It also gave me too little control over the generated API and
too little provenance to understand why a particular type had been emitted
that way. This describes the version and workflow I encountered, not a claim
about Glutinum's current capabilities.

Xantham grew out of an exploratory fork of
[Glutinum](https://github.com/glutinum-org/cli), but the aim was not just to
automate more of the same file-at-a-time work. It was to generate from the
package's declaration graph, let consumers shape the resulting bindings, and
make mapping decisions inspectable. The JavaScript imports matter too: a
declaration can live in an internal `.d.ts` file while its public runtime
value is exported from the package root.

## From package declarations to bindings

The package is the input model. Xantham follows its public declarations,
resolves their relationships, then emits F# declarations alongside runtime
imports and mapping findings:

:::steps
### Find the public surface

Start at the package's entry points and collect the declarations that make up
its public API.

### Resolve the graph

Ask TypeScript's compiler for type information across files and package
boundaries, rather than implementing another TypeScript type checker.

### Shape the F# API

Apply small, ordered transformations to select F# types, members, modules, and
imports.

### Render bindings and findings

Write F# output and per-symbol reports so consumers can inspect mappings that
are wider or more ergonomic than the source type.
:::

That separation matters for imports. The file that declares a function does
not necessarily identify the module a JavaScript consumer imports. A function
declared in `dist/internal/client.d.ts`, for example, may still be exported
from `example-client`. Xantham tracks the public runtime path instead of
assuming the declaration's file path is the import path.

## One TypeScript distinction, made visible

The generator's optional-parameter fixture isolates three similar-looking
signatures:

```typescript
export declare function marked(a: string, b?: string): string;
export declare function unioned(a: string, b: string | undefined): string;
export declare function required(a: string, b: string): string;
```

Here is the corresponding excerpt from the fixture's generated F# exports.
The site type-checks this example; it does not run the JavaScript fixture or
regenerate the output on every site build.
*)
(*** hide ***)
#r "nuget: Xantham.Fable.Core.TS, 0.1.0"
open Fable.Core

(*** show ***)
[<Erase>]
type Exports =
    [<Import("marked", "optional-param-lab")>]
    static member marked (a: string, ?b: string) : string = jsNative
    [<Import("unioned", "optional-param-lab")>]
    static member unioned (a: string, ?b: string) : string = jsNative
    [<Import("required", "optional-param-lab")>]
    static member required (a: string, b: string) : string = jsNative

(**
`marked` allows the caller to omit `b`; `unioned` requires an argument, but
that argument may be `undefined`. Both become an optional F# argument in this
mapping, while `required` remains required. That is an ergonomic projection,
not a lossless translation. The findings say exactly why:

- **MB001 — OptionalParameterAsOption:** TypeScript marks the parameter with
  `?`, so callers may omit it. Xantham represents it as an F# optional
  argument, `?b: string`.
- **MB006 — OptionalParameterFromUnion:** the parameter itself is required,
  but its type includes `undefined`. Xantham also represents it as `?b:
  string`, an ergonomic F# shape that does not force the caller to supply an
  option whose value may be absent.

That difference affects calls: TypeScript accepts `unioned("x", undefined)`
but rejects `unioned("x")`; the generated F# optional argument permits the
omitted form. The ergonomic mapping therefore admits a call shape that the
original TypeScript signature does not.

The generated F# signatures are the same, but the manifest distinguishes
whether absence comes from the parameter marker or from the type. That
provenance lets a consumer see what the generator understood and decide
whether the compromise is suitable.

The symbol's overall fidelity is the worst tier among its findings:

- **Exact:** F# enforces the same values and operations as the TypeScript
  declaration.
- **Ergonomic:** the representation has an idiomatic F# shape while preserving
  the intended meaning.
- **Widened:** TypeScript information is lost, so the generated type accepts
  or produces a broader set of values.
- **Escape:** the construct is not represented; the consumer must handle it
  outside the generated binding.

The finding name, tier, pipeline pass, source symbol, and message are recorded
in the per-symbol report, so a reader can inspect the reason rather than infer
fidelity from generated syntax alone.

## Make the generated API fit

This is an API, not just a configuration promise. Configuration can select
public inputs and subpaths, name the generated modules and runtime imports,
and choose how dependency types are shipped, referenced, mapped, or widened.
For transformations beyond those settings,
`Xantham.Generator.Customization` exposes a `GeneratorExtension` over a
resolved semantic snapshot.

One checked-in example selects an input type through the compiler's actual
`HTMLElement` ancestry and emits a companion interface for selected inherited
properties (`value`, `title`, and `tagName`). A component can implement that
marker without manually implementing the properties. The same extension can
emit framework-plugin stubs or direct JavaScript property accessors; the latter
also works from a compiled binding DLL. When no extensions are registered,
the existing generation functions retain their default behavior.

This is how a consumer adapts bindings without editing generated files: write
a small extension against resolved declarations, then generate and compile the
result as part of that consumer's build.

## Carry types into dependent packages

Package relationships do not stop at one generation run. Suppose package
`example-adapter` refers to types exported by `example`. Generating both in
isolation can duplicate the same declaration under different F# identities.
Xantham can emit a **declaration catalog** with the producer's generated
bindings; a later run consumes that catalog to recognize and reuse the
producer's type identities while generating the dependent package.

Enable the catalog in the producer configuration:

```json
{
  "module": "Example",
  "declarationCatalog": true
}
```

The producer writes `declarations.json` beside its F# output. Point the
dependent package's configuration at it:

```json
{
  "module": "Example.Adapter",
  "entry": "adapter.d.ts",
  "runtime": "example/adapter",
  "declarationReferences": ["/bindings/example/declarations.json"]
}
```

The dependent binding keeps its own JavaScript import path, while shared type
references resolve to the producer's F# identities. The catalog is metadata
for coordinating generation, not a substitute for the generated F# types:
compile or reference the producer's binding before the dependent binding.
Catalogs also validate declaration and API compatibility, rather than assuming
that matching package names or TypeScript major versions guarantee reuse. This
turns generated output into a typed dependency that downstream package
bindings can build on.

## A compiler client of its own

**Xantham.TypeScript.Wire** is the .NET client used by the generator to query
TypeScript's `tsc --api` server. Its protocol bindings are generated from
TypeScript sources and published independently, so other .NET tools can query
compiler syntax, symbols, types, and diagnostics without adopting Xantham's
F# mappings.

The generator uses the synchronous protocol and batches overlapping requests
in-process with a `MailboxProcessor`. The project chose that approach over the
larger asynchronous protocol envelope for memory efficiency; it is a design
choice, not a general performance guarantee. Wire must also match the pinned
TypeScript compiler build. An upstream protocol change can require regenerating
the bindings and adapting the client.

## How do I know the output is right?

No single test can prove a generated binding correct. The project uses
independent gates for output stability, F# usability, JavaScript behavior, and
customization integration:

1. **Regenerate against the compiler and compare the whole result.** Each
   fixture runs through the live TypeScript compiler and generation pipeline.
   The test compares every emitted file byte-for-byte with its committed
   golden, including the F# module, aggregate manifest, and per-symbol
   `symbols.jsonl`; it also rejects stale files. These goldens detect
   unintended output changes, not semantic correctness by themselves. Each
   fixture is generated twice in fresh sessions to check deterministic output.
   At the current source snapshot, the corpus has 114 fixture directories and
   130 generated F# files, covering focused TypeScript edge cases as well as
   pinned real packages.
2. **Compile the generated F#.** An ordinary project build compiles the
   committed golden corpus against Fable.Core 5.2.0 and Xantham's support
   libraries. The project includes the goldens by wildcard, so adding a
   generated file does not depend on remembering to add it to a test list.
   A separate compile gate checks TypeScript compiler-library ownership under
   another library profile.
3. **Run the erased bindings.** The opt-in run gate compiles selected generated
   bindings with Fable and executes them in JavaScript. Assertions inspect
   JavaScript-visible values and behavior: imports, constructors, argument
   omission, callbacks, tagged unions, property keys, inheritance, and more.
   This catches interop failures that an F# type-check cannot.
4. **Test the customization in its host framework.** The Partas acceptance
   gate generates the companion through the public extension API, compiles a
   component with the real Partas Fable plugin, checks its emitted JSX, and
   renders it to verify runtime properties. It also checks direct property
   access from generated source and a compiled DLL, including inherited and
   escaped keys; a separate compile test verifies that writing a readonly
   property fails.

The CI test workflow runs the complete suite with the run gate enabled. It
also uploads the verified golden corpus with the source commit and a SHA-256
for every golden file. This makes a generated change reviewable and ties the
evidence to the exact source revision. These checks provide regression
evidence for the tested corpus and behaviors; they are not a proof that every
TypeScript package or type shape is supported.

From the Xantham repository root, run the suite and runtime checks with:

```bash
dotnet fsi build.fsx -- test --run-gate
```

The ordinary `test` command runs the Expecto suite and builds the solution,
which compiles the committed golden corpus. `--run-gate` adds Fable execution
of selected bindings and the Partas customization acceptance checks. CI uses
the same path with `--ci`:
`dotnet fsi build.fsx -- test --ci --run-gate`.

Production use provides a different kind of evidence from tests. **FSharp.CloudEdge
uses Xantham to generate bindings for Cloudflare APIs**; its public repository
contains the generation and catalog workflows. **This website itself consumes
Xantham-generated TypeScript DOM support bindings** in browser code for
`globalThis`, `document`, element queries, and layout measurements. The
optional-parameter fixture above is a separate test example; it is not the
code running this page.

## Inspect the implementation

**Mapping and provenance**

- [Optional-parameter TypeScript fixture](https://github.com/shayanhabibi/Xantham/blob/master/tests/fixtures/optional-param-lab/index.d.ts) and its [generated F#](https://github.com/shayanhabibi/Xantham/blob/master/tests/Xantham.Generator.Tests/golden/optional-param-lab/OptionalParamLab.fs)
- [Per-symbol findings](https://github.com/shayanhabibi/Xantham/blob/master/tests/Xantham.Generator.Tests/golden/optional-param-lab/symbols.jsonl)

**Validation and customization**

- [Golden comparisons and determinism tests](https://github.com/shayanhabibi/Xantham/blob/master/tests/Xantham.Generator.Tests/Pipeline.test.fs), [generated-output compile gate](https://github.com/shayanhabibi/Xantham/blob/master/tests/Xantham.Generator.CompileGate/Xantham.Generator.CompileGate.fsproj), and [JavaScript runtime gate](https://github.com/shayanhabibi/Xantham/blob/master/tests/Xantham.Generator.RunGate/Program.fs)
- [CI test workflow](https://github.com/shayanhabibi/Xantham/blob/master/.github/workflows/test.yml)
- [Customization guide and runnable example](https://shayanhabibi.github.io/Xantham/xantham-cli/guide/customization/) · [Partas acceptance gate](https://github.com/shayanhabibi/Xantham/tree/master/tests/Xantham.Generator.PartasGate)

**Package composition and consumers**

- [Dependency, shared-type, and declaration-catalog guide](https://shayanhabibi.github.io/Xantham/xantham-cli/guide/dependencies/)
- [FSharp.CloudEdge](https://github.com/fsprojects/FSharp.CloudEdge), including its [catalog workflow](https://github.com/fsprojects/FSharp.CloudEdge/blob/main/scripts/catalogs.mjs) and [binding generation script](https://github.com/fsprojects/FSharp.CloudEdge/blob/main/scripts/generate.mjs)
- [This site's generated DOM binding use](https://github.com/shayanhabibi/shayanhabibi/blob/main/site/content/index.fsx) and [pinned Xantham support packages](https://github.com/shayanhabibi/shayanhabibi/blob/main/site/components/Profile.Components.fsproj)

**Architecture**

- [Generator workflow](https://shayanhabibi.github.io/Xantham/dev/generator/) · [TypeScript.Wire guide](https://shayanhabibi.github.io/Xantham/wire/) · [Project history and Glutinum roots](https://github.com/shayanhabibi/Xantham/blob/master/site/content/blog/15092026-xantham.md) · [Project repository](https://github.com/shayanhabibi/Xantham)

[Discuss an integration or developer-tooling project](../#contact).
*)
