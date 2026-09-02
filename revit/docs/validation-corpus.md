# Validation corpus (PRD §13, phase 4)

Tracks the tutorial-sourced task corpus PRD §13 calls for: natural-language tasks, run end-to-end
against an agent that has *only* `execute_script` + the three discovery tools (`list_functions`/
`search_functions`/`describe_function`) -- no hand-written wrapper, no script authored from prior
knowledge of the answer. The question each case answers is "is API discovery sufficient for an agent
to self-teach this workflow," not "can the connector run this script" (`revit/test-harness/` already
answers that, for a different set of cases -- see "Relationship to `revit/test-harness/`" below).

## Grading protocol

1. State the task as a natural-language sentence, phrased the way a tutorial or a user would ask it --
   not "call CreateWall," but "model a wall along this line."
2. Solve it live, using only `search_functions`/`list_functions`/`describe_function` to find each API
   member -- record which discovery calls were needed and whether the first query surfaced the right
   member (a case needing three reformulations before finding the right method is a discovery gap even
   if it eventually passes).
3. Run the resulting script via `execute_script` against a real connected Revit instance.
4. Check the stated expected-outcome condition against the live document afterward (element count,
   parameter value, exported file exists) -- not "the script didn't throw."
5. Record PASS/FAIL plus the discovery path. A FAIL or a rough discovery path is a real product gap:
   file an issue, don't just note it here and move on.
6. Once passing, the validated script becomes a `revit/test-harness/` regression test (see below) so
   later add-in changes can't silently break it -- that replay is NOT a re-test of discoverability,
   only of the connector mechanism.

## Relationship to `revit/test-harness/`

The harness's existing Phase A/B/C bundles (`phase_a_test.go`/`phase_b_test.go`/`phase_c_test.go`) are
narrow, single-feature checks (create a wall, place a door, add a dimension) authored by whoever wrote
the test -- valuable regression coverage of the connector's own plumbing, but they don't answer this
corpus's question, since the script wasn't produced by discovery-only research under task-level
ambiguity. This corpus is TASK-level (a case may need several of those primitives combined) and its
initial pass is graded on the discovery process, not just the final script. Once a case passes, its
replay test lives in `validation_corpus_test.go`, deliberately separate from the Phase A/B/C files, so
"which of these came from a real discovery session vs. which were written knowing the answer" stays
visible in the file layout.

## Competitive coverage floor (§13)

Separate, not yet started: one task per fixed tool exposed by the broadest cataloged competitors
(§03), phrased at the same level ("get all door widths on level 2," not "call door-width tool"). Any
failure there is a discovery gap with priority over new tutorial cases. Needs a re-read of §03's survey
to enumerate the actual tool lists before cases can be written -- tracked here, not started.

## Case list

Status: `todo` / `in progress` / `pass` / `fail (see issue #N)`.

