package eu.kanade.tachiyomi.network.interceptor

import eu.kanade.tachiyomi.network.HttpException
import eu.kanade.tachiyomi.network.NetworkHelper
import eu.kanade.tachiyomi.network.POST
import eu.kanade.tachiyomi.network.await
import extension.bridge.logging.androidCompatLogger
import kotlinx.coroutines.GlobalScope
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import kotlinx.serialization.decodeFromString
import kotlinx.serialization.json.Json
import okhttp3.Cookie
import okhttp3.FormBody
import okhttp3.HttpUrl
import okhttp3.Interceptor
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import okhttp3.Response
import okhttp3.ResponseBody.Companion.toResponseBody
import okio.Buffer
import extension.bridge.Settings
import uy.kohesive.injekt.injectLazy
import java.io.IOException
import kotlin.time.Duration.Companion.seconds
import kotlin.time.toJavaDuration

class CloudflareInterceptor(
    private val setUserAgent: (String) -> Unit,
) : Interceptor {
    private val logger = androidCompatLogger(CloudflareInterceptor::class.java)

    override fun intercept(chain: Interceptor.Chain): Response {
        val originalRequest = chain.request()

        logger.debug { "CloudflareInterceptor is being used." }

        // NEVER run challenge detection OR solving on the solver's OWN endpoint. The solver request
        // goes through this same OkHttp client, and the solver's JSON response embeds the challenge
        // URL it was asked to solve (e.g. "solution":{"url":".../@waf/challenge..."}). With a broad
        // body sniffer that response looked like a challenge, so the interceptor tried to SOLVE THE
        // SOLVER'S OWN REQUEST — recursing into CFClearance, whose non-reentrant mutex then deadlocked
        // and the process hung silently. Short-circuit here: the solver endpoint is never a target.
        if (isSolverRequest(originalRequest.url.toString())) {
            return chain.proceed(originalRequest)
        }

        val originalResponse = chain.proceed(originalRequest)

        // Check if Cloudflare anti-bot is on.
        //
        // Detection is intentionally broad. The legacy interstitial is 403/503 with a
        // `Server: cloudflare` header, but Cloudflare now also serves MANAGED challenges (observed
        // on kagane.to: `Cf-Mitigated: challenge`, body `<title>Just a moment...</title>` with
        // `_cf_chl_opt` / `cType: 'managed'`). Those can arrive with a different status and the
        // browser-host fetch path does not always surface the Server header, so `cf-mitigated` and
        // challenge body markers are treated as authoritative in addition to the status+Server pair.
        if (!isCloudflareChallenge(originalResponse)) {
            return originalResponse
        }

        if (!Settings.flareSolverrEnabled) {
            // Explicit INFO: the failure the caller will see is an IOException that surfaces as a
            // bare status, so name the real cause here where the URL is still known.
            logger.info {
                "Cloudflare challenge for ${originalRequest.url} but the FlareSolverr/trawl bypass " +
                    "is disabled (Settings.flareSolverrEnabled=false) — configure it in Settings."
            }
            throw IOException("Cloudflare bypass currently disabled")
        }

        // INFO (not debug): whether the solver is actually used is the single most useful signal
        // when a Cloudflare-protected source fails, and it was previously invisible at the default
        // log level. Challenge URL + solver URL make a misconfigured endpoint obvious.
        logger.info {
            "Cloudflare challenge detected for ${originalRequest.url} " +
                "(${originalResponse.code}); solving via ${Settings.flareSolverrUrl}..."
        }

        return try {
            originalResponse.close()
            // network.cookieStore.remove(originalRequest.url.toUri())

            val target = with(CFClearance) { originalRequest.toFlareSolverTarget() }
            val flareResponseFallback = Settings.flareSolverrAsResponseFallback && target.canUseResponseFallback

            // SOLVE + VERIFY + RETRY. The solver can report status:"ok" while having solved NOTHING —
            // e.g. comix.to's own interactive image CAPTCHA WAF returns the challenge page as a
            // "successful" solution (solution.url stays on the challenge route). Blindly trusting
            // "ok" fed the challenge HTML to the extension, which then failed with a confusing
            // downstream error. So: solve, retry the ORIGINAL request, and only accept the result if
            // the challenge is actually gone. Otherwise back off exponentially and retry, then fail
            // with an explicit message. The extension is NOT disabled.
            var delayMs = SolveRetryBaseDelayMs
            for (attempt in 1..MaxSolveAttempts) {
                val flareResponse =
                    runBlocking {
                        CFClearance.resolveWithFlareSolver(originalRequest, target, !flareResponseFallback)
                    }

                logger.info {
                    "Cloudflare solver responded (status=${flareResponse.status}, " +
                        "solution=${flareResponse.solution.status}, attempt=$attempt/$MaxSolveAttempts, " +
                        "solutionUrl=${flareResponse.solution.url})."
                }

                // Solver explicitly reported no challenge (already cleared / not a challenge). Keep
                // the historical response-fallback behaviour.
                if (flareResponse.message.contains("not detected", ignoreCase = true)) {
                    logger.info { "FlareSolverr failed to detect Cloudflare challenge" }
                    if (flareResponseFallback &&
                        flareResponse.solution.status in 200..299 &&
                        flareResponse.solution.response != null
                    ) {
                        val isImage = flareResponse.solution.response.contains(CHROME_IMAGE_TEMPLATE_REGEX)
                        if (!isImage) {
                            setUserAgent(flareResponse.solution.userAgent)
                            return originalResponse
                                .newBuilder()
                                .code(flareResponse.solution.status)
                                .body(flareResponse.solution.response.toResponseBody())
                                .build()
                        }
                    }
                }

                val retryRequest = CFClearance.requestWithFlareSolverr(flareResponse, setUserAgent, originalRequest)
                val retried = chain.proceed(retryRequest)

                // SOLVED only when the retried response is NOT a challenge AND the solver did not just
                // hand back the challenge page itself (solution.url still on a challenge route).
                if (!isCloudflareChallenge(retried) && !isChallengeUrl(flareResponse.solution.url)) {
                    return retried
                }

                retried.close()

                if (attempt >= MaxSolveAttempts) {
                    throw IOException(
                        "Challenge could not be solved after $MaxSolveAttempts attempt(s) for " +
                            "${originalRequest.url}: the site is still serving a challenge " +
                            "(solution.url=${flareResponse.solution.url}). This is likely an unsupported " +
                            "WAF (e.g. an interactive CAPTCHA) the solver cannot pass."
                    )
                }

                logger.info {
                    "Solver did not clear the challenge (attempt $attempt/$MaxSolveAttempts); " +
                        "retrying in ${delayMs}ms."
                }
                Thread.sleep(delayMs)
                delayMs *= 2
            }

            // Unreachable: the loop either returns or throws.
            throw IOException("Challenge solve loop exited unexpectedly.")
        } catch (e: Exception) {
            // Because OkHttp's enqueue only handles IOExceptions, wrap non-IO failures so the app
            // does not crash. Our own IOException (the explicit unsolved-challenge message) is
            // rethrown as-is so it is not double-wrapped and stays readable.
            if (e is IOException) throw e
            throw IOException(e)
        }
    }

    companion object {
        private val ERROR_CODES = listOf(403, 503)
        private val SERVER_CHECK = arrayOf("cloudflare-nginx", "cloudflare")
        val COOKIE_NAMES = listOf("cf_clearance")
        private val CHROME_IMAGE_TEMPLATE_REGEX = Regex("""<title>(.*?) \(\d+×\d+\)</title>""")

        /** Attempts to solve+verify before giving up (the extension is not disabled on failure). */
        private const val MaxSolveAttempts = 3

        /** First retry delay; doubles each attempt (exponential backoff). */
        private const val SolveRetryBaseDelayMs = 2_000L

        /**
         * True when a URL is (still) a challenge route, so a solver "solution" that merely lands here
         * is a FALSE success. Covers Cloudflare's `/cdn-cgi/challenge-platform` and comix.to's own
         * `/@waf/challenge` (an interactive image-rotation CAPTCHA no solver can pass).
         */
        internal fun isChallengeUrl(url: String?): Boolean {
            if (url.isNullOrEmpty()) return false
            return url.contains("/@waf/challenge", ignoreCase = true) ||
                url.contains("/cdn-cgi/challenge-platform", ignoreCase = true)
        }

        /**
         * Some solvers (notably TRAWL) reject a session id that is not limited to
         * `[A-Za-z0-9_-]` with HTTP 400 ("session ID must contain 1-128 letters, numbers,
         * underscores or hyphens"). The shipped default `extension.bridge` contains a dot, so
         * every solve 400s. Map any disallowed character to `_` at the request boundary (rather
         * than changing the stored default) so FlareSolverr-style and TRAWL-style solvers both
         * accept it. Stable per source string, so the session is still reused across calls.
         */
        internal fun sanitizeSessionName(name: String): String {
            val mapped = name.map { if (it.isLetterOrDigit() || it == '_' || it == '-') it else '_' }.joinToString("")
            return mapped.ifEmpty { "extension_bridge" }.take(128)
        }

        /**
         * Substrings that identify a challenge document body, including Cloudflare markers and
         * comix.to's own WAF page (`/@waf/challenge`, `/@waf/generate`).
         *
         * These fragments also appear inside the SOLVER's JSON response (it echoes the challenge
         * URL it solved), so this list must NEVER be consulted for a solver response — [bodyLooksLikeChallenge]
         * returns early via [isSolverJson], and [intercept] skips the solver endpoint entirely via
         * [isSolverRequest]. Without those guards this list caused self-recursion and a deadlock.
         */
        private val CHALLENGE_BODY_MARKERS = arrayOf(
            "_cf_chl_opt",
            "/cdn-cgi/challenge-platform/",
            "cf-chl-",
            "cf-turnstile",
            "challenges.cloudflare.com",
            "Just a moment...",
            "Attention Required!",
            // comix.to's own WAF challenge page (interactive image CAPTCHA).
            "/@waf/challenge",
            "@waf/generate",
        )

        /**
         * True when the URL is the configured solver endpoint (FlareSolverr/trawl). Detection and
         * solving must BOTH be skipped for it: the solver request is issued through this same OkHttp
         * client, and its JSON response can look like a challenge, which previously recursed.
         */
        internal fun isSolverRequest(url: String): Boolean {
            val solver = Settings.flareSolverrUrl.trim()
            if (solver.isEmpty()) return false
            val bare = solver.removeSuffix("/").removePrefix("http://").removePrefix("https://").substringBefore('/')
            return bare.isNotEmpty() && url.contains(bare, ignoreCase = true)
        }

        /**
         * The proxy URL to pass to the solver's `proxy` field, or null.
         *
         * When the configured proxy is an HTTP transparent proxy (trawl), the solving leg should
         * egress through the SAME proxy so it presents the same IP/fingerprint as the rest of the
         * chain. Only HTTP is passed: trawl must never be used as SOCKS, and a SOCKS proxy is
         * deliberately NOT forwarded (FlareSolverr's `proxy` field expects an HTTP/SOCKS URL in
         * `scheme://host:port` form, and forwarding it is out of scope here).
         *
         * Guarded by the enabled flag so an unconfigured solver is never sent a proxy.
         */
        internal fun solverProxyUrl(): String? {
            if (!Settings.socksProxyEnabled) return null
            if (!Settings.proxyScheme.equals("http", ignoreCase = true)) return null
            val host = Settings.socksProxyHost.trim()
            val port = Settings.socksProxyPort.trim()
            if (host.isEmpty() || port.isEmpty() || port == "0") return null
            return "http://$host:$port"
        }

        /**
         * True when the body is a solver JSON response (FlareSolverr/trawl `/v1` shape:
         * `{"status":..., "message":..., "solution":{...,"url":...,"response":...}}`). Such a body
         * must never be treated as a challenge document.
         */
        private fun isSolverJson(body: String): Boolean =
            body.contains("\"solution\"", ignoreCase = true) &&
                (body.contains("\"startTimestamp\"", ignoreCase = true) ||
                    body.contains("\"endTimestamp\"", ignoreCase = true))

        /** How many response bytes to sniff for challenge markers (16 KiB is ample). */
        private const val CHALLENGE_SNIFF_BYTES = 16L * 1024

        /**
         * True when the response is a Cloudflare challenge/interstitial, covering BOTH the legacy
         * 403/503 interstitial and modern managed/Turnstile challenges.
         *
         * Order of checks (cheapest/most authoritative first):
         *  1. `Cf-Mitigated: challenge` (any status) — Cloudflare sets this on managed challenges.
         *  2. `403/503` + a `Server` header naming Cloudflare (legacy interstitial).
         *  3. challenge body markers (`_cf_chl_opt`, `/cdn-cgi/challenge-platform/`, `cf-chl-`,
         *     `cf-turnstile`, `challenges.cloudflare.com`, "Just a moment...", "Attention Required!") —
         *     catches a managed challenge that arrived as 200, or one whose Server header was dropped
         *     by the browser fetch. `peekBody` buffers without consuming, so the real body is preserved.
         *
         * Static (companion) so it is unit-testable without constructing the interceptor.
         */
        internal fun isCloudflareChallenge(response: Response): Boolean {
            response.header("cf-mitigated")?.let { value ->
                if (value.contains("challenge", ignoreCase = true)) return true
            }

            val server = response.header("Server")
            if (response.code in ERROR_CODES &&
                server != null &&
                SERVER_CHECK.any { server.contains(it, ignoreCase = true) }
            ) {
                return true
            }

            return bodyLooksLikeChallenge(response)
        }

        private fun bodyLooksLikeChallenge(response: Response): Boolean {
            // peekBody buffers up to N bytes without consuming the response body. Guard against a
            // null/one-shot/streaming body so a peek failure can never crash the interceptor.
            val text = runCatching { response.peekBody(CHALLENGE_SNIFF_BYTES).string() }.getOrNull()
            if (text.isNullOrEmpty()) return false
            // NEVER classify a solver JSON response as a challenge: its `solution.response` often
            // contains the challenge HTML we asked it to solve, and matching markers inside it caused
            // self-recursion (see isSolverRequest / the marker comment).
            if (isSolverJson(text)) return false
            return CHALLENGE_BODY_MARKERS.any { text.contains(it, ignoreCase = true) }
        }
    }
}

