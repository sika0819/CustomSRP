#!/usr/bin/env python3
"""Commit a generated preview mesh into the live low-LOD asset / Oak rebuilt path.

TFF: overwrite *_LOD1.asset bytes, keep .meta GUID; backup under Assets/_scratch/.../backup.
Oak: copy preview to Models/OakRebuilt/, patch prefab MeshFilter guids.

Usage:
  python3 Tools/commit_tree_lod_preview.py GrandFir1
  python3 Tools/commit_tree_lod_preview.py Oak01
"""

from __future__ import annotations

import argparse
import re
import shutil
import sys
import uuid
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SCRATCH = ROOT / "Assets/_scratch/tree_lod_rebuild"
TFF = ROOT / "Assets/GameMain/Environment/Models/TffOilUv"
LARCH = ROOT / "Assets/GameMain/Environment/Models/LarchUv"
OAK_OUT = ROOT / "Assets/GameMain/Environment/Models/OakRebuilt"
LVSHU = ROOT / "Assets/GameMain/Environment/Prefabs/DaoYu1/Lvshu"

TFF_JOBS = {
    **{f"GrandFir{i}": (TFF / f"GrandFir{i}_LOD1.asset", SCRATCH / f"GrandFir{i}/preview/GrandFir{i}_LOD1_preview.asset") for i in range(1, 9)},
    "Spruce3": (TFF / "Spruce3_LOD1.asset", SCRATCH / "Spruce3/preview/Spruce3_LOD1_preview.asset"),
    "Larch3": (LARCH / "Larch3_LOD1.asset", SCRATCH / "Larch3/preview/Larch3_LOD1_preview.asset"),
    "Larch4": (LARCH / "Larch4_LOD1.asset", SCRATCH / "Larch4/preview/Larch4_LOD1_preview.asset"),
    "Larch7": (LARCH / "Larch7_LOD1.asset", SCRATCH / "Larch7/preview/Larch7_LOD1_preview.asset"),
}

OAK_JOBS = {
    "Oak01": {
        "preview": SCRATCH / "Oak01/preview/SM_OakTree_01_LOD2_preview.asset",
        "commit": OAK_OUT / "SM_OakTree_01_LOD2.asset",
        "prefabs": [LVSHU / "SM_Env_Tree_Forest_01.prefab"],
        "child": "LOD2",
    },
    "Oak02": {
        "preview": SCRATCH / "Oak02/preview/SM_OakTree_02_LOD2_preview.asset",
        "commit": OAK_OUT / "SM_OakTree_02_LOD2.asset",
        "prefabs": [LVSHU / "SM_Env_Tree_Forest_02.prefab"],
        "child": "LOD2",
    },
    "Oak03": {
        "preview": SCRATCH / "Oak03/preview/SM_OakTree_03_LOD2_preview.asset",
        "commit": OAK_OUT / "SM_OakTree_03_LOD2.asset",
        "prefabs": [LVSHU / "SM_Env_Tree_Meadow_01.prefab"],
        "child": "LOD2",
    },
    "Oak04": {
        "preview": SCRATCH / "Oak04/preview/SM_OakTree_04_LOD2_preview.asset",
        "commit": OAK_OUT / "SM_OakTree_04_LOD2.asset",
        "prefabs": [LVSHU / "SM_Env_Tree_Forest_03.prefab"],
        "child": "LOD2",
    },
    "Oak05": {
        "preview": SCRATCH / "Oak05/preview/SM_OakTree_05_LOD2_preview.asset",
        "commit": OAK_OUT / "SM_OakTree_05_LOD2.asset",
        "prefabs": [LVSHU / "SM_Env_Tree_Meadow_02.prefab", LVSHU / "Golden Larch 1.prefab"],
        "child": "LOD2",
    },
    "Oak06": {
        "preview": SCRATCH / "Oak06/preview/SM_OakTree_06_LOD2_preview.asset",
        "commit": OAK_OUT / "SM_OakTree_06_LOD2.asset",
        "prefabs": [LVSHU / "SM_Env_Tree_Fruit_01.prefab", LVSHU / "Golden Larch 2.prefab"],
        "child": "LOD2",
    },
    "Oak07": {
        "preview": SCRATCH / "Oak07/preview/SM_OakTree_07_LOD2_preview.asset",
        "commit": OAK_OUT / "SM_OakTree_07_LOD2.asset",
        "prefabs": [LVSHU / "Golden Larch 5.prefab"],
        "child": "LOD2",
    },
}


def _read_guid(meta: Path) -> str:
    text = meta.read_text(encoding="utf-8")
    m = re.search(r"^guid:\s*([0-9a-fA-F]+)", text, re.M)
    if not m:
        raise RuntimeError(f"no guid in {meta}")
    return m.group(1)


