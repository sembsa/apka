using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace Generator.Engine.Sources;

/// <summary>
/// Pobiera HTML strony, ktora klient podal jako „mam strone, ale slaba". Wszystko tutaj
/// jest ograniczeniem, bo adres pochodzi z zewnatrz i pobiera go NASZ serwer:
///
/// - polaczenie idzie przez `ConnectCallback`, ktory sam rozwiazuje DNS i sprawdza adres
///   TUZ PRZED zestawieniem gniazda. To zamyka dwie dziury naraz: wyscig miedzy
///   sprawdzeniem a polaczeniem (rekord DNS moze sie zmienic) i przekierowanie na adres
///   wewnetrzny — kazdy skok przechodzi przez ten sam callback,
/// - piec przekierowan, dziesiec sekund na calosc, piec na samo polaczenie,
/// - tylko HTML i tylko 2 MB; czytamy strumieniem, bo `ReadAsStringAsync` nie ma
///   gornej granicy i strona-pulapka zjadlaby pamiec procesu.
/// </summary>
public partial class PobieraczStrony : IDisposable
{
    public const int MaxBajtow = 2 * 1024 * 1024;

    /// Puste User-Agent dostaje 403 od sporej czesci polskiego hostingu, wiec
    /// przedstawiamy sie wprost — razem z adresem, zeby administrator strony mial
    /// gdzie sprawdzic, co go odwiedza.
    private const string UserAgent = "GeneratorStron/1.0 (+https://github.com/sembsa/apka)";

    private readonly HttpClient _http;

    static PobieraczStrony() =>
        // Bez tego `Encoding.GetEncoding("windows-1250")` RZUCA na .NET-cie —
        // a to jest kodowanie polowy starych stron malych firm, czyli dokladnie
        // tego rynku, ktoremu ten tryb sluzy.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// `handler` istnieje dla testow tresci (status, typ, rozmiar, kodowanie), ktore
    /// maja isc bez sieci. Atrapa OMIJA `ConnectCallback` — polityka adresow ma wiec
    /// wlasne testy, patrz PobieraczStronyTests.
    public PobieraczStrony(HttpMessageHandler? handler = null, TimeSpan? limit = null)
    {
        _http = new HttpClient(handler ?? StworzHandler(), disposeHandler: true)
        {
            Timeout = limit ?? TimeSpan.FromSeconds(10),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("pl,en;q=0.5");
    }

    private static SocketsHttpHandler StworzHandler() => new()
    {
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 5,
        AutomaticDecompression = DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        ConnectCallback = async (kontekst, ct) =>
        {
            var host = kontekst.DnsEndPoint.Host;
            var rozwiazane = await Dns.GetHostAddressesAsync(host, ct);
            var adres = PolitykaAdresu.Wybierz(rozwiazane, host);

            var gniazdo = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await gniazdo.ConnectAsync(new IPEndPoint(adres, kontekst.DnsEndPoint.Port), ct);
                return new NetworkStream(gniazdo, ownsSocket: true);
            }
            catch
            {
                gniazdo.Dispose();
                throw;
            }
        },
    };

    /// <summary>
    /// Zwraca surowy HTML zdekodowany do tekstu. Nie wyciaga tresci — to robi
    /// <see cref="EkstraktorTresci"/>. Rozdzielone, bo pobieranie wymaga sieci,
    /// a wyciaganie nie, i testuje sie je zupelnie inaczej.
    /// </summary>
    public async Task<string> PobierzAsync(string adres, CancellationToken ct = default)
    {
        var uri = Zbuduj(adres);

        HttpResponseMessage odp;
        try
        {
            odp = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException ex) when (ex.InnerException is ZrodloException wewnetrzny)
        {
            // ConnectCallback rzuca „w srodku" HttpClienta, ktory opakowuje wszystko
            // w HttpRequestException. Bez rozpakowania odmowa z polityki adresow
            // dotarlaby do wolajacego jako zwykla awaria sieci i stracilaby powod.
            throw wewnetrzny;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            // `TaskCanceledException` bez zadania anulowania to przekroczony `Timeout`
            // HttpClienta — dla klienta to ta sama sytuacja co brak odpowiedzi.
            throw new PobranieNieudaneException(
                $"nie udalo sie polaczyc ze strona '{uri}': {ex.Message}", ex);
        }

        using (odp)
        {
            if (!odp.IsSuccessStatusCode)
                throw new PobranieNieudaneException(
                    $"strona '{uri}' odpowiedziala kodem {(int)odp.StatusCode}");

            var typ = odp.Content.Headers.ContentType;
            if (!CzyHtml(typ))
                throw new PobranieNieudaneException(
                    $"pod adresem '{uri}' nie ma strony HTML, tylko '{typ?.MediaType ?? "brak typu"}'");

            // Deklarowana dlugosc odcina pulapke ZANIM cokolwiek przeczytamy; petla
            // nizej i tak liczy bajty, bo naglowka moze nie byc albo moze klamac.
            if (odp.Content.Headers.ContentLength > MaxBajtow)
                throw new PobranieNieudaneException(
                    $"strona '{uri}' ma {odp.Content.Headers.ContentLength} bajtow, limit to {MaxBajtow}");

            var bajty = await CzytajZLimitem(odp, uri, ct);
            return Dekoduj(bajty, typ?.CharSet);
        }
    }

