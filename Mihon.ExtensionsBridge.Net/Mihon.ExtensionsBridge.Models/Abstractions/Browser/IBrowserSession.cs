using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Mihon.ExtensionsBridge.Models.Abstractions.Browser
{
    /// <summary>
    /// A live, isolated browser context. One session == one browser context and is expected to be
    /// pooled/reused by the orchestration service.
    /// </summary>
    public interface IBrowserSession : IAsyncDisposable
    {
        /// <summary>Executes one fetch/navigation on behalf of an extension request.</summary>
        Task<BrowserResponse> ExecuteAsync(BrowserRequest request, CancellationToken ct = default);

        /// <summary>
        /// Live-DOM tier: navigate the page to <paramref name="url"/>, returning at navigation
        /// COMMIT (not load).
        ///
        /// Commit is deliberate: the caller fires <c>onPageStarted</c> immediately after this
        /// returns, so a document-start script the extension evaluates from it is installed BEFORE
        /// the page's own deferred/module scripts run. The caller must then call
        /// <see cref="WaitForLoadAsync"/> before firing <c>onPageFinished</c>.
        /// </summary>
        Task NavigateAsync(string url, CancellationToken ct = default);

        /// <summary>
        /// Waits until the page started by the most recent <see cref="NavigateAsync"/> /
        /// <see cref="SetDocumentAsync"/> reaches its <c>load</c> event (i.e. <c>onPageFinished</c>
        /// time). Paired with the commit-returning navigate/setDocument to reproduce the real
        /// WebView <c>onPageStarted</c> → load → <c>onPageFinished</c> ordering.
        /// </summary>
        Task WaitForLoadAsync(CancellationToken ct = default);

        /// <summary>Live-DOM tier: evaluate a script in the current page.</summary>
        Task<string?> EvaluateAsync(string script, CancellationToken ct = default);

        /// <summary>
        /// Loads <paramref name="html"/> as the document for <paramref name="url"/> (the
        /// <c>loadDataWithBaseURL</c> path), establishing that URL as the origin. Returns at
        /// navigation COMMIT; the caller fires <c>onPageStarted</c>, then awaits
        /// <see cref="WaitForLoadAsync"/>.
        ///
        /// Implementations MUST deliver the markup as the RESULT OF A NAVIGATION rather than via
        /// <c>document.write</c>. `document.write` swaps the node tree out from under any script
        /// that already holds DOM references — React then fails with
        /// "Failed to execute 'removeChild' … not a child of this node" and never completes its
        /// data fetch — and it does not trigger context init scripts, so the <c>window[name]</c>
        /// JS interfaces vanish. A real navigation avoids both problems.
        /// </summary>
        Task SetDocumentAsync(string url, string html, CancellationToken ct = default);

        /// <summary>
        /// Registers a JS-callable binding. The page calls <c>window[name](payloadString)</c> and
        /// the handler runs in .NET (never the reverse: no foreign native thread calls into IKVM).
        ///
        /// Returns a task that completes only once the binding is actually installable in the
        /// browser (the driver has acknowledged the exposure). Callers MUST await it — and any
        /// navigate/setDocument that follows MUST also be gated on the bindings being registered,
        /// otherwise a page whose inline bootstrap calls <c>window[name]</c> during parse can race
        /// an unregistered binding and its callback is silently lost (the extension then times out
        /// with "Timed out waiting for WebView").
        /// </summary>
        Task AddBindingAsync(string name, Func<string, Task<object?>> handler);

        /// <summary>
        /// Installs a script that runs in every page/frame before page scripts. Used to expose the
        /// `window[name].method(...)` object shape on top of a binding.
        /// </summary>
        Task AddInitScriptAsync(string script, CancellationToken ct = default);

        Task<IReadOnlyList<BrowserCookie>> GetCookiesAsync(string url, CancellationToken ct = default);

        Task SetCookiesAsync(string url, IReadOnlyCollection<BrowserCookie> cookies, CancellationToken ct = default);

        Task<byte[]> ScreenshotAsync(CancellationToken ct = default);
    }
}
