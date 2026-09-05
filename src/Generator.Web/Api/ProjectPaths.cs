using Generator.Engine.ClaudeCli;
using Generator.Engine.Jobs;
using Generator.Engine.Storage;
using Generator.Engine.Versioning;

namespace Generator.Web.Api;

/// Jedno miejsce, ktore wie, gdzie lezy projekt. Silnikowe store'y sa per katalog
/// i tanie w tworzeniu, wiec nie trzymamy ich w DI — tworzymy na zadanie.
public class ProjectPaths(string root, Func<string, IRoundRunner> runnerFactory)
{
    public ProjectPaths(string root, string claudeExecutable)
        : this(root, id => new GenerationService(
            new ClaudeRunner(claudeExecutable),
            new ProjectStore(Path.Combine(root, id)),
            new VersionStore(Path.Combine(root, id))))
    {
    }

    public string Root => root;
    public string Dir(string id) => Path.Combine(root, id);
    public bool Exists(string id) => File.Exists(Path.Combine(Dir(id), "project.json"));

    public ProjectStore Store(string id) => new(Dir(id));
    public VersionStore Versions(string id) => new(Dir(id));
    public CommentStore Comments(string id) => new(Dir(id));
    public IRoundRunner Engine(string id) => runnerFactory(id);
}
