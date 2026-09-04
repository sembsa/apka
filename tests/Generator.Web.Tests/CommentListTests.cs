using Bunit;
using Generator.Web.Components;
using Generator.Web.Contracts;
using Xunit;

namespace Generator.Web.Tests;

public class CommentListTests : BunitContext
{
    private static CommentDto C(string id, string status, string? note, string? anchor = "hero") =>
        new(id, anchor, $"uwaga {id}", "desktop", DateTimeOffset.UnixEpoch, status, note);

    [Fact]
    public void Note_jest_przy_komentarzu_nie_w_zbiorczym_podsumowaniu()
    {
        // Kontrakt 3.3 i sekcja 4 planu: klient musi wiedziec, ze zostal zrozumiany.
        var cut = Render<CommentList>(ps => ps
            .Add(p => p.Comments, [C("c1", "applied", "Powiekszylem telefon w naglowku.")]));

        var item = cut.Find("[data-test='komentarz-c1']");
        Assert.Contains("Powiekszylem telefon", item.TextContent);
    }

    [Fact]
    public void Rejected_pokazuje_uzasadnienie_i_nie_wyglada_jak_awaria()
    {
        var cut = Render<CommentList>(ps => ps
            .Add(p => p.Comments, [C("c1", "rejected", "Nie mam zdjecia, ktorym moglbym to zastapic.")]));

        var item = cut.Find("[data-test='komentarz-c1']");
        Assert.Contains("Nie mam zdjecia", item.TextContent);
        Assert.DoesNotContain("błąd", cut.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("awaria", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Wycofac_mozna_tylko_komentarz_open()
    {
        // Kontrakt 3.3: frontend umie wycofac, dopoki open. Statusu nie dotyka.
        var cut = Render<CommentList>(ps => ps
            .Add(p => p.Comments, [C("c1", "open", null), C("c2", "applied", "zrobione")]));

        Assert.NotNull(cut.Find("[data-test='wycofaj-c1']"));
        Assert.Empty(cut.FindAll("[data-test='wycofaj-c2']"));
    }

    [Fact]
    public void Komentarz_globalny_jest_opisany_jako_dotyczacy_calosci()
    {
        var cut = Render<CommentList>(ps => ps
            .Add(p => p.Comments, [C("c1", "open", null, anchor: null)]));

        Assert.Contains("cała strona", cut.Find("[data-test='komentarz-c1']").TextContent);
    }

    [Fact]
    public void Kolejnosc_wyswietlania_to_kolejnosc_dodawania()
    {
        // Kontrakt 3.2: przy sprzecznych uwagach obowiazuje OSTATNIA — klient musi
        // widziec, ktora to jest.
        var cut = Render<CommentList>(ps => ps
            .Add(p => p.Comments, [C("c1", "open", null), C("c2", "open", null)]));

        var markup = cut.Markup;
        Assert.True(markup.IndexOf("komentarz-c1", StringComparison.Ordinal)
                  < markup.IndexOf("komentarz-c2", StringComparison.Ordinal));
    }
}
