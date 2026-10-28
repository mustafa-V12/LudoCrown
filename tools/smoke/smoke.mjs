// End-to-end smoke test against a running DEVELOPMENT server (dev tools enabled, ideally backed by PostgreSQL).
// Usage: node smoke.mjs [baseUrl]   (default http://localhost:5080)
//
// Covers: guest sign-in, dev sandbox purchases (success / duplicate / pending / failure), gold purchases with retry,
// a ranked Classic match with gem rerolls (incl. a retried request), held-roll passes, a rejected boost, quick chat,
// a casual Quick match with a Shield bottle visible to the opponent, mode stats, public profile privacy,
// league points, and an idempotent end-of-season reward.
import * as signalR from "@microsoft/signalr";

const base = process.argv[2] ?? "http://localhost:5080";
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const failures = [];
const check = (ok, what) => { console.log(`${ok ? "  ok  " : "  FAIL"} ${what}`); if (!ok) failures.push(what); };

async function raw(method, path, body, token) {
  await sleep(120); // stay under the API rate limit
  const res = await fetch(base + path, {
    method,
    headers: { "Content-Type": "application/json", ...(token ? { Authorization: `Bearer ${token}` } : {}) },
    body: body ? JSON.stringify(body) : undefined,
  });
  const text = await res.text();
  return { status: res.status, body: text ? JSON.parse(text) : null };
}
async function json(method, path, body, token) {
  const r = await raw(method, path, body, token);
  if (r.status >= 400) throw new Error(`${method} ${path} -> ${r.status} ${JSON.stringify(r.body)}`);
  return r.body;
}

/** A scripted player. `plan` decides per state what to try (reroll/boost/pass) on top of normal play. */
async function client(name) {
  const tokens = await json("POST", "/auth/guest", { displayName: name });
  const token = tokens.accessToken;
  const conn = new signalR.HubConnectionBuilder()
    .withUrl(`${base}/hubs/match`, { accessTokenFactory: () => token })
    .configureLogging(signalR.LogLevel.Warning)
    .build();
  const c = { name, token, conn, ended: null, last: null, lastActed: -1, rerolls: 0, retriedReroll: null, passes: 0,
    boostTried: null, boostUsed: null, sawOpponentShield: false, reactions: 0, plan: {} };
  c.reset = () => { c.ended = null; c.lastActed = -1; c.done = new Promise((res) => { c.resolveDone = res; }); };
  c.reset();
  conn.on("MatchEnded", (info) => { c.ended = info; c.resolveDone(); });
  conn.on("MatchmakingStatus", () => {});
  conn.on("RoomUpdated", () => {});
  conn.on("Reaction", () => { c.reactions++; });
  conn.on("MatchState", (s) => { c.last = s; act(c, s).catch((e) => console.error(name, e.message)); });
  await conn.start();
  return c;
}

async function act(c, s) {
  if (s.seats.some((x, i) => i !== s.yourSeat && x.shieldActive)) c.sawOpponentShield = true;
  if (s.winnerSeat !== null && s.winnerSeat !== undefined) return;
  if (s.currentSeat !== s.yourSeat || s.version <= c.lastActed) return;
  c.lastActed = s.version;
  await sleep(150);

  if (s.phase === "AwaitingRoll") {
    if (c.plan.tryBoost && c.boostTried === null) {
      const r = await c.conn.invoke("UseBoost", s.matchId, s.version, crypto.randomUUID());
      c.boostTried = r;
      if (!r.error) { c.boostUsed = r; return; } // the boost bumps the version; the next push continues the turn
    }
    await c.conn.invoke("Roll", s.matchId, s.version);
    return;
  }
  if (s.phase !== "AwaitingMove") return;

  if (c.plan.maxRerolls && c.rerolls < c.plan.maxRerolls && s.rerollStatus === "Available") {
    const requestId = crypto.randomUUID();
    const r = await c.conn.invoke("Reroll", s.matchId, s.version, requestId);
    if (!r.error) {
      c.rerolls++;
      if (!c.retriedReroll) {
        // Simulate a network retry of the very same request: must return the same result and charge nothing more.
        const again = await c.conn.invoke("Reroll", s.matchId, s.version, requestId);
        c.retriedReroll = { first: r, again };
      }
      return;
    }
  }
  if (s.legalPieces.length) { await c.conn.invoke("Move", s.matchId, s.legalPieces[0], s.version); return; }
  if (s.canPass) { c.passes++; await c.conn.invoke("PassTurn", s.matchId, s.version); }
}

