---
name: project-apka-pamiec-wspoldzielona-git
description: Pamięć projektu apka leży w repo (.claude/memory) i jest wymieniana z kolegą przez git
metadata:
  type: project
---

Repo: `github.com/sembsa/apka`, katalog lokalny `/Users/strzesniewski/Temp/apka`.
Ustawione 2026-09-04 na życzenie użytkownika („musisz się wymieniać pamięcią przez gita z nim").

Pamięć NIE leży w `~/.claude/projects/-Users-strzesniewski-Temp-apka/memory` —
ten katalog jest **symlinkiem** do `<repo>/.claude/memory`. Zapis pamięci = zmiana w repo.

**Why:** Kolega ma własną sesję Claude Code na tym samym repo. Pamięć trzymana lokalnie
w `~/.claude` byłaby dla niego niewidoczna; w repo jedzie razem z kodem przez pull/push.

**How to apply:** Po zapisaniu pliku pamięci od razu commit z prefiksem `memory:` i push —
inaczej druga strona jej nie widzi. Przed pisaniem nowej pamięci `git pull --rebase --autostash`
i sprawdź, czy kolega nie zapisał już tego faktu (wtedy uzupełnij jego plik, nie duplikuj).
`.claude/memory/MEMORY.md` ma `merge=union` w `.gitattributes`, więc indeks scala się sam.
Setup na nowej maszynie: `./scripts/link-claude-memory.sh`.
Zobacz [[feedback-praca-w-parze-git-rytm]].
