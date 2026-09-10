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
/// obvious workaround is not silent.
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

    /// <summary>Names that end the process or the interpreter, by qualified name.</summary>
    private static readonly IReadOnlySet<string> ExitNames = new HashSet<string>
    {
        RhinoApp + ".Exit", RhinoScriptSyntax + ".Exit", "sys.exit", "os._exit", "os.abort", "os.kill", "exit", "quit", "SystemExit",
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

        if (ExitNames.Contains(qualified) && (called || (qualified != "exit" && qualified != "quit")))
        {
            // The bare builtins exit/quit only when called: `exit` is a plausible variable name.
            throw ScriptApiDenylistViolationException.UndoOrExitMember(qualified);
        }

        var bare = qualified.StartsWith("builtins.", StringComparison.Ordinal) ? qualified.Substring("builtins.".Length) : qualified;
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

            if (t.Kind != PythonTokenKind.String || depth != 1)
            {
                continue;
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
        var parts = new List<string> { tokens[i].Text };
        var j = i + 1;
        while (j + 1 < tokens.Count && tokens[j].IsOp(".") && tokens[j + 1].Kind == PythonTokenKind.Name)
        {
            parts.Add(tokens[j + 1].Text);
            j += 2;
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
        var inner = open + 1;
        if (inner >= close || tokens[inner].Kind != PythonTokenKind.Name)
        {
            return ("", close + 1);
        }

        var (chain, afterChain) = ReadChain(tokens, inner);
        if (afterChain >= close || !tokens[afterChain].IsOp(",") || afterChain + 1 >= close)
        {
            // Not the getattr(obj, ...) shape the walk understands: treat as computed.
            throw DynamicCode("getattr(" + chain + ", <expression>)");
        }

        var arg = tokens[afterChain + 1];
        if (arg.Kind != PythonTokenKind.String)
        {
            throw DynamicCode("getattr(" + chain + ", <expression>)");
        }

        return (scope.Resolve(chain + "." + arg.Text), close + 1);
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
