namespace Rensaio.RPC.Contracts;

/// <summary>
/// gRPC message-size limits shared by both ends of the RPC boundary.
///
/// Plan/word/chapter payloads are exchanged as JSON strings (<see cref="RpcJson"/>) inside
/// the protobuf envelope, and real sources can produce catalogs far larger than the 4 MiB
/// default (the backend otherwise fails with <c>ResourceExhausted</c> /
/// "Received message exceeds the maximum configured message size").
///
/// Keeping the values here guarantees the backend client and the RPC host server agree on
/// the same ceiling, so neither rejects a payload the other is willing to send.
/// </summary>
public static class RpcGrpcSizes
{
    /// <summary>4 MiB — gRPC's default on both sides.</summary>
    public const int Default = 4 * 1024 * 1024;

    /// <summary>
    /// Maximum size (in bytes) of a single message accepted from the peer, and of a single
    /// message sent to the peer. Raised well above the default to accommodate large JSON
    /// catalogs and chapter lists.
    /// </summary>
    public const int MaxMessageSize = 256 * 1024 * 1024; // 256 MiB
}
