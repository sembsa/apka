// Wstrzykiwany do strony klienta DOPIERO przy serwowaniu podgladu (PreviewInjector),
// nigdy zapisywany do snapshotu - inaczej klient dostalby nasz kod w ZIP-ie.
//
// Zostaje przy e.target.closest('[data-cmt-id]') - to jedna linia standardowego DOM
// i robi dokladnie to, co trzeba. Format id waliduje C# (AnchorFormat), w jednym miejscu.
// Viewport bierzemy z wlasnej szerokosci iframe'a: to jest doslownie ta szerokosc,
// przy ktorej klient patrzyl (kontrakt 3.2). Prog 768 musi byc rowny progowi, na ktorym
// host przelacza szerokosc podgladu - jesli sie rozjada, viewport zacznie klamac.
document.addEventListener('click', (e) => {
  const block = e.target.closest('[data-cmt-id]');
  window.parent.postMessage({
    type: 'cmt-click',
    anchor: block ? block.getAttribute('data-cmt-id') : null,
    viewport: window.innerWidth < 768 ? 'mobile' : 'desktop',
  }, window.location.origin);   // nigdy '*'
}, true);
