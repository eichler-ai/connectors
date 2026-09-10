using System;
using System.Collections.Generic;
using System.Linq;

namespace Rhino.MCPBridge.Core.Execution.Python;

/// <summary>
/// The Python half of the denylist (PRD §07/§14): the same tables as <see cref="ScriptApiDenylist"/>,
/// applied to Python source before it reaches CPython, so a refused script changes nothing and is
/// refused deterministically on the connection thread. Both languages raise the same
/// <see cref="ScriptApiDenylistViolationException"/> with the same codes.
///
/// It is a token walk with import-alias resolution rather than a bound-symbol walk, because there is
/// no type checker for Python. A dotted chain is resolved through <c>import</c>/<c>from … import … as</c>
/// aliases and the bound globals (<c>doc</c>, <c>scriptcontext.doc</c>, <c>Rhino.RhinoDoc.ActiveDoc</c>
/// all denote a RhinoDoc) to a qualified name, and that name is keyed against the tables -- never a
/// bare member name, so a script's own <c>def Undo(self)</c> is not refused. What the walk cannot see,
/// and says so here: a value copied into another variable first (<c>d = doc; d.Undo()</c>) or reached
/// through a call's return value. The runtime cannot close that gap for RhinoCommon either, so the
/// denylist is what Revit §14 calls it -- a guard against the common accident, with the dynamic-code
/// builtins (<c>exec</c>, <c>eval</c>, <c>__import__</c>, computed <c>getattr</c>) refused so the
/// obvious workaround is not silent. The trade-offs in the other direction, wrong refusals: a
/// variable named <c>doc</c> holding something else (an XML document's <c>Save</c> is gated as
/// RhinoDoc's), and under <c>from rhinoscriptsyntax import *</c> a script's own function named like
/// an interactive one (<c>def GetLayer</c>) is refused at its call. f-string interpolations are
/// tokenized and walked like the rest of the text.
/// </summary>
internal static class PythonScriptGuard
{
    private const string RhinoDoc = "Rhino.RhinoDoc";
    private const string RhinoApp = "Rhino.RhinoApp";
    private const string RhinoInputCustom = "Rhino.Input.Custom.";
    private const string RhinoScriptSyntax = "rhinoscriptsyntax";

    /// <summary>Bound globals and idioms that denote the document, mapped onto the RhinoDoc type.</summary>
    private static readonly (string Prefix, string Type)[] DocumentPrefixes =
    {
        ("doc", RhinoDoc),
        ("scriptcontext.doc", RhinoDoc),
        (RhinoDoc + ".ActiveDoc", RhinoDoc),
    };

    /// <summary>rhinoscriptsyntax functions that prompt on the command line or open a picker (the
    /// module's docstrings: "Prompts the user …"). Every other rs.Get* reads state and is allowed.</summary>
    internal static readonly IReadOnlySet<string> InteractiveRhinoScriptFunctions = new HashSet<string>
    {
        "GetAngle", "GetBoolean", "GetBox", "GetColor", "GetCursorPos", "GetDistance", "GetEdgeCurves", "GetInteger",
        "GetLayer", "GetLayers", "GetLine", "GetLinetype", "GetMeshFaces", "GetMeshVertices", "GetObject", "GetObjectEx",
        "GetObjectGrips", "GetObjects", "GetObjectsEx", "GetPoint", "GetPointOnCurve", "GetPointOnMesh", "GetPointOnSurface",
        "GetPoints", "GetPolyline", "GetPrintWindow", "GetReal", "GetRectangle", "GetString", "GetView", "ComboListBox",
        "CheckListBox", "EditBox", "ListBox", "MessageBox", "MultiListBox", "OpenFileName", "OpenFileNames", "PropertyListBox",
        "RealBox", "SaveFileName", "StringBox", "TextBox", "PopupMenu", "BrowseForFolder",
    };

    /// <summary>Builtins that load or evaluate code the walk cannot see.</summary>
    private static readonly IReadOnlySet<string> DynamicCodeBuiltins = new HashSet<string> { "exec", "eval", "compile", "__import__" };

