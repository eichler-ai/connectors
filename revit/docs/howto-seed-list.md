# How-to corpus — the regrouped, feature-level seed list

Status: **for review** before annotation and extraction (seed plan §5.1: "the regrouped list is
confirmed at seed time"). This regroups the 33 audited rows of [`howto-seed-plan.md`](howto-seed-plan.md)
§3a/§3b into one document per Revit feature or connector mechanism at moderate depth (numbered steps,
roughly 3–8 KB of script). Row numbers below (`#n`) refer to that audit. Nothing here is annotated or
extracted until the list is settled; edit ids, splits and merges in place.

Conventions the seed follows, decided earlier and repeated so the review can check them:

- Each how-to is framed as a **user task**: a modelling goal, its start point, its end point, and how
  the result is verified (§3). `task` is that goal in one retrieval-optimised sentence; the script's
  numbered comments carry the route and the explanation (no `summary`).
- A "this does not exist" fact lives in the `task` sentence and a step comment of the document that
  carries the route; no standalone negative documents.
- `pitfalls[]` reference the connector's error `code` where one exists (`script-write-outside-transaction`,
  `script-target-must-not-be-modifiable`, `script-api-denied`, `script-lifecycle-confirmation-required`,
  `script-subtransaction-needs-transaction`) rather than paraphrasing it — from the `skill.md` regroup (#167).
- Where a how-to cites `skill.md`, it cites the #167 section titles: *Running a script*, *Writing to a
  document*, *Documents*, *What needs your confirmation*, *Reading errors*, *Exchanging files*,
  *Discovering the API*, *When something isn't working*.
- Each document's verification stamp records the expected **net** `mutations` (`net_created`,
  `net_modified`, `net_deleted`, optional `by_category`) so the sweep checks "did what it says", not
  just "ran". A how-to that creates and deletes its own scratch element expects zero.

## 1. The list

| # | id | Feature | Absorbs audit rows | Value | Est. script |
|---|---|---|---|---|---|
| 1 | `walls-create-and-join` | Walls: create from lines on a level, type lookup, closed footprint, confirm joins | #1, #16 | H | 5 KB |
| 2 | `collect-elements-with-filters` | `FilteredElementCollector`: by category, by class, in a view, with a parameter filter | #2 (+ recorded "get all walls" miss) | H | 4 KB |
| 3 | `element-parameters-read-write-delete` | Parameters by name and `BuiltInParameter`, storage types, read-only checks, delete and confirm | #3, #4 | M | 4 KB |
| 4 | `shared-parameters-file-and-binding` | Write a shared-parameter file, open it, bind to categories under a `GroupTypeId` | #5 | H | 5 KB |
| 5 | `groups-create-edit-propagate` | Create a group, place instances, edit so every instance changes, the member-move trap | #6 (§3e draft) | H | 6 KB |
| 6 | `levels-grids-and-plan-views` | Create a level, the `ViewPlan` it does not get for free, grids, view lookup by level | #21, #12, #7's finding | H | 5 KB |
| 7 | `rooms-create-tag-area` | Create a room on a level, tag it in the level's plan, computation height on script-created levels | #7, #19 | H | 5 KB |
| 8 | `family-instances-place-hosted` | Place a hosted family instance (door in wall): symbol lookup, `Activate()`, host and level | #8 | H | 4 KB |
| 9 | `family-document-build-load-place` | Build a minimal family document, load it into the project between blocks, place an instance | #17 | H | 7 KB |
| 10 | `dimensions-in-plan-views` | Dimension between walls in a level-scoped plan view; references and dimension types | #9 | M | 4 KB |
| 11 | `schedules-create-with-fields` | Create a schedule for a category, add fields, sort, read the rows back | #10 | M | 4 KB |
| 12 | `floors-create-from-loop` | Create a floor from a closed curve loop on a level; floor type lookup | #11 | M | 3 KB |
| 13 | `sheets-viewports-and-title-blocks` | Create a sheet with the right title block, place a view, never swap `SheetTitleBlockId` on a live sheet | #13, #20 | H | 6 KB |
| 14 | `text-notes-and-annotation-text` | Add a text note to a view; the trailing `\r` in `TextNote.Text` | #14 | M | 3 KB |
| 15 | `export-views-dwg` | Build or find the view inside a block, `Document.Export` outside it, export options | #15, export half of #23 | H | 4 KB |
| 16 | `stairs-with-edit-scope` | `StairsEditScope`: `Start`, the run inside one block, `Commit` between blocks | #18, stairs half of #23 | H | 6 KB |
| 17 | `transactions-write-inside-blocks` | Read at top level, write inside `Connector.WithTransaction`, returned values, a throw rolls back even if caught, `SubTransaction` as a savepoint | #22, #33, #24 | H | 6 KB |
| 18 | `self-transacting-calls-between-blocks` | `LoadFamily`, `Export`, `RequestViewChange`, `EditScope.Commit` need the target not modifiable: run them between blocks | #23 (mechanism; 9, 15, 16 reference it) | H | 5 KB |
| 19 | `documents-create-write-and-close` | Create a project or family document (headless), write into it, route by `document_id`, close it by routing away | #25, #26, #27 | H | 7 KB |
| 20 | `lifecycle-settle-save-close` | Save-as or close in the same run: `Connector.Settle`, then the confirmation-gated call | #30 | M | 4 KB |
| 21 | `files-publish-and-audit-trail` | Publish an output file, overwrite flag, where scripts and logs land | #28 | M | 3 KB |
| 22 | `results-return-values-and-mutations` | Return projections not elements, `output` vs `return_value`, read the `mutations` report | #29, #32 | M | 4 KB |
| 23 | `undo-redo-and-run-labels` | Label a run, undo and redo it with the tools, tell the agent's work from the person's | #31 | M | 3 KB |

