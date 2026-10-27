// Usage: node smoke.mjs [baseUrl]   (default http://localhost:5080)
import * as signalR from "@microsoft/signalr";

const base = process.argv[2] ?? "http://localhost:5080";
const json = async (method, path, body, token) => {
  const res = await fetch(base + path, {
    method,
    headers: { "Content-Type": "application/json", ...(token ? { Authorization: `Bearer ${token}` } : {}) },
    body: body ? JSON.stringify(body) : undefined,
  });
  const text = await res.text();
  if (!res.ok) throw new Error(`${method} ${path} -> ${res.status} ${text}`);
  return text ? JSON.parse(text) : null;
};

async function client(name) {
  const tokens = await json("POST", "/auth/guest", { displayName: name });
  const conn = new signalR.HubConnectionBuilder()
    .withUrl(`${base}/hubs/match`, { accessTokenFactory: () => tokens.accessToken })
    .configureLogging(signalR.LogLevel.Warning)
    .build();
  const c = { name, tokens, conn, ended: null, actions: 0, rejected: 0, lastActedVersion: -1 };
  c.done = new Promise((resolve) => {
    conn.on("MatchEnded", (info) => { c.ended = info; resolve(); });
  });
  conn.on("MatchmakingStatus", () => {});
  conn.on("RoomUpdated", () => {});
  conn.on("Reaction", () => {});
  conn.on("MatchState", async (s) => {
    if (s.winnerSeat !== null && s.winnerSeat !== undefined) return;
    if (s.currentSeat !== s.yourSeat) return;
    if (s.version <= c.lastActedVersion) return;   // each state version is acted on once
    c.lastActedVersion = s.version;
    await new Promise((r) => setTimeout(r, 150)); // human-like pacing (server allows ~10 calls/s)
    try {
      let err = null;
      if (s.phase === "AwaitingRoll") err = await conn.invoke("Roll", s.matchId, s.version);
      else if (s.phase === "AwaitingMove" && s.legalPieces.length) err = await conn.invoke("Move", s.matchId, s.legalPieces[0], s.version);
      else return;
      c.actions++;
      if (err) c.rejected++; // duplicate pushes of the same version are expected to be rejected as stale
    } catch (e) { console.error(name, e.message); }
  });
  await conn.start();
  return c;
}

const t0 = Date.now();
const providers = await json("GET", "/auth/providers");
console.log("providers:", providers);
const a = await client("Alice");
const b = await client("Bobby");

// Cheating attempt: a client tries to invoke the hub with a dice value / unknown method.
try { await a.conn.invoke("Roll", "nonexistent", 0); } catch (e) { /* returns error object, not throw */ }

const sa = await a.conn.invoke("FindMatch", 2);
const sb = await b.conn.invoke("FindMatch", 2);
console.log("matchmaking:", sa.state, "->", sb.state, sb.matchId);

const timeout = setTimeout(() => { console.error("TIMEOUT"); process.exit(1); }, 120000);
await Promise.all([a.done, b.done]);
clearTimeout(timeout);

const meA = await json("GET", "/me", null, a.tokens.accessToken);
const meB = await json("GET", "/me", null, b.tokens.accessToken);
const hist = await json("GET", "/me/matches", null, a.tokens.accessToken);
console.log(`Alice placement ${a.ended.yourPlacement}, coins +${a.ended.coinsAwarded}; Bobby placement ${b.ended.yourPlacement}, coins +${b.ended.coinsAwarded}`);
console.log(`Server-side totals: Alice coins=${meA.coins} games=${meA.stats.gamesPlayed} wins=${meA.stats.gamesWon}; Bobby coins=${meB.coins} games=${meB.stats.gamesPlayed} wins=${meB.stats.gamesWon}`);
console.log(`actions sent: ${a.actions + b.actions} (stale/rejected: ${a.rejected + b.rejected}); history entries: ${hist.length}; ${(Date.now() - t0) / 1000}s`);

const ok = a.ended && b.ended && [a.ended.yourPlacement, b.ended.yourPlacement].sort().join() === "1,2"
  && meA.stats.gamesPlayed === 1 && meB.stats.gamesPlayed === 1 && meA.stats.gamesWon + meB.stats.gamesWon === 1 && hist.length === 1;

// Account deletion works end to end.
const del = await json("DELETE", "/me", {}, b.tokens.accessToken);
let deletedOk = false;
try { await json("POST", "/auth/refresh", { refreshToken: b.tokens.refreshToken }); } catch { deletedOk = true; }
console.log("account deletion:", del.deleted && deletedOk ? "OK" : "FAILED");

await a.conn.stop(); await b.conn.stop();
console.log(ok && deletedOk ? "SMOKE TEST PASSED" : "SMOKE TEST FAILED");
process.exit(ok && deletedOk ? 0 : 1);
