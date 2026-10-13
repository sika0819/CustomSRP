using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using YooAsset.Editor;

[DisplayName("定位地址: 文件名.智能尾缀")]
public class AddressByFileNameAndExt : IAddressRule
{
    public string GetAssetAddress(AddressRuleData data)
    {
        var ext = Path.GetExtension(data.AssetPath);
        if (ext == ".asset")
        {
            var a = UnityEditor.AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(data.AssetPath);
            if (a == null) return ".errortype";
            var type = a.GetType();
            var dt = Path.GetFileNameWithoutExtension(data.AssetPath);
            return dt + $".{type.Name.ToLowerInvariant()}";
        }

        return Path.GetFileName(data.AssetPath);
    }
}

/// <summary>
/// 父文件夹名 + 带扩展名的文件名，避免同名 mat/贴图、以及不同目录下同名文件地址冲突。
/// 例：Plants_CropField_01.mat / Plants_CropField_01.tga
/// </summary>
[DisplayName("定位地址: 文件夹名_文件名.扩展名")]
public class AddressByFolderAndFileNameAndExt : IAddressRule
{
    public string GetAssetAddress(AddressRuleData data)
    {
        string fileName = Path.GetFileName(data.AssetPath);
        var dirName = new FileInfo(data.AssetPath).Directory?.Name ?? string.Empty;
        return string.IsNullOrEmpty(dirName) ? fileName : $"{dirName}_{fileName}";
    }
}

/// <summary>
/// DaoYu1 下相对路径（无扩展名）。例：Lvshu/Maple 1、Other/Huodui。
/// 与 Game.SceneAssets.DaoYu1Location 对齐。
/// </summary>
[DisplayName("定位地址: DaoYu1 相对路径")]
public class AddressByDaoYu1Relative : IAddressRule
{
    const string Marker = "/Prefabs/DaoYu1/";

    public string GetAssetAddress(AddressRuleData data)
    {
        string path = data.AssetPath.Replace('\\', '/');
        int idx = path.IndexOf(Marker, StringComparison.OrdinalIgnoreCase);
        string relative = idx >= 0
            ? path.Substring(idx + Marker.Length)
            : Path.GetFileName(path);
        int dot = relative.LastIndexOf('.');
        if (dot > 0)
            relative = relative.Substring(0, dot);
        return relative;
    }
}