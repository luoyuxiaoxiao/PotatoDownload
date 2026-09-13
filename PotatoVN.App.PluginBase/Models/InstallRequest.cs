using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
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
    /// <summary>单文件大小上限：size 来自深链（任意网页可构造），用于预分配前的合理性检查。</summary>
    public const ulong MaxSize = 512UL << 30;

    private static readonly string[] SupportedArchiveFormats =
        ["7z", "zip", "rar", "tar", "tar.gz", "tar.bz2", "tar.xz", "tar.zst"];

    private static readonly Regex ProviderRegex = new("^[a-z0-9._-]{1,64}$", RegexOptions.Compiled);
    private static readonly Regex ChecksumRegex = new("^[0-9a-f]{64}$", RegexOptions.Compiled);

    private const string InvalidFileNameChars = "<>:\"/\\|?*";
    private static readonly string[] ReservedFileNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];
    /// <summary>写日志时必须脱敏的参数：带签名的下载直链、压缩包密码。</summary>
    private static readonly string[] SensitiveQueryKeys = ["url", "archive_password"];

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
        if (Size == 0 || Size > MaxSize)
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
        if (string.IsNullOrWhiteSpace(Title) || Title.Length > 512)
            throw new InstallRequestException("title 为空或过长");
    }

    /// <summary>下载直链是否已过期。</summary>
    public bool IsExpired(DateTimeOffset now) => ExpiresAt is { } expires && now.ToUnixTimeSeconds() >= expires;

    /// <summary>深链的日志形式：签名直链与压缩包密码替换为占位符，其余参数保留便于排查。</summary>
    public static string RedactForLog(Uri uri)
    {
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        var parts = query.AllKeys
            .Where(key => key is not null)
            .Select(key => SensitiveQueryKeys.Contains(key!, StringComparer.OrdinalIgnoreCase)
                ? $"{key}=<redacted>"
                : $"{key}={query[key]}");
        return $"{uri.Scheme}://{uri.Host}?{string.Join("&", parts)}";
    }

    /// <summary>
    /// 安全的单段文件/目录名：不含路径分隔符与 Windows 非法字符、不是保留设备名（CON、NUL…）、
    /// 不以点或空格结尾。用于 file_name 与压缩包顶层目录名（两者都来自不可信输入）。
    /// </summary>
    internal static bool IsSafeFileName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 255) return false;
        if (value is "." or "..") return false;
        if (value[^1] is '.' or ' ') return false;
        foreach (var c in value)
            if (c < 0x20 || InvalidFileNameChars.Contains(c)) return false;
        var stem = value.Split('.')[0];
        return !ReservedFileNames.Contains(stem, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 判断主机名是否指向本机/内网（对齐 LunaBox 协议的安全策略，防止 SSRF）。
    /// 这里只能检查字面 IP 与内网域名后缀；域名解析结果与 HTTP 重定向目标由
    /// DownloadService 在建连时用 <see cref="IsForbiddenAddress"/> 再检查一次。
    /// </summary>
    internal static bool IsPrivateOrLoopbackHost(string host)
    {
        var normalized = host.Trim().Trim('[', ']').TrimEnd('.').ToLowerInvariant();
        if (normalized is "localhost") return true;
        if (normalized.EndsWith(".local", StringComparison.Ordinal) ||
            normalized.EndsWith(".internal", StringComparison.Ordinal) ||
            normalized.EndsWith(".lan", StringComparison.Ordinal) ||
            normalized.EndsWith(".home", StringComparison.Ordinal) ||
            normalized.EndsWith(".localhost", StringComparison.Ordinal))
            return true;
        return IPAddress.TryParse(normalized, out var ip) && IsForbiddenAddress(ip);
    }

    /// <summary>
    /// 禁止连接的地址：回环、未指定、私有网段、CGNAT、链路本地、组播与保留段；
    /// IPv4 映射的 IPv6 地址按其 IPv4 判断，IPv6 另含唯一本地地址 fc00::/7。
    /// </summary>
    internal static bool IsForbiddenAddress(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) return true;
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast) return true;
            return (ip.GetAddressBytes()[0] & 0xfe) == 0xfc;
        }
        var bytes = ip.GetAddressBytes();
        return bytes[0] switch
        {
            0 or 10 or 127 => true,
            100 when bytes[1] is >= 64 and <= 127 => true,
            169 when bytes[1] == 254 => true,
            172 when bytes[1] is >= 16 and <= 31 => true,
            192 when bytes[1] == 168 => true,
            >= 224 => true,
            _ => false,
        };
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
