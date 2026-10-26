#!/usr/bin/env python3
"""
Pack a Unity Hub custom template for the oil-painting Custom SRP.

Hub 3.18+ custom templates:
  ~/Unity user templates/<sanitized-name>/<sanitized-name>.tgz
  + sibling package.json

Critical: package.json "unity" must equal the FULL editor version
(e.g. 6000.6.4f1). Hub filters with template.unity === editorVersion,
so "6000.6" alone is hidden when you pick 6000.6.4f1 in New Project.

Usage:
  python3 Tools/OilCustomSrpTemplate/pack.py
  python3 Tools/OilCustomSrpTemplate/pack.py --also-editor-templates
  python3 Tools/OilCustomSrpTemplate/pack.py --unity 6000.6.4f1
"""

from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import subprocess
import tarfile
import tempfile
import uuid
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]  # CustomSRP project root
TOOL = Path(__file__).resolve().parent
DEFAULT_OUT = Path.home() / "Unity user templates"

PACKAGE_NAME = "com.boluo.template.customsrp-oil"
DISPLAY_NAME = "3D CustomSRP Oil NPR"
# Folder/archive name Hub uses (sanitizeTemplateFileName of displayName).
HUB_DIR_NAME = "3d-customsrp-oil-npr"
PIPELINE_GUID = "56cebf3f603e74345968fb3371d4157d"
EMPTY_SCENE_GUID = "1ef6dd0d5af3e480e83d1fda14f06428"
EMPTY_SCENE_PATH = "Assets/CustomSRP/Scenes/Empty.unity"

# Game-coupled / island-specific sources omitted from the reusable template.
EXCLUDE_RELATIVE = {
    "Assets/CustomSRP/Runtime/OilAtmosphereDriver.cs",
    "Assets/CustomSRP/Runtime/OilAtmosphereDriver.cs.meta",
    "Assets/CustomSRP/Editor/BakeTreeBillboardNormalsFromLod0.cs",
    "Assets/CustomSRP/Editor/BakeTreeBillboardNormalsFromLod0.cs.meta",
    "Assets/CustomSRP/Editor/BakeTreeBillboardFromLod0.cs",
    "Assets/CustomSRP/Editor/BakeTreeBillboardFromLod0.cs.meta",
    "Assets/CustomSRP/Editor/OilWaterReflectBakeTool.cs",
    "Assets/CustomSRP/Editor/OilWaterReflectBakeTool.cs.meta",
    "Assets/CustomSRP/Editor/CreateMooreaTerrainScene.cs",
    "Assets/CustomSRP/Editor/CreateMooreaTerrainScene.cs.meta",
    "Assets/CustomSRP/Editor/MooreaHeightmapPostprocessor.cs",
    "Assets/CustomSRP/Editor/MooreaHeightmapPostprocessor.cs.meta",
    "Assets/CustomSRP/Editor/CreateIslandTerrainScene.cs",
    "Assets/CustomSRP/Editor/CreateIslandTerrainScene.cs.meta",
    "Assets/CustomSRP/Editor/CylindricalUVFixUtility.cs",
    "Assets/CustomSRP/Editor/CylindricalUVFixUtility.cs.meta",
    "Assets/CustomSRP/Editor/AddRainSystemToPrefab.cs",
    "Assets/CustomSRP/Editor/AddRainSystemToPrefab.cs.meta",
    "Assets/CustomSRP/Editor/OilTerrainGrassBakeTool.cs",
    "Assets/CustomSRP/Editor/OilTerrainGrassBakeTool.cs.meta",
    "Assets/CustomSRP/Editor/OilTerrainGrassKeepOutBakeTool.cs",
    "Assets/CustomSRP/Editor/OilTerrainGrassKeepOutBakeTool.cs.meta",
    "Assets/CustomSRP/doc",
}

# Keep Empty sample only — skip heavy demo scenes under CustomSRP/Scenes.
SCENE_KEEP = {"Empty.unity", "Empty.unity.meta", "Empty"}

PROJECT_SETTINGS_SKIP = {
    "ProjectVersion.txt",  # Hub stamps editor version on create
    "ProjectAuditorSettings.asset",
}


def load_package_json() -> dict:
    return json.loads((TOOL / "package.json").read_text(encoding="utf-8"))


