namespace M365SecurityDashboard.Api.Services;

/// <summary>
/// Where the rolling JSON log file goes. The configured path (default
/// logs\ beside the app) is used when the process can write there. A Windows
/// service running as LOCAL SERVICE cannot create a folder under Program Files,
/// so it falls back to %ProgramData%\Vigil365\logs; if that is not writable
/// either, file logging is off and stdout still gets every line, rather than the
/// service failing to start.
/// </summary>
public static class LogFileLocation
{
    /// <param name="Path">The log file path pattern to use, or null for no file log.</param>
    /// <param name="Warning">Why the configured location was not used, to log once started.</param>
    public sealed record Choice(string? Path, string? Warning);

    public static Choice Resolve(string configuredPath, string baseDirectory, string? fallbackDirectory)
    {
        var preferred = System.IO.Path.GetFullPath(configuredPath, baseDirectory);
        var preferredDir = System.IO.Path.GetDirectoryName(preferred)!;
        if (CanWriteTo(preferredDir)) return new Choice(preferred, null);

        if (fallbackDirectory is not null && CanWriteTo(fallbackDirectory))
            return new Choice(System.IO.Path.Combine(fallbackDirectory, System.IO.Path.GetFileName(preferred)),
                $"Cannot write log files to {preferredDir}; writing them to {fallbackDirectory} instead. Set Logging:File:Path to a folder this service can write to.");

        return new Choice(null,
            $"Cannot write log files to {preferredDir}{(fallbackDirectory is null ? "" : $" or {fallbackDirectory}")}; file logging is off (stdout only). Set Logging:File:Path to a folder this service can write to.");
    }

    /// <summary>%ProgramData%\Vigil365\logs on Windows, where the installer puts them; none elsewhere.</summary>
    public static string? DefaultFallbackDirectory()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return string.IsNullOrEmpty(programData) ? null : System.IO.Path.Combine(programData, "Vigil365", "logs");
    }

    /// <summary>
    /// Creates the folder and a throwaway file in it. Creating the folder is not
    /// enough: an existing folder (say from an earlier run as LocalSystem) can
    /// still refuse the file, and the file sink then fails silently.
    /// </summary>
    private static bool CanWriteTo(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = System.IO.Path.Combine(directory, $".write-test-{Guid.NewGuid():N}");
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or NotSupportedException or System.Security.SecurityException)
        {
            return false;
        }
    }
}
