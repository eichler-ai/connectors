# Revit MCP — coverage / gap matrix vs. the surveyed catalog

**Purpose.** A prioritized backlog of how-tos and API-coverage tests for *our* Revit connector,
built by using an open-source Revit MCP server (the surveyed catalog — BIMwright `rvt-mcp`,
Apache-2.0) as nothing more than a *checklist of Revit operations that a mature connector tends to
expose*. This document is authored from scratch. The only things taken from the surveyed catalog are
non-copyrightable facts: (a) the *operation* a tool performs, restated in our own words; (b) the
**Revit API types/members** that operation calls (facts about Autodesk's Revit API, not the
catalog's IP); (c) the toolset category name. **No source, comments, docstrings, or prose from the
surveyed catalog were copied or paraphrased structurally.** Every API member below is an Autodesk
Revit API fact and must still be **live-verified on Revit 2025 + 2027** before anything enters our
corpus.

Our model is different from the surveyed catalog's: they ship ~200 fixed tools, one per operation;
we ship a small tool surface (`execute_script` + discovery: `list_functions` / `search_functions` /
`describe_function`) and teach operations through the **how-to corpus** and prove them through the
**validation corpus** + **test-harness**. So a "gap" here is not "we lack a tool" — it's "our corpus
teaches no route to this operation and no test asserts the API members are discoverable + runnable."

---

## 1. Summary

- **Operations surveyed (catalog, general Revit; excludes the firm-specific `kei` DB toolset, the
  tool-baker/adaptive-bake machinery, and pure infra/UI):** ~185 operational handlers across ~21
  toolset categories.
- **Operations our corpus already covers** (a how-to teaches the route, or a validation/harness case
  exercises it): **~40** operations, concentrated in architectural modelling + the connector's own
  script/transaction/document/file mechanics.
- **Gaps by cluster (the deep-dive clusters):**

  | Cluster | Surveyed ops | Our status | Net gap |
  |---|---|---|---|
  | MEP (duct/pipe/conduit/cable tray/fittings/systems) | ~17 | none | **~17 (full gap)** |
  | Structural (beam/column/brace/foundation/rebar/loads) | ~12 | none | **~12 (full gap)** |
  | Exports beyond DWG/PNG (IFC/NWC/gbXML/FBX/DGN/DWF/PDF/CSV) | ~11 | DWG + PNG only | **~9** |
  | Materials (create/assign/assets/takeoff) | ~11 | none (material *param* touched via door family) | **~10** |
  | Links + shared coordinates | ~12 | none | **~12 (full gap)** |
  | Graphics overrides / view filters / view templates | ~14 | none | **~14 (full gap)** |
  | View creation (sections, callouts, elevations) | ~6 | callout leaders only (partial) | **~5** |
  | Batch annotation / tagging / keynotes | ~10 | single tag via rooms/areas | **~8** |
  | Revisions / worksets / phases / purge | ~9 | none | **~9** |

  Beyond the deep-dive clusters, our architectural core (walls, floors, roofs, rooms, areas, levels,
  grids, sheets, viewports, schedules, dimensions, text, families, groups, stairs, project info) is
  **well covered** — that's the corpus's design center.

---

## 2. Coverage table (grouped by toolset)

Status legend: **have** = a how-to teaches it or a validation/harness case runs it; **partial** =
an adjacent how-to touches the same API family but not this operation; **gap** = no route taught, no
test. Priority reflects how central the operation is to an AEC user of *our* connector, not the
catalog's completeness.

### MEP (`mep`) — full gap cluster
| Operation | Key Revit API members | Status | Prio |
|---|---|---|---|
| Create duct between two points | `Autodesk.Revit.DB.Mechanical.Duct.Create(doc, systemTypeId, ductTypeId, levelId, start, end)`; `MechanicalSystemType`; `DuctType`; `BuiltInParameter.RBS_CURVE_WIDTH/HEIGHT/DIAMETER_PARAM` | gap | High |
| Create pipe between two points | `Autodesk.Revit.DB.Plumbing.Pipe.Create(...)`; `PipeType`; `PipingSystemType`; `BuiltInParameter.RBS_PIPE_DIAMETER_PARAM` | gap | High |
| Create conduit run | `Autodesk.Revit.DB.Electrical.Conduit.Create(doc, typeId, start, end, levelId)`; `BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM` | gap | Med |
| Create cable tray run | `Autodesk.Revit.DB.Electrical.CableTray.Create(doc, typeId, start, end, levelId)`; `BuiltInParameter.RBS_CABLETRAY_WIDTH/HEIGHT_PARAM` | gap | Med |
| Create MEP fitting (elbow/tee/cross/transition/union) | `Document.Create.NewElbowFitting/NewTeeFitting/NewCrossFitting/NewTransitionFitting/NewUnionFitting(Connector...)` | gap | Med |
| Connect two MEP elements at connectors | connector-set iteration; `Connector.ConnectTo`; `ConnectorType.End/Curve`; connector `Origin` proximity | gap | Med |
| Place air terminal / lighting fixture (hosted MEP family) | `Document.Create.NewFamilyInstance(...)`; `FamilyPlacementType.OneLevelBasedHosted/WorkPlaneBased`; `BuiltInCategory.OST_DuctTerminal` | gap | Med |
| Query MEP connectors / systems / disconnects / inventory | `ConnectorManager`; `Connector`; `MEPSystem`; `MechanicalSystemType`/`PipingSystemType` collectors | gap | Low |
| Panel schedule read | `Autodesk.Revit.DB.Electrical.PanelScheduleView` / `PanelScheduleData` | gap | Low |
| Create MEP space | `Document.Create.NewSpace(...)`; `Autodesk.Revit.DB.Mechanical.Space` | gap | Low |

### Structural (`structural`) — full gap cluster
| Operation | Key Revit API members | Status | Prio |
|---|---|---|---|
| Create structural beam | `Document.Create.NewFamilyInstance(curve, symbol, level, StructuralType.Beam)`; `Line.Create`; `BuiltInCategory.OST_StructuralFraming` | gap | High |
| Create structural column | `Document.Create.NewFamilyInstance(..., StructuralType.Column)`; `BuiltInParameter.FAMILY_TOP_LEVEL_OFFSET_PARAM`; `OST_StructuralColumns` | gap | High |
| Create brace | `Document.Create.NewFamilyInstance(..., StructuralType.Brace)` | gap | Low |
| Create isolated footing / wall foundation | `NewFamilyInstance(..., StructuralType.Footing)`; `OST_StructuralFoundation`; `Autodesk.Revit.DB.Structure.*` | gap | Med |
| Create rebar set | `Autodesk.Revit.DB.Structure.Rebar.CreateFromCurves(...)`; `RebarBarType`; `RebarHookType`; `RebarStyle.Standard`; `RebarLayoutKind.FixedNumber/MaximumSpacing/Single`; `RebarHookOrientation` | gap | Low |
| Create/read structural loads | `AreaLoad.Create`; `LineLoad`/`PointLoad`; `BuiltInParameter.LOAD_FORCE_FX..FZ`, `LOAD_MOMENT_MX..MZ`, `LOAD_CASE_ID`; `OST_PointLoads/OST_LineLoads/OST_AreaLoads` | gap | Low |
| Tag structural framing | `IndependentTag.Create`; `OST_StructuralFraming` | gap | Low |

### Export (`export`) — partial (DWG + PNG have)
| Operation | Key Revit API members | Status | Prio |
|---|---|---|---|
| Export DWG | `Document.Export(folder, name, viewIds, DWGExportOptions)` | **have** (`export-views-dwg`, validation #1) | — |
| Export view as PNG/JPG image | `Document.ExportImage(ImageExportOptions)`; `ImageFileType.PNG/JPEGLossless`; `ImageResolution.DPI`; `ZoomFitType`; `ExportRange.SetOfViews` | **have** (`capture-a-view-as-a-png-image...`) | — |
| Export IFC | `Document.Export(..., IFCExportOptions)` | gap | High |
| Export NWC (Navisworks) | `Document.Export(..., NavisworksExportOptions)` (requires the NWC exporter add-in installed) | gap | Med |
| Export gbXML (energy) | `Document.Export(..., GBXMLExportOptions)` | gap | Low |
| Export PDF | `Document.Export(folder, name, viewIds, PDFExportOptions)` (2022+) | gap | High |
| Export FBX / DGN / DWF(x) | `FBXExportOptions`; `DGNExportOptions`; `DWFExportOptions`/`DWFXExportOptions` | gap | Low |
| Export schedule to CSV | `ViewSchedule.Export(folder, name, ViewScheduleExportOptions)` | gap | Med |
| Export elements/room data | `FilteredElementCollector` projection + file write via connector Publish | partial (results/files how-tos teach publish) | Low |

### Materials (`materials`) — full gap cluster
| Operation | Key Revit API members | Status | Prio |
|---|---|---|---|
| Create material | `Autodesk.Revit.DB.Material.Create(doc, name)` | gap | Med |
| Assign material to element | `BuiltInParameter.STRUCTURAL_MATERIAL_PARAM`; category/compound-structure material set | gap | Med |
| Set thermal / structural asset | `PropertySetElement.Create(doc, ThermalAsset)` / `StructuralAsset`; `Material.ThermalAssetId`/`StructuralAssetId` | gap | Low |
| Set appearance / identity | `AppearanceAssetElement`; `Material.AppearanceAssetId`; identity `BuiltInParameter`s | gap | Low |
| Material takeoff / quantities | `Element.GetMaterialIds`, `Element.GetMaterialArea`, `Element.GetMaterialVolume` | gap | Med |
| List / duplicate / get properties | `FilteredElementCollector(Material)`; `Material.Duplicate` | gap | Low |

### Links + shared coordinates (`links`) — full gap cluster
| Operation | Key Revit API members | Status | Prio |
|---|---|---|---|
| Link a Revit model | `RevitLinkType.Create(doc, ModelPath, options)`; `RevitLinkInstance.Create(doc, typeId)`; `ModelPathUtils` | gap | High |
| Reload / unload link | `RevitLinkType.Reload` / `.Unload`; `RevitLinkType.IsLoaded` | gap | Med |
| Query linked-model elements | `RevitLinkInstance.GetLinkDocument`; `FilteredElementCollector` on the link doc; `RevitLinkInstance.GetTotalTransform` | gap | Med |
| Link / import CAD (DWG) into a view | `Document.Link`/`Document.Import`; `DWGImportOptions` | gap | Med |
| Acquire coordinates from link | `Document.AcquireCoordinates(linkInstanceId)`; base-point `BuiltInParameter.BASEPOINT_EASTWEST/NORTHSOUTH/ELEVATION/ANGLETON_PARAM` | gap | Med |
| Publish coordinates to link | `Document.PublishCoordinates(linkInstanceId)` | gap | Low |
| Read link / project coordinate system | `RevitLinkInstance.GetTotalTransform`/`GetTransform`; `ProjectLocation`; `Transform` | gap | Med |
| Set project base point | base-point element `BuiltInParameter`s (see acquire row) | gap | Low |

### View, graphics, filters (`view` / `graphics`) — mostly gap
| Operation | Key Revit API members | Status | Prio |
|---|---|---|---|
| Create section / elevation / callout / 3D view | `ViewSection.CreateSection`; `ViewSection.CreateDetail`; `Elevation.NewElevationMarker`; `View3D.CreateIsometric`; `ViewFamilyType` lookup | partial (callout-leaders how-to only) | High |
| Override element graphics in view | `View.SetElementOverrides(id, OverrideGraphicSettings)`; `OverrideGraphicSettings` | gap | Med |
| Create/apply view filter | `ParameterFilterElement.Create`; `View.AddFilter`; `View.SetFilterOverrides` | gap | Med |
| Create/apply view template | `View.CreateViewTemplate`; `View.ViewTemplateId`; `View.ApplyViewTemplateParameters` | gap | Med |
| Category / element visibility, crop, scale, phase | `View.SetCategoryHidden`; `View.CropBox`/`CropBoxActive`; `View.Scale`; `View.get_Parameter(VIEW_PHASE)` | partial (levels/grids how-to creates plan views) | Med |
| Color/split elements, filled region | `FilledRegion.Create`; `OverrideGraphicSettings` | gap | Low |

### Annotation & tagging (`annotation`)
| Operation | Key Revit API members | Status | Prio |
|---|---|---|---|
| Text note | `TextNote.Create`; `TextNote.Text` (trailing `\r` trap) | **have** (`text-notes-and-annotation-text`) | — |
| Dimension in plan | `Document.Create.NewDimension`; `ReferenceArray` | **have** (`dimensions-in-plan-views`) | — |
| Single element / room / area tag | `IndependentTag.Create`; `RoomTag`; `AreaTag` | partial (rooms/areas how-tos tag one) | — |
| Batch-tag all of a category on a view/level | `IndependentTag.Create` looped over a `FilteredElementCollector` | gap | High |
| Keynotes (apply/list) | `BuiltInParameter.KEYNOTE_PARAM`; `KeynoteEntry` / keynote table | gap | Low |
| Detail line | `Document.Create.NewDetailCurve` | gap | Low |

### Schedules (`schedule`)
| Operation | Key Revit API members | Status | Prio |
|---|---|---|---|
| Create schedule + fields + sort + read back | `ViewSchedule.CreateSchedule`; `ScheduleDefinition.GetSchedulableFields`; `AddField`; `GetTableData`/`GetCellText` | **have** (`schedules-create-with-fields`, door-schedule how-to) | — |
| Place schedule on sheet | `ScheduleSheetInstance.Create` | **have** (door-schedule how-to) | — |
| Schedule filter/sort/formula edits | `ScheduleFilter`; `ScheduleSortGroupField`; `ScheduleField.Formula` | partial | Low |
| Export schedule CSV | `ViewSchedule.Export` | gap | Med |

### Rooms / areas (`rooms`)
| Operation | Key Revit API members | Status | Prio |
|---|---|---|---|
| Create room + tag + area | `Document.Create.NewRoom`; `RoomTag`; `Room.Area`; `LEVEL_ROOM_COMPUTATION_HEIGHT` | **have** (`rooms-create-tag-area`) | — |
| Area plan + area + tag; area scheme clone | `ViewPlan.CreateAreaPlan`; `Document.Create.NewArea`; `AreaScheme` | **have** (two area how-tos) | — |
| Room separator lines | `Document.Create.NewRoomBoundaryLines` | gap | Low |
| Auto-create rooms from walls; room finishes | plan-topology loop; `SpatialElementGeometryCalculator` | gap | Low |

### Families / parameters / organization (mostly have)
| Operation | Key Revit API members | Status | Prio |
|---|---|---|---|
| Place hosted family instance | `Document.Create.NewFamilyInstance(...)`; `FamilySymbol.Activate` | **have** (`family-instances-place-hosted`) | — |
| Build family doc, load, place | `Application.NewFamilyDocument`; `Document.LoadFamily`; family param build | **have** (`family-document-build-load-place`, custom door how-to) | — |
| Element params read/write/delete | `LookupParameter`; `get_Parameter(BuiltInParameter)`; `StorageType`; `Parameter.Set` | **have** (`element-parameters-read-write-delete`) | — |
| Shared/project parameters + binding | shared-param file via `System.IO` + `OpenSharedParameterFile`; `GroupTypeId` (2025+); category `Binding` | **have** (`shared-parameters-file-and-binding`) | — |
| Type parameters read/write; duplicate/rename type | `Element.GetTypeId`; `ElementType.Duplicate`; type `Parameter.Set` | partial | Med |
| Groups create/edit/propagate | `GroupType`; `Group`; member-edit propagation trap | **have** (`groups-create-edit-propagate`) | — |
| Levels, grids, plan views | `Level.Create`; `Grid.Create`; `ViewPlan.Create` | **have** (`levels-grids-and-plan-views`) | — |
| Project info (name/number) | `ProjectInfo` `BuiltInParameter`s | **have** (`set-the-project-name...`) | — |
| Phases / worksets / purge / revisions | `Phase`; `WorksetTable`; `Document.Delete` purge; `Revision.Create`; `Sheet.AddRevision` | gap | Low |

### Architectural elements (`create` / core) — have
| Walls (create+join) | Floors (from loop) | Roofs (hipped footprint) | Stairs (edit scope) | Sheets/viewports/titleblocks | all **have**. |

### Connector mechanics (our own surface, no catalog analog needed)
Transactions, self-transacting calls between blocks, documents create/write/close, lifecycle
settle/save/close, files publish + audit trail, results/mutations, undo/redo/run-labels — all
**have**. These are our differentiators and have no equivalent in a fixed-tool catalog.

---

## 3. Prioritized backlog

### 3a. How-tos worth authoring (top ~18)

Each fits the existing corpus schema (`kind: howto`, retrieval-optimized `task` sentence, numbered
step comments carrying the route, `pitfalls[]` keyed to connector error `code`s where one applies,
a verification stamp of net `mutations`). "Fold into" names an existing lineage a submission could
extend rather than starting fresh.

| # | Proposed how-to (title / one-line task) | Revit API members it exercises | Fold into |
|---|---|---|---|
| 1 | **Run a duct between two points on a level** — model a mechanical duct of a named type | `Mechanical.Duct.Create`; `MechanicalSystemType`; `DuctType`; `RBS_CURVE_*_PARAM` | new `mep-` lineage |
| 2 | **Run a pipe between two points** — model a plumbing pipe, set its diameter | `Plumbing.Pipe.Create`; `PipeType`; `PipingSystemType`; `RBS_PIPE_DIAMETER_PARAM` | mep lineage |
| 3 | **Connect two MEP elements / add a fitting** — join duct/pipe runs with an elbow or tee | `Connector.ConnectTo`; `Document.Create.NewElbowFitting/NewTeeFitting`; `ConnectorManager` | mep lineage |
| 4 | **Place a hosted MEP family (air terminal / light)** — put a ceiling-hosted fixture | `NewFamilyInstance`; `FamilyPlacementType.OneLevelBasedHosted`; `OST_DuctTerminal` | folds into `family-instances-place-hosted` |
| 5 | **Create a structural beam between two grid points** — frame a beam of a named type | `NewFamilyInstance(curve, symbol, level, StructuralType.Beam)`; `Line.Create` | new `structural-` lineage |
| 6 | **Create a structural column at a point** — set a column, top-level offset | `NewFamilyInstance(..., StructuralType.Column)`; `FAMILY_TOP_LEVEL_OFFSET_PARAM` | structural lineage |
| 7 | **Create an isolated footing under a column** — place a foundation | `NewFamilyInstance(..., StructuralType.Footing)`; `OST_StructuralFoundation` | structural lineage |
| 8 | **Export a model to IFC** — hand a consultant an IFC | `Document.Export(..., IFCExportOptions)` | folds into `export-views-dwg` as sibling |
| 9 | **Export sheets to PDF** — issue a PDF set from a sheet selection | `Document.Export(..., PDFExportOptions)` (2022+) | export lineage |
| 10 | **Export a coordination model to NWC** — publish for Navisworks | `Document.Export(..., NavisworksExportOptions)` (needs NWC exporter) | export lineage |
| 11 | **Export a schedule to CSV** — get schedule rows as a file | `ViewSchedule.Export(ViewScheduleExportOptions)` | folds into `schedules-create-with-fields` |
| 12 | **Link a Revit model and place it by shared coordinates** — attach a consultant model | `RevitLinkType.Create`; `RevitLinkInstance.Create`; `ModelPathUtils` | new `links-` lineage |
| 13 | **Query elements inside a linked model** — read the link's walls/rooms | `RevitLinkInstance.GetLinkDocument`; `GetTotalTransform`; `FilteredElementCollector` on link doc | links lineage |
| 14 | **Acquire / publish shared coordinates with a link** — align project base points | `Document.AcquireCoordinates`; `Document.PublishCoordinates`; `BASEPOINT_*_PARAM` | links lineage |
| 15 | **Create a material and assign it to an element** — author + apply a material | `Material.Create`; `STRUCTURAL_MATERIAL_PARAM` / compound-structure material | new `materials-` lineage |
| 16 | **Run a material takeoff / quantities** — sum area/volume by material | `Element.GetMaterialIds`; `GetMaterialArea`; `GetMaterialVolume` | materials lineage |
| 17 | **Create a section view and place it on a sheet** — cut a section, issue it (validation case #12) | `ViewSection.CreateSection`; `ViewFamilyType`; `Viewport.Create` | folds into `sheets-viewports-and-title-blocks` |
| 18 | **Batch-tag every door/element of a category on a level** — auto-tag (validation case #11) | `IndependentTag.Create` looped over `FilteredElementCollector`; `OST_Doors` | folds into `text-notes-and-annotation-text`→ new annotation lineage |

Secondary (Med/Low, author after the above): view filters + overrides
(`ParameterFilterElement.Create` / `View.SetElementOverrides`), view templates
(`View.CreateViewTemplate`), conduit/cable-tray runs, rebar sets, structural loads, gbXML/FBX/DGN
exports, revisions/worksets/phases.

### 3b. API-coverage test ideas

Two kinds, matching our two test tiers. **Discovery tests** (`connector_api_discovery_test.go` /
`semantic_search_test.go` style) assert `search_functions`/`describe_function` actually *surfaces*
the member from an intent phrase — the surveyed members above are the assertion targets. **Live
`execute_script` success cases** become `validation_corpus_test.go` replays once passed.

| Operation | Assert discoverable (search intent → member) | Live `execute_script` success condition |
|---|---|---|
| Create duct | "create a duct" → `Mechanical.Duct.Create` on page 1 | duct count +1; `RBS_CURVE_WIDTH_PARAM` reads back the set width |
| Create pipe | "create a pipe" → `Plumbing.Pipe.Create` | pipe +1; diameter param equals requested |
| Structural beam | "create a beam between two points" → `NewFamilyInstance` + `StructuralType.Beam` | framing element +1 on `OST_StructuralFraming` with correct end levels |
| Structural column | "place a column" → `NewFamilyInstance` + `StructuralType.Column` | column +1; top offset param set |
| Export IFC | "export to IFC" → `IFCExportOptions` + `Document.Export` | `.ifc` file exists, non-zero |
| Export PDF | "export sheets to PDF" → `PDFExportOptions` | `.pdf` exists, non-zero (guard 2022+ availability) |
| Link Revit model | "link a revit model" → `RevitLinkType.Create` + `RevitLinkInstance.Create` | link instance present; `GetLinkDocument` non-null |
| Shared coordinates | "acquire coordinates from a link" → `Document.AcquireCoordinates` | base-point `BASEPOINT_EASTWEST_PARAM` changes to the link's value |
| Create material | "create a material" → `Material.Create` | material +1, findable by name |
| Material takeoff | "material quantities of a wall" → `Element.GetMaterialArea` | non-zero area for a known wall |
| Section view | "create a section view" → `ViewSection.CreateSection` | section view +1; placeable on a sheet |
| Batch tag | "tag every door" → `IndependentTag.Create` | tag count == door count on the level |
| View filter/override | "override element color in a view" → `OverrideGraphicSettings` + `View.SetElementOverrides` | element's projection color reads back as set |

The discovery half is the highest leverage: several of these members are the kind that ranked poorly
in past sweeps (issues #80/#87 — intent search weak, exact-name search fine), so each new cluster
should get a `search_functions` assertion, not just a script replay.

---

## 4. Notes

**Version-sensitive members (live-verify on 2025 AND 2027 carefully; per-version `#if` branches were
visible in the surveyed catalog for these).**
- **`ElementId` is 64-bit from Revit 2024+.** Any script passing element ids through JSON must not
  assume 32-bit `int`; our scripts already lean on the connector's globals, but discovery tests that
  round-trip ids should cover the 2027 (int64) path explicitly. (The catalog carries a compat shim
  keyed on `REVIT2024_OR_GREATER` for exactly this.)
- **`PDFExportOptions`** is 2022+; **`Document.Export(..., PDFExportOptions)`** signatures shifted
  across versions — verify the sheet-set overload on both 2025 and 2027.
- **Rebar** (`Rebar.CreateFromCurves`, `RebarBarType`, `RebarHookType`) has version-branched surface;
  low priority, but if authored, test both versions.
- **`GroupTypeId` replaced `BuiltInParameterGroup`** (2024/2025+) — already reflected in our
  `shared-parameters-file-and-binding` how-to; keep any new parameter-binding how-to on `GroupTypeId`.
- **`Material` thermal/structural assets** (`PropertySetElement.Create` with `ThermalAsset`/
  `StructuralAsset`) had version-branched code in the survey — verify asset construction on both.
- **`Floor.Create` / `ViewPlan.CreateAreaPlan` / `ScheduleSheetInstance.Create`** signatures are
  already version-pinned in our corpus; new sketch/view-based how-tos should follow the same pattern
  of confirming the current-version overload live rather than from memory.
- **NWC export** depends on the Navisworks exporter add-in being installed in the target Revit —
  a live-verify prerequisite, not just an API fact.

**Process guardrails (restated).** Everything above is authored independently from the surveyed
catalog used only as an operation checklist; no catalog code or prose was reproduced. Every Revit API
member is a claim about Autodesk's API to be **proven live** via the discovery-only grading protocol
in `revit/docs/validation-corpus.md` (state the task in natural language, find each member through
`search_functions` only, run via `execute_script` against a real connected Revit, check the live
outcome — not "didn't throw"), then frozen as a `validation_corpus_test.go` replay. No how-to enters
the corpus until it passes the `TestHowToSweep` / `TestHowToEndToEnd` stamps on **both Revit 2025 and
2027**. Skip the `kei` firm-database toolset entirely — not general Revit.
