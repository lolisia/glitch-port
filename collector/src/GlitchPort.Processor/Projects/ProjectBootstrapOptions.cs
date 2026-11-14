namespace GlitchPort.Processor.Projects;

/// <summary>
/// Projects and public keys created when the Processor starts, bound from the
/// <c>Processor:Bootstrap</c> configuration section.
/// </summary>
public sealed class ProjectBootstrapOptions
{
    /// <summary>Projects to create when they are missing.</summary>
    public ProjectBootstrapProject[] Projects { get; set; } = [];
}

/// <summary>A project created at startup together with its public keys.</summary>
public sealed class ProjectBootstrapProject
{
    /// <summary>Id of the project. An existing row with this id is kept.</summary>
    public long Id { get; set; }

    /// <summary>Display name of the project.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Unique URL segment of the project.</summary>
    public string Slug { get; set; } = string.Empty;

    /// <summary>Public keys to create when they are missing.</summary>
    public string[] PublicKeys { get; set; } = [];
}
