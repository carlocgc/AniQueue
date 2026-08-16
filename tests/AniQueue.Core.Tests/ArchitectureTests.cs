using System.Reflection;

namespace AniQueue.Core.Tests;

/// <summary>
/// Enforces the layering rule from ROADMAP.md §3 mechanically rather than by
/// convention. Core's isolation is what keeps the majority of the test suite
/// database-free and fast, so it is worth a test that fails the build the moment
/// someone adds a convenient dependency.
/// </summary>
public class ArchitectureTests
{
    /// <summary>
    /// Assembly name prefixes Core must never reference, and why.
    /// </summary>
    public static TheoryData<string> ForbiddenInCore => new()
    {
        "Microsoft.EntityFrameworkCore", // persistence belongs to Infrastructure
        "Microsoft.AspNetCore",          // hosting and UI belong to Web
        "Microsoft.Data.Sqlite"          // the storage engine is an Infrastructure detail
    };

    [Theory]
    [MemberData(nameof(ForbiddenInCore))]
    public void Core_does_not_reference_infrastructure_or_ui_assemblies(string forbiddenPrefix)
    {
        var referenced = LoadCore()
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(name => name.StartsWith(forbiddenPrefix, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.True(
            referenced.Length == 0,
            $"AniQueue.Core must not reference '{forbiddenPrefix}'. Found: {string.Join(", ", referenced)}. "
            + "See ROADMAP.md §3 — Core stays dependency-free so its tests need no fixtures.");
    }

    private static Assembly LoadCore() => Assembly.Load(new AssemblyName("AniQueue.Core"));
}
