using Generator.Engine.Model;
using Generator.Engine.Prompts;
using Xunit;

namespace Generator.Engine.Tests;

public class PromptBuilderTests
{
    private static Comment C(string id, string? anchor, string text, string viewport = "desktop") =>
        new(id, anchor, text, viewport, DateTimeOffset.UnixEpoch);

    [Fact]
    public void Runda_zawiera_liste_id_z_poprzedniej_wersji()
    {
        // Kontrakt 3.1: "zachowaj data-cmt-id" bez listy jest instrukcja bez przedmiotu.
        var prompt = PromptBuilder.BuildRound(
            [C("c1", "hero", "telefon za maly")],
            ["hero", "cennik", "kontakt"]);

        Assert.Contains("hero", prompt);
        Assert.Contains("cennik", prompt);
        Assert.Contains("kontakt", prompt);
        Assert.Contains("data-cmt-id", prompt);
    }

    [Fact]
    public void Runda_zachowuje_kolejnosc_komentarzy_klienta()
    {
        // Kontrakt 3.2: sprzeczne komentarze rozstrzyga OSTATNI.
        var prompt = PromptBuilder.BuildRound(
            [C("c1", null, "cennik nad opiniami"), C("c2", null, "opinie nad cennikiem")],
            []);

        var first = prompt.IndexOf("cennik nad opiniami", StringComparison.Ordinal);
        var second = prompt.IndexOf("opinie nad cennikiem", StringComparison.Ordinal);
        Assert.True(first < second, "kolejnosc komentarzy nie zostala zachowana");
    }

    [Fact]
    public void Runda_zada_raportu_per_commentId()
    {
        // Kontrakt 3.3: status NIE z prozy, tylko z raportu kluczowanego naszym id.
        var prompt = PromptBuilder.BuildRound([C("c-abc", "hero", "x")], ["hero"]);

        Assert.Contains("c-abc", prompt);
        Assert.Contains("applied", prompt);
        Assert.Contains("rejected", prompt);
    }

    [Fact]
    public void Komentarz_globalny_jest_oznaczony_jako_dotyczacy_calosci()
    {
        var prompt = PromptBuilder.BuildRound([C("c1", null, "calosc za ciemna")], ["hero"]);
        Assert.Contains("cala strona", prompt);
    }

    [Fact]
    public void Komentarz_przenosi_viewport()
    {
        var prompt = PromptBuilder.BuildRound([C("c1", "kontakt", "telefon nie widac", "mobile")], ["kontakt"]);
        Assert.Contains("mobile", prompt);
    }

    [Fact]
    public void Naprawa_kotwic_wymienia_brakujace_id()
    {
        var prompt = PromptBuilder.BuildAnchorRepair(["cennik", "hero"]);
        Assert.Contains("cennik", prompt);
        Assert.Contains("hero", prompt);
    }

    [Fact]
    public void Brief_przenosi_opis_klienta()
    {
        var prompt = PromptBuilder.BuildBrief("Fryzjer w Nowym Saczu, strzyzenie i broda");
        Assert.Contains("Fryzjer w Nowym Saczu", prompt);
        Assert.Contains("data-cmt-id", prompt);
    }
}
