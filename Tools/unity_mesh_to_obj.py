#!/usr/bin/env python3
"""Export a Unity YAML Mesh (.asset) to Wavefront OBJ with Bark/Leaf objects.

Supports typical float32 interleaved vertex buffers (format 0) used by this project.
"""

from __future__ import annotations

import argparse
import re
import struct
import sys
from pathlib import Path


def _hex_to_bytes(hex_blob: str) -> bytes:
    cleaned = re.sub(r"[^0-9a-fA-F]", "", hex_blob)
    if len(cleaned) % 2 == 1:
        cleaned = cleaned[:-1]
    return bytes.fromhex(cleaned)


def _parse_channels(block: str):
    channels = []
    for m in re.finditer(
        r"- stream:\s*(\d+)\s*\n\s*offset:\s*(\d+)\s*\n\s*format:\s*(\d+)\s*\n\s*dimension:\s*(\d+)",
        block,
    ):
        channels.append(
            {
                "stream": int(m.group(1)),
                "offset": int(m.group(2)),
                "format": int(m.group(3)),
                "dimension": int(m.group(4)),
            }
        )
    return channels


def _parse_submeshes(text: str):
    # First m_SubMeshes block only (before m_Shapes). Unity order: firstByte then indexCount.
    m = re.search(r"m_SubMeshes:\s*\n(.*?)(?:\n  m_Shapes:|\n  m_BindPose:)", text, re.S)
    if not m:
        return []
    block = m.group(1)
    subs = []
    for sm in re.finditer(
        r"firstByte:\s*(\d+)\s*\n\s*indexCount:\s*(\d+)\s*\n\s*topology:\s*\d+\s*\n"
        r"\s*baseVertex:\s*(\d+)\s*\n\s*firstVertex:\s*(\d+)\s*\n\s*vertexCount:\s*(\d+)",
        block,
    ):
        subs.append(
            {
                "firstByte": int(sm.group(1)),
                "indexCount": int(sm.group(2)),
                "baseVertex": int(sm.group(3)),
                "firstVertex": int(sm.group(4)),
                "vertexCount": int(sm.group(5)),
            }
        )
    return subs


def _find_hex_field(text: str, field: str) -> bytes:
    # m_IndexBuffer: followed by hex, or _typelessdata:
    patterns = [
        rf"{field}:\s*\n((?:[0-9a-fA-F \n\r\t])+)",
        rf"{field}:\s*((?:[0-9a-fA-F]+))",
    ]
    for pat in patterns:
        m = re.search(pat, text)
        if m:
            return _hex_to_bytes(m.group(1))
    raise RuntimeError(f"field not found: {field}")


