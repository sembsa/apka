using Generator.Engine.Model;

namespace Generator.Engine.Versioning;

public class VersionStore(string projectDir)
{
    public string WorkDir => Path.Combine(projectDir, "work");

    public string SnapshotPath(int number) =>
        Path.Combine(projectDir, "versions", number.ToString("D3"));

    public VersionMeta Commit(int number, string sessionId, decimal costUsd, IReadOnlyList<string> orphanedAnchors)
    {
        var target = SnapshotPath(number);
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        CopyDirectory(WorkDir, target);

        return new VersionMeta(number, sessionId, target, costUsd, orphanedAnchors);
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);

        foreach (var file in Directory.EnumerateFiles(from))
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: true);

        foreach (var dir in Directory.EnumerateDirectories(from))
            CopyDirectory(dir, Path.Combine(to, Path.GetFileName(dir)));
    }
}
