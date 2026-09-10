#! python 3
import sys, platform, threading, json
import Rhino
import scriptcontext as sc
out = {
  "python": sys.version,
  "impl": platform.python_implementation(),
  "thread": threading.current_thread().name,
  "rhino": str(Rhino.RhinoApp.Version),
  "doc": sc.doc.Name if sc.doc else None,
  "doc_path": sc.doc.Path if sc.doc else None,
  "objects": sc.doc.Objects.Count if sc.doc else None,
  "docs_open": Rhino.RhinoDoc.OpenDocuments().Length,
}
open("/private/tmp/claude-501/-Users-nicholas-dev-eichler-connectors/85a53e63-f0b1-4b58-ada2-56628049742b/scratchpad/spikes/probe.out.json","w").write(json.dumps(out, indent=1))
