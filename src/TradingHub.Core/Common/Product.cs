namespace TradingHub.Common;

/// <summary>El nombre de la app y sus carpetas.</summary>
public static class Product
{
    public const string Name = "Trading Hub";

    /// <summary>La carpeta de la app dentro de <paramref name="baseDir"/>.</summary>
    public static string Folder(string baseDir) => Path.Combine(baseDir, Name);

    /// <summary>The unchanged Trading Hub application-data location before any explicit opt-in.</summary>
    public static string DefaultAppData => StorageLocations.DefaultAppRoot;

    /// <summary>The unchanged Trading Hub scripts location before any explicit opt-in.</summary>
    public static string DefaultDocuments => StorageLocations.DefaultScriptsRoot;

    /// <summary>The process's effective app location; explicit environment overrides win.</summary>
    public static string AppData => StorageLocations.Startup.AppRoot;

    /// <summary>The process's effective scripts location. Existing directory names are preserved.</summary>
    public static string Documents => StorageLocations.Startup.ScriptsRoot;
}
