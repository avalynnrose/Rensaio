package extension.bridge

/**
 * Minimal stub used in the AndroidCompat build to satisfy Cloudflare config references.
 * Values mirror the defaults from the real server config, but can be overridden in tests.
 */
import android.webkit.WebView
import com.typesafe.config.ConfigException
import com.typesafe.config.ConfigRenderOptions
import com.typesafe.config.ConfigValue
import com.typesafe.config.parser.ConfigDocument
import java.util.Locale
import android.os.Looper
import eu.kanade.tachiyomi.App
import eu.kanade.tachiyomi.createAppModule
import eu.kanade.tachiyomi.network.NetworkHelper
import org.koin.core.context.startKoin
import org.koin.core.module.Module
import okhttp3.Cookie
import org.koin.dsl.module
import uy.kohesive.injekt.Injekt
import uy.kohesive.injekt.api.get
import extension.bridge.browser.BrowserHostGateway
import xyz.nulldev.androidcompat.AndroidCompat
import xyz.nulldev.androidcompat.AndroidCompatInitializer
import xyz.nulldev.androidcompat.androidCompatModule
import xyz.nulldev.ts.config.ApplicationRootDir
import xyz.nulldev.ts.config.BASE_LOGGER_NAME
import xyz.nulldev.ts.config.GlobalConfigManager
import xyz.nulldev.ts.config.configManagerModule
import xyz.nulldev.ts.config.initLoggerConfig
import xyz.nulldev.ts.config.setLogLevelFor
import xyz.nulldev.ts.config.updateFileAppender
import extension.bridge.settings.SettingsRegistry
import kotlin.concurrent.thread
import kotlin.io.path.Path
import kotlin.io.path.createDirectories
import kotlin.io.path.div
import kotlin.math.roundToInt
import xyz.nulldev.ts.config.CONFIG_PREFIX
import org.bouncycastle.jce.provider.BouncyCastleProvider
import android.app.Application
import android.content.Context
import com.typesafe.config.Config
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.flow.launchIn
import kotlinx.coroutines.flow.onEach
import kotlin.collections.associate
import kotlin.getValue
import uy.kohesive.injekt.injectLazy
import java.io.File
import java.lang.reflect.Modifier
import java.security.Security
import java.security.KeyStore
import java.security.SecureRandom
import java.security.cert.CertificateException
import java.security.cert.CertificateParsingException
import java.security.cert.X509Certificate
import kotlin.jvm.internal.FunctionBase
import kotlin.jvm.internal.FunctionReference
import kotlin.jvm.internal.Lambda
import kotlin.jvm.internal.MutablePropertyReference0
import kotlin.jvm.internal.MutablePropertyReference1
import kotlin.jvm.internal.MutablePropertyReference2
import kotlin.jvm.internal.PropertyReference0
import kotlin.jvm.internal.PropertyReference1
import kotlin.jvm.internal.PropertyReference2
import kotlin.jvm.internal.Reflection
import kotlin.jvm.internal.ReflectionFactory
import kotlin.reflect.KClass
import kotlin.reflect.KClassifier
import kotlin.reflect.KDeclarationContainer
import kotlin.reflect.KFunction
import kotlin.reflect.KMutableProperty0
import kotlin.reflect.KMutableProperty1
import kotlin.reflect.KMutableProperty2
import kotlin.reflect.KProperty0
import kotlin.reflect.KProperty1
import kotlin.reflect.KProperty2
import kotlin.reflect.KType
import kotlin.reflect.KTypeProjection
import kotlin.reflect.KTypeParameter
import kotlin.reflect.KVariance
import extension.bridge.logging.AndroidCompatLogBridge
import extension.bridge.logging.AndroidCompatJulHandler
import extension.bridge.logging.AndroidCompatLogSink
import extension.bridge.logging.AndroidCompatLogger
import extension.bridge.logging.androidCompatLogger
import extension.bridge.network.SystemProxyBridge
import extension.bridge.security.TrustManagerBridge
import android.webkit.CookieManager
import java.net.URL
import java.net.URLClassLoader
import android.util.Log;

val mutableConfigValueScope = CoroutineScope(SupervisorJob() + Dispatchers.Default)

private val application: Application by injectLazy()

object pathConfig {
    var dataRoot: String = ""
    var tempRoot: String = ""
}


