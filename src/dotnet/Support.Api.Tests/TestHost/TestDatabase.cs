namespace Support.Api.Tests.TestHost;

/// <summary>
/// A temporary SQLite database file that outlives one test host, so a test can stop a host and start a new one on the
/// same file (a restart). Deleted when disposed.
/// </summary>
internal sealed class TestDatabase : IDisposable
{
    private readonly string directory = NewDirectory();

    public string Path => System.IO.Path.Combine(directory, "support.db");

    public IReadOnlyDictionary<string, string?> Settings => new Dictionary<string, string?> { ["Storage:Path"] = Path };

    public static string NewDirectory()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "maf-support-lab-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    public static void Delete(string? path)
    {
        if (path is not null && Directory.Exists(path))
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (IOException)
            {
                // A host that is still shutting down may hold the file briefly; the OS temp folder is cleaned eventually.
            }
        }
    }

    public void Dispose() => Delete(directory);
}
