#! python 3
import json, traceback
import Rhino
from Rhino.Geometry import Point3d, Sphere
import scriptcontext as sc
doc = sc.doc
r = {"steps": []}
def step(name, **kw):
    kw["name"] = name; r["steps"].append(kw)
try:
    n0 = doc.Objects.Count
    step("start", objects=n0, undo_active=doc.UndoActive, redo_active=doc.RedoActive)

    # 1. record with changes, then Undo(): does it revert exactly the record?
    sn = doc.BeginUndoRecord("MCP: spike record")
    a = doc.Objects.AddSphere(Sphere(Point3d(0,0,0), 5))
    b = doc.Objects.AddSphere(Sphere(Point3d(20,0,0), 5))
    ended = doc.EndUndoRecord(sn)
    step("record-with-2-adds", serial=sn, end_ok=ended, objects=doc.Objects.Count, undo_active=doc.UndoActive)
    undone = doc.Undo()
    step("undo-after-record", undo_returned=undone, objects=doc.Objects.Count, redo_active=doc.RedoActive)
    redone = doc.Redo()
    step("redo", redo_returned=redone, objects=doc.Objects.Count)
    doc.Undo()
    step("undo-again", objects=doc.Objects.Count)

    # 2. empty record: does it leave an undo entry?
    doc.ClearUndoRecords(True)
    step("cleared", undo_active=doc.UndoActive)
    sn2 = doc.BeginUndoRecord("MCP: empty record")
    ended2 = doc.EndUndoRecord(sn2)
    step("empty-record", serial=sn2, end_ok=ended2, undo_active=doc.UndoActive)
    undone2 = doc.Undo()
    step("undo-after-empty", undo_returned=undone2, objects=doc.Objects.Count)

    # 3. nested BeginUndoRecord while one is open
    sn3 = doc.BeginUndoRecord("outer")
    c = doc.Objects.AddSphere(Sphere(Point3d(0,20,0), 3))
    sn4 = doc.BeginUndoRecord("inner")
    d = doc.Objects.AddSphere(Sphere(Point3d(0,40,0), 3))
    e4 = doc.EndUndoRecord(sn4)
    e3 = doc.EndUndoRecord(sn3)
    step("nested", outer=sn3, inner=sn4, inner_end=e4, outer_end=e3, objects=doc.Objects.Count)
    u = doc.Undo()
    step("undo-after-nested", undo_returned=u, objects=doc.Objects.Count)
    u = doc.Undo()
    step("undo-after-nested-2", undo_returned=u, objects=doc.Objects.Count)

    # 4. what does the undo stack look like? (any API to inspect top entry name?)
    names = [m for m in dir(doc) if 'Undo' in m or 'Redo' in m]
    step("undo-api-surface", members=names)
except Exception as ex:
    r["error"] = traceback.format_exc()
open("/private/tmp/claude-501/-Users-nicholas-dev-eichler-connectors/85a53e63-f0b1-4b58-ada2-56628049742b/scratchpad/spikes/undo_probe.out.json","w").write(json.dumps(r, indent=1, default=str))