    /// <summary>Names that end the Rhino process. <c>sys.exit</c>/<c>exit()</c>/<c>SystemExit</c> are NOT
    /// here: inside the embedded interpreter they raise SystemExit, which ends the run, not Rhino
    /// (verified live), so they are the ordinary "stop early" idiom.</summary>
    private static readonly IReadOnlySet<string> ExitNames = new HashSet<string>
    {
        RhinoApp + ".Exit", RhinoScriptSyntax + ".Exit", "os._exit", "os.abort", "os.kill",
    };

    public sealed class Analysis
    {
        public IReadOnlyList<string> LifecycleMembers { get; init; } = Array.Empty<string>();
        public bool RequiresLifecycleConfirmation => LifecycleMembers.Count > 0;
    }

    /// <summary>Throws <see cref="ScriptApiDenylistViolationException"/> for a hard-blocked use; returns
    /// the lifecycle members found (the caller decides whether confirmation was given).</summary>
    public static Analysis Analyze(string scriptText)
    {
        var tokens = PythonTokenizer.Tokenize(scriptText);
        var scope = new Scope();
        scope.CollectImports(tokens);

        var lifecycle = new List<string>();
        var seen = new HashSet<string>();
        var inImport = false;
        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            // An import statement names modules, not uses; the scope already read it.
            if (t.Kind == PythonTokenKind.Newline || t.IsOp(";"))
            {
                inImport = false;
                continue;
            }

            if (t.Kind != PythonTokenKind.Name || (i > 0 && tokens[i - 1].IsOp(".")))
            {
                continue;
            }

            if (t.IsName("import") || t.IsName("from"))
            {
                inImport = true;
            }

            if (inImport)
            {
                continue;
            }

            if (i > 0 && (tokens[i - 1].IsName("def") || tokens[i - 1].IsName("class")))
            {
                continue; // a definition, not a use
            }

            string qualified;
            int end;
            if (t.IsName("getattr") && i + 1 < tokens.Count && tokens[i + 1].IsOp("("))
            {
                (qualified, end) = ReadGetAttr(tokens, i, scope);
            }
            else
            {
                (var chain, end) = ReadChain(tokens, i);
                qualified = scope.Resolve(chain);
            }

            var called = end < tokens.Count && tokens[end].IsOp("(");
            Check(qualified, called, tokens, end, lifecycle, seen);
        }