33 audit rows → 23 documents. Every accepted row lands somewhere; the §3c non-candidates stay out.

## 2. What changed against the audit

- **Merged**: #1+#16 (walls), #3+#4 (parameters + delete, the "decide" in §5.3), #12 into #21 with #7's
  view finding (the other §5.3 "decide"), #7+#19 (rooms), #13+#20 (sheets), #22+#33+#24 (transactions),
  #25+#26+#27 (documents), #29+#32 (results). #32 was "defer" in the audit; it is in because the
  `mutations` report is now the sweep's own check signal and an agent should know how to read it.
- **Split**: #23 was one row; its export and stairs halves become the worked examples inside 15 and 16
  (the peer session's two "canonical Phase 3 shapes"), and 18 keeps the mechanism with `LoadFamily`
  and `RequestViewChange` as its own examples. 15/16/18 cross-reference by id, not by repeating text.
- **Kept single on purpose**: 14 (text notes) and 12 (floors) are small; per the granularity decision
  every feature gets its own lineage, and a later submission on annotation text or floor edges folds
  into them rather than starting a new one.
- **#31 included**: #146 is fully merged, so the undo/redo tools are stable enough to seed.
- **Kinds**: everything is `howto`; the `pitfall` rows (#19, #20, #29, #33) become `pitfalls[]`
  entries in the document that teaches the feature, with `code` where one applies.

## 3. Per-document outline, framed as a user task

Each how-to teaches an aspect of the Revit API in service of a modelling goal the user actually has.
So each entry is framed as that task: the **goal** in the user's words, the **start point** (what the
model holds and what the script assumes it can find), the **end point** (what exists afterwards),
and the **verification** (how the script proves it, and what the sweep's stamp asserts). The
numbered steps and the pitfalls follow from that frame. The document's `task` sentence is the goal
line; the steps are the script's comments.

### 1. `walls-create-and-join`
- **Goal:** enclose a rectangular room footprint with walls of a named type on a level, joined at the corners.
- **Start:** a project with at least one level and a Basic wall type; the footprint as four corner points in feet.
- **End:** four walls on that level, each corner joined.
- **Verify:** `AreElementsJoined` per corner (and why `IsWallJoinAllowedAtEnd` is the wrong check); return ids and join results. Stamp: `net_created: 4`, category `Walls`.
- **Pitfalls:** type lookup by name is case-sensitive and template-dependent, fall back to the first Basic `WallType`; `Wall.Create` with `structural: false`.

### 2. `collect-elements-with-filters`
- **Goal:** find every element of a kind so the next step can act on them: all walls, all doors on a level, all elements visible in a view.
- **Start:** any project; nothing is created.
- **End:** unchanged model; a list of ids and names.
- **Verify:** counts per filter agree with a second, independent collector (e.g. category vs class for walls); returns a projection. Stamp: no `mutations` (read-only).
- **Pitfalls:** the collector idiom cannot come back from `search_functions` as a member (the recorded miss); omitting `WhereElementIsNotElementType` counts types too.

### 3. `element-parameters-read-write-delete`
- **Goal:** read an element's parameter, change it, and remove an element the user no longer wants.
- **Start:** a project with at least one wall; a scratch element the script creates itself.
- **End:** the parameter set; the scratch element gone.
- **Verify:** read the parameter back after `Set`; `GetElement` of the deleted id returns null. Stamp: zero net (created then deleted), `net_modified: 1`.
- **Pitfalls:** `IsReadOnly`/`StorageType` before `Set`; doubles are internal feet (`UnitUtils`); `LookupParameter` by name vs `get_Parameter(BuiltInParameter)`.

### 4. `shared-parameters-file-and-binding`
- **Goal:** add a project-wide shared parameter (say "Fire Rating Note") to every wall and door so schedules and tags can show it.
- **Start:** a project with no shared-parameter file set.
- **End:** a shared-parameter file on disk, the definition in it, bound as an instance parameter to Walls and Doors under a parameter group.
- **Verify:** `LookupParameter` on a wall finds it and it accepts a value. Stamp: `net_modified >= 1` (binding counts are version-dependent).
- **Pitfalls:** no `Application.CreateSharedParameterFile`, write the tab-delimited file with `System.IO` then `OpenSharedParameterFile`; `BuiltInParameterGroup` is gone, use `GroupTypeId` (2025+); the file lives under the connector's files directory so the script stays scrub-clean.

### 5. `groups-create-edit-propagate`
- **Goal:** make a repeated assembly (say a furniture group) and change it once so every placed copy updates.
- **Start:** a project with a level; the script creates the members.
- **End:** a group type with two placed instances, both showing the edit.
- **Verify:** read the edited property from each instance's members. Stamp: `net_created` = members + 2 instances.
- **Pitfalls:** no group-edit-scope API; editing a member directly is refused once two or more instances are placed (rolled back, `status=error`); `MoveElement` on a member's own id silently does nothing, move the group instance's id.

### 6. `levels-grids-and-plan-views`
- **Goal:** add a new storey with its own floor plan and structural grid so later work has somewhere to draw.
- **Start:** a project with at least one level (for the elevation offset).
- **End:** a level, a floor plan for it, and a grid line.
- **Verify:** a plan view whose `GenLevel` is the new level exists; the grid is on it. Stamp: `net_created: 3`.
- **Pitfalls:** `Level.Create` does not create a plan view, `ViewPlan.Create` with a floor-plan `ViewFamilyType` does; annotating in a view not scoped to the level fails with a deep `ArgumentNullException`.

### 7. `rooms-create-tag-area`
- **Goal:** turn an enclosed footprint into a named room with a tag and a reported area.
- **Start:** an enclosed footprint (cites 1) on a level that has a plan view (cites 6).
- **End:** a room, its tag in the plan, a non-zero area.
- **Verify:** `Room.Area > 0` and the tag's `Room` is the room. Stamp: `net_created: 2`.
- **Pitfalls:** a room on a script-created level reports zero area until `LEVEL_ROOM_COMPUTATION_HEIGHT` is set; an unenclosed room has no area.

### 8. `family-instances-place-hosted`
- **Goal:** put a door of a given type into an existing wall.
- **Start:** a project with a wall and a loaded door family.
- **End:** a door instance hosted by that wall on its level.
- **Verify:** the instance's `Host` is the wall; its level matches. Stamp: `net_created: 1`, `net_modified: 1`.
- **Pitfalls:** `FamilySymbol.Activate()` before `NewFamilyInstance` or it throws; the point must lie on the host's location line; symbol lookup by family and type name.

### 9. `family-document-build-load-place`
- **Goal:** create a custom component the project lacks (a simple block), bring it in, and place one.
- **Start:** a project and a family template on the Revit install.
- **End:** a saved family file, loaded into the project, one instance placed.
- **Verify:** `Family` by name exists in the project; the instance's symbol belongs to it. Stamp: `net_created: 2` on the project.
- **Pitfalls:** `LoadFamily` needs the target not modifiable, load between blocks (cites 18); the two-call split; the created family document is headless (cites 19).

### 10. `dimensions-in-plan-views`
- **Goal:** annotate the distance between two walls on the floor plan.
- **Start:** two parallel walls on a level with a plan view (cites 1, 6).
- **End:** a dimension between them in that plan.
- **Verify:** the dimension's value equals the wall spacing within tolerance. Stamp: `net_created: 1`.
- **Pitfalls:** the view must be scoped to the walls' level; location-curve references vs geometry references.

### 11. `schedules-create-with-fields`
- **Goal:** produce a wall schedule with the columns the user asked for, sorted.
- **Start:** a project with walls.
- **End:** a schedule view with those fields, sorted by one of them.
- **Verify:** read the body back with `GetTableData`/`GetCellText` and check the header row and row count. Stamp: `net_created: 1`.
- **Pitfalls:** fields come from `GetSchedulableFields` by parameter id, not by display name.

### 12. `floors-create-from-loop`
- **Goal:** put a floor slab of a named type under an enclosed footprint.
- **Start:** a level and a closed footprint (cites 1).
- **End:** a floor on that level.
- **Verify:** the floor's `LevelId` and its sketch area against the footprint. Stamp: `net_created: 1`.
- **Pitfalls:** open or non-planar loops throw; the 2022+ `Floor.Create(doc, loops, typeId, levelId)` signature.

### 13. `sheets-viewports-and-title-blocks`
- **Goal:** issue a floor plan on a sheet with the right title block.
- **Start:** a project with a title-block family and a plan view not yet on a sheet.
- **End:** a sheet with that title block and the plan placed on it.
- **Verify:** `GetAllPlacedViews` contains the view; the title block instance on the sheet is the requested type. Stamp: `net_created: 2`.
- **Pitfalls:** create the sheet with the right title block, never set `SheetTitleBlockId` on a live sheet (Revit crash, issue #113); `Viewport.CanAddViewToSheet` first, a view already on a sheet throws; the recorded search miss for `ViewSheet.Create` (issue #65).

### 14. `text-notes-and-annotation-text`
- **Goal:** add a note to a drawing and read notes back for review.
- **Start:** a view and a text note type.
- **End:** the note in the view.
- **Verify:** read `Text` back and compare after trimming. Stamp: `net_created: 1`.
- **Pitfalls:** `TextNote.Text` comes back with an undocumented trailing `\r`.

### 15. `export-views-dwg`
- **Goal:** hand a consultant a DWG of a plan.
- **Start:** a plan view (created in a block if absent).
- **End:** a DWG file the user can fetch (cites 21).
- **Verify:** the file exists with non-zero size; publish it. Stamp: zero, or `net_created: 1` if the view was created.
- **Pitfalls:** `Document.Export` inside a block fails with `script-target-must-not-be-modifiable`, run it between blocks (cites 18); the recorded query hit ranks.

### 16. `stairs-with-edit-scope`
- **Goal:** add a straight stair between two levels.
- **Start:** two levels (cites 6).
- **End:** a stairs element with a run and landing.
- **Verify:** the stairs' base and top levels match; `mutations` shows the stairs category. Stamp: `net_created >= 1`.
- **Pitfalls:** `StairsEditScope.Start` and `Commit` between blocks, the run inside one (`script-target-must-not-be-modifiable` otherwise); `Connector.Settle` ordering.

### 17. `transactions-write-inside-blocks`
- **Goal:** make a change safely so a mistake rolls back and a success is one undo step.
- **Start:** any project.
- **End:** exactly the committed changes, nothing from the rolled-back attempts.
- **Verify:** counts before/after each block; the returned value from the `Func<T>` form. Stamp: only what the committed blocks created.
- **Pitfalls:** `script-write-outside-transaction`; a body that throws is rolled back even if the script catches, and the document stays usable; `SubTransaction` as a savepoint (`script-subtransaction-needs-transaction`; `Dispose` alone rolls back).

### 18. `self-transacting-calls-between-blocks`
- **Goal:** load a family or switch the active view in the same run as a write.
- **Start:** a project with a family file available and a second view.
- **End:** the family loaded, the view changed, the write committed.
- **Verify:** family present; `ActiveView` changed. Stamp: `net_created: 1`.
- **Pitfalls:** `LoadFamily`, `Export`, `RequestViewChange`, `EditScope.Commit` inside a block fail with `script-target-must-not-be-modifiable`; run them between blocks (#160's finding that such commits used to be rolled back).

### 19. `documents-create-write-and-close`
- **Goal:** build something in a fresh scratch project, keep it on disk, and tidy up.
- **Start:** a connected Revit; no scratch document.
- **End:** a new document written to, reachable later by `document_id`, then closed.
- **Verify:** the document appears in `list_instances` with `active: false`; the write is visible on the next run; it is absent after the close. Stamp: on the created document, what it wrote.
- **Pitfalls:** the created document is headless (no window); `UIDocument` is null when routed there; closing while routed at it fails for that call only, route elsewhere and find it by title; a throw rolls created documents back; an unknown `document_id` fails with the candidate list.

### 20. `lifecycle-settle-save-close`
- **Goal:** save the work under a new name (or discard it and close) at the end of the run.
- **Start:** a document with uncommitted work in the run.
- **End:** the file saved as, or the document closed clean.
- **Verify:** the saved file exists; the discarded document shows zero mutations. Stamp: zero after discard, the writes after keep.
- **Pitfalls:** `Connector.Settle` first; `script-lifecycle-confirmation-required` without `confirm_lifecycle`; Revit's transaction-phase check precedes event dispatch.

### 21. `files-publish-and-audit-trail`
- **Goal:** get a report file the script wrote into the user's hands, and find the run later.
- **Start:** any project.
- **End:** the file published; the run's script and log located.
- **Verify:** the published path listed by the files tool; overwrite refused without the flag then accepted with it. Stamp: zero.
- **Pitfalls:** overwrite fails per file unless the flag is set.

### 22. `results-return-values-and-mutations`
- **Goal:** report what the run found and prove what it changed.
- **Start:** any project; the script creates one element.
- **End:** a projection returned; `mutations` naming the created category.
- **Verify:** `return_value` is the projection, `output` is the console text, `mutations.net_created: 1`. Stamp: `net_created: 1` with the category.
- **Pitfalls:** an element return is a no-display-form marker; caught throws contribute nothing to the report; the `(uncategorized)` bucket.

### 23. `undo-redo-and-run-labels`
- **Goal:** try a change, look at it, and take it back without touching the person's own work.
- **Start:** any project.
- **End:** the model as found.
- **Verify:** the labelled run's element exists, is gone after undo, back after redo, gone again. Stamp: `net_created: 1` by the run; zero on the model after the final undo.
- **Pitfalls:** undo without confirmation is refused; the label is how the agent's step is told from the person's in Revit's undo history.

## 4. Questions for the reviewer

1. **23 documents, or fewer?** 12 and 14 could fold into neighbours (floors into walls as "sketch-based
   elements", text notes into 10 as "annotation"), but each is a distinct Revit feature, which the
   granularity decision favours.
2. **Overlap between 15, 16 and 18.** The mechanism document (18) and the two feature documents each
   carry a full script; the feature documents cite 18 by id in a pitfall rather than repeating the
   rule. Alternative: drop 18 and let 9, 15 and 16 be the only examples.
3. **Ids.** Feature-first (`walls-…`, `rooms-…`) so a sorted corpus reads as an index; say if you
   prefer verb-first.
4. **Tags.** Proposed vocabulary: the Revit feature (`walls`, `rooms`, `sheets`, …), `connector` for
   17–23, and `pitfall` on any document whose value is mostly a trap. Add or cut.
