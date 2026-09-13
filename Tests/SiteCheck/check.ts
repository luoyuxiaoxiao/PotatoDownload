// 测验网站构链等价性自检：docs/index.html 里移植的 buildPotatoVNUrl 必须与 Shionlib 源码产出逐字节相同，
// 且测试开关（provider 覆盖/缺参/不校验/密码/格式覆盖）只存在于包装层。
// 运行：SHIONLIB_DIR=~/Projects/scratch/shionlib deno run --allow-read --allow-env --sloppy-imports --no-check Tests/SiteCheck/check.ts
const shionlib = Deno.env.get("SHIONLIB_DIR") ?? `${Deno.env.get("HOME")}/Projects/scratch/shionlib`;
const { buildPotatoVNUrl: real } = await import(
  new URL(`file://${shionlib}/apps/frontend/components/game/download/helpers/potatovn.ts`).href
);

const html = Deno.readTextFileSync(new URL("../../docs/index.html", import.meta.url));
const src = html.slice(html.indexOf("const PAYLOAD = {"), html.indexOf("const PRESETS = ["));
const m = new Function(src + "\nreturn { buildInstallUrl, validPresetParams, getArchiveFormat, PAYLOAD_URL };")();

let n = 0;
const eq = (a: unknown, b: unknown, what: string) => {
  n++;
  if (a !== b) { console.error(`FAIL ${what}\n  got:  ${a}\n  want: ${b}`); Deno.exit(1); }
};
const toReal = (p: any) => real({
  resourceId: p.resourceId, url: p.url, fileName: p.fileName, size: p.size,
  checksumAlgo: p.checksumAlgo, checksum: p.checksum, expiresAt: p.expiresAt,
  title: p.title, bangumiId: p.bgmId, vndbId: p.vndbId, hikarinagiId: p.hikarinagiId,
});
const q = (u: string) => new URLSearchParams(u.slice(u.indexOf("?") + 1));

// 1. 默认 E2E 预设与 Shionlib 完全一致
const base = m.validPresetParams();
eq(m.buildInstallUrl(base), toReal(base), "E2E preset == shionlib");

// 2. 刁钻标题 / 文件名 / 签名直链：编码逐字节一致（空格→+、Unicode、保留字符）
const nasty = m.validPresetParams({
  title: "Fate/stay night 【Réalta Nua】 & more? #1 100%",
  fileName: "Fate stay night.tar.gz",
  url: "https://cdn.example.com/dl/Fate%20stay.tar.gz?token=a+b%2Fc&expires=1&sig=x/y=",
  vndbId: "v11", hikarinagiId: 42,
});
const nastyUrl = m.buildInstallUrl(nasty);
eq(nastyUrl, toReal(nasty), "nasty title == shionlib");
eq(q(nastyUrl).get("archive_format"), "tar.gz", "archive_format derived from tar.gz");
eq(q(nastyUrl).get("title"), nasty.title, "title round-trips");
eq(q(nastyUrl).get("url"), nasty.url, "signed url round-trips");
eq(q(nastyUrl).get("vndb_id"), "v11", "vndb prefix kept");
eq(q(nastyUrl).get("hikarinagi_id"), "42", "hikarinagi_id set");

// 3. 包装层开关
eq(q(m.buildInstallUrl(m.validPresetParams({ omit: ["size"] }))).has("size"), false, "omit size");
const noSum = q(m.buildInstallUrl(m.validPresetParams({ checksumAlgo: "", checksum: "" })));
eq(noSum.has("checksum_algo") || noSum.has("checksum"), false, "no-checksum drops both");
eq(q(m.buildInstallUrl(m.validPresetParams({ provider: "test" }))).get("provider"), "test", "provider override");
eq(q(m.buildInstallUrl(m.validPresetParams({ provider: "shionlib" }))).get("provider"), "shionlib", "provider default");
eq(q(m.buildInstallUrl(m.validPresetParams({ archiveFormat: "7z" }))).get("archive_format"), "7z", "archive_format override");
eq(q(m.buildInstallUrl(m.validPresetParams({ archivePassword: "p w&d" }))).get("archive_password"), "p w&d", "password round-trips");
// 表单传入空字符串开关时仍须等于 Shionlib
eq(m.buildInstallUrl({ ...base, provider: "shionlib", archiveFormat: "", archivePassword: "", vndbId: "", hikarinagiId: "" }),
   toReal({ ...base, vndbId: "", hikarinagiId: "" }), "empty form knobs == shionlib");
eq(m.PAYLOAD_URL.startsWith("https://luoyuxiaoxiao.github.io/PotatoDownload/"), true, "payload on Pages");
eq(m.getArchiveFormat("x.exe"), "none", "unknown ext → none (shionlib behaviour)");

console.log(`ALL PASS (${n} checks)`);
