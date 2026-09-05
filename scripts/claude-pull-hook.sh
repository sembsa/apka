#!/usr/bin/env bash
# Hook UserPromptSubmit: przed każdym promptem sprawdza, co dopchnęła druga osoba,
# i wstrzykuje listę nowych commitów do kontekstu Claude'a.
#
# Świadomie NIE robi `pull --rebase --autostash`. Automatyczny rebase przy każdym
# prompcie to ta sama pułapka, w którą wpadliśmy oboje 2026-09-04 (patrz CLAUDE.md):
# nieodtworzony autostash zostawia markery konfliktu w plikach. Hook tylko fast-forwarduje
# czyste drzewo, a przy brudnym mówi, co jest do zrobienia, i niczego nie rusza.
#
# Wyjście: JSON na stdout (hookSpecificOutput.additionalContext) albo cicho, gdy nic nowego.
set -uo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)" || exit 0
cd "$REPO" || exit 0

# Wiadomość od drugiej osoby. Zwykły plik, więc widać ją w `git log` jak każdą inną
# zmianę, a adresat kasuje ją jednym `rm` — nic nie siedzi zaszyte w skrypcie.
# Pokazujemy ją TAKŻE wtedy, gdy nie ma nowych commitów: inaczej hook kończy przez
# `quiet` i wiadomość nigdy by nie doszła.
WIADOMOSC="$(cat "$REPO/.claude/wiadomosc.txt" 2>/dev/null || true)"

quiet() {
  if [ -n "$WIADOMOSC" ]; then
    emit "$WIADOMOSC"
    exit 0
  fi
  printf '{"suppressOutput":true}\n'
  exit 0
}

# Escape do stringa JSON: backslash, cudzysłów, taby, potem nowe linie na \n.
esc() {
  sed -e 's/\\/\\\\/g' -e 's/"/\\"/g' -e 's/\t/\\t/g' | awk '{ printf "%s\\n", $0 }'
}

# Escapuje sama, żeby wywołania nie powtarzały `| esc` — i dokleja wiadomość na górze,
# zanim pójdzie cokolwiek o gicie.
emit() {
  tresc="$1"
  [ -n "$WIADOMOSC" ] && tresc="$(printf '%s\n\n%s' "$WIADOMOSC" "$tresc")"
  printf '{"hookSpecificOutput":{"hookEventName":"UserPromptSubmit","additionalContext":"%s"}}\n' \
    "$(printf '%s' "$tresc" | esc)"
}

git rev-parse --git-dir >/dev/null 2>&1 || exit 0

# Offline albo brak dostępu do origin — nie blokujemy pracy, po prostu milczymy.
git fetch --quiet 2>/dev/null || quiet

UPSTREAM="$(git rev-parse --abbrev-ref --symbolic-full-name '@{u}' 2>/dev/null)" || quiet
[ -n "$UPSTREAM" ] || quiet

BEHIND="$(git rev-list --count "HEAD..$UPSTREAM" 2>/dev/null)" || quiet
AHEAD="$(git rev-list --count "$UPSTREAM..HEAD" 2>/dev/null)" || quiet

[ "${BEHIND:-0}" -eq 0 ] && quiet   # nic nowego u drugiej osoby

LOG="$(git log --oneline --no-decorate "HEAD..$UPSTREAM" 2>/dev/null | head -20)"
FILES="$(git diff --stat "HEAD..$UPSTREAM" 2>/dev/null | tail -30)"

# Blokuje TYLKO zmiany w plikach śledzonych. Untracked celowo nie blokują:
# fast-forward ich nie tyka, a przy pracy subagentów w drzewie prawie zawsze coś
# untracked leży (nowe pliki zadania przed commitem) — poprzednia wersja sprawdzała
# `git status --porcelain` i przez to nie pobierała niczego przez cały czas wykonywania
# planu. Jeśli nadchodzący commit doda plik, który koliduje z untracked, git sam odmówi
# fast-forwardu i wpadniemy w gałąź "fast-forward się nie udał" niżej.
# (git status, nie git diff-index: diff-index fałszywie alarmuje przy nieodświeżonym
# stat-cache, np. zaraz po klonie albo reset --hard — sprawdzone.)
if [ -n "$(git status --porcelain --untracked-files=no 2>/dev/null)" ]; then
  # Brudne drzewo — nie tykamy go automatycznie.
  MSG="$(printf 'GIT: %s nowych commitów na %s, ale NIE pobrano — masz niezacommitowane zmiany.\nZróbcie to ręcznie, świadomie (CLAUDE.md: pull --rebase --autostash, potem git stash list i grep na markery).\n\nCzeka:\n%s\n\nPliki:\n%s' \
    "$BEHIND" "$UPSTREAM" "$LOG" "$FILES")"
  emit "$MSG"
  exit 0
fi

if [ "${AHEAD:-0}" -gt 0 ]; then
  # Rozjechane: mamy własne niepushnięte commity, fast-forward niemożliwy.
  MSG="$(printf 'GIT: historia rozjechana — %s commitów u nas niepushniętych, %s nowych na %s. Fast-forward niemożliwy, potrzebny świadomy rebase.\n\nCzeka:\n%s' \
    "$AHEAD" "$BEHIND" "$UPSTREAM" "$LOG")"
  emit "$MSG"
  exit 0
fi

# Czyste drzewo, jesteśmy tylko z tyłu — bezpieczny fast-forward.
if git merge --ff-only "$UPSTREAM" --quiet 2>/dev/null; then
  MSG="$(printf 'GIT: dociągnięto %s nowych commitów od drugiej osoby (fast-forward z %s):\n%s\n\nPliki:\n%s' \
    "$BEHIND" "$UPSTREAM" "$LOG" "$FILES")"
else
  MSG="$(printf 'GIT: %s nowych commitów na %s, ale fast-forward się nie udał. Do sprawdzenia ręcznie.\n\nCzeka:\n%s' \
    "$BEHIND" "$UPSTREAM" "$LOG")"
fi
emit "$MSG"
