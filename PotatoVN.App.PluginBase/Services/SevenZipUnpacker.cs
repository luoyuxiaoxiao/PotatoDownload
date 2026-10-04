using System;
using System.Buffers;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using PotatoVN.App.PluginBase.Models;
using static PotatoVN.App.PluginBase.Services.SevenZipNative;

namespace PotatoVN.App.PluginBase.Services;

/// <summary>官方 7z.dll 的小型解压适配层；每个归档独立持有、关闭并释放原生对象与 DLL。</summary>
internal static class SevenZipUnpacker
{
    private static string? _pluginDirectory;

    internal static void SetPluginDirectory(string directory) =>
        _pluginDirectory = Path.GetFullPath(directory);

    internal static string GetLibraryPath(string directory, Architecture architecture)
    {
        var rid = architecture switch
        {
            Architecture.X86 => "win-x86",
            Architecture.X64 => "win-x64",
            Architecture.Arm64 => "win-arm64",
            _ => throw new PlatformNotSupportedException($"7-Zip 不支持当前进程架构：{architecture}"),
        };
        return Path.Combine(Path.GetFullPath(directory), "Native", "7zip", rid, "7z.dll");
    }

    private static string LibraryPath()
    {
        // 宿主从内存加载插件时 Assembly.Location 可能为空，初始化阶段用稳定 API GetPluginPath 明确传入。
        var directory = _pluginDirectory ?? Path.GetDirectoryName(typeof(SevenZipUnpacker).Assembly.Location);
        if (string.IsNullOrEmpty(directory)) throw new InvalidOperationException("未能确定插件的 7-Zip 原生库目录。");
        var path = GetLibraryPath(directory, RuntimeInformation.ProcessArchitecture);
        if (!File.Exists(path)) throw new FileNotFoundException("插件缺少对应架构的 7z.dll，请重新安装完整插件包。", path);
        return path;
    }

