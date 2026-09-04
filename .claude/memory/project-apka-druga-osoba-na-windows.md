---
name: project-apka-druga-osoba-na-windows
description: Druga osoba w repo apka pracuje na Windows w Git Bash — narzędzia repo muszą być cross-platform
metadata:
  type: project
---

Przemysław Kubala pracuje nad `apka` na **Windows, w Git Bash**; Sebastian na macOS.
Ustalone 2026-09-04: jego pierwszy commit do repo poprawiał `scripts/link-claude-memory.sh`,
bo w Git Bash `pwd` daje `/c/Users/...`, a Claude Code sluguje ścieżkę windowsową
(`C:\Users\...` → `C--Users-...`).

**Why:** Każdy skrypt czy narzędzie pomocnicze w tym repo uruchamiają obie osoby.
Rozwiązanie macOS-only zablokuje połowę zespołu i wróci jako jego poprawka.

**How to apply:** Skrypty w `scripts/` pisz przenośnie — bez `brew`, `sed -i ''`,
BSD-owych flag i ścieżek `/Users/...` na sztywno. Przy ścieżkach systemowych zakładaj
oba warianty (POSIX i windowsowy przez `cygpath`). Ścieżki w dokumentacji podawaj
relatywnie do repo. Zobacz [[feedback-praca-w-parze-git-rytm]]
i [[project-apka-pamiec-wspoldzielona-git]].
