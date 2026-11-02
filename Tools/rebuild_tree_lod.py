#!/usr/bin/env python3
"""Rebuild a tree low-LOD mesh from a high-LOD OBJ via Decimate Collapse.

Expects an OBJ with two objects/groups named Bark and Leaf (case-insensitive
prefix). Writes a 2-object OBJ (Bark then Leaf) plus a JSON report.

Usage (Blender CLI):
  blender --background --python Tools/rebuild_tree_lod.py -- \\
    --high path/high.obj --out path/low.obj \\
    [--bark-ratio 0.35] [--leaf-ratio 0.5] \\
    [--target-bark-tris N] [--target-leaf-tris N] \\
    [--report path/report.json]
"""

from __future__ import annotations

import argparse
import json
import math
import sys
from pathlib import Path


def _parse_args(argv: list[str]) -> argparse.Namespace:
    if "--" in argv:
        argv = argv[argv.index("--") + 1 :]
    p = argparse.ArgumentParser(description="Decimate tree LOD OBJ (Bark/Leaf)")
    p.add_argument("--high", required=True, help="Input high-LOD OBJ")
    p.add_argument("--out", required=True, help="Output low-LOD OBJ")
    p.add_argument("--bark-ratio", type=float, default=None)
    p.add_argument("--leaf-ratio", type=float, default=None)
    p.add_argument("--target-bark-tris", type=int, default=None)
    p.add_argument("--target-leaf-tris", type=int, default=None)
    p.add_argument("--report", default=None, help="Optional JSON report path")
    p.add_argument("--default-bark-ratio", type=float, default=0.35)
    p.add_argument("--default-leaf-ratio", type=float, default=0.5)
    return p.parse_args(argv)


def _import_bpy():
    import bpy  # noqa: WPS433 — only available inside Blender

    return bpy


def _clear_scene(bpy) -> None:
    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.object.delete(use_global=False)
    for block in list(bpy.data.meshes):
        bpy.data.meshes.remove(block)
    for block in list(bpy.data.materials):
        bpy.data.materials.remove(block)


def _import_obj(bpy, path: Path):
    # Match Unity Y-up / -Z forward so round-trip OBJ keeps tree height on Y.
    if hasattr(bpy.ops.wm, "obj_import"):
        bpy.ops.wm.obj_import(
            filepath=str(path),
            forward_axis="NEGATIVE_Z",
            up_axis="Y",
            use_split_objects=True,
            use_split_groups=True,
        )
    else:
        bpy.ops.import_scene.obj(
            filepath=str(path),
            axis_forward="-Z",
            axis_up="Y",
            use_split_objects=True,
            use_split_groups=True,
        )


def _export_obj(bpy, path: Path, objects) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    bpy.ops.object.select_all(action="DESELECT")
    for obj in objects:
        obj.select_set(True)
        bpy.context.view_layer.objects.active = obj
    if hasattr(bpy.ops.wm, "obj_export"):
        bpy.ops.wm.obj_export(
            filepath=str(path),
            export_selected_objects=True,
            export_materials=False,
            export_uv=True,
            export_normals=True,
            forward_axis="NEGATIVE_Z",
            up_axis="Y",
        )
    else:
        bpy.ops.export_scene.obj(
            filepath=str(path),
            use_selection=True,
            use_materials=False,
            use_normals=True,
            use_uvs=True,
            axis_forward="-Z",
            axis_up="Y",
        )


def _tri_count(obj) -> int:
    mesh = obj.data
    mesh.calc_loop_triangles()
    return len(mesh.loop_triangles)


def _aabb(obj):
    corners = [obj.matrix_world @ v.co for v in obj.data.vertices]
    if not corners:
        return {
            "min": [0, 0, 0],
            "max": [0, 0, 0],
            "size": [0, 0, 0],
            "center": [0, 0, 0],
        }
    xs = [c.x for c in corners]
    ys = [c.y for c in corners]
    zs = [c.z for c in corners]
    mn = [min(xs), min(ys), min(zs)]
    mx = [max(xs), max(ys), max(zs)]
    size = [mx[i] - mn[i] for i in range(3)]
    center = [(mn[i] + mx[i]) * 0.5 for i in range(3)]
    return {"min": mn, "max": mx, "size": size, "center": center}


