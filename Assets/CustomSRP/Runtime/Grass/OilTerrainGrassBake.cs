using System.IO;
using UnityEngine;

namespace CustomSRP
{
    /// <summary>岛级种草点位 sidecar（SGB1）。编辑器烘一次，运行时只做视锥/距离更新。</summary>
    public static class OilTerrainGrassBake
    {
        public const int Magic = 0x31424753; // SGB1
        public const int Version = 3;
        public const float DefaultCellSize = 16f;
        public const string EditorPath = "Assets/CustomSRP/Grass/Island_GrassBake.bytes";
        /// <summary>GameFramework / Yoo 运行时路径（与 AllMapItem 同目录）。</summary>
        public const string GameAssetPath = "Assets/AssetRaw/Configs/Island_GrassBake.bytes";

        public struct Cell
        {
            public Vector3 Center;
            public Vector3 Extent;
            public int Count;
            public int Offset;
        }

        public sealed class Data
        {
            public float OriginX;
            public float OriginZ;
            public float CellSize = DefaultCellSize;
            public int CellCountX;
            public int CellCountZ;
            public Cell[] Cells;
            public Vector3[] Positions;
            public byte[] Variants;
            public byte[] Dryness;
        }

        public static byte[] Write(Data data)
        {
            if (data?.Cells == null || data.Positions == null || data.Variants == null)
                return null;
            int cellN = data.CellCountX * data.CellCountZ;
            if (data.CellCountX <= 0 || data.CellCountZ <= 0 || data.Cells.Length != cellN)
                return null;
            if (data.Variants.Length != data.Positions.Length)
                return null;
            if (data.Dryness == null || data.Dryness.Length != data.Positions.Length)
                return null;

            using (var stream = new MemoryStream(64 + cellN * 32 + data.Positions.Length * 14))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(Magic);
                writer.Write(Version);
                writer.Write(data.OriginX);
                writer.Write(data.OriginZ);
                writer.Write(data.CellSize);
                writer.Write(data.CellCountX);
                writer.Write(data.CellCountZ);
                writer.Write(data.Positions.Length);
                for (int i = 0; i < cellN; i++)
                {
                    var c = data.Cells[i];
                    writer.Write(c.Center.x);
                    writer.Write(c.Center.y);
                    writer.Write(c.Center.z);
                    writer.Write(c.Extent.x);
                    writer.Write(c.Extent.y);
                    writer.Write(c.Extent.z);
                    writer.Write(c.Count);
                    writer.Write(c.Offset);
                }

                for (int i = 0; i < data.Positions.Length; i++)
                {
                    Vector3 p = data.Positions[i];
                    writer.Write(p.x);
                    writer.Write(p.y);
                    writer.Write(p.z);
                    writer.Write(data.Variants[i]);
                    writer.Write(data.Dryness[i]);
                }

                return stream.ToArray();
            }
        }

        public static bool TryRead(byte[] bytes, out Data data)
        {
            data = null;
            if (bytes == null || bytes.Length < 32)
                return false;

            using (var stream = new MemoryStream(bytes, writable: false))
            using (var reader = new BinaryReader(stream))
            {
                if (reader.ReadInt32() != Magic)
                    return false;
                int version = reader.ReadInt32();
                if (version != 1 && version != 2 && version != Version)
                    return false;

                var parsed = new Data
                {
                    OriginX = reader.ReadSingle(),
                    OriginZ = reader.ReadSingle(),
                    CellSize = reader.ReadSingle(),
                    CellCountX = reader.ReadInt32(),
                    CellCountZ = reader.ReadInt32(),
                };
                if (parsed.CellCountX <= 0 || parsed.CellCountZ <= 0 || parsed.CellSize < 0.5f)
                    return false;

                int posCount = reader.ReadInt32();
                if (posCount < 0 || posCount > 2_000_000)
                    return false;

                int cellN = parsed.CellCountX * parsed.CellCountZ;
                if (cellN <= 0 || cellN > 65_536)
                    return false;

                parsed.Cells = new Cell[cellN];
                for (int i = 0; i < cellN; i++)
                {
                    parsed.Cells[i] = new Cell
                    {
                        Center = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
                        Extent = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
                        Count = reader.ReadInt32(),
                        Offset = reader.ReadInt32(),
                    };
                    var c = parsed.Cells[i];
                    if (c.Count < 0 || c.Offset < 0 || c.Offset + c.Count > posCount)
                        return false;
                }

                parsed.Positions = new Vector3[posCount];
                parsed.Variants = new byte[posCount];
                parsed.Dryness = new byte[posCount];
                for (int i = 0; i < posCount; i++)
                {
                    parsed.Positions[i] = new Vector3(
                        reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                    if (version >= 2)
                    {
                        parsed.Variants[i] = reader.ReadByte();
                        if (parsed.Variants[i] >= OilTerrainGrassBladeMesh.VariantCount)
                            parsed.Variants[i] = (byte)OilTerrainGrassBladeMesh.Variant.Patch;
                    }
                    else
                    {
                        parsed.Variants[i] = (byte)OilTerrainGrassBladeMesh.PickVariant(parsed.Positions[i]);
                    }

                    parsed.Dryness[i] = version >= 3 ? reader.ReadByte() : (byte)0;
                }

                data = parsed;
                return true;
            }
        }
    }
}
