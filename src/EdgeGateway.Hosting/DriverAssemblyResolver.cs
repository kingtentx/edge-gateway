using System.Reflection;
using System.Runtime.Loader;

namespace EdgeGateway.Hosting;

/// <summary>
/// Resolves the legacy protocol assemblies staged in the single drivers directory.
/// Only named managed assemblies are resolved here; uploaded plugin dependency isolation
/// continues to use its own AssemblyLoadContext and manifest.
/// </summary>
internal static class DriverAssemblyResolver
{
    private static readonly object Sync = new();
    private static readonly HashSet<string> Directories = new(OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private static bool _configured;

    public static void Configure(string directory)
    {
        var fullPath = Path.GetFullPath(directory);
        Directory.CreateDirectory(fullPath);
        lock (Sync)
        {
            Directories.Add(fullPath);
            if (_configured) return;
            AssemblyLoadContext.Default.Resolving += Resolve;
            _configured = true;
        }
    }

    private static Assembly? Resolve(AssemblyLoadContext context, AssemblyName name)
    {
        if (string.IsNullOrWhiteSpace(name.Name)) return null;
        string[] directories;
        lock (Sync) directories = Directories.ToArray();
        foreach (var directory in directories)
        {
            var candidate = Path.Combine(directory, name.Name + ".dll");
            if (!File.Exists(candidate)) continue;
            try { return context.LoadFromAssemblyPath(candidate); }
            catch (FileLoadException)
            {
                var alreadyLoaded = context.Assemblies.FirstOrDefault(assembly =>
                    string.Equals(assembly.GetName().Name, name.Name, StringComparison.OrdinalIgnoreCase));
                if (alreadyLoaded is not null) return alreadyLoaded;
                throw;
            }
        }
        return null;
    }
}