def write_meta(path: Path, importer: str = "MonoImporter") -> None:
    guid = uuid.uuid4().hex
    if importer == "MonoImporter":
        path.write_text(
            "fileFormatVersion: 2\n"
            f"guid: {guid}\n"
            "MonoImporter:\n"
            "  externalObjects: {}\n"
            "  serializedVersion: 2\n"
            "  defaultReferences: []\n"
            "  executionOrder: 0\n"
            "  icon: {instanceID: 0}\n"
            "  userData: \n"
            "  assetBundleName: \n"
            "  assetBundleVariant: \n",
            encoding="utf-8",
        )
    elif importer == "DefaultImporter":
        path.write_text(
            "fileFormatVersion: 2\n"
            f"guid: {guid}\n"
            "DefaultImporter:\n"
            "  externalObjects: {}\n"
            "  userData: \n"
            "  assetBundleName: \n"
            "  assetBundleVariant: \n",
            encoding="utf-8",
        )
    else:
        path.write_text(
            "fileFormatVersion: 2\n"
            f"guid: {guid}\n"
            "folderAsset: yes\n"
            "DefaultImporter:\n"
            "  externalObjects: {}\n"
            "  userData: \n"
            "  assetBundleName: \n"
            "  assetBundleVariant: \n",
            encoding="utf-8",
        )


def should_copy_customsrp(rel: str) -> bool:
    norm = rel.replace("\\", "/")
    if norm in EXCLUDE_RELATIVE:
        return False
    for excl in EXCLUDE_RELATIVE:
        if excl.endswith("/") or not excl.endswith((".cs", ".meta")):
            if norm == excl or norm.startswith(excl.rstrip("/") + "/"):
                return False
    # Scenes: Empty only
    if "/Scenes/" in norm or norm.endswith("/Scenes"):
        parts = norm.split("/")
        try:
            idx = parts.index("Scenes")
        except ValueError:
            return True
        rest = parts[idx + 1 :]
        if not rest:
            return True
        if rest[0] not in SCENE_KEEP and rest[0] + ".unity" not in SCENE_KEEP:
            # allow Empty.meta folder? Empty is a file; skip MooreaTerrain etc.
            if rest[0] not in ("Empty.unity", "Empty.unity.meta"):
                return False
    return True


def copy_customsrp(dst_assets: Path) -> None:
    src = ROOT / "Assets" / "CustomSRP"
    dst = dst_assets / "CustomSRP"
    for dirpath, dirnames, filenames in os.walk(src):
        rel_dir = Path(dirpath).relative_to(ROOT).as_posix()
        # prune excluded dirs early
        dirnames[:] = [
            d
            for d in dirnames
            if should_copy_customsrp(f"{rel_dir}/{d}")
        ]
        out_dir = dst_assets / Path(rel_dir).relative_to("Assets")
        out_dir.mkdir(parents=True, exist_ok=True)
        # folder .meta
        folder_meta = Path(dirpath).with_suffix(Path(dirpath).suffix + ".meta")
        # Unity folder metas are SiblingName.meta next to folder — handled below via walk of parent
        for name in filenames:
            rel = f"{rel_dir}/{name}"
            if not should_copy_customsrp(rel):
                continue
            shutil.copy2(Path(dirpath) / name, out_dir / name)

    # Ensure Assets/CustomSRP.meta exists
    src_meta = ROOT / "Assets" / "CustomSRP.meta"
    if src_meta.is_file():
        shutil.copy2(src_meta, dst_assets / "CustomSRP.meta")


def copy_stubs(dst_assets: Path) -> None:
    stub_root = dst_assets / "TemplateStubs"
    stub_root.mkdir(parents=True, exist_ok=True)
    write_meta(dst_assets / "TemplateStubs.meta", importer="Folder")
    for src in sorted((TOOL / "stubs").glob("*.cs")):
        dst = stub_root / src.name
        shutil.copy2(src, dst)
        write_meta(Path(str(dst) + ".meta"), importer="MonoImporter")


def write_readme_asset(dst_assets: Path) -> None:
    readme = dst_assets / "README.md"
    readme.write_text(
        "# CustomSRP Oil NPR\n\n"
        "Unity 6 oil-painting **Custom Scriptable Render Pipeline** starter.\n\n"
        "## First steps\n\n"
        "1. Open `Assets/CustomSRP/Scenes/Empty.unity`.\n"
        "2. Confirm **Edit → Project Settings → Graphics** uses "
        "`CustomSRP/Settings/CustomRenderPipelineAsset`.\n"
        "3. Color space is **Linear**.\n"
        "4. Menu **CustomSRP → Create & Assign Pipeline Asset** if Graphics is empty.\n"
        "5. Assign `CustomSRP/OilNPR` to materials for the oil look.\n\n"
        "## What's included\n\n"
        "- Custom SRP (Render Graph): lighting, shadows, Post FX, bloom, LUT, FXAA\n"
        "- OilNPR / TerrainOilNPR / OilSkyboxNPR / outline / ocean / river shaders\n"
        "- Empty sample scene + pipeline / PostFX settings\n"
        "- `Assets/TemplateStubs` — tiny Game/Log facades so the pipeline compiles alone\n\n"
        "## Not included\n\n"
        "GameMain island, GameFramework, YooAsset, terrain data, Moorea demo scenes.\n"
        "Replace stubs when you wire your own game services.\n",
        encoding="utf-8",
    )
    write_meta(Path(str(readme) + ".meta"), importer="DefaultImporter")


