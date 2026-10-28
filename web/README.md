# Ludo Crown — web version

Ludo Crown in the browser, for quick testing on any phone without installing the app. Open
**https://mustafa-v12.github.io/LudoCrown/** in Safari, then *Share → Add to Home Screen* to play full-screen.

- Classic and Quick modes, 2–4 players
- Against the computer (Easy / Normal / Hard, the same bots as the app) or pass-and-play on one phone
- The game is saved on the device after every move and can be resumed
- Works offline after the first visit (service worker)
- Not included (they need the server): online matches, accounts, gem rerolls, Shield bottles, store, leagues

## Same rules as the app

`src/engine/` is a TypeScript port of `src/LudoCrown.Core` (board geometry, rules engine, bots).
`test/conformance.test.ts` replays games recorded from the C# engine and checks every result, error, event, state,
legal move and bot score. When you change the C# rules, regenerate the recordings and port the change:

```
dotnet run --project tools/LudoCrown.TraceGen -- web/test/fixtures/engine-traces.json.gz
cd web && npm test
```

## Develop

```
cd web
npm install
npm run dev      # http://localhost:5173/LudoCrown/
npm test
npm run build    # output in web/dist
```

`BASE_PATH` sets the URL prefix (default `/LudoCrown/` for GitHub Pages; use `BASE_PATH=/` for other hosts).

## Deploy

`.github/workflows/web.yml` tests, builds and publishes to GitHub Pages on every push to `main` that touches `web/`.
One-time setup in the repository: **Settings → Pages → Source: GitHub Actions**.
