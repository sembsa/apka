using Generator.Web.Api;
using Generator.Web.Contracts;
using Generator.Web.Mock;
using Xunit;

namespace Generator.Web.Tests;

public class MockProjectApiTests
{
    private static MockProjectApi Api() => new() { SimulatedDelay = TimeSpan.Zero };

    private static CommentDto C(string id, string? anchor, string text) =>
        new(id, anchor, text, "desktop", DateTimeOffset.UtcNow, "open", null);


    /// <summary>
    /// Ostatnia znana rozbieznosc atrapy (Sebastian zostawil ja do decyzji). Prawdziwe
    /// `ProjectApi` rozdziela: wybor tylko zapisuje kierunek, a wersje 1 buduje OSOBNE
    /// zadanie `CreateFirstVersionAsync` — bo generacja trwa minuty. Atrapa robila to
    /// odwrotnie: wersje tworzyl wybor, a `CreateFirstVersionAsync` nie robilo nic.
    ///
    /// Roznica jest niewidoczna, dopoki UI wola obie metody po kolei — ale test
    /// komponentu, ktory zawola samo `Choose`, przechodzi pod atrapa i klamie
    /// o produkcji. Dokladnie ta klasa bledu, ktora kosztowala nas juz cztery pomylki.
    /// </summary>
    [Fact]
    public async Task Wybor_propozycji_NIE_tworzy_wersji_tak_jak_w_prawdziwym_api()
    {
        var api = Api();
        var p = await api.CreateAsync("idea", "Fryzjer");

        // Sam wybor. BEZ CreateFirstVersionAsync — o to w tym tescie chodzi.
        await api.ChooseProposalAsync(p.Id, "a");

        Assert.Empty((await api.GetAsync(p.Id)).Versions);
        Assert.Equal(0, (await api.GetAsync(p.Id)).CurrentVersion);
    }

    [Fact]
    public async Task Wersje_pierwsza_tworzy_dopiero_CreateFirstVersion()
    {
        var api = Api();
        var p = await api.CreateAsync("idea", "Fryzjer");
        await api.ChooseProposalAsync(p.Id, "a");
        await api.CreateFirstVersionAsync(p.Id);

        var po = await api.GetAsync(p.Id);
        Assert.Equal(1, po.CurrentVersion);
        Assert.Equal(1, Assert.Single(po.Versions).Number);
    }

