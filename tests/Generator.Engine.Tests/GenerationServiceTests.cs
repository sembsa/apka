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

/// Punkt 1 raportu: runner ktory NAJPIERW zwraca zwykly (kosztowny) wynik, i
/// DOPIERO PRZY KOLEJNYM wywolaniu rzuca OperationCanceledException — tak jak
/// realny scenariusz z recenzji ("po przebiegu kosztujacym $0.37 i anulowaniu
/// powtorki"). Runner rzucajacy juz przy PIERWSZYM wywolaniu (jak stary
/// CancellingRunner uzywany ponizej) czyni asercje "SpentUsd == 0" prawdziwa
/// TRYWIALNIE — zaden koszt nigdy nie zdazyl powstac, wiec test nie sprawdza w
/// ogole tego, co mial sprawdzac.
internal class RetryThenCancellingRunner(string firstJson) : IClaudeRunner
{
    public int Calls { get; private set; }

    public Task<ClaudeRunOutcome> RunAsync(ClaudeRunRequest request, CancellationToken ct = default)
    {
        Calls++;
        if (Calls == 1)
            return Task.FromResult(new ClaudeRunOutcome(true, 0, firstJson, "", TimeSpan.FromSeconds(1)));

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

    /// Punkt 8 raportu: RunRoundAsync ignoruje wartosc parametru "meta" i zawsze
    /// czyta swiezy stan przez projects.Load(). Kazdy test, ktory chce ustawic
    /// stan inny niz swiezo utworzony (Status, RoundsLimit, BudgetUsd, Versions),
    /// MUSI go jawnie zapisac przez store.Save() — inaczej silnik go po prostu nie
    /// zobaczy (to jest wlasnie dowod, ze fix dziala: "meta" przekazywane dalej
    /// bez zapisu przestaje miec jakikolwiek wplyw).
    private (ProjectMeta meta, ProjectStore store, VersionStore versions) Setup()
    {
        var store = new ProjectStore(_project);
        var meta = store.Create(SourceKind.Idea) with { Status = ProjectStatus.Active };
        store.Save(meta);
        return (meta, store, new VersionStore(_project));
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
        store.Save(meta);
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
        store.Save(meta);
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
        store.Save(metaV1);   // punkt 8: RunRoundAsync czyta z dysku, nie z argumentu
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
        store.Save(metaV1);   // punkt 8: RunRoundAsync czyta z dysku, nie z argumentu
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
        store.Save(metaV1);   // punkt 8: RunRoundAsync czyta z dysku, nie z argumentu
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
    public async Task Naprawa_kotwic_ktora_sie_udaje_daje_puste_osierocone_i_snapshot_z_kotwica()
    {
        // Punkt 2 raportu: wszystkie trzy testy petli kotwic powyzej uzywaja
        // napraw, ktore PADAJA albo NIC NIE ZMIENIAJA — zaden nie sprawdza jedynej
        // nieobserwowalnej galezi: udanej naprawy. Zamiana argumentow w wywolaniu
        // DirectoryOps.SafeReplace, ktore odswieza kopie zapasowa po udanej
        // probie ("ten stan tez jest dobry — odswiez kopie"), daje 109/109
        // zielonych na kodzie SPRZED tego testu (potwierdzone empirycznie) — a w
        // produkcji oznaczaloby to, ze klient dostaje wersje BEZ kotwicy i PUSTA
        // liste osieroconych, czyli §3.4 "nigdy po cichu" zlamane w ciszy.
        var (meta, store, versions) = Setup();
        var metaV1 = meta with
        {
            Versions = [new VersionMeta(1, "s-0", versions.SnapshotPath(1), 0.5m, [])],
        };
        store.Save(metaV1);   // punkt 8: RunRoundAsync czyta z dysku, nie z argumentu
        Directory.CreateDirectory(versions.SnapshotPath(1));
        File.WriteAllText(Path.Combine(versions.SnapshotPath(1), "index.html"),
            """<html><body><h1 data-cmt-id="hero">x</h1><p data-cmt-id="cennik">y</p></body></html>""");

        var runner = new ScriptedRunner(
            (Json("s-1", 0.1m, "RAPORT c1 applied ok"), WritePage("""<h1 data-cmt-id="hero">x</h1>""")),   // gubi cennik
            (Json("s-1", 0.1m, "RAPORT c1 applied ok"),
                WritePage("""<h1 data-cmt-id="hero">x</h1><p data-cmt-id="cennik">y</p>""")));              // naprawia naprawde

        var outcome = await new GenerationService(runner, store, versions)
            .RunRoundAsync(metaV1, [C("c1", "hero", "x")]);

        Assert.True(outcome.Succeeded);
        Assert.Equal(2, runner.Calls);   // proba + JEDNA udana naprawa (petla wychodzi od razu)
        Assert.Empty(outcome.Version!.OrphanedAnchors);

        // Nie wystarczy sprawdzic pustej listy osieroconych (ta akurat jest
        // liczona PRZED odswiezeniem kopii zapasowej i przetrwalaby nawet
        // zamiane argumentow) — kluczowe jest to, co NAPRAWDE trafilo do
        // snapshotu klienta.
        var html = File.ReadAllText(Path.Combine(outcome.Version!.SnapshotDir, "index.html"));
        Assert.Contains("data-cmt-id=\"cennik\"", html);
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
    public async Task Anulowanie_propaguje_sie_bez_zuzycia_rundy_ani_wersji()
    {
        // Kontrakt ClaudeCli: IClaudeRunner.RunAsync przy anulowaniu rzuca
        // OperationCanceledException i NIGDY nie zwraca ClaudeRunOutcome. Petla
        // GenerationService nie moze tego zlapac i zamienic na Failed/Halted —
        // musi przepuscic wyjatek. Runner rzuca juz przy PIERWSZYM wywolaniu, wiec
        // zaden koszt jeszcze nie powstal — to jest test na "anulowanie NIE
        // wymysla wersji/kosztu z powietrza", NIE na "koszt przetrwa anulowanie"
        // (to sprawdza osobny test ponizej, ktory naprawia dokladnie ten przeoczony
        // przypadek — patrz punkt 1 raportu).
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
    public async Task Anulowanie_po_oplaconej_probie_nie_gubi_wydanych_pieniedzy()
    {
        // Punkt 1 raportu (KRYTYCZNE): "spent" trafial do trwalego stanu tylko w
        // Failed() i na sciezce sukcesu. Anulowanie omijalo OBIE, bo
        // OperationCanceledException lecial przez cala metode BEZ "finally" — po
        // przebiegu kosztujacym realne pieniadze i anulowaniu KOLEJNEJ proby,
        // project.json pokazywal SpentUsd: 0. Runner: pierwsza proba wraca z
        // bledem wykonania (GateVerdict.Retry — ale Parsed niesie koszt, zgodnie z
        // kontraktem "pieniadze wydane niezaleznie od werdyktu"), DOPIERO DRUGA
        // rzuca OperationCanceledException, tak jak kosztowna powtorka, ktora pada
        // w trakcie limitu czasu kolejki.
        var (meta, store, versions) = Setup();
        var runner = new RetryThenCancellingRunner(ErrorJson("s-1", 0.37m));

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new GenerationService(runner, store, versions).RunRoundAsync(meta, [C("c1", null, "x")]));

        Assert.Equal(2, runner.Calls);   // pierwsza (oplacona) proba + druga, ktora anuluje
        var persisted = store.Load();

        // Kluczowa asercja: koszt PIERWSZEJ, zakonczonej proby MUSI przetrwac
        // anulowanie DRUGIEJ — na kodzie z briefu (bez "finally") tu bylo 0.
        Assert.Equal(0.37m, persisted.SpentUsd);
        Assert.Equal(0, persisted.RoundsUsed);   // anulowanie/awaria nie zuzywa rundy klienta
        Assert.Empty(persisted.Versions);        // i nie tworzy zadnej wersji
    }

    [Fact]
    public async Task RunRoundAsync_ignoruje_nieswiezy_argument_meta_i_uzywa_stanu_z_dysku()
    {
        // Punkt 8 raportu: RunRoundAsync budowal nowy stan z ARGUMENTU "meta", nie z
        // biezacej zawartosci pliku — kolejka trzymajaca "meta" przez cala dlugosc
        // przebiegu (realnie kilka minut) moglaby cicho zgubic wersje albo cofnac
        // SpentUsd, gdyby ktos w tym czasie zapisal projekt. Tu przekazujemy
        // CELOWO nieaktualny obiekt "meta" (RoundsLimit=1, jakby zaraz mial zamrozic
        // projekt) — a na dysku lezy inny, swiezy stan (RoundsLimit=15, domyslny).
        // Silnik MUSI zachowac sie zgodnie z dyskiem, nie z argumentem.
        var (meta, store, versions) = Setup();
        var staleMeta = meta with { RoundsLimit = 1, BudgetUsd = 0.01m };   // NIGDY nie zapisany na dysk

        var runner = new ScriptedRunner((
            Json("s-1", 0.05m, "RAPORT c1 applied ok"),
            WritePage("""<h1 data-cmt-id="hero">x</h1>""")));

        var outcome = await new GenerationService(runner, store, versions)
            .RunRoundAsync(staleMeta, [C("c1", "hero", "x")]);

        Assert.True(outcome.Succeeded);
        var persisted = store.Load();
        // Gdyby silnik uzyl przekazanego "staleMeta" (RoundsLimit=1, BudgetUsd=0.01),
        // projekt zamrozilby sie natychmiast. Prawdziwy stan z dysku (domyslne
        // RoundsLimit=15, BudgetUsd=8.00) nie ma powodu zamrazac po jednej rundzie
        // za $0.05.
        Assert.Equal(ProjectStatus.Active, persisted.Status);
        Assert.Equal(ProjectStore.DefaultRoundsLimit, persisted.RoundsLimit);
    }

    [Fact]
    public async Task Zamrozony_projekt_odrzuca_runde_bez_wywolania_modelu_i_bez_dotykania_workdir()
    {
        // Punkt 7 raportu: Freeze() dziala dopiero PO przebiegu, wiec bez straznicy
        // na wejsciu RunRoundAsync na projekcie Frozen uruchomiloby claude -p (budzet
        // POZA limitem), a udana sciezka ustawilaby na koncu Status = Active — czyli
        // PO CICHU odmrozilaby projekt. Sprawdzamy tez, ze WorkDir NIE jest ruszany —
        // straznica musi zwracac wprost, a nie przechodzic przez Failed()/Restore
        // (to zepsuloby WorkDir projektu, ktory nigdy nie poprosil o runde).
        var (meta, store, versions) = Setup();
        File.WriteAllText(Path.Combine(versions.WorkDir, "index.html"), "biezaca zamrozona strona");
        store.Save(meta with { Status = ProjectStatus.Frozen });

        var runner = new ScriptedRunner((Json("s-1", 0.5m, "gotowe"), WritePage("nie powinno sie wykonac")));

        var outcome = await new GenerationService(runner, store, versions)
            .RunRoundAsync(meta, [C("c1", null, "x")]);

        Assert.False(outcome.Succeeded);
        Assert.Equal(FailureHandling.Halted, outcome.Failure!.Handling);
        Assert.Equal(0, runner.Calls);      // model NIGDY nie wywolany
        Assert.Equal(0m, outcome.SpentUsd);

        var persisted = store.Load();
        Assert.Equal(ProjectStatus.Frozen, persisted.Status);   // NIE odmrozony
        Assert.Equal(
            "biezaca zamrozona strona",
            File.ReadAllText(Path.Combine(versions.WorkDir, "index.html")));   // WorkDir nietkniety
    }

    [Fact]
    public async Task Nieudana_runda_przywraca_workdir_do_ostatniej_udanej_wersji()
    {
        // Punkt 4 raportu: petla naprawy kotwic ma kopie zapasowa, petla bramki
        // TWARDEJ nie miala zadnej, a Failed() nic nie przywracal. Po nieudanej
        // rundzie WorkDir zostawal w stanie, w jakim przerwal go ostatni przebieg
        // (tu: urwany HTML), a nastepna runda szlaby --resume po zepsutej stronie.
        var (meta, store, versions) = Setup();
        var goodPage = """<html><body><h1 data-cmt-id="hero">x</h1></body></html>""";
        Directory.CreateDirectory(versions.SnapshotPath(1));
        File.WriteAllText(Path.Combine(versions.SnapshotPath(1), "index.html"), goodPage);
        // Symulujemy, ze WorkDir ma tresc wersji 1 (tak zostawilby prawdziwy Commit
        // — WorkDir i snapshot sa zgodne zaraz po udanej rundzie).
        File.WriteAllText(Path.Combine(versions.WorkDir, "index.html"), goodPage);

        var metaV1 = meta with
        {
            Versions = [new VersionMeta(1, "s-0", versions.SnapshotPath(1), 0.5m, [])],
        };
        store.Save(metaV1);

        // Obie proby (przebieg + jedna powtorka) psuja render.
        var runner = new ScriptedRunner((Json("s-1", 0.1m, "gotowe"), WriteBrokenPage()));

        var outcome = await new GenerationService(runner, store, versions)
            .RunRoundAsync(metaV1, [C("c1", "hero", "x")]);

        Assert.False(outcome.Succeeded);
        Assert.Equal(2, runner.Calls);

        // Kluczowa asercja: WorkDir musi zawierac strone z wersji 1, NIE urwany
        // HTML z ostatniej nieudanej proby.
        var restored = File.ReadAllText(Path.Combine(versions.WorkDir, "index.html"));
        Assert.Equal(goodPage, restored);
        Assert.True(RenderValidator.Check(versions.WorkDir).Ok);
    }

    [Fact]
    public async Task Nieudana_pierwsza_runda_bez_wczesniejszej_wersji_czysci_workdir()
    {
        // Wariant punktu 4 dla projektu bez ANI JEDNEJ udanej wersji: nie ma do
        // czego wracac (Restore nie ma czego kopiowac), wiec WorkDir jest po
        // prostu czyszczony do pusta zamiast zostawiac urwany HTML.
        var (meta, store, versions) = Setup();
        var runner = new ScriptedRunner((Json("s-1", 0.1m, "gotowe"), WriteBrokenPage()));

        var outcome = await new GenerationService(runner, store, versions)
            .RunRoundAsync(meta, [C("c1", null, "x")]);

        Assert.False(outcome.Succeeded);
        Assert.False(File.Exists(Path.Combine(versions.WorkDir, "index.html")),
            "WorkDir powinien byc pusty po nieudanej pierwszej rundzie, nie zawierac urwany HTML");
    }

    [Fact]
    public async Task Przebieg_bez_zmian_w_katalogu_roboczym_jest_traktowany_jak_niepowodzenie()
    {
        // Punkt 6 raportu: bramka renderowalnosci sprawdzala stan KATALOGU, nie to,
        // czy TEN przebieg cokolwiek zrobil. Od rundy 2 katalog roboczy juz ma
        // tresc poprzedniej wersji, wiec model, ktory nic nie zrobil (ale zwrocil
        // subtype:success i puste permission_denials), przechodzilby bramke czysto
        // przez przypadek i dawal wersje 2 identyczna z wersja 1 — klient traci
        // jedna z limitowanych poprawek za pokazanie mu tej samej strony.
        var (meta, store, versions) = Setup();
        var page = """<html><body><h1 data-cmt-id="hero">x</h1></body></html>""";
        Directory.CreateDirectory(versions.SnapshotPath(1));
        File.WriteAllText(Path.Combine(versions.SnapshotPath(1), "index.html"), page);
        File.WriteAllText(Path.Combine(versions.WorkDir, "index.html"), page);   // "poprzednia runda" to zostawila

        var metaV1 = meta with
        {
            Versions = [new VersionMeta(1, "s-0", versions.SnapshotPath(1), 0.5m, [])],
        };
        store.Save(metaV1);

        // Zwraca sukces (subtype:success, permission_denials puste), ale NIC nie
        // dotyka w katalogu roboczym — oba wywolania (proba + powtorka).
        var runner = new ScriptedRunner((Json("s-1", 0.1m, "gotowe"), _ => { }));

        var outcome = await new GenerationService(runner, store, versions)
            .RunRoundAsync(metaV1, [C("c1", "hero", "x")]);

        Assert.False(outcome.Succeeded);
        Assert.Equal(FailureHandling.Halted, outcome.Failure!.Handling);
        Assert.Contains("nie zmieni", outcome.Failure.Cause);
        Assert.Equal(2, runner.Calls);   // proba + jedna powtorka, jak przy zlym renderze

        var persisted = store.Load();
        Assert.Equal(0, persisted.RoundsUsed);   // awaria po naszej stronie nie zuzywa rundy
        Assert.Single(persisted.Versions);       // nadal tylko wersja 1 — zadnej nowej
        Assert.Equal(0.2m, persisted.SpentUsd);  // ale koszt obu prob jest widoczny w budzecie
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

    [Fact]
    public async Task Runda_po_powrocie_ma_numer_3_rodzica_1_i_nowa_sesje()
    {
        var (meta, store, versions) = Setup();
        var runner = new ScriptedRunner(
            (Json("s-1", 0.10m, "ok"), WritePage("""<h1 data-cmt-id="hero">v1</h1>""")),
            (Json("s-1", 0.10m, "ok"),
             WritePage("""<h1 data-cmt-id="hero">v2</h1><p data-cmt-id="stopka">x</p>""")),
            (Json("s-9", 0.10m, "ok"), WritePage("""<h1 data-cmt-id="hero">v3</h1>""")));

        var svc = new GenerationService(runner, store, versions);
        await svc.RunRoundAsync(meta, [C("c1", null, "raz")]);
        await svc.RunRoundAsync(store.Load(), [C("c2", null, "dwa")]);

        // v2 MUSI zachowac "hero" i dolozyc "stopka". Gdyby v2 porzucila "hero",
        // odpalilaby sie petla naprawy kotwic, ScriptedRunner odtworzylby ostatni
        // wpis skryptu i nadpisal v2 trescia v3 — a wtedy slowo "stopka" nie
        // istnialoby w zadnym snapshocie i obie asercje o kotwicach na koncu tego
        // testu bylyby prawdziwe ZAWSZE (zmierzone w recenzji). Dodatkowo dwa
        // identyczne zapisy pod rzad czynilyby DirectorySignature zaleznym od
        // rozdzielczosci mtime — na NTFS test padalby losowo.
        Assert.Equal(2, runner.Calls);   // jeden przebieg na runde, zero napraw

        // Powrot do v1 — to samo, co zrobi ProjectApi.RollbackAsync w Zadaniu 5.
        store.Save(store.Load() with { CurrentVersion = 1 });

        var outcome = await svc.RunRoundAsync(store.Load(), [C("c3", null, "trzy")]);

        Assert.True(outcome.Succeeded);
        Assert.Equal(3, outcome.Version!.Number);          // max+1, nie Count+1
        Assert.Equal(1, outcome.Version!.BasedOn);         // rodzicem jest v1, nie v2
        Assert.Equal(3, store.Load().CurrentVersion);

        // Nowa sesja: --session-id, nie --resume po transkrypcie pamietajacym v2.
        var ostatnie = runner.Requests[^1];
        Assert.True(ostatnie.IsFirstRun);
        Assert.NotEqual("s-1", ostatnie.SessionId);

        // Kotwice do promptu ida z v1 ("hero"), nie z v2 ("stopka") — inaczej
        // model dostalby polecenie zachowania bloku, ktorego nie ma w plikach.
        Assert.Contains("hero", runner.Instructions[^1]);
        Assert.DoesNotContain("stopka", runner.Instructions[^1]);
    }

    [Fact]
    public async Task ChangedAnchors_porownuje_z_RODZICEM_a_nie_z_poprzednim_numerem()
    {
        var (meta, store, versions) = Setup();
        var runner = new ScriptedRunner(
            (Json("s-1", 0.10m, "ok"), WritePage("""<p data-cmt-id="hero">A</p>""")),
            (Json("s-1", 0.10m, "ok"), WritePage("""<p data-cmt-id="hero">B</p>""")),
            (Json("s-9", 0.10m, "ok"), WritePage("""<p data-cmt-id="hero">A</p>""")));

        var svc = new GenerationService(runner, store, versions);
        await svc.RunRoundAsync(meta, [C("c1", null, "raz")]);
        await svc.RunRoundAsync(store.Load(), [C("c2", null, "dwa")]);
        store.Save(store.Load() with { CurrentVersion = 1 });

        var v3 = (await svc.RunRoundAsync(store.Load(), [C("c3", null, "trzy")])).Version!;

        // v3 ma tresc IDENTYCZNA z v1 (swoim rodzicem) i rozna od v2. Porownanie
        // z v2 daloby ["hero"] i podswietlilo klientowi blok, ktory sie nie zmienil.
        Assert.Empty(v3.ChangedAnchors!);

        var v2 = store.Load().Versions.First(v => v.Number == 2);
        Assert.Equal(["hero"], v2.ChangedAnchors!);
    }

    [Fact]
    public async Task Nieudana_runda_po_powrocie_przywraca_WorkDir_do_v1_a_nie_do_v2()
    {
        // Kontrakt 4.4: nieudana runda to dla klienta BRAK ZDARZENIA. Po powrocie
        // do v1 „brak zdarzenia" znaczy v1 — przywrocenie v2 oddaje klientowi
        // tresc, ktora swiadomie odrzucil, a nastepna udana runda powstaje na niej,
        // deklarujac w BasedOn wersje 1. Po cichu i bez sladu.
        var (meta, store, versions) = Setup();
        var runner = new ScriptedRunner(
            (Json("s-1", 0.10m, "ok"), WritePage("""<h1 data-cmt-id="hero">TRESC-V1</h1>""")),
            (Json("s-1", 0.10m, "ok"), WritePage("""<h1 data-cmt-id="hero">TRESC-V2</h1>""")),
            (Json("s-9", 0.10m, "ok"), WriteBrokenPage()));   // bramka twarda odrzuci

        var svc = new GenerationService(runner, store, versions);
        await svc.RunRoundAsync(meta, [C("c1", null, "raz")]);
        await svc.RunRoundAsync(store.Load(), [C("c2", null, "dwa")]);
        store.Save(store.Load() with { CurrentVersion = 1 });

        var outcome = await svc.RunRoundAsync(store.Load(), [C("c3", null, "trzy")]);

        Assert.False(outcome.Succeeded);
        var work = await File.ReadAllTextAsync(Path.Combine(versions.WorkDir, "index.html"));
        Assert.Contains("TRESC-V1", work);
        Assert.DoesNotContain("TRESC-V2", work);

        // I nic klientowi nie zabrala: wersje, licznik rund i wersja biezaca stoja.
        var persisted = store.Load();
        Assert.Equal(2, persisted.Versions.Count);
        Assert.Equal(1, persisted.CurrentVersion);
        Assert.Equal(2, persisted.RoundsUsed);
    }

    [Fact]
    public async Task Numeracja_nie_nadpisuje_snapshotu_gdy_w_historii_jest_dziura()
    {
        // `Commit` kasuje SnapshotPath(number) PRZED kopiowaniem, wiec kolizja
        // numeru nadpisuje starszy snapshot klienta bez sladu. Przy ciaglej
        // historii Count+1 i max+1 daja to samo — dziura je rozdziela.
        var (meta, store, versions) = Setup();
        var runner = new ScriptedRunner(
            (Json("s-1", 0.10m, "ok"), WritePage("""<h1 data-cmt-id="hero">v9</h1>""")));

        store.Save(store.Load() with
        {
            Versions = [new VersionMeta(9, "s-1", versions.SnapshotPath(9), 0.1m, [], null)],
            CurrentVersion = 9,
        });

        var outcome = await new GenerationService(runner, store, versions)
            .RunRoundAsync(store.Load(), [C("c1", null, "x")]);

        // Count+1 dalby 2. max+1 daje 10.
        Assert.Equal(10, outcome.Version!.Number);
    }
}
