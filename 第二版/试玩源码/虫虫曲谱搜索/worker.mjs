const pianoOrigin = "https://xxy608.github.io";
const upstream = "https://www.gangqinpu.com";
const maximumPageBytes = 2 * 1024 * 1024;
const entities = { amp: "&", quot: '"', apos: "'", lt: "<", gt: ">", nbsp: " " };

function plainText(html) {
  return html.replace(/<[^>]*>/g, "").replace(/&(#x[\da-f]+|#\d+|[a-z]+);/gi, (original, entity) => {
    if (entity[0] !== "#") return Object.hasOwn(entities, entity) ? entities[entity] : original;
    const point = /^#x/i.test(entity) ? Number.parseInt(entity.slice(2), 16) : Number(entity.slice(1));
    return point > 0 && point <= 0x10ffff ? String.fromCodePoint(point) : original;
  }).replace(/\s+/g, " ").trim().slice(0, 160);
}

function normalize(value) {
  return value.normalize("NFKC").toLowerCase().replace(/[^\p{L}\p{N}]/gu, "");
}

export function parseResults(html, query) {
  // The score tab may display popular songs for a term it cannot match.
  if (!html.includes('class="search_result"')) throw new Error("Search page format changed");
  const terms = query.split(/\s+/).map(normalize).filter(Boolean);
  const results = new Map();
  for (const [, group] of html.matchAll(/<hgroup\b[^>]*\bclass=["']audio-info["'][^>]*>([\s\S]*?)<\/hgroup>/gi)) {
    const heading = /<h[1-3]\b[^>]*>\s*<a\b([^>]*)>([\s\S]*?)<\/a>/i.exec(group);
    const id = heading?.[1].match(/\bhref=["'](?:https:\/\/www\.gangqinpu\.com)?\/cchtml\/(\d{1,12})\.htm["']/i)?.[1];
    if (!id || results.has(id)) continue;
    const title = plainText(heading[2]);
    const artist = plainText(group.match(/<a\b[^>]*\bhref=["'](?:https:\/\/www\.gangqinpu\.com)?\/specialtype\/[^"']+["'][^>]*>([\s\S]*?)<\/a>/i)?.[1] ?? "");
    const searchable = normalize(title + " " + artist);
    if (!title || !terms.every(term => searchable.includes(term))) continue;
    results.set(id, { id, title, artist });
  }
  return [...results.values()].slice(0, 20);
}

async function readSearchPage(response) {
  if (!response.ok || !response.body || !response.headers.get("content-type")?.includes("text/html")) {
    throw new Error("Upstream response unavailable");
  }
  const reader = response.body.getReader();
  const decoder = new TextDecoder();
  let bytes = 0;
  let html = "";
  try {
    for (;;) {
      const { value, done } = await reader.read();
      if (done) break;
      bytes += value.byteLength;
      if (bytes > maximumPageBytes) {
        await reader.cancel();
        throw new Error("Upstream response too large");
      }
      html += decoder.decode(value, { stream: true });
    }
    return html + decoder.decode();
  } finally {
    reader.releaseLock();
  }
}

function json(data, status = 200, cache = "no-store") {
  return Response.json(data, { status, headers: {
    "Access-Control-Allow-Origin": pianoOrigin,
    "Cache-Control": cache,
    "Vary": "Origin",
    "X-Content-Type-Options": "nosniff"
  }});
}

export default {
  async fetch(request) {
    const url = new URL(request.url);
    if (url.pathname === "/") return new Response("ESP32 迷你电子琴曲谱搜索", { headers: { "Content-Type": "text/plain; charset=utf-8" } });
    if (url.pathname !== "/search") return json({ error: "找不到此接口。" }, 404);
    if (request.headers.get("Origin") !== pianoOrigin) return json({ error: "此接口供电子琴试玩页使用。" }, 403);
    if (request.method !== "GET") return json({ error: "此接口只接受搜索请求。" }, 405);
    const query = url.searchParams.get("q")?.trim() ?? "";
    if (query.length > 100 || !normalize(query) || /[\/\\\u0000-\u001f\u007f]/u.test(query)) {
      return json({ error: "请输入有效的曲名或作者。" }, 400);
    }
    try {
      const response = await fetch(`${upstream}/search/0_${encodeURIComponent(query)}_1.htm`, {
        redirect: "manual",
        headers: { "Accept": "text/html" },
        signal: AbortSignal.timeout(10000),
        cf: { cacheEverything: true, cacheTtlByStatus: { "200": 120, "300-599": 0 } }
      });
      return json({ results: parseResults(await readSearchPage(response), query) }, 200, "private, max-age=120");
    } catch {
      return json({ error: "暂时无法读取虫虫钢琴的搜索结果，请重试。" }, 502);
    }
  }
};
