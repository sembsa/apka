#!/usr/bin/env bash
# Podłącza katalog pamięci Claude Code tej sesji do pamięci współdzielonej w repo.
# Uruchom raz na maszynę po sklonowaniu repo. Idempotentny.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SHARED_MEM="$REPO_ROOT/.claude/memory"

# Claude Code trzyma stan projektu w ~/.claude/projects/<slug>, gdzie slug to
# bezwzględna ścieżka projektu z '/' i '.' zamienionymi na '-'.
SLUG="$(printf '%s' "$REPO_ROOT" | sed 's#[/.]#-#g')"
PROJECT_DIR="$HOME/.claude/projects/$SLUG"
LINK="$PROJECT_DIR/memory"

mkdir -p "$SHARED_MEM"        # najpierw cel, żeby symlink nigdy nie zwisał
mkdir -p "$PROJECT_DIR"

if [ -L "$LINK" ]; then
  CURRENT="$(readlink "$LINK")"
  if [ "$CURRENT" = "$SHARED_MEM" ]; then
    echo "OK: pamięć już podłączona -> $SHARED_MEM"
    exit 0
  fi
  echo "Podmieniam symlink (był: $CURRENT)"
  rm "$LINK"
elif [ -d "$LINK" ]; then
  # Prawdziwy katalog z lokalną pamięcią — przenieś zawartość do repo, nie usuwaj.
  shopt -s dotglob nullglob
  MOVED=0
  for f in "$LINK"/*; do
    base="$(basename "$f")"
    if [ "$base" = "MEMORY.md" ] && [ -f "$SHARED_MEM/MEMORY.md" ]; then
      # dwa indeksy: dopisz lokalne linie na koniec współdzielonego
      printf '\n' >> "$SHARED_MEM/MEMORY.md"
      cat "$f" >> "$SHARED_MEM/MEMORY.md"
      echo "Doklejono lokalny MEMORY.md do współdzielonego indeksu (sprawdź duplikaty)"
    elif [ -e "$SHARED_MEM/$base" ]; then
      mv "$f" "$SHARED_MEM/$base.local"
      echo "Kolizja nazwy: zapisano jako $base.local"
    else
      mv "$f" "$SHARED_MEM/"
    fi
    MOVED=$((MOVED+1))
  done
  shopt -u dotglob nullglob
  rmdir "$LINK" 2>/dev/null || { echo "BŁĄD: $LINK niepusty, przerywam"; exit 1; }
  [ "$MOVED" -gt 0 ] && echo "Przeniesiono $MOVED plików lokalnej pamięci do $SHARED_MEM"
elif [ -e "$LINK" ]; then
  echo "BŁĄD: $LINK istnieje i nie jest katalogiem ani symlinkiem"; exit 1
fi

ln -s "$SHARED_MEM" "$LINK"
echo "Podłączono: $LINK -> $SHARED_MEM"
echo "Pamiętaj: git config pull.rebase true && git config rebase.autoStash true"
