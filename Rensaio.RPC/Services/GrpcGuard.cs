using Grpc.Core;
using Rensaio.RPC.Contracts;
using Rensaio.RPC.Runtime;

namespace Rensaio.RPC.Services;

/// <summary>
/// Shared helpers for the gRPC services: token validation, readiness checks,
/// and mapping RPC exceptions onto gRPC status codes so the backend observes the
/// same failure categories it did before the process split.
///
/// The per-start token travels as gRPC metadata (header "rens-rpc-token") on
/// every call rather than in each message, so it is enforced uniformly.
/// </summary>
public static class GrpcGuard
{
    /// <summary>Metadata key carrying the per-start token.</summary>
    public const string TokenHeader = "rens-rpc-token";

    /// <summary>Reads the token from the call metadata (empty when absent).</summary>
    public static string ReadToken(ServerCallContext context)
    {
        foreach (var entry in context.RequestHeaders)
        {
            if (string.Equals(entry.Key, TokenHeader, StringComparison.OrdinalIgnoreCase))
                return entry.Value ?? string.Empty;
        }
        return string.Empty;
    }

    /// <summary>Throws <see cref="RpcException"/> when the token is invalid.</summary>
    public static void EnsureAuthorized(RpcStartupToken startup, ServerCallContext context)
    {
        if (!startup.IsValid(ReadToken(context)))
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Invalid or missing RPC token."));
    }

    /// <summary>Throws when the bridge has not finished initializing yet.</summary>
    public static void EnsureReady(RpcReadiness readiness)
    {
        if (readiness.Failure != null)
            throw new RpcException(new Status(StatusCode.FailedPrecondition, readiness.Failure.Message));

        if (!readiness.IsReady)
            throw new RpcException(new Status(StatusCode.Unavailable, "RPC bridge is still initializing."));
    }

    /// <summary>
    /// Maps a domain/transport exception onto a gRPC status. The original category, type name,
    /// HTTP status and message are embedded in the status detail via <see cref="RpcError"/> so
    /// the backend can re-materialize the same exception the in-process bridge would have thrown
    /// (gRPC otherwise replaces it with a bare <see cref="RpcException"/> and drops the cause).
    ///
    /// Callers still rely on the backend's own retry/backoff wrapper for transient cases, which
    /// inspects the reconstructed <c>HttpRequestException</c> server-side.
    /// </summary>
    public static RpcException Map(Exception ex) => ex switch
    {
        RpcException rpc => rpc,
        SourceNotFoundException notFound => new RpcException(new Status(StatusCode.NotFound, notFound.Message)),
        UnauthorizedRpcException => new RpcException(new Status(StatusCode.Unauthenticated, ex.Message)),
        HttpRequestException http => new RpcException(new Status(
            StatusCode.Unavailable,
            RpcError.Encode(
                $"Source HTTP failure ({(int?)http.StatusCode}): {http.Message}",
                new RpcErrorEnvelope
                {
                    Kind = RpcErrorKind.Http,
                    ExceptionType = http.GetType().FullName,
                    HttpStatus = (int?)http.StatusCode,
                    Message = http.Message,
                }))),
        OperationCanceledException => new RpcException(new Status(
            StatusCode.Cancelled,
            RpcError.Encode("Operation cancelled.", new RpcErrorEnvelope
            {
                Kind = RpcErrorKind.Canceled,
                ExceptionType = ex.GetType().FullName,
                Message = ex.Message,
            }))),
        _ => new RpcException(new Status(
            StatusCode.Internal,
            RpcError.Encode(ex.Message, new RpcErrorEnvelope
            {
                Kind = RpcErrorKind.Other,
                ExceptionType = ex.GetType().FullName,
                Message = ex.Message,
            }))),
    };
}
