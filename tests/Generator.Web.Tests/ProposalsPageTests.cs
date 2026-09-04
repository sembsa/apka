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
    public async Task Podczas_generowania_widac_ze_cos_sie_dzieje()
    {
        var api = new MockProjectApi { SimulatedDelay = TimeSpan.FromMilliseconds(300) };
        Services.AddSingleton<IProjectApi>(api);
        var p = await api.CreateAsync("idea", "x");

        var cut = Render<Proposals>(ps => ps.Add(x => x.ProjectId, p.Id));

        Assert.Contains("Przygotowuję", cut.Markup);
    }
}
