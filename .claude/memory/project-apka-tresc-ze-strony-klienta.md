---
name: project-apka-tresc-ze-strony-klienta
description: W apka tryb „mam stronę" pobiera cudzą stronę na nasz serwer — to granica zaufania i powierzchnia SSRF naraz
metadata:
  type: project
---

2026-09-06, dług nr 6 zamknięty. Od tej zmiany `apka` robi dwie rzeczy, których wcześniej
nie robiła, i obie przesuwają granicę bezpieczeństwa:

1. **Nasz serwer wykonuje żądanie HTTP pod adres podany przez klienta** (SSRF).
   Decyzja zapada na ADRESIE IP w `ConnectCallback` — po rozwiązaniu DNS i przy każdym
   skoku przekierowania. Sprawdzanie nazwy hosta nie wystarcza: `localtest.me` rozwiązuje
   się do 127.0.0.1, a rekord DNS może się zmienić między sprawdzeniem a połączeniem.
2. **Cudza treść ląduje w prompcie**, a `claude` czyta prompt i pisze pliki.
   Wchodzi wyłącznie jako ogrodzony cytat, zawsze PO naszych instrukcjach.

**Why:** To jedyne miejsce w aplikacji sięgające do obcej sieci — dlatego `IZrodloStrony`
jest osobną rejestracją w DI, a nie `new` w środku `ProjectApi`: ma być widoczne
w składzie zależności i podmienialne, żeby żaden test nie wyszedł do internetu.

**How to apply:** Zmieniając `PromptBuilder`, trzymaj każdy blok danych w funkcji `Blok`
— drugi wariant ogrodzenia rozjedzie się z pierwszym po pierwszej poprawce, a to jest
granica, na której stoi odporność na wstrzyknięcie. Nie dodawaj wyjątku „dla dev"
na localhost. Odporności na wstrzyknięcie nie dowodzi test jednostkowy (sprawdza tylko
kształt promptu) — dowodzi ŻYWY przebieg: zmierzone, że strona z „zignoruj poprzednie
instrukcje" nie została wykonana, a fakty z niej trafiły do wszystkich trzech szkiców
($0,328). Zobacz [[project-apka-prompt-przez-stdin]] i [[feedback-testy-niezalezne-od-maszyny]].
