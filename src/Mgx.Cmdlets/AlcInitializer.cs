using System.Management.Automation;
using System.Reflection;
using System.Runtime.Loader;

namespace Mgx.Cmdlets;

/// <summary>
/// Assembly Load Context initializer for dependency isolation.
/// Reuses assemblies already loaded in any ALC (including Microsoft.Graph's
/// msgraph-load-context) to avoid type identity conflicts. Only loads from
/// the Dependencies folder for assemblies not found anywhere.
/// Pattern adopted from Mge project's ALC coexistence investigation.
/// </summary>
public class AlcInitializer : IModuleAssemblyInitializer, IModuleAssemblyCleanup
{
    private static readonly string DepsPath = Path.Combine(
        Path.GetDirectoryName(typeof(AlcInitializer).Assembly.Location)!,
        "Dependencies");

    public void OnImport()
    {
        AssemblyLoadContext.Default.Resolving += ResolveDependency;
    }

    private static Assembly? ResolveDependency(AssemblyLoadContext defaultAlc, AssemblyName name)
    {
        try
        {
            // If the assembly is already loaded in ANY ALC (including
            // msgraph-load-context or other module ALCs), return that instance,
            // but only if the major version is compatible. Returning an older
            // major version could cause MissingMethodException at runtime.
            foreach (var loaded in AppDomain.CurrentDomain.GetAssemblies())
            {
                var loadedName = loaded.GetName();
                if (!string.Equals(loadedName.Name, name.Name, StringComparison.OrdinalIgnoreCase))
                    continue;

                // If the requested version is unknown or the loaded version meets
                // the minimum version (same major, >= minor), reuse it to avoid type identity splits.
                // Requiring same major prevents MissingMethodException from breaking API changes.
                if (name.Version == null || loadedName.Version == null
                    || (loadedName.Version.Major == name.Version.Major
                        && loadedName.Version >= name.Version))
                {
                    return loaded;
                }
            }

            // Not loaded anywhere (or only an incompatible version):
            // load from our Dependencies folder into Default ALC
            var dllPath = Path.Combine(DepsPath, $"{name.Name}.dll");
            return File.Exists(dllPath) ? defaultAlc.LoadFromAssemblyPath(dllPath) : null;
        }
        catch (Exception ex)
        {
            // Resolver must never throw; return null to let the runtime continue
            // its normal resolution process.
            System.Diagnostics.Debug.WriteLine($"[Mgx ALC] Failed to resolve '{name.Name}': {ex.Message}");
            return null;
        }
    }

    public void OnRemove(PSModuleInfo module)
    {
        // Static-state cleanup MUST happen before the resolver is detached below.
        //
        // ResetHttpClient JIT-compiles code referencing ResilientGraphClient, whose fields
        // include Polly types. Polly.Core ships in Dependencies/ and is reachable ONLY via
        // ResolveDependency. It is also loaded lazily, so in a session where no Graph request
        // ever ran it is absent from the AppDomain entirely.
        //
        // This cleanup used to live in the mgx.psm1 OnRemove scriptblock, which PowerShell
        // invokes AFTER this callback. That ordering left the resolver already detached, so
        // ResetHttpClient threw FileNotFoundException for Polly.Core, Remove-Module failed,
        // and the module could never be unloaded. Owning the cleanup here makes the ordering
        // a property of the code rather than of PowerShell's callback sequence.
        //
        // ResetHttpClient also calls ResiliencePipelineFactory.Reset internally, so both
        // pieces of static state are released by this single call.
        try
        {
            Base.MgxCmdletBase.ResetHttpClient();
        }
        catch (Exception ex)
        {
            // Never let teardown throw: a failure here would block module removal,
            // which is the exact defect this ordering fixes.
            System.Diagnostics.Debug.WriteLine($"[Mgx ALC] Cleanup on remove failed: {ex.Message}");
        }

        AssemblyLoadContext.Default.Resolving -= ResolveDependency;

        // After the resolver: this only detaches an event handler, needs no dependency
        // resolution, and must not run before ResetHttpClient (which may trigger loads
        // that the type cache should still observe).
        Base.MgxCmdletBase.DetachAssemblyLoadHandler();
    }
}
