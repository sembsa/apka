---
name: project-apka-prompt-przez-stdin
description: W apka prompt do `claude -p` idzie przez stdin, nie argumentem — na Windows cmd.exe urywał go na pierwszej nowej linii
metadata:
  type: project
---

2026-09-05: silnik `apka` był **nieuruchamialny na Windows** i nikt tego nie widział
przez ~300 zielonych testów. `claude` z npm to `claude.cmd`; CreateProcess uruchamia
skrypt wsadowy interpreterem, a cmd.exe kończy polecenie na pierwszym znaku nowej linii.
`PromptBuilder` składa prompt przez `AppendLine`, więc do modelu docierała **tylko
pierwsza linia**, a `--output-format json` w ogóle nie dochodził — stdout był prozą,
nie JSON-em. Znalazł to Przemek przy pierwszym uruchomieniu na prawdziwym CLI.

Naprawa: `-p` jako goła flaga, instrukcja przez `StandardInput`, wszystkie trzy
strumienie na jawnym UTF-8 bez BOM (domyślne kodowanie przekierowanego strumienia to
strona kodowa konsoli — CP1250 na polskim Windows).

**Why:** Atrapy testowe (`ScriptedRunner`, `FakeClaude`) omijają tę warstwę, więc żaden
test na macOS nie mógł tego złapać. Każda przyszła zmiana w tym, jak startujemy
podproces, ma ten sam martwy punkt.

**How to apply:** Nic wieloliniowego ani dłuższego niż ~8000 znaków nie może jechać
argumentem do `claude` — limit linii poleceń cmd.exe to ~8191 znaków, a brief z wklejonym
szkicem HTML potrafi go przekroczyć. Tekst od klienta w argumencie to dodatkowo `% ^ & | < >`
do interpretacji. Jedyne, co dziś jedzie argumentem, to `--append-system-prompt` — stąd test
pilnujący, że dopiski nie mają nowej linii ani `%`. Zmiany w `ClaudeRunner.RunAsync` weryfikuj
uruchomieniem `Generator.Cli` na żywym CLI (~$0.13); testy sprawdzają kanał, nie zachowanie
cmd.exe. Zobacz [[project-apka-druga-osoba-na-windows]] i [[feedback-testy-niezalezne-od-maszyny]].