/*
 * This class is ported from https://github.com/vvanglro/cf-clearance
 * The original code is licensed under Apache 2.0
*/
object CFClearance {
    private val logger = androidCompatLogger(CFClearance::class.java)
    private val network: NetworkHelper by injectLazy()
    private val client by lazy {
        val timeout = Settings.flareSolverrTimeout.seconds
        network.client
            .newBuilder()
            .callTimeout(timeout.plus(10.seconds).toJavaDuration())
            .readTimeout(timeout.plus(5.seconds).toJavaDuration())
            .build()
    }
    private val json: Json by injectLazy()
    private val jsonMediaType = "application/json".toMediaType()
    private val mutex = Mutex()

    internal data class FlareSolverTarget(
        val cmd: String,
        val url: String,
        val postData: String? = null,
        val canUseResponseFallback: Boolean = true,
    )

    internal fun Request.toFlareSolverTarget(): FlareSolverTarget {
        if (method == "GET") {
            return FlareSolverTarget("request.get", url.toString())
        }

        val requestBody = body
        if (method == "POST" && requestBody is FormBody) {
            val postData =
                Buffer()
                    .also { requestBody.writeTo(it) }
                    .readUtf8()
            return FlareSolverTarget("request.post", url.toString(), postData)
        }

        // FlareSolverr's request.post only supports application/x-www-form-urlencoded.
        // Solve at the origin for JSON and other request types, then retry the untouched
        // original request with the returned clearance cookies.
        val origin =
            url
                .newBuilder()
                .encodedPath("/")
                .encodedQuery(null)
                .fragment(null)
                .build()
                .toString()
        return FlareSolverTarget("request.get", origin, canUseResponseFallback = false)
    }

