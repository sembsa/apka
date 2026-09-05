---
name: feedback-testy-niezalezne-od-maszyny
description: W apka test zależny od kultury systemu albo separatora ścieżki jest zielony na jednej maszynie i czerwony na drugiej — obie strony wpadły w to tego samego dnia
metadata:
  type: feedback
---

2026-09-05, dwa razy w jedną i drugą stronę:

- Moje atrapy silnika składały JSON przez interpolację `{{cost}}`, więc `decimal`
  formatował się kulturą systemu. Na polskiej lokalizacji `0,05` zamiast `0.05` —
  **32 czerwone testy u Przemka**, wszystkie zielone u mnie. Naprawił `CultureInfo.InvariantCulture`.
- Jego `ClaudeRunnerZnajdzTests` wpisywały `C:\npm\claude.cmd` na sztywno, a `Znajdz`
  składa kandydata przez `Path.Combine` — na macOS wychodzi `C:\npm/claude.cmd`.
  **3 czerwone testy u mnie**, zielone u niego. Naprawiłem helperem `Kat`/`Plik`.

Ta sama klasa błędu dotyczy też `DirectorySignature` opartego na mtime (NTFS vs APFS)
i formatów dat w UI (`ToString("MMMM")` zależy od kultury procesu).

**Why:** Test zależny od maszyny nie mówi „kod jest zły" tylko „jesteś na innym systemie".
Kosztuje pół godziny diagnozy osobie, która go nie pisała, i uczy ignorowania czerwieni.
Przy dwóch maszynach w parze to nie pech, tylko powtarzalny mechanizm.

**How to apply:** W testach i w kodzie, który buduje tekst z liczb lub dat:
`CultureInfo.InvariantCulture` zawsze. Ścieżki: `Path.Combine`, `Path.PathSeparator`,
`Path.DirectorySeparatorChar` — nigdy `/`, `\`, `:` ani `;` wpisane wprost, także
w danych testowych i w oczekiwaniach. Nie opieraj asercji na mtime pliku.
Zanim uznasz test za gotowy, zapytaj: co w nim zależy od tego, na czym akurat biegnie?
Zobacz [[project-apka-druga-osoba-na-windows]] i [[feedback-praca-w-parze-git-rytm]].
