using System;
using System.Collections.Generic;
using System.Text;

namespace PotatoVN.App.PluginBase.Helper;

/// <summary>
/// 推送测试：构造 potato-vn://install 测试深链（与 TestPlatform/index.html 的预设用例一致）。
/// 测试载荷托管于 httpbingo.org/base64/（2026-09 实测字节级可靠；
/// httpbin.org 的 /base64 对含 +/ 的标准 base64 一律 404，不可用）。
/// 注意：发布应用市场前必须移除本功能（Plugin_Ui 中的"推送测试"侧边栏按钮）。
/// </summary>
internal static class TestPush
{
    private const string PayloadBase64Url =
        "UEsDBBQAAAAIAAAAIVz11Vl9OAAAADYAAAAQAAAAQ0xBTk5BRC9nYW1lLmV4ZQvIL0ksyXfJL8_LyU9MUXA1clUoSS0uUUitSE0uLUlMyknVU_DLL1FIVChKTcxRSE_MTdXj5QIAUEsDBBQAAAAIAAAAIVxXf6mLQQAAAEQAAAASAAAAQ0xBTk5BRC9yZWFkbWUudHh0C8gvSSzJd8kvz8vJT0xRSM1L0S3J1wVSCiWpxSUKBYmVIHEdhfTUvNSixJLUFIWkSoUQoFRATmJJWn5Rrh4vFwBQSwECFAAUAAAACAAAACFc9dVZfTgAAAA2AAAAEAAAAAAAAAAAAAAAAAAAAAAAQ0xBTk5BRC9nYW1lLmV4ZVBLAQIUABQAAAAIAAAAIVxXf6mLQQAAAEQAAAASAAAAAAAAAAAAAAAAAGYAAABDTEFOTkFEL3JlYWRtZS50eHRQSwUGAAAAAAIAAgB-AAAA1wAAAAAA";
    private const int PayloadSize = 363;
    private const string PayloadSha256 = "0cdfccdc7ca8af40cc94c15f20de7d0e940f0ab731bed16cef34acafcdad4754";
    private const string Endpoint = "https://httpbingo.org/base64/";
    private const long ExpiresFar = 2000000000; // 2033-05-18

    // E2E 必须使用真实存在且可联网刮削的游戏（宿主 AddVirtualGame 会按 title 在线刮削，
    // 查无此游戏时直接抛 PvnException），这里用 CLANNAD（bgm subject 13）。
    // 注意：E2E 会把假的安装路径挂到库里的 CLANNAD 条目上，测试后请手动删除该条目/安装。
    private const string TestTitle = "CLANNAD";
    private const string TestBgmId = "13";
    private const string TestFileName = "CLANNAD.zip";

    public sealed record Scenario(string Name, string Description, string Expect, Func<string> BuildUrl);

    public static IReadOnlyList<Scenario> Scenarios { get; } = new[]
    {
        new Scenario(
            "完整流程（E2E）",
            "以真实游戏条目 CLANNAD（bgm_id=13）推送：下载 → sha256 校验 → 解压 → 入库 → 刮削。测试后请删除库中 CLANNAD 条目及其安装路径。",
            "弹条「开始下载」→ 下载弹窗任务推进 → 「完成」→ 库中 CLANNAD 条目新增安装路径。",
            () => BuildValid()),
        new Scenario(
            "已过期链接",
            "expires_at 设为 2001 年；推送入队，但下载阶段判定链接过期。",
            "任务出现后失败，提示链接已过期。",
            () => BuildValid(expiresAt: 1000000000)),
        new Scenario(
            "校验和错误",
            "checksum 全 0；文件能下载完，但 sha256 校验必然失败。",
            "任务在「校验」阶段失败并提示校验不匹配。",
            () => BuildValid(checksum: new string('0', 64))),
        new Scenario(
            "内网地址（SSRF 防护）",
            "url 指向 192.168 内网地址；解析阶段直接拒绝。",
            "弹错误「下载 URL 不允许指向本机或内网地址」，无新任务。",
            () => BuildValid(url: "http://192.168.1.100/test_game.zip")),
        new Scenario(
            "缺少必填参数",
            "故意去掉 size 参数；解析阶段拒绝。",
            "弹错误「缺少或无效参数: size」。",
            () => BuildValid(omitSize: true)),
    };

    private static string BuildValid(string? url = null, string? checksum = null,
        long expiresAt = ExpiresFar, bool omitSize = false)
    {
        // 每次构造都生成唯一 resource_id，避免被插件的永久去重（provider+resource_id+checksum+url）拦下
        var resourceId = "test-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString("x");
        var pairs = new List<KeyValuePair<string, string>>
        {
            new("v", "1"),
            new("provider", "test"),
            new("resource_id", resourceId),
            new("url", url ?? Endpoint + PayloadBase64Url),
            new("file_name", TestFileName),
            new("archive_format", "zip"),
        };
        if (!omitSize) pairs.Add(new KeyValuePair<string, string>("size", PayloadSize.ToString()));
        pairs.Add(new KeyValuePair<string, string>("checksum_algo", "sha256"));
        pairs.Add(new KeyValuePair<string, string>("checksum", checksum ?? PayloadSha256));
        pairs.Add(new KeyValuePair<string, string>("expires_at", expiresAt.ToString()));
        pairs.Add(new KeyValuePair<string, string>("bgm_id", TestBgmId));
        pairs.Add(new KeyValuePair<string, string>("title", TestTitle));

        var sb = new StringBuilder("potato-vn://install?");
        foreach (var (key, value) in pairs)
            sb.Append(key).Append('=').Append(Uri.EscapeDataString(value)).Append('&');
        return sb.ToString(0, sb.Length - 1);
    }
}