async function playMatch(a, b, mode, queue) {
  a.reset(); b.reset();
  const sa = await a.conn.invoke("FindMatch", 2, mode, queue);
  const sb = await b.conn.invoke("FindMatch", 2, mode, queue);
  console.log(`matchmaking ${mode}/${queue}:`, sa.state, "->", sb.state, sb.matchId);
  const timeout = sleep(8 * 60_000).then(() => { throw new Error(`${mode} match timed out`); });
  await Promise.race([Promise.all([a.done, b.done]), timeout]);
  return sb.matchId;
}

const t0 = Date.now();
const info = await json("GET", "/info");
console.log("server:", info.environment, "storage:", info.storage);
check(info.environment === "Development", "server runs in Development (dev tools are only mapped there)");

const a = await client("Alice");
const b = await client("Bobby");

// ---------- economy & store ----------
for (const p of [a, b]) await json("POST", "/dev/economy/grant", { coins: 5000, gems: 20, shields: 2 }, p.token);
const inv0 = (await json("GET", "/me/inventory", null, a.token)).balances;
const catalog = await json("GET", "/store/catalog", null, a.token);
check(catalog.products.length >= 8 && catalog.devSandbox === true, "catalog lists packs and the dev sandbox is enabled");
check(catalog.products.filter((p) => p.kind === "iap").every((p) => p.appStoreProductId?.includes("REPLACE") || p.appStoreProductId), "IAP products carry (placeholder) App Store product ids");

const tx = "devtx-" + crypto.randomUUID();
const p1 = await raw("POST", "/store/purchases", { store: "dev_sandbox", productId: "gems_small", transactionId: tx }, a.token);
check(p1.status === 200 && p1.body.status === "credited" && p1.body.balances.gems === inv0.gems + 50, "sandbox purchase credited +50 gems after verification");
const p2 = await raw("POST", "/store/purchases", { store: "dev_sandbox", productId: "gems_small", transactionId: tx }, a.token);
check(p2.body.status === "already_credited" && p2.body.balances.gems === inv0.gems + 50, "duplicate transaction is not credited twice");
const steal = await raw("POST", "/store/purchases", { store: "dev_sandbox", productId: "gems_small", transactionId: tx }, b.token);
check(steal.status === 422, "another account cannot claim the same transaction");
const txP = "devtx-" + crypto.randomUUID();
const pend = await raw("POST", "/store/purchases", { store: "dev_sandbox", productId: "coins_small", transactionId: txP, simulate: "pending" }, a.token);
check(pend.status === 202 && pend.body.status === "pending", "pending verification is reported as pending, nothing credited");
const later = await raw("POST", "/store/purchases", { store: "dev_sandbox", productId: "coins_small", transactionId: txP }, a.token);
check(later.body.status === "credited", "pending purchase is credited once verification succeeds");
const fail = await raw("POST", "/store/purchases", { store: "dev_sandbox", productId: "coins_small", transactionId: "devtx-" + crypto.randomUUID(), simulate: "fail" }, a.token);
check(fail.status === 422 && fail.body.status === "rejected", "failed verification is rejected and never shown as success");

const coinsBefore = (await json("GET", "/me/inventory", null, a.token)).balances.coins;
const reqId = crypto.randomUUID();
const buy1 = await raw("POST", "/store/buy", { productId: "shield_single", requestId: reqId }, a.token);
const buy2 = await raw("POST", "/store/buy", { productId: "shield_single", requestId: reqId }, a.token);
check(buy1.status === 200 && buy2.status === 200 && buy2.body.balances.coins === coinsBefore - 2000, "gold purchase retried with the same request id is charged once");

// ---------- ranked Classic: rerolls allowed, boosts not ----------
const gemsBefore = (await json("GET", "/me/inventory", null, a.token)).balances.gems;
a.plan = { maxRerolls: 3, tryBoost: true }; b.plan = { maxRerolls: 2 };
const m1 = await playMatch(a, b, "classic", "ranked");
check(a.ended && b.ended, "ranked Classic match finished for both players");
check(a.boostTried?.error?.code === "BoostNotAllowed", "boost bottle rejected in a ranked match");
check(a.rerolls > 0, `rerolls used in a ranked match (${a.rerolls})`);
check(a.retriedReroll && a.retriedReroll.again.rerollsUsed === a.retriedReroll.first.rerollsUsed && a.retriedReroll.again.gemBalance === a.retriedReroll.first.gemBalance,
  "retried reroll request returned the same result without a second charge");
