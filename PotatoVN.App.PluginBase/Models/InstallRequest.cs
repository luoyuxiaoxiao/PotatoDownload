using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;

namespace PotatoVN.App.PluginBase.Models;

/// <summary>
/// Shionlib 推送安装请求（协议 v1，与 ReinaManager 的 reinamanager://install 协议对齐）。
/// </summary>
public class InstallRequest
{
    public const uint SupportedProtocolVersion = 1;
    public const string Scheme = "potato-vn";
    public const string Host = "install";

    private static readonly string[] SupportedArchiveFormats =
        ["7z", "zip", "rar", "tar", "tar.gz", "tar.bz2", "tar.xz", "tar.zst"];

    private static readonly Regex ProviderRegex = new("^[a-z0-9._-]{1,64}$", RegexOptions.Compiled);
    private static readonly Regex ChecksumRegex = new("^[0-9a-f]{64}$", RegexOptions.Compiled);

    public uint V { get; init; }
    public string Provider { get; init; } = string.Empty;
    public string ResourceId { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public string ArchiveFormat { get; init; } = string.Empty;
    public string? ArchivePassword { get; init; }
    public ulong Size { get; init; }
    public string? ChecksumAlgo { get; init; }
    public string? Checksum { get; init; }
    public long? ExpiresAt { get; init; }
    public string? BgmId { get; init; }
    public string? VndbId { get; init; }
    public string? HikarinagiId { get; init; }
    public string Title { get; init; } = string.Empty;

    /// <summary>去重键：同一 provider + resource_id + checksum + url 视为同一次推送。</summary>
    public string DeduplicationKey => $"{Provider}\u001f{ResourceId}\u001f{Checksum ?? string.Empty}\u001f{Url}";

    public static InstallRequest Parse(Uri uri)
    {
        if (!string.Equals(uri.Scheme, Scheme, StringComparison.OrdinalIgnoreCase))
            throw new InstallRequestException("不支持的协议 scheme");
        if (!string.Equals(uri.Host, Host, StringComparison.OrdinalIgnoreCase))
            throw new InstallRequestException("不支持的协议 host");

        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        var request = new InstallRequest
        {
            V = ParseUInt(query["v"], "v"),
            Provider = query["provider"] ?? string.Empty,
            ResourceId = query["resource_id"] ?? string.Empty,
            Url = query["url"] ?? string.Empty,
            FileName = query["file_name"] ?? string.Empty,
            ArchiveFormat = query["archive_format"] ?? string.Empty,
            ArchivePassword = query["archive_password"],
            Size = ParseULong(query["size"], "size"),
            ChecksumAlgo = query["checksum_algo"]?.ToLowerInvariant(),
            Checksum = query["checksum"]?.ToLowerInvariant(),
            ExpiresAt = ParseLong(query["expires_at"], "expires_at"),
            BgmId = query["bgm_id"],
            VndbId = query["vndb_id"],
            HikarinagiId = query["hikarinagi_id"],
            Title = query["title"] ?? string.Empty,
        };
        request.Validate();
        return request;
    }

    public void Validate()
    {
        if (V != SupportedProtocolVersion)
            throw new InstallRequestException($"不支持的安装协议版本: {V}");
        if (!ProviderRegex.IsMatch(Provider))
            throw new InstallRequestException("provider 格式无效");
        if (string.IsNullOrWhiteSpace(ResourceId) || ResourceId.Length > 256)
            throw new InstallRequestException("resource_id 为空或过长");

        if (!Uri.TryCreate(Url, UriKind.Absolute, out var downloadUrl) ||
            (downloadUrl.Scheme != Uri.UriSchemeHttp && downloadUrl.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrEmpty(downloadUrl.Host))
            throw new InstallRequestException("下载 URL 无效");
        if (IsPrivateOrLoopbackHost(downloadUrl.Host))
            throw new InstallRequestException("下载 URL 不允许指向本机或内网地址");

        if (!IsSafeFileName(FileName))
            throw new InstallRequestException("file_name 必须是安全的单个文件名");
        if (!SupportedArchiveFormats.Contains(ArchiveFormat))
            throw new InstallRequestException($"不支持的压缩格式: {ArchiveFormat}");
        if (ArchivePassword is { Length: 0 })
            throw new InstallRequestException("archive_password 不能为空");
        if (ArchivePassword is { Length: > 1024 })
            throw new InstallRequestException("archive_password 过长");
        if (ArchivePassword is not null && ArchivePassword.IndexOfAny(['\r', '\n', '\0']) >= 0)
            throw new InstallRequestException("archive_password 包含非法字符");
        if (Size == 0)
            throw new InstallRequestException("文件大小无效");

        switch (ChecksumAlgo, Checksum)
        {
            case (null, null):
                break;
            case ("sha256" or "blake3", { } checksum) when ChecksumRegex.IsMatch(checksum):
                break;
            case (null, _) or (_, null):
                throw new InstallRequestException("checksum_algo 与 checksum 必须同时提供");
            default:
                throw new InstallRequestException($"不支持的校验算法: {ChecksumAlgo}");
        }

        if (ExpiresAt is <= 0)
            throw new InstallRequestException("expires_at 无效");
        if (string.IsNullOrWhiteSpace(BgmId))
            throw new InstallRequestException("bgm_id 不能为空");
        if (string.IsNullOrWhiteSpace(Title))
            throw new InstallRequestException("title 不能为空");
    }

    /// <summary>下载直链是否已过期。</summary>
    public bool IsExpired(DateTimeOffset now) => ExpiresAt is { } expires && now.ToUnixTimeSeconds() >= expires;

    private static bool IsSafeFileName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "." or "..") return false;
        if (value.IndexOfAny(['/', '\\', ':']) >= 0) return false;
        var fileName = Path.GetFileName(value);
        return string.Equals(fileName, value, StringComparison.Ordinal);
    }

