namespace LocaleSmith.Core.Models;

/// <summary>
/// Identifies the Minecraft Java content family whose user-visible text is being localized.
/// </summary>
public enum MinecraftContentKind
{
    /// <summary>
    /// The package structure did not provide enough evidence for a specialist profile.
    /// </summary>
    Unknown,

    /// <summary>
    /// A loader-backed Minecraft Java mod or JAR containing Java classes.
    /// </summary>
    Mod,

    /// <summary>
    /// A Minecraft Java resource pack.
    /// </summary>
    ResourcePack,

    /// <summary>
    /// An OptiFine- or Iris-compatible shader pack.
    /// </summary>
    ShaderPack
}
