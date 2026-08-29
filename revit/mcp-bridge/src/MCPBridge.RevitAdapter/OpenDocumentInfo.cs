namespace MCPBridge.RevitAdapter;

/// <summary>
/// One open, non-linked document's identity summary -- the shape execute_script's document_id routing
/// uses for its candidates list when a requested id matches nothing (PRD §01: the error names what IS
/// addressable). Plain strings/bools only, deliberately: this rides on IUiApplicationAdapter, which
/// tier-1 fakes implement, and naming a Revit type here would drag a RevitAPI reference into the test
/// assembly (the documented silently-unloadable-test-assembly failure class).
/// </summary>
internal readonly record struct OpenDocumentInfo(string DocumentId, string Title, bool IsActive);