def copy_project_settings(dst: Path) -> None:
    src = ROOT / "ProjectSettings"
    out = dst / "ProjectSettings"
    out.mkdir(parents=True, exist_ok=True)
    for item in src.iterdir():
        if item.name in PROJECT_SETTINGS_SKIP:
            continue
        if item.is_file():
            shutil.copy2(item, out / item.name)

    # Point build settings at Empty sample
    ebs = out / "EditorBuildSettings.asset"
    ebs.write_text(
        "%YAML 1.1\n"
        "%TAG !u! tag:unity3d.com,2011:\n"
        "--- !u!1045 &1\n"
        "EditorBuildSettings:\n"
        "  m_ObjectHideFlags: 0\n"
        "  serializedVersion: 2\n"
        "  m_Scenes:\n"
        f"  - enabled: 1\n"
        f"    path: {EMPTY_SCENE_PATH}\n"
        f"    guid: {EMPTY_SCENE_GUID}\n"
        "  m_configObjects: {}\n"
        "  m_UseUCBPForAssetBundles: 0\n",
        encoding="utf-8",
    )

    # Neutral product name
    ps = out / "ProjectSettings.asset"
    text = ps.read_text(encoding="utf-8")
    text = re.sub(r"companyName:.*", "companyName: CustomSRP", text)
    text = re.sub(r'productName:.*', 'productName: Oil CustomSRP', text)
    # Ensure Linear (1)
    if "m_ActiveColorSpace:" in text:
        text = re.sub(r"m_ActiveColorSpace:\s*\d+", "m_ActiveColorSpace: 1", text)
    ps.write_text(text, encoding="utf-8")

    # Ensure Graphics + Quality still reference pipeline GUID
    for name in ("GraphicsSettings.asset", "QualitySettings.asset"):
        p = out / name
        t = p.read_text(encoding="utf-8")
        if PIPELINE_GUID not in t:
            print(f"WARNING: {name} missing pipeline GUID {PIPELINE_GUID}")


def write_packages(dst: Path, pkg: dict) -> None:
    packages = dst / "Packages"
    packages.mkdir(parents=True, exist_ok=True)
    deps = dict(pkg.get("dependencies") or {})
    # Always include core engine modules used by a 3D project
    modules = {
        "com.unity.modules.ai": "1.0.0",
        "com.unity.modules.androidjni": "1.0.0",
        "com.unity.modules.animation": "1.0.0",
        "com.unity.modules.assetbundle": "1.0.0",
        "com.unity.modules.audio": "1.0.0",
        "com.unity.modules.imageconversion": "1.0.0",
        "com.unity.modules.imgui": "1.0.0",
        "com.unity.modules.jsonserialize": "1.0.0",
        "com.unity.modules.particlesystem": "1.0.0",
        "com.unity.modules.physics": "1.0.0",
        "com.unity.modules.physicscore2d": "1.0.0",
        "com.unity.modules.screencapture": "1.0.0",
        "com.unity.modules.terrain": "1.0.0",
        "com.unity.modules.terrainphysics": "1.0.0",
        "com.unity.modules.ui": "1.0.0",
        "com.unity.modules.uielements": "1.0.0",
        "com.unity.modules.umbra": "1.0.0",
        "com.unity.modules.unitywebrequest": "1.0.0",
        "com.unity.modules.unitywebrequestassetbundle": "1.0.0",
        "com.unity.modules.unitywebrequestaudio": "1.0.0",
        "com.unity.modules.unitywebrequesttexture": "1.0.0",
        "com.unity.modules.unitywebrequestwww": "1.0.0",
        "com.unity.modules.video": "1.0.0",
        "com.unity.modules.wind": "1.0.0",
    }
    deps.update(modules)
    (packages / "manifest.json").write_text(
        json.dumps({"dependencies": deps}, indent=2) + "\n",
        encoding="utf-8",
    )


