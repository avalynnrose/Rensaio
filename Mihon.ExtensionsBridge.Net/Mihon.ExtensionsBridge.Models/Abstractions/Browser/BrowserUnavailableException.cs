using System;

namespace Mihon.ExtensionsBridge.Models.Abstractions.Browser
{
    /// <summary>
    /// Thrown when the browser subsystem cannot serve a request (disabled, no provider available,
    /// or capacity exhausted). Callers degrade to the direct network chain / FlareSolverr, exactly
    /// as the old RendererGate.canSpawn() advisory check did.
    /// </summary>
    public sealed class BrowserUnavailableException : Exception
    {
        public BrowserUnavailableException(string message) : base(message)
        {
        }

        public BrowserUnavailableException(string message, Exception inner) : base(message, inner)
        {
        }
    }
}