| # | Task | Source | Status |
|---|------|--------|--------|
| 1 | Export the active view to DWG | Autodesk Revit API sample ("DWG Export") | pass |
| 2 | Model a closed rectangular building footprint (4 walls forming a loop) and confirm they join at corners | Building Coder, "Creating a Simple House" pattern | pass |
| 3 | Load a family from a library path and place an instance of it | Autodesk Revit API sample ("Family Placement") | pass |
| 4 | Compute total wall area on a given level (aggregate across many elements, not read one parameter) | Building Coder, area-reporting pattern | todo |
| 5 | Create a door schedule (counts by type), distinct from Phase B's wall schedule | Autodesk Revit API sample ("Schedule Creation") | todo |
| 6 | Filter all elements of a category visible in the active view and hide them | Building Coder, "Temporary Hide/Isolate" pattern | todo |
| 7 | Rename every level in the document to a naming convention (batch-modify a parameter across many elements, not one) | Building Coder, batch-parameter-edit pattern | todo |
| 8 | Create a simple gable roof from a building footprint | Autodesk Revit API sample ("Roof Creation") | todo |
| 9 | Create a curtain wall with a rectangular grid pattern | Autodesk Revit API sample ("Curtain Wall") | todo |
| 10 | Create a topography surface from a set of points | Autodesk Revit API sample ("Toposolid/Topography") | todo |
| 11 | Tag every door on a level automatically | Building Coder, batch-tagging pattern | todo |
| 12 | Create a section view through a building and place it on a sheet | Autodesk Revit API sample ("View Creation") | todo |
| 13 | Import a linked DWG and query its geometry/bounding box | Autodesk Revit API sample ("Link/Import") | todo |
| 14 | Array-copy an element pattern along a line (e.g. a row of columns) | Building Coder, transform/array pattern | todo |
| 15 | Compute room areas for a level and report them (aggregate, distinct from #4's element-count style aggregate) | Building Coder, room-area-reporting pattern | todo |

Fifteen cases sized to PRD §13's "15-20" floor; extend past 15 only if a gap surfaces that isn't
already covered above. Deliberately excludes anything acting on a genuinely workshared central model
(Save/SynchronizeWithCentral/Print against the corpus's own target document) -- those are real
workflows but regression-replaying them destructively isn't worth the risk for this corpus; PRD §14's
lifecycle-gate tests already cover that those actions are correctly gated, which is the property this
corpus doesn't need to re-prove. `confirm_lifecycle_actions` itself is fine when the gated call is
throwaway housekeeping the corpus created and controls entirely -- case #3 closes its own scratch
family document this way, nothing external.

## Case notes

**#1 (pass).** First query (`search_functions("export view to dwg")`) surfaced `Document.Export(folder,
name, views, DWGExportOptions)` at rank 6 of page 1 and `DWGExportOptions` at rank 1 -- no
reformulation needed, a clean discovery pass. One real gap found along the way, not a blocker for this
case but filed separately: this connector's own script globals (`Document`, `ExportsDirectory`,
`Publish`, ...) are completely invisible to `list_functions`/`search_functions` -- they only reflect
the RevitAPI corpus, never `ScriptGlobals` itself -- so a script only discovers `Document` is the real
global (not `doc`, which is a fixture-local alias, not a global) via a compile error, not via search.
See issue #84. **Closed by [issue #91](https://github.com/eichler-ai/connectors/issues/91)**: the
connector's own API is now indexed as an add-in API under `Eichler.Connectors.Revit`, so
`list_functions`/`describe_function` return it beside Revit's. The finding above is kept as the
historical record of why -- do not read it as current behaviour. (Search by *intent* is still weak for
it, per #80/#87; search by exact name and browsing both work.) Regression replay: `revit/test-harness/validation_corpus_test.go`,
`TestValidationCorpus_ExportViewToDwg`.

**#2 (pass).** First query (`search_functions("join walls at corner")`) found nothing usable --
`JoinGeometryUtils`/`WallUtils` were absent from the first page, buried under
`BuiltInParameter`/`BuiltInFailures` noise. A second, more specific query
(`"AreElementsJoined geometry"`) found `JoinGeometryUtils.AreElementsJoined` at rank 1 -- filed as
issue #87, a real reformulation-needed gap even though the case passed. Separately, and NOT a
discovery-tool gap -- a genuine Revit API distinction this case's initial instinct got wrong:
`JoinGeometryUtils.AreElementsJoined` measures solid BOOLEAN union, not the wall END-JOIN miter a
closed footprint's corners actually need; four walls with coincident corner endpoints tested FALSE for
it at every corner, live. `WallUtils.IsWallJoinAllowedAtEnd` is the correct check (True by default at
every end, confirmed live). Regression replay: `TestValidationCorpus_ClosedRectangularFootprint`.

**#3 (pass).** This dev machine's installed content library turned out to be a stub (446 `.rfa` files,
mostly localized placeholder/redirect content, no real furniture families) -- discovered live via
`Directory.GetFiles`, not assumed. Rather than depend on an optional content-library install a corpus
case shouldn't be fragile against, this case builds and loads its own minimal Generic Model family
instead -- still a genuine, complete exercise of `Document.LoadFamily` + `FamilySymbol` activation +
`NewFamilyInstance`. Two real findings about transactions colliding with `Document.LoadFamily`
specifically (not generic Revit API behavior), both documented in `caveats.md`'s "must not be
modifiable" section: the call needs its target document to have no open transaction (the source
turned out not to matter once re-tested under Phase 3 -- the original finding blamed both). Under the
original always-open model that forced a two-call split plus a careful ordering
of `Connector.OpenForWriting`; since #146 Phase 3 (group-always, transaction-on-write) it only means
the call goes between `Connector.WithTransaction` blocks -- the case now builds the family in one call
and loads and places it in the next, with `LoadFamily` outside the placement block. Regression replay:
`TestValidationCorpus_LoadFamilyAndPlaceInstance`.
