using Bunit;
using Generator.Web.Components.Pages;
using Generator.Web.Api;
using Generator.Web.Contracts;
using Generator.Web.Mock;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Generator.Web.Tests;

public class ProposalsPageTests : BunitContext
{
    private async Task<(MockProjectApi api, string projectId)> Setup()
    {
        var api = new MockProjectApi { SimulatedDelay = TimeSpan.Zero };
        Services.AddSingleton<IProjectApi>(api);
        var p = await api.CreateAsync("idea", "Fryzjer");
        return (api, p.Id);
    }

    [Fact]
    public async Task Pokazuje_trzy_propozycje_jako_podglady_w_iframe()
    {
        var (_, id) = await Setup();
        var cut = Render<Proposals>(ps => ps.Add(p => p.ProjectId, id));
        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll("iframe").Count));
    }

    [Fact]
    public async Task Wybor_to_jedno_klikniecie_bez_pisania()
    {
        var (_, id) = await Setup();
        var cut = Render<Proposals>(ps => ps.Add(p => p.ProjectId, id));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-test='wybierz']")));

        Assert.Empty(cut.FindAll("textarea"));
        Assert.Empty(cut.FindAll("input[type='text']"));
    }

    [Fact]
    public async Task Wybor_zamawia_wersje_pierwsza_osobnym_zadaniem()
    {
        // Atrapa tworzy wersje 1 juz przy wyborze, prawdziwe ProjectApi juz NIE —
        // generacja trwa minuty, wiec musi byc zadaniem (IProjectApi.CreateFirstVersionAsync).
        // Bez tego wywolania edytor otwiera sie z pusta lista wersji i klient nie ma
        // w co kliknac. Na samej atrapie tego nie widac: dlatego szpieg, nie asercja
        // na stanie projektu.
        var szpieg = new Szpieg(new MockProjectApi { SimulatedDelay = TimeSpan.Zero });
        Services.AddSingleton<IProjectApi>(szpieg);
        var p = await szpieg.CreateAsync("idea", "Fryzjer");

        var cut = Render<Proposals>(ps => ps.Add(x => x.ProjectId, p.Id));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-test='wybierz']")));
        cut.Find("[data-test='wybierz']").Click();

        cut.WaitForAssertion(() => Assert.Equal(
            [nameof(IProjectApi.ChooseProposalAsync), nameof(IProjectApi.CreateFirstVersionAsync)],
            szpieg.Wywolania.Where(w => w != nameof(IProjectApi.RequestProposalsAsync)
                                     && w != nameof(IProjectApi.GetProposalsAsync))));
    }

    [Fact]
    public async Task Propozycje_pokazuja_sie_dopiero_po_skonczonym_zadaniu()
    {
        // Kontrakt 1: propozycje to ZADANIE. Prawdziwy silnik oddaje `jobId` od reki,
        // a szkice ladują na dysku minute pozniej — pytanie o nie od razu dawaloby
        // PUSTA liste i klienta bez czego wybierac. Atrapa konczy zadanie, zanim je
        // odda, wiec sama tego nie pokaze.
        var szpieg = new Szpieg(new MockProjectApi { SimulatedDelay = TimeSpan.Zero })
        {
            ZadanieTrwa = true,
        };
        Services.AddSingleton<IProjectApi>(szpieg);
        var p = await szpieg.CreateAsync("idea", "Fryzjer");

        var cut = Render<Proposals>(ps => ps
            .Add(x => x.ProjectId, p.Id)
            .Add(x => x.PollInterval, TimeSpan.FromMilliseconds(10)));

        cut.WaitForAssertion(() => Assert.Contains("Przygotowuję", cut.Markup));
        Assert.Empty(cut.FindAll("iframe"));

        // JEDNO pytanie o szkice — to sonda „czy jest co pokazac" sprzed zamowienia.
        // Drugie, czyli ODBIOR wyniku, nie ma prawa paść, dopóki zadanie trwa:
        // prawdziwy silnik oddaje wtedy pusta liste i klient nie ma czego wybierac.
        Assert.Equal(1, szpieg.Wywolania.Count(w => w == nameof(IProjectApi.GetProposalsAsync)));

        szpieg.ZadanieTrwa = false;
        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll("iframe").Count));
    }

    [Fact]
    public async Task Powrot_na_strone_szkicow_nie_zamawia_drugiego_kompletu()
    {
        // Klient wracajacy strzalka „wstecz" z edytora placil ~$0,50 za nowy komplet
        // szkicow, ktorego nie zamawial — a `RunProposalsAsync` przy okazji kasuje
        // `proposals/` i zeruje `ChosenProposal`, wiec powrot wycieral mu wybor.
        var szpieg = new Szpieg(new MockProjectApi { SimulatedDelay = TimeSpan.Zero });
        Services.AddSingleton<IProjectApi>(szpieg);
        var p = await szpieg.CreateAsync("idea", "Fryzjer");

        // Pierwsze wejscie: komplet powstaje i zostaje na dysku.
        await szpieg.RequestProposalsAsync(p.Id);
        szpieg.Wywolania.Clear();

        var cut = Render<Proposals>(ps => ps.Add(x => x.ProjectId, p.Id));

        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll("iframe").Count));
        Assert.DoesNotContain(nameof(IProjectApi.RequestProposalsAsync), szpieg.Wywolania);
    }

    [Fact]
    public async Task Odmowa_przy_wejsciu_pokazuje_zdanie_zamiast_zrywac_obwod()
    {
        // Druga karta otwarta w trakcie generacji dostawala `JobRunningException`
        // prosto z `OnInitializedAsync` — a wyjatek stamtad KONCZY OBWOD: klient
        // widzi pasek „polaczenie zerwane" zamiast zdania o tym, co sie dzieje.
        var szpieg = new Szpieg(new MockProjectApi { SimulatedDelay = TimeSpan.Zero })
        {
            RzucaPrzyZamowieniu = new JobRunningException("p"),
        };
        Services.AddSingleton<IProjectApi>(szpieg);
        var p = await szpieg.CreateAsync("idea", "Fryzjer");

        var cut = Render<Proposals>(ps => ps.Add(x => x.ProjectId, p.Id));

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-test='kolizja']")));
        Assert.Equal("alert", cut.Find("[data-test='kolizja']").GetAttribute("role"));
        // „Przygotowuję…" pod komunikatem o odmowie byloby klamstwem — nic sie nie liczy.
        Assert.DoesNotContain("Przygotowuję", cut.Markup);
    }

    [Fact]
    public async Task Odmowa_przy_wyborze_pokazuje_zdanie_i_nie_zostawia_buduje()
    {
        // Klik w „Ta mi sie podoba" nie mial ZADNEGO try/catch: kazda odmowa
        // prawdziwego API (trwajace zadanie, zamrozony projekt, szkic ktorego juz
        // nie ma) wychodzila z uchwytu zdarzenia i zabijala obwod.
        var szpieg = new Szpieg(new MockProjectApi { SimulatedDelay = TimeSpan.Zero })
        {
            RzucaPrzyWyborze = new JobRunningException("p"),
        };
        Services.AddSingleton<IProjectApi>(szpieg);
        var p = await szpieg.CreateAsync("idea", "Fryzjer");

        var cut = Render<Proposals>(ps => ps.Add(x => x.ProjectId, p.Id));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-test='wybierz']")));
        cut.Find("[data-test='wybierz']").Click();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("[data-test='kolizja']")));
        // Strona nie moze zostac na „Buduję Twoją stronę…", skoro nic sie nie buduje.
        Assert.Empty(cut.FindAll("[data-test='buduje']"));
        Assert.NotEmpty(cut.FindAll("[data-test='wybierz']"));
    }

    [Fact]
    public async Task Podczas_generowania_widac_ze_cos_sie_dzieje()
    {
        var api = new MockProjectApi { SimulatedDelay = TimeSpan.FromMilliseconds(300) };
        Services.AddSingleton<IProjectApi>(api);
        var p = await api.CreateAsync("idea", "x");

        var cut = Render<Proposals>(ps => ps.Add(x => x.ProjectId, p.Id));

        Assert.Contains("Przygotowuję", cut.Markup);
    }

    /// <summary>
    /// Atrapa z notatnikiem: zapisuje, co strona zamowila, i umie udawac zadanie,
    /// ktore JESZCZE trwa. Sama MockProjectApi tego nie potrafi — konczy kazde
    /// zadanie w chwili jego powstania, wiec kod czekajacy na wynik i kod, ktory
    /// wcale nie czeka, wygladaja na niej identycznie.
    /// </summary>
    [Fact]
    public async Task Klient_z_gotowa_strona_wraca_do_edytora_zamiast_w_slepy_zaulek()
    {
        // Powrot strzalka „wstecz" z edytora. Szkice nadal leza na dysku, wiec strona
        // pokazywala wybor; klik po cichu nadpisywal wybrany kierunek, a
        // CreateFirstVersionAsync odmawiala. Klient zostawal z czerwonym zdaniem
        // i BEZ DROGI POWROTU — MainLayout nie ma nawigacji, a tu nie ma linku.
        // Atrapa tego nie pokazywala, bo jej odpowiedniki nie maja strazy.
        var atrapa = new MockProjectApi { SimulatedDelay = TimeSpan.Zero };
        var szpieg = new Szpieg(atrapa);
        Services.AddSingleton<IProjectApi>(szpieg);

        var p = await atrapa.CreateAsync("idea", "Fryzjer");
        await atrapa.ChooseProposalAsync(p.Id, "a");     // atrapa tworzy tu wersje 1

        Render<Proposals>(ps => ps.Add(x => x.ProjectId, p.Id));

        var nav = Services.GetRequiredService<NavigationManager>();
        Assert.Contains($"/editor/{p.Id}", nav.Uri);

        // I ani grosza: zadne zamowienie nie poszlo.
        Assert.DoesNotContain(nameof(IProjectApi.RequestProposalsAsync), szpieg.Wywolania);
    }

    [Fact]
    public async Task Niepelny_komplet_szkicow_strona_zamawia_ponownie()
    {
        // Silnik potrafi zwrocic zadanie nieudane, ktore zapisalo 2 z 3 szkicow.
        // Gdyby strona uznala dwie karty za komplet, klient wybieralby z dwoch
        // i nigdy nie dostal trzeciej — a straz po stronie API by tego nie uratowala,
        // bo strona w ogole by jej nie zapytala.
        var atrapa = new MockProjectApi { SimulatedDelay = TimeSpan.Zero };
        var szpieg = new Szpieg(atrapa) { IleSzkicow = 2 };
        Services.AddSingleton<IProjectApi>(szpieg);

        var p = await atrapa.CreateAsync("idea", "Fryzjer");

        // Szkice MUSZA juz istniec w atrapie, zanim strona zapyta — inaczej pierwszy
        // odczyt oddaje zero, oba warunki (`== 3` i `> 0`) sa falszywe, strona zamawia
        // i test przechodzi niezaleznie od tego, ktory z nich jest w kodzie.
        // Tak wlasnie napisalem go za pierwszym razem i mutacja przezyla.
        await atrapa.RequestProposalsAsync(p.Id);
        szpieg.Wywolania.Clear();

        Render<Proposals>(ps => ps.Add(x => x.ProjectId, p.Id));

        Assert.Contains(nameof(IProjectApi.RequestProposalsAsync), szpieg.Wywolania);
    }

    private sealed class Szpieg(MockProjectApi atrapa) : IProjectApi
    {
        public List<string> Wywolania { get; } = [];
        public bool ZadanieTrwa { get; set; }

        /// Odmowa `ProjectApi` w dwoch miejscach, w ktorych strona ja realnie dostaje.
        /// Osobne pola, bo test wyboru musi NAJPIERW dostac szkice, zeby mial w co kliknac.
        public Exception? RzucaPrzyZamowieniu { get; set; }
        public Exception? RzucaPrzyWyborze { get; set; }

        public Task<ProjectView> CreateAsync(string source, string description) =>
            atrapa.CreateAsync(source, description);

        public Task<ProjectView> GetAsync(string id) => atrapa.GetAsync(id);

        public Task<string> RequestProposalsAsync(string id)
        {
            Wywolania.Add(nameof(RequestProposalsAsync));
            if (RzucaPrzyZamowieniu is not null) throw RzucaPrzyZamowieniu;
            return atrapa.RequestProposalsAsync(id);
        }

        /// Ile szkicow oddac zamiast tego, co ma atrapa. Atrapa umie tylko 0 albo 3,
        /// wiec stanu „2 z 3" — czyli zadania, ktore zapisalo niepelny komplet —
        /// nie da sie bez tego odtworzyc. A to wlasnie ten stan rozstrzyga, czy strona
        /// liczy komplet, czy „cokolwiek".
        public int? IleSzkicow { get; set; }

        public async Task<IReadOnlyList<ProposalView>> GetProposalsAsync(string id)
        {
            Wywolania.Add(nameof(GetProposalsAsync));
            var wszystkie = await atrapa.GetProposalsAsync(id);
            return IleSzkicow is int ile ? [.. wszystkie.Take(ile)] : wszystkie;
        }

        public Task ChooseProposalAsync(string id, string proposalId)
        {
            Wywolania.Add(nameof(ChooseProposalAsync));
            if (RzucaPrzyWyborze is not null) throw RzucaPrzyWyborze;
            return atrapa.ChooseProposalAsync(id, proposalId);
        }

        public Task<string> CreateFirstVersionAsync(string id)
        {
            Wywolania.Add(nameof(CreateFirstVersionAsync));
            return atrapa.CreateFirstVersionAsync(id);
        }

        public Task<string> ApplyCommentsAsync(
            string id, int version, IReadOnlyList<CommentDto> comments) =>
            atrapa.ApplyCommentsAsync(id, version, comments);

        public async Task<JobView> GetJobAsync(string jobId) =>
            ZadanieTrwa ? new JobView(jobId, "running", null) : await atrapa.GetJobAsync(jobId);

        public Task RollbackAsync(string id, int version) => atrapa.RollbackAsync(id, version);

        public Task<IReadOnlyList<CommentDto>> GetCommentsAsync(string id, int version) =>
            atrapa.GetCommentsAsync(id, version);
        public Task SaveCommentsAsync(string id, int version, IReadOnlyList<CommentDto> comments) =>
            atrapa.SaveCommentsAsync(id, version, comments);
    }
}
