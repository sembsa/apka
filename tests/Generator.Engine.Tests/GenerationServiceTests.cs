using Generator.Engine.ClaudeCli;
using Generator.Engine.Gates;
using Generator.Engine.Jobs;
using Generator.Engine.Model;
using Generator.Engine.Storage;
using Generator.Engine.Versioning;
using Xunit;

namespace Generator.Engine.Tests;

/// Runner sterowany skryptem: kolejny przebieg bierze kolejny wpis.
/// Akcja dostaje katalog roboczy, wiec test decyduje, jakie pliki "wygeneruje" model.
/// Requests trzyma pelne zadania (nie tylko instrukcje) — bez tego usuniecie
/// "isFirstRun = false" albo ponowne uzycie zlego SessionId przy powtorce
/// przechodzi przez wszystkie testy niezauwazone.
internal class ScriptedRunner(params (string Json, Action<string> Effect)[] script) : IClaudeRunner
{
    public int Calls { get; private set; }
    public List<string> Instructions { get; } = [];
    public List<ClaudeRunRequest> Requests { get; } = [];

    public Task<ClaudeRunOutcome> RunAsync(ClaudeRunRequest request, CancellationToken ct = default)
    {
        // Straznik na wypadek regresji limitu powtorek: bez niego usuniecie
        // sprawdzenia "attempts > RunRetryLimit" w petli render-retry zamienia
        // test w NIESKONCZONA petle zamiast czerwonego wyniku (zmierzone: proces
        // testowy wisial >80s, zabity recznie). Rzucony wyjatek daje szybkie,
        // jednoznaczne FAIL zamiast zawieszenia CI.
        if (Calls >= 20)
            throw new InvalidOperationException($"ScriptedRunner: {Calls} wywolan bez konca — petla bez limitu?");

        var step = script[Math.Min(Calls, script.Length - 1)];
        Calls++;
        Instructions.Add(request.Instruction);
        Requests.Add(request);
        step.Effect(request.WorkspaceDir);
        return Task.FromResult(new ClaudeRunOutcome(true, 0, step.Json, "", TimeSpan.FromSeconds(1)));
    }
}

/// Runner, ktory nigdy nie wraca — symuluje kontrakt anulowania z ClaudeCli:
/// IClaudeRunner.RunAsync przy anulowaniu rzuca OperationCanceledException i
/// NIGDY nie zwraca ClaudeRunOutcome. Petla GenerationService musi to uszanowac
/// (nie zamieniac na Failed/Halted, tylko przepuscic wyjatek dalej).
internal class CancellingRunner : IClaudeRunner
{
    public int Calls { get; private set; }

    public Task<ClaudeRunOutcome> RunAsync(ClaudeRunRequest request, CancellationToken ct = default)
    {
        Calls++;
        return Task.FromException<ClaudeRunOutcome>(new OperationCanceledException());
    }
}

public class GenerationServiceTests : IDisposable
{
    private readonly string _project = Directory.CreateTempSubdirectory("gen-svc-").FullName;
    public void Dispose() => Directory.Delete(_project, recursive: true);

    private static string Json(string sessionId, decimal cost, string result, string denials = "[]") => $$"""
        {"is_error":false,"subtype":"success","stop_reason":"end_turn","terminal_reason":"completed",
        "session_id":"{{sessionId}}","total_cost_usd":{{cost}},"permission_denials":{{denials}},
        "result":"{{result}}"}
        """;

    /// subtype != "success" (i is_error:true) wywoluje GateVerdict.Retry z powodu
    /// pol JSON-a, niezaleznie od exit code — ScriptedRunner zawsze zwraca exit 0,
    /// wiec to jedyny sposob wywolania Retry z tego runnera.
    private static string ErrorJson(string sessionId, decimal cost) => $$"""
        {"is_error":true,"subtype":"error_during_execution","stop_reason":"end_turn","terminal_reason":"completed",
        "session_id":"{{sessionId}}","total_cost_usd":{{cost}},"permission_denials":[],
        "result":""}
        """;

