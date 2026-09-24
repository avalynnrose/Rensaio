using System.Net;
using Grpc.Core;
using Rensaio.RPC.Contracts;

namespace RensaioBackend.Services.Rpc;

/// <summary>
/// Re-materializes the exception the pre-split in-process bridge would have thrown from the
/// <see cref="RpcException"/> that gRPC substitutes for it.
///
/// gRPC does not preserve CLR exception identity (an <see cref="RpcException"/> carries no
/// <c>InnerException</c>), so the RPC host embeds the original category/type/status/message in
/// the status detail (see <see cref="RpcError"/>). Without this unwrap, every transport-mapped
/// failure surfaces as <see cref="RpcException"/> and the original per-category logging — HTTP
/// status breakdown, cancellation wording — is lost.
/// </summary>
public static class RpcExceptionUnwrapper
{
    /// <summary>
    /// Returns the original exception when the supplied exception is an <see cref="RpcException"/>
    /// carrying an <see cref="RpcErrorEnvelope"/>; otherwise returns the exception unchanged.
    /// </summary>
    public static Exception Unwrap(Exception ex)
    {
        if (ex is not RpcException rpc)
            return ex;

        if (!RpcError.TryDecode(rpc.Status.Detail, out var humanMessage, out var envelope) || envelope == null)
            return ex;

        return envelope.Kind switch
        {
            RpcErrorKind.Http => new HttpRequestException(
                humanMessage,
                inner: null,
                envelope.HttpStatus is int status ? (HttpStatusCode)status : null),
            RpcErrorKind.Canceled => envelope.ExceptionType == typeof(TaskCanceledException).FullName
                ? new TaskCanceledException(humanMessage)
                : new OperationCanceledException(humanMessage),
            _ => new RemoteSourceException(envelope.ExceptionType, humanMessage),
        };
    }
}

/// <summary>
/// Fallback for a remote failure whose original type has no local counterpart. Its
/// <see cref="ToString"/> surfaces the original (remote) exception type, so a log never simply
/// reads "Grpc.Core.RpcException" when an underlying failure was actually transported.
/// </summary>
public sealed class RemoteSourceException : Exception
{
    public RemoteSourceException(string? remoteTypeName, string message) : base(message)
        => RemoteTypeName = remoteTypeName;

    /// <summary>Fully-qualified name of the original remote exception type.</summary>
    public string? RemoteTypeName { get; }

    public override string ToString()
        => string.IsNullOrWhiteSpace(RemoteTypeName)
            ? base.ToString()
            : $"{RemoteTypeName}: {Message}";
}
