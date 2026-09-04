using Generator.Web.Contracts;
using Generator.Web.Mock;
using Xunit;

namespace Generator.Web.Tests;

public class MockProjectApiTests
{
    private static MockProjectApi Api() => new() { SimulatedDelay = TimeSpan.Zero };

    private static CommentDto C(string id, string? anchor, string text) =>
        new(id, anchor, text, "desktop", DateTimeOffset.UtcNow, "open", null);

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
        api.NextJobOutcome = MockJobOutcome.Halted;
        var p = await api.CreateAsync("idea", "x");

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
        api.NextJobOutcome = MockJobOutcome.Halted;
        var p = await api.CreateAsync("idea", "x");
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

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => api.ApplyCommentsAsync(p.Id, 1, [C("c1", null, "x")]));
    }

    [Fact]
    public async Task Udana_runda_zwraca_note_przy_kazdym_komentarzu()
    {
        var api = Api();
        var p = await api.CreateAsync("idea", "x");
        var jobId = await api.ApplyCommentsAsync(p.Id, 1, [C("c1", "hero", "telefon za maly")]);
        await api.GetJobAsync(jobId);

        var comments = await api.GetCommentsAsync(p.Id, 1);
        var c = Assert.Single(comments);
        Assert.Equal("applied", c.Status);
        Assert.False(string.IsNullOrWhiteSpace(c.Note));
    }
}
