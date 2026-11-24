using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace WinDrop.App.Services;

/// <summary>
/// 单实例进程互斥与本地命名管道（Named Pipe）文件投递服务
/// 用于处理资源管理器右键菜单「通过 WinDrop 发送到手机」唤醒已运行托盘进程
/// </summary>
public sealed class SingleInstanceIpcService : IDisposable
{
    private const string MutexName = @"Local\WinDrop_SingleInstance_Mutex_v2";
    private const string PipeName = "WinDrop_SingleInstance_Pipe_v2";

    private Mutex? _mutex;
    private CancellationTokenSource? _cts;

    public bool IsPrimaryInstance { get; private set; }

    public event Action<IReadOnlyList<string>>? FilesReceivedFromSecondaryInstance;
    public event Action? ActivateMainWindowRequested;

    public bool TryAcquirePrimaryInstance()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        IsPrimaryInstance = createdNew;
        return createdNew;
    }

    public void StartPipeServer()
    {
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => PipeListenLoopAsync(_cts.Token));
    }

    private async Task PipeListenLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                await server.WaitForConnectionAsync(token);
                using var sr = new StreamReader(server, Encoding.UTF8);
                string payload = await sr.ReadToEndAsync(token);

                if (!string.IsNullOrWhiteSpace(payload))
                {
                    var msg = JsonSerializer.Deserialize<IpcPayload>(payload);
                    if (msg?.Files is { Count: > 0 })
                    {
                        FilesReceivedFromSecondaryInstance?.Invoke(msg.Files);
                    }
                    else
                    {
                        ActivateMainWindowRequested?.Invoke();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                await Task.Delay(300, CancellationToken.None);
            }
        }
    }

    public static async Task<bool> SendToPrimaryInstanceAsync(IEnumerable<string> filesToSend)
    {
        try
        {
            await using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await client.ConnectAsync(1500);

            var payload = new IpcPayload { Files = filesToSend.ToList() };
            byte[] json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
            await client.WriteAsync(json);
            await client.FlushAsync();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        if (IsPrimaryInstance && _mutex != null)
        {
            try { _mutex.ReleaseMutex(); } catch { }
        }
        _mutex?.Dispose();
    }

    private sealed class IpcPayload
    {
        public List<string> Files { get; set; } = new();
    }
}