def _combined_aabb(objects):
    mins = [math.inf, math.inf, math.inf]
    maxs = [-math.inf, -math.inf, -math.inf]
    for obj in objects:
        box = _aabb(obj)
        for i in range(3):
            mins[i] = min(mins[i], box["min"][i])
            maxs[i] = max(maxs[i], box["max"][i])
    size = [maxs[i] - mins[i] for i in range(3)]
    center = [(mins[i] + maxs[i]) * 0.5 for i in range(3)]
    return {"min": mins, "max": maxs, "size": size, "center": center}


def _classify(objects):
    bark = None
    leaf = None
    others = []
    for obj in objects:
        name = (obj.name or "").lower()
        if name.startswith("bark") or ".bark" in name or "_bark" in name:
            bark = obj
        elif name.startswith("leaf") or ".leaf" in name or "_leaf" in name:
            leaf = obj
        else:
            others.append(obj)
    if bark is None or leaf is None:
        # Fall back to largest-tris = leaf, second = bark when only 2 meshes
        ranked = sorted(objects, key=_tri_count, reverse=True)
        if len(ranked) >= 2 and (bark is None or leaf is None):
            if leaf is None:
                leaf = ranked[0]
            if bark is None:
                for cand in ranked:
                    if cand is not leaf:
                        bark = cand
                        break
        elif len(ranked) == 1:
            bark = ranked[0]
            leaf = ranked[0]
    return bark, leaf, others


def _ensure_single_user(bpy, obj):
    if obj.data.users > 1:
        obj.data = obj.data.copy()


def _decimate(bpy, obj, ratio: float) -> dict:
    _ensure_single_user(bpy, obj)
    before = _tri_count(obj)
    ratio = max(0.02, min(1.0, float(ratio)))
    if ratio >= 0.999 or before < 8:
        return {"before": before, "after": before, "ratio": ratio, "skipped": True}

    bpy.ops.object.select_all(action="DESELECT")
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj
    bpy.ops.object.mode_set(mode="EDIT")
    bpy.ops.mesh.select_all(action="SELECT")
    # Remove degenerate faces before collapse
    bpy.ops.mesh.dissolve_degenerate(threshold=1e-6)
    bpy.ops.object.mode_set(mode="OBJECT")

    mod = obj.modifiers.new(name="LOD_Decimate", type="DECIMATE")
    mod.decimate_type = "COLLAPSE"
    mod.ratio = ratio
    mod.use_collapse_triangulate = True
    # Keep UVs / borders as much as Blender allows
    if hasattr(mod, "use_symmetry"):
        mod.use_symmetry = False
    bpy.ops.object.modifier_apply(modifier=mod.name)

    bpy.ops.object.mode_set(mode="EDIT")
    bpy.ops.mesh.select_all(action="SELECT")
    bpy.ops.mesh.normals_make_consistent(inside=False)
    bpy.ops.mesh.delete_loose()
    bpy.ops.mesh.dissolve_degenerate(threshold=1e-6)
    bpy.ops.object.mode_set(mode="OBJECT")

    after = _tri_count(obj)
    return {"before": before, "after": after, "ratio": ratio, "skipped": False}


def _nonmanifold_edge_count(bpy, obj) -> int:
    bpy.ops.object.select_all(action="DESELECT")
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj
    bpy.ops.object.mode_set(mode="EDIT")
    bpy.ops.mesh.select_all(action="DESELECT")
    bpy.ops.mesh.select_non_manifold()
    import bmesh

    bm = bmesh.from_edit_mesh(obj.data)
    count = sum(1 for e in bm.edges if e.select)
    bpy.ops.object.mode_set(mode="OBJECT")
    return count