    [Fact]
    public async Task Wersji_pierwszej_nie_da_sie_zbudowac_bez_wyboru()
    {
        // Prawdziwe API rzuca InvalidOperationException („klient nie wybral propozycji").
        var api = Api();
        var p = await api.CreateAsync("idea", "Fryzjer");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => api.CreateFirstVersionAsync(p.Id));
    }

    [Fact]
    public async Task Druga_wersja_pierwsza_jest_odrzucana()
    {
        // Straz z prawdziwego API: wersje pierwsza generuje sie tylko raz, kolejne
        // zmiany ida przez runde uwag. Bez tego powrot strzalka „wstecz" budowal
        // klientowi druga wersje pierwsza za pieniadze.
        var api = Api();
        var p = await api.CreateAsync("idea", "Fryzjer");
        await api.ChooseProposalAsync(p.Id, "a");
        await api.CreateFirstVersionAsync(p.Id);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => api.CreateFirstVersionAsync(p.Id));
    }
    [Fact]
    public async Task Nowy_projekt_ma_token_i_liczniki_z_kontraktu()
    {
        var p = await Api().CreateAsync("idea", "Fryzjer w Nowym Saczu");

        Assert.NotEmpty(p.Token);
        Assert.Equal(0, p.RoundsUsed);
        Assert.Equal(15, p.RoundsLimit);
        Assert.Empty(p.Versions);
    }

    [Fact]
    public async Task Runda_zwraca_jobId_a_nie_gotowa_wersje()
    {
        // Kontrakt 1: wszystko, co uruchamia model, jest zadaniem.
        var api = Api();
        var p = await api.CreateAsync("idea", "x");
        await api.ChooseProposalAsync(p.Id, "a");   // runda potrzebuje wersji biezacej (5.1)
        await api.CreateFirstVersionAsync(p.Id);   // wersja 1 to OSOBNE zadanie (jak w ProjectApi)

        var jobId = await api.ApplyCommentsAsync(p.Id, 1, [C("c1", "hero", "telefon za maly")]);

        Assert.NotEmpty(jobId);
        var job = await api.GetJobAsync(jobId);
        Assert.Contains(job.Status, new[] { "queued", "running", "succeeded" });
    }

    [Fact]
    public async Task Halted_niesie_handling_ale_nie_przyczyne()
    {
        // Kontrakt 4.3: cause NIE idzie do UI.
        var api = Api();
        var p = await api.CreateAsync("idea", "x");
        await api.ChooseProposalAsync(p.Id, "a");   // runda potrzebuje wersji biezacej (5.1)
        await api.CreateFirstVersionAsync(p.Id);   // wersja 1 to OSOBNE zadanie (jak w ProjectApi)
        api.NextJobOutcome = MockJobOutcome.Halted;

        var jobId = await api.ApplyCommentsAsync(p.Id, 1, [C("c1", null, "x")]);
        var job = await api.GetJobAsync(jobId);

        Assert.Equal("failed", job.Status);
        Assert.Equal("halted", job.Failure!.Handling);
    }

    [Fact]
    public async Task Nieudana_runda_nie_zuzywa_rundy_i_zostawia_komentarze_open()
    {
        // Kontrakt 4.4: z punktu widzenia klienta to BRAK ZDARZENIA.
        var api = Api();
        var p = await api.CreateAsync("idea", "x");
        await api.ChooseProposalAsync(p.Id, "a");   // runda potrzebuje wersji biezacej (5.1)
        await api.CreateFirstVersionAsync(p.Id);   // wersja 1 to OSOBNE zadanie (jak w ProjectApi)
        api.NextJobOutcome = MockJobOutcome.Halted;
        await api.ApplyCommentsAsync(p.Id, 1, [C("c1", null, "x")]);

        var after = await api.GetAsync(p.Id);
        var comments = await api.GetCommentsAsync(p.Id, 1);

        Assert.Equal(0, after.RoundsUsed);
        Assert.All(comments, c => Assert.Equal("open", c.Status));
    }

    [Fact]
    public async Task Wyczerpanie_rund_zamraza_projekt_a_apply_odmawia()
    {
        // Kontrakt 4.5: frozen, podglad dziala, nowe rundy odrzucane.
        var api = Api();
        var p = await api.CreateAsync("idea", "x");
        api.ForceFrozen(p.Id);

        var frozen = await api.GetAsync(p.Id);
        Assert.Equal("frozen", frozen.Status);

        // Konkretny typ, nie InvalidOperationException: `Editor.Kolizja` wymienia
        // typy po nazwie, wiec goly wyjatek bazowy przez nia NIE przechodzil
        // i pod atrapa zamrozenie zrywalo obwod zamiast pokazac zdanie.
        await Assert.ThrowsAsync<ProjectFrozenException>(
            () => api.ApplyCommentsAsync(p.Id, 1, [C("c1", null, "x")]));
    }

    [Fact]
    public async Task Udana_runda_zwraca_note_przy_kazdym_komentarzu()
    {
        var api = Api();
        var p = await api.CreateAsync("idea", "x");
        await api.ChooseProposalAsync(p.Id, "a");   // runda potrzebuje wersji biezacej (5.1)
        await api.CreateFirstVersionAsync(p.Id);   // wersja 1 to OSOBNE zadanie (jak w ProjectApi)
        var jobId = await api.ApplyCommentsAsync(p.Id, 1, [C("c1", "hero", "telefon za maly")]);
        await api.GetJobAsync(jobId);

        var comments = await api.GetCommentsAsync(p.Id, 1);
        var c = Assert.Single(comments);
        Assert.Equal("applied", c.Status);
        Assert.False(string.IsNullOrWhiteSpace(c.Note));
    }

    [Fact]
    public async Task Atrapa_odmawia_tymi_samymi_typami_wyjatkow_co_prawdziwe_API()
    {
        // `Editor.Kolizja` i `Proposals.Kolizja` rozpoznaja odmowe PO TYPIE. Atrapa
        // rzucala ArgumentOutOfRangeException i goly InvalidOperationException albo
        // nie rzucala wcale, wiec pod nia te same klikniecia zrywaly obwod, a dwie
        // z czterech galezi obslugi odmowy nie mialy jak zostac dotkniete.
        var api = Api();
        var p = await api.CreateAsync("idea", "x");
        await api.ChooseProposalAsync(p.Id, "a");
        await api.CreateFirstVersionAsync(p.Id);   // wersja 1 to OSOBNE zadanie (jak w ProjectApi)

        // Szkic spoza bialej listy silnika (a|b|c) — glosno, nie cicho.
        await Assert.ThrowsAsync<KeyNotFoundException>(() => api.ChooseProposalAsync(p.Id, "p-1"));

        // Wersja, ktorej nie ma: KeyNotFoundException, nie ArgumentOutOfRangeException.
        await Assert.ThrowsAsync<KeyNotFoundException>(() => api.RollbackAsync(p.Id, 7));

        // Uwagi do wersji innej niz biezaca (kontrakt 5.1).
        await Assert.ThrowsAsync<StaleVersionException>(
            () => api.ApplyCommentsAsync(p.Id, 99, [C("c1", null, "x")]));

        // Zamrozenie blokuje takze zamowienie szkicow (4.5).
        api.ForceFrozen(p.Id);
        await Assert.ThrowsAsync<ProjectFrozenException>(() => api.RequestProposalsAsync(p.Id));
    }

    [Fact]
    public async Task Atrapa_umie_powiedziec_ze_zadanie_juz_trwa()
    {
        // Czwarty typ odmowy. Bez niego dwuklik „Popraw to" na atrapie przechodzil
        // obojetnie, a na prawdziwym API konczyl sie JobRunningException — czyli
        // zachowanie, ktorego nie dalo sie ani przeklikac, ani przetestowac.
        var api = new MockProjectApi { SimulatedDelay = TimeSpan.FromMilliseconds(200) };
        var p = await api.CreateAsync("idea", "x");
        await api.ChooseProposalAsync(p.Id, "a");
        await api.CreateFirstVersionAsync(p.Id);   // wersja 1 to OSOBNE zadanie (jak w ProjectApi)

        var pierwsza = api.ApplyCommentsAsync(p.Id, 1, [C("c1", null, "raz")]);

        await Assert.ThrowsAsync<JobRunningException>(
            () => api.ApplyCommentsAsync(p.Id, 1, [C("c2", null, "dwa")]));
        await Assert.ThrowsAsync<JobRunningException>(() => api.RollbackAsync(p.Id, 1));

        await pierwsza;

        // Po zakonczeniu rezerwacja znika — inaczej projekt odmawialby juz na zawsze.
        await api.RollbackAsync(p.Id, 1);
    }
}
