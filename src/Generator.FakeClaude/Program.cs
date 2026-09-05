using System.Text;
using System.Text.Json;

// Jawny UTF-8 bez BOM na stdin i stdout. Domyslnie Console uzywa strony kodowej
// konsoli — na polskim Windows CP1250 — a rodzic (ClaudeRunner) czyta i pisze UTF-8.
// Bez tego test na polskie znaki w promptcie byl zielony na macOS i czerwony na
// Windows. Prawdziwy `claude` (Node) pisze UTF-8, wiec atrapa musi robic to samo.
var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
Console.SetIn(new StreamReader(Console.OpenStandardInput(), utf8));
Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true });

// Atrapa `claude -p` dla testów. Nie waliduje flag poza --scenario;
// sprawdzanie flag jest zadaniem testów ClaudeRunner, nie atrapy.
var scenario = "success";
string? pidFile = null;
for (var i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--scenario") scenario = args[i + 1];
    if (args[i] == "--pidfile") pidFile = args[i + 1];
}

const string success = """
{"is_error":false,"subtype":"success","stop_reason":"end_turn","terminal_reason":"completed",
"num_turns":2,"session_id":"b0c0830f-81e8-4cd4-865a-5e7562278188","total_cost_usd":0.018619,
"permission_denials":[],"result":"Utworzylem index.html",
"modelUsage":{"claude-opus-5[1m]":{},"claude-haiku-4-5-20251001":{}}}
""";

// Realny kształt odmowy uprawnien: JSON wyglada na sukces, plik nie powstaje.
const string denied = """
{"is_error":false,"subtype":"success","stop_reason":"end_turn","terminal_reason":"completed",
"num_turns":2,"session_id":"e00452ce-e4db-4a8e-84e6-83e38c926cdf","total_cost_usd":0.012750,
"permission_denials":[{"tool_name":"Write","tool_use_id":"toolu_01BRkt","tool_input":{"file_path":"/tmp/t1/index.html","content":"<h1>Test</h1>"}}],
"result":"Nie mam uprawnien do zapisu tego pliku.",
"modelUsage":{"claude-opus-5[1m]":{},"claude-haiku-4-5-20251001":{}}}
""";

switch (scenario)
{
    case "success":
        Console.Out.Write(success);
        return 0;
    case "denied":
        Console.Out.Write(denied);
        return 0;
    case "crash":
        Console.Error.Write("Error: bypassPermissions not supported in restricted mode");
        return 1;   // stdout celowo pusty
    case "stdin-wait":
        // Dla testu ClaudeRunner, ze rodzic zamyka stdin natychmiast po Start().
        // Jesli rodzic NIE zamknie stdin, ReadToEnd() wisi, az ktos je zamknie —
        // to jedyny sposob, zeby test czasowy naprawde sprawdzal Close(),
        // zamiast przechodzic niezaleznie od tego, czy Close() w ogole wywolano.
        Console.In.ReadToEnd();
        Console.Out.Write(success);
        return 0;
    case "echo-args":
        // Dla testow end-to-end na BuildArguments/RunAsync: wypisuje WLASNE argv
        // (to, co faktycznie dostal proces, nie to, co ClaudeRunner mysli, ze wyslal)
        // jako tablice JSON. Odsacza stdin, mimo ze go nie uzywa: rodzic pisze tam
        // teraz prompt, a atrapa konczaca sie bez odczytu zrywa potok w trakcie
        // zapisu — test padalby losowo, w zaleznosci od tego, kto zdazyl pierwszy.
        Console.In.ReadToEnd();
        Console.Out.Write(JsonSerializer.Serialize(args));
        return 0;
    case "echo-stdin":
        // Dla testu kanalu promptu: odsyla RAZEM argv i tresc stdin, zeby jeden
        // test mogl stwierdzic i to, ze instrukcja dotarla w calosci, i to, ze NIE
        // ma jej w argumentach. Sama „obecnosc w stdin" nie wystarcza — przy
        // promptcie w obu miejscach naraz test byl by zielony, a na Windows cmd.exe
        // dalej urywalby polecenie na pierwszej nowej linii.
        Console.Out.Write(JsonSerializer.Serialize(new Echo(args, Console.In.ReadToEnd())));
        return 0;
    case "sleep":
        // Dla testu anulowania: spi realnie dlugo, zeby dac czas na Cancel() w
        // trakcie dzialania procesu. Jesli podano --pidfile, zapisuje tam wlasny
        // PID PRZED usnieciem — stdout z RunAsync jest dostepny dopiero po
        // zakonczeniu procesu (ReadToEndAsync czeka na EOF), wiec test nie moze
        // poznac PID inaczej niz przez plik.
        if (pidFile is not null) File.WriteAllText(pidFile, Environment.ProcessId.ToString());
        Task.Delay(TimeSpan.FromSeconds(30)).Wait();
        Console.Out.Write(success);
        return 0;
    default:
        Console.Error.Write($"nieznany scenariusz: {scenario}");
        return 2;
}

record Echo(string[] Argv, string Stdin);