def write_package_docs(package_root: Path, pkg: dict) -> None:
    (package_root / "package.json").write_text(
        json.dumps(pkg, indent=2) + "\n", encoding="utf-8"
    )
    write_meta(package_root / "package.json.meta", importer="DefaultImporter")

    (package_root / "README.md").write_text(
        f"# {DISPLAY_NAME}\n\n"
        f"{pkg.get('description', '')}\n\n"
        "Create a new project from this template in Unity Hub "
        "(Templates → 3D CustomSRP Oil NPR).\n",
        encoding="utf-8",
    )
    write_meta(package_root / "README.md.meta", importer="DefaultImporter")

    (package_root / "CHANGELOG.md").write_text(
        f"# Changelog\n\n## [{pkg['version']}] - 2026-10-23\n\n"
        "- Initial Hub template packed from CustomSRP oil NPR pipeline.\n",
        encoding="utf-8",
    )
    write_meta(package_root / "CHANGELOG.md.meta", importer="DefaultImporter")

    (package_root / "LICENSE.md").write_text(
        "Copyright (c) CustomSRP Oil NPR template authors.\n"
        "Use and modify for your projects. Shaders/runtime follow the source project license.\n",
        encoding="utf-8",
    )
    write_meta(package_root / "LICENSE.md.meta", importer="DefaultImporter")

    docs = package_root / "Documentation~"
    docs.mkdir(exist_ok=True)
    (docs / "index.md").write_text(
        f"# {DISPLAY_NAME}\n\n"
        "Oil-painting Custom SRP starter for Unity 6.\n\n"
        "## Open the sample\n\n"
        f"- Scene: `{EMPTY_SCENE_PATH}`\n"
        "- Menu: CustomSRP → Create Empty Scene / Create & Assign Pipeline Asset\n",
        encoding="utf-8",
    )


def read_project_unity_version() -> str:
    """Full editor version Hub compares against (must be exact, e.g. 6000.6.4f1)."""
    pv = ROOT / "ProjectSettings" / "ProjectVersion.txt"
    if pv.is_file():
        for line in pv.read_text(encoding="utf-8").splitlines():
            if line.startswith("m_EditorVersion:"):
                return line.split(":", 1)[1].strip()
    return "6000.6.4f1"


def make_tgz(package_root: Path, out_tgz: Path) -> None:
    out_tgz.parent.mkdir(parents=True, exist_ok=True)
    if out_tgz.exists():
        out_tgz.unlink()
    # Hub expects archive root = package/
    with tarfile.open(out_tgz, "w:gz", compresslevel=6) as tar:
        tar.add(package_root, arcname="package")


def install_hub_layout(out_dir: Path, pkg: dict, tgz_src: Path) -> Path:
    """
    Hub custom-template layout:
      <out>/<sanitized>/<sanitized>.tgz
      <out>/<sanitized>/package.json   (sibling, preferred by getTplPackage)
    """
    folder = out_dir / HUB_DIR_NAME
    folder.mkdir(parents=True, exist_ok=True)
    dest_tgz = folder / f"{HUB_DIR_NAME}.tgz"
    if dest_tgz.exists():
        dest_tgz.unlink()
    shutil.copy2(tgz_src, dest_tgz)
    (folder / "package.json").write_text(
        json.dumps(pkg, indent=2) + "\n", encoding="utf-8"
    )
    # Remove legacy flat archive if present (would also be scanned, but confusing).
    legacy = out_dir / f"{PACKAGE_NAME}-{pkg['version']}.tgz"
    if legacy.is_file():
        legacy.unlink()
        print(f"Removed legacy flat archive: {legacy}")
    return dest_tgz


