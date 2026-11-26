"use strict";

{
  const results = el("online-results");
  const status = el("online-status");
  const panel = el("scores");
  const query = el("online-query");
  const alternatives = el("online-alternatives");
  const external = el("external-score-search");
  const normalize = value => value.normalize("NFKC").toLowerCase();
  let indexRequest, busyId = null, searchVersion = 0;

  async function fetchJSON(file, sizeLimit) {
    const response = await fetch(file, { signal: AbortSignal.timeout(15000) });
    if (!response.ok) throw new Error("连接失败，请重试。");
    const text = await response.text();
    if (new TextEncoder().encode(text).length > sizeLimit) throw new Error("曲谱资源超过大小限制。");
    return JSON.parse(text.replace(/^\uFEFF/, ""));
  }

  function index() {
    if (!indexRequest) indexRequest = fetchJSON("catalog/index.json", 64 * 1024).then(entries => {
      if (!Array.isArray(entries) || !entries.every(entry =>
        entry && /^[a-z0-9][a-z0-9-]{0,45}$/.test(entry.id) &&
        typeof entry.title === "string" && typeof entry.author === "string" &&
        Array.isArray(entry.aliases) && entry.aliases.every(alias => typeof alias === "string") &&
        Number.isInteger(entry.bpm) && typeof entry.polyphony === "boolean")) {
        throw new Error("曲库目录格式无效。");
      }
      return entries;
    }).catch(error => { indexRequest = null; throw error; });
    return indexRequest;
  }

  function songId(entry) { return "online-" + entry.id; }

  function refreshButtons() {
    for (const button of results.querySelectorAll("button[data-song]")) {
      const added = hasSong(button.dataset.song);
      button.disabled = added || Boolean(busyId);
      button.textContent = added ? "已在曲库" : button.dataset.song === busyId ? "正在加入…" : "加入曲库";
    }
  }

  function render(entries) {
    results.replaceChildren();
    for (const entry of entries) {
      const item = document.createElement("li");
      const text = document.createElement("div");
      const title = document.createElement("strong");
      const detail = document.createElement("p");
      const button = document.createElement("button");
      title.textContent = entry.title;
      detail.textContent = entry.author + " · " + entry.bpm + " BPM · " + (entry.polyphony ? "单音 / 多音" : "单音");
      button.type = "button";
      button.dataset.song = songId(entry);
      button.setAttribute("aria-label", "加入曲库：" + entry.title);
      button.onclick = () => add(entry);
      text.append(title, detail);
      item.append(text, button);
      results.append(item);
    }
    refreshButtons();
  }

  async function search() {
    const version = ++searchVersion;
    const keyword = query.value.trim();
    const terms = normalize(keyword).split(/\s+/u).filter(Boolean);
    alternatives.hidden = true;
    status.textContent = "正在载入在线曲库…";
    try {
      const entries = await index();
      if (version !== searchVersion) return;
      const matches = entries.filter(entry => {
        const searchable = normalize([entry.title, entry.author, ...entry.aliases].join(" "));
        return terms.every(term => searchable.includes(term));
      });
      render(matches);
      status.textContent = matches.length ? (terms.length ? "找到 " + matches.length + " 首曲谱。" : "精选练习曲 · " + matches.length + " 首") :
                "本站练习曲库暂未收录「" + keyword + "」。";
      if (!matches.length && keyword) {
        external.href = "https://www.gangqinpu.com/search/0_" + encodeURIComponent(keyword) + "_1.htm";
        alternatives.hidden = false;
      }
    } catch {
      if (version !== searchVersion) return;
      results.replaceChildren();
      status.textContent = "在线曲库暂时无法载入，请点「搜索」重试。";
    }
  }

  async function add(entry) {
    const id = songId(entry);
    if (busyId || hasSong(id)) return;
    busyId = id;
    status.textContent = active ? "正在下载「" + entry.title + "」…" : "正在下载曲谱并开启模拟琴…";
    refreshButtons();
    try {
      const [, song] = await Promise.all([powerOn(), fetchJSON("catalog/" + entry.id + ".json", 120 * 1024)]);
      if (song?.id !== id || song.title !== entry.title) throw new Error("曲谱和目录不匹配，请重试。");
      status.textContent = importNotice(song, await importSong(song));
    } catch (error) {
      status.textContent = error.message || "下载失败，请重试。";
    } finally {
      busyId = null;
      refreshButtons();
    }
  }

  el("online-form").onsubmit = event => { event.preventDefault(); search(); };
  panel.addEventListener("toggle", () => {
    if (panel.open && !indexRequest) search();
  });
  el("jump-to-local-score").addEventListener("click", () => requestAnimationFrame(() => el("file").focus()));
  document.addEventListener("piano-library-change", refreshButtons);
  if (panel.open) search();
}
