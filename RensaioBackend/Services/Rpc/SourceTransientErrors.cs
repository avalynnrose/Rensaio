namespace RensaioBackend.Services.Rpc;

/// <summary>
/// Classifies failures that cross the RPC boundary from the Android-compat WebView host.
///
/// Some of these are TRANSIENT: the WebView host raises
/// <c>java.lang.Exception("Timed out waiting for WebView")</c> while a challenge page is still
/// initialising (or a navigation is still settling), and the very same request frequently succeeds
/// on a subsequent attempt. Treating that timeout as terminal abandons the source for the whole
/// run (page fetch gives up, the source is skipped), so callers retry it with a bounded backoff
/// instead — exactly like a transport/5xx failure.
/// </summary>
public static class SourceTransientErrors
{
    /// <summary>Message raised by the Android-compat WebView wait when the payload never arrives.</summary>
    public const string WebViewTimeoutMessage = "Timed out waiting for WebView";

    /// <summary>
    /// True when the failure is the WebView wait timeout. Matched on the message (case-insensitive)
    /// so it is recognized regardless of how the failure was reconstructed on this side: the RPC
    /// host transports the bare <c>java.lang.Exception</c>, which <see cref="RpcExceptionUnwrapper"/>
    /// rebuilds as a <see cref="RemoteSourceException"/>, while an in-process call throws the IKVM
    /// <c>java.lang.Exception</c> directly.
    /// </summary>
    public static bool IsWebViewTimeout(Exception ex)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (current.Message.Contains(WebViewTimeoutMessage, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
