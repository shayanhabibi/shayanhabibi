(**
---
title: "Partas.Solid: F# components to Solid JSX"
description: A working F# component, its compilation path, and the design tradeoffs behind Partas.Solid.
layout: case-study
---
*)

(**
# Partas.Solid: F# components to Solid JSX

I wanted to write a component library in F# while keeping the authoring style and
readable output of Oxpecker.Solid. That meant extending the DSL beyond native
elements: custom components needed typed properties and a way to be composed
using the same computation-expression syntax.

Partas.Solid is my fork of [Oxpecker.Solid](https://github.com/lanayx/Oxpecker).
I developed the component model and Fable AST transformations on that foundation.
The project's [origin story](https://github.com/shayanhabibi/Partas.Solid/blob/master/docs/site/content/about/oxpecker-fork.md)
explains the collaboration and the reasons for the fork.

## A component you can inspect and run

This small example keeps state in a Solid signal. Adding a request updates the
display; resetting returns it to zero. The F# shown here is compiled during this
site's build and mounted below it. It is the running example's source.
*)
(*** hide ***)
#r "nuget: Partas.Solid, 3.0.0-local.e08ad85"
#r "nuget: Xantham.Fable.Core.TS"
open Partas.Solid
open Partas.Solid.Aria

(*** solid render=CompilationDemo jsx ***)
[<SolidComponent>]
let CompilationDemo () =
    let count, setCount = createSignal 0
    div (id = "compilation-demo", class' = "compilation-demo") {
        p (role = "status", ariaLive = "polite") { $"Build requests: {count ()}" }
        button (type' = "button", onClick = fun _ -> setCount (count () + 1)) {
            "Add request"
        }
        button (type' = "button", onClick = fun _ -> setCount 0) { "Reset" }
    }

(**
<noscript>The interactive example needs JavaScript. Its source and the walkthrough remain available above and below.</noscript>

## The compilation path

1. **F# source.** The compiler checks the component's types. Computation expressions
   describe elements and children; attributes identify bodies the plugin transforms.
2. **Fable and the Partas plugin.** The plugin rewrites supported component and
   element patterns into JSX. This is where the F# DSL becomes the component
   structure that Solid expects.
3. **Solid's JSX compiler.** Solid compiles that JSX into browser code. Partas
   does not replace Solid's runtime or implement its own browser reactivity engine.
4. **A browser bundle.** On this site, the Nacara plugin bundles the compiled
   example and mounts it in the static page. The browser runs JavaScript, not F# scripts.

The generated JSX below the F# example lets you inspect the intermediate output instead
of treating compilation as a black box.

## Design choices and tradeoffs

**Readable JSX as an intermediate representation.** Keeping recognisable components
and props in the output makes it possible to compare a failing example with Solid
documentation. The cost is another compilation step and a dependency on the
supported Solid compiler version.

**Typed components in the element DSL.** Custom component types can declare props
and use a `SolidTypeComponent` member for their implementation. They can then be
composed with the same syntax as native elements. Supporting this requires more
AST transformation logic than a wrapper around element constructors.

**Preserving reactive reads.** A property read must remain visible to Solid's
dependency tracking. The plugin therefore needs to preserve the semantics of
props and children through its rewrites. F# type checking alone cannot prove that
the generated component behaves correctly: output snapshots and browser tests
address different parts of that problem.

**An explicit compatibility boundary.** This site uses a pinned local Partas.Solid
3 build with Solid 2 release-candidate packages. That is a reproducible preview,
not a claim of compatibility with every Solid 1 component library.

## Evidence in this site

The homepage's project explorer is authored in an `.fsx` file, reuses an F#
`ProjectRow` component, and compiles through this same pipeline. Filtering and
carousel navigation are browser behaviours; a separately rendered HTML list
keeps project information accessible when the browser bundle is unavailable.

That fallback belongs to this site's design. It is not automatic server rendering
provided by Partas.Solid.

## Inspect the implementation

- [Plugin source](https://github.com/shayanhabibi/Partas.Solid/blob/master/src/Partas.Solid.FablePlugin/Plugin.fs)
- [JSX output and component examples](https://github.com/shayanhabibi/Partas.Solid/blob/master/docs/site/content/about/jsx-output.md)
- [Component attribute and transformation scope](https://github.com/shayanhabibi/Partas.Solid/blob/master/docs/site/content/guide/solid-component-attribute.md)
- [Project repository](https://github.com/shayanhabibi/Partas.Solid)

[Discuss an engineering role or consulting project](../#contact).
*)
