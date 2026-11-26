"use strict";

{
  const results = el("online-results");
  const status = el("online-status");
  const panel = el("scores");
  const query = el("online-query");
  const form = el("online-form");
  const alternatives = el("online-alternatives");
  const external = el("external-score-search");
  const chongchongSearch = "https://esp32-piano-score-lookup.atomicgem65.chatgpt.site/search";
  const normalize = value => value.normalize("NFKC").toLowerCase();
  let indexRequest, busyId = null, searchVersion = 0, searchRequest;

  async function fetchJSON(file, sizeLimit, signal = AbortSignal.timeout(15000)) {
    const response = await fetch(file, { signal, credentials: "omit" });
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

  function render(entries, fromChongchong = false) {
    results.replaceChildren();
    for (const entry of entries) {
      const item = document.createElement("li");
      const text = document.createElement("div");
      const title = document.createElement("strong");
      const detail = document.createElement("p");
      title.textContent = entry.title;
      const action = document.createElement(fromChongchong ? "a" : "button");
      if (fromChongchong) {
        detail.textContent = entry.artist || "虫虫钢琴";
        action.href = "https://www.gangqinpu.com/cchtml/" + entry.id + ".htm";
        action.target = "_blank";
        action.rel = "noopener noreferrer";
        action.textContent = "查看曲谱";
        action.setAttribute("aria-label", "在虫虫钢琴查看「" + entry.title + "」（新标签）");
      } else {
        detail.textContent = entry.author + " · " + entry.bpm + " BPM · " + (entry.polyphony ? "单音 / 多音" : "单音");
        action.type = "button";
        action.dataset.song = songId(entry);
        action.setAttribute("aria-label", "加入曲库：" + entry.title);
        action.onclick = () => add(entry);
      }
      text.append(title, detail);
      item.append(text, action);
      results.append(item);
    }
    refreshButtons();
  }

  async function search() {
    const version = ++searchVersion;
    const keyword = query.value.trim();
    const fromChongchong = form.elements.namedItem("score-source").value === "chongchong";
    searchRequest?.abort();
    searchRequest = new AbortController();
    alternatives.hidden = true;
    results.replaceChildren();
    if (fromChongchong && !keyword) {
      status.textContent = "输入曲名或作者，搜索虫虫钢琴。";
      return;
    }
    status.textContent = fromChongchong ? "正在搜索虫虫钢琴…" : "正在载入本站练习曲…";
    if (fromChongchong) {
      external.href = "https://www.gangqinpu.com/search/0_" + encodeURIComponent(keyword) + "_1.htm";
      alternatives.hidden = false;
    }
    try {
      if (fromChongchong) {
        const url = new URL(chongchongSearch);
        url.searchParams.set("q", keyword);
        const signal = AbortSignal.any([searchRequest.signal, AbortSignal.timeout(15000)]);
        const data = await fetchJSON(url, 64 * 1024, signal);
        if (version !== searchVersion) return;
        if (data?.query !== keyword || !Array.isArray(data.results) || data.results.length > 20 ||
            !data.results.every(entry => entry && /^\d{1,12}$/.test(entry.id) && typeof entry.title === "string" &&
                entry.title && typeof entry.artist === "string")) throw new Error("搜索结果格式无效。");
        render(data.results, true);
        status.textContent = data.results.length ?
          "显示 " + data.results.length + " 条匹配曲谱。请在虫虫详情页查看谱面和获取条件。" :
          "虫虫钢琴的搜索结果里没有匹配「" + keyword + "」的曲谱，可以换个关键词。";
        return;
      }
      const entries = await index();
      if (version !== searchVersion) return;
      const terms = normalize(keyword).split(/\s+/u).filter(Boolean);
      const matches = entries.filter(entry => {
        const searchable = normalize([entry.title, entry.author, ...entry.aliases].join(" "));
        return terms.every(term => searchable.includes(term));
      });
      render(matches);
      status.textContent = matches.length ? (terms.length ? "找到 " + matches.length + " 首曲谱。" : "精选练习曲 · " + matches.length + " 首") :
                "本站练习曲库暂未收录「" + keyword + "」。";
    } catch {
      if (version !== searchVersion) return;
      status.textContent = fromChongchong ? "暂时无法连接虫虫钢琴。可以重试，或在虫虫钢琴查看结果。" :
        "本站练习曲暂时无法载入，请点「搜索」重试。";
    }
  }

  async function add(entry) {
    const id = songId(entry);
    if (busyId || hasSong(id)) return;
    const version = searchVersion;
    busyId = id;
    status.textContent = active ? "正在下载「" + entry.title + "」…" : "正在下载曲谱并开启模拟琴…";
    refreshButtons();
    try {
      const [, song] = await Promise.all([powerOn(), fetchJSON("catalog/" + entry.id + ".json", 120 * 1024)]);
      if (song?.id !== id || song.title !== entry.title) throw new Error("曲谱和目录不匹配，请重试。");
      const notice = importNotice(song, await importSong(song));
      if (version === searchVersion) status.textContent = notice;
    } catch (error) {
      if (version === searchVersion) status.textContent = error.message || "下载失败，请重试。";
    } finally {
      busyId = null;
      refreshButtons();
    }
  }

  form.onsubmit = event => { event.preventDefault(); search(); };
  form.addEventListener("change", event => {
    if (event.target.name !== "score-source") return;
    query.placeholder = event.target.value === "practice" ? "例如 小星星、贝多芬" : "例如 海屿你";
    search();
  });
  panel.addEventListener("toggle", () => {
    if (panel.open && !indexRequest) search();
  });
  el("jump-to-local-score").addEventListener("click", () => requestAnimationFrame(() => el("file").focus()));
  document.addEventListener("piano-library-change", refreshButtons);
  if (panel.open) search();
}