// private val logger = androidCompatLogger(SettingsConfig::class.java)
class ApplicationDirs(
    val dataRoot: String = pathConfig.dataRoot,
    val tempRoot: String = pathConfig.tempRoot,
) {
    val extensionsRoot = "$dataRoot/extensions"
    val downloadsRoot =  "$dataRoot/downloads"
    val localMangaRoot = "$dataRoot/local" 
    val webUIRoot = "$dataRoot/webUI"
    val webUIServe = "$tempRoot/webUI-serve"
    val automatedBackupRoot = "$dataRoot/backups" 

    val tempThumbnailCacheRoot = "$tempRoot/thumbnails"
    val tempMangaCacheRoot = "$tempRoot/manga-cache"

    val thumbnailDownloadsRoot = "$downloadsRoot/thumbnails"
    val mangaDownloadsRoot = "$downloadsRoot/mangas"
}

@Suppress("DEPRECATION")
class LooperThread : Thread() {
    override fun run() {
       // logger.info { "Starting Android Main Loop" }
        Looper.prepareMainLooper()
        Looper.loop()
    }
}
val androidCompat by lazy { AndroidCompat() }

object AndroidCompatRuntime {
    @Volatile private var looperThread: LooperThread? = null
    @Volatile private var registeredSink: AndroidCompatLogSink? = null
    @Volatile private var defaultUncaughtHandler: Thread.UncaughtExceptionHandler? = null

    fun startMainLooperIfNeeded(): LooperThread {
        return synchronized(this) {
            val existing = looperThread
            if (existing != null) return existing
            val t = LooperThread()
            looperThread = t
            t.start()
            t
        }
    }

    fun stopMainLooper(timeoutMillis: Long = 5000L) {
        val t = synchronized(this) { looperThread }
        val looper = Looper.getMainLooper()
        // Request loop exit
        looper?.quitSafely()
        // Join with timeout
        if (t != null) {
            try {
                t.join(timeoutMillis)
            } catch (_: InterruptedException) {
                // ignore
            } finally {
                synchronized(this) { looperThread = null }
            }
        }
    }

    fun registerSink(sink: AndroidCompatLogSink) {
        registeredSink = sink
        AndroidCompatLogBridge.registerSink(sink)
    }

    fun unregisterSink() {
        registeredSink?.let {
            try {
                AndroidCompatLogBridge.clearSink()
            } catch (_: Throwable) {
                // ignore
            } finally {
                registeredSink = null
            }
        }
    }

    fun setDefaultUncaughtHandler(handler: Thread.UncaughtExceptionHandler) {
        defaultUncaughtHandler = handler
        Thread.setDefaultUncaughtExceptionHandler(handler)
    }

    fun restoreDefaultUncaughtHandler() {
        // Restore JVM default (null) or previous host-level handler
        Thread.setDefaultUncaughtExceptionHandler(null)
        defaultUncaughtHandler = null
    }
}
val jarLoaderMap = mutableMapOf<String, URLClassLoader>()
private val extensionLoaderLogger = androidCompatLogger(AndroidCompatRuntime::class.java)

/**
    * Loads the extension main class called [className] from the jar located at [jarPath].
    * Delegates to the single loader path in [Extensions] so every load shares the same
    * [jarLoaderMap]-managed classloader (no orphaned loaders; unload can close it).
    */
fun loadExtensionSources(
    jarPath: String,
    className: String,
): Any {
    return Extensions.loadExtension(jarPath, className)
}
fun unloadExtension(jarPath: String)
{
    val loader = synchronized(jarLoaderMap) { jarLoaderMap.remove(jarPath) }
    if (loader == null) {
        return
    }
    // Best-effort close: releases the jar file handle (Windows needs this before the
    // file can be moved/deleted) and allows the JVM to release loaded classes.
    try {
        loader.close()
    } catch (e: Throwable) {
        extensionLoaderLogger.warn(e) { "Error closing classloader for $jarPath" }
    }
    // Best-effort GC hint to expedite collection of now-unreferenced classes.
    System.gc()
}

/** Closes every registered extension classloader. Called once during host shutdown. */
fun unloadAllExtensions()
{
    val loaders = synchronized(jarLoaderMap) {
        jarLoaderMap.values.toList().also { jarLoaderMap.clear() }
    }
    for (loader in loaders) {
        try {
            loader.close()
        } catch (_: Throwable) {
            // best-effort
        }
    }
}

/** Test/diagnostic accessor: number of jar paths currently registered. */
fun extensionLoaderMapSize(): Int = synchronized(jarLoaderMap) { jarLoaderMap.size }

