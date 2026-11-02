#!/usr/bin/env python3
"""Offline batch: Unity Mesh/FBX high → Blender decimate → preview Mesh.asset for all 19 jobs."""

from __future__ import annotations

import json
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
BLENDER = Path("/Applications/Blender.app/Contents/MacOS/Blender")
SCRATCH = ROOT / "Assets/_scratch/tree_lod_rebuild"
TFF = ROOT / "Assets/GameMain/Environment/Models/TffOilUv"
LARCH = ROOT / "Assets/GameMain/Environment/Models/LarchUv"
MODELS = ROOT / "Assets/GameMain/Environment/Models"
SCRIPT = ROOT / "Tools/rebuild_tree_lod.py"
TO_OBJ = ROOT / "Tools/unity_mesh_to_obj.py"
TO_ASSET = ROOT / "Tools/obj_to_unity_mesh_asset.py"


def run(cmd: list[str]) -> subprocess.CompletedProcess:
    print("+", " ".join(str(c) for c in cmd), flush=True)
    return subprocess.run(cmd, cwd=str(ROOT), text=True, capture_output=True)


def tris_from_asset(asset: Path) -> list[int]:
    text = asset.read_text(encoding="utf-8", errors="ignore")
    import re

    m = re.search(r"m_SubMeshes:\s*\n(.*?)(?:\n  m_Shapes:|\n  m_BindPose:)", text, re.S)
    if not m:
        return []
    return [int(x) // 3 for x in re.findall(r"indexCount:\s*(\d+)", m.group(1))]


def rebuild_tff(job_id: str, high: Path, low: Path, preview_name: str) -> dict:
    work = SCRATCH / job_id
    high_obj = work / "high" / f"{job_id}_high.obj"
    low_obj = work / "low" / f"{job_id}_low.obj"
    report = work / "report.json"
    preview = work / "preview" / f"{preview_name}.asset"
    high_obj.parent.mkdir(parents=True, exist_ok=True)
    low_obj.parent.mkdir(parents=True, exist_ok=True)
    preview.parent.mkdir(parents=True, exist_ok=True)

    r1 = run([sys.executable, str(TO_OBJ), str(high), str(high_obj)])
    if r1.returncode != 0:
        return {"id": job_id, "ok": False, "stage": "export", "err": r1.stderr}

    targets = tris_from_asset(low) if low.exists() else []
    bark_t = targets[0] if len(targets) > 0 else None
    leaf_t = targets[1] if len(targets) > 1 else None
    cmd = [
        str(BLENDER),
        "--factory-startup",
        "--background",
        "--python",
        str(SCRIPT),
        "--",
        "--high",
        str(high_obj),
        "--out",
        str(low_obj),
        "--report",
        str(report),
    ]
    if bark_t:
        cmd += ["--target-bark-tris", str(bark_t)]
    if leaf_t:
        cmd += ["--target-leaf-tris", str(leaf_t)]
    r2 = run(cmd)
    if r2.returncode not in (0, 5) or not low_obj.exists():
        return {
            "id": job_id,
            "ok": False,
            "stage": "blender",
            "code": r2.returncode,
            "err": (r2.stdout or "")[-2000:] + "\n" + (r2.stderr or "")[-2000:],
        }

    r3 = run(
        [
            sys.executable,
            str(TO_ASSET),
            str(low_obj),
            str(preview),
            "--name",
            preview_name,
        ]
    )
    if r3.returncode != 0:
        return {"id": job_id, "ok": False, "stage": "asset", "err": r3.stderr}

    rep = json.loads(report.read_text()) if report.exists() else {}
    return {
        "id": job_id,
        "ok": bool(rep.get("ok", True)),
        "preview": str(preview.relative_to(ROOT)),
        "report": rep,
        "stdout": r3.stdout.strip(),
    }


def rebuild_oak(job_id: str, fbx: Path, high_name: str, low_name: str, preview_name: str) -> dict:
    """Import FBX in Blender, decimate high LOD mesh, export OBJ → preview asset."""
    work = SCRATCH / job_id
    high_obj = work / "high" / f"{job_id}_high.obj"
    low_obj = work / "low" / f"{job_id}_low.obj"
    report = work / "report.json"
    preview = work / "preview" / f"{preview_name}.asset"
    extract_py = work / "extract_high.py"
    high_obj.parent.mkdir(parents=True, exist_ok=True)
    low_obj.parent.mkdir(parents=True, exist_ok=True)
    preview.parent.mkdir(parents=True, exist_ok=True)

    extract_py.write_text(
        f"""
import bpy
from pathlib import Path
fbx = Path(r"{fbx}")
high_name = "{high_name}"
low_name = "{low_name}"
out_high = Path(r"{high_obj}")
out_low_ref = Path(r"{work / 'high' / (job_id + '_oldlow.obj')}")

bpy.ops.object.select_all(action='SELECT')
bpy.ops.object.delete(use_global=False)
bpy.ops.import_scene.fbx(filepath=str(fbx), automatic_bone_orientation=False)

def find(substr):
    for o in bpy.context.scene.objects:
        if o.type == 'MESH' and substr.lower() in o.name.lower():
            return o
    return None

high = find(high_name)
low = find(low_name)
if high is None:
    raise SystemExit('high mesh not found: ' + high_name)

# Split by material slots into Bark/Leaf objects for export
def export_split(obj, path: Path):
    bpy.ops.object.select_all(action='DESELECT')
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj
    # Duplicate
    bpy.ops.object.duplicate()
    dup = bpy.context.view_layer.objects.active
    # Separate by material
    bpy.ops.object.mode_set(mode='EDIT')
    bpy.ops.mesh.separate(type='MATERIAL')
    bpy.ops.object.mode_set(mode='OBJECT')
    parts = [o for o in bpy.context.selected_objects if o.type == 'MESH']
    # Rename by slot order approx: first selected order unstable — sort by tri count / name
    parts = sorted(parts, key=lambda o: o.name)
    if len(parts) >= 1:
        parts[0].name = 'Bark'
        parts[0].data.name = 'Bark'
    if len(parts) >= 2:
        # Larger canopy usually leaf
        parts = sorted(parts, key=lambda o: len(o.data.polygons))
        parts[0].name = 'Bark'
        parts[0].data.name = 'Bark'
        parts[-1].name = 'Leaf'
        parts[-1].data.name = 'Leaf'
        parts = [parts[0], parts[-1]] if parts[0] != parts[-1] else [parts[0]]
    bpy.ops.object.select_all(action='DESELECT')
    for p in parts:
        p.select_set(True)
    path.parent.mkdir(parents=True, exist_ok=True)
    bpy.ops.wm.obj_export(
        filepath=str(path),
        export_selected_objects=True,
        export_materials=False,
        export_uv=True,
        export_normals=True,
        forward_axis='NEGATIVE_Z',
        up_axis='Y',
    )
    # cleanup dups
    bpy.ops.object.delete()

export_split(high, out_high)
targets = {{'bark': 0, 'leaf': 0}}
if low is not None:
    # count tris per material
    mesh = low.data
    if len(mesh.materials) >= 2:
        # approximate: use polygon material_index
        bark = sum(1 for p in mesh.polygons if p.material_index == 0)
        leaf = sum(1 for p in mesh.polygons if p.material_index == 1)
        targets = {{'bark': bark, 'leaf': leaf}}
    else:
        targets = {{'bark': len(mesh.polygons)//3, 'leaf': len(mesh.polygons) - len(mesh.polygons)//3}}
out_targets = Path(r"{work / 'targets.json'}")
import json
out_targets.write_text(json.dumps(targets))
print('EXTRACT_OK', out_high, targets)
""",
        encoding="utf-8",
    )

    r0 = run(
        [
            str(BLENDER),
            "--factory-startup",
            "--background",
            "--python",
            str(extract_py),
        ]
    )
    if r0.returncode != 0 or not high_obj.exists():
        return {
            "id": job_id,
            "ok": False,
            "stage": "extract",
            "err": (r0.stdout or "")[-2000:] + "\n" + (r0.stderr or "")[-2000:],
        }

    targets = {"bark": None, "leaf": None}
    tpath = work / "targets.json"
    if tpath.exists():
        targets = json.loads(tpath.read_text())

    cmd = [
        str(BLENDER),
        "--factory-startup",
        "--background",
        "--python",
        str(SCRIPT),
        "--",
        "--high",
        str(high_obj),
        "--out",
        str(low_obj),
        "--report",
        str(report),
    ]
    if targets.get("bark"):
        cmd += ["--target-bark-tris", str(targets["bark"])]
    if targets.get("leaf"):
        cmd += ["--target-leaf-tris", str(targets["leaf"])]
    r2 = run(cmd)
    if r2.returncode not in (0, 5) or not low_obj.exists():
        return {
            "id": job_id,
            "ok": False,
            "stage": "blender",
            "err": (r2.stdout or "")[-2000:] + "\n" + (r2.stderr or "")[-2000:],
        }

    r3 = run(
        [sys.executable, str(TO_ASSET), str(low_obj), str(preview), "--name", preview_name]
    )
    if r3.returncode != 0:
        return {"id": job_id, "ok": False, "stage": "asset", "err": r3.stderr}
    rep = json.loads(report.read_text()) if report.exists() else {}
    return {
        "id": job_id,
        "ok": bool(rep.get("ok", True)),
        "preview": str(preview.relative_to(ROOT)),
        "report": rep,
        "stdout": r3.stdout.strip(),
    }


def main() -> int:
    jobs = []
    for i in range(1, 9):
        jobs.append(
            (
                "tff",
                f"GrandFir{i}",
                TFF / f"GrandFir{i}_LOD0.asset",
                TFF / f"GrandFir{i}_LOD1.asset",
                f"GrandFir{i}_LOD1_preview",
            )
        )
    jobs.append(
        ("tff", "Spruce3", TFF / "Spruce3_LOD0.asset", TFF / "Spruce3_LOD1.asset", "Spruce3_LOD1_preview")
    )
    for n in (3, 4, 7):
        jobs.append(
            (
                "tff",
                f"Larch{n}",
                LARCH / f"Larch{n}_LOD0.asset",
                LARCH / f"Larch{n}_LOD1.asset",
                f"Larch{n}_LOD1_preview",
            )
        )

    results = []
    only = sys.argv[1:] if len(sys.argv) > 1 else None
    for kind, *rest in jobs:
        job_id = rest[0]
        if only and job_id not in only and "all" not in only:
            continue
        if kind == "tff":
            results.append(rebuild_tff(rest[0], rest[1], rest[2], rest[3]))

    # Oaks
    oak_jobs = [
        ("Oak01", "SM_OakTree_01.fbx", "LOD1", "LOD2", "SM_OakTree_01_LOD2_preview"),
        ("Oak02", "SM_OakTree_02.fbx", "LOD1", "LOD2", "SM_OakTree_02_LOD2_preview"),
        ("Oak03", "SM_OakTree_03.fbx", "LOD1", "LOD2", "SM_OakTree_03_LOD2_preview"),
        ("Oak04", "SM_OakTree_04.fbx", "LOD1", "LOD2", "SM_OakTree_04_LOD2_preview"),
        ("Oak05", "SM_OakTree_05.fbx", "LOD1", "LOD2", "SM_OakTree_05_LOD2_preview"),
        ("Oak06", "SM_OakTree_06.fbx", "LOD1", "LOD2", "SM_OakTree_06_LOD2_preview"),
        ("Oak07", "SM_OakTree_07.fbx", "LOD1", "LOD2", "SM_OakTree_07_LOD2_preview"),
    ]
    for job_id, fbx_name, high_sub, low_sub, preview_name in oak_jobs:
        if only and job_id not in only and "all" not in only:
            continue
        results.append(
            rebuild_oak(job_id, MODELS / fbx_name, high_sub, low_sub, preview_name)
        )

    summary = SCRATCH / "batch_summary.json"
    summary.parent.mkdir(parents=True, exist_ok=True)
    summary.write_text(json.dumps(results, indent=2), encoding="utf-8")
    ok = sum(1 for r in results if r.get("ok"))
    print(f"DONE {ok}/{len(results)} ok → {summary}")
    for r in results:
        print(r.get("id"), "OK" if r.get("ok") else "FAIL", r.get("stage"), r.get("preview"))
    return 0 if ok == len(results) else 1


if __name__ == "__main__":
    sys.exit(main())
