"use strict";

{
  const results = el("online-results");
  const status = el("online-status");
  const importStatus = el("online-import-status");
  const returnToPiano = el("return-to-piano");
  const folderButton = el("add-score-folder");
  const removeFolder = el("remove-score-folder");
  const folderFiles = el("score-folder-files");
  const folderStatus = el("folder-status");
  const folderResults = el("folder-results");
  const panel = el("scores");
  const query = el("online-query");
  const form = el("online-form");
  const chongchongSearch = "https://esp32-piano-score-lookup.atomicgem65.chatgpt.site/search";
  const normalize = value => value.normalize("NFKC").toLowerCase();
  let indexRequest, busyId = null, searchVersion = 0, searchRequest;
  let folder = null;

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
    for (const button of panel.querySelectorAll("button[data-song]")) {
      const added = hasSong(button.dataset.song);
      button.disabled = added || Boolean(busyId);
      button.textContent = added ? "已在曲库" : button.dataset.song === busyId ? "正在加入…" : button.dataset.label;
    }
  }

  function scoreRow(titleText, detailText, action) {
    const item = document.createElement("li");
    const text = document.createElement("div");
    const title = document.createElement("strong");
    const detail = document.createElement("p");
    title.textContent = titleText;
    detail.textContent = detailText;
    text.append(title, detail);
    item.append(text, action);
    return item;
  }

  function addButton(id, title, label, load) {
    const button = document.createElement("button");
    button.type = "button";
    button.dataset.song = id;
    button.dataset.label = label;
    button.setAttribute("aria-label", label + "：" + title);
    button.onclick = () => add(id, title, load);
    return button;
  }

  function renderFolder(keyword) {
    const terms = normalize(keyword).split(/\s+/u).filter(Boolean);
    const matches = terms.length && folder ?
      folder.entries.filter(entry => terms.every(term => normalize(entry.title).includes(term))) : [];
    el("folder-matches").hidden = !matches.length;
    folderResults.replaceChildren(...matches.map(entry => scoreRow(entry.title,
      entry.bpm ? entry.bpm + " BPM · " + entry.file.name : entry.file.name,
      addButton(entry.id, entry.title, "加入曲库", () => readScoreFile(entry.file, entry.id)))));
    refreshButtons();
  }

  function showFolder() {
    folderButton.textContent = folder ? "更新简谱文件夹" : "添加简谱文件夹";
    removeFolder.hidden = !folder;
    folderStatus.textContent = folder ?
      "已添加「" + folder.name + "」的 " + folder.entries.length + " 份曲谱。搜索曲名即可一键加入。" :
      "添加一次文件夹，即可反复搜索并一键加入。文件只存在当前浏览器。";
    renderFolder(query.value.trim());
  }

  function render(entries, fromChongchong = false) {
    results.replaceChildren(...entries.map(entry => {
      if (fromChongchong) {
        const availability = document.createElement("span");
        availability.className = "score-unavailable";
        availability.textContent = "暂无可导入谱";
        return scoreRow(entry.title, entry.artist || "虫虫钢琴", availability);
      }
      const id = songId(entry);
      const detail = entry.author + " · " + entry.bpm + " BPM · " + (entry.polyphony ? "单音 / 多音" : "单音");
      return scoreRow(entry.title, detail, addButton(id, entry.title, "下载并导入", async () => {
        const song = await fetchJSON("catalog/" + entry.id + ".json", 120 * 1024);
        if (song?.id !== id || song.title !== entry.title) throw new Error("曲谱和目录不匹配，请重试。");
        return song;
      }));
    }));
    refreshButtons();
  }

  async function search() {
    const version = ++searchVersion;
    const keyword = query.value.trim();
    const fromChongchong = form.elements.namedItem("score-source").value === "chongchong";
    searchRequest?.abort();
    searchRequest = new AbortController();
    renderFolder(keyword);
    results.replaceChildren();
    if (fromChongchong && !keyword) {
      status.textContent = "输入曲名或作者，搜索虫虫钢琴。";
      return;
    }
    status.textContent = fromChongchong ? "正在搜索虫虫钢琴…" : "正在载入本站练习曲…";
    try {
      if (fromChongchong) {
        const url = new URL(chongchongSearch);
        url.searchParams.set("q", keyword);
        const signal = AbortSignal.any([searchRequest.signal, AbortSignal.timeout(15000)]);
        const data = await fetchJSON(url, 64 * 1024, signal);
        if (version !== searchVersion) return;
        if (!Array.isArray(data?.results) || data.results.length > 20 ||
            !data.results.every(entry => entry && /^\d{1,12}$/.test(entry.id) && typeof entry.title === "string" &&
                entry.title && typeof entry.artist === "string")) throw new Error("搜索结果格式无效。");
        render(data.results, true);
        status.textContent = data.results.length ?
          "虫虫钢琴找到 " + data.results.length + " 条曲谱。当前未提供可直接导入的乐谱文件。" :
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
      status.textContent = fromChongchong ? "暂时无法连接虫虫钢琴，请重试。" :
        "本站练习曲暂时无法载入，请点「搜索」重试。";
    }
  }

  async function add(id, title, load) {
    if (busyId || hasSong(id)) return;
    busyId = id;
    refreshButtons();
    returnToPiano.hidden = true;
    importStatus.textContent = "正在加入「" + title + "」…";
    try {
      const [, song] = await Promise.all([powerOn(), load()]);
      importStatus.textContent = importNotice(song, await importSong(song));
      returnToPiano.hidden = false;
    } catch (error) {
      importStatus.textContent = error.message || "曲谱导入失败。";
    } finally {
      busyId = null;
      refreshButtons();
    }
  }

  async function changeFolder(load) {
    folderButton.disabled = removeFolder.disabled = true;
    try {
      const next = await load();
      await PianoScoreFolder.save(next);
      folder = next;
      showFolder();
    } catch (error) {
      folderStatus.textContent = error.message || "无法保存简谱文件夹，请重试。";
    } finally {
      folderButton.disabled = removeFolder.disabled = false;
    }
  }

  folderButton.onclick = () => { folderFiles.value = ""; folderFiles.click(); };
  folderFiles.onchange = () => {
    if (folderFiles.files.length) changeFolder(() => PianoScoreFolder.fromFiles(folderFiles.files));
  };
  removeFolder.onclick = () => changeFolder(() => null);
  folderButton.disabled = true;
  PianoScoreFolder.read().then(saved => {
    folder = saved;
    showFolder();
  }).catch(() => { folderStatus.textContent = "暂时无法读取浏览器里的简谱文件夹。"; })
    .finally(() => { folderButton.disabled = false; });

  form.onsubmit = event => { event.preventDefault(); search(); };
  form.addEventListener("change", event => {
    if (event.target.name !== "score-source") return;
    query.placeholder = event.target.value === "practice" ? "例如 小星星、贝多芬" : "例如 海屿你";
    search();
  });
  panel.addEventListener("toggle", () => {
    if (panel.open && !indexRequest) search();
  });
  document.addEventListener("piano-library-change", refreshButtons);
  if (panel.open) search();
}