const gemsAfter = (await json("GET", "/me/inventory", null, a.token)).balances.gems;
const gemReward = a.ended.gemsAwarded ?? 0;
check(gemsAfter === gemsBefore - a.rerolls + gemReward, `gems: ${gemsBefore} - ${a.rerolls} rerolls + ${gemReward} reward = ${gemsAfter}`);
check(typeof a.ended.leaguePoints === "number", "ranked result carries league points");
console.log("  passes on held rolls:", a.passes + b.passes);

// ---------- quick chat ----------
// (Match is over; chat is only accepted during matches, so this is checked in the next match.)

// ---------- casual Quick with a Shield bottle ----------
a.plan = { tryBoost: true }; b.plan = {};
a.boostTried = null; a.boostUsed = null;
const shieldsBefore = (await json("GET", "/me/inventory", null, a.token)).balances.items.find((i) => i.id === "boost_shield")?.quantity ?? 0;
setTimeout(async () => { try { if (a.last) await a.conn.invoke("SendReaction", a.last.matchId, "good_luck"); } catch {} }, 2500);
const m2 = await playMatch(a, b, "quick", "casual");
check(a.ended && b.ended && a.ended.mode === "Quick", "casual Quick match finished (first piece home wins)");
check(a.ended.leaguePoints === null || a.ended.leaguePoints === undefined, "casual match gives no league points");
check(a.boostUsed !== null, "Shield bottle accepted in a casual match");
check(b.sawOpponentShield, "opponent saw the active Shield");
const shieldsAfter = (await json("GET", "/me/inventory", null, a.token)).balances.items.find((i) => i.id === "boost_shield")?.quantity ?? 0;
check(shieldsAfter === shieldsBefore - 1, "exactly one Shield bottle consumed");
check(b.reactions >= 1, "quick chat message delivered to the opponent");

// ---------- stats, profiles, privacy ----------
const modes = await json("GET", "/me/stats/modes", null, a.token);
const byMode = Object.fromEntries(modes.map((m) => [m.mode, m.gamesPlayed]));
check(byMode.classic === 1 && byMode.quick === 1, "per-mode stats: 1 Classic + 1 Quick");
const aPublic = a.last.seats[a.last.yourSeat].publicId;
const prof = await json("GET", `/players/${aPublic}`, null, b.token);
const leaked = JSON.stringify(prof).match(/email|provider|userId|@/i);
check(!leaked && prof.statsVisible, "public profile shows no private data");
await json("PATCH", "/me/privacy", { showStats: false }, a.token);
const hidden = await json("GET", `/players/${aPublic}`, null, b.token);
check(hidden.statsVisible === false && hidden.overall === null, "hidden stats are not sent to other players");

// ---------- league & season rewards ----------
const league = await json("GET", "/league", null, a.token);
check(league.me?.joined && league.group.length >= 2, `league standing: ${league.me?.points} points, group of ${league.group.length}`);
const ledgerBefore = await json("GET", "/me/ledger?limit=200", null, a.token);
await json("POST", "/dev/league/end-season", null, a.token);
const after = await json("GET", "/league", null, a.token);
check(after.lastSeason && after.lastSeason.rewardGranted, `season finalised, reward granted (${JSON.stringify(after.lastSeason?.reward)})`);
await json("POST", "/dev/league/end-season", null, a.token); // a second finalisation must not pay the old season again
const ledgerAfter = await json("GET", "/me/ledger?limit=200", null, a.token);
const rewardsFor = (l) => l.filter((e) => e.reason === "league_reward").length;
check(rewardsFor(ledgerAfter) - rewardsFor(ledgerBefore) <= 3, "league reward written once per asset, not duplicated");
check(ledgerAfter.some((e) => e.reason === "reroll") && ledgerAfter.some((e) => e.reason === "purchase"), "ledger has reroll and purchase entries");

await a.conn.stop(); await b.conn.stop();
console.log(`\nmatches: ${m1}, ${m2}; ${((Date.now() - t0) / 1000).toFixed(1)}s`);
console.log(failures.length ? `SMOKE FAILED (${failures.length})` : "SMOKE PASSED");
process.exit(failures.length ? 1 : 0);
