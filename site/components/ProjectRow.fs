module Profile.Components

open Profile
open Partas.Solid

[<SolidComponent>]
let ProjectRow (project: Project) =
    article (class' = "project-row") {
        div (class' = "project-index") {
            span () { project.Category.AsString }
            img (class' = "project-illustration", src = Projects.graphUrl project, alt = project.GraphLabel, width = 160, height = 104, loading = "lazy")
        }
        div (class' = "project-copy") {
            h3 () { project.Name }
            p (class' = "project-summary") { project.Summary }
            div (class' = "technologies") {
                For.Keyed(each = project.Technologies) {
                    yield fun technology _ -> span () { technology }
                }
            }
            details (class' = "project-details") {
                summary () { "Technical details" }
                p () { project.Detail }
            }
        }
        div (class' = "project-links") {
            match Projects.caseStudyUrl project with
            | Some url -> a (href = url) { "Technical walkthrough" }
            | None -> ()
            a (href = project.SourceUrl) { "Source ↗" }
            a (href = project.DocsUrl) { "Documentation ↗" }
        }
    }
