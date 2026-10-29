namespace LinguaBridge.Application.Modules;

/// <summary>Registered by each module so the host can tell which modules are loaded and which schemas they own.</summary>
public interface IModuleDescriptor
{
    string Name { get; }

    /// <summary>Database schemas owned exclusively by this module.</summary>
    IReadOnlyList<string> Schemas { get; }
}

public sealed record ModuleDescriptor(string Name, IReadOnlyList<string> Schemas) : IModuleDescriptor;