def _ratio_for(target_tris, source_tris, explicit, default):
    if explicit is not None:
        return float(explicit)
    if target_tris is not None and source_tris > 0:
        return max(0.02, min(1.0, float(target_tris) / float(source_tris)))
    return float(default)


def main(argv: list[str] | None = None) -> int:
    args = _parse_args(argv if argv is not None else sys.argv)
    high = Path(args.high).resolve()
    out = Path(args.out).resolve()
    if not high.is_file():
        print(f"[rebuild_tree_lod] missing high OBJ: {high}", file=sys.stderr)
        return 2

    bpy = _import_bpy()
    _clear_scene(bpy)
    _import_obj(bpy, high)

    mesh_objects = [o for o in bpy.context.scene.objects if o.type == "MESH"]
    if not mesh_objects:
        print("[rebuild_tree_lod] no mesh objects imported", file=sys.stderr)
        return 3

    bark, leaf, others = _classify(mesh_objects)
    if bark is None or leaf is None:
        print(
            f"[rebuild_tree_lod] failed to classify Bark/Leaf from {[o.name for o in mesh_objects]}",
            file=sys.stderr,
        )
        return 4

    # Rename for stable export order/names
    bark.name = "Bark"
    leaf.name = "Leaf"
    bark.data.name = "Bark"
    leaf.data.name = "Leaf"

    high_aabb = _combined_aabb([bark, leaf] if bark != leaf else [bark])
    bark_tris = _tri_count(bark)
    leaf_tris = _tri_count(leaf)

    bark_ratio = _ratio_for(
        args.target_bark_tris, bark_tris, args.bark_ratio, args.default_bark_ratio
    )
    leaf_ratio = _ratio_for(
        args.target_leaf_tris, leaf_tris, args.leaf_ratio, args.default_leaf_ratio
    )

    bark_stats = _decimate(bpy, bark, bark_ratio)
    leaf_stats = (
        {"before": leaf_tris, "after": leaf_tris, "ratio": 1.0, "skipped": True}
        if leaf == bark
        else _decimate(bpy, leaf, leaf_ratio)
    )

    low_aabb = _combined_aabb([bark, leaf] if bark != leaf else [bark])
    size_high = high_aabb["size"]
    size_low = low_aabb["size"]
    aabb_delta = []
    for i in range(3):
        base = size_high[i] if abs(size_high[i]) > 1e-6 else 1.0
        aabb_delta.append((size_low[i] - size_high[i]) / base)

    nm_bark = _nonmanifold_edge_count(bpy, bark)
    nm_leaf = 0 if leaf == bark else _nonmanifold_edge_count(bpy, leaf)

    export_list = [bark] if bark == leaf else [bark, leaf]
    _export_obj(bpy, out, export_list)

    report = {
        "high": str(high),
        "out": str(out),
        "bark": {
            "name": bark.name,
            **bark_stats,
            "nonmanifold_edges": nm_bark,
        },
        "leaf": {
            "name": leaf.name,
            **leaf_stats,
            "nonmanifold_edges": nm_leaf,
        },
        "others_ignored": [o.name for o in others],
        "aabb_high": high_aabb,
        "aabb_low": low_aabb,
        "aabb_size_delta_rel": aabb_delta,
        # Leaf cards are intentionally open (high non-manifold). Gate on silhouette only.
        "ok": abs(aabb_delta[0]) < 0.25
        and abs(aabb_delta[1]) < 0.25
        and abs(aabb_delta[2]) < 0.25
        and bark_stats["after"] > 0
        and leaf_stats["after"] > 0,
    }

    text = json.dumps(report, indent=2)
    print(text)
    if args.report:
        report_path = Path(args.report)
        report_path.parent.mkdir(parents=True, exist_ok=True)
        report_path.write_text(text, encoding="utf-8")

    return 0 if report["ok"] else 5


if __name__ == "__main__":
    sys.exit(main())