    @Serializable
    data class FlareSolverCookie(
        val name: String,
        val value: String,
    )

    @Serializable
    data class FlareSolverRequest(
        val cmd: String,
        val url: String,
        val maxTimeout: Int? = null,
        val session: String? = null,
        @SerialName("session_ttl_minutes")
        val sessionTtlMinutes: Int? = null,
        val cookies: List<FlareSolverCookie>? = null,
        val returnOnlyCookies: Boolean? = null,
        val proxy: String? = null,
        val postData: String? = null, // only used with cmd 'request.post'
    )

    @Serializable
    data class FlareSolverSolutionCookie(
        val name: String,
        val value: String,
        val domain: String,
        val path: String? = null,
        val expires: Double? = null,
        val size: Int? = null,
        val httpOnly: Boolean? = null,
        val secure: Boolean? = null,
        val session: Boolean? = null,
        val sameSite: String? = null,
    )

    @Serializable
    data class FlareSolverSolution(
        val url: String,
        val status: Int,
        val headers: Map<String, String>? = null,
        val response: String? = null,
        val cookies: List<FlareSolverSolutionCookie>,
        val userAgent: String,
    )

    @Serializable
    data class FlareSolverResponse(
        val solution: FlareSolverSolution,
        val status: String,
        val message: String,
        val startTimestamp: Long,
        val endTimestamp: Long,
        val version: String,
    )

