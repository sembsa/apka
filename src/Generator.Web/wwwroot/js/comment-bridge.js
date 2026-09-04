// Shim: jedyne miejsce, w ktorym postMessage spotyka sie z .NET.
// Iframe traktujemy jak niezaufany, mimo ze jest same-origin: jego tresc
// wygenerowal model z danych klienta. Stad trzy bramki przed invokeMethodAsync.
window.commentBridge = (() => {
  let handler = null;

  return {
    listen(dotNetRef, iframeId) {
      handler = (event) => {
        const iframe = document.getElementById(iframeId);
        if (!iframe || event.source !== iframe.contentWindow) return;  // 1. nasz iframe
        if (event.origin !== window.location.origin) return;           // 2. nasze pochodzenie
        const d = event.data;
        if (!d || d.type !== 'cmt-click') return;                      // 3. znany ksztalt
        // Viewportu tu nie ma: tryb podgladu zna host, bo to on go ustawil.
        // Liczony w iframe klamal przy kazdym kliknieciu (patrz preview-click.js).
        dotNetRef.invokeMethodAsync('OnBlockClicked', {
          anchor: typeof d.anchor === 'string' ? d.anchor : null,
        });
      };
      window.addEventListener('message', handler);
    },
    stop() {
      if (handler) window.removeEventListener('message', handler);
      handler = null;
    },
  };
})();

// Sama szerokosc okna. Progu tu NIE MA - decyzje „telefon czy komputer" podejmuje C#
// (Podglad.TrybDlaOkna), zeby nie bylo dwoch progow w dwoch plikach.
window.hostWidth = () => window.innerWidth;