/** Test/diagnostic accessor: whether the given jar path has a registered classloader. */
fun isExtensionLoaded(jarPath: String): Boolean = synchronized(jarLoaderMap) { jarLoaderMap.containsKey(jarPath) }
private fun installJulBridge() {
    val logManager = java.util.logging.LogManager.getLogManager()
    val root = java.util.logging.Logger.getLogger("")
    logManager.reset()
    val handler = AndroidCompatJulHandler()
    root.addHandler(handler)
}

// NOTE: prewarmAwtEventThreads was removed with JCEF. It existed solely to pre-create the AWT/Swing
// event threads that JCEF's OSR path posted to. The .NET-hosted browser has no AWT involvement, so
// there is nothing to warm — and starting AWT machinery here was actively harmful (it forced
// Toolkit.getDefaultToolkit() and produced the HeadlessToolkit/Class.forName failures).

fun applicationSetup(dataRoot: String, tempRoot: String, sink: AndroidCompatLogSink)
{
    val logger = androidCompatLogger(SettingsConfig::class.java)
    // Register sink via manager
    AndroidCompatRuntime.registerSink(sink)
    installJulBridge()
    AndroidCompatRuntime.setDefaultUncaughtHandler(Thread.UncaughtExceptionHandler { _, throwable ->
        logger.error(throwable) { "unhandled exception" }
    })

    Thread.setDefaultUncaughtExceptionHandler { _, throwable ->
        logger.error(throwable) { "unhandled exception" }
    }

    //installSafeReflectionFactory()
    System.setProperty("$CONFIG_PREFIX.server.rootDir", dataRoot)
    System.setProperty("java.io.tmpdir", tempRoot)
    System.setProperty("jcef.dir","$dataRoot/jcef" )
    pathConfig.dataRoot = dataRoot
    pathConfig.tempRoot = tempRoot

    // Start controllable main looper
    AndroidCompatRuntime.startMainLooperIfNeeded()

   

    GlobalConfigManager.registerModule(
        SettingsConfig.register { GlobalConfigManager.config },
    )
    logger.info { "Running Extension-Bridge " }

    onSettingsMutated { settings ->
        logger.info {
            "Proxy changed - enabled=${settings.socksProxyEnabled} scheme=${settings.proxyScheme} " +
                "address=${settings.socksProxyHost}:${settings.socksProxyPort} , username=[REDACTED], password=[REDACTED]"
        }
        SystemProxyBridge.apply(settings.toProxySettings())
    }

    // ORDER MATTERS: install the SubjectKeyIdentifier-tolerant platform trust manager FIRST.
    // SystemProxyBridge.apply() composes the proxy CA on top of the platform manager; doing this
    // afterwards would leave the proxy composition reading a stale platform source.
    TrustManagerBridge.ensureSubjectKeyIdentifierTolerance()

    SystemProxyBridge.apply(Settings.toProxySettings())

    logger.debug {
        "Loaded config:\n" +
            GlobalConfigManager
                .getRedactedConfig(
                    SettingsRegistry
                        .getAll()
                        .filter { !it.value.privacySafe }
                        .keys
                        .toList(),
                ).root()
                .render(ConfigRenderOptions.concise().setFormatted(true))
    }

    logger.debug { "Data Root directory is set to: ${dataRoot}" }
    Locale.setDefault(Locale.ENGLISH)

    val app = App()
    startKoin {
        modules(
            createAppModule(app),
            androidCompatModule(),
            configManagerModule(),
        )
    }
    // NOTE: the CEF cookie-seeding InitBrowserHandler was removed with JCEF. Cookie seeding into
    // the browser now happens in-process: CookieManagerImpl.setCookie writes through to the .NET
    // host, and WebViewFetchInterceptor seeds the session from the JVM cookie store per fetch.


    
    // Load Android compatibility dependencies
    AndroidCompatInitializer().init()
    // start app
    androidCompat.startApp(app)




    Injekt
        .get<NetworkHelper>()
        .userAgentFlow
        .onEach { System.setProperty("http.agent", it) }
        .launchIn(mutableConfigValueScope)
    
    val non = CookieManager.getInstance()

    // NOTE: the CEF external message pump (CefAppBridge.setExternalPump + CefPumpBridge +
    // CefTimer/RpcCefTimer, driven from the Avalonia DispatcherTimer) was removed with JCEF.
    // It existed only to avoid reverse-JNI calls from CEF native threads into IKVM; the
    // .NET-hosted browser has no such threads, so the workaround is obsolete.

    // Boot-time master switch: when cefEnabled is false, JCEF is never initialized
    // (no ~512 MiB baseline, no native threads, no CPU overhead). WebView-requiring
    // sources degrade to the direct network chain / FlareSolverr exactly as they do
    // when JCEF init fails headlessly. Takes effect on restart.
    //
    // Skipped entirely when the .NET browser host is the selected backend: JCEF is then
    // unused, and initializing it here both wastes ~512 MiB and produces the AWT
    // Toolkit/Class.forName failure below (the host is installed right after
    // applicationSetup returns, because the C# service cannot warm up before
    // applicationSetup has registered the config modules).
    // Only skip when the host is ALREADY installed. Keying off the setting alone would be unsafe:
    // a WebView created before the host is installed (the provider factory is consulted per
    // WebView) would be left with an inert provider instead of a working JCEF fallback.
    val hostedBrowserInstalled = Settings.browserEnabled &&
        (Settings.browserProvider.lowercase() in setOf("browserhost", "patchright", "playwright")) &&
        BrowserHostGateway.isAvailable()

    if (!hostedBrowserInstalled) {
        // JCEF has been removed; there is no JVM-side browser to initialise. Without the host,
        // WebView-requiring sources degrade to the direct network chain / FlareSolverr.
        logger.warn {
            "The .NET browser host is not installed (browserEnabled=${Settings.browserEnabled}, " +
                "provider='${Settings.browserProvider}'); WebView sources will degrade to the direct network chain"
        }
    } else {
        logger.info {
            "Browser backend: .NET host ('${Settings.browserProvider}') — JCEF is not used"
        }
    }

    // AES/CBC/PKCS7Padding Cypher provider for zh.copymanga
    Security.addProvider(BouncyCastleProvider())

}
fun applicationShutdown(logger: AndroidCompatLogger) {
    // Stop Android main looper to prevent pending callbacks
    AndroidCompatRuntime.stopMainLooper(timeoutMillis = 5000L)
    // Close every extension classloader so jars are unlocked/releasable at exit.
    unloadAllExtensions()
    // Unregister sinks and handlers
    AndroidCompatRuntime.unregisterSink()
    AndroidCompatRuntime.restoreDefaultUncaughtHandler()
    logger.info { "Android compatibility runtime shut down." }
}

