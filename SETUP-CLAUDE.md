# Setup dla drugiej osoby (i dla jej Claude'a)

Czytasz to, bo w tym repo pracują równolegle dwie osoby, każda w swojej sesji
Claude Code, na tej samej gałęzi `main`. Pamięć Claude'a jest **współdzielona przez gita**,
żeby oba Claude'y wiedziały to samo o projekcie.

Wykonaj to raz na tej maszynie.

## 1. Podłącz pamięć do repo

```bash
git pull --rebase --autostash
./scripts/link-claude-memory.sh
```

Skrypt zamienia `~/.claude/projects/<slug-repo>/memory` na symlink do
`<repo>/.claude/memory`. Od tej pory każdy plik pamięci, który zapisze Claude,
jest plikiem w repo i jedzie do drugiej osoby przez `git push`.

Skrypt jest idempotentny — można go uruchomić ponownie bez skutków ubocznych.

**Jeśli skrypt zgłosi, że nie znalazł katalogu sesji:** uruchom raz `claude`
w katalogu repo (utworzy `~/.claude/projects/<slug>`) i odpal skrypt ponownie.
Skrypt celowo nie tworzy tego katalogu sam — prawidłową nazwę zna tylko Claude Code.

**Jeśli miałeś już lokalną pamięć w tym projekcie:** skrypt jej nie usuwa —
przenosi pliki do `.claude/memory/`, a lokalny `MEMORY.md` dokleja na koniec
współdzielonego indeksu. Przejrzyj wynik (możliwe duplikaty linii), potem:

```bash
git add .claude/memory && git commit -m "memory: przeniesienie lokalnej pamięci do repo" && git push
```

## 2. Ustaw gita pod pracę w parze

```bash
git config pull.rebase true
git config rebase.autoStash true
```

## 3. Zrestartuj sesję Claude Code

Wyjdź z `claude` i uruchom ponownie w katalogu repo. Dopiero wtedy dociągnie się
`CLAUDE.md` z repo oraz indeks pamięci przez symlink.

> **Jeśli czytasz to jako Claude:** wykonaj kroki 1–2, a o restart sesji poproś
> użytkownika — sam nie możesz zrestartować sesji, w której działasz. Nie kończ
> zadania bez wyraźnego przekazania tej prośby.

## Weryfikacja (wszystkie trzy muszą przejść)

```bash
./scripts/link-claude-memory.sh          # drugi bieg musi wypisać "OK: pamięć już podłączona"
cat .claude/memory/MEMORY.md             # indeks z wpisami
git config pull.rebase                   # true
```

Gdyby symlinku trzeba było poszukać ręcznie:

```bash
find ~/.claude/projects -maxdepth 2 -name memory -type l -exec readlink {} \;
```

## Zasady, których trzyma się druga strona (są w `CLAUDE.md`)

1. `git pull --rebase --autostash` na starcie sesji i przed edycją plików
2. Małe, jednotematyczne commity — nie zbieraj po kilka funkcjonalności
3. `git push` natychmiast po commicie; niepushnięty commit to konflikt u drugiej osoby
4. Po zapisaniu pliku pamięci: od razu commit z prefiksem `memory:` i push —
   pamięć niepushnięta nie istnieje dla drugiej strony
5. Żadnego `push --force` na `main`

`.claude/memory/MEMORY.md` ma `merge=union` w `.gitattributes`, więc gdy oba Claude'y
dopiszą linię do indeksu, git scali je sam, bez konfliktu.
