using RensaioBackend.Services.Rpc;
using Xunit;

namespace RensaioBackend.Tests;

/// <summary>
/// The Android-compat WebView wait timeout ("Timed out waiting for WebView") must be treated as a
/// transient, retryable failure — it crosses the RPC boundary as a reconstructed
/// <see cref="RemoteSourceException"/> (remote <c>java.lang.Exception</c>), not an
/// <c>HttpRequestException</c>, so message-based classification is what catches it.
/// </summary>
public sealed class SourceTransientErrorsTests
{
    [Fact]
    public void RemoteSourceException_WithWebViewTimeoutMessage_IsTransient()
    {
        var ex = new RemoteSourceException("java.lang.Exception", "Timed out waiting for WebView");
        Assert.True(SourceTransientErrors.IsWebViewTimeout(ex));
    }

    [Fact]
    public void PlainException_CaseInsensitive_IsTransient()
    {
        Assert.True(SourceTransientErrors.IsWebViewTimeout(new Exception("timed out WAITING for webview")));
    }

    [Fact]
    public void NestedInnerException_IsDetected()
    {
        var ex = new Exception("wrapper", new RemoteSourceException("java.lang.Exception", "Timed out waiting for WebView"));
        Assert.True(SourceTransientErrors.IsWebViewTimeout(ex));
    }

    [Fact]
    public void UnrelatedFailures_AreNotTransient()
    {
        Assert.False(SourceTransientErrors.IsWebViewTimeout(new Exception("HTTP error 404")));
        Assert.False(SourceTransientErrors.IsWebViewTimeout(new InvalidOperationException("Source does not support latest updates.")));
    }
}