    internal suspend fun resolveWithFlareSolver(
        originalRequest: Request,
        target: FlareSolverTarget,
        onlyCookies: Boolean,
    ): FlareSolverResponse {
        val timeout = Settings.flareSolverrTimeout.seconds
        // Solvers such as TRAWL reject a session id containing characters outside [A-Za-z0-9_-]
        // (the shipped default "extension.bridge" has a dot) with HTTP 400. Normalize it here so
        // the request is accepted; FlareSolverr is unaffected (it accepts either form).
        val sessionName = CloudflareInterceptor.sanitizeSessionName(Settings.flareSolverrSessionName)
        return with(json) {
            mutex.withLock {
                val response =
                    client
                        .newCall(
                            POST(
                                url = Settings.flareSolverrUrl.removeSuffix("/") + "/v1",
                                body =
                                    Json
                                        .encodeToString(
                                            FlareSolverRequest(
                                                target.cmd,
                                                target.url,
                                                session = sessionName,
                                                sessionTtlMinutes = Settings.flareSolverrSessionTtl,
                                                cookies =
                                                    network.cookieStore
                                                        .get(originalRequest.url)
                                                        .filter { it.name !in CloudflareInterceptor.COOKIE_NAMES }
                                                        .map { cookie ->
                                                            FlareSolverCookie(cookie.name, cookie.value)
                                                        },
                                                returnOnlyCookies = onlyCookies,
                                                maxTimeout = timeout.inWholeMilliseconds.toInt(),
                                                postData = target.postData,
                                                proxy = CloudflareInterceptor.solverProxyUrl(),
                                            ),
                                        ).toRequestBody(jsonMediaType),
                            ),
                        ).await()

                // DECISIVE TRACE (INFO): proves whether Rensaio actually reached the solver, and
                // exactly what the solver said — instead of an opaque "HTTP error 400". Without this,
                // a solve that never left the process is indistinguishable from a solver rejection.
                val code = response.code
                val bodyText = response.body?.string().orEmpty()
                logger.info {
                    "Cloudflare solver HTTP $code for ${target.url} via ${Settings.flareSolverrUrl} " +
                        "(session=$sessionName): ${bodyText.take(600)}"
                }

                if (!response.isSuccessful) {
                    // Preserve the HttpException contract (callers/catches expect it) — but the
                    // solver's error body is now logged above, so the cause is never hidden again.
                    throw HttpException(code)
                }

                json.decodeFromString<FlareSolverResponse>(bodyText)
            }
        }.also { response ->
            // The solver can return HTTP 200 with status != "ok"; surface that too.
            if (!response.status.equals("ok", ignoreCase = true)) {
                logger.info {
                    "Cloudflare solver returned status='${response.status}' " +
                        "message='${response.message}' (url=${Settings.flareSolverrUrl})."
                }
            }
        }
    }

