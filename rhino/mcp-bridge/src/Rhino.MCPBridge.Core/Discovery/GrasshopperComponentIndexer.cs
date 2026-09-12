using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Rhino.MCPBridge.Core.Discovery;

/// <summary>One Grasshopper component type from the ComponentServer catalog, read by the adapter (which alone
/// names Grasshopper types) and turned into a discovery entry by <see cref="GrasshopperComponentIndexer"/>.</summary>
public sealed class GrasshopperCatalogEntry
{
    public string Guid { get; }
    public string Name { get; }
    public string Nickname { get; }
    public string Category { get; }
    public string SubCategory { get; }
    public string? Description { get; }
    public IReadOnlyList<GrasshopperPort> Inputs { get; }
    public IReadOnlyList<GrasshopperPort> Outputs { get; }

    public GrasshopperCatalogEntry(string guid, string name, string nickname, string category, string subCategory,
        string? description, IReadOnlyList<GrasshopperPort> inputs, IReadOnlyList<GrasshopperPort> outputs)
    {
        Guid = guid;
        Name = name;
        Nickname = nickname;
        Category = category;
        SubCategory = subCategory;
        Description = description;
        Inputs = inputs;
        Outputs = outputs;
    }
}

/// <summary>One input or output port of a component (name, its Grasshopper data type, and a description).</summary>
public sealed class GrasshopperPort
{
    public string Name { get; }
    public string Type { get; }
    public string? Description { get; }

    public GrasshopperPort(string name, string type, string? description)
    {
        Name = name;
        Type = type;
        Description = description;
    }
}

/// <summary>
/// Indexes the installed Grasshopper component catalog (PRD §09 kind=grasshopper) into the same
/// <see cref="DiscoveryCache"/> as the reflected .NET assemblies, so an agent searching "circle" finds the
/// Grasshopper Circle component beside <c>Rhino.Geometry.Circle</c>. Like <see cref="RhinoScriptIndexer"/>,
/// this synthesises <see cref="ReflectedType"/>/<see cref="ReflectedMember"/> by hand rather than reflecting
/// .NET members — a component has no .NET signature, only a category, description, and input/output ports.
///
/// <para>Components are grouped by Category into one synthetic type per category under the single
/// <c>Grasshopper</c> namespace, so <c>list_functions</c> browses Grasshopper → Curve → Circle. member_id is
/// the dotted <c>Grasshopper.&lt;Category&gt;.&lt;Name&gt;</c>, which describe_function resolves by either
/// <c>member</c> or <c>member_id</c>. The GH-typed reading of the catalog lives in the adapter; this class is
/// pure so it is unit-testable over hand-built entries.</para>
/// </summary>
public static class GrasshopperComponentIndexer
{
    /// <summary>The single synthetic namespace all Grasshopper components live under.</summary>
    public const string Namespace = "Grasshopper";

    /// <summary>The assemblies.file_path sentinel + SyncSource id for this source.</summary>
    public const string SourceId = "grasshopper:components";

    /// <summary>The member Kind string these entries carry (passed through verbatim to the agent).</summary>
    public const string ComponentKind = "GrasshopperComponent";

    /// <summary>
    /// Builds the discovery types for a catalog. Returns null when the catalog is empty. The content hash is
    /// over the sorted component identities, so it is stable across launches and only changes when the
    /// installed component set does (driving a re-index and a semsearch fingerprint change).
    /// </summary>
    public static (string ContentHash, IReadOnlyList<ReflectedType> Types)? Build(IReadOnlyList<GrasshopperCatalogEntry> components)
    {
        if (components is null || components.Count == 0)
        {
            return null;
        }

        // Group by category; one synthetic type per category. Categories sorted for a stable index.
        var byCategory = new SortedDictionary<string, List<ReflectedMember>>(StringComparer.Ordinal);
        var hashInput = new StringBuilder();
        foreach (var entry in components.OrderBy(c => c.Guid, StringComparer.Ordinal))
        {
            hashInput.Append(entry.Guid).Append('|').Append(entry.Name).Append('|').Append(entry.Category).Append('\n');

            var category = string.IsNullOrWhiteSpace(entry.Category) ? "Uncategorized" : entry.Category.Trim();
            if (!byCategory.TryGetValue(category, out var members))
            {
                members = new List<ReflectedMember>();
                byCategory[category] = members;
            }

            var memberId = $"{Namespace}.{category}.{entry.Name}";
            // A duplicate name within a category (rare; two plug-ins) keeps the first so member_id stays usable.
            if (members.Any(m => string.Equals(m.MemberId, memberId, StringComparison.Ordinal)))
            {
                continue;
            }

            members.Add(BuildMember(category, entry));
        }

        var types = byCategory
            .Where(kv => kv.Value.Count > 0)
            .Select(kv => new ReflectedType
            {
                Namespace = Namespace,
                Name = kv.Key,
                FullName = $"{Namespace}.{kv.Key}",
                MemberId = $"{Namespace}.{kv.Key}",
                Documented = true,
                BaseFullName = null,
                Members = kv.Value,
            })
            .ToList();

        if (types.Count == 0)
        {
            return null;
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(hashInput.ToString())));
        return (hash, types);
    }

    private static ReflectedMember BuildMember(string category, GrasshopperCatalogEntry entry)
    {
        var inputs = string.Join(", ", entry.Inputs.Select(PortText));
        var outputs = string.Join(", ", entry.Outputs.Select(PortText));
        var subtitle = string.IsNullOrWhiteSpace(entry.SubCategory) ? category : $"{category} › {entry.SubCategory}";
        var nick = string.IsNullOrWhiteSpace(entry.Nickname) || entry.Nickname == entry.Name ? "" : $" ({entry.Nickname})";

        // A Grasshopper component has no C# call. The "signature" is a readable summary of the component and
        // its ports; the "python_call" is a real line that places the component on a bound definition by guid.
        var signature = $"{entry.Name}{nick}  [{subtitle}]  in: ({inputs})  out: ({outputs})";
        var pythonCall = $"ghdoc.AddObject(Grasshopper.Instances.ComponentServer.EmitObject(System.Guid(\"{entry.Guid}\")), False)";

        return new ReflectedMember
        {
            Kind = ComponentKind,
            Name = entry.Name,
            Signature = signature,
            PythonCall = pythonCall,
            Summary = DiscoveryReflector.Truncate(string.IsNullOrWhiteSpace(entry.Description) ? null : entry.Description),
            MemberId = $"{Namespace}.{category}.{entry.Name}",
            Returns = entry.Outputs.Count == 0 ? null : $"outputs: {outputs}",
            Parameters = entry.Inputs.Select(p => new ReflectedParameter
            {
                Name = p.Name,
                Type = p.Type,
                Description = string.IsNullOrWhiteSpace(p.Description) ? null : p.Description,
            }).ToList(),
        };
    }

    private static string PortText(GrasshopperPort port) =>
        string.IsNullOrWhiteSpace(port.Type) ? port.Name : $"{port.Name}:{port.Type}";
}
