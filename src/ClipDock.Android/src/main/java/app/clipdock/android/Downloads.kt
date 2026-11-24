package app.clipdock.android

import android.app.Application
import android.os.Handler
import android.os.Looper
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import com.chaquo.python.PyObject
import com.chaquo.python.Python
import com.chaquo.python.android.AndroidPlatform
import org.json.JSONObject
import java.io.File
import java.io.IOException
import java.net.URI
import java.util.concurrent.CancellationException
import java.util.concurrent.Executors
import java.util.concurrent.atomic.AtomicBoolean

class ClipDock : Application() {
    internal val downloads by lazy { Downloads(this) }
}

/** Chaquopy exposes this bound Java method to the shared Python engine. */
class TransferProgress(private val cancelled: AtomicBoolean, private val report: (Int) -> Unit) {
    private var previous = Int.MIN_VALUE
    fun update(percent: Int): Boolean {
        if (percent != previous) {
            previous = percent
            report(percent)
        }
        return !cancelled.get()
    }
}

internal class Downloads(private val app: Application) {
    private val worker = Executors.newSingleThreadExecutor()
    private val main = Handler(Looper.getMainLooper())
    private val library = MediaFiles(app)
    private val work = File(app.cacheDir, "transfer")
    private var cancellation: AtomicBoolean? = null
    var files by mutableStateOf(emptyList<SavedFile>())
        private set
    var status by mutableStateOf<Int?>(null)
        private set
    var percent by mutableStateOf<Int?>(null)
        private set
    var error by mutableStateOf<Int?>(null)
        private set

    init {
        worker.execute {
            work.deleteRecursively()
            runCatching { library.discardInterruptedCopies() }
        }
    }

    fun refresh() {
        worker.execute {
            try {
                val saved = library.saved()
                main.post { files = saved }
            } catch (_: Exception) {
                main.post { error = R.string.error_library }
            }
        }
    }

    fun paste(text: String) {
        if (cancellation != null) return
        val link = text.trim()
        val uri = runCatching { URI(link) }.getOrNull()
        if (uri?.scheme?.lowercase() !in setOf("http", "https") || uri?.host.isNullOrBlank() || uri?.rawUserInfo != null) {
            error = R.string.error_invalid
            return
        }
        val stop = AtomicBoolean(false)
        cancellation = stop
        error = null
        percent = null
        status = R.string.checking_link
        worker.execute {
            val failure = try {
                save(link, stop)
                null
            } catch (_: CancellationException) {
                null
            } catch (_: UnsupportedMedia) {
                R.string.error_format
            } catch (_: IOException) {
                R.string.error_storage
            } catch (_: Exception) {
                R.string.error_download
            } finally {
                work.deleteRecursively()
            }
            main.post {
                cancellation = null
                status = null
                percent = null
                error = if (stop.get()) null else failure
                refresh()
            }
        }
    }

    fun cancel() {
        cancellation?.set(true)
        status = R.string.cancelling
    }

    private fun save(link: String, stop: AtomicBoolean) {
        fun checkCancelled() { if (stop.get()) throw CancellationException() }
        if (!work.mkdirs() && !work.isDirectory) throw IOException("Cannot create work directory")
        if (!Python.isStarted()) Python.start(AndroidPlatform(app))
        val listener = TransferProgress(stop) { value ->
            main.post {
                if (cancellation === stop && !stop.get()) {
                    if (value >= 0) status = R.string.downloading
                    percent = value.takeIf { it >= 0 }
                }
            }
        }
        val callback = PyObject.fromJava(listener)["update"] ?: error("Missing progress callback")
        val result = Python.getInstance().getModule("media").callAttr("run", link, work.path, callback)
        checkCancelled()
        val media = JSONObject(result.toString())
        when (media.optString("error")) {
            "" -> Unit
            "format", "single" -> throw UnsupportedMedia()
            else -> throw IllegalArgumentException("Cannot fetch media")
        }
        val names = media.getJSONArray("files")
        val parts = (0 until names.length()).map { File(work, names.getString(it)) }
        main.post { if (!stop.get()) { status = R.string.saving_file; percent = null } }
        val file = finishMedia(parts, work, ::checkCancelled)
        checkCancelled()
        library.publish(file, media.getString("title"), ::checkCancelled)
    }
}
