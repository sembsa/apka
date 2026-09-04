using Bunit;
using Generator.Web.Components.Shared;
using Xunit;

namespace Generator.Web.Tests;

/// <summary>
/// Sprawdza wylacznie to, ze bUnit renderuje NASZ komponent w tym projekcie.
/// Bez tego zepsuta uprzaz wyszlaby dopiero w Tasku 2, wsrod testow logiki.
/// Uwaga na wersje: bUnit 2.x ma BunitContext i Render&lt;T&gt;() — TestContext
/// i RenderComponent&lt;T&gt;() z tutoriali 1.x nie kompiluja sie (CS0619).
/// </summary>
public class HarnessTests : BunitContext
{
    [Fact]
    public void Uprzaz_renderuje_komponent()
    {
        var cut = Render<LicznikRund>(p => p
            .Add(x => x.Uzyte, 12)
            .Add(x => x.Limit, 15));

        Assert.Equal("zostały 3 poprawki", cut.Find("[data-test='licznik']").TextContent.Trim());
    }

    [Fact]
    public void Licznik_nie_pokazuje_budzetu()
    {
        // Kontrakt 4.1: licznik rund jest dla klienta, budzet tylko dla nas.
        var cut = Render<LicznikRund>(p => p.Add(x => x.Uzyte, 1).Add(x => x.Limit, 15));

        Assert.DoesNotContain("$", cut.Markup);
        Assert.DoesNotContain("usd", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }
}
