using System.Buffers.Binary;
using System.IO.Pipes;
using HybridMount.ViewModels;
using Microsoft.Win32;

namespace HybridMount.Tests;

/// <summary>直接链接生产服务，验证失败保留、真实 SQLite 队列和 IPC 边界。</summary>
public sealed class ContractTests
{
    private sealed class Store : IAppearancePreferenceStore
    {
        internal bool Fail;
        internal AppearancePreferences Value = AppearancePreferences.Default;
        public AppearancePreferences Read() => Fail ? throw new IOException("测试故障") : Value;
        public void Write(AppearancePreferences value) { if (Fail) throw new IOException("测试故障"); Value = value; }
    }
    /// <summary>存储故障不可推进正式快照，控件选择也必须恢复。</summary>
    [Fact]
    public void AppearanceFailurePreservesCommittedValue()
    {
        var store = new Store();
        var service = new AppearanceSettingsService(store);
        using var vm = new AppearanceSettingsViewModel(service);
        vm.ThemeSelection = "Dark";
        Assert.Equal(AppearanceTheme.Dark, service.Current.Theme);
        store.Fail = true;
        vm.ThemeSelection = "Light";
        Assert.Equal("Dark", vm.ThemeSelection);
        Assert.False(service.Reload());
        Assert.Equal(AppearanceTheme.Dark, service.Current.Theme);
        Assert.NotNull(service.ErrorStatus);
    }
    /// <summary>非法注册表类型按字段回退，不覆盖另一合法字段，读操作不回写。</summary>
    [Fact]
    public void RegistryIsolatedRoundtripAndInvalidTypes()
    {
        var path = @"Software\HybridMount.NET.Tests\" + Guid.NewGuid().ToString("N");
        try
        {
            var store = new AppearancePreferenceStoreService(path);
            Assert.Equal(AppearancePreferences.Default, store.Read());
            store.Write(new(AppearanceBackdrop.AcrylicThin, AppearanceTheme.Dark));
            Assert.Equal(new(AppearanceBackdrop.AcrylicThin, AppearanceTheme.Dark), store.Read());
            using var key = Registry.CurrentUser.OpenSubKey(path, true)!;
            Assert.Equal(RegistryValueKind.DWord, key.GetValueKind("AppearanceTheme"));
            key.SetValue("AppearanceBackdrop", "invalid", RegistryValueKind.String);
            Assert.Equal(new(AppearanceBackdrop.None, AppearanceTheme.Dark), store.Read());
            Assert.Equal("invalid", key.GetValue("AppearanceBackdrop"));
            Assert.Equal(@"Software\HybridMount.NET", ApplicationPaths.RegistryPath);
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(path, false); }
    }
    /// <summary>打开失败不能用默认值覆盖无法读取的正式配置。</summary>
    [Fact]
    public void UnreadableAppearanceCannotBeSaved()
    {
        var service = new AppearanceSettingsService(new Store { Fail = true });
        Assert.False(service.IsLoaded);
        Assert.False(service.TrySave(new(AppearanceBackdrop.MicaBase, AppearanceTheme.Light), out _));
    }
    /// <summary>退出排空已接纳日志，去重、清除屏障和重开数据库保持真实顺序。</summary>
    [Fact]
    public async Task LoggerPersistsDrainsDeduplicatesAndClears()
    {
        var directory = Path.Combine(Path.GetTempPath(), "HybridMount.Tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "diagnostics.db");
        try
        {
            await using (var logger = new LoggerService(path))
            {
                logger.EntryWritten += (_, _) => throw new InvalidOperationException("故障订阅者");
                logger.Queue("测试", "Information", "重复"); logger.Queue("测试", "Information", "重复");
                for (var index = 0; index < 120; index++) logger.Queue("测试", "Warning", $"条目 {index}");
                var entries = await logger.ReadAsync();
                Assert.Equal(100, entries.Count);
                Assert.Equal("条目 119", entries[0].Message);
                await logger.ClearAsync();
                Assert.Empty(await logger.ReadAsync());
                logger.Queue("测试", "Information", "重复");
                logger.Queue("测试", "Error", "退出前最后一条");
            }
            await using var reopened = new LoggerService(path);
            var saved = await reopened.ReadAsync();
            Assert.Equal(2, saved.Count);
            Assert.Equal("退出前最后一条", saved[0].Message);
            Assert.Equal("重复", saved[1].Message);
            Assert.Null(reopened.LastError);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    /// <summary>错误 JSON、相对路径和越界长度不能成为有效文件命令。</summary>
    [Theory]
    [InlineData(0u)][InlineData(1048577u)][InlineData(uint.MaxValue)]
    public async Task InvalidFrameIsRejectedBeforePayload(uint length)
    {
        var header = new byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(header, length);
        using var stream = new MemoryStream(header);
        await Assert.ThrowsAsync<InvalidDataException>(() => IpcProtocol.ReadAsync<IpcRequest>(stream));
    }
    /// <summary>截断帧和非法请求不能触及路径，Unicode 与反斜杠往返保真。</summary>
    [Fact]
    public async Task FramingAndRequestValidation()
    {
        var request = new IpcRequest(1, Guid.NewGuid().ToString(), "freeSpace", [@"D:\测试\文件.txt"]);
        using var stream = new MemoryStream();
        await IpcProtocol.WriteAsync(stream, request); stream.Position = 0;
        var read = await IpcProtocol.ReadAsync<IpcRequest>(stream);
        Assert.Equal(request.Paths, read.Paths); Assert.True(IpcProtocol.IsValid(read));
        Assert.False(IpcProtocol.IsValid(request with { Paths = ["relative.txt"] }));
        Assert.False(IpcProtocol.IsValid(request with { Operation = "delete" }));
        Assert.False(IpcProtocol.IsValid(request with { Version = 2 }));
        using var truncated = new MemoryStream(new byte[] { 10, 0, 0, 0, 123 });
        await Assert.ThrowsAsync<EndOfStreamException>(() => IpcProtocol.ReadAsync<IpcRequest>(truncated));
    }
    /// <summary>畸形客户端和故障订阅者不破坏管道，文件命令明确返回尚未实现。</summary>
    [Fact]
    public async Task PipeRecoversAndPlaceholderDoesNotModifyFiles()
    {
        var directory = Path.Combine(Path.GetTempPath(), "HybridMount.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "不改动.txt"); await File.WriteAllTextAsync(file, "original");
        try
        {
            await using var logger = new LoggerService(Path.Combine(directory, "log.db"));
            var name = "HybridMount.Tests." + Guid.NewGuid().ToString("N");
            await using var server = new IpcServerService(logger, name);
            server.RequestReceived += _ => throw new InvalidOperationException("测试订阅故障");
            server.Start();
            await using (var malformed = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
            { await malformed.ConnectAsync(3000); await malformed.WriteAsync(new byte[4]); }
            foreach (var operation in new[] { "freeSpace", "keepOnDevice" })
            {
                var response = await IpcClient.SendAsync(new(1, Guid.NewGuid().ToString(), operation, [file]), name);
                Assert.Equal("notImplemented", response.Status);
            }
            Assert.Equal("original", await File.ReadAllTextAsync(file));
            Assert.Equal(2, (await logger.ReadAsync()).Count(entry => entry.Source == "资源管理器"));
            await server.DisposeAsync();
            await server.DisposeAsync();
        }
        finally { Directory.Delete(directory, true); }
    }
    /// <summary>第二线程不能持有同一实例租约；释放必须由持有线程执行。</summary>
    [Fact]
    public void SingleInstanceLeaseRecoversAfterRelease()
    {
        var name = "Local\\HybridMount.Tests." + Guid.NewGuid().ToString("N");
        using (var first = new SingleInstanceService(name))
        {
            Assert.True(first.IsPrimary);
            var primary = true;
            var thread = new Thread(() => { using var second = new SingleInstanceService(name); primary = second.IsPrimary; });
            thread.Start(); thread.Join();
            Assert.False(primary);
        }
        using var next = new SingleInstanceService(name); Assert.True(next.IsPrimary);
    }
}
