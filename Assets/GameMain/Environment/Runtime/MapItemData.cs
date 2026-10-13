using System.Collections.Generic;
using System.IO;
using UnityEngine;

using UnityGameFramework.Runtime;
namespace Game
{
    /// <summary>
    /// DaoYu1 摆放条目。二进制格式与 Boluo AllMapItem.bytes 一致（魔数 BLM1）。
    /// </summary>
    public sealed class MapItemData
    {
        const int BinaryMagic = 0x314D4C42;

        public int Index;
        public string Parent;
        public string ObjName;
        public Vector3 Pos;
        public Vector3 Rotation;
        public Vector3 Scale;
        public bool IsActive;

        public string PrefabName
        {
            get
            {
                if (string.IsNullOrEmpty(ObjName))
                    return ObjName;
                int paren = ObjName.IndexOf('(');
                if (paren >= 0)
                    return ObjName.Substring(0, paren).TrimEnd();
                return ObjName;
            }
        }

        public string PrefabPrefix => PrefabName;

        public static bool ReadBinaryTo(byte[] bytes, List<MapItemData> output)
        {
            if (bytes == null || bytes.Length < 12 || output == null)
            {
                Log.Error("[MapItemData] ReadBinaryTo: empty or null");
                return false;
            }

            using (var stream = new MemoryStream(bytes, false))
            using (var reader = new BinaryReader(stream, System.Text.Encoding.UTF8))
            {
                if (reader.ReadInt32() != BinaryMagic)
                {
                    Log.Error("[MapItemData] magic mismatch — re-export AllMapItem.bytes");
                    return false;
                }

                int itemCount = reader.ReadInt32();
                int stringCount = reader.ReadInt32();
                var stringTable = new string[stringCount];
                for (int i = 0; i < stringCount; i++)
                    stringTable[i] = reader.ReadString();

                if (output.Capacity < output.Count + itemCount)
                    output.Capacity = output.Count + itemCount;

                for (int i = 0; i < itemCount; i++)
                {
                    var data = new MapItemData
                    {
                        Index = reader.ReadInt32(),
                        Parent = stringTable[reader.ReadInt32()],
                        ObjName = stringTable[reader.ReadInt32()],
                        Pos = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
                        Rotation = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
                        Scale = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
                        IsActive = reader.ReadBoolean(),
                    };
                    output.Add(data);
                }
            }

            return true;
        }

        public static void WriteBinary(List<MapItemData> items, Stream stream)
        {
            var stringToIndex = new Dictionary<string, int>();
            var stringTable = new List<string>();
            int Intern(string value)
            {
                if (!stringToIndex.TryGetValue(value, out int index))
                {
                    index = stringTable.Count;
                    stringTable.Add(value);
                    stringToIndex.Add(value, index);
                }

                return index;
            }

            using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
            {
                writer.Write(BinaryMagic);
                writer.Write(items.Count);

                var parentIdx = new int[items.Count];
                var objNameIdx = new int[items.Count];
                for (int i = 0; i < items.Count; i++)
                {
                    parentIdx[i] = Intern(items[i].Parent ?? "null");
                    objNameIdx[i] = Intern(items[i].ObjName ?? string.Empty);
                }

                writer.Write(stringTable.Count);
                for (int i = 0; i < stringTable.Count; i++)
                    writer.Write(stringTable[i]);

                for (int i = 0; i < items.Count; i++)
                {
                    MapItemData data = items[i];
                    writer.Write(data.Index);
                    writer.Write(parentIdx[i]);
                    writer.Write(objNameIdx[i]);
                    writer.Write(data.Pos.x);
                    writer.Write(data.Pos.y);
                    writer.Write(data.Pos.z);
                    writer.Write(data.Rotation.x);
                    writer.Write(data.Rotation.y);
                    writer.Write(data.Rotation.z);
                    writer.Write(data.Scale.x);
                    writer.Write(data.Scale.y);
                    writer.Write(data.Scale.z);
                    writer.Write(data.IsActive);
                }
            }
        }
    }
}