    private static Action<string> WritePage(string body) => dir =>
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "index.html"), $"<html><body>{body}</body></html>");
    };

    private static Action<string> WriteBrokenPage() => dir =>
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "index.html"), "<html><body>bez domkniecia");
    };

    private (ProjectMeta meta, ProjectStore store, VersionStore versions) Setup()
    {
        var store = new ProjectStore(_project);
        var meta = store.Create(SourceKind.Idea);
        return (meta with { Status = ProjectStatus.Active }, store, new VersionStore(_project));
    }

    private static Comment C(string id, string? anchor, string text) =>
        new(id, anchor, text, "desktop", DateTimeOffset.UnixEpoch);

    [Fact]
    public async Task Udana_runda_tworzy_snapshot_i_zuzywa_runde()
    {
        var (meta, store, versions) = Setup();
        var runner = new ScriptedRunner((
            Json("s-1", 0.20m, "RAPORT c1 applied Powiekszylem telefon."),
            WritePage("""<h1 data-cmt-id="hero">x</h1>""")));

        var svc = new GenerationService(runner, store, versions);
        var outcome = await svc.RunRoundAsync(meta, [C("c1", "hero", "telefon za maly")]);

        Assert.True(outcome.Succeeded);
        Assert.NotNull(outcome.Version);
        Assert.True(File.Exists(Path.Combine(outcome.Version!.SnapshotDir, "index.html")));
        Assert.Equal(0.20m, outcome.SpentUsd);
        var applied = Assert.Single(outcome.CommentResults);
        Assert.Equal(CommentStatus.Applied, applied.Status);
        Assert.Equal("c1", applied.CommentId);

        // Trwaly stan projektu: runda faktycznie sie "zuzyla" (kontrakt 4.1/4.2).
        // Usuniecie WithRoundConsumed albo Save po sukcesie przechodzi bez tych
        // asercji niezauwazone.
        var persisted = store.Load();
        Assert.Equal(1, persisted.RoundsUsed);
        Assert.Equal(0.20m, persisted.SpentUsd);
        Assert.Single(persisted.Versions);
        Assert.Equal(ProjectStatus.Active, persisted.Status);
    }

    [Fact]
    public async Task Odmowa_uprawnien_konczy_sie_halted_bez_powtorki()
    {
        var (meta, store, versions) = Setup();
        var denials = """[{"tool_name":"Write","tool_input":{"file_path":"/tmp/index.html"}}]""";
        var runner = new ScriptedRunner((Json("s-1", 0.0127m, "brak uprawnien", denials), _ => { }));

        var outcome = await new GenerationService(runner, store, versions)
            .RunRoundAsync(meta, [C("c1", null, "x")]);

        Assert.False(outcome.Succeeded);
        Assert.Equal(FailureHandling.Halted, outcome.Failure!.Handling);
        Assert.Equal(1, runner.Calls);                 // zadnej powtorki
        Assert.Equal(0.0127m, outcome.SpentUsd);       // ale pieniadze wydane
    }

    [Fact]
    public async Task Strona_ktora_sie_nie_renderuje_jest_powtarzana_a_potem_halted()
    {
        var (meta, store, versions) = Setup();
        var runner = new ScriptedRunner((Json("s-1", 0.1m, "gotowe"), dir =>
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "index.html"), "<html><body>bez domkniecia");
        }));

        var outcome = await new GenerationService(runner, store, versions)
            .RunRoundAsync(meta, [C("c1", null, "x")]);

        Assert.False(outcome.Succeeded);
        Assert.Equal(FailureHandling.Halted, outcome.Failure!.Handling);
        Assert.Contains("renderu", outcome.Failure.Cause);
        Assert.Equal(2, runner.Calls);   // pierwsza proba + jedna powtorka
        var versionsDir = Path.Combine(_project, "versions");
        Assert.True(!Directory.Exists(versionsDir) || !Directory.EnumerateDirectories(versionsDir).Any(),
            "nieudana wersja nie moze zostawic snapshotu");

        // Kontrakt 4.1 (asymetria liczników) + 4.4 (brak zdarzenia dla klienta):
        // dwie oplacone, nieudane proby licza sie do budzetu, ale NIE do rund
        // klienta. To glowny test na blad "Failed nic nie zapisuje" — na
        // referencyjnym kodzie z briefu SpentUsd zostaje 0.
        var persisted = store.Load();
        Assert.Equal(0.2m, persisted.SpentUsd);
        Assert.Equal(0, persisted.RoundsUsed);
        Assert.Empty(persisted.Versions);
        Assert.Equal(ProjectStatus.Active, persisted.Status);
    }

    [Fact]
    public async Task Udana_runda_ktora_wyczerpuje_limit_rund_zamraza_projekt()
    {
        // Kontrakt 4.1: zamrazanie po wyczerpaniu licznika dotyczy KAZDEGO zapisu
        // stanu projektu, nie tylko sciezki awarii — take sukcesu, ktory akurat
        // zuzywa ostatnia dostepna runde.
        var (meta, store, versions) = Setup();
        meta = meta with { RoundsLimit = 1 };
        var runner = new ScriptedRunner((
            Json("s-1", 0.05m, "RAPORT c1 applied ok"),
            WritePage("""<h1 data-cmt-id="hero">x</h1>""")));

        var outcome = await new GenerationService(runner, store, versions)
            .RunRoundAsync(meta, [C("c1", "hero", "x")]);

        Assert.True(outcome.Succeeded);
        var persisted = store.Load();
        Assert.Equal(ProjectStatus.Frozen, persisted.Status);
        Assert.Equal(1, persisted.RoundsUsed);
    }

    [Fact]
    public async Task Awaria_ktora_wyczerpuje_budzet_zamraza_projekt_bez_zuzycia_rundy()
    {
        // Kontrakt 4.1: projekt zamraza PIERWSZY wyczerpany licznik — tu budzet,
        // mimo ze runda w ogole sie nie powiodla i licznik rund stoi w miejscu.
        var (meta, store, versions) = Setup();
        meta = meta with { BudgetUsd = 0.01m };
        var denials = """[{"tool_name":"Write","tool_input":{"file_path":"/tmp/index.html"}}]""";
        var runner = new ScriptedRunner((Json("s-1", 0.02m, "brak uprawnien", denials), _ => { }));

        var outcome = await new GenerationService(runner, store, versions)
            .RunRoundAsync(meta, [C("c1", null, "x")]);

        Assert.False(outcome.Succeeded);
        var persisted = store.Load();
        Assert.Equal(ProjectStatus.Frozen, persisted.Status);
        Assert.Equal(0.02m, persisted.SpentUsd);
        Assert.Equal(0, persisted.RoundsUsed);
    }

    [Fact]
    public async Task Blad_wykonania_jest_powtarzany_a_druga_proba_wznawia_ta_sama_sesje()
    {
        // Bramka RunGate.Retry (subtype != success), nie testowana wcale przez
        // testy z briefu — tam kazdy scenariusz konczy sie albo od razu Ok, albo
        // od razu Halt. Sprawdza tez, ze druga proba UZYWA TEJ SAMEJ sesji przez
        // --resume, a nie nowego GUID-a (usuniecie "isFirstRun = false" albo
        // ponowne losowanie sessionId przechodzi bez tej asercji).
        var (meta, store, versions) = Setup();
        var runner = new ScriptedRunner(
            (ErrorJson("s-1", 0.03m), _ => { }),
            (Json("s-1", 0.10m, "RAPORT c1 applied ok"), WritePage("""<h1 data-cmt-id="hero">x</h1>""")));

        var outcome = await new GenerationService(runner, store, versions)
            .RunRoundAsync(meta, [C("c1", "hero", "x")]);

        Assert.True(outcome.Succeeded);
        Assert.Equal(2, runner.Calls);
        Assert.Equal(0.13m, outcome.SpentUsd);
        Assert.True(runner.Requests[0].IsFirstRun);
        Assert.False(runner.Requests[1].IsFirstRun);
        Assert.False(string.IsNullOrEmpty(runner.Requests[0].SessionId));
        Assert.Equal(runner.Requests[0].SessionId, runner.Requests[1].SessionId);
    }

    [Fact]
    public async Task Pusty_stdout_bez_sesji_nie_wymusza_resume_przy_powtorce()
    {
        // Kontrast do testu powyzej: tam Parsed byl NIEPUSTY (subtype=error), wiec
        // sesja realnie powstala i powtorka slusznie przechodzi na --resume. Tutaj
        // pierwszy przebieg nie zwraca ZADNEGO JSON-a (np. padl, zanim model
        // cokolwiek odpowiedzial) — Parsed == null, sesja mogla nigdy nie powstac.
        // Zmierzone empirycznie na prawdziwym `claude -p --resume <nieistniejacy-id>`:
        // exit 1, pusty stdout, stderr "No conversation found with session ID: ...".
        // Bezwarunkowe przestawienie na --resume zamienialoby taka usterke w
        // gwarantowany Halted. Powtorka musi wiec nadal probowac --session-id.
        var (meta, store, versions) = Setup();
        var runner = new ScriptedRunner(
            ("", _ => { }),   // brak JSON-a na stdout -> GateVerdict.Retry, Parsed == null
            (Json("s-1", 0.1m, "RAPORT c1 applied ok"), WritePage("""<h1 data-cmt-id="hero">x</h1>""")));

        var outcome = await new GenerationService(runner, store, versions)
            .RunRoundAsync(meta, [C("c1", "hero", "x")]);

        Assert.True(outcome.Succeeded);
        Assert.Equal(2, runner.Calls);
        Assert.True(runner.Requests[0].IsFirstRun);
        Assert.True(runner.Requests[1].IsFirstRun);   // NIE --resume — sesja nigdy nie powstala
        Assert.Equal(runner.Requests[0].SessionId, runner.Requests[1].SessionId);
    }

    [Fact]
    public async Task Brakujace_kotwice_sa_naprawiane_a_potem_wersja_jest_dostarczana()
    {
        // Kontrakt 3.4: po wyczerpaniu prob wersja IDZIE do klienta z orphanedAnchors.
        var (meta, store, versions) = Setup();
        var withHero = WritePage("""<h1 data-cmt-id="hero">x</h1>""");
        var metaV1 = meta with
        {
            Versions = [new VersionMeta(1, "s-0", versions.SnapshotPath(1), 0.5m, [])],
        };
        Directory.CreateDirectory(versions.SnapshotPath(1));
        File.WriteAllText(Path.Combine(versions.SnapshotPath(1), "index.html"),
            """<html><body><h1 data-cmt-id="hero">x</h1><p data-cmt-id="cennik">y</p></body></html>""");

        var runner = new ScriptedRunner(
            (Json("s-1", 0.1m, "RAPORT c1 applied ok"), withHero),   // gubi "cennik"
            (Json("s-1", 0.1m, "RAPORT c1 applied ok"), withHero),   // nadal gubi
            (Json("s-1", 0.1m, "RAPORT c1 applied ok"), withHero));

        var outcome = await new GenerationService(runner, store, versions)
            .RunRoundAsync(metaV1, [C("c1", "hero", "x")]);

        Assert.True(outcome.Succeeded);
        Assert.Equal(["cennik"], outcome.Version!.OrphanedAnchors);
        Assert.Equal(3, runner.Calls);   // proba + AnchorRetryLimit
        Assert.Contains(runner.Instructions, i => i.Contains("Przywroc"));

        // Koszt PROB NAPRAWY kotwic tez musi zostac policzony — nie tylko koszt
        // glownego przebiegu. Bez tego klient placi za proby naprawy, ktore nigdy
        // nie trafiaja do SpentUsd. Trzy wywolania po 0.1m kazde.
        Assert.Equal(0.3m, outcome.SpentUsd);
        Assert.Equal(0.3m, store.Load().SpentUsd);

        // Kazda proba naprawy i tak wznawia sesje z metaV1 (nie sesje "pierwszego
        // przebiegu"), bo projekt juz ma wersje 1.
        Assert.All(runner.Requests, r => Assert.False(r.IsFirstRun));
        Assert.All(runner.Requests, r => Assert.Equal("s-0", r.SessionId));
    }

    [Fact]
    public async Task Proba_naprawy_kotwic_ktora_psuje_render_jest_odrzucana()
    {
        // To jest wlasnie kolizja dwoch bramek: proba naprawy bramki MIEKKIEJ
        // (kotwice) psuje bramke TWARDA (renderowalnosc). Twarda ma pierwszenstwo
        // dla KAZDEJ wersji, nie tylko dla pierwszego przebiegu — commitowany
        // snapshot musi sie renderowac, nawet jesli oznacza to porzucenie proby
        // naprawy kotwic i dostarczenie z dluzsza lista osieroconych.
        var (meta, store, versions) = Setup();
        var metaV1 = meta with
        {
            Versions = [new VersionMeta(1, "s-0", versions.SnapshotPath(1), 0.5m, [])],
        };
        Directory.CreateDirectory(versions.SnapshotPath(1));
        File.WriteAllText(Path.Combine(versions.SnapshotPath(1), "index.html"),
            """<html><body><h1 data-cmt-id="hero">x</h1><p data-cmt-id="cennik">y</p></body></html>""");

        var runner = new ScriptedRunner(
            (Json("s-1", 0.1m, "RAPORT c1 applied ok"), WritePage("""<h1 data-cmt-id="hero">x</h1>""")), // gubi cennik
            (Json("s-1", 0.1m, "RAPORT c1 applied ok"), WriteBrokenPage()));                              // "naprawa" psuje HTML

        var outcome = await new GenerationService(runner, store, versions)
            .RunRoundAsync(metaV1, [C("c1", "hero", "x")]);

        Assert.True(outcome.Succeeded);
        Assert.Equal(2, runner.Calls);
        Assert.Equal(["cennik"], outcome.Version!.OrphanedAnchors);
        // Kluczowa asercja: commitowany snapshot musi nadal przechodzic bramke
        // twarda — na referencyjnym kodzie z briefu tu ladowalby zepsuty HTML.
        Assert.True(RenderValidator.Check(outcome.Version!.SnapshotDir).Ok);
        Assert.True(File.Exists(Path.Combine(outcome.Version!.SnapshotDir, "index.html")));
        var html = File.ReadAllText(Path.Combine(outcome.Version!.SnapshotDir, "index.html"));
        Assert.Contains("</html>", html);
    }

    [Fact]
    public async Task Odmowa_uprawnien_w_trakcie_naprawy_kotwic_przywraca_backup_i_dostarcza_wersje()
    {
        // Wariant kolizji bramek inny niz "JSON OK, zepsuty render": tu sama proba
        // naprawy dostaje Halt (np. odmowa uprawnien), mimo ze plik, ktory przy
        // okazji napisala, SAM W SOBIE renderuje sie poprawnie. Efekt naprawy jest
        // CELOWO poprawnym HTML-em (nie zepsutym): to izoluje sprawdzenie werdyktu
        // bramki wykonania od sprawdzenia renderu — "repaired" musi byc false dla
        // KAZDEGO werdyktu innego niz Ok, NIEZALEZNIE od tego, czy plik akurat sie
        // renderuje. Gdyby kod patrzyl tylko na render (ignorujac Halt), petla
        // probowalaby dalej zamiast przerwac po pierwszej odmowie — objawiloby sie
        // to trzecim wywolaniem runnera zamiast oczekiwanych dwoch.
        var (meta, store, versions) = Setup();
        var metaV1 = meta with
        {
            Versions = [new VersionMeta(1, "s-0", versions.SnapshotPath(1), 0.5m, [])],
        };
        Directory.CreateDirectory(versions.SnapshotPath(1));
        File.WriteAllText(Path.Combine(versions.SnapshotPath(1), "index.html"),
            """<html><body><h1 data-cmt-id="hero">x</h1><p data-cmt-id="cennik">y</p></body></html>""");

        var denials = """[{"tool_name":"Write","tool_input":{"file_path":"/tmp/index.html"}}]""";
        var runner = new ScriptedRunner(
            (Json("s-1", 0.1m, "RAPORT c1 applied ok"), WritePage("""<h1 data-cmt-id="hero">x</h1>""")),        // gubi cennik
            (Json("s-1", 0.05m, "brak uprawnien", denials), WritePage("""<h1 data-cmt-id="hero">x</h1>""")));   // Halt, ale plik renderuje sie OK

        var outcome = await new GenerationService(runner, store, versions)
            .RunRoundAsync(metaV1, [C("c1", "hero", "x")]);

        Assert.True(outcome.Succeeded);
        // Kluczowa asercja: Halt musi przerwac petle PO PIERWSZEJ probie, mimo ze
        // plik z tej proby sam w sobie by sie wyrenderowal — bez tego (patrzenie
        // tylko na render) petla probowalaby jeszcze raz, dajac 3 wywolania.
        Assert.Equal(2, runner.Calls);
        Assert.Equal(["cennik"], outcome.Version!.OrphanedAnchors);
        Assert.Equal(0.15m, outcome.SpentUsd);   // 0.1 (glowny przebieg) + 0.05 (odrzucona proba naprawy)
        Assert.True(RenderValidator.Check(outcome.Version!.SnapshotDir).Ok);
    }

    [Fact]
    public async Task Komentarz_bez_wpisu_w_raporcie_zostaje_open()
    {
        var (meta, store, versions) = Setup();
        var runner = new ScriptedRunner((
            Json("s-1", 0.1m, "RAPORT c1 applied zrobione"),
            WritePage("ok")));

        var outcome = await new GenerationService(runner, store, versions)
            .RunRoundAsync(meta, [C("c1", null, "a"), C("c2", null, "b")]);

        Assert.Single(outcome.CommentResults);                       // tylko c1
        Assert.DoesNotContain(outcome.CommentResults, r => r.CommentId == "c2");
    }

    [Fact]
    public async Task Anulowanie_propaguje_sie_i_nie_zostawia_sladu()
    {
        // Kontrakt ClaudeCli: IClaudeRunner.RunAsync przy anulowaniu rzuca
        // OperationCanceledException i NIGDY nie zwraca ClaudeRunOutcome. Petla
        // GenerationService nie moze tego zlapac i zamienic na Failed/Halted —
        // musi przepuscic wyjatek. Sprawdza tez, ze nic po drodze nie zdazylo
        // zmienic trwalego stanu projektu.
        var (meta, store, versions) = Setup();
        var runner = new CancellingRunner();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new GenerationService(runner, store, versions).RunRoundAsync(meta, [C("c1", null, "x")]));

        Assert.Equal(1, runner.Calls);
        var persisted = store.Load();
        Assert.Equal(0, persisted.RoundsUsed);
        Assert.Equal(0m, persisted.SpentUsd);
        Assert.Empty(persisted.Versions);
    }

    [Fact]
    public void Raport_odrzucony_jest_czytany_wraz_z_uzasadnieniem()
    {
        var results = GenerationService.ParseReport(
            "RAPORT c1 rejected Nie mam zdjecia, ktorym moglbym to zastapic.",
            [C("c1", null, "zmien zdjecie")]);

        var r = Assert.Single(results);
        Assert.Equal(CommentStatus.Rejected, r.Status);
        Assert.Contains("zdjecia", r.Note);
    }

    [Fact]
    public void Raport_z_nieznanym_id_jest_pomijany()
    {
        // Kontrakt 3.3: status wylacznie z raportu KLUCZOWANEGO NASZYM Comment.Id.
        // Model potrafi wpisac dowolny tekst — id spoza listy uwag tej rundy
        // nie moze wyciekac do wyniku.
        var results = GenerationService.ParseReport(
            "RAPORT c9 applied nieznany komentarz\nRAPORT c1 applied znany",
            [C("c1", null, "x")]);

        var r = Assert.Single(results);
        Assert.Equal("c1", r.CommentId);
    }
}
