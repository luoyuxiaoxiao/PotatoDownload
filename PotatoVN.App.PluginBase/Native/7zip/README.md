# 7-Zip 原生解压库

本目录随插件原样打包，包含 **7-Zip 26.03（2026-09-03）** 官方 Windows 发行物中的 `7z.dll`，未修改二进制。
仅 Windows 的 7z 格式调用此引擎；进程架构分别选择 win-x86、win-x64、win-arm64。
没有安装器、外部进程或系统全局安装依赖，也不搜索 PATH。每次操作关闭 COM 对象后立即卸载 DLL。

- 官方下载页：https://www.7-zip.org/download.html
- 官方固定版本：https://github.com/ip7z/7zip/releases/tag/26.03
- 对应完整源码：https://github.com/ip7z/7zip/releases/download/26.03/7z2603-src.tar.xz
- 版权：Copyright (C) 1999-2026 Igor Pavlov。
- 许可：LGPL-2.1-or-later（主体），另含 BSD 2/3-clause 代码及 unRAR 限制。完整分发声明见 `License.txt`，LGPL 正文见 `LGPL-2.1.txt`。这些条款只针对本第三方库。

## 可复现来源

从下表对应的官方 HTTPS 地址下载固定版本，再用 `7z x -so <发行包.exe> 7z.dll` 提取，无需运行安装器。
升级时同时更新全部架构、版本、摘要和许可证，并在 Windows 重跑 CoreChecks（包含原生解压、密码、取消与 DLL 释放检查）。

- **win-x86**（1,317,376 字节）：https://github.com/ip7z/7zip/releases/download/26.03/7z2603.exe
  - 发行包 SHA-256：`0f6ec2eda1f8c5dc4c267ee761c0dad8a9d5e8863e0c84b7ac026bc9625a1560`
  - `7z.dll` SHA-256：`d132e89038c802c5d5281e543a83dc407680effe0144f21b4fb431dd45fca61d`
- **win-x64**（1,906,688 字节）：https://github.com/ip7z/7zip/releases/download/26.03/7z2603-x64.exe
  - 发行包 SHA-256：`0859c524b8a63551848f0c246abddcb1d0b7b656b0fbfe879f8d85e61a9e6edd`
  - `7z.dll` SHA-256：`65e4c1f855f9ef6e8f0f5df8e3f27d9eb5f07311408639da0a1ca0b8f4871b0d`
- **win-arm64**（1,767,936 字节）：https://github.com/ip7z/7zip/releases/download/26.03/7z2603-arm64.exe
  - 发行包 SHA-256：`e22ce71c11dcf503c448fe51e56f41830eb4e1344fa5c7731ae63bce533a8e8e`
  - `7z.dll` SHA-256：`a1f2e41eaf7ad40f5b1aa66a73e7fe327e64e0b7d65638a863c1e3a6fe64e667`

## 打包与加载约定

原生资产位于 `Native/7zip`，避免被现有 PackPlugin 的 `runtimes` 清理规则移除。
打包目标会检查三种 DLL 均存在；缺包时运行时直接报错，不能静默改用托管 7z。
插件初始化只保存 `GetPluginPath()` 返回的目录；不固定占用 DLL，不与其它插件共享全局封装状态。

COM ABI 的 GUID、属性编号与方法顺序对照官方 `CPP/7zip/IStream.h`、`Archive/IArchive.h`、`IPassword.h`、`PropID.h`；本仓库的适配代码为独立实现，未复制第三方 .NET 封装源码。
