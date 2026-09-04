using Bunit;
using Generator.Web.Components;
using Generator.Web.Contracts;
using Xunit;

namespace Generator.Web.Tests;

public class JobStatusTests : BunitContext
{
    [Fact]
    public void Running_pokazuje_ze_pracujemy_bez_szczegolow_technicznych()
    {
        var cut = Render<JobStatus>(ps => ps
            .Add(p => p.Job, new JobView("j1", "running", null)));

        Assert.Contains("pracuj", cut.Markup, StringComparison.OrdinalIgnoreCase);

        // Plan zadania mial tu tez DoesNotContain("claude") — usuniete, bo sekcja 7
        // planu produktu WPROST przewiduje komunikat "Claude pracuje nad Twoja strona".
        // Nazwa modelu nie jest wyciekiem technicznym; wyciekiem sa ponizsze.
        foreach (var techniczne in new[] { "token", "exit", "stdout", "permission", "session" })
            Assert.DoesNotContain(techniczne, cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Retrying_wyglada_dla_klienta_jak_dalsza_praca()
    {
        // Kontrakt 4.3: powtorki sa NIEWIDOCZNE dla klienta.
        var cut = Render<JobStatus>(ps => ps
            .Add(p => p.Job, new JobView("j1", "failed", new JobFailureView("retrying", 1))));

        Assert.Contains("pracuj", cut.Markup, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(cut.FindAll("[data-test='ponow']"));
    }

    [Fact]
    public void Halted_mowi_ze_problem_jest_u_nas_i_nie_ma_sensu_klikac()
    {
        var cut = Render<JobStatus>(ps => ps
            .Add(p => p.Job, new JobView("j1", "failed", new JobFailureView("halted", 2))));

        var text = cut.Markup;
        Assert.Contains("u nas", text);
        Assert.Contains("nic nie przepadło", text, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(cut.FindAll("[data-test='ponow']"));
    }

    [Fact]
    public void Nigdy_nie_pokazuje_przyczyny_technicznej()
    {
        // Kontrakt 4.3: cause nie istnieje w JobFailureView i nie ma sie skad wziac.
        var pola = typeof(JobFailureView).GetProperties().Select(p => p.Name).ToArray();
        Assert.DoesNotContain("Cause", pola);
    }

    [Fact]
    public void Brak_zadania_nic_nie_renderuje()
    {
        var cut = Render<JobStatus>(ps => ps.Add(p => p.Job, (JobView?)null));
        Assert.Empty(cut.Markup.Trim());
    }
}
