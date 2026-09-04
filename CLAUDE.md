# apka — zasady pracy w tym repo

## Praca w parze (dwie osoby, dwa Claude'y, jedno repo)

To repo pisze równolegle dwoje ludzi, każdy ze swoją sesją Claude Code.
Zakładaj, że drugi commit może pojawić się w każdej chwili.

**Rytm gita (obowiązkowy):**

1. Na starcie sesji i przed każdą pracą nad plikiem: `git pull --rebase --autostash`
2. Commituj małe, jednotematyczne zmiany — nie zbieraj po kilka funkcjonalności
3. `git push` natychmiast po każdym commicie (niepushnięty commit = kolizja u drugiej osoby)
4. Przed pushem, jeśli push odbity: `git pull --rebase --autostash`, rozwiąż, push ponownie
5. Nie rób `push --force` na `main` — druga osoba ma tę samą historię

Lokalna konfiguracja (ustaw raz): `git config pull.rebase true && git config rebase.autoStash true`

### Po `pull --rebase --autostash` — zawsze sprawdź, zanim commitujesz

Autostash **może nie zdołać** odtworzyć Twoich zmian (konflikt z tym, co przyszło).
Wtedy pliki zostają z markerami konfliktu, a `git add -A` wciąga je do commita.
Zdarzyło się to obu stronom tego samego dnia (`77c4fff`, `d40a6cf`), więc to nie pech:

```bash
git pull --rebase --autostash
git stash list                                  # niepusty = autostash NIE wrócił
grep -rn '^<<<<<<<\|^>>>>>>>' --include='*.md' .   # musi nic nie zwrócić
```

Dopiero potem `git add`. Jeśli markery są — scal ręcznie i sprawdź ponownie.

Po scaleniu **usuń nieodtworzony autostash** (`git stash drop`), ale dopiero gdy
potwierdzisz, że jego treść jest już w plikach — inaczej zostaje na liście na zawsze
i każdy następny `git stash list` daje fałszywy alarm.

## Pamięć współdzielona przez git

Pamięć Claude'a **nie** leży w `~/.claude/projects/.../memory` — leży w repo, w
`.claude/memory/`, i jest wymieniana między obiema osobami przez git.

- Katalog pamięci sesji jest symlinkiem do `<repo>/.claude/memory`
- Setup po klonie (raz na maszynę): `./scripts/link-claude-memory.sh`
- Indeks: `.claude/memory/MEMORY.md` (jedna linia na plik pamięci, `merge=union` w `.gitattributes`)
- Jeden fakt = jeden plik `.md` z frontmatterem (`name`, `description`, `metadata.type`)
- **Po zapisaniu pliku pamięci: od razu `git add` + commit z prefiksem `memory:` + push.**
  Pamięć niepushnięta nie istnieje dla drugiej osoby.
- Przed pisaniem nowej pamięci: `git pull --rebase --autostash`, potem sprawdź, czy
  druga osoba nie zapisała już tego samego faktu — wtedy uzupełnij jej plik, nie twórz duplikatu

@.claude/memory/MEMORY.md
