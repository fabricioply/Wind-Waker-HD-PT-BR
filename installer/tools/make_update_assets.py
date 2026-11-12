#!/usr/bin/env python3
"""Gera os arquivos que o atualizador do instalador espera em cada Release do GitHub.

Uso:  python tools/make_update_assets.py [saida]      (padrão: ./release-assets)

Anexe à release, além do .exe do instalador, os DOIS arquivos gerados:
  - update.json                          (versão + SHA-256 do pacote)
  - wind-waker-ptbr-payload-vX.Y.Z.zip   (manifest.json + .pack da tradução)
A tag da release precisa ser vX.Y.Z, igual à versão do manifest.json.
"""
import hashlib, json, shutil, sys, zipfile
from pathlib import Path

root = Path(__file__).resolve().parent.parent
payload = root / "App" / "Assets" / "payload.zip"
out = Path(sys.argv[1]) if len(sys.argv) > 1 else root / "release-assets"
out.mkdir(parents=True, exist_ok=True)

with zipfile.ZipFile(payload) as z:
    names = sorted(z.namelist())
    assert names == ["content/Common/Pack/permanent_2d_UsEnglish.pack", "manifest.json"], names
    manifest = json.loads(z.read("manifest.json"))
    pack_sha = hashlib.sha256(z.read("content/Common/Pack/permanent_2d_UsEnglish.pack")).hexdigest()

version = manifest["version"]
name = f"wind-waker-ptbr-payload-v{version}.zip"
shutil.copyfile(payload, out / name)
data = (out / name).read_bytes()
(out / "update.json").write_text(json.dumps({
    "version": version,
    "payload": name,
    "payload_sha256": hashlib.sha256(data).hexdigest(),
    "payload_size": len(data),
    "pack_sha256": pack_sha,
}, indent=2) + "\n", encoding="utf-8")
print(f"Gerado em {out}:\n  {name} ({len(data):,} bytes)\n  update.json  (tag da release: v{version})")