        return new Analysis { LifecycleMembers = lifecycle };
    }

    private static void Check(string qualified, bool called, List<PythonToken> tokens, int callParen, List<string> lifecycle, HashSet<string> seen)
    {
        if (qualified.Length == 0)
        {
            return;
        }

        if (ExitNames.Contains(qualified))
        {
            throw ScriptApiDenylistViolationException.UndoOrExitMember(qualified);
        }

        // 2: `builtins.exec` and `__builtins__.exec` are the same call.
        var bare = qualified.StartsWith("builtins.", StringComparison.Ordinal) ? qualified.Substring("builtins.".Length)
            : qualified.StartsWith("__builtins__.", StringComparison.Ordinal) ? qualified.Substring("__builtins__.".Length)
            : qualified;
        if (DynamicCodeBuiltins.Contains(bare) || qualified == "importlib" || qualified.StartsWith("importlib.", StringComparison.Ordinal))
        {
            throw DynamicCode(qualified);
        }

        if (qualified.StartsWith(RhinoDoc + ".", StringComparison.Ordinal))
        {
            var member = Segment(qualified, RhinoDoc);
            if (ScriptApiDenylist.DeniedMembers[RhinoDoc].Contains(member))
            {
                throw ScriptApiDenylistViolationException.UndoOrExitMember(RhinoDoc + "." + member);
            }

            if (ScriptApiDenylist.LifecycleMembersByType[RhinoDoc].Contains(member) && seen.Add(RhinoDoc + "." + member))
            {
                lifecycle.Add(RhinoDoc + "." + member);
            }

            return;
        }

        foreach (var type in ScriptApiDenylist.DeniedTypes)
        {
            if (qualified.StartsWith(type + ".", StringComparison.Ordinal))
            {
                throw ScriptApiDenylistViolationException.InteractiveGetter(type + "." + Segment(qualified, type));
            }
        }

        if (qualified.StartsWith(RhinoInputCustom, StringComparison.Ordinal) && called
            && qualified.Substring(RhinoInputCustom.Length).StartsWith("Get", StringComparison.Ordinal)
            && !qualified.Substring(RhinoInputCustom.Length).Contains('.'))
        {
            // Constructing one of the Get* getter objects (GetPoint(), GetObject() ...): the same class as
            // deriving from GetBaseClass in C#.
            throw ScriptApiDenylistViolationException.InteractiveGetter(qualified);
        }

        if (qualified.StartsWith(RhinoScriptSyntax + ".", StringComparison.Ordinal))
        {
            var fn = Segment(qualified, RhinoScriptSyntax);
            if (InteractiveRhinoScriptFunctions.Contains(fn))
            {
                throw ScriptApiDenylistViolationException.InteractiveGetter(RhinoScriptSyntax + "." + fn);
            }

            if (fn == "Command" && called)
            {
                CheckCommandStrings(RhinoScriptSyntax + ".Command", tokens, callParen, lifecycle, seen);
            }

            return;
        }

        if ((qualified == RhinoApp + ".RunScript" || qualified == RhinoApp + ".ExecuteCommand") && called)
        {
            CheckCommandStrings(qualified, tokens, callParen, lifecycle, seen);
        }
    }

    /// <summary>The literal string arguments of the call opening at <paramref name="paren"/>, scanned for
    /// command tokens exactly as the C# walk scans constant arguments.</summary>
    private static void CheckCommandStrings(string method, List<PythonToken> tokens, int paren, List<string> lifecycle, HashSet<string> seen)
    {
        var depth = 0;
        for (var i = paren; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.IsOp("(") || t.IsOp("[") || t.IsOp("{")) { depth++; continue; }
            if (t.IsOp(")") || t.IsOp("]") || t.IsOp("}"))
            {
                if (--depth == 0) return;
                continue;
            }

            if (t.Kind != PythonTokenKind.String)
            {
                continue; // any depth: str('_Exit') and ('_Exi' + 't') fragments are scanned too
            }

            foreach (var token in ScriptApiDenylist.CommandTokens(t.Text))
            {
                if (ScriptApiDenylist.DeniedCommandTokens.Contains(token))
                {
                    throw ScriptApiDenylistViolationException.UndoOrExitMember(method + "(\"" + token + "\")");
                }

                if (ScriptApiDenylist.LifecycleCommandTokens.Contains(token))
                {
                    var key = method + "(\"" + token + "\")";
                    if (seen.Add(key)) lifecycle.Add(key);
                }
            }
        }
    }

    private static ScriptApiDenylistViolationException DynamicCode(string name) =>
        ScriptApiDenylistViolationException.Denied(name,
            "It loads or evaluates code the connector cannot analyse, which would defeat the checks that keep a script from prompting for input, exiting Rhino, or touching the undo stack.",
            "Write the calls directly in the script; everything reachable through exec/eval/__import__/importlib or a computed getattr is reachable by name.");

    private static string Segment(string qualified, string prefix)
    {
        var rest = qualified.Substring(prefix.Length + 1);
        var dot = rest.IndexOf('.');
        return dot < 0 ? rest : rest.Substring(0, dot);
    }

    /// <summary>Reads <c>Name(.Name)*</c> starting at <paramref name="i"/>; returns the dotted chain and the index after it.</summary>
    private static (string Chain, int End) ReadChain(List<PythonToken> tokens, int i)
    {
        // `(doc).Undo()` is `doc.Undo()`: count the parens that directly wrap the chain's head so the
        // matching closers can be stepped over when a `.Name` follows them.
        var wrapping = 0;
        for (var k = i - 1; k >= 0 && tokens[k].IsOp("("); k--) wrapping++;

        var parts = new List<string> { tokens[i].Text };
        var j = i + 1;
        while (j + 1 < tokens.Count)
        {
            if (tokens[j].IsOp(".") && tokens[j + 1].Kind == PythonTokenKind.Name)
            {
                parts.Add(tokens[j + 1].Text);
                j += 2;
            }
            else if (wrapping > 0 && tokens[j].IsOp(")"))
            {
                // Step over up to `wrapping` closers when a `.Name` follows them.
                var k = j;
                var closed = 0;
                while (closed < wrapping && k < tokens.Count && tokens[k].IsOp(")")) { k++; closed++; }
                if (k + 1 < tokens.Count && tokens[k].IsOp(".") && tokens[k + 1].Kind == PythonTokenKind.Name)
                {
                    wrapping -= closed;
                    j = k;
                }
                else
                {
                    break;
                }
            }
            else
            {
                break;
            }
        }

        return (string.Join(".", parts), j);
    }

    /// <summary>
    /// <c>getattr(chain, "member")</c> reads as <c>chain.member</c>. A second argument that is not a
    /// string literal is a computed member name, refused as dynamic code. Returns the resolved name and
    /// the index of the token after the call's closing paren, so a trailing <c>(</c> is seen as a call.
    /// </summary>
    private static (string Qualified, int End) ReadGetAttr(List<PythonToken> tokens, int i, Scope scope)
    {
        var open = i + 1;
        var close = MatchingParen(tokens, open);
        // The receiver: a dotted chain the walk can resolve, or any other expression (a call, a
        // subscript), which resolves to nothing and is allowed -- the member name is what matters.
        string? chain = null;
        var afterReceiver = open + 1;
        if (afterReceiver < close && tokens[afterReceiver].Kind == PythonTokenKind.Name)
        {
            (chain, afterReceiver) = ReadChain(tokens, afterReceiver);
        }

        if (chain is null || afterReceiver >= close || !tokens[afterReceiver].IsOp(","))
        {
            afterReceiver = SkipToTopLevelComma(tokens, open + 1, close);
            chain = null;
        }

        var describe = "getattr(" + (chain ?? "<expression>") + ", <expression>)";
        if (afterReceiver >= close)
        {
            throw DynamicCode(describe);
        }

        // The member name must be ONE string literal that is the whole argument: "Und" + "o",
        // "Und" "o" (implicit concatenation) and name variables are computed names.
        var arg = tokens[afterReceiver + 1];
        var afterArg = afterReceiver + 2;
        if (arg.Kind != PythonTokenKind.String || afterArg > close || !(tokens[afterArg].IsOp(")") || tokens[afterArg].IsOp(",")))
        {
            throw DynamicCode(describe);
        }

        return (chain is null ? "" : scope.Resolve(chain + "." + arg.Text), close + 1);
    }

    /// <summary>The index of the first top-level comma in (from, to), or to when there is none.</summary>
    private static int SkipToTopLevelComma(List<PythonToken> tokens, int from, int to)
    {
        var depth = 0;
        for (var j = from; j < to; j++)
        {
            if (tokens[j].IsOp("(") || tokens[j].IsOp("[") || tokens[j].IsOp("{")) depth++;
            else if (tokens[j].IsOp(")") || tokens[j].IsOp("]") || tokens[j].IsOp("}")) depth--;
            else if (depth == 0 && tokens[j].IsOp(",")) return j;
        }

        return to;
    }

    private static int MatchingParen(List<PythonToken> tokens, int open)
    {
        var depth = 0;
        for (var j = open; j < tokens.Count; j++)
        {
            if (tokens[j].IsOp("(") || tokens[j].IsOp("[") || tokens[j].IsOp("{")) depth++;
            else if (tokens[j].IsOp(")") || tokens[j].IsOp("]") || tokens[j].IsOp("}"))
            {
                if (--depth == 0) return j;
            }
        }

        return tokens.Count - 1;
    }

    /// <summary>Import aliases and star imports, collected in one pass over the logical lines.</summary>
    private sealed class Scope
    {
        private readonly Dictionary<string, string> _aliases = new();
        private readonly List<string> _starModules = new();

        public void CollectImports(List<PythonToken> tokens)
        {
            var i = 0;
            while (i < tokens.Count)
            {
                var lineEnd = i;
                while (lineEnd < tokens.Count && tokens[lineEnd].Kind != PythonTokenKind.Newline) lineEnd++;
                var first = i;
                // `import` and `from` may follow a `;` or an indent; the logical line's first token suffices
                // for the common case, and a statement after `;` is walked below.
                for (var s = first; s < lineEnd; s++)
                {
                    if (s == first || tokens[s - 1].IsOp(";"))
                    {
                        if (tokens[s].IsName("import")) ParseImport(tokens, s + 1, lineEnd);
                        else if (tokens[s].IsName("from")) ParseFrom(tokens, s + 1, lineEnd);
                    }
                }

                i = lineEnd + 1;
            }
        }

        // import a.b.c [as x], d [as y]
        private void ParseImport(List<PythonToken> tokens, int i, int end)
        {
            while (i < end)
            {
                var parts = new List<string>();
                while (i < end && tokens[i].Kind == PythonTokenKind.Name && !tokens[i].IsName("as"))
                {
                    parts.Add(tokens[i].Text);
                    i++;
                    if (i < end && tokens[i].IsOp(".")) i++;
                    else break;
                }

                if (parts.Count == 0) return;
                var module = string.Join(".", parts);
                if (i < end && tokens[i].IsName("as") && i + 1 < end && tokens[i + 1].Kind == PythonTokenKind.Name)
                {
                    _aliases[tokens[i + 1].Text] = module;
                    i += 2;
                }
                else
                {
                    _aliases[parts[0]] = parts[0];
                }

                if (i < end && tokens[i].IsOp(",")) i++;
                else if (i < end && tokens[i].IsOp(";")) return;
                else if (i < end) return;
            }
        }

        // from a.b import c [as x], d [as y] | from a.b import *  | from . import x (relative: ignored)
        private void ParseFrom(List<PythonToken> tokens, int i, int end)
        {
            var parts = new List<string>();
            while (i < end && !tokens[i].IsName("import"))
            {
                if (tokens[i].Kind == PythonTokenKind.Name) parts.Add(tokens[i].Text);
                i++;
            }

            if (i >= end || parts.Count == 0) return;
            var module = string.Join(".", parts);
            i++; // import
            while (i < end)
            {
                var t = tokens[i];
                if (t.IsOp("*"))
                {
                    _starModules.Add(module);
                    return;
                }

                if (t.IsOp("(") || t.IsOp(")") || t.IsOp(",")) { i++; continue; }
                if (t.IsOp(";")) return;
                if (t.Kind != PythonTokenKind.Name) { i++; continue; }
                var name = t.Text;
                i++;
                if (i < end && tokens[i].IsName("as") && i + 1 < end && tokens[i + 1].Kind == PythonTokenKind.Name)
                {
                    _aliases[tokens[i + 1].Text] = module + "." + name;
                    i += 2;
                }
                else
                {
                    _aliases[name] = module + "." + name;
                }
            }
        }

        /// <summary>The chain with its first name resolved through the aliases (or a star import that
        /// could supply it), then the document idioms folded onto the RhinoDoc type.</summary>
        public string Resolve(string chain)
        {
            var dot = chain.IndexOf('.');
            var head = dot < 0 ? chain : chain.Substring(0, dot);
            var tail = dot < 0 ? "" : chain.Substring(dot);
            string resolved;
            if (_aliases.TryGetValue(head, out var target))
            {
                resolved = target + tail;
            }
            else
            {
                resolved = chain;
                foreach (var module in _starModules)
                {
                    // A star import of rhinoscriptsyntax or Rhino.Input makes GetPoint()/RhinoGet reachable bare.
                    if (module == RhinoScriptSyntax && InteractiveRhinoScriptFunctions.Contains(head) || module == RhinoScriptSyntax && (head == "Command" || head == "Exit"))
                    {
                        resolved = module + "." + chain;
                        break;
                    }

                    if (module == "Rhino.Input" && head == "RhinoGet" || module == "Rhino.Input.Custom" && head.StartsWith("Get", StringComparison.Ordinal)
                        || module == "Rhino.UI" && head == "Dialogs" || module == "Rhino" && (head == "RhinoDoc" || head == "RhinoApp"))
                    {
                        resolved = module + "." + chain;
                        break;
                    }
                }
            }

            foreach (var (prefix, type) in DocumentPrefixes)
            {
                if (resolved == prefix) return type;
                if (resolved.StartsWith(prefix + ".", StringComparison.Ordinal)) return type + resolved.Substring(prefix.Length);
            }

            return resolved;
        }
    }
}
