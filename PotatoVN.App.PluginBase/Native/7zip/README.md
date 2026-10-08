# 7-Zip 原生解压库

本目录随插件原样打包，包含 **7-Zip ZS 26.02（v1.5.7-R2，2026-07-09 发布）** 发行物中的 `7z.dll`，未修改二进制。
7-Zip ZS 是 mcmilk 维护的 7-Zip 分支，在官方 26.02 基线上额外支持 7z 容器内的 Zstandard / LZ4 / Brotli / Lizard / LZ5 / Fast-LZMA2
（Shionlib 上已出现 ZSTD 打包的 7z，官方 7-Zip 的 7z 格式不支持容器内 zstd，只有该分支/同类能解）。
仅 Windows 的 7z 格式调用此引擎；进程架构分别选择 win-x86、win-x64、win-arm64。
没有安装器、外部进程或系统全局安装依赖，也不搜索 PATH。每次操作关闭 COM 对象后立即卸载 DLL。

- 分支仓库与发布页：https://github.com/mcmilk/7-Zip-zstd/releases/tag/v26.02-v1.5.7-R2
- 上游官方（基线 26.02）：https://github.com/ip7z/7zip/releases/tag/26.02
- 版权：Copyright (C) 1999-2026 Igor Pavlov；分支增补编解码器版权见分支仓库。
- 许可：LGPL-2.1-or-later（主体），另含 BSD 2/3-clause 代码及 unRAR 限制；附加编解码器（zstd/brotli/lz4/lizard/lz5/fast-lzma2）为 BSD/MIT 类许可。完整分发声明见 `License.txt`，LGPL 正文见 `LGPL-2.1.txt`。这些条款只针对本第三方库。

## 可复现来源

从下表对应的发布地址下载固定版本安装包（NSIS），用任意 7-Zip 兼容工具直接解出根目录的 `7z.dll`（本仓库 2026-10-08 即用插件自身 COM 绑定从官方 26.03 dll 解出，无需运行安装器）。
升级时同时更新全部架构、版本、摘要和许可证，并在 Windows 重跑 CoreChecks（包含原生解压、密码、取消与 DLL 释放检查）。

- **win-x86**（2,851,521 字节）：https://github.com/mcmilk/7-Zip-zstd/releases/download/v26.02-v1.5.7-R2/7z26.02-zstd-x86.exe
  - 发行包 SHA-256：下载后自验（release 未公布哈希）
  - `7z.dll` SHA-256：`a48905c82caeb2beed2b31e4900bbfbd594d00bae68bf99c1c38e7844a38a51f`
- **win-x64**（3,344,024 字节）：https://github.com/mcmilk/7-Zip-zstd/releases/download/v26.02-v1.5.7-R2/7z26.02-zstd-x64.exe
  - 发行包 SHA-256：下载后自验
  - `7z.dll` SHA-256：`e491beaaa7d165d8e1c862f55ee556148640d4d0f582b10fec072f4d9b93d268`
- **win-arm64**（3,001,051 字节）：https://github.com/mcmilk/7-Zip-zstd/releases/download/v26.02-v1.5.7-R2/7z26.02-zstd-arm64.exe
  - 发行包 SHA-256：下载后自验
  - `7z.dll` SHA-256：`12434d4387782cc0ba624a79f2ba2d02018ee0361698fc8301a1ee03eca6c1cf`

## 打包与加载约定

原生资产位于 `Native/7zip`，避免被现有 PackPlugin 的 `runtimes` 清理规则移除。
打包目标会检查三种 DLL 均存在；缺包时运行时直接报错，不能静默改用托管 7z。
插件初始化只保存 `GetPluginPath()` 返回的目录；不固定占用 DLL，不与其它插件共享全局封装状态。

COM ABI 的 GUID、属性编号与方法顺序对照官方 `CPP/7zip/IStream.h`、`Archive/IArchive.h`、`IPassword.h`、`PropID.h`；本仓库的适配代码为独立实现，未复制第三方 .NET 封装源码。
