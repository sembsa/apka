namespace Generator.Engine.Sources;

/// <summary>
/// Pobiera strone klienta i oddaje z niej tekst gotowy do promptu. Interfejs istnieje
/// po to, zeby warstwa HTTP i jej testy nie musialy siegac do sieci — a takze zeby
/// „siegniecie do sieci" bylo w kodzie widoczna zaleznoscia, a nie ukrytym skutkiem
/// ubocznym zakladania projektu.
/// </summary>
public interface IZrodloStrony
{
    Task<string> WyciagnijAsync(string adres, CancellationToken ct = default);
}

/// <summary>
/// Zlozenie dwoch kawalkow, ktore osobno testuja sie duzo lepiej: pobranie (siec,
/// adresy, kodowania) i wyciagniecie tresci (parsowanie HTML). Tutaj nie ma juz
/// wlasnej logiki i nie ma czego testowac osobno.
/// </summary>
public class ZrodloStrony(PobieraczStrony? pobieracz = null) : IZrodloStrony, IDisposable
{
    private readonly PobieraczStrony _pobieracz = pobieracz ?? new PobieraczStrony();

    public async Task<string> WyciagnijAsync(string adres, CancellationToken ct = default) =>
        await EkstraktorTresci.Wyciagnij(await _pobieracz.PobierzAsync(adres, ct));

    public void Dispose()
    {
        _pobieracz.Dispose();
        GC.SuppressFinalize(this);
    }
}
