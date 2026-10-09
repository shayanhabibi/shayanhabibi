module Profile.Presentation

open Nacara.Core
open Partas.Nacara.Theme
open Profile
open Oxpecker.ViewEngine
open Oxpecker.ViewEngine.Aria

let private arrow (symbol: string) = span(ariaHidden = true) { symbol }

let private staticProject expanded (project: Project) =
    article(class' = "project-row") {
        div(class' = "project-index") {
            span() { project.Category.AsString }
            img(class' = "project-illustration", src = Projects.graphUrl project, alt = project.GraphLabel, width = 160, height = 104, loading = "lazy")
        }
        div(class' = "project-copy") {
            h3() { project.Name }
            p(class' = "project-summary") { project.Summary }
            div(class' = "technologies") {
                for technology in project.Technologies do
                    span() { technology }
            }
            if expanded then
                div(class' = "project-details") { p() { project.Detail } }
            else
                details(class' = "project-details") {
                    summary() { "Technical details" }
                    p() { project.Detail }
                }
        }
        div(class' = "project-links") {
            match Projects.caseStudyUrl project with
            | Some url -> a(href = url) { "Technical walkthrough" }
            | None -> ()
            a(href = project.SourceUrl) { "Source ↗" }
            a(href = project.DocsUrl) { "Documentation ↗" }
        }
    }

let private connectionDiagram () =
    let options = DottedGraph.defaults "connecting-thread" "Ideas connected through tooling to working systems"
    let idea = DottedGraph.point 64. 70.
    let tooling = DottedGraph.point 164. 70.
    let junction = DottedGraph.point 164. 150.
    let system = DottedGraph.point 278. 150.
    let connections = [
        DottedGraph.connection [ idea; tooling; junction; system ]
        { DottedGraph.connection [ tooling; DottedGraph.point 278. 70. ] with Opacity = 0.25 }
        { DottedGraph.connection [ DottedGraph.point 64. 150.; junction ] with Opacity = 0.25 }
    ]
    let nodes = [
        DottedGraph.hollowNode "var(--paper)" idea
        DottedGraph.node tooling
        DottedGraph.hollowNode "var(--paper)" junction
        DottedGraph.node system
    ]
    let labels = [
        DottedGraph.label (DottedGraph.point 48. 46.) "IDEA"
        DottedGraph.label (DottedGraph.point 142. 46.) "TOOLING"
        DottedGraph.label (DottedGraph.point 238. 190.) "SYSTEM"
    ]
    DottedGraph.render options connections nodes labels

let headerContent: HtmlElement list = [
    a(class' = "identity", href = "/", ariaLabel = "Home") {
        span(class' = "identity-mark", ariaHidden = true) { "sh." }
        span() { "Shayan Habibi" }
    }
    nav(ariaLabel = "Main navigation") {
        a(href = "/#work") { "Selected work" }
        a(href = "/#capabilities") { "Approach" }
        a(class' = "nav-contact", href = "/#contact") { "Get in touch "; arrow "↗" }
    }
]

let private hero () =
    section(class' = "hero", ariaLabelledBy = "hero-title") {
        div(class' = "hero-copy") {
            p(class' = "eyebrow") {
                span(class' = "small-cross", ariaHidden = true) { "+" }
                " SOFTWARE ENGINEERING & CONSULTING"
            }
            h1(id = "hero-title", ariaLabel = "F# tooling. Across ecosystems.") {
                span(class' = "hero-rotator", ariaHidden = true) {
                    let first = span(class' = "hero-word")
                    first.AddAttribute { Name = "data-active"; Value = "true" }
                    first { "F# tooling." }
                    span(class' = "hero-word") { "Typed bindings." }
                    span(class' = "hero-word") { "Reactive systems." }
                    span(class' = "hero-word") { "Build automation." }
                }
                br()
                em() { "Across ecosystems." }
            }
            p(class' = "hero-description") { "I build compiler integrations, typed bindings and build tools for F# and .NET. My work connects these ecosystems through reactive interfaces, generated APIs and repeatable workflows." }
            div(class' = "hero-actions") {
                a(class' = "action-primary", href = "#work") { "Explore the work "; arrow "↓" }
                a(class' = "action-text", href = "https://github.com/shayanhabibi") { "On GitHub "; arrow "↗" }
            }
            nav(class' = "hero-contact-paths", ariaLabel = "Work together") {
                a(href = "#employment") { "Hiring" }
                a(href = "#contracting") { "Consulting / contracting" }
            }
        }
        aside(class' = "hero-note", ariaLabel = "Engineering focus") {
            div(class' = "note-heading") { span(class' = "eyebrow") { "THE CONNECTING THREAD" }; arrow "01 / 03" }
            connectionDiagram ()
            p() { "Make the complex useful."; br(); span() { "Connect the pieces that matter." } }
            div(class' = "note-foot") { span() { "F# / .NET / INTEROPERABILITY" }; arrow "↗" }
        }
    }

let private selectedWork content =
    section(id = "work", class' = "work-section", ariaLabelledBy = "work-title") {
        div(class' = "section-heading") {
            div() { p(class' = "eyebrow") { "01 — SELECTED WORK" }; h2(id = "work-title") { "Tools, from source to use." } }
            p() { "Compiler integration, typed interop"; br(); " and workspace automation." }
        }
        // Nacara has already rendered this authored Markdown/literate content as HTML.
        raw content
        div(id = "project-fallback", class' = "project-list") {
            for project in Projects.all do
                staticProject false project
        }
        a(class' = "all-work", href = "/all-projects/") { "All projects\u2003"; arrow "↗" }
    }

let private capabilities () =
    section(id = "capabilities", class' = "capabilities", ariaLabelledBy = "capabilities-title") {
        div() {
            p(class' = "eyebrow") { "02 — APPROACH" }
            h2(id = "capabilities-title") { "From the difficult part"; br(); "to the useful part." }
            p(class' = "section-description") { "I work where systems meet: turning technical constraints into tools people can use, extend and understand." }
            p(class' = "section-description") { "I won't just meet your goals, I'll surpass them." }
        }
        div(class' = "capability-list") {
            for number, title, description in [
                "01", "Languages & tooling", "Compilers, code generation and developer tools that make expressive ideas practical."
                "02", "Bridging ecosystems", "Typed bindings and integrations across .NET, JavaScript and existing systems."
                "03", "Practical automation", "Focused applications and workflow tools built around the work they need to support."
            ] do
                div() { span() { number }; h3() { title }; p() { description } }
        }
    }

let private contact () =
    section(id = "contact", class' = "contact-section", ariaLabelledBy = "contact-title") {
        div(class' = "contact-heading") {
            p(class' = "eyebrow") { "03 — WORK TOGETHER" }
            h2(id = "contact-title") { "Something worth building?" }
            p() { "Two ways to start a conversation." }
        }
        div(class' = "contact-paths") {
            for id, audience, title, description in [
                "employment", "FOR TEAMS", "Hiring an engineer.", "Compiler tooling, interoperability, reactive systems and practical software engineering."
                "contracting", "FOR PROJECTS", "Solving a specific problem.", "Integrations, automation and specialised tools, from a defined problem to a working solution."
            ] do
                article(id = id) {
                    p(class' = "eyebrow") { audience }
                    h3() { title }
                    p() { description }
                }
        }
        a(class' = "contact-placeholder", href = "mailto:shayan.habibi01@gmail.com") { "shayan.habibi01@gmail.com"; arrow "↗" }
    }

let private bodyContent content: HtmlElement list = [
    hero ()
    div(class' = "practice-line") {
        span() { "Independent work. Shared openly." }
        span() { "Compilers "; i() { "/" }; " Integrations "; i() { "/" }; " Reactive systems" }
    }
    selectedWork content
    capabilities ()
    contact ()
]

let shell (headerContent: #HtmlElement list) (content: #HtmlElement list) = Fragment() {
    a(class' = "skip-link", href = "#main") { "Skip to content" }
    div(class' = "page-shell") {
        header(class' = "site-header") { yield! headerContent }
        main(id = "main") { yield! content }
        footer(class' = "site-footer") {
            span() { "Shayan Habibi "; span(class' = "footer-muted") { "/ Software engineering" } }
            a(href = "https://github.com/shayanhabibi") { "GitHub ↗" }
            span(class' = "footer-muted") { "Built entirely in F#" }
        }
    }
}

let layout (context: PageContext<DocFrontMatter>) =
    let isCaseStudy = context.FrontMatter.Layout = Some "case-study"
    let isProjectIndex = context.FrontMatter.Layout = Some "project-index"
    let pageContent: HtmlElement list =
        if isProjectIndex then [
            div(class' = "project-catalog") {
                div(class' = "case-study catalog-heading") {
                    a(class' = "case-back", href = "/#work") { "Back to selected work" }
                    p(class' = "eyebrow") { "ENGINEERING PROJECTS" }
                    raw context.Content
                }
                section(id = "project-catalog-list", class' = "project-list", ariaLabelledBy = "catalog-title") {
                    h2(id = "catalog-title", class' = "visually-hidden") { "Project list" }
                    for project in Projects.all do
                        staticProject true project
                }
                a(class' = "all-work", href = "https://github.com/shayanhabibi?tab=repositories") { "More work on GitHub\u2003"; arrow "↗" }
            }
        ]
        elif isCaseStudy then [
            div(class' = "walkthrough-layout") {
                nav(class' = "case-navigation", ariaLabel = "On this page") {
                    p(class' = "eyebrow") { "ON THIS PAGE" }
                    ul() {
                        for heading in context.Page.Headings |> List.filter (fun heading -> heading.Level = 2) do
                            li() { a(href = "#" + heading.Anchor) { heading.Text } }
                    }
                    a(class' = "case-all-projects", href = "/all-projects/") { "All projects "; arrow "↗" }
                }
                article(class' = "case-study") {
                    a(class' = "case-back", href = "/#work") { "Back to selected work" }
                    p(class' = "eyebrow") { "ENGINEERING WALKTHROUGH" }
                    raw context.Content
                }
            }
        ]
        else bodyContent context.Content
    let pageTitle =
        if isCaseStudy || isProjectIndex then context.FrontMatter.Title + " — Shayan Habibi"
        else "Shayan Habibi — Software engineering & consulting"
    let content = Fragment() {
        head() {
            meta(charset = "utf-8")
            meta(name = "viewport", content = "width=device-width, initial-scale=1")
            title() { pageTitle }
            meta(name = "description", content = (context.FrontMatter.Description |> Option.defaultValue "Shayan Habibi builds F# compiler integrations, typed bindings and workspace automation."))
            link(rel = "icon", href = context.Site.UrlOfAsset "favicon.svg", type' = "image/svg+xml")
            for asset in context.Site.PageAssets do
                match asset with
                | Stylesheet path -> link(rel = "stylesheet", href = context.Site.UrlOfAsset path)
                | _ -> ()
            link(rel = "stylesheet", href = context.Site.UrlOfAsset "profile.css")
            if context.Page.Route.Segments = [ "ranvier" ] then
                link(rel = "stylesheet", href = context.Site.UrlOfAsset "ranvier-maps.css")
        }
        body() {
            shell headerContent pageContent
            for asset in context.Site.PageAssets do
                match asset with
                | Script (path, defer) -> script(src = context.Site.UrlOfAsset path, defer = defer) { () }
                | InlineScript code -> script() { raw code }
                | _ -> ()
        }
    }
    // Nacara's current layout contract requires Feliz; render the Oxpecker tree once at this boundary.
    Feliz.ViewEngine.Html.html [
        Feliz.ViewEngine.prop.lang "en"
        Feliz.ViewEngine.prop.dangerouslySetInnerHTML (Render.toString content)
    ]
