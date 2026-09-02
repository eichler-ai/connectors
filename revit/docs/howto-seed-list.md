# How-to corpus — the regrouped, feature-level seed list

Status: **for review** before annotation and extraction (seed plan §5.1: "the regrouped list is
confirmed at seed time"). This regroups the 33 audited rows of [`howto-seed-plan.md`](howto-seed-plan.md)
§3a/§3b into one document per Revit feature or connector mechanism at moderate depth (numbered steps,
roughly 3–8 KB of script). Row numbers below (`#n`) refer to that audit. Nothing here is annotated or
extracted until the list is settled; edit ids, splits and merges in place.

Conventions the seed follows, decided earlier and repeated so the review can check them:

- `task` is one retrieval-optimised sentence; script comments carry the explanation (no `summary`).
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

## 3. Per-document outline

Each entry: the steps the script will number, the pitfalls it carries, the expected net mutations
the stamp asserts, and any open point for the reviewer.

### 1. `walls-create-and-join`
Steps: find a level and a Basic wall type by name (fallback to the first `WallType` of kind Basic);
create four walls from a closed footprint; confirm each corner joined (`AreElementsJoined`, and why
`IsWallJoinAllowedAtEnd` is the wrong check); return ids and join results.
Pitfalls: type lookup by name is case-sensitive and template-dependent; `Wall.Create` with `structural: false`.
Net: `net_created: 4`, category `Walls`.

### 2. `collect-elements-with-filters`
Steps: all elements of a category (`OfCategory` + `WhereElementIsNotElementType`); all instances of a
class (`OfClass`); scoped to a view; a parameter filter (`ElementParameterFilter`); counts and a
projection back. Pitfalls: `search_functions` cannot return the collector idiom as a member (the
recorded miss); forgetting `WhereElementIsNotElementType` doubles counts with types.
Net: zero (read-only; `mutations` absent). Open: include a `LogicalAndFilter` step or leave for a submission.

### 3. `element-parameters-read-write-delete`
Steps: `LookupParameter` by name vs `get_Parameter(BuiltInParameter)`; check `StorageType` and
`IsReadOnly` before `Set`; set a string and a double (internal units); create a scratch element,
delete it, confirm it is gone. Pitfalls: set on a read-only parameter throws; doubles are internal
feet, convert with `UnitUtils`. Net: zero (the scratch element is created and deleted).

### 4. `shared-parameters-file-and-binding`
Steps: write the tab-delimited shared-parameter file with `System.IO` (there is no
`Application.CreateSharedParameterFile`); `OpenSharedParameterFile`; create a group and definition;
bind to a category set with `InstanceBinding` under `GroupTypeId` (2025+); verify on an instance.
Pitfalls: `BuiltInParameterGroup` is gone; the file path must be writable and is per-machine. Net:
`net_created` for the binding is version-dependent, assert `net_modified >= 0` only. Open: the file is
written under the connector's files directory, not a user path, so the script stays scrub-clean.

### 5. `groups-create-edit-propagate`
The §3e draft, as written. Steps: create a group from elements, place two instances, edit through the
group type so both change, then the two traps. Pitfalls: no group-edit-scope API; editing a member
directly is refused with two or more placed instances (rolled back, `status=error`);
`MoveElement` on a member's own id does nothing, move the group's id. Net: `net_created` = elements +
group instances.

### 6. `levels-grids-and-plan-views`
Steps: `Level.Create`; find a `ViewFamilyType` for floor plans and `ViewPlan.Create` (the view does
not come for free); look a plan up by `GenLevel`; `Grid.Create` from a line; return ids. Pitfalls:
no plan view is created with a level; `NewRoomTag`/`NewDimension` in a non-level view fail with a
deep `ArgumentNullException`. Net: `net_created: 3` (level, view, grid).

### 7. `rooms-create-tag-area`
Steps: pick or create a level and its plan (cites 6); `NewRoom` at a point inside a closed footprint;
`NewRoomTag` in the plan; read `Area`; on a script-created level set `LEVEL_ROOM_COMPUTATION_HEIGHT`
first. Pitfalls: a room on a script-created level reports zero area until the computation height is
set; an unenclosed room has no area. Net: `net_created: 2` plus the level pieces if created.

### 8. `family-instances-place-hosted`
Steps: find a door `FamilySymbol` by family and type name; `Activate()` it; find a host wall and its
level; `NewFamilyInstance` with the hosted overload; verify `Host`. Pitfalls: an unactivated symbol
throws; the point must lie on the host's location line. Net: `net_created: 1`, `net_modified: 1` (host).

### 9. `family-document-build-load-place`
Steps: `NewFamilyDocument` from a template; add an extrusion inside a block on that document; save
under the connector's files directory; `LoadFamily` into the project **between** blocks (cites 18);
place an instance inside a block. Pitfalls: `LoadFamily` needs the target not modifiable; the two-call
split; the created family document is headless (cites 19). Net: `net_created: 2` on the project
(family + instance). Open: this is the largest document; it stays one because the feature is one.

### 10. `dimensions-in-plan-views`
Steps: level-scoped plan (cites 6); references from two walls' location lines; `NewDimension` with a
`ReferenceArray`; optional dimension type. Pitfalls: needs a view of the walls' level; references
from `GetGeometryObjectFromReference` vs location-curve references. Net: `net_created: 1`.

### 11. `schedules-create-with-fields`
Steps: `ViewSchedule.CreateSchedule` for a category; add fields from `GetSchedulableFields`; a sort
field; read the body back with `GetTableData`/`GetCellText`. Pitfalls: field lookup by parameter id,
not name. Net: `net_created: 1`.

### 12. `floors-create-from-loop`
Steps: a closed `CurveLoop`; floor type lookup; `Floor.Create(doc, loops, typeId, levelId)` (2022+
signature); return the id. Pitfalls: open loops throw; the loop must be planar. Net: `net_created: 1`.

### 13. `sheets-viewports-and-title-blocks`
Steps: find a title-block symbol, `ViewSheet.Create`; find a placeable view; `Viewport.CanAddViewToSheet`
then `Viewport.Create`; to change the title block, create the sheet with the right type (never set
`SheetTitleBlockId` on a live sheet: Revit crash, issue #113). Pitfalls: the recorded search miss for
`ViewSheet.Create` (issue #65); `Viewport.Create` throws for a view already on a sheet. Net:
`net_created: 2`.

### 14. `text-notes-and-annotation-text`
Steps: `TextNote.Create` in a view with a type; read `Text` back and trim the appended `\r`. Pitfalls:
the undocumented trailing `\r`. Net: `net_created: 1`.

### 15. `export-views-dwg`
Steps: inside a block, create or find the view to export; between blocks, `Document.Export` with
`DWGExportOptions` to the connector's files directory; publish the file (cites 21). Pitfalls:
`Export` inside a block fails with `script-target-must-not-be-modifiable` (cites 18); the recorded
query hit (`Document.Export` rank 6). Net: zero or `net_created: 1` if the view was created.

### 16. `stairs-with-edit-scope`
Steps: `StairsEditScope.Start` between blocks; the run and landing inside one `WithTransaction`;
`Commit` between blocks; verify the stairs. Pitfalls: `Commit` while a connector transaction is open
(`script-target-must-not-be-modifiable`); `Connector.Settle` ordering. Net: `net_created` ≥ 1 (stairs).

### 17. `transactions-write-inside-blocks`
Steps: a read at top level; a write outside a block and its code (`script-write-outside-transaction`);
the same write inside `Connector.WithTransaction`; the `Func<T>` form returning the body's value; a
body that throws is rolled back even though the script catches, the document stays usable; a
`SubTransaction` savepoint with `using` + `Commit`/`RollBack`. Pitfalls: those codes;
`script-subtransaction-needs-transaction`; `Dispose` alone rolls back. Net: exactly what the
committed blocks created, nothing from the rolled-back ones.

### 18. `self-transacting-calls-between-blocks`
Steps: the rule and the shape (block, then the call at top level, then a block); `LoadFamily` and
`RequestViewChange` as the worked examples; what the error looks like. Pitfalls:
`script-target-must-not-be-modifiable`; #160's finding that these commits used to be rolled back.
Net: `net_created: 1` (the loaded family).

### 19. `documents-create-write-and-close`
Steps: `Application.NewProjectDocument`; the created document is headless (`active: false`, no
window); write into it in the same run; on a later run route by `document_id` (`UIDocument` is null);
close it by routing the call elsewhere and finding it by title. Pitfalls: closing while routed at it
fails for that call only; activation is refused inside a block; a throw rolls created documents back
too; an unknown `document_id` fails with the candidate list. Net: on the created document,
`net_created` of what it wrote.

### 20. `lifecycle-settle-save-close`
Steps: write; `Connector.Settle(keep)`; `SaveAs` under the connector's files directory with
`confirm_lifecycle`; the discard-then-close variant. Pitfalls: `script-lifecycle-confirmation-required`;
Revit's transaction-phase check precedes event dispatch. Net: zero after discard, the writes after keep.

### 21. `files-publish-and-audit-trail`
Steps: write a file under `Connector.FilesDirectory`; publish it; the overwrite flag; where the run's
script and log are kept. Pitfalls: overwrite fails per file unless the flag is set. Net: zero.

### 22. `results-return-values-and-mutations`
Steps: return a projection (`new { id, name }` list) not an element; `output` (console) vs
`return_value`; read `mutations` (`net_*`, `by_category`, the `(uncategorized)` bucket) to confirm
the run did what it says. Pitfalls: an element return is a no-display-form marker; caught throws
contribute nothing to the report. Net: `net_created: 1` with the category named.

### 23. `undo-redo-and-run-labels`
Steps: run with a label; `undo_last_run` with confirmation; `redo`; how the label distinguishes the
agent's work in Revit's undo history. Pitfalls: undo without confirmation is refused. Net: `net_created: 1`
by the labelled run, and zero on the model after the undo.

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
