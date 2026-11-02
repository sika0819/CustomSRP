#!/usr/bin/env python3
"""Write a Unity YAML Mesh .asset (readable, 2 submeshes) from a Bark/Leaf OBJ."""

from __future__ import annotations

import argparse
import struct
import sys
from pathlib import Path


def _parse_obj(path: Path):
    pos, nrm, uv = [], [], []
    # per-submesh list of (p,t,n) indices (1-based as in OBJ, converted later)
    subs = []
    names = []
    current = -1

    def ensure(name: str):
        nonlocal current
        for i, n in enumerate(names):
            if n.lower() == name.lower():
                current = i
                return
        names.append(name)
        subs.append([])
        current = len(names) - 1

    for raw in path.read_text(encoding="utf-8", errors="ignore").splitlines():
        line = raw.strip()
        if not line or line.startswith("#"):
            continue
        sp = line.split()
        tag = sp[0]
        if tag == "v" and len(sp) >= 4:
            pos.append(tuple(map(float, sp[1:4])))
        elif tag == "vn" and len(sp) >= 4:
            nrm.append(tuple(map(float, sp[1:4])))
        elif tag == "vt" and len(sp) >= 3:
            uv.append(tuple(map(float, sp[1:3])))
        elif tag in ("o", "g") and len(sp) >= 2:
            ensure(sp[1])
        elif tag == "f":
            if current < 0:
                ensure("Bark")
            face = []
            for corner in sp[1:]:
                parts = corner.split("/")
                vi = int(parts[0])
                ti = int(parts[1]) if len(parts) > 1 and parts[1] else 0
                ni = int(parts[2]) if len(parts) > 2 and parts[2] else 0
                face.append((vi, ti, ni))
            for i in range(1, len(face) - 1):
                subs[current].extend([face[0], face[i], face[i + 1]])

    # Expand to unique verts per corner (safe for split normals/uv)
    verts, norms, uvs = [], [], []
    sub_indices = []
    for faces in subs:
        idxs = []
        for vi, ti, ni in faces:
            p = pos[vi - 1 if vi > 0 else len(pos) + vi]
            t = uv[ti - 1] if ti > 0 else (0.0, 0.0)
            n = nrm[ni - 1] if ni > 0 else (0.0, 1.0, 0.0)
            idxs.append(len(verts))
            verts.append(p)
            uvs.append(t)
            norms.append(n)
        sub_indices.append(idxs)

    # Prefer Bark, Leaf order
    order = list(range(len(names)))
    def key(i):
        n = names[i].lower()
        if "bark" in n:
            return 0
        if "leaf" in n:
            return 1
        return 2 + i

    order.sort(key=key)
    names = [names[i] for i in order]
    sub_indices = [sub_indices[i] for i in order]
    return verts, norms, uvs, sub_indices, names


def _hex(data: bytes) -> str:
    return data.hex()


def _aabb(verts):
    if not verts:
        return (0, 0, 0), (0, 0, 0)
    xs = [v[0] for v in verts]
    ys = [v[1] for v in verts]
    zs = [v[2] for v in verts]
    mn = (min(xs), min(ys), min(zs))
    mx = (max(xs), max(ys), max(zs))
    center = tuple((mn[i] + mx[i]) * 0.5 for i in range(3))
    extent = tuple((mx[i] - mn[i]) * 0.5 for i in range(3))
    return center, extent


