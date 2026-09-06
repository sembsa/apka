---
name: feedback-flaky-losowa-ofiara-to-teardown
description: Flaky test wskazujacy ZA KAZDYM RAZEM inny test to podpis awarii w teardownie, nie w kodzie produkcyjnym
metadata:
  type: feedback
---

Gdy losowy czerwony trafia **za kazdym razem w INNY test**, szukaj najpierw w `Dispose`
klasy testowej, a nie w sciezce produkcyjnej. xUnit tworzy instancje klasy na kazdy test
i przypisuje wyjatek z teardownu temu testowi, ktory wlasnie przebiegl — stad losowa
ofiara. Awaria w produkcji czerwieni zwykle TEN SAM test.

**Why:** w tym repo (2026-09-06) Przemek zmierzyl 1 czerwony na 25 pelnych przebiegow,
raz `VersionStoreTests.Restore_...`, raz `ProjectApiTests.Rollback_...` — dwa rozne
zestawy. Wspolny mianownik nie byl w kodzie testowanym, tylko w `Directory.Delete(root,
recursive: true)` w obu teardownach, bez ponawiania.

Ale nie przeceniaj sygnalu przy malym n: oba te testy przechodza przez `SafeReplace`,
wiec n=2 nie rozstrzyga miedzy teardownem a produkcja. Poprawka objela oba miejsca.

**How to apply:** zanim opakujesz kolejna operacje produkcyjna w ponawianie, sprawdz
ksztalt teardownu wszystkich klas, w ktorych flaky wychodzil. I pamietaj o mierze:
przy czestotliwosci 1/25 dwadziescia zielonych przebiegow po poprawce to okolo 45%
szansy takze BEZ poprawki — to nie jest dowod. Powiedz to wprost. Patrz tez
[[feedback-testy-niezalezne-od-maszyny]].