def export_obj(asset_path: Path, out_path: Path) -> dict:
    text = asset_path.read_text(encoding="utf-8", errors="ignore")
    vc_m = re.search(r"m_VertexCount:\s*(\d+)", text)
    if not vc_m:
        raise RuntimeError("m_VertexCount missing")
    vcount = int(vc_m.group(1))

    ch_m = re.search(r"m_Channels:\s*\n((?:    - stream:.*\n(?:      .*\n)*)+)", text)
    if not ch_m:
        raise RuntimeError("m_Channels missing")
    channels = _parse_channels(ch_m.group(1))
    # Keep only channels with dimension > 0
    active = [c for c in channels if c["dimension"] > 0]
    if not active:
        raise RuntimeError("no active channels")

    # Stride = max offset+size among stream 0 float channels
    def fmt_size(fmt: int, dim: int) -> int:
        # Unity VertexFormat: 0=Float32
        if fmt == 0:
            return 4 * dim
        if fmt == 1:  # Float16
            return 2 * dim
        if fmt == 2:  # UNorm8
            return 1 * dim
        return 4 * dim

    stride = 0
    for c in active:
        if c["stream"] != 0:
            continue
        end = c["offset"] + fmt_size(c["format"], c["dimension"])
        stride = max(stride, end)
    if stride <= 0:
        raise RuntimeError("bad stride")

    # Vertex data: _typelessdata under m_VertexData
    vd_m = re.search(
        r"m_VertexData:.*?_typelessdata:\s*((?:[0-9a-fA-F \n\r\t]+))",
        text,
        re.S,
    )
    if not vd_m:
        # Sometimes data is inline after m_DataSize
        vd_m = re.search(r"_typelessdata:\s*((?:[0-9a-fA-F \n\r\t]+))", text)
    if not vd_m:
        raise RuntimeError("vertex _typelessdata missing")
    vbytes = _hex_to_bytes(vd_m.group(1))
    if len(vbytes) < stride * vcount:
        raise RuntimeError(f"vertex buffer short: {len(vbytes)} < {stride * vcount}")

    # Channel layout assumptions: pos0, nrm1, tan2, col3, uv0 4, uv1 5
    def read_f32(buf: bytes, off: int, dim: int):
        return struct.unpack_from("<" + "f" * dim, buf, off)

    pos_ch = active[0]
    nrm_ch = next((c for c in active if c["offset"] == 12 and c["dimension"] == 3), None)
    uv_ch = next((c for c in active if c["dimension"] == 2), None)

    positions = []
    normals = []
    uvs = []
    for i in range(vcount):
        base = i * stride
        positions.append(read_f32(vbytes, base + pos_ch["offset"], 3))
        if nrm_ch:
            normals.append(read_f32(vbytes, base + nrm_ch["offset"], 3))
        if uv_ch:
            uvs.append(read_f32(vbytes, base + uv_ch["offset"], 2))

    idx_bytes = _find_hex_field(text, "m_IndexBuffer")
    # UInt16 indices typical
    index_format16 = True
    if len(idx_bytes) % 2 != 0:
        raise RuntimeError("odd index buffer")
    indices = list(struct.unpack("<" + "H" * (len(idx_bytes) // 2), idx_bytes))

    subs = _parse_submeshes(text)
    if not subs:
        subs = [{"indexCount": len(indices), "firstByte": 0, "baseVertex": 0}]

    out_path.parent.mkdir(parents=True, exist_ok=True)
    lines = ["# unity_mesh_to_obj", f"# source {asset_path.name} verts={vcount}"]
    for p in positions:
        lines.append(f"v {p[0]:.9g} {p[1]:.9g} {p[2]:.9g}")
    for n in normals:
        lines.append(f"vn {n[0]:.9g} {n[1]:.9g} {n[2]:.9g}")
    for t in uvs:
        lines.append(f"vt {t[0]:.9g} {t[1]:.9g}")

    has_n = len(normals) == vcount
    has_t = len(uvs) == vcount
    tri_counts = []
    for si, sub in enumerate(subs):
        name = "Bark" if si == 0 else ("Leaf" if si == 1 else f"Part{si}")
        lines.append(f"o {name}")
        lines.append(f"g {name}")
        first_index = sub["firstByte"] // (2 if index_format16 else 4)
        count = sub["indexCount"]
        base_v = sub.get("baseVertex", 0)
        tris = 0
        for t in range(0, count, 3):
            i0 = indices[first_index + t] + base_v + 1
            i1 = indices[first_index + t + 1] + base_v + 1
            i2 = indices[first_index + t + 2] + base_v + 1
            if has_t and has_n:
                lines.append(f"f {i0}/{i0}/{i0} {i1}/{i1}/{i1} {i2}/{i2}/{i2}")
            elif has_t:
                lines.append(f"f {i0}/{i0} {i1}/{i1} {i2}/{i2}")
            elif has_n:
                lines.append(f"f {i0}//{i0} {i1}//{i1} {i2}//{i2}")
            else:
                lines.append(f"f {i0} {i1} {i2}")
            tris += 1
        tri_counts.append(tris)

    out_path.write_text("\n".join(lines) + "\n", encoding="utf-8")
    return {"verts": vcount, "sub_tris": tri_counts, "out": str(out_path)}


def main(argv=None) -> int:
    p = argparse.ArgumentParser()
    p.add_argument("asset")
    p.add_argument("out")
    args = p.parse_args(argv)
    info = export_obj(Path(args.asset), Path(args.out))
    print(info)
    return 0


if __name__ == "__main__":
    sys.exit(main())
