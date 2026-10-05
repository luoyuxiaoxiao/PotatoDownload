using System;
using System.IO;
using System.Text.Json;

namespace PotatoVN.App.PluginBase.Services;

/// <summary>
/// 写入宿主格式的 .PotatoVN/meta.json（GameMetaBackup v2 的最小子集：Name + Ids）。
/// 宿主 AddGameInstallation 优先读取安装目录内的本地 meta，存在时跳过在线刮削，
/// 避免按目录名二次刮削的结果与占位游戏 UID 冲突（vndb 前缀差异、命中不同条目、
/// 离线刮削失败）而重复建游戏。宿主用 Newtonsoft 默认设置反序列化，
/// 这里的属性名与 GameMetaBackup/Galgame/LockableProperty 的序列化形状一一对应。
/// 本类不依赖宿主程序集，方便 CoreChecks 直接链接测试。
/// </summary>
internal static class LocalMetaWriter
{
    private const string MetaDirectoryName = ".PotatoVN";
    private const string MetaFileName = "meta.json";
    private const int BackupVersion = 2; // GameMetaBackup.CurrentVersion（宿主稳定版 v1.10.2 起即为 2）

    /// <summary>
    /// 在安装目录写入本地 meta；已存在 meta（压缩包自带的用户数据）时绝不覆盖，返回 false。
    /// </summary>
    public static bool TryWrite(string gamePath, string name, string?[] ids)
    {
        var metaDir = Path.Combine(gamePath, MetaDirectoryName);
        var metaFile = Path.Combine(metaDir, MetaFileName);
        if (File.Exists(metaFile)) return false;
        Directory.CreateDirectory(metaDir);
        var backup = new MetaBackup
        {
            Game = new MetaGame
            {
                Name = new MetaLockableString { Value = name },
                Ids = ids,
            },
        };
        File.WriteAllText(metaFile, JsonSerializer.Serialize(backup));
        return true;
    }

    /// <summary>
    /// 入库完成后移除本插件写入的 meta：留在目录里会让宿主下次添加该文件夹时
    /// 读到这份只有 ID 的稀疏快照，跳过在线刮削补全。只删 meta.json 本身，
    /// 目录里出现其它文件（宿主后来写入的完整备份）时连目录一起保留。
    /// </summary>
    public static void Cleanup(string gamePath)
    {
        try
        {
            var metaDir = Path.Combine(gamePath, MetaDirectoryName);
            var metaFile = Path.Combine(metaDir, MetaFileName);
            if (File.Exists(metaFile)) File.Delete(metaFile);
            if (Directory.Exists(metaDir) && Directory.GetFileSystemEntries(metaDir).Length == 0)
                Directory.Delete(metaDir);
        }
        catch
        {
            // 清理失败只影响下次手动添加该文件夹时是否重新刮削，不影响本次入库结果
        }
    }

    private sealed class MetaBackup
    {
        public int Version { get; set; } = BackupVersion;
        public MetaGame Game { get; set; } = new();
    }

    private sealed class MetaGame
    {
        public MetaLockableString Name { get; set; } = new();
        public string?[]? Ids { get; set; }
    }

    private sealed class MetaLockableString
    {
        public string? Value { get; set; }
        public bool IsLock { get; set; }
    }
}
