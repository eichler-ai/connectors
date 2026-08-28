using System;
using Autodesk.Revit.UI;

namespace MCPBridge.RevitAdapter;

/// <summary>Real implementation wrapping Autodesk.Revit.UI.UIApplication. Not unit-tested (see RevitTransactionAdapter).</summary>
public sealed class RevitUiApplicationAdapter : IUiApplicationAdapter, IRawUiApplicationSource, IDocumentCreationSource
{
    private readonly UIApplication _uiApplication;

    public RevitUiApplicationAdapter(UIApplication uiApplication)
    {
        _uiApplication = uiApplication;
        ActiveUiDocument = uiApplication.ActiveUIDocument is { } doc
            ? new RevitUiDocumentAdapter(doc)
            : null;
    }

    public IUiDocumentAdapter? ActiveUiDocument { get; }

    /// <summary>The real UIApplication this adapter wraps (PRD §14) -- see IDocumentAdapter.RawDocument.</summary>
    public UIApplication RawUiApplication => _uiApplication;

    /// <summary>
    /// See <see cref="IDocumentCreationSource"/>. `Application` is an ordinary property on the real
    /// UIApplication (PRD §14, "Application-level access needed no new plumbing") -- nothing new is
    /// exposed here that a script could not already reach; what this adds is that the returned
    /// document gets wrapped in an IDocumentAdapter the executor can open a managed transaction on.
    /// </summary>
    public IDocumentAdapter CreateProjectDocument(string? templatePath)
    {
        var application = _uiApplication.Application;
        var resolvedTemplatePath = string.IsNullOrEmpty(templatePath)
            ? application.DefaultProjectTemplate
            : templatePath;

        if (string.IsNullOrEmpty(resolvedTemplatePath))
        {
            // Fail with the actual condition rather than letting Revit throw on an empty path (PRD
            // §01): DefaultProjectTemplate is per-install and genuinely can be blank.
            throw new InvalidOperationException(
                "No project template was given and this Revit install's Application.DefaultProjectTemplate " +
                "is empty, so there is nothing to create a project document from. Pass an explicit " +
                "template path to CreateProjectDocument.");
        }

        return new RevitDocumentAdapter(application.NewProjectDocument(resolvedTemplatePath));
    }

    /// <summary>See <see cref="IDocumentCreationSource"/>.</summary>
    public IDocumentAdapter CreateFamilyDocument(string templatePath)
    {
        if (string.IsNullOrEmpty(templatePath))
        {
            throw new ArgumentException(
                "A family template path is required -- unlike project documents there is no " +
                "install-wide default family template to fall back on.",
                nameof(templatePath));
        }

        return new RevitDocumentAdapter(_uiApplication.Application.NewFamilyDocument(templatePath));
    }
}
