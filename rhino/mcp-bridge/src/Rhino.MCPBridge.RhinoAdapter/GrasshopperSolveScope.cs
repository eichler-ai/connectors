using System.Collections.Generic;
using Grasshopper.Kernel;
using Rhino.MCPBridge.Core.Execution;

namespace Rhino.MCPBridge.RhinoAdapter;

/// <summary>
/// Collects Grasshopper solution events on one <see cref="GH_Document"/> for a run's duration (PRD §10):
/// subscribes to <c>SolutionStart</c>/<c>SolutionEnd</c> on construction and unsubscribes on Dispose. After
/// the run <see cref="BuildReport"/> walks the definition's objects and reports every one that carried a
/// runtime message or ended in a phase other than <c>Computed</c>. Grasshopper-typed, so it is only
/// instantiated once <see cref="GrasshopperWatcher.GrasshopperLoaded"/> is true.
/// </summary>
internal sealed class GrasshopperSolveScope : IGrasshopperSolveScope
{
    private readonly GH_Document _doc;
    private readonly List<GrasshopperSolution> _solutions = new();
    private DateTime _startUtc;
    private int _startDepth;
    private bool _solving;

    public GrasshopperSolveScope(GH_Document doc)
    {
        _doc = doc;
        _doc.SolutionStart += OnSolutionStart;
        _doc.SolutionEnd += OnSolutionEnd;
    }

    private void OnSolutionStart(object sender, GH_SolutionEventArgs e)
    {
        _startUtc = DateTime.UtcNow;
        _startDepth = _doc.SolutionDepth;
        _solving = true;
    }

    private void OnSolutionEnd(object sender, GH_SolutionEventArgs e)
    {
        if (!_solving)
        {
            return;
        }

        _solving = false;
        _solutions.Add(new GrasshopperSolution
        {
            StartedAt = _startUtc.ToString("o"),
            DurationMs = (DateTime.UtcNow - _startUtc).TotalMilliseconds,
            State = _doc.SolutionState.ToString(),
            Depth = _startDepth,
        });
    }

    public GrasshopperReport? BuildReport()
    {
        if (_solutions.Count == 0)
        {
            return null; // no solution ended during the run -> no grasshopper field
        }

        var components = new List<GrasshopperComponentReport>();
        foreach (var obj in _doc.Objects)
        {
            if (obj is not IGH_ActiveObject active)
            {
                continue;
            }

            var messages = CollectMessages(active);
            var phase = active.Phase.ToString();
            // Only the objects worth an agent's attention: a message, or a non-Computed end (PRD §10).
            if (messages.Count == 0 && phase == "Computed")
            {
                continue;
            }

            components.Add(new GrasshopperComponentReport
            {
                Guid = obj.InstanceGuid,
                Nickname = obj.NickName ?? "",
                Type = obj.Name ?? obj.GetType().Name,
                Phase = phase,
                ProcessorMs = active.ProcessorTime.TotalMilliseconds,
                Messages = messages,
            });
        }

        return new GrasshopperReport { Solutions = _solutions, Components = components };
    }

    private static List<GrasshopperMessage> CollectMessages(IGH_ActiveObject active)
    {
        var list = new List<GrasshopperMessage>();
        foreach (var (level, severity) in Levels)
        {
            foreach (var text in active.RuntimeMessages(level))
            {
                list.Add(new GrasshopperMessage { Severity = severity, Text = text });
            }
        }

        return list;
    }

    private static readonly (GH_RuntimeMessageLevel Level, string Severity)[] Levels =
    {
        (GH_RuntimeMessageLevel.Error, "error"),
        (GH_RuntimeMessageLevel.Warning, "warning"),
        (GH_RuntimeMessageLevel.Remark, "remark"),
    };

    public void Dispose()
    {
        _doc.SolutionStart -= OnSolutionStart;
        _doc.SolutionEnd -= OnSolutionEnd;
    }
}