def install_editor_project_templates(tgz_src: Path, pkg: dict) -> None:
    """
    Also drop a classic UPM template into the Editor ProjectTemplates folder so
    it shows next to URP / 2D on New Project (needs admin write).
    """
    version = pkg["version"]
    editor = pkg["unity"]
    # Strip patch letter for path guess: 6000.6.4f1 → try exact install folder
    pt = Path(
        f"/Applications/Unity/Hub/Editor/{editor}/Unity.app/Contents/"
        "Resources/PackageManager/ProjectTemplates"
    )
    if not pt.is_dir():
        # Fall back: any 6000.6.* install
        hub_editors = Path("/Applications/Unity/Hub/Editor")
        candidates = sorted(hub_editors.glob("6000.6.*/Unity.app/Contents/Resources/PackageManager/ProjectTemplates"))
        if not candidates:
            print("WARN: no Unity 6000.6 ProjectTemplates folder found; skip --also-editor-templates")
            return
        pt = candidates[-1]

    # Classic naming: com.unity.template.<id>-<ver>.tgz ; name field without version.
    classic_name = "com.unity.template.customsrp-oil"
    classic_pkg = dict(pkg)
    classic_pkg["name"] = classic_name
    # Built-in templates use series (6000.6); keep full version for safety either way.
    dest = pt / f"{classic_name}-{version}.tgz"

    # Rebuild a temp tgz with classic package name inside.
    stage = Path(tempfile.mkdtemp(prefix="oil-tpl-editor-"))
    try:
        # Re-extract source tgz, rewrite package.json name, retar
        extract = stage / "extract"
        extract.mkdir()
        with tarfile.open(tgz_src, "r:gz") as tar:
            tar.extractall(extract)
        pkg_json = extract / "package" / "package.json"
        pkg_json.write_text(json.dumps(classic_pkg, indent=2) + "\n", encoding="utf-8")
        tmp_tgz = stage / dest.name
        with tarfile.open(tmp_tgz, "w:gz", compresslevel=6) as tar:
            tar.add(extract / "package", arcname="package")

        try:
            shutil.copy2(tmp_tgz, dest)
            print(f"Installed Editor ProjectTemplates: {dest}")
        except PermissionError:
            fallback = out_editor_copy_path(tmp_tgz.name)
            shutil.copy2(tmp_tgz, fallback)
            print(f"No write access to {pt}")
            print(f"Copied to {fallback}")
            print(f"Run manually:\n  sudo cp {fallback!s} {dest!s}")
    finally:
        shutil.rmtree(stage, ignore_errors=True)


def out_editor_copy_path(filename: str) -> Path:
    d = ROOT / "Tools" / "OilCustomSrpTemplate" / "dist"
    d.mkdir(parents=True, exist_ok=True)
    return d / filename


def build(
    out_dir: Path,
    keep_stage: bool,
    unity_version: str | None,
    also_editor_templates: bool,
) -> Path:
    pkg = load_package_json()
    version = pkg["version"]
    unity = unity_version or read_project_unity_version()
    pkg["name"] = PACKAGE_NAME
    pkg["displayName"] = DISPLAY_NAME
    # Hub: template.unity === editorVersion (exact). Do NOT use "6000.6".
    pkg["unity"] = unity
    pkg["type"] = "template"
    pkg["host"] = "hub"
    pkg.setdefault("renderPipeline", "SRP")

    stage = Path(tempfile.mkdtemp(prefix="oil-customsrp-template-"))
    package_root = stage / "package"
    project_data = package_root / "ProjectData~"
    assets = project_data / "Assets"
    assets.mkdir(parents=True)

    try:
        copy_customsrp(assets)
        copy_stubs(assets)
        write_readme_asset(assets)
        copy_project_settings(project_data)
        write_packages(project_data, pkg)
        write_package_docs(package_root, pkg)

        staged_tgz = stage / f"{HUB_DIR_NAME}.tgz"
        make_tgz(package_root, staged_tgz)
        out_tgz = install_hub_layout(out_dir, pkg, staged_tgz)

        if also_editor_templates:
            install_editor_project_templates(staged_tgz, pkg)

        size_mb = out_tgz.stat().st_size / (1024 * 1024)
        print(f"Wrote {out_tgz} ({size_mb:.1f} MB)")
        print(f"package.json unity = {unity!r} (must match Hub editor dropdown exactly)")
        print(f"Hub templates path: {out_dir}")
        print("Fully quit & reopen Unity Hub, then New Project → filter/search "
              f"{DISPLAY_NAME!r} (or Templates page → Custom).")
        return out_tgz
    finally:
        if keep_stage:
            print(f"Staging left at: {stage}")
        else:
            shutil.rmtree(stage, ignore_errors=True)


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument(
        "--out",
        type=Path,
        default=DEFAULT_OUT,
        help=f"Hub templates directory (default: {DEFAULT_OUT})",
    )
    ap.add_argument(
        "--unity",
        default=None,
        help="Exact editor version for package.json unity field (default: ProjectVersion.txt)",
    )
    ap.add_argument(
        "--also-editor-templates",
        action="store_true",
        help="Also copy into Unity Editor ProjectTemplates (shows beside URP; may need sudo)",
    )
    ap.add_argument(
        "--keep-stage",
        action="store_true",
        help="Keep the temporary package/ staging folder",
    )
    args = ap.parse_args()
    build(
        args.out.expanduser(),
        args.keep_stage,
        args.unity,
        args.also_editor_templates,
    )


if __name__ == "__main__":
    main()
