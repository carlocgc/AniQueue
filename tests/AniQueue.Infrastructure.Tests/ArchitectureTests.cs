using System.Reflection;

namespace AniQueue.Infrastructure.Tests;

/// <summary>
/// Infrastructure is allowed to depend on EF Core and SQLite, but not on the UI.
/// Business logic drifting into components is the failure mode the brief calls
/// out; this catches the reverse drift, where rendering concerns leak down
/// into the data layer.
/// </summary>
public class ArchitectureTests
{
    public static TheoryData<string> ForbiddenInInfrastructure => new()
    {
        "Microsoft.AspNetCore.Components" // Blazor types must not reach the data layer
    };

    [Theory]
    [MemberData(nameof(ForbiddenInInfrastructure))]
    public void Infrastructure_does_not_reference_ui_assemblies(string forbiddenPrefix)
    {
        var referenced = LoadInfrastructure()
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(name => name.StartsWith(forbiddenPrefix, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.True(
            referenced.Length == 0,
            $"AniQueue.Infrastructure must not reference '{forbiddenPrefix}'. Found: {string.Join(", ", referenced)}.");
    }

    // Note: there is deliberately no "Infrastructure references Core" test here.
    // GetReferencedAssemblies only reports references the compiler actually emitted,
    // so such a test really asserts "Infrastructure happens to use a Core type" —
    // a compiler artifact, not an architectural rule. The forbidden-reference checks
    // above are sound for the same reason in reverse: an unused reference is
    // harmless, and using a forbidden type is precisely what makes it appear.

    private static Assembly LoadInfrastructure() => Assembly.Load(new AssemblyName("AniQueue.Infrastructure"));
}
