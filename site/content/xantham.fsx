(**
---
title: "Xantham: TypeScript APIs in F#"
description: Compiler-backed binding generation, an actual type-mapping example, and the limits of translating TypeScript to F#.
layout: case-study
---
*)

(**
# Xantham: TypeScript APIs in F#

Using a JavaScript package from F# means representing its types and runtime imports.
Maintaining those bindings by hand becomes harder as declarations span files,
dependencies and public entry points. I built Xantham to generate that surface
using the TypeScript compiler's own type information.

Xantham began as an exploratory fork of [Glutinum](https://github.com/glutinum-org/cli).
My work includes package-wide generation, reusable compiler access from .NET,
generated imports and reports of type-mapping compromises. The
[project history](https://github.com/shayanhabibi/Xantham/blob/master/site/content/blog/15092026-xantham.md)
describes those roots and the subsequent direction.

## A mapping you can inspect

These declarations come from the generator's optional-parameter test fixture:

```typescript
export declare function marked(a: string, b?: string): string;
export declare function unioned(a: string, b: string | undefined): string;
export declare function required(a: string, b: string): string;
```

Below is an excerpt of the fixture's generated F# exports. The site type-checks
this excerpt; it does not run the JavaScript fixture or regenerate it on every
site build.
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
The interesting result is the compromise: `marked` permits omission in TypeScript,
while `unioned` requires an argument whose value may be `undefined`. Both become
an optional F# argument here. The generator's findings preserve information about
the source distinction so it can be reviewed. A binding that compiles is not
automatically a lossless translation.

## The generation path

1. **Harvest public declarations.** Identify the package surface and public entry points.
2. **Resolve types.** Follow compiler information across declarations and package boundaries.
3. **Shape the F# API.** Select F# representations for types, members and imports.
4. **Render files and findings.** Emit bindings alongside reports of decisions that need attention.

`Xantham.TypeScript.Wire` supplies the compiler client. Its protocol bindings are
generated from TypeScript sources and published independently, so another .NET
tool can query the compiler without adopting the generator's F# mappings.

## Design choices and tradeoffs

**Compiler-backed resolution.** Querying the compiler avoids building a separate
TypeScript type checker. It also creates a compatibility boundary: Wire's protocol
is unversioned, so its generated bindings must match the pinned compiler build.
A mismatch can appear as a decoding failure.

**Batching over the synchronous protocol.** The client uses a `MailboxProcessor`
to batch overlapping requests in-process. The project chose this over the larger
asynchronous protocol envelope for memory efficiency. This is a design decision,
not a performance claim about every workload.

**Explicit mapping compromises.** Some TypeScript types need broader F# representations.
Per-symbol findings make these choices inspectable rather than presenting every
generated API as equally precise. Consumers still need to review the APIs they use.

**Separate declarations from imports.** A declaration's source file is not necessarily
its public runtime import path. Package-wide generation must preserve that distinction
and decide how dependency types are shared between generated outputs.

## Evidence and validation

The repository includes declaration fixtures, generated golden files, consumer
compile gates and runtime checks for JavaScript behaviour. Snapshot changes need
review; compilation alone does not prove that an import reaches the right value.

This site's browser code uses the published TypeScript DOM support bindings.
That is one concrete consumer, not evidence that every npm library is supported.

## Inspect the implementation

- [TypeScript fixture](https://github.com/shayanhabibi/Xantham/blob/master/tests/fixtures/optional-param-lab/index.d.ts)
- [Generated F# fixture](https://github.com/shayanhabibi/Xantham/blob/master/tests/Xantham.Generator.Tests/golden/optional-param-lab/OptionalParamLab.fs)
- [Generator workflow](https://shayanhabibi.github.io/Xantham/dev/generator/)
- [Compiler client documentation](https://shayanhabibi.github.io/Xantham/wire/)
- [Project repository](https://github.com/shayanhabibi/Xantham)

[Discuss an integration or developer-tooling project](/#contact).
*)
