using System.Reflection;
using LinguaBridge.Application.Modules;
using LinguaBridge.Domain.Common;
using LinguaBridge.Modules.AiTutor;
using LinguaBridge.Modules.Billing;
using LinguaBridge.Modules.Content;
using LinguaBridge.Modules.Learning;

namespace LinguaBridge.UnitTests;

/// <summary>
/// Guards the module boundaries. The compiler only records references that are actually used, so these break the
/// moment someone uses a type across a forbidden boundary, not merely when a project reference is added.
/// </summary>
public class ArchitectureTests
{
    private static readonly Assembly DomainAssembly = typeof(BaseEntity).Assembly;
    private static readonly Assembly ApplicationAssembly = typeof(IModuleDescriptor).Assembly;

    public static TheoryData<Assembly> ModuleAssemblies =>
    [
        typeof(ContentModule).Assembly,
        typeof(LearningModule).Assembly,
        typeof(AiTutorModule).Assembly,
        typeof(BillingModule).Assembly,
    ];

    [Fact]
    public void Domain_does_not_depend_on_any_other_LinguaBridge_project()
    {
        Assert.Empty(LinguaBridgeReferencesOf(DomainAssembly));
    }

    [Fact]
    public void Application_depends_only_on_Domain()
    {
        Assert.All(LinguaBridgeReferencesOf(ApplicationAssembly), reference => Assert.Equal("LinguaBridge.Domain", reference));
    }

    [Theory]
    [MemberData(nameof(ModuleAssemblies))]
    public void Module_depends_only_on_Core(Assembly module)
    {
        var allowed = new[] { "LinguaBridge.Domain", "LinguaBridge.Application" };

        Assert.All(LinguaBridgeReferencesOf(module), reference => Assert.Contains(reference, allowed));
    }

    private static string[] LinguaBridgeReferencesOf(Assembly assembly) =>
        assembly.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(name => name.StartsWith("LinguaBridge.", StringComparison.Ordinal))
            .Order()
            .ToArray();
}
