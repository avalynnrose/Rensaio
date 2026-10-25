package eu.kanade.tachiyomi.network.interceptor

import okhttp3.FormBody
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.Protocol
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import okhttp3.Response
import okhttp3.ResponseBody.Companion.toResponseBody
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertNull
import kotlin.test.assertTrue

class CloudflareInterceptorTest {
    @Test
    fun `cf-mitigated challenge header is detected regardless of status`() {
        // Observed on kagane.to: 403 + Cf-Mitigated: challenge + "Just a moment..." body.
        val managed = response(
            code = 403,
            headers = mapOf("Server" to "cloudflare", "Cf-Mitigated" to "challenge"),
            body = "<html><head><title>Just a moment...</title></head></html>",
        )
        assertTrue(CloudflareInterceptor.isCloudflareChallenge(managed))

        // A managed challenge can also arrive as 200 — the header alone must trigger detection.
        val managed200 = response(
            code = 200,
            headers = mapOf("Cf-Mitigated" to "challenge"),
            body = "challenge markup",
        )
        assertTrue(CloudflareInterceptor.isCloudflareChallenge(managed200))
    }

    @Test
    fun `solver json response is never treated as a challenge`() {
        // The solver echoes the challenge URL in solution.url and may include challenge HTML in
        // solution.response. This must NOT trigger detection (it caused self-recursion + deadlock).
        val solverJson = response(
            code = 200,
            headers = mapOf("Content-Type" to "application/json"),
            body = """{"status":"ok","message":"","startTimestamp":1,"endTimestamp":2,""" +
                """"solution":{"url":"https://comix.to/@waf/challenge?return=%2Fbrowse","status":200,""" +
                """"response":"<title>Just a moment...</title>"}}""",
        )
        assertFalse(CloudflareInterceptor.isCloudflareChallenge(solverJson))
    }

    @Test
    fun `challenge urls are recognised (solver false-success guard)`() {
        // comix.to's own interactive WAF route and Cloudflare's platform route both count as
        // "still a challenge" — a solver that "solves" to these has NOT actually cleared anything.
        assertTrue(CloudflareInterceptor.isChallengeUrl("https://comix.to/@waf/challenge?return=%2Fbrowse"))
        assertTrue(CloudflareInterceptor.isChallengeUrl("https://x/cdn-cgi/challenge-platform/h/g/orchestrate"))
        assertFalse(CloudflareInterceptor.isChallengeUrl("https://comix.to/browse?page=1"))
        assertFalse(CloudflareInterceptor.isChallengeUrl(null))
        assertFalse(CloudflareInterceptor.isChallengeUrl(""))
    }

    @Test
    fun `comix waf challenge body is detected`() {
        val comixWaf = response(
            code = 200,
            headers = mapOf("Server" to "cloudflare"),
            body = "<html><head><title>Security check</title></head><body>" +
                "<script>fetch('/@waf/generate')</script></body></html>",
        )
        assertTrue(CloudflareInterceptor.isCloudflareChallenge(comixWaf))
    }

    @Test
    fun `legacy 403 with cloudflare server is detected`() {
        val legacy = response(
            code = 403,
            headers = mapOf("Server" to "cloudflare"),
            body = "<html>blocked</html>",
        )
        assertTrue(CloudflareInterceptor.isCloudflareChallenge(legacy))
    }

    @Test
    fun `managed challenge body is detected even without server header or 403`() {
        // 200 + no Cf-Mitigated + no Server — only body markers. Catches the browser-fetch path
        // where the response headers are sanitized.
        val bodyOnly = response(
            code = 200,
            headers = emptyMap(),
            body = "<script>window._cf_chl_opt = {cType: 'managed'};</script>" +
                "<script src=\"/cdn-cgi/challenge-platform/h/g/orchestrate/chl_page/v1\"></script>",
        )
        assertTrue(CloudflareInterceptor.isCloudflareChallenge(bodyOnly))
    }

    @Test
    fun `ordinary api json is not treated as a challenge`() {
        val json = response(
            code = 200,
            headers = mapOf("Content-Type" to "application/json", "Server" to "nginx"),
            body = """{"series":[{"id":1,"title":"One Piece"}]}""",
        )
        assertFalse(CloudflareInterceptor.isCloudflareChallenge(json))
    }

    @Test
    fun `empty body without challenge signals is not a challenge`() {
        assertFalse(CloudflareInterceptor.isCloudflareChallenge(response(204, emptyMap(), "")))
    }

    private fun response(code: Int, headers: Map<String, String>, body: String): Response {
        val builder = Response.Builder()
            .request(Request.Builder().url("https://kagane.to/").build())
            .protocol(Protocol.HTTP_1_1)
            .code(code)
            .message("msg")
        headers.forEach { (name, value) -> builder.header(name, value) }
        return builder.body(body.toResponseBody(null)).build()
    }
    @Test
    fun `form post is forwarded through request post`() {
        val request =
            Request
                .Builder()
                .url("https://example.com/search?page=2")
                .post(FormBody.Builder().add("query", "one piece").build())
                .build()

        val target = with(CFClearance) { request.toFlareSolverTarget() }

        assertEquals("request.post", target.cmd)
        assertEquals("https://example.com/search?page=2", target.url)
        assertEquals("query=one+piece", target.postData)
        assertEquals(true, target.canUseResponseFallback)
    }

    @Test
    fun `json post solves at origin and leaves body for okhttp retry`() {
        val jsonBody = """{"title":"one piece"}""".toRequestBody("application/json".toMediaType())
        val request =
            Request
                .Builder()
                .url("https://kagane.to/api/v2/search/series?page=0&size=35")
                .post(jsonBody)
                .build()

        val target = with(CFClearance) { request.toFlareSolverTarget() }

        assertEquals("request.get", target.cmd)
        assertEquals("https://kagane.to/", target.url)
        assertNull(target.postData)
        assertEquals(false, target.canUseResponseFallback)
        assertEquals(jsonBody, request.body)
        assertEquals("application/json; charset=utf-8", request.body?.contentType().toString())
    }

    @Test
    fun `get keeps its original target`() {
        val request = Request.Builder().url("https://example.com/path?q=value").build()

        val target = with(CFClearance) { request.toFlareSolverTarget() }

        assertEquals("request.get", target.cmd)
        assertEquals("https://example.com/path?q=value", target.url)
        assertNull(target.postData)
        assertEquals(true, target.canUseResponseFallback)
    }
}