def write_mesh_asset(obj_path: Path, asset_path: Path, mesh_name: str) -> dict:
    verts, norms, uvs, sub_indices, names = _parse_obj(obj_path)
    vcount = len(verts)

    # Interleaved: pos3 + nrm3 + tan4 + col4 + uv0_2 + uv1_2 = 72 bytes
    stride = 72
    vbuf = bytearray()
    for i in range(vcount):
        px, py, pz = verts[i]
        nx, ny, nz = norms[i]
        u, v = uvs[i]
        # crude tangent
        tx, ty, tz, tw = 1.0, 0.0, 0.0, 1.0
        vbuf += struct.pack("<3f", px, py, pz)
        vbuf += struct.pack("<3f", nx, ny, nz)
        vbuf += struct.pack("<4f", tx, ty, tz, tw)
        vbuf += struct.pack("<4f", 1.0, 1.0, 1.0, 1.0)
        vbuf += struct.pack("<2f", u, v)
        vbuf += struct.pack("<2f", 0.0, 0.0)

    # 16-bit indices if possible
    use32 = vcount > 65535
    ibuf = bytearray()
    sub_meta = []
    first_byte = 0
    for idxs in sub_indices:
        for idx in idxs:
            if use32:
                ibuf += struct.pack("<I", idx)
            else:
                ibuf += struct.pack("<H", idx)
        index_count = len(idxs)
        # per-submesh vertex range
        if idxs:
            first_v = min(idxs)
            last_v = max(idxs)
            vcnt = last_v - first_v + 1
        else:
            first_v, vcnt = 0, 0
        sm_verts = [verts[i] for i in idxs] if idxs else []
        center, extent = _aabb(sm_verts)
        sub_meta.append(
            {
                "firstByte": first_byte,
                "indexCount": index_count,
                "firstVertex": first_v,
                "vertexCount": vcnt,
                "center": center,
                "extent": extent,
            }
        )
        first_byte += index_count * (4 if use32 else 2)

    center, extent = _aabb(verts)
    asset_path.parent.mkdir(parents=True, exist_ok=True)
    lines = []
    lines.append("%YAML 1.1")
    lines.append("%TAG !u! tag:unity3d.com,2011:")
    lines.append("--- !u!43 &4300000")
    lines.append("Mesh:")
    lines.append("  m_ObjectHideFlags: 0")
    lines.append("  m_CorrespondingSourceObject: {fileID: 0}")
    lines.append("  m_PrefabInstance: {fileID: 0}")
    lines.append("  m_PrefabAsset: {fileID: 0}")
    lines.append(f"  m_Name: {mesh_name}")
    lines.append("  serializedVersion: 12")
    lines.append("  m_SubMeshes:")
    for sm in sub_meta:
        lines.append("  - serializedVersion: 2")
        lines.append(f"    firstByte: {sm['firstByte']}")
        lines.append(f"    indexCount: {sm['indexCount']}")
        lines.append("    topology: 0")
        lines.append("    baseVertex: 0")
        lines.append(f"    firstVertex: {sm['firstVertex']}")
        lines.append(f"    vertexCount: {sm['vertexCount']}")
        lines.append("    localAABB:")
        c, e = sm["center"], sm["extent"]
        lines.append(f"      m_Center: {{x: {c[0]}, y: {c[1]}, z: {c[2]}}}")
        lines.append(f"      m_Extent: {{x: {e[0]}, y: {e[1]}, z: {e[2]}}}")
    lines.append("  m_Shapes:")
    lines.append("    vertices: []")
    lines.append("    shapes: []")
    lines.append("    channels: []")
    lines.append("    fullWeights: []")
    lines.append("  m_BindPose: []")
    lines.append("  m_BoneNameHashes: ")
    lines.append("  m_RootBoneNameHash: 0")
    lines.append("  m_BonesAABB: []")
    lines.append("  m_VariableBoneCountWeights:")
    lines.append("    m_Data: ")
    lines.append("  m_MeshCompression: 0")
    lines.append("  m_IsReadable: 1")
    lines.append("  m_KeepVertices: 0")
    lines.append("  m_KeepIndices: 0")
    lines.append(f"  m_IndexFormat: {1 if use32 else 0}")
    lines.append(f"  m_IndexBuffer: {_hex(ibuf)}")
    lines.append("  m_VertexData:")
    lines.append("    serializedVersion: 3")
    lines.append(f"    m_VertexCount: {vcount}")
    # channels matching project trees
    channel_defs = [
        (0, 3),
        (12, 3),
        (24, 4),
        (40, 4),
        (56, 2),
        (64, 2),
    ]
    lines.append("    m_Channels:")
    for off, dim in channel_defs:
        lines.append("    - stream: 0")
        lines.append(f"      offset: {off}")
        lines.append("      format: 0")
        lines.append(f"      dimension: {dim}")
    for _ in range(8):
        lines.append("    - stream: 0")
        lines.append("      offset: 0")
        lines.append("      format: 0")
        lines.append("      dimension: 0")
    lines.append(f"    m_DataSize: {len(vbuf)}")
    lines.append(f"    _typelessdata: {_hex(bytes(vbuf))}")
    # Minimal stubs for remaining fields Unity expects
    lines.append("  m_CompressedMesh:")
    for field in (
        "m_Vertices",
        "m_UV",
        "m_Normals",
        "m_Tangents",
        "m_Weights",
        "m_NormalSigns",
        "m_TangentSigns",
        "m_FloatColors",
        "m_BoneIndices",
        "m_Triangles",
    ):
        lines.append(f"    {field}:")
        lines.append("      m_NumItems: 0")
        if field in ("m_Vertices", "m_UV", "m_Normals", "m_Tangents", "m_FloatColors"):
            lines.append("      m_Range: 0")
            lines.append("      m_Start: 0")
        lines.append("      m_Data: ")
        lines.append("      m_BitSize: 0")
    lines.append("    m_UVInfo: 0")
    lines.append("  m_LocalAABB:")
    lines.append(f"    m_Center: {{x: {center[0]}, y: {center[1]}, z: {center[2]}}}")
    lines.append(f"    m_Extent: {{x: {extent[0]}, y: {extent[1]}, z: {extent[2]}}}")
    lines.append("  m_MeshUsageFlags: 0")
    lines.append("  m_CookingFlags: 0")
    lines.append("  m_BakedConvexCollisionMesh: ")
    lines.append("  m_BakedTriangleCollisionMesh: ")
    lines.append("  m_MeshMetrics[0]: 1")
    lines.append("  m_MeshMetrics[1]: 1")
    lines.append("  m_MeshOptimizationFlags: 1")
    lines.append("  m_StreamData:")
    lines.append("    serializedVersion: 2")
    lines.append("    offset: 0")
    lines.append("    size: 0")
    lines.append("    path: ")
    asset_path.write_text("\n".join(lines) + "\n", encoding="utf-8")

    # Ensure .meta with new guid if missing
    meta = asset_path.with_suffix(asset_path.suffix + ".meta")
    if not meta.exists():
        import uuid

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

    return {
        "verts": vcount,
        "subs": [len(s) // 3 for s in sub_indices],
        "names": names,
        "asset": str(asset_path),
    }


def main(argv=None) -> int:
    p = argparse.ArgumentParser()
    p.add_argument("obj")
    p.add_argument("asset")
    p.add_argument("--name", default=None)
    args = p.parse_args(argv)
    asset = Path(args.asset)
    name = args.name or asset.stem
    info = write_mesh_asset(Path(args.obj), asset, name)
    print(info)
    return 0


if __name__ == "__main__":
    sys.exit(main())
