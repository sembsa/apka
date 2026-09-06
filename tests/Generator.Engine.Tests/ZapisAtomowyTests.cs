using Generator.Engine.IO;
using Xunit;

namespace Generator.Engine.Tests;

/// <summary>
/// Hipoteza na losowa porazke, ktora Przemek widzi na Windows przy rownolegle
/// biegnacych klasach testowych (`UnauthorizedAccessException` z `File.Move`
/// w `ProjectStore.Save`). Na macOS sie tego nie odtwarza, wiec testujemy LOGIKE
/// ponawiania — jedyna czesc, ktora da sie sprawdzic deterministycznie i tak samo
/// na kazdym systemie. Dowodu, ze to naprawia tamten test, te testy NIE daja.
/// </summary>
public class ZapisAtomowyTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gen-atom-").FullName;
    public void Dispose() => DirectoryOps.Delete(_dir);

    [Fact]
    public void Przejsciowa_blokada_konczy_sie_sukcesem_po_ponowieniu()
    {
        var wywolania = 0;
        var przerwy = new List<int>();

        ZapisAtomowy.Ponow(() =>
        {
            wywolania++;
            if (wywolania < 3) throw new UnauthorizedAccessException("plik chwilowo zajety");
        }, proby: 5, odczekaj: przerwy.Add);

        Assert.Equal(3, wywolania);
        Assert.Equal([1, 2], przerwy);   // odstep rosnie z numerem proby
    }

    [Fact]
    public void Trwala_blokada_leci_dalej_po_ostatniej_probie_a_nie_w_kolko()
    {
        var wywolania = 0;

        Assert.Throws<IOException>(() => ZapisAtomowy.Ponow(
            () => { wywolania++; throw new IOException("na stale"); },
            proby: 4, odczekaj: _ => { }));

        // Rowno 4, nie 3 i nie 5: ostatnia proba tez sie liczy, a po niej wyjatek
        // wychodzi nieopakowany, zeby wolajacy zobaczyl prawdziwa przyczyne.
        Assert.Equal(4, wywolania);
    }

    [Fact]
    public void Blad_nie_bedacy_blokada_pliku_nie_jest_ponawiany()
    {
        // Ponawianie ma dotyczyc chwilowej niedostepnosci pliku. Ponawianie czegokolwiek
        // innego (np. bledu programisty) tylko opoznia i zaciemnia porazke.
        var wywolania = 0;

        Assert.Throws<InvalidOperationException>(() => ZapisAtomowy.Ponow(
            () => { wywolania++; throw new InvalidOperationException("blad logiki"); },
            proby: 5, odczekaj: _ => { }));

        Assert.Equal(1, wywolania);
    }

    [Fact]
    public void Zapis_podmienia_plik_w_calosci_i_nie_zostawia_smieci()
    {
        var cel = Path.Combine(_dir, "project.json");
        File.WriteAllText(cel, "stara tresc");

        ZapisAtomowy.Zapisz(cel, "nowa tresc");

        Assert.Equal("nowa tresc", File.ReadAllText(cel));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void Rownolegle_zapisy_tej_samej_sciezki_nie_dziela_pliku_tymczasowego()
    {
        // Wspolne `plik.tmp` znaczy, ze dwa rownolegle zapisy pisza do tego samego
        // pliku i jeden obcina drugiemu tresc w polowie. Test sprawdza wlasnosc, ktora
        // to wyklucza: kazdy zapis konczy sie PELNA trescia jednego z pisarzy, nigdy
        // sklejka, a po wszystkim nie zostaje ani jeden plik tymczasowy.
        var cel = Path.Combine(_dir, "comments.json");
        var tresci = Enumerable.Range(0, 16).Select(i => new string((char)('a' + i), 20_000)).ToArray();

        Parallel.ForEach(tresci, t => ZapisAtomowy.Zapisz(cel, t));

        Assert.Contains(File.ReadAllText(cel), tresci);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }
}
