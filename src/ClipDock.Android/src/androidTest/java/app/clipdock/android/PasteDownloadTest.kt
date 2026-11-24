package app.clipdock.android

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Intent
import android.media.MediaExtractor
import android.media.MediaFormat
import android.os.SystemClock
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import androidx.test.uiautomator.By
import androidx.test.uiautomator.UiDevice
import androidx.test.uiautomator.Until
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.security.MessageDigest

@RunWith(AndroidJUnit4::class)
class PasteDownloadTest {
    @Test fun pasteAndSave() {
        val instrumentation = InstrumentationRegistry.getInstrumentation()
        val context = instrumentation.targetContext
        val device = UiDevice.getInstance(instrumentation)
        val library = MediaFiles(context)
        val origin = "http://127.0.0.1:18741"
        val activity = instrumentation.startActivitySync(
            Intent(context, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        )
        val pasteButton = By.text(context.getString(R.string.paste))
        fun paste(text: String) {
            assertNotNull("Paste control missing", device.wait(Until.findObject(pasteButton), 10000))
            instrumentation.runOnMainSync {
                activity.getSystemService(ClipboardManager::class.java).setPrimaryClip(ClipData.newPlainText("URL", text))
            }
            device.findObject(pasteButton).click()
        }
        fun save(path: String, name: String): SavedFile {
            val before = library.saved().map { it.uri }.toSet()
            paste("$origin/$path")
            val deadline = SystemClock.elapsedRealtime() + 65000
            while (SystemClock.elapsedRealtime() < deadline) {
                val file = library.saved().firstOrNull { it.uri !in before }
                if (file != null) {
                    assertTrue(file.name, file.name.startsWith(name))
                    assertTrue(device.wait(Until.hasObject(By.text(file.name)), 5000))
                    return file
                }
                val failure = device.findObject(By.text(context.getString(R.string.error_download)))
                if (failure != null) fail("Download of $path failed: ${failure.text}")
                SystemClock.sleep(250)
            }
            throw AssertionError("Timed out downloading $path")
        }
        paste("ClipDock")
        assertTrue(device.wait(Until.hasObject(By.text(context.getString(R.string.error_invalid))), 5000))
        val video = save("watch", "ClipDock Sample")
        val audio = save("tone.wav", "tone")
        for ((file, hashArg) in listOf(video to "videoHash", audio to "audioHash")) {
            val bytes = context.contentResolver.openInputStream(file.uri)!!.use { it.readBytes() }
            val digest = MessageDigest.getInstance("SHA-256").digest(bytes).joinToString("") { "%02x".format(it) }
            assertEquals(InstrumentationRegistry.getArguments().getString(hashArg), digest)
        }
        val combined = save("split.mpd", "split")
        val reader = MediaExtractor()
        try {
            reader.setDataSource(context, combined.uri, null)
            val types = (0 until reader.trackCount).map { reader.getTrackFormat(it).getString(MediaFormat.KEY_MIME)!! }
            assertTrue(types.any { it.startsWith("video/") })
            assertTrue(types.any { it.startsWith("audio/") })
        } finally { reader.release() }
        val ids = library.saved().map { it.uri }
        paste("$origin/empty")
        assertTrue(device.wait(Until.hasObject(By.text(context.getString(R.string.error_download))), 30000))
        assertEquals(ids, library.saved().map { it.uri })
    }
}