    fun requestWithFlareSolverr(
        flareSolverResponse: FlareSolverResponse,
        setUserAgent: (String) -> Unit,
        originalRequest: Request,
    ): Request {
        if (flareSolverResponse.solution.status in 200..299) {
            setUserAgent(flareSolverResponse.solution.userAgent)
            val cookies =
                flareSolverResponse.solution.cookies
                    .map { cookie ->
                        Cookie
                            .Builder()
                            .name(cookie.name)
                            .value(cookie.value)
                            .domain(cookie.domain.removePrefix("."))
                            .also {
                                if (cookie.httpOnly != null && cookie.httpOnly) it.httpOnly()
                                if (cookie.secure != null && cookie.secure) it.secure()
                                if (!cookie.path.isNullOrEmpty()) it.path(cookie.path)
                                // We need to convert the expires time to milliseconds for the persistent cookie store
                                if (cookie.expires != null && cookie.expires > 0) it.expiresAt((cookie.expires * 1000).toLong())
                                if (!cookie.domain.startsWith('.')) {
                                    it.hostOnlyDomain(cookie.domain.removePrefix("."))
                                }  
                            }.build()
                    }.groupBy { it.domain }
                    .flatMap { (domain, cookies) ->
                        network.cookieStore.addAll(
                            HttpUrl
                                .Builder()
                                .scheme("http")
                                .host(domain.removePrefix("."))
                                .build(),
                            cookies,
                        )

                        cookies
                    }
            logger.debug { "New cookies\n${cookies.joinToString("; ")}" }
            val finalCookies =
                network.cookieStore.get(originalRequest.url).joinToString("; ", postfix = "; ") {
                    "${it.name}=${it.value}"
                }
            logger.debug { "Final cookies\n$finalCookies" }
            return originalRequest
                .newBuilder()
                .header("Cookie", finalCookies)
                .header("User-Agent", flareSolverResponse.solution.userAgent)
                .build()
        } else {
            logger.debug { "Cloudflare challenge failed to resolve" }
            throw CloudflareBypassException()
        }
    }

    private class CloudflareBypassException : Exception()
}
