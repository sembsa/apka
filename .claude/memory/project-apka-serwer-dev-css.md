---
name: project-apka-serwer-dev-css
description: Serwer bez ASPNETCORE_ENVIRONMENT=Development oddaje CSS jako 0 bajtow — strony wygladaja jak bez stylu
metadata:
  type: project
---

Uruchomienie `dotnet run --project src/Generator.Web --no-launch-profile` BEZ
`ASPNETCORE_ENVIRONMENT=Development` powoduje, ze `MapStaticAssets` nie ma wlaczonych
Static Web Assets. Skutek: `/app.css` oddaje **0 bajtow** przy `Accept-Encoding: gzip`
(czyli przegladarce), a curl bez kompresji dostaje pelny plik — wiec sprawdzanie
curlem NIE wykryje problemu. Strona wyglada jak zupelnie niestylowana.

Objaw w logu serwera: `StaticAssetsInvoker[17] The application is not running against
the published output and Static Web Assets are not enabled`.

Poprawne uruchomienie do ogladania UI:

```bash
ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://localhost:5099 \
  dotnet run --project src/Generator.Web --no-launch-profile
```

**Dlaczego to warto pamietac:** stracilem na tym kwadrans, diagnozujac wlasny CSS
i wlasny komponent, podczas gdy kod byl poprawny — bledna byla komenda uruchomienia.
Dotyczy KAZDEJ strony aplikacji, nie tylko nowych, wiec trafi tak samo druga osobe.

**Jak stosowac:** zanim uznasz, ze styl sie nie wczytuje, sprawdz
`curl -H "Accept-Encoding: gzip" -o /dev/null -w "%{size_download}" .../app.css` —
zero bajtow znaczy „zle uruchomiony serwer", nie „zly CSS".

Zwiazane: [[project-apka-druga-osoba-na-windows]]
