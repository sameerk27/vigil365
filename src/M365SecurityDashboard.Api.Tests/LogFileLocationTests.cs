using M365SecurityDashboard.Api.Services;

namespace M365SecurityDashboard.Api.Tests;

/// <summary>
/// The GUI installer runs the service as LOCAL SERVICE, which cannot create
/// logs\ under Program Files. An unwritable log folder must not stop the service:
/// it falls back to a writable one, or logs to stdout only.
/// </summary>
public sealed class LogFileLocationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vigil365-logtest-" + Guid.NewGuid().ToString("N"));

    public LogFileLocationTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    /// <summary>A folder that cannot be created: its parent is a file. Portable, unlike ACLs.</summary>
    private string Unwritable()
    {
        var file = Path.Combine(_root, "not-a-folder");
        File.WriteAllText(file, "");
        return Path.Combine(file, "logs");
    }

    [Fact]
    public void A_writable_configured_folder_is_used_as_is()
    {
        var choice = LogFileLocation.Resolve("logs/vigil365-.json", _root, Path.Combine(_root, "fallback"));
        Assert.Equal(Path.Combine(_root, "logs", "vigil365-.json"), choice.Path);
        Assert.Null(choice.Warning);
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "logs"))); // the write probe cleans up
    }

    [Fact]
    public void An_unwritable_folder_falls_back_to_the_writable_one()
    {
        var fallback = Path.Combine(_root, "ProgramData", "Vigil365", "logs");
        var choice = LogFileLocation.Resolve(Path.Combine(Unwritable(), "vigil365-.json"), _root, fallback);
        Assert.Equal(Path.Combine(fallback, "vigil365-.json"), choice.Path);
        Assert.Contains(fallback, choice.Warning);
    }

    [Fact]
    public void With_nowhere_writable_file_logging_is_off_rather_than_the_service_failing()
    {
        var choice = LogFileLocation.Resolve(Path.Combine(Unwritable(), "vigil365-.json"), _root, Path.Combine(_root, "not-a-folder", "fallback"));
        Assert.Null(choice.Path);
        Assert.Contains("file logging is off", choice.Warning);
    }
}
