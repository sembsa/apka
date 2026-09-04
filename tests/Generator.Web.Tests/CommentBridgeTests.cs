using Bunit;
using Generator.Web.Components;
using Generator.Web.Contracts;
using Xunit;

namespace Generator.Web.Tests;

public class CommentBridgeTests : BunitContext
{
    /// <summary>
    /// Sedno poprawki: viewport bierzemy od HOSTA, ktory ustawil tryb podgladu.
    /// Wczesniej liczyl go skrypt w iframe z `window.innerWidth < 768` — a nieostylowany
    /// iframe ma domyslne 300px, wiec KAZDA uwaga szla jako „mobile", takze z monitora
    /// 1920px. Blad byl jednolity, wiec niewidoczny: nikt nie widzial roznicy.
    /// </summary>
    [Theory]
    [InlineData("desktop")]
    [InlineData("mobile")]
    public async Task Viewport_w_uwadze_bierze_sie_z_trybu_hosta(string tryb)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        CommentDto? drafted = null;
        var cut = Render<CommentBridge>(ps => ps
            .Add(p => p.OnCommentDrafted, c => drafted = c)
            .Add(p => p.Viewport, tryb));

        await cut.Instance.OnBlockClicked(new CommentBridge.ClickPayload("hero"));

        Assert.Equal(tryb, drafted!.Viewport);
    }

    [Fact]
    public async Task Nieznany_tryb_hosta_nie_przecieka_do_uwagi()
    {
        // Kontrakt 3.2 zna dwie wartosci. Cokolwiek innego to blad po naszej stronie
        // i nie ma prawa wyladowac w danych klienta.
        JSInterop.Mode = JSRuntimeMode.Loose;
        CommentDto? drafted = null;
        var cut = Render<CommentBridge>(ps => ps
            .Add(p => p.OnCommentDrafted, c => drafted = c)
            .Add(p => p.Viewport, "tablet-poziomo"));

        await cut.Instance.OnBlockClicked(new CommentBridge.ClickPayload("hero"));

        Assert.Equal("desktop", drafted!.Viewport);
    }

    [Fact]
    public async Task Klik_w_blok_tworzy_komentarz_z_anchorem_i_stabilnym_id()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        CommentDto? drafted = null;
        var cut = Render<CommentBridge>(ps => ps
            .Add(p => p.OnCommentDrafted, c => drafted = c));

        await cut.Instance.OnBlockClicked(new CommentBridge.ClickPayload("oferta-strzyzenie"));

        Assert.NotNull(drafted);
        Assert.Equal("oferta-strzyzenie", drafted.Anchor);
        Assert.Equal("desktop", drafted.Viewport);
        Assert.Equal("open", drafted.Status);
        // Kontrakt 3.2 po korekcie: id powstaje RAZ, przy dodaniu komentarza.
        Assert.True(Guid.TryParse(drafted.Id, out _), "Id musi byc GUID-em nadanym przy dodaniu");
        Assert.Null(drafted.Note);
    }

    [Fact]
    public async Task Klik_poza_blokiem_daje_komentarz_globalny_bez_osobnego_typu()
    {
        // Kontrakt 3.2: anchor == null JEST znaczeniem, nie brakiem danych.
        JSInterop.Mode = JSRuntimeMode.Loose;
        CommentDto? drafted = null;
        var cut = Render<CommentBridge>(ps => ps
            .Add(p => p.OnCommentDrafted, c => drafted = c)
            .Add(p => p.Viewport, "mobile"));

        await cut.Instance.OnBlockClicked(new CommentBridge.ClickPayload(null));

        Assert.NotNull(drafted);
        Assert.Null(drafted.Anchor);
        Assert.Equal("mobile", drafted.Viewport);
    }

    [Fact]
    public async Task Anchor_niezgodny_z_formatem_traktujemy_jak_globalny()
    {
        // Format z 3.1: [a-z][a-z0-9-]{0,39}. "Hero" i "oferta 1" nie przechodza.
        JSInterop.Mode = JSRuntimeMode.Loose;
        var drafted = new List<CommentDto>();
        var cut = Render<CommentBridge>(ps => ps
            .Add(p => p.OnCommentDrafted, c => drafted.Add(c)));

        await cut.Instance.OnBlockClicked(new CommentBridge.ClickPayload("Hero"));
        await cut.Instance.OnBlockClicked(new CommentBridge.ClickPayload("oferta 1"));

        Assert.Equal(2, drafted.Count);
        Assert.All(drafted, c => Assert.Null(c.Anchor));
    }

    [Fact]
    public async Task Kazdy_klik_dostaje_wlasne_id()
    {
        // Idempotencja ponowienia stoi na tym, ze id jest stabilne per komentarz,
        // a nie wspoldzielone miedzy komentarzami.
        JSInterop.Mode = JSRuntimeMode.Loose;
        var drafted = new List<CommentDto>();
        var cut = Render<CommentBridge>(ps => ps
            .Add(p => p.OnCommentDrafted, c => drafted.Add(c)));

        await cut.Instance.OnBlockClicked(new CommentBridge.ClickPayload("hero"));
        await cut.Instance.OnBlockClicked(new CommentBridge.ClickPayload("hero"));

        Assert.Equal(2, drafted.Select(c => c.Id).Distinct().Count());
    }

    [Fact]
    public async Task Nie_przenosi_migawki_tresci_bloku()
    {
        // Kontrakt 3.2: swiadomie NIE wysylamy snapshotu tresci.
        JSInterop.Mode = JSRuntimeMode.Loose;
        CommentDto? drafted = null;
        var cut = Render<CommentBridge>(ps => ps
            .Add(p => p.OnCommentDrafted, c => drafted = c));

        await cut.Instance.OnBlockClicked(new CommentBridge.ClickPayload("hero"));

        var pola = typeof(CommentDto).GetProperties().Select(p => p.Name).ToArray();
        Assert.DoesNotContain("Snapshot", pola);
        Assert.DoesNotContain("BlockHtml", pola);
        Assert.NotNull(drafted);
    }
}