    private static Uri Zbuduj(string adres)
    {
        // Klient wpisuje „mojafirma.pl" rownie czesto jak pelny adres. Doklejamy https,
        // zamiast odmawiac — inaczej polowa nietechnicznych klientow odbija sie od
        // formularza za cos, co nie jest ich bledem.
        var tekst = adres.Trim();
        if (!tekst.Contains("://", StringComparison.Ordinal)) tekst = "https://" + tekst;

        if (!Uri.TryCreate(tekst, UriKind.Absolute, out var uri))
            throw new AdresNiepoprawnyException($"'{adres}' nie wyglada na adres strony");

        if (!PolitykaAdresu.CzyDozwolonySchemat(uri))
            throw new AdresNiepoprawnyException(
                $"adres '{adres}' uzywa schematu '{uri.Scheme}' — obslugujemy tylko http i https");

        return uri;
    }

    private static bool CzyHtml(MediaTypeHeaderValue? typ) =>
        typ?.MediaType is "text/html" or "application/xhtml+xml";

    private static async Task<byte[]> CzytajZLimitem(HttpResponseMessage odp, Uri uri, CancellationToken ct)
    {
        await using var strumien = await odp.Content.ReadAsStreamAsync(ct);
        using var bufor = new MemoryStream();
        var kawalek = new byte[8192];

        while (true)
        {
            var n = await strumien.ReadAsync(kawalek, ct);
            if (n == 0) break;

            if (bufor.Length + n > MaxBajtow)
                throw new PobranieNieudaneException(
                    $"strona '{uri}' przekracza limit {MaxBajtow} bajtow");

            bufor.Write(kawalek, 0, n);
        }

        return bufor.ToArray();
    }

    /// Kolejnosc jak w przegladarce: naglowek HTTP, potem `<meta charset>` z poczatku
    /// dokumentu, na koniec UTF-8. Odwrotna kolejnosc byla by bledem — serwer wie o sobie
    /// wiecej niz dokument, ktory moze byc kopiowany miedzy hostami.
    public static string Dekoduj(byte[] bajty, string? charsetZNaglowka)
    {
        var kodowanie = Znajdz(charsetZNaglowka) ?? Znajdz(WyniuchajMeta(bajty)) ?? Encoding.UTF8;
        return kodowanie.GetString(bajty);
    }

    private static Encoding? Znajdz(string? nazwa)
    {
        if (string.IsNullOrWhiteSpace(nazwa)) return null;
        try
        {
            return Encoding.GetEncoding(nazwa.Trim().Trim('"', '\''));
        }
        catch (ArgumentException)
        {
            // Nazwa kodowania z cudzej strony bywa literowka albo wymyslem. Nie jest to
            // powod, zeby odmowic pobrania — schodzimy o poziom nizej w kolejnosci.
            return null;
        }
    }

    /// Szukamy w pierwszych 2 kB, tak jak przegladarki: `<meta charset>` z konca
    /// dlugiego dokumentu i tak nie zdazylby zadzialac. Bajty czytamy jako latin1,
    /// bo nazwa kodowania jest zawsze w ASCII, a latin1 nie gubi zadnego bajtu.
    private static string? WyniuchajMeta(byte[] bajty)
    {
        var poczatek = Encoding.Latin1.GetString(bajty, 0, Math.Min(bajty.Length, 2048));
        var m = CharsetRegex().Match(poczatek);
        return m.Success ? m.Groups[1].Value : null;
    }

    [GeneratedRegex("""charset\s*=\s*["']?\s*([a-zA-Z0-9_:.-]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex CharsetRegex();

    public void Dispose()
    {
        _http.Dispose();   // disposeHandler: true — zabiera tez handler, wlasny czy podstawiony
        GC.SuppressFinalize(this);
    }
}
