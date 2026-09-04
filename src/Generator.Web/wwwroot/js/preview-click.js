// Wstrzykiwany do strony klienta DOPIERO przy serwowaniu podgladu (PreviewInjector),
// nigdy zapisywany do snapshotu - inaczej klient dostalby nasz kod w ZIP-ie.
//
// Zostaje przy e.target.closest('[data-cmt-id]') - to jedna linia standardowego DOM
// i robi dokladnie to, co trzeba. Format id waliduje C# (Kotwica.Poprawna).
//
// Viewportu tu NIE liczymy. Kiedys stalo tu `window.innerWidth < 768` i to klamalo
// przy KAZDYM kliknieciu: nieostylowany iframe ma domyslne 300px, wiec kazda uwaga
// szla jako "mobile", takze gdy klient patrzyl na monitorze 1920px. Prog w JS i
// prawdziwa szerokosc podgladu leza w innych plikach i musialy sie rozjechac.
// Tryb podgladu zna host - to on go ustawil - wiec on go dopisuje (kontrakt 3.2).
document.addEventListener('click', (e) => {
  const block = e.target.closest('[data-cmt-id]');
  window.parent.postMessage({
    type: 'cmt-click',
    anchor: block ? block.getAttribute('data-cmt-id') : null,
  }, window.location.origin);   // nigdy '*'
}, true);
