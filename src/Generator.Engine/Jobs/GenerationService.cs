using System.Text.RegularExpressions;
using Generator.Engine.ClaudeCli;
using Generator.Engine.Gates;
using Generator.Engine.IO;
using Generator.Engine.Model;
using Generator.Engine.Prompts;
using Generator.Engine.Storage;
using Generator.Engine.Versioning;

namespace Generator.Engine.Jobs;

/// Kontrakt 4.3: frontend rozgalezia sie po Handling; Cause zostaje w logach.
/// RunRoundAsync jest terminalne — samo wyczerpuje swoj wewnetrzny budzet powtorek,
/// wiec kazde JobFailure, ktore stad wychodzi, niesie Halted (nigdy Retrying:
/// to by sugerowalo, ze wywolujacy ma sam sprobowac ponownie, a on juz probowal).
public record JobFailure(FailureHandling Handling, string Cause, int Attempts);

public record GenerationOutcome(
    bool Succeeded,
    VersionMeta? Version,
    JobFailure? Failure,
    IReadOnlyList<CommentResult> CommentResults,
    decimal SpentUsd);

/// Szkic kierunku z §3 planu produktu. Name pochodzi z <title> szkicu (ruling 4),
/// Html to caly jednoplikowy szkic — klient wybiera obrazkiem, nie opisem.
public record Proposal(string Id, string Name, string Html);

public record ProposalsOutcome(bool Succeeded, IReadOnlyList<Proposal> Proposals,
    JobFailure? Failure, decimal SpentUsd);

