using System.Reflection;

namespace Mgx.Engine;

/// <summary>
/// SDK version identifier injected into the SdkVersion HTTP header on all Graph requests.
/// Enables correlation of Mgx traffic in Microsoft's Graph API telemetry.
/// </summary>
internal static class MgxSdkVersion
{
    /// <summary>Header value, e.g. "mgx/1.0.4".</summary>
    internal static readonly string Value = $"mgx/{Read()}";

    /// <summary>
    /// The assembly version, taken from <Version> in Directory.Build.props. The informational
    /// version is the one that carries the full three-part number; SourceLink appends "+<commit>"
    /// to it, which is stripped. AssemblyVersion is the fallback because it is always present.
    /// </summary>
    private static string Read()
    {
        var assembly = typeof(MgxSdkVersion).Assembly;

        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plus = informational.IndexOf('+');
            return plus >= 0 ? informational[..plus] : informational;
        }

        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}