    [SupportedOSPlatform("windows")]
    internal static IEnumerable<string> EntryKeys(InstallRequest request, string path, CancellationToken ct)
    {
        using var archive = new NativeArchive(path, request.ArchivePassword, ct);
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (!entry.IsDirectory) yield return entry.Name;
        }
    }

    [SupportedOSPlatform("windows")]
    internal static void Extract(InstallRequest request, string path, string targetDirectory,
        Action<int, int>? onProgress, CancellationToken ct, Action<UnpackProgress>? onDetailedProgress)
    {
        using var archive = new NativeArchive(path, request.ArchivePassword, ct);
        long? total = 0;
        foreach (var entry in archive.Entries.Where(e => !e.IsDirectory))
            total = total is { } sum && entry.Size is { } size && size <= long.MaxValue - sum ? sum + size : null;
        var session = new UnpackService.ExtractionSession(targetDirectory, total,
            archive.Entries.Count(e => !e.IsDirectory), onProgress, onDetailedProgress, ct);
        foreach (var entry in archive.Entries)
            session.ValidateEntry(entry.Name, entry.IsDirectory, entry.LinkTarget, entry.Attributes);
        archive.Extract(session);
    }

    private sealed record NativeEntry(string Name, bool IsDirectory, long? Size,
        int? Attributes, string? LinkTarget, DateTime? ModifiedTime);

    /// <summary>回调只向原生返回 HRESULT；原异常留在托管侧，Extract/Open 返回后原样抛出。</summary>
    private sealed class CallbackContext
    {
        private ExceptionDispatchInfo? _failure;
        internal CancellationToken Token { get; }
        internal string? Password { get; }

        internal CallbackContext(string? password, CancellationToken token)
        {
            Password = password;
            Token = token;
        }

        internal int Invoke(Action action)
        {
            try
            {
                Token.ThrowIfCancellationRequested();
                if (_failure is not null) return unchecked((int)0x80004005);
                action();
                return 0;
            }
            catch (Exception ex)
            {
                Interlocked.CompareExchange(ref _failure, ExceptionDispatchInfo.Capture(ex), null);
                return ex is OperationCanceledException ? unchecked((int)0x80004004) : unchecked((int)0x80004005);
            }
        }

        internal void Check(int result, string operation)
        {
            _failure?.Throw();
            Token.ThrowIfCancellationRequested();
            if (result != 0)
                throw new InvalidDataException($"7-Zip {operation}失败，压缩包可能损坏或密码不正确 (0x{result:X8})。");
        }

        internal int GetPassword(out string password)
        {
            password = Password ?? string.Empty;
            return Invoke(() =>
            {
                if (Password is null) throw new InvalidDataException("此 7z 压缩包需要解压密码。");
            });
        }
    }

    [SupportedOSPlatform("windows")]
    private sealed class NativeArchive : IDisposable
    {
        private IntPtr _module;
        private IInArchive? _archive;
        private InputStream? _input;
        private readonly CallbackContext _context;
        internal IReadOnlyList<NativeEntry> Entries { get; private set; } = Array.Empty<NativeEntry>();

        internal NativeArchive(string path, string? password, CancellationToken ct)
        {
            _context = new CallbackContext(password, ct);
            ct.ThrowIfCancellationRequested();
            try
            {
                // 只加载插件私有绝对路径；依赖仅允许同目录和 System32，不搜索工作目录/PATH。
                _module = LoadLibraryEx(LibraryPath(), IntPtr.Zero, 0x00000100 | 0x00000800);
                if (_module == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法加载插件的 7z.dll。");
                var address = GetProcAddress(_module, "CreateObject");
                if (address == IntPtr.Zero) throw new InvalidDataException("7z.dll 缺少 CreateObject 导出，插件包可能损坏。");
                var create = Marshal.GetDelegateForFunctionPointer<CreateObject>(address);
                var classId = SevenZipClass;
                var interfaceId = typeof(IInArchive).GUID;
                _context.Check(create(ref classId, ref interfaceId, out _archive), "初始化");
                _input = new InputStream(File.OpenRead(path), _context);
                var callback = new OpenCallback(_context);
                ulong maxSearchOffset = 0; // install 协议传来的 7z 必须从文件头开始，不扫描其它嵌入格式。
                _context.Check(_archive.Open(_input, ref maxSearchOffset, callback), "读取压缩包");
                GC.KeepAlive(callback);
                _context.Check(_archive.GetNumberOfItems(out var count), "读取条目数");
                var entries = new List<NativeEntry>();
                for (uint i = 0; i < count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var name = ReadProperty(i, Property.Path) as string ?? string.Empty;
                    var directory = ReadProperty(i, Property.IsDirectory) is true;
                    var unsignedSize = ReadProperty(i, Property.Size) as ulong?;
                    if (unsignedSize is > long.MaxValue) throw new InvalidDataException("7z 条目长度超出支持范围。");
                    var attributes = ReadProperty(i, Property.Attributes) as uint?;
                    var posix = ReadProperty(i, Property.PosixAttributes) as uint? ?? 0;
                    var link = ReadProperty(i, Property.SymbolicLink) as string
                        ?? ReadProperty(i, Property.HardLink) as string;
                    if ((posix & 0xf000) == 0xa000) link ??= "symbolic link";
                    if (ReadProperty(i, Property.IsAnti) is true || ReadProperty(i, Property.IsAlternateStream) is true)
                        throw new InvalidDataException($"7z 包含不支持的删除/备用数据流条目：{name}");
                    entries.Add(new NativeEntry(name, directory, unsignedSize is { } value ? (long)value : null,
                        attributes is { } attr ? unchecked((int)attr) : null, link,
                        ReadProperty(i, Property.ModifiedTime) as DateTime?));
                }
                Entries = entries;
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        private object? ReadProperty(uint index, Property property)
        {
            var value = new PropVariant();
            try
            {
                _context.Check(_archive!.GetProperty(index, property, ref value), "读取条目属性");
                return (VarEnum)value.Type switch
                {
                    VarEnum.VT_EMPTY => null,
                    VarEnum.VT_BSTR => Marshal.PtrToStringBSTR(value.Pointer),
                    VarEnum.VT_BOOL => value.Boolean != 0,
                    VarEnum.VT_UI4 => value.UInt32,
                    VarEnum.VT_UI8 => value.Unsigned,
                    VarEnum.VT_FILETIME => SafeFileTime(value.Signed),
                    _ => throw new InvalidDataException($"7z 条目属性类型无效：{property}/{value.Type}"),
                };
            }
            finally { PropVariantClear(ref value); }
        }

        private static DateTime? SafeFileTime(long value)
        {
            try { return DateTime.FromFileTimeUtc(value); }
            catch (ArgumentOutOfRangeException) { return null; }
        }

        internal void Extract(UnpackService.ExtractionSession session)
        {
            using var callback = new ExtractCallback(Entries, session, _context);
            // 一次 Extract(all)：solid 块只解一次，绝不逐文件重新打开/从块头重新解码。
            var result = _archive!.Extract(IntPtr.Zero, uint.MaxValue, 0, callback);
            _context.Check(result, "解压");
            if (callback.FilesCompleted != Entries.Count(e => !e.IsDirectory))
                throw new InvalidDataException("7z 未能解出全部文件。");
        }

        public void Dispose()
        {
            var archive = _archive;
            _archive = null;
            try
            {
                if (archive is not null)
                {
                    try { archive.Close(); }
                    finally
                    {
                        // 必须先释放 RCW 的所有原生引用再 FreeLibrary，不能等 GC 去访问已卸载的 vtable。
                        Marshal.FinalReleaseComObject(archive);
                    }
                }
            }
            finally
            {
                try { _input?.Dispose(); }
                finally
                {
                    _input = null;
                    if (_module != IntPtr.Zero)
                    {
                        FreeLibrary(_module);
                        _module = IntPtr.Zero;
                    }
                }
            }
        }
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class OpenCallback : IArchiveOpenCallback, ICryptoGetTextPassword
    {
        private readonly CallbackContext _context;
        internal OpenCallback(CallbackContext context) => _context = context;
        public int SetTotal(IntPtr files, IntPtr bytes) => _context.Invoke(() => { });
        public int SetCompleted(IntPtr files, IntPtr bytes) => _context.Invoke(() => { });
        public int CryptoGetTextPassword(out string password) => _context.GetPassword(out password);
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class InputStream : IInStream, ISequentialInStream, IDisposable
    {
        private readonly FileStream _file;
        private readonly CallbackContext _context;
        private byte[]? _buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        internal InputStream(FileStream file, CallbackContext context) { _file = file; _context = context; }
        public int Read(IntPtr data, uint size, IntPtr processedSize)
        {
            if (processedSize != IntPtr.Zero) Marshal.WriteInt32(processedSize, 0);
            return _context.Invoke(() =>
            {
                var count = _file.Read(_buffer!, 0, (int)Math.Min(size, (uint)_buffer!.Length));
                _context.Token.ThrowIfCancellationRequested();
                Marshal.Copy(_buffer, 0, data, count);
                if (processedSize != IntPtr.Zero) Marshal.WriteInt32(processedSize, count);
            });
        }
        public int Seek(long offset, uint origin, IntPtr newPosition) => _context.Invoke(() =>
        {
            var position = _file.Seek(offset, (SeekOrigin)origin);
            if (newPosition != IntPtr.Zero) Marshal.WriteInt64(newPosition, position);
        });
        public void Dispose()
        {
            _file.Dispose();
            if (_buffer is { } buffer) { _buffer = null; ArrayPool<byte>.Shared.Return(buffer); }
        }
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class OutputStream : ISequentialOutStream, IDisposable
    {
        internal UnpackService.ExtractionSession.OutputStream File { get; }
        private readonly CallbackContext _context;
        private byte[]? _buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        internal OutputStream(UnpackService.ExtractionSession.OutputStream file, CallbackContext context)
        { File = file; _context = context; }
        public int Write(IntPtr data, uint size, IntPtr processedSize)
        {
            if (processedSize != IntPtr.Zero) Marshal.WriteInt32(processedSize, 0);
            return _context.Invoke(() =>
            {
                if (size > int.MaxValue) throw new InvalidDataException("7-Zip 输出块超出支持范围。");
                var written = 0;
                while (written < size)
                {
                    _context.Token.ThrowIfCancellationRequested();
                    var count = Math.Min((int)size - written, _buffer!.Length);
                    Marshal.Copy(IntPtr.Add(data, written), _buffer, 0, count);
                    File.Write(_buffer, 0, count);
                    written += count;
                    if (processedSize != IntPtr.Zero) Marshal.WriteInt32(processedSize, written);
                }
            });
        }
        public void Dispose()
        {
            File.Dispose();
            if (_buffer is { } buffer) { _buffer = null; ArrayPool<byte>.Shared.Return(buffer); }
        }
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class ExtractCallback : IArchiveExtractCallback, ICryptoGetTextPassword, IDisposable
    {
        private readonly IReadOnlyList<NativeEntry> _entries;
        private readonly UnpackService.ExtractionSession _session;
        private readonly CallbackContext _context;
        private OutputStream? _output;
        private NativeEntry? _current;
        internal int FilesCompleted { get; private set; }

        internal ExtractCallback(IReadOnlyList<NativeEntry> entries, UnpackService.ExtractionSession session,
            CallbackContext context) { _entries = entries; _session = session; _context = context; }

        // 7-Zip 的 SetCompleted 可能包含 solid 跳过的数据；这里只检查取消，字节进度完全由输出流统计。
        public int SetTotal(ulong total) => _context.Invoke(() => { });
        public int SetCompleted(IntPtr completed) => _context.Invoke(() => { });
        public int PrepareOperation(int askMode) => _context.Invoke(() => { });
        public int CryptoGetTextPassword(out string password) => _context.GetPassword(out password);

        public int GetStream(uint index, out ISequentialOutStream? stream, int askMode)
        {
            ISequentialOutStream? result = null;
            var status = _context.Invoke(() =>
            {
                if (_output is not null) throw new InvalidDataException("7-Zip 请求了重叠的文件输出流。");
                _current = null;
                if (askMode != 0) return;
                if (index >= _entries.Count) throw new InvalidDataException("7-Zip 返回了无效条目索引。");
                _current = _entries[(int)index];
                var file = _session.OpenEntry(_current.Name, _current.IsDirectory,
                    _current.Size, _current.LinkTarget, _current.Attributes);
                if (file is not null) result = _output = new OutputStream(file, _context);
            });
            stream = result;
            return status;
        }

        public int SetOperationResult(int result) => _context.Invoke(() =>
        {
            if (result != 0)
            {
                var reason = result switch
                {
                    1 => "不支持的压缩算法", 2 => "数据损坏或密码不正确", 3 => "CRC 校验失败或密码不正确",
                    4 => "数据不可用", 5 => "压缩数据意外结束", 6 => "压缩数据有多余内容",
                    7 => "不是有效压缩包", 8 => "压缩包头损坏", 9 => "密码不正确",
                    _ => $"未知解压错误 {result}",
                };
                throw new InvalidDataException($"7z 解压失败：{reason}（{_current?.Name}）。");
            }
            if (_output is null) return;
            _session.CompleteEntry(_output.File, _current?.ModifiedTime);
            _output.Dispose();
            _output = null;
            FilesCompleted++;
        });

        public void Dispose()
        {
            _output?.Dispose();
            _output = null;
        }
    }
}
