// Relais des rapports du mod STS Tools vers Discord (Cloudflare Worker).
//
// Le mod n'a plus les adresses des webhooks Discord : il envoie ses rapports ici, qui les vérifie et les transmet.
// Secrets du Worker (jamais dans le dépôt) : CRASH_WEBHOOK, BUG_WEBHOOK. Stockage : KV « REPORTS ».
//
// POST /report, multipart :
//   payload_json = {"kind": "crash"|"bug", "content": "...", "player": {"id": "7656...", "name": "..."},
//                   "signature": "hex (plantages)"}
//   files[0]     = zip du rapport (facultatif)
// Réponse JSON : {"ok": true, "duplicate": false} ; 400 (rapport invalide), 429 (trop de rapports), 502 (Discord).

const MAX_CONTENT = 2000;
const MAX_ZIP = 9_500_000;
// Rapports par heure, par joueur (id Steam) et par adresse IP
const LIMITS = {
  crash: { player: 3, ip: 10 },
  bug: { player: 5, ip: 15 },
};
const DUPLICATE_DAYS = 30;

export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    if (url.pathname !== "/report") return reply({ error: "not found" }, 404);
    if (request.method !== "POST") return reply({ error: "POST only" }, 405);
    try {
      return await report(request, env);
    } catch (e) {
      return reply({ error: "relay error" }, 500);
    }
  },
};

async function report(request, env) {
  let form;
  try {
    form = await request.formData();
  } catch {
    return reply({ error: "multipart form expected" }, 400);
  }
  let payload;
  try {
    payload = JSON.parse(form.get("payload_json"));
  } catch {
    return reply({ error: "payload_json missing or invalid" }, 400);
  }

  // Forme du rapport
  const kind = payload.kind;
  const webhook = kind === "crash" ? env.CRASH_WEBHOOK : kind === "bug" ? env.BUG_WEBHOOK : null;
  if (!webhook) return reply({ error: "kind must be crash or bug" }, 400);
  const content = typeof payload.content === "string" ? payload.content.slice(0, MAX_CONTENT) : "";
  if (!content.trim()) return reply({ error: "empty content" }, 400);
  const playerId = /^\d{1,20}$/.test(payload.player?.id ?? "") ? payload.player.id : "unknown";
  const playerName = String(payload.player?.name ?? "").replace(/[`@\n\r]/g, "'").slice(0, 40) || "unknown";
  const signature = /^[0-9a-fA-F]{8,64}$/.test(payload.signature ?? "") ? payload.signature.toLowerCase() : null;
  let zip = form.get("files[0]");
  if (zip && typeof zip !== "string") {
    if (zip.size > MAX_ZIP) return reply({ error: "zip too large" }, 400);
  } else {
    zip = null;
  }
  const zipName = zip && /^[\w.-]{1,80}\.zip$/.test(zip.name) ? zip.name : `${kind}.zip`;

  // Limites par heure, par joueur et par adresse IP (tiennent même si le mod est contourné)
  const hour = Math.floor(Date.now() / 3_600_000);
  const ip = request.headers.get("CF-Connecting-IP") ?? "unknown";
  const counters = [
    [`rate:${kind}:player:${playerId}:${hour}`, LIMITS[kind].player],
    [`rate:${kind}:ip:${ip}:${hour}`, LIMITS[kind].ip],
  ];
  for (const [key, limit] of counters)
    if (Number((await env.REPORTS.get(key)) ?? 0) >= limit) return reply({ error: "too many reports, try later" }, 429);
  const count = async () => {
    for (const [key] of counters)
      await env.REPORTS.put(key, String(Number((await env.REPORTS.get(key)) ?? 0) + 1), { expirationTtl: 7200 });
  };

  // Plantage déjà signalé (par n'importe quel joueur) : compteur sur le premier message, pas de nouveau message
  if (kind === "crash" && signature) {
    const key = `duplicate:${signature}`;
    const known = await env.REPORTS.get(key, "json");
    if (known) {
      const players = Array.from(new Set([...(known.players ?? []), playerId]));
      const seen = (known.count ?? 1) + 1;
      const footer = `\n**Seen again**: ${seen} times by ${players.length} player(s), last ${new Date().toISOString().slice(0, 16).replace("T", " ")} UTC`;
      const edit = await fetch(
        `${webhook}/messages/${known.messageId}` + (known.threadId ? `?thread_id=${known.threadId}` : ""),
        {
          method: "PATCH",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ content: (known.content + footer).slice(0, MAX_CONTENT), allowed_mentions: { parse: [] } }),
        },
      );
      if (edit.ok) {
        await env.REPORTS.put(key, JSON.stringify({ ...known, players, count: seen }), {
          expirationTtl: DUPLICATE_DAYS * 86400,
        });
        await count();
        return reply({ ok: true, duplicate: true });
      }
      // Premier message supprimé (bug réglé puis revenu) : nouveau rapport complet
      await env.REPORTS.delete(key);
    }
  }

  const sent = await post(env, kind, webhook, content, zip, zipName, playerId, playerName);
  if (!sent.ok) return reply({ error: "discord refused the report" }, 502);
  if (kind === "crash" && signature && sent.messageId)
    await env.REPORTS.put(
      `duplicate:${signature}`,
      JSON.stringify({ messageId: sent.messageId, threadId: sent.threadId, content, count: 1, players: [playerId] }),
      { expirationTtl: DUPLICATE_DAYS * 86400 },
    );
  await count();
  return reply({ ok: true, duplicate: false });
}

// Message par le webhook : dans le fil du joueur si le salon est un forum (fil créé au premier rapport), sinon
// message ordinaire (refus retenu pour les envois suivants).
async function post(env, kind, webhook, content, zip, zipName, playerId, playerName) {
  const threadKey = `thread:${kind}:${playerId}`;
  const noForumKey = `noforum:${kind}`;
  let forum = playerId !== "unknown" && !(await env.REPORTS.get(noForumKey));
  let thread = forum ? await env.REPORTS.get(threadKey) : null;
  const threadName = `${playerName} (${playerId})`.slice(0, 100);

  let response = await send(webhook, content, zip, zipName, thread, forum && !thread ? threadName : null);
  // Fil supprimé : on en recrée un
  if (forum && thread && (response.status === 400 || response.status === 404)) {
    await env.REPORTS.delete(threadKey);
    thread = null;
    response = await send(webhook, content, zip, zipName, null, threadName);
  }
  // Salon ordinaire : pas de fil
  if (forum && !thread && response.status === 400) {
    await env.REPORTS.put(noForumKey, "1", { expirationTtl: 86400 });
    forum = false;
    response = await send(webhook, content, zip, zipName, null, null);
  }
  if (!response.ok) return { ok: false };
  const message = await response.json().catch(() => ({}));
  if (forum && !thread && message.channel_id) await env.REPORTS.put(threadKey, message.channel_id);
  return { ok: true, messageId: message.id, threadId: forum ? message.channel_id : null };
}

async function send(webhook, content, zip, zipName, threadId, threadName) {
  const form = new FormData();
  const payload = { content, allowed_mentions: { parse: [] } };
  if (threadName) payload.thread_name = threadName;
  form.append("payload_json", JSON.stringify(payload));
  if (zip) form.append("files[0]", new Blob([await zip.arrayBuffer()], { type: "application/zip" }), zipName);
  const query = "wait=true" + (threadId ? `&thread_id=${threadId}` : "");
  return fetch(`${webhook}?${query}`, { method: "POST", body: form });
}

function reply(body, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}