public partial class GenerationService(IClaudeRunner runner, ProjectStore projects, VersionStore versions)
    : IRoundRunner
{
    /// Kontrakt 4.3: jedna automatyczna powtorka, potem halted.
    public const int RunRetryLimit = 1;

    /// Bramka miekka 5.2 nie podaje liczby — 2 do przestawienia po pomiarach.
    public const int AnchorRetryLimit = 2;

    [GeneratedRegex(@"^RAPORT\s+(\S+)\s+(applied|rejected)\s+(.*)$", RegexOptions.Multiline)]
    private static partial Regex ReportRegex();

    /// UWAGA (raport, punkt 8): parametr "meta" jest ignorowany poza sygnatura —
    /// pierwsza linia metody nadpisuje go swiezym odczytem z dysku. Kolejka moze
    /// trzymac obiekt "meta" przez cala dlugosc przebiegu (realnie kilka minut);
    /// gdyby ktos w tym czasie zapisal projekt (inny watek, recznie), praca na
    /// przekazanej-a-nieswiezej kopii cicho zgubilaby wersje albo cofnela SpentUsd.
    /// Parametr zostaje dla zgodnosci sygnatury (i bo caller zwykle i tak ma "meta"
    /// pod reka po Load/Create) — Twoj wybor byl mozliwy takze przez usuniecie go,
    /// patrz raport.
    private async Task<GenerationOutcome> RunAsync(
        ProjectMeta meta,
        IReadOnlyList<Comment> comments,
        Func<ProjectMeta, string> buildInstruction,
        bool consumesRound,
        CancellationToken ct)
    {
        meta = projects.Load();

        // Punkt 7 raportu: Freeze() dziala dopiero PO przebiegu, wiec bez tej
        // straznicy na WEJSCIU runda na projekcie Frozen uruchomilaby claude -p
        // (wydajac budzet POZA limitem), a udana sciezka ustawilaby na koncu
        // Status = Active — czyli PO CICHU odmrazalaby projekt. Odrzucamy przed
        // dotknieciem WorkDir i przed jakimkolwiek wywolaniem modelu; zero kosztu.
        if (meta.Status == ProjectStatus.Frozen)
        {
            return new GenerationOutcome(false, null,
                new JobFailure(
                    FailureHandling.Halted,
                    "projekt jest zamrozony (Frozen) — runda odrzucona bez wywolania modelu",
                    Attempts: 0),
                [], 0m);
        }

        // work/ jest JEDYNA mutowalna prawda systemu, a rdzen dotad przyjmowal go
        // w stanie, w jakim zostawil go poprzedni przebieg. Jedyne przywrocenie,
        // jakie istnialo (RestoreWorkDirAfterFailure), wisi na `Failed()` — czyli
        // dziala wylacznie przy awarii, ktora zdazyla WROCIC. Anulowanie i zabicie
        // procesu nie wracaja nigdy: work/ zostaje z polowa zapisanego index.html,
        // a nastepna runda idzie `--resume` po urwanym pliku i utrwala go jako
        // wersje, za ktora klient placi.
        //
        // W sciezce szczesliwej to no-op: po udanej rundzie work/ jest juz kopia
        // snapshotu wersji biezacej. W sciezce po awarii to naprawa, i to przed
        // pierwszym wywolaniem modelu, czyli za darmo.
        //
        // TYLKO gdy wersja biezaca istnieje. Przypadku `Current is null` nie ruszamy:
        // Generator.Cli wola RunRoundAsync na pustym projekcie wlasnie po to, zeby
        // postawic pierwsza strone, i to jest kontrakt silnika, nie blad.
        //
        // Brak snapshotu wersji, ktora JEST na liscie, to zlamany niezmiennik, nie
        // sytuacja do przemilczenia — `Restore` rzuca DirectoryNotFoundException
        // i wyjatek leci dalej (kolejka zapisze `halted` i zaloguje przyczyne).
        // Cicha tolerancja znaczylaby dokladnie to, przed czym ta linia broni:
        // runde po katalogu o nieznanej zawartosci.
        if (meta.Current is not null) versions.Restore(meta.CurrentVersion);

        // previousAnchors ZOSTAJE w rdzeniu, mimo ze instrukcje buduje juz lambda:
        // bramka miekka (petla naprawy kotwic, nizej) porownuje z nimi stan katalogu
        // po przebiegu, wiec bez tej linii nie skompilowalaby sie. Dla RunRoundAsync
        // liczymy je dwa razy (raz w lambdzie, raz tutaj) — to idempotentny odczyt
        // snapshotu rodzica, ktory w trakcie przebiegu sie nie zmienia.
        var previousAnchors = PreviousAnchors(meta);
        var instruction = buildInstruction(meta);

        // Sesja jest jedna na projekt, ale TYLKO dopoki historia jest liniowa.
        // Po powrocie (CurrentVersion != najnowsza) --resume podalby modelowi
        // transkrypt pamietajacy zmiany, ktorych w plikach juz nie ma. Pliki sa
        // prawda: zaczynamy swieza sesje i tracimy wylacznie wlasny kontekst modelu.
        var current = meta.Current;
        var isFirstRun = current is null || meta.CurrentVersion != meta.Versions.Max(v => v.Number);
        var sessionId = isFirstRun ? Guid.NewGuid().ToString() : current!.SessionId;

        decimal spent = 0m;
        var attempts = 0;
        ClaudeResult? lastParsed = null;

        // Punkt 1 raportu (KRYTYCZNE): pieniadze wydane przed anulowaniem nie moga
        // zniknac. IClaudeRunner.RunAsync przy anulowaniu rzuca OperationCanceledException
        // zamiast wracac normalnie — bez tego "finally" wyjatek lecialby przez cala
        // reszte metody i ANI Failed(), ANI sciezka sukcesu nigdy by sie nie wykonaly,
        // wiec zaden dotychczasowy zapis "spent" nigdy by nie nastapil.
        // "persisted" pilnuje, zeby SUMA wydanych dotad pieniedzy trafila na dysk,
        // niezaleznie od tego, w ktorym miejscu ponizej metoda zostanie przerwana.
        // OperationCanceledException NIE jest tu lapany — ma lecieć dalej nietkniety,
        // zgodnie z kontraktem CancellationToken w .NET (patrz ClaudeRunner.RunAsync).
        var persisted = false;
        try
        {
            // --- przebieg + bramka 5.1 + bramka twarda 5.2, z jedna powtorka ---
            // Kolejnosc: bramka wykonania (RunGate) NAJPIERW — nie ma sensu sprawdzac
            // renderowalnosci pliku po przebiegu, ktory sam w sobie sie nie powiodl.
            while (true)
            {
                attempts++;

                // Punkt 6 raportu: sygnatura WorkDir PRZED tym konkretnym przebiegiem —
                // porownywana z sygnatura PO, zeby odroznic "ten przebieg cokolwiek
                // zrobil" od "katalog akurat sie renderuje, bo tak zostawila go
                // POPRZEDNIA runda".
                var before = DirectorySignature(versions.WorkDir);

                var outcome = await runner.RunAsync(
                    new ClaudeRunRequest(versions.WorkDir, instruction, sessionId, isFirstRun), ct);

                var gate = RunGate.Evaluate(outcome);
                // Kontrakt: Parsed niesie TotalCostUsd takze przy werdykcie negatywnym —
                // pieniadze zostaly wydane niezaleznie od tego, czy przebieg sie udal.
                spent += gate.Parsed?.TotalCostUsd ?? 0m;
                lastParsed = gate.Parsed ?? lastParsed;

                if (gate.Verdict == GateVerdict.Halt)
                {
                    var failure = Failed(meta, FailureHandling.Halted, gate.Cause, attempts, spent);
                    persisted = true;
                    return failure;
                }

                if (gate.Verdict == GateVerdict.Retry)
                {
                    if (attempts > RunRetryLimit)
                    {
                        var failure = Failed(meta, FailureHandling.Halted, gate.Cause, attempts, spent);
                        persisted = true;
                        return failure;
                    }

                    // Sesja istnieje dopiero, gdy CLI zdazylo zwrocic sparsowalny JSON
                    // (gate.Parsed niepuste) — to na tym opiera sie caly --resume.
                    // Przy pustym/niesparsowalnym stdout (np. przebieg padl zanim
                    // model cokolwiek zwrocil) sesja mogla nigdy nie powstac; empirycznie
                    // sprawdzone na prawdziwym `claude -p --resume <nieistniejacy-id>`:
                    // exit 1, pusty stdout, stderr "No conversation found with session
                    // ID: ...". Bezwarunkowe przestawienie na --resume zamienialoby
                    // KAZDA taka usterke w gwarantowany Halted (powtorka od razu pada
                    // na zlym --resume), mimo ze powtorka z --session-id mialaby szanse.
                    if (gate.Parsed is not null)
                        isFirstRun = false;
                    continue;
                }

                var render = RenderValidator.Check(versions.WorkDir);
                var after = DirectorySignature(versions.WorkDir);
                var unchanged = before == after;

                if (render.Ok && !unchanged) break;

                // Punkt 6 raportu: warunek 5 z 5.1 sprawdzal renderowalnosc STANU
                // katalogu, nie tego, czy TEN przebieg cokolwiek zapisal. Od rundy 2
                // WorkDir juz ma tresc poprzedniej wersji, wiec model, ktory nic nie
                // zrobil (ale zglosil subtype:success i puste permission_denials),
                // przechodzilby czysto przez przypadek — klient dostalby wersje N+1
                // identyczna z N i strate jednej z limitowanych rund. Renderowalnosc
                // ma pierwszenstwo w komunikacie: to ONA jest prawdziwym powodem, gdy
                // oba warunki zawioda naraz (zachowuje istniejacy test na komunikat
                // "wersja sie nie renderuje").
                var cause = !render.Ok
                    ? $"wersja sie nie renderuje: {string.Join("; ", render.Problems)}"
                    : "przebieg zakonczyl sie zglaszanym sukcesem, ale nie zmienil zadnego pliku w katalogu roboczym";

                if (attempts > RunRetryLimit)
                {
                    var failure = Failed(meta, FailureHandling.Halted, cause, attempts, spent);
                    persisted = true;
                    return failure;
                }

                isFirstRun = false;
                instruction = render.Ok
                    ? $"{instruction}\n\nPOPRAW: katalog roboczy nie zmienil sie mimo zglaszanego sukcesu — wprowadz realna zmiane."
                    : $"{instruction}\n\nPOPRAW: {cause}";
            }

            // --- bramka miekka: kotwice. Po wyczerpaniu prob wersja IDZIE do klienta,
            // ale KAZDA proba naprawy dotyka realny WorkDir na dysku (efekt runnera
            // pisze pliki bezposrednio) — proba, ktora zepsuje renderowalnosc, nie
            // moze zostac wpisana do snapshotu (bramka twarda ma pierwszenstwo nad
            // kazda wersja, nie tylko nad pierwszym przebiegiem). Dlatego trzymamy
            // kopie zapasowa ostatniego znanego DOBREGO stanu i przywracamy ja, gdy
            // proba naprawy cokolwiek popsuje.
            var currentAnchors = AnchorExtractor.Extract(versions.WorkDir);
            var orphaned = AnchorExtractor.Orphaned(previousAnchors, currentAnchors);

            if (orphaned.Count > 0)
            {
                var backupDir = Directory.CreateTempSubdirectory("gen-svc-anchor-backup-").FullName;
                try
                {
                    DirectoryOps.SafeReplace(versions.WorkDir, backupDir);

                    var anchorTries = 0;
                    while (orphaned.Count > 0 && anchorTries < AnchorRetryLimit)
                    {
                        anchorTries++;
                        var repair = await runner.RunAsync(
                            new ClaudeRunRequest(versions.WorkDir, PromptBuilder.BuildAnchorRepair(orphaned), sessionId, false),
                            ct);

                        var gate = RunGate.Evaluate(repair);
                        spent += gate.Parsed?.TotalCostUsd ?? 0m;

                        var repaired = gate.Verdict == GateVerdict.Ok && RenderValidator.Check(versions.WorkDir).Ok;
                        if (!repaired)
                        {
                            // Nie walczymy dalej — przywracamy ostatni dobry stan (ta
                            // proba mogla go nadpisac zepsutym HTML-em) i dostarczamy
                            // z lista osieroconych sprzed tej proby.
                            DirectoryOps.SafeReplace(backupDir, versions.WorkDir);
                            break;
                        }

                        currentAnchors = AnchorExtractor.Extract(versions.WorkDir);
                        orphaned = AnchorExtractor.Orphaned(previousAnchors, currentAnchors);
                        DirectoryOps.SafeReplace(versions.WorkDir, backupDir);   // ten stan tez jest dobry — odswiez kopie
                    }
                }
                finally
                {
                    // Sprzatanie w finally moze samo rzucic (uchwyt pliku wciaz
                    // otwarty, wyscig z systemem plikow) — dokladnie tak samo jak przy
                    // Process.Kill w ClaudeRunner.cs. Gdyby anulowanie przyszlo w
                    // trakcie petli naprawy kotwic, OperationCanceledException lecialby
                    // przez ten finally; goly Directory.Delete, ktory akurat wtedy
                    // rzuci (bo proba naprawy trzyma otwarty plik / trwa jeszcze zapis),
                    // ZASTAPILBY je swoim wyjatkiem (np. IOException) i wywolujacy
                    // zapisalby to jako zwykla awarie zamiast uszanowac anulowanie.
                    // Ten catch (Exception) jest wiec celowo szeroki: zaden blad ze
                    // sprzatania nie jest wazniejszy od wyjatku, ktory to sprzatanie
                    // przerwalo — NIE zawezaj do konkretnych typow.
                    try
                    {
                        Directory.Delete(backupDir, recursive: true);
                    }
                    catch (Exception)
                    {
                    }
                }
            }

            // Liczymy PRZED Commit, na WorkDir kontra snapshot rodzica: po Commit
            // snapshot dziecka i WorkDir sa identyczne, wiec kolejnosc nie zmienia
            // wyniku — ale liczenie z WorkDir nie wymaga, zeby Commit sie powiodl.
            var changed = await ZmienioneBloki.PoliczZeSnapshotow(
                meta.Current?.SnapshotDir, versions.WorkDir);

            var number = meta.NextVersionNumber;
            var version = versions.Commit(number, sessionId, spent, orphaned,
                basedOn: meta.Current?.Number, changedAnchors: changed);

            // Ruling 7 + kontrakt 4.1: rundy klienta zuzywa WYLACZNIE runda
            // komentarzy. Wersja pierwsza i propozycje kosztuja budzet, ale nie
            // sa poprawkami, wiec nie zabieraja klientowi zadnej z 15.
            var zBudzetem = ProjectStore.WithSpend(meta, spent);
            var updated = (consumesRound ? ProjectStore.WithRoundConsumed(zBudzetem) : zBudzetem) with
            {
                Status = ProjectStatus.Active,
                Versions = [.. meta.Versions, version],
                CurrentVersion = number,
            };
            // Punkt 1 raportu, druga droga tej samej luki: versions.Commit() (wyzej)
            // jest PRZED tym Save. Jesli Save rzuci, snapshot juz lezy na dysku, ale
            // bez tego "finally" runda i jej koszt przepadalyby razem z wyjatkiem —
            // "persisted" ponizej zostaje false, wiec finally i tak zapisze chociaz
            // sam koszt (bez wersji, bo "updated" nigdy nie trafil do dysku).
            projects.Save(Freeze(updated));
            persisted = true;

            var results = ParseReport(lastParsed?.ResultText ?? string.Empty, comments);
            return new GenerationOutcome(true, version, null, results, spent);
        }
        finally
        {
            if (!persisted)
                try { projects.Save(Freeze(ProjectStore.WithSpend(meta, spent))); } catch (Exception) { }
        }
    }

    public Task<GenerationOutcome> RunRoundAsync(
        ProjectMeta meta, IReadOnlyList<Comment> comments, CancellationToken ct = default) =>
        RunAsync(meta, comments,
            m => PromptBuilder.BuildRound(comments, PreviousAnchors(m)),
            consumesRound: true, ct);

    /// Wersja 1 z wybranego szkicu. Zamiast uwag idzie brief; cala reszta —
    /// piec warunkow z sekcji 2, bramka twarda, naprawa kotwic, snapshot, Freeze —
    /// jest ta sama sciezka co runda i celowo nie jest tu duplikowana.
    public Task<GenerationOutcome> RunFirstVersionAsync(
        ProjectMeta meta, CancellationToken ct = default)
    {
        // F1: `consumesRound: false` jest wlasnoscia TEJ metody, nie stanu projektu —
        // wiec bez tej strazy drugie wywolanie tworzy pelnoprawna wersje ZA DARMO
        // (licznik rund stoi). Gorzej: przy istniejacej wersji `isFirstRun` w rdzeniu
        // wychodzi `false`, wiec prompt „zbuduj strone od zera" pojechalby z `--resume`
        // na sesji poprzedniej wersji, do WorkDir, ktory juz zawiera jej pliki —
        // klient dostaje strone zbudowana od nowa w miejsce swojej biezacej.
        // Odrzucamy przed dotknieciem WorkDir i przed wywolaniem modelu, dokladnie
        // jak straz `Frozen` w RunProposalsAsync. Stan bierzemy z dysku, nie
        // z parametru — patrz komentarz nad rdzeniem.
        var swieza = projects.Load();
        if (swieza.Versions.Count > 0)
        {
            return Task.FromResult(new GenerationOutcome(false, null,
                new JobFailure(
                    FailureHandling.Halted,
                    $"projekt ma juz {swieza.Versions.Count} wersji — wersje pierwsza generuje sie " +
                    "tylko raz; kolejne zmiany ida przez runde uwag",
                    Attempts: 0),
                [], 0m));
        }

        return RunAsync(meta,
            [],
            m => PromptBuilder.BuildBrief(OpisDoPromptu(m), ReadChosenSketch(m), CzytajZrodlo(m)),
            consumesRound: false, ct);
    }

    /// F2 (bezpieczenstwo): `ChosenProposal` przychodzi z zadania HTTP, a tutaj
    /// trafialoby jako niewalidowany segment sciezki. `Path.Combine` przepuszcza
    /// „../", a dla sciezki BEZWZGLEDNEJ odrzuca baze w calosci — w obu razach
    /// tresc dowolnego pliku z dysku wladowalaby sie w prompt. Biala lista trzech
    /// literalow zamyka to szczelniej i prosciej niz GetFullPath + porownywanie
    /// prefiksow: inne id niz `SketchIds` NIGDY nie powstaje po naszej stronie.
    /// Wybor spoza listy = brak wyboru (wersja 1 powstanie z samego opisu).
    ///
    /// F3: id Z listy, ale bez pliku, to co innego — to zlamany niezmiennik
    /// (RunProposalsAsync czysci wybor razem z katalogiem), wiec jest GLOSNE.
    /// Cichy `null` dawalby tu wersje 1 zbudowana wbrew wyborowi klienta i nikt
    /// by sie nie dowiedzial. InvalidDataException, bo to ta sama klasa bledu i ten
    /// sam kanal co `ProjectStore.Load` na uszkodzonym project.json — a rdzen i tak
    /// zaczyna od `projects.Load()`, wiec wywolujacy juz musi ja obslugiwac.
    /// Rzucamy przed `try` w rdzeniu: nic nie jest do cofniecia, zero wydatku.
    private string? ReadChosenSketch(ProjectMeta meta)
    {
        if (meta.ChosenProposal is null) return null;
        if (!SketchIds.Contains(meta.ChosenProposal)) return null;

        var plik = Path.Combine(ProposalsDir, $"{meta.ChosenProposal}.html");
        if (!File.Exists(plik))
            throw new InvalidDataException(
                $"projekt wskazuje wybrany szkic '{meta.ChosenProposal}', ale pliku nie ma: {plik}");

        return File.ReadAllText(plik);
    }

    /// Katalog szkicow, OBOK work/ — trzy konkurencyjne pliki HTML w katalogu
    /// roboczym zepsulyby wersje pierwsza.
    private string ProposalsDir => Path.Combine(projects.Dir, "proposals");

    /// Tresc obecnej strony klienta, wyciagnieta przy zakladaniu projektu. Obok work/
    /// z tego samego powodu co proposals/: VersionStore kopiuje CALY katalog roboczy,
    /// wiec plik z materialem zrodlowym trafilby do snapshotu i do paczki klienta.
    public static string SciezkaZrodla(string katalogProjektu) =>
        Path.Combine(katalogProjektu, "zrodlo.txt");

    /// <summary>
    /// Material zrodlowy dla trybu „mam strone, ale slaba". Projekt z rodzajem `Idea`
    /// go nie ma i nie potrzebuje.
    ///
    /// Projekt `Url` BEZ pliku to zlamany niezmiennik, nie sytuacja do obsluzenia po
    /// cichu: zalozenie projektu pobiera strone albo sie nie udaje, wiec brak pliku
    /// znaczy, ze ktos skasowal go spod nas. Cichy `null` dalby wersje zbudowana
    /// z niczego — klient dostalby wymyslona firme zamiast swojej i nikt by sie nie
    /// dowiedzial. Ta sama decyzja i ten sam typ wyjatku co przy wybranym szkicu (F3).
    /// </summary>
    private PromptBuilder.ObecnaStrona? CzytajZrodlo(ProjectMeta meta)
    {
        if (meta.Source != SourceKind.Url) return null;

        var plik = SciezkaZrodla(projects.Dir);
        if (!File.Exists(plik))
            throw new InvalidDataException(
                $"projekt powstal z adresu '{meta.SourceUrl}', ale pliku z trescia strony nie ma: {plik}");

        return new PromptBuilder.ObecnaStrona(meta.SourceUrl ?? "", File.ReadAllText(plik));
    }

    /// W trybie „mam strone" opis klienta JEST adresem (tak zapisuje go ProjectApi),
    /// a adres stoi juz w naglowku bloku obecnej strony. Powtarzanie go jako „OPIS
    /// KLIENTA: https://..." to szum, za ktory placimy tokenami i ktory niczego nie wnosi.
    private static string? OpisDoPromptu(ProjectMeta meta) =>
        meta.Source == SourceKind.Url ? null : meta.Description;

    /// Jedyne dopuszczalne id szkicu — sluzy ZA RAZ za biala liste w ReadChosenSketch
    /// i za liste do odczytu w CzytajSzkice. Jedno zrodlo prawdy: dwie osobne listy
    /// rozjechalyby sie przy pierwszej zmianie liczby kierunkow.
    private static readonly string[] SketchIds = ["a", "b", "c"];

    public async Task<ProposalsOutcome> RunProposalsAsync(ProjectMeta meta, CancellationToken ct = default)
    {
        meta = projects.Load();
        if (meta.Status == ProjectStatus.Frozen)
        {
            return new ProposalsOutcome(false, [],
                new JobFailure(FailureHandling.Halted,
                    "projekt jest zamrozony (Frozen) — propozycje odrzucone bez wywolania modelu", 0),
                0m);
        }

        // F3: za chwile kasujemy proposals/, wiec dotychczasowy wybor klienta
        // wskazuje na material, ktorego juz nie ma. Czyscimy go RAZEM z katalogiem —
        // celem jest, zeby stan projektu byl spojny, a nie sprzeczny-ale-wykrywalny.
        // Zapis PRZED skasowaniem katalogu: gdyby Delete padlo, zostajemy z „brak
        // wyboru" (nieszkodliwe), a nie z „wybor wskazuje na nieistniejacy plik".
        // Dziala na `meta` swiezo wczytanej wyzej — to wybor SPRZED regeneracji;
        // przeladowanie na koncu (przy zapisie wydatku) juz nic nie czysci.
        if (meta.ChosenProposal is not null)
        {
            meta = meta with { ChosenProposal = null };
            projects.Save(meta);
        }

        if (Directory.Exists(ProposalsDir)) Directory.Delete(ProposalsDir, recursive: true);
        Directory.CreateDirectory(ProposalsDir);

        var outcome = await runner.RunAsync(new ClaudeRunRequest(
            ProposalsDir, PromptBuilder.BuildProposals(OpisDoPromptu(meta), CzytajZrodlo(meta)),
            SessionId: Guid.NewGuid().ToString(), IsFirstRun: true,
            SystemAppendix: ClaudeRunner.ProposalsAppendix), ct);

        var gate = RunGate.Evaluate(outcome);
        var spent = gate.Parsed?.TotalCostUsd ?? 0m;

        // Freeze, nie goly WithSpend: kontrakt 4.1 mowi, ze projekt zatrzymuje
        // PIERWSZY wyczerpany licznik. Propozycje to realny wydatek, wiec musza
        // umiec przewazyc budzet tak samo jak runda — inaczej projekt z niskim
        // budzetem przekracza go po cichu i zamraza sie dopiero runde pozniej.
        //
        // Nie ma tu za to `finally` ratujacego koszt przy anulowaniu (jak w rundzie):
        // runda kumuluje wydatek przez kilka przebiegow i przy anulowaniu ma co
        // uratowac, a propozycje to JEDNO wywolanie — anulowane, nie zwraca JSON-a,
        // wiec kosztu i tak nie znamy. Zapisanie zera nie byloby ratunkiem.
        //
        // F6: zapisujemy stan PRZELADOWANY z dysku, nie `meta` sprzed wywolania
        // modelu. Miedzy jednym a drugim mijaja minuty, a wybor propozycji celowo
        // NIE idzie przez kolejke (Zadanie 5) — zapis calej starej `meta` cofnalby
        // `ChosenProposal` zapisane w miedzyczasie. Ta sama zasada, ktora rdzen rundy
        // stosuje przez `meta = projects.Load()` w pierwszej linii.
        projects.Save(Freeze(ProjectStore.WithSpend(projects.Load(), spent)));

        if (gate.Verdict != GateVerdict.Ok)
        {
            // Bez powtorki (ruling 3): RunGate juz rozstrzygnal, czy to sytuacja
            // deterministyczna, a kolejka i tak nie ponawia.
            return new ProposalsOutcome(false, [],
                new JobFailure(FailureHandling.Halted, gate.Cause, 1), spent);
        }

        var propozycje = await CzytajSzkice();
        if (propozycje.Count < 3)
        {
            return new ProposalsOutcome(false, [],
                new JobFailure(FailureHandling.Halted,
                    $"model zapisal {propozycje.Count} z 3 szkicow", 1), spent);
        }

        return new ProposalsOutcome(true, propozycje, null, spent);
    }

    /// Nazwa kierunku to <title> szkicu. Plik bez <title> dostaje nazwe zastepcza —
    /// klient ma wybrac obrazkiem, wiec brak podpisu nie moze wywalac calej sciezki.
    private async Task<IReadOnlyList<Proposal>> CzytajSzkice()
    {
        var wynik = new List<Proposal>();
        foreach (var id in SketchIds)
        {
            var plik = Path.Combine(ProposalsDir, $"{id}.html");
            if (!File.Exists(plik)) continue;

            var html = await File.ReadAllTextAsync(plik);

            // F4: samo File.Exists wystarczalo, zeby policzyc plik do trojki, wiec
            // zerobajtowy c.html dawal Succeeded i trzecia karte z pustym podgladem
            // zamiast czytelnego „model zapisal 2 z 3 szkicow". Warunek celowo bez
            // progu dlugosci: arbitralna liczba jest gorsza niz jej brak. Ma byc
            // jakakolwiek tresc i ma to byc HTML.
            if (html.Trim().Length == 0 || !html.Contains('<')) continue;

            var m = TitleRegex().Match(html);

            // F5: <title> rozbity na kilka linii (zwyczajne w sformatowanym HTML)
            // dawal nazwe z lamaniem linii i wciecien — a ta idzie prosto do JSON-a
            // i do UI. Ta sama regula co dla tekstu uwagi, nie drugi jej wariant.
            var nazwa = m.Success
                ? PromptBuilder.NormalizeWhitespace(m.Groups[1].Value)
                : $"Kierunek {id.ToUpperInvariant()}";

            wynik.Add(new Proposal(id, nazwa, html));
        }
        return wynik;
    }

    [GeneratedRegex(@"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TitleRegex();

    /// Kontrakt 4.1: projekt zatrzymuje PIERWSZY wyczerpany licznik (rundy albo budzet).
    private static ProjectMeta Freeze(ProjectMeta m) =>
        m.RoundsUsed >= m.RoundsLimit || m.SpentUsd >= m.BudgetUsd
            ? m with { Status = ProjectStatus.Frozen }
            : m;

    /// Kontrakt 3.3: status wylacznie z raportu kluczowanego naszym Comment.Id.
    /// Komentarz bez wpisu zostaje open — nie zgadujemy za model.
    public static IReadOnlyList<CommentResult> ParseReport(string resultText, IReadOnlyList<Comment> comments)
    {
        var known = comments.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var results = new List<CommentResult>();

        foreach (var m in ReportRegex().Matches(resultText).Cast<Match>())
        {
            var id = m.Groups[1].Value;
            if (!known.Contains(id)) continue;
            if (results.Any(r => r.CommentId == id)) continue;

            results.Add(new CommentResult(
                id,
                m.Groups[2].Value == "applied" ? CommentStatus.Applied : CommentStatus.Rejected,
                m.Groups[3].Value.Trim()));
        }

        return results;
    }

    /// Kontrakt 4.1 (asymetria liczników): powtorki po awarii ZUZYWAJA budzet —
    /// pieniadze zostaly wydane naprawde — ale NIE zuzywaja rund klienta, bo
    /// awaria po naszej stronie nie moze zabierac klientowi umowionych poprawek.
    /// Dlatego tu jest WithSpend, ale NIGDY WithRoundConsumed, i status wraca
    /// wylacznie przez Freeze — nigdy na sile ustawiany na Active — bo kontrakt
    /// 4.4 wymaga, zeby nieudana runda byla dla klienta brakiem zdarzenia poza
    /// realnie wydanymi pieniedzmi: Versions i RoundsUsed zostaja nietkniete.
    private GenerationOutcome Failed(ProjectMeta meta, FailureHandling handling, string cause, int attempts, decimal spent)
    {
        projects.Save(Freeze(ProjectStore.WithSpend(meta, spent)));
        RestoreWorkDirAfterFailure(meta);
        return new GenerationOutcome(false, null, new JobFailure(handling, cause, attempts), [], spent);
    }

    /// Punkt 4 raportu: petla naprawy kotwic ma kopie zapasowa, petla bramki
    /// TWARDEJ nie miala zadnej — po nieudanej rundzie WorkDir zostawal w stanie,
    /// w jakim akurat przerwal go ostatni przebieg (np. urwany HTML), a NASTEPNA
    /// runda szla --resume po zepsutej stronie. Asymetria byla odwrotna do ryzyka:
    /// chroniona byla rzadka sciezka (naprawa kotwic), niechroniona rutynowa
    /// (kazda nieudana runda). Przywracamy do ostatniej udanej wersji — a gdy
    /// zadna jeszcze nie istnieje, po prostu czyscimy WorkDir do pusta (nie ma do
    /// czego wracac).
    private void RestoreWorkDirAfterFailure(ProjectMeta meta)
    {
        try
        {
            if (meta.Current is null)
            {
                if (Directory.Exists(versions.WorkDir))
                    Directory.Delete(versions.WorkDir, recursive: true);
                Directory.CreateDirectory(versions.WorkDir);
            }
            else
            {
                // Za CurrentVersion, nie za ostatnia: po powrocie do v1 nieudana
                // runda musi zostawic klientowi v1, a nie podmienic mu ja na v2.
                versions.Restore(meta.CurrentVersion);
            }
        }
        catch (Exception)
        {
            // Sprzatanie WorkDir po awarii nie moze przesloniec PRAWDZIWEJ przyczyny
            // awarii (JobFailure, ktory Failed() i tak juz zwraca). NIE zawezaj.
        }
    }

    private IReadOnlyList<string> PreviousAnchors(ProjectMeta meta) =>
        meta.Current is null
            ? []
            : [.. AnchorExtractor.Extract(meta.Current.SnapshotDir)];

    /// Punkt 6 raportu: sygnatura stanu katalogu (sciezki wzgledne + rozmiary +
    /// czasy modyfikacji, posortowane deterministycznie). Rownosc sygnatury PRZED
    /// i PO przebiegu oznacza "ten przebieg nie zmienil ani jednego pliku" —
    /// odrozniamy to od "katalog akurat sie renderuje" (co samo w sobie nie
    /// dowodzi, ze cokolwiek sie wydarzylo w TEJ probie).
    private static string DirectorySignature(string dir)
    {
        if (!Directory.Exists(dir)) return string.Empty;

        var entries = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .Select(f => (Rel: Path.GetRelativePath(dir, f), Info: new FileInfo(f)))
            .OrderBy(e => e.Rel, StringComparer.Ordinal)
            .Select(e => $"{e.Rel}:{e.Info.Length}:{e.Info.LastWriteTimeUtc.Ticks}");

        return string.Join("|", entries);
    }
}
