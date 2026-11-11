"""Fetch official pinned tools into ignored project storage; never replace the host SDK."""
import argparse
import concurrent.futures
import hashlib
import json
from pathlib import Path
import urllib.request
import zipfile

ROOT = Path(__file__).resolve().parents[2]
TOOLS = ROOT / "build/android-tools"
LOCK = json.loads(Path(__file__).with_name("toolchain-lock.json").read_text())
OPENER = urllib.request.build_opener()

def prepare(name):
    item = LOCK[name]
    archive = TOOLS / "downloads" / item["url"].rsplit("/", 1)[1]
    archive.parent.mkdir(parents=True, exist_ok=True)
    def digest(path):
        with path.open("rb") as stream:
            return hashlib.file_digest(stream, item["algorithm"]).hexdigest()
    if not archive.exists() or digest(archive) != item["hash"]:
        partial = archive.with_suffix(".part")
        with OPENER.open(item["url"], timeout=45) as response, partial.open("wb") as stream:
            total = 0
            while chunk := response.read(2 * 1024 * 1024):
                stream.write(chunk); total += len(chunk)
                if total % (128 * 1024 * 1024) == 0: print(name, total // 1048576, "MiB", flush=True)
        if digest(partial) != item["hash"]:
            raise RuntimeError(f"Checksum mismatch: {name}")
        partial.replace(archive)
    target = TOOLS / name
    receipt = target / "verified-tool.json"
    if receipt.exists() and json.loads(receipt.read_text()) == item:
        print(name, "already verified", flush=True); return
    target.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(archive) as source:
        for member in source.infolist():
            if not (target / member.filename).resolve().is_relative_to(target.resolve()):
                raise RuntimeError("Archive path escapes destination")
        source.extractall(target)
    receipt.write_text(json.dumps(item, indent=2))
    print(name, "verified and extracted", flush=True)

if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("tools", nargs="+", choices=list(LOCK))
    parser.add_argument("--proxy", help="Optional local HTTP proxy, used only for tool downloads")
    args = parser.parse_args()
    if args.proxy:
        OPENER = urllib.request.build_opener(urllib.request.ProxyHandler({"http": args.proxy, "https": args.proxy}))
    with concurrent.futures.ThreadPoolExecutor(max_workers=3) as pool:
        for result in pool.map(prepare, args.tools): pass
