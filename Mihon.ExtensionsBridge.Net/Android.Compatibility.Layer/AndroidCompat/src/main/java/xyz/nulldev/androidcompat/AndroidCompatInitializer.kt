package xyz.nulldev.androidcompat

import android.webkit.WebView
import extension.bridge.Settings
import extension.bridge.browser.BrowserHostGateway
import extension.bridge.logging.androidCompatLogger
import xyz.nulldev.androidcompat.config.ApplicationInfoConfigModule
import xyz.nulldev.androidcompat.config.FilesConfigModule
import xyz.nulldev.androidcompat.config.SystemConfigModule
import xyz.nulldev.androidcompat.webkit.PlaywrightWebViewProvider
import xyz.nulldev.ts.config.GlobalConfigManager

/**
 * Initializes the Android compatibility module
 */
class AndroidCompatInitializer {
    private val logger = androidCompatLogger(AndroidCompatInitializer::class.java)

    fun init() {
        // Register config modules
        GlobalConfigManager.registerModules(
            FilesConfigModule.register(GlobalConfigManager.config),
            ApplicationInfoConfigModule.register(GlobalConfigManager.config),
            SystemConfigModule.register(GlobalConfigManager.config),
        )

        // The JVM no longer owns a browser: CEF/JCEF has been removed, and every WebView is
        // backed by the .NET browser host (Patchright/Playwright) through BrowserHostGateway.
        // If the host is absent the provider is inert and WebView-requiring sources degrade to
        // the direct network chain / FlareSolverr, exactly as they did when JCEF failed.
        WebView.setProviderFactory { view: WebView ->
            if (!BrowserHostGateway.isAvailable()) {
                logger.warn { "The .NET browser host is not installed; WebView sources will degrade to the direct network chain" }
            }
            PlaywrightWebViewProvider(view)
        }

        // Set some properties extensions use
        System.setProperty(
            "http.agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36",
        )
    }

    /**
     * Chooses the WebView backend for each `WebView` instance.
     *
     * `Settings.browserProvider`:
     *  - `browserhost` — always use the .NET-hosted browser (Patchright/Playwright).
     *  - `jcef`        — always use the embedded JCEF browser.
     *  - `auto`        — prefer the .NET host when it is installed and available, else JCEF.
     *
     * The provider factory is consulted per `WebView`, so this also self-heals: if the host is
     * not yet installed when the first WebView is created, JCEF is used, and once the host is
     * installed subsequent WebViews use it.
     */
    private fun useBrowserHost(): Boolean = when (Settings.browserProvider.lowercase()) {
        "browserhost", "playwright", "patchright" -> {
            if (BrowserHostGateway.isAvailable()) {
                true
            } else {
                logger.warn { "browserProvider=${Settings.browserProvider} requested but the .NET browser host is unavailable; falling back to JCEF" }
                false
            }
        }
        "jcef" -> false
        else -> BrowserHostGateway.isAvailable()
    }
}
