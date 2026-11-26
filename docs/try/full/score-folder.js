"use strict";

const PianoScoreFolder = (() => {
  const storeName = "folders";
  const key = "scores";
  let database;

  function openDatabase() {
    if (!database) {
      database = new Promise((resolve, reject) => {
        const open = indexedDB.open("mini-piano-score-folder", 1);
        open.onupgradeneeded = () => open.result.createObjectStore(storeName);
        open.onsuccess = () => resolve(open.result);
        open.onerror = () => reject(open.error);
      }).catch(error => {
        database = undefined;
        throw error;
      });
    }
    return database;
  }

  async function read() {
    const db = await openDatabase();
    return new Promise((resolve, reject) => {
      const request = db.transaction(storeName).objectStore(storeName).get(key);
      request.onsuccess = () => resolve(request.result ?? null);
      request.onerror = () => reject(request.error);
    });
  }

  async function save(folder) {
    const db = await openDatabase();
    return new Promise((resolve, reject) => {
      const transaction = db.transaction(storeName, "readwrite");
      const store = transaction.objectStore(storeName);
      transaction.oncomplete = () => resolve();
      transaction.onabort = () => reject(transaction.error);
      if (folder) store.put(folder, key);
      else store.delete(key);
    });
  }

  async function fromFiles(files) {
    const localFiles = Array.from(files).filter(file => file.webkitRelativePath.split("/").length === 2 &&
      /\.(?:txt|json|ccmz)$/i.test(file.name) && !/\.lyrics\.txt$/i.test(file.name) &&
      file.size > 0 && file.size <= scoreFileSizeLimit(file.name));
    const names = new Set(localFiles.map(file => file.name));
    const originals = localFiles.filter(file => !names.has(file.name.replace(/ \(\d+\)(?=\.[^.]+$)/, "")) ||
      !/ \(\d+\)\.[^.]+$/.test(file.name));
    if (!originals.length) throw new Error("所选文件夹里没有可读取的 CCMZ、TXT 简谱或琴端 JSON。");
    const entries = await Promise.all(originals.map(async file => {
      const fingerprint = JSON.stringify([file.name, file.size, file.lastModified]);
      const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(fingerprint));
      const hash = Array.from(new Uint8Array(digest).subarray(0, 16), byte => byte.toString(16).padStart(2, "0")).join("");
      return { ...scoreFileParts(file.name), id: "folder-" + hash, file };
    }));
    entries.sort((a, b) => a.title.localeCompare(b.title, "zh-CN") || a.file.name.localeCompare(b.file.name, "zh-CN"));
    return { name: originals[0].webkitRelativePath.split("/")[0], entries };
  }

  return { read, save, fromFiles };
})();
