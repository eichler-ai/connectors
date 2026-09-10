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
    step("state-after-previous-script", objects=doc.Objects.Count, undo_active=doc.UndoActive, redo_active=doc.RedoActive,
         rec_enabled=doc.UndoRecordingEnabled, rec_is_active=doc.UndoRecordingIsActive,
         cur_serial=doc.CurrentUndoRecordSerialNumber, next_serial=doc.NextUndoRecordSerialNumber,
         in_command=Rhino.Commands.Command.InCommand(), in_script=Rhino.Commands.Command.InScriptRunnerCommand())
    doc.Objects.Clear()
    doc.ClearUndoRecords(True)
    sn = doc.BeginUndoRecord("MCP: rec A")
    step("inside-record", rec_is_active=doc.UndoRecordingIsActive, cur_serial=doc.CurrentUndoRecordSerialNumber)
    doc.Objects.AddSphere(Sphere(Point3d(0,0,0), 5))
    doc.EndUndoRecord(sn)
    step("after-end", objects=doc.Objects.Count, undo_active=doc.UndoActive)
    # try Undo via the command instead of the API
    ok = Rhino.RhinoApp.RunScript("_-Undo", False)
    step("runscript-undo", ok=ok, objects=doc.Objects.Count, undo_active=doc.UndoActive, redo_active=doc.RedoActive)
except Exception as ex:
    r["error"] = traceback.format_exc()
open("/private/tmp/claude-501/-Users-nicholas-dev-eichler-connectors/85a53e63-f0b1-4b58-ada2-56628049742b/scratchpad/spikes/undo_probe2.out.json","w").write(json.dumps(r, indent=1, default=str))
