---
name: feedback-praca-w-parze-git-rytm
description: W repo apka pracujemy w parze — częsty pull/commit/push jest autoryzowany z góry, bez pytania
metadata:
  type: feedback
---

Sebastian pracuje nad repo `apka` razem z kolegą siedzącym obok (Przemysław Kubala,
przemyslaw.kubala@soneta.pl), każdy w swojej sesji Claude Code na tym samym `main`.
Polecenie z 2026-09-04: „bierz pod uwagę częste commit i git pull git push".

**Why:** Dwie sesje piszą do jednej gałęzi. Niepushnięty commit u jednej strony to
konflikt u drugiej. Pytanie o zgodę na każdy push zabijałoby ten rytm — użytkownik
udzielił trwałej autoryzacji dla `pull`/`commit`/`push` w tym repo.

**How to apply:** `git pull --rebase --autostash` na starcie sesji i przed edycją plików;
małe jednotematyczne commity; `git push` od razu po commicie, bez dopytywania.
Nigdy `push --force` na `main`. Szczegóły rytmu są też w repo w `CLAUDE.md`.
Zobacz [[project-apka-pamiec-wspoldzielona-git]].
