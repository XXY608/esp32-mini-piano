"""CCMZ + sourced vocal recording -> reviewable character/note import bundle."""
import argparse
import hashlib
import io
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import zipfile
import requests
import soundfile as sf
import imageio_ffmpeg
import yt_dlp
from reference_score import prepare_reference
from source_selection import select_lyrics
from audio_source import cached_recording
from requests.adapters import HTTPAdapter

TOOLS = Path(__file__).resolve().parent

def status(message):
    print(message, flush=True)

def save_json(path, data):
    path.write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding="utf-8")

def run(command, log):
    with log.open("w", encoding="utf-8") as output:
        result = subprocess.run(command, stdout=output, stderr=subprocess.STDOUT, cwd=TOOLS / "TIFA-main")
    if result.returncode:
        raise RuntimeError(f"处理失败，详见 {log.name}")

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", required=True, type=Path)
    parser.add_argument("--title", required=True)
    parser.add_argument("--job", required=True, type=Path)
    args = parser.parse_args()
    job = args.job.resolve()
    job.mkdir(parents=True, exist_ok=True)
    raw = args.source.read_bytes()
    if not raw or raw[0] not in (1, 2) or len(raw) > 50 * 1024 * 1024:
        raise ValueError("不是受支持的 CCMZ 文件")
    decoded = raw[1:] if raw[0] == 1 else bytes(x ^ 1 for x in raw[1:])
    with zipfile.ZipFile(io.BytesIO(decoded)) as archive:
        names = ("data.ccmid", "data.ccxml") if raw[0] == 1 else ("midi.json", "score.json")
        for source, destination in zip(names, ("midi.json", "score.json")):
            entry = archive.getinfo(source)
            if entry.file_size > 16 * 1024 * 1024:
                raise ValueError("CCMZ 内容超过大小限制")
            (job / destination).write_bytes(archive.read(entry))
    midi = json.loads((job / "midi.json").read_text(encoding="utf-8-sig"))
    ticks = next(iter(midi["measures"].values()))["duration"] / midi["beats"]
    seconds_per_tick = midi["tempos"][0]["tempo"] / 1e6 / ticks
    duration = max((e.get("tick", 0) + e.get("duration", 0)) * seconds_per_tick for e in midi["events"])
    status("1/5 搜索同曲歌词与完整人声来源…")
    session = requests.Session()
    session.mount('https://lrclib.net/', HTTPAdapter(max_retries=2))
    response = session.get("https://lrclib.net/api/search", params={"q": args.title}, timeout=40)
    response.raise_for_status()
    score_metadata = json.loads((job / 'score.json').read_text(encoding='utf-8-sig'))
    known = json.loads((TOOLS / "known-sources.json").read_text(encoding="utf-8"))
    selected = select_lyrics(response.json(), args.title, duration, score_metadata, known)
    save_json(job / "selected-lyrics.json", selected)
    source = next((s for s in known if s["title"] == selected["trackName"]), None)
    if source is None:
        query = f"ytsearch5:{selected['trackName']} {selected['artistName']} official audio"
        with yt_dlp.YoutubeDL({"quiet": True, "skip_download": True, "noplaylist": True, "socket_timeout": 30}) as downloader:
            found = downloader.extract_info(query, download=False)
        sources = [e for e in found.get("entries", []) if e and e.get("duration")
                   and abs(e["duration"] - selected["duration"]) <= max(15, selected["duration"] * .12)
                   and not any(k in e.get("title", "").lower() for k in ("伴奏", "纯音乐", "instrumental", "piano cover"))]
        if not sources:
            raise ValueError("没有找到时长相符的完整人声来源，未更改曲谱")
        video = min(sources, key=lambda e: abs(e["duration"] - selected["duration"]) - (3 if "官方" in e.get("title", "") else 0))
        source = {"url": video.get("webpage_url") or video["url"]}
    if source.get('score_url'):
        try:
            prepare_reference(job, TOOLS, source['score_url'])
        except (requests.RequestException, ValueError) as error:
            (job / 'reference-score.log').write_text('未使用参考带词谱：' + str(error), encoding='utf-8')
            status('参考带词谱暂不可用，继续音频对齐并保留未确认的诊断…')
    audio = job / "audio"
    audio.mkdir(exist_ok=True)
    cached_provenance = TOOLS / "audio/provenance.json"
    cached = json.loads(cached_provenance.read_text(encoding="utf-8")) if cached_provenance.exists() else {}
    if cached.get("webpage_url") == source["url"] and (TOOLS / "audio/reference.wav").exists():
        shutil.copyfile(TOOLS / "audio/reference.wav", audio / "reference.wav")
        provenance = cached
    else:
        cached_source = cached_recording(TOOLS, source['url'])
        if cached_source:
            recording, provenance = cached_source
            status('2/5 复用同一来源且 SHA256 一致的完整录音…')
            downloaded = audio / recording.name
            shutil.copyfile(recording, downloaded)
            provenance = dict(provenance, retrieval='verified-local-source')
        else:
            status("2/5 下载有来源的完整人声录音…")
            options = {"format": "bestaudio/best", "noplaylist": True, "quiet": True, "socket_timeout": 30,
                       "retries": 1, "outtmpl": str(audio / "reference.%(ext)s")}
            with yt_dlp.YoutubeDL(options) as downloader:
                info = downloader.extract_info(source["url"], download=True)
                downloaded = Path(downloader.prepare_filename(info))
            provenance = {k: info.get(k) for k in ("webpage_url", "title", "channel", "channel_url", "duration")}
            provenance["sha256"] = hashlib.sha256(downloaded.read_bytes()).hexdigest()
            provenance['retrieval'] = 'download'
        subprocess.run([imageio_ffmpeg.get_ffmpeg_exe(), "-v", "error", "-y", "-i", str(downloaded),
                        "-ac", "1", "-ar", "44100", str(audio / "reference.wav")], check=True)
    audio_info = sf.info(audio / "reference.wav")
    if abs(audio_info.duration - selected["duration"]) > max(15, selected["duration"] * .12):
        raise ValueError("录音时长与歌词版本不符，未使用该录音")
    provenance["audio_seconds"] = audio_info.duration
    provenance["source_ccmz_sha256"] = hashlib.sha256(raw).hexdigest()
    save_json(audio / "provenance.json", provenance)
    os.environ["TORCH_HOME"] = str(TOOLS / "models/torch")
    os.environ["HF_HOME"] = str(TOOLS / "models/huggingface")
    os.environ["PYTHONIOENCODING"] = "utf-8"
    status("3/5 分离人声，提取逐字时间（本机处理）…")
    run([sys.executable, "-m", "demucs", "--two-stems", "vocals", "--other-method", "none", "--shifts", "0",
         "-d", "cpu", "-o", str(job / "separated"), str(audio / "reference.wav")], job / "demucs.log")
    run([sys.executable, str(TOOLS / 'calibrate_segments.py'), str(job), str(TOOLS)], job / 'lead-in.log')
    run([sys.executable, str(TOOLS / "prepare_segments.py"), str(job)], job / "prepare.log")
    run([sys.executable, "infer.py", str(job / "segments"), "-m", str(TOOLS / "models/TIFA-1.0/model.ckpt"),
         "--g2p", str(TOOLS / "models/TIFA-1.0/g2p-zh.yaml"), "-l", "zh", "-L", "en", "-o", str(job / "aligned"),
         "--num-workers", "0", "--batch-size", "1", "--skip-handling", "preserve", "--stat"], job / "tifa.log")
    grids = list((job / "aligned").glob("*.TextGrid"))
    expected = json.loads((job / "segments.json").read_text(encoding="utf-8"))
    if len(grids) != len(expected):
        raise ValueError("部分歌词未能完成发音对齐，详见 tifa.log；未导入不完整结果")
    run([sys.executable, str(TOOLS / 'refine_overlaps.py'), str(job), str(TOOLS)], job / 'refine.log')
    status("4/5 匹配原曲旋律与 CCMZ，定位每个字的音符区间…")
    run([sys.executable, str(TOOLS / "align_score.py"), str(job)], job / "alignment.log")
    status("5/5 生成逐字音符对应与同音拆分记录…")
    run([sys.executable, str(TOOLS / "build_bound_score.py"), str(job)], job / "bundle.log")
    status("逐字音符对应已生成，正在自动导入并保存…")

if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        print(str(error), file=sys.stderr, flush=True)
        sys.exit(1)