// NOTE: Cookie.toCefCookie() was removed with JCEF. The JVM cookie store is the source of truth
// and is synced into the .NET browser session via the browser gateway instead.

internal fun installSafeReflectionFactory() {
    runCatching {
        val field = Reflection::class.java.getDeclaredField("factory").apply { isAccessible = true }
        val modifiersField = java.lang.reflect.Field::class.java.getDeclaredField("modifiers").apply { isAccessible = true }
        modifiersField.setInt(field, field.modifiers and Modifier.FINAL.inv())
        val current = field.get(null) as? ReflectionFactory ?: return
        if (current is SafeReflectionFactory) return
        field.set(null, SafeReflectionFactory(current))
    }
}

private class SafeReflectionFactory(
    private val delegate: ReflectionFactory,
) : ReflectionFactory() {
    override fun createKotlinClass(clazz: Class<*>): KClass<*> =
        delegateMethod("createKotlinClass", clazz) as KClass<*>

    override fun createKotlinClass(clazz: Class<*>, internalName: String): KClass<*> =
        delegateMethod("createKotlinClass", clazz, internalName) as KClass<*>

    override fun getOrCreateKotlinPackage(jClass: Class<*>, moduleName: String): KDeclarationContainer =
        delegateMethod("getOrCreateKotlinPackage", jClass, moduleName) as KDeclarationContainer

    override fun getOrCreateKotlinClass(jClass: Class<*>): KClass<*> =
        delegateMethod("getOrCreateKotlinClass", jClass) as KClass<*>

    override fun getOrCreateKotlinClass(jClass: Class<*>, name: String): KClass<*> =
        delegateMethod("getOrCreateKotlinClass", jClass, name) as KClass<*>

    override fun renderLambdaToString(lambda: Lambda<*>): String =
        delegateMethod("renderLambdaToString", lambda) as String

    override fun renderLambdaToString(lambda: FunctionBase<*>): String =
        delegateMethod("renderLambdaToString", lambda) as String

    override fun function(function: FunctionReference): KFunction<*> =
        delegateMethod("function", function) as KFunction<*>

    override fun property0(property: PropertyReference0): KProperty0<*> =
        delegateMethod("property0", property) as KProperty0<*>

    override fun mutableProperty0(property: MutablePropertyReference0): KMutableProperty0<*> =
        delegateMethod("mutableProperty0", property) as KMutableProperty0<*>

    override fun property1(property: PropertyReference1): KProperty1<*, *> =
        delegateMethod("property1", property) as KProperty1<*, *>

    override fun mutableProperty1(property: MutablePropertyReference1): KMutableProperty1<*, *> =
        delegateMethod("mutableProperty1", property) as KMutableProperty1<*, *>

    override fun property2(property: PropertyReference2): KProperty2<*, *, *> =
        delegateMethod("property2", property) as KProperty2<*, *, *>

    override fun mutableProperty2(property: MutablePropertyReference2): KMutableProperty2<*, *, *> =
        delegateMethod("mutableProperty2", property) as KMutableProperty2<*, *, *>

    override fun typeOf(
        klass: KClassifier,
        arguments: List<KTypeProjection>,
        isMarkedNullable: Boolean,
    ): KType {
        return runCatching { delegateMethod("typeOf", klass, arguments, isMarkedNullable) as KType }
            .getOrElse { StubKType(klass, arguments, isMarkedNullable) }
    }

    override fun typeParameter(
        owner: Any,
        name: String,
        variance: KVariance,
        isReified: Boolean,
    ): KTypeParameter =
        delegateMethod("typeParameter", owner, name, variance, isReified) as KTypeParameter

    override fun setUpperBounds(typeParameter: KTypeParameter, bounds: List<KType>) {
        runCatching { delegateMethod("setUpperBounds", typeParameter, bounds) }
    }

    override fun mutableCollectionType(type: KType): KType =
        delegateMethod("mutableCollectionType", type) as KType

    private fun delegateMethod(name: String, vararg args: Any?): Any? {
        val translatedArgs = args.map(::normalizeArgument)
        val methods = delegate.javaClass.methods.filter { it.name == name && it.parameterCount == translatedArgs.size }
        val method = methods.firstOrNull { candidate ->
            candidate.parameterTypes.zip(translatedArgs).all { (param, arg) ->
                param.matchesArgument(arg)
            }
        } ?: throw NoSuchMethodException("Unable to delegate $name to ${delegate.javaClass.name}")
        method.isAccessible = true
        return if (translatedArgs.isEmpty()) {
            method.invoke(delegate)
        } else {
            method.invoke(delegate, *translatedArgs.toTypedArray())
        }
    }

    private fun normalizeArgument(arg: Any?): Any? {
        return when (arg) {
            is String -> kotlinToJavaNameAliases[arg] ?: arg
            is Array<*> -> arg.map(::normalizeArgument).toTypedArray()
            else -> arg
        }
    }

    private fun Class<*>.matchesArgument(arg: Any?): Boolean {
        if (arg == null) return true

        val candidates = resolveCandidateClasses(arg)

        if (candidates.any { candidate -> isAssignableFrom(candidate) }) {
            return true
        }

        if (isPrimitive) {
            val wrapper = primitiveWrappers[this]
            if (wrapper != null && candidates.any(wrapper::isAssignableFrom)) {
                return true
            }
        }

        return false
    }

    private fun resolveCandidateClasses(arg: Any?): List<Class<*>> {
        val discovered = LinkedHashSet<Class<*>>()
        val queue = ArrayDeque<Class<*>>()

        fun enqueue(type: Class<*>) {
            if (discovered.add(type)) {
                queue.add(type)
            }
        }

        fun enqueuePrimitiveCompanions(type: Class<*>) {
            if (type.isPrimitive) {
                primitiveWrappers[type]?.let(::enqueue)
            } else {
                wrapperToPrimitive[type]?.let(::enqueue)
            }
        }

        when (arg) {
            is Class<*> -> {
                enqueue(arg)
                enqueue(arg::class.java)
            }
            else -> enqueue(arg!!::class.java)
        }

        while (queue.isNotEmpty()) {
            val current = queue.removeFirst()
            enqueuePrimitiveCompanions(current)
            kotlinToJavaTypeAliases[current.name]?.forEach(::enqueue)
            current.superclass?.let(::enqueue)
            current.interfaces.forEach(::enqueue)
        }

        return discovered.toList()
    }

    companion object {
        private val primitiveWrappers = mapOf( // align primitive params with boxed args
            Boolean::class.javaPrimitiveType!! to java.lang.Boolean::class.java,
            Byte::class.javaPrimitiveType!! to java.lang.Byte::class.java,
            Char::class.javaPrimitiveType!! to java.lang.Character::class.java,
            Short::class.javaPrimitiveType!! to java.lang.Short::class.java,
            Int::class.javaPrimitiveType!! to java.lang.Integer::class.java,
            Long::class.javaPrimitiveType!! to java.lang.Long::class.java,
            Float::class.javaPrimitiveType!! to java.lang.Float::class.java,
            Double::class.javaPrimitiveType!! to java.lang.Double::class.java,
            Void.TYPE to java.lang.Void::class.java,
        )

        private val wrapperToPrimitive = primitiveWrappers.entries.associate { (primitive, wrapper) -> wrapper to primitive }

        private val kotlinToJavaTypeAliases: Map<String, Set<Class<*>>> = mapOf(
            "kotlin.String" to setOf(String::class.java),
            "kotlin.Char" to setOf(Char::class.javaPrimitiveType!!, java.lang.Character::class.java),
            "kotlin.Boolean" to setOf(Boolean::class.javaPrimitiveType!!, java.lang.Boolean::class.java),
            "kotlin.Byte" to setOf(Byte::class.javaPrimitiveType!!, java.lang.Byte::class.java),
            "kotlin.Short" to setOf(Short::class.javaPrimitiveType!!, java.lang.Short::class.java),
            "kotlin.Int" to setOf(Int::class.javaPrimitiveType!!, java.lang.Integer::class.java),
            "kotlin.Long" to setOf(Long::class.javaPrimitiveType!!, java.lang.Long::class.java),
            "kotlin.Float" to setOf(Float::class.javaPrimitiveType!!, java.lang.Float::class.java),
            "kotlin.Double" to setOf(Double::class.javaPrimitiveType!!, java.lang.Double::class.java),
            "kotlin.Unit" to setOf(Void.TYPE, java.lang.Void::class.java),
            "kotlin.Any" to setOf(Any::class.java),
            "kotlin.collections.List" to setOf(java.util.List::class.java),
            "kotlin.collections.MutableList" to setOf(java.util.List::class.java),
            "kotlin.collections.Set" to setOf(java.util.Set::class.java),
            "kotlin.collections.MutableSet" to setOf(java.util.Set::class.java),
            "kotlin.collections.Map" to setOf(java.util.Map::class.java),
            "kotlin.collections.MutableMap" to setOf(java.util.Map::class.java),
            "kotlin.collections.Collection" to setOf(java.util.Collection::class.java),
            "kotlin.collections.MutableCollection" to setOf(java.util.Collection::class.java),
            "kotlin.collections.Iterable" to setOf(java.lang.Iterable::class.java),
            "kotlin.collections.MutableIterable" to setOf(java.lang.Iterable::class.java),
            "kotlin.Array" to setOf(java.lang.reflect.Array::class.java)
        )

        private val kotlinToJavaNameAliases: Map<String, String> = kotlinToJavaTypeAliases
            .mapValues { (_, classes) -> classes.first().name }
            .plus(
                mapOf(
                    "kotlin.IntArray" to IntArray::class.java.name,
                    "kotlin.LongArray" to LongArray::class.java.name,
                    "kotlin.ShortArray" to ShortArray::class.java.name,
                    "kotlin.ByteArray" to ByteArray::class.java.name,
                    "kotlin.CharArray" to CharArray::class.java.name,
                    "kotlin.FloatArray" to FloatArray::class.java.name,
                    "kotlin.DoubleArray" to DoubleArray::class.java.name,
                    "kotlin.BooleanArray" to BooleanArray::class.java.name
                )
            )
    }
}

private class StubKType(
    override val classifier: KClassifier?,
    override val arguments: List<KTypeProjection>,
    override val isMarkedNullable: Boolean,
) : KType {
    override val annotations: List<Annotation> = emptyList()

    override fun toString(): String {
        val base = classifier?.toString() ?: "kotlin.Any"
        val rendered =
            if (arguments.isEmpty()) {
                base
            } else {
                "$base<${arguments.joinToString(", ") { it.type?.toString() ?: "*" }}>"
            }
        return if (isMarkedNullable) "$rendered?" else rendered
    }
}
