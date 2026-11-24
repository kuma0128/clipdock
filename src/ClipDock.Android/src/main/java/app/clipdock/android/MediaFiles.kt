package app.clipdock.android

import android.content.ContentResolver
import android.content.ContentUris
import android.content.ContentValues
import android.content.Context
import android.media.MediaCodec
import android.media.MediaExtractor
import android.media.MediaFormat
import android.media.MediaMuxer
import android.net.Uri
import android.os.Bundle
import android.os.Environment
import android.provider.MediaStore
import android.provider.MediaStore.MediaColumns as Col
import android.webkit.MimeTypeMap
import java.io.File
import java.io.IOException
import java.nio.ByteBuffer

internal data class SavedFile(val uri: Uri, val name: String, val mime: String, val size: Long)
internal class UnsupportedMedia : Exception()

internal class MediaFiles(context: Context) {
    private val resolver = context.contentResolver
    private val owner = context.packageName
    private val collection = MediaStore.Downloads.getContentUri(MediaStore.VOLUME_EXTERNAL_PRIMARY)
    private val folder = Environment.DIRECTORY_DOWNLOADS + "/ClipDock/"

    private fun entries(pending: Boolean): List<SavedFile> {
        val args = Bundle().apply {
            putString(ContentResolver.QUERY_ARG_SQL_SELECTION,
                "${Col.RELATIVE_PATH} = ? AND ${Col.OWNER_PACKAGE_NAME} = ? AND ${Col.IS_PENDING} = ?")
            putStringArray(ContentResolver.QUERY_ARG_SQL_SELECTION_ARGS, arrayOf(folder, owner, if (pending) "1" else "0"))
            putString(ContentResolver.QUERY_ARG_SQL_SORT_ORDER, "${Col.DATE_ADDED} DESC, ${Col._ID} DESC")
            putInt(MediaStore.QUERY_ARG_MATCH_PENDING, MediaStore.MATCH_INCLUDE)
        }
        val columns = arrayOf(Col._ID, Col.DISPLAY_NAME, Col.MIME_TYPE, Col.SIZE)
        return resolver.query(collection, columns, args, null)?.use { cursor ->
            buildList {
                while (cursor.moveToNext()) {
                    add(SavedFile(ContentUris.withAppendedId(collection, cursor.getLong(0)),
                        cursor.getString(1), cursor.getString(2), cursor.getLong(3)))
                }
            }
        } ?: throw IOException("Downloads unavailable")
    }

    fun saved() = entries(false)
    fun discardInterruptedCopies() {
        for (entry in entries(true)) resolver.delete(entry.uri, null, null)
    }

    fun publish(file: File, title: String, checkCancelled: () -> Unit) {
        val extension = file.extension.lowercase()
        // Keep long multibyte titles and the extension within filesystem filename limits.
        val cleaned = title.replace(Regex("[\\p{Cntrl}/\\\\:]"), " ").trim(' ', '.').ifBlank { "ClipDock" }
        val name = cleaned.take(60).trimEnd { it.isHighSurrogate() }
        val values = ContentValues().apply {
            put(Col.DISPLAY_NAME, "$name.$extension")
            put(Col.MIME_TYPE, MimeTypeMap.getSingleton().getMimeTypeFromExtension(extension) ?: "application/octet-stream")
            put(Col.RELATIVE_PATH, folder)
            put(Col.IS_PENDING, 1)
        }
        val uri = resolver.insert(collection, values) ?: throw IOException("Cannot create download")
        var published = false
        try {
            val output = resolver.openOutputStream(uri, "w") ?: throw IOException("Cannot open download")
            output.use { dest ->
                file.inputStream().use { source ->
                    val buffer = ByteArray(128 * 1024)
                    while (true) {
                        checkCancelled()
                        val count = source.read(buffer)
                        if (count == -1) break
                        dest.write(buffer, 0, count)
                    }
                }
            }
            checkCancelled()
            values.clear()
            values.put(Col.IS_PENDING, 0)
            if (resolver.update(uri, values, null, null) != 1) throw IOException("Download disappeared")
            published = true
        } finally {
            if (!published) resolver.delete(uri, null, null)
        }
    }
}

private fun MediaFormat.mime() = getString(MediaFormat.KEY_MIME).orEmpty()

/** Validate a single file, or combine the engine's video-only MP4 and audio-only M4A. */
internal fun finishMedia(parts: List<File>, work: File, checkCancelled: () -> Unit): File {
    if (parts.size !in 1..2 || parts.any { !it.isFile || it.length() == 0L }) throw UnsupportedMedia()
    val readers = mutableListOf<MediaExtractor>()
    try {
        for (part in parts) {
            val reader = MediaExtractor()
            readers.add(reader)
            reader.setDataSource(part.path)
        }
        if (parts.size == 1) {
            val reader = readers.single()
            if ((0 until reader.trackCount).none {
                    val mime = reader.getTrackFormat(it).mime()
                    mime.startsWith("audio/") || mime.startsWith("video/")
                }) throw UnsupportedMedia()
            return parts.single()
        }

        val output = File(work, "merged.mp4")
        val muxer = MediaMuxer(output.path, MediaMuxer.OutputFormat.MUXER_OUTPUT_MPEG_4)
        try {
            val tracks = readers.mapIndexed { index, reader ->
                val type = if (index == 0) "video/" else "audio/"
                val track = (0 until reader.trackCount).firstOrNull { reader.getTrackFormat(it).mime().startsWith(type) }
                    ?: throw UnsupportedMedia()
                reader.selectTrack(track)
                val format = reader.getTrackFormat(track)
                if (reader.sampleTrackIndex < 0) throw UnsupportedMedia()
                if (type == "video/" && format.containsKey(MediaFormat.KEY_ROTATION)) {
                    muxer.setOrientationHint(format.getInteger(MediaFormat.KEY_ROTATION))
                }
                muxer.addTrack(format)
            }
            // An AAC priming packet may precede time zero. Move both tracks by the
            // same amount so the muxer gets nonnegative timestamps and keeps sync.
            val originUs = minOf(0L, readers.minOf { it.sampleTime })
            muxer.start()
            var buffer = ByteBuffer.allocateDirect(256 * 1024)
            val info = MediaCodec.BufferInfo()
            while (true) {
                checkCancelled()
                val index = readers.indices.filter { readers[it].sampleTrackIndex >= 0 }.minByOrNull { readers[it].sampleTime } ?: break
                val reader = readers[index]
                val size = reader.sampleSize
                if (size !in 0..(64L * 1024 * 1024) || reader.sampleFlags and MediaExtractor.SAMPLE_FLAG_ENCRYPTED != 0)
                    throw UnsupportedMedia()
                if (size > buffer.capacity()) buffer = ByteBuffer.allocateDirect(size.toInt())
                buffer.clear()
                val count = reader.readSampleData(buffer, 0)
                if (count != size.toInt()) throw UnsupportedMedia()
                val flags = if (reader.sampleFlags and MediaExtractor.SAMPLE_FLAG_SYNC != 0) MediaCodec.BUFFER_FLAG_KEY_FRAME else 0
                info.set(0, count, reader.sampleTime - originUs, flags)
                muxer.writeSampleData(tracks[index], buffer, info)
                reader.advance()
            }
            muxer.stop()
        } finally {
            muxer.release()
        }
        return output
    } catch (_: IllegalStateException) {
        throw UnsupportedMedia()
    } finally {
        readers.forEach { it.release() }
    }
}