    /// <summary>
    /// 判断主机是否指向本机/内网（对齐 LunaBox 协议的安全策略，防止 SSRF）。
    /// 拒绝：localhost、回环、私有网段、链路本地、0.0.0.0、常见内网域名后缀。
    /// </summary>
    private static bool IsPrivateOrLoopbackHost(string host)
    {
        var normalized = host.Trim().TrimEnd('.').ToLowerInvariant();
        if (normalized is "localhost" or "0.0.0.0" or "::1") return true;
        if (normalized.EndsWith(".local", StringComparison.Ordinal) ||
            normalized.EndsWith(".internal", StringComparison.Ordinal) ||
            normalized.EndsWith(".lan", StringComparison.Ordinal) ||
            normalized.EndsWith(".home", StringComparison.Ordinal))
            return true;

        if (IPAddress.TryParse(normalized, out var ip))
        {
            if (IPAddress.IsLoopback(ip)) return true;
            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                var bytes = ip.GetAddressBytes();
                // 10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16, 169.254.0.0/16
                if (bytes[0] == 10) return true;
                if (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) return true;
                if (bytes[0] == 192 && bytes[1] == 168) return true;
                if (bytes[0] == 169 && bytes[1] == 254) return true;
            }
        }
        return false;
    }

    private static uint ParseUInt(string? value, string name)
    {
        if (!uint.TryParse(value, out var result)) throw new InstallRequestException($"缺少或无效参数: {name}");
        return result;
    }

    private static ulong ParseULong(string? value, string name)
    {
        if (!ulong.TryParse(value, out var result)) throw new InstallRequestException($"缺少或无效参数: {name}");
        return result;
    }

    private static long? ParseLong(string? value, string name)
    {
        if (string.IsNullOrEmpty(value)) return null;
        if (!long.TryParse(value, out var result)) throw new InstallRequestException($"无效参数: {name}");
        return result;
    }
}

public class InstallRequestException(string message) : Exception(message);