def _ensure_meta(asset: Path, guid: str | None = None) -> str:
    meta = Path(str(asset) + ".meta")
    if meta.exists() and guid is None:
        return _read_guid(meta)
    if guid is None:
        guid = uuid.uuid4().hex
    meta.write_text(
        "fileFormatVersion: 2\n"
        f"guid: {guid}\n"
        "NativeFormatImporter:\n"
        "  externalObjects: {}\n"
        "  mainObjectFileID: 4300000\n"
        "  userData: \n"
        "  assetBundleName: \n"
        "  assetBundleVariant: \n",
        encoding="utf-8",
    )
    return guid


def _rewrite_mesh_name(asset_text: str, name: str) -> str:
    return re.sub(r"(m_Name:\s*).*", rf"\1{name}", asset_text, count=1)


def commit_tff(job_id: str) -> str:
    dest, preview = TFF_JOBS[job_id]
    if not preview.exists():
        raise FileNotFoundError(preview)
    backup_dir = SCRATCH / "backup" / job_id
    backup_dir.mkdir(parents=True, exist_ok=True)
    if dest.exists():
        shutil.copy2(dest, backup_dir / dest.name)
        if dest.with_suffix(dest.suffix + ".meta").exists() or Path(str(dest) + ".meta").exists():
            shutil.copy2(Path(str(dest) + ".meta"), backup_dir / (dest.name + ".meta"))
    guid = _read_guid(Path(str(dest) + ".meta"))
    text = preview.read_text(encoding="utf-8")
    text = _rewrite_mesh_name(text, dest.stem)
    dest.write_text(text, encoding="utf-8")
    _ensure_meta(dest, guid)
    return f"tff {job_id}: replaced {dest.relative_to(ROOT)} (guid {guid})"


def _patch_prefab_mesh(prefab: Path, child_substr: str, mesh_guid: str) -> int:
    text = prefab.read_text(encoding="utf-8")
    go_names = {}
    for m in re.finditer(
        r"--- !u!1 &(\d+)\nGameObject:\n(?:  .*\n)*?  m_Name:\s*(.*)",
        text,
    ):
        go_names[m.group(1)] = m.group(2).strip()

    count = 0
    pattern = re.compile(
        r"(--- !u!33 &\d+\nMeshFilter:\n(?:  .*\n)+?  m_GameObject: \{fileID: (\d+)\}\n(?:  .*\n)*?  m_Mesh: \{fileID: [^}]+\})",
        re.M,
    )

    def repl(m: re.Match) -> str:
        nonlocal count
        go_id = m.group(2)
        name = go_names.get(go_id, "")
        block = m.group(1)
        if child_substr.lower() in name.lower() and "billboard" not in name.lower():
            count += 1
            return re.sub(
                r"m_Mesh: \{fileID: [^}]+\}",
                f"m_Mesh: {{fileID: 4300000, guid: {mesh_guid}, type: 2}}",
                block,
                count=1,
            )
        return block

    new_text, _ = pattern.subn(repl, text)
    if count:
        prefab.write_text(new_text, encoding="utf-8")
    return count


def commit_oak(job_id: str) -> str:
    job = OAK_JOBS[job_id]
    preview: Path = job["preview"]
    commit: Path = job["commit"]
    if not preview.exists():
        raise FileNotFoundError(preview)
    OAK_OUT.mkdir(parents=True, exist_ok=True)
    backup_dir = SCRATCH / "backup" / job_id
    backup_dir.mkdir(parents=True, exist_ok=True)
    if commit.exists():
        shutil.copy2(commit, backup_dir / commit.name)

    text = preview.read_text(encoding="utf-8")
    text = _rewrite_mesh_name(text, commit.stem)
    commit.write_text(text, encoding="utf-8")
    if Path(str(preview) + ".meta").exists() and not Path(str(commit) + ".meta").exists():
        # new guid for commit asset
        guid = _ensure_meta(commit)
    elif Path(str(commit) + ".meta").exists():
        guid = _read_guid(Path(str(commit) + ".meta"))
        # refresh asset keep guid
        _ensure_meta(commit, guid)
    else:
        guid = _ensure_meta(commit)

    # Also copy preview meta guid? Prefer stable commit guid.
    total = 0
    for prefab in job["prefabs"]:
        total += _patch_prefab_mesh(prefab, job["child"], guid)
    return f"oak {job_id}: {commit.relative_to(ROOT)} guid={guid} prefab_rewires={total}"


def main(argv=None) -> int:
    p = argparse.ArgumentParser()
    p.add_argument("job_id")
    args = p.parse_args(argv)
    job = args.job_id
    if job in TFF_JOBS:
        print(commit_tff(job))
        return 0
    if job in OAK_JOBS:
        print(commit_oak(job))
        return 0
    print("unknown job", job, file=sys.stderr)
    return 2


if __name__ == "__main__":
    sys.exit(main())
