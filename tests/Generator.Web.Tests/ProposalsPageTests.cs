using Bunit;
using Generator.Web.Components.Pages;
using Generator.Web.Contracts;
using Generator.Web.Mock;
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
        Assert.DoesNotContain(nameof(IProjectApi.GetProposalsAsync), szpieg.Wywolania);

        szpieg.ZadanieTrwa = false;
        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll("iframe").Count));
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
    private sealed class Szpieg(MockProjectApi atrapa) : IProjectApi
    {
        public List<string> Wywolania { get; } = [];
        public bool ZadanieTrwa { get; set; }

        public Task<ProjectView> CreateAsync(string source, string description) =>
            atrapa.CreateAsync(source, description);

        public Task<ProjectView> GetAsync(string id) => atrapa.GetAsync(id);

        public Task<string> RequestProposalsAsync(string id)
        {
            Wywolania.Add(nameof(RequestProposalsAsync));
            return atrapa.RequestProposalsAsync(id);
        }

        public Task<IReadOnlyList<ProposalView>> GetProposalsAsync(string id)
        {
            Wywolania.Add(nameof(GetProposalsAsync));
            return atrapa.GetProposalsAsync(id);
        }

        public Task ChooseProposalAsync(string id, string proposalId)
        {
            Wywolania.Add(nameof(ChooseProposalAsync));
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
    }
}
