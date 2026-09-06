# Pamięć współdzielona (repo apka)

Ten plik to indeks pamięci. Jedna linia na jeden plik pamięci w `.claude/memory/`.
Treść pamięci NIGDY nie trafia tutaj — tylko wskaźnik `- [Tytuł](plik.md) — zaczepka`.

- [Rytm gita przy pracy w parze](feedback-praca-w-parze-git-rytm.md) — częsty pull/commit/push autoryzowany z góry, bez pytania
- [Pamięć współdzielona przez git](project-apka-pamiec-wspoldzielona-git.md) — pamięć leży w repo `.claude/memory`, symlink z ~/.claude
- [Druga osoba pracuje na Windows](project-apka-druga-osoba-na-windows.md) — Git Bash, skrypty w repo muszą być cross-platform
- [Sprzeczne decyzje w parze](feedback-sprzeczne-decyzje-rozstrzyga-sebastian.md) — oznacz spór w dokumencie, podaj bilans i zapytaj swojego użytkownika; nie wybieraj sam
- [Serwer dev i pusty CSS](project-apka-serwer-dev-css.md) — bez ASPNETCORE_ENVIRONMENT=Development /app.css ma 0 bajtow dla przeglądarki
- [Testy niezależne od maszyny](feedback-testy-niezalezne-od-maszyny.md) — kultura i separatory ścieżek; obie strony na tym padły
- [Prompt do claude przez stdin](project-apka-prompt-przez-stdin.md) — cmd.exe urywał wieloliniowy prompt; silnik nie działał na Windows
- [Flaky: losowa ofiara to teardown](feedback-flaky-losowa-ofiara-to-teardown.md) — inny test za kazdym razem = awaria w Dispose, nie w produkcji
- [Treść ze strony klienta](project-apka-tresc-ze-strony-klienta.md) — SSRF i wstrzyknięcie promptu; granica zaufania trybu „mam stronę”
