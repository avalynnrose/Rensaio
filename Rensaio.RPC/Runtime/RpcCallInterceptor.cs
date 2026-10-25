using System.Runtime.InteropServices;
using Grpc.Core;
using Grpc.Core.Interceptors;

namespace Rensaio.RPC.Runtime;

/// <summary>
/// EXPERIMENT (RENSAIO_RPC_DIAG=1): logs every inbound gRPC call with its method, the
/// managed thread id, the OS thread id, and the duration, to
/// <c>rpc-calls-<pid>.log</c> in the host base directory.
///
/// Purpose: when the process FailFasts in <c>WToolkit.eventLoop</c> -> <c>JavaVM.GetEnv</c>,
/// the last lines of this file show exactly which RPC call was being served (and on which
/// thread) immediately before the fault, pinpointing the triggering operation.
/// </summary>
public sealed class RpcCallInterceptor : Interceptor
{
    private static readonly object FileLock = new();
    private static readonly string LogPath =
        Path.Combine(AppContext.BaseDirectory, $"rpc-calls-{Environment.ProcessId}.log");

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    private static void Write(string line)
    {
        try
        {
            lock (FileLock)
            {
                File.AppendAllText(LogPath, $"[{DateTime.UtcNow:O}] {line}{Environment.NewLine}");
            }
        }
        catch
        {
            // Diagnostics must never throw.
        }
    }

    private static string ThreadTag()
    {
        int managed = Environment.CurrentManagedThreadId;
        string os = OperatingSystem.IsWindows() ? GetCurrentThreadId().ToString() : "n/a";
        return $"mgr={managed} os={os}";
    }

    private async Task TraceAsync(string method, Func<Task> invoke)
    {
        Write($"CALL START {method} {ThreadTag()}");
        long start = Environment.TickCount64;
        try
        {
            await invoke().ConfigureAwait(false);
            Write($"CALL END   {method} {ThreadTag()} ok ms={Environment.TickCount64 - start}");
        }
        catch (Exception ex)
        {
            Write($"CALL FAIL  {method} {ThreadTag()} {ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }

    private async Task<T> TraceAsync<T>(string method, Func<Task<T>> invoke)
    {
        Write($"CALL START {method} {ThreadTag()}");
        long start = Environment.TickCount64;
        try
        {
            T result = await invoke().ConfigureAwait(false);
            Write($"CALL END   {method} {ThreadTag()} ok ms={Environment.TickCount64 - start}");
            return result;
        }
        catch (Exception ex)
        {
            Write($"CALL FAIL  {method} {ThreadTag()} {ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }

    public override Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request, ServerCallContext context, UnaryServerMethod<TRequest, TResponse> continuation)
        => TraceAsync(context.Method, () => continuation(request, context));

    public override Task ServerStreamingServerHandler<TRequest, TResponse>(
        TRequest request, IServerStreamWriter<TResponse> responseStream, ServerCallContext context,
        ServerStreamingServerMethod<TRequest, TResponse> continuation)
        => TraceAsync(context.Method, () => continuation(request, responseStream, context));

    public override Task<TResponse> ClientStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream, ServerCallContext context,
        ClientStreamingServerMethod<TRequest, TResponse> continuation)
        => TraceAsync(context.Method, () => continuation(requestStream, context));

    public override Task DuplexStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream, IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context, DuplexStreamingServerMethod<TRequest, TResponse> continuation)
        => TraceAsync(context.Method, () => continuation(requestStream, responseStream, context));
}
