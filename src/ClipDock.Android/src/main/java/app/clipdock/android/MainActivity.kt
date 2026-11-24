package app.clipdock.android

import android.content.ActivityNotFoundException
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Intent
import android.os.Bundle
import android.text.format.Formatter
import android.widget.Toast
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.*
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp

class MainActivity : ComponentActivity() {
    private val downloads get() = (application as ClipDock).downloads

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        setContent {
            val colors = if (isSystemInDarkTheme()) darkColorScheme(primary = Color(0xFFA7C8FF))
                else lightColorScheme(primary = Color(0xFF155BBA), background = Color(0xFFF5F7FA),
                    surface = Color.White, primaryContainer = Color(0xFFE3EEFF), onPrimaryContainer = Color(0xFF123862))
            MaterialTheme(colorScheme = colors) {
                DownloadScreen(downloads, ::paste, { launchFile(it, false) }, { launchFile(it, true) })
            }
        }
    }

    override fun onResume() {
        super.onResume()
        downloads.refresh()
    }

    private fun paste() {
        val clip = getSystemService(ClipboardManager::class.java).primaryClip
        val item = if (clip != null && clip.itemCount > 0) clip.getItemAt(0) else null
        downloads.paste((item?.text ?: item?.uri)?.toString().orEmpty())
    }

    private fun launchFile(file: SavedFile, share: Boolean) {
        val intent = if (share) {
            Intent(Intent.ACTION_SEND).setType(file.mime).putExtra(Intent.EXTRA_STREAM, file.uri).apply {
                clipData = ClipData.newRawUri(file.name, file.uri)
            }
        } else Intent(Intent.ACTION_VIEW).setDataAndType(file.uri, file.mime)
        intent.addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
        try {
            startActivity(if (share) Intent.createChooser(intent, null) else intent)
        } catch (_: ActivityNotFoundException) {
            Toast.makeText(this, "このファイルを開けるアプリが見つかりません。", Toast.LENGTH_LONG).show()
        }
    }
}

@Composable
private fun DownloadScreen(downloads: Downloads, paste: () -> Unit, open: (SavedFile) -> Unit, share: (SavedFile) -> Unit) {
    val colors = MaterialTheme.colorScheme
    val shape = RoundedCornerShape(20.dp)
    Surface(modifier = Modifier.fillMaxSize(), color = colors.background) {
        Box(Modifier.safeDrawingPadding(), contentAlignment = Alignment.TopCenter) {
            LazyColumn(modifier = Modifier.widthIn(max = 640.dp).fillMaxSize(),
                contentPadding = PaddingValues(horizontal = 22.dp, vertical = 26.dp),
                verticalArrangement = Arrangement.spacedBy(10.dp)) {
                item {
                    Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(12.dp),
                        modifier = Modifier.padding(bottom = 12.dp)) {
                        Image(painterResource(R.drawable.clipdock_logo), null, Modifier.size(42.dp))
                        Text("ClipDock", style = MaterialTheme.typography.headlineLarge, fontWeight = FontWeight.Bold)
                    }
                }
                item {
                    Surface(shape = shape, color = colors.surface) {
                        Column(Modifier.fillMaxWidth().padding(22.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
                            Text("リンクから保存", style = MaterialTheme.typography.headlineSmall, fontWeight = FontWeight.Bold)
                            Text("動画ページや音声・動画ファイルのリンクをコピーしてください。",
                                style = MaterialTheme.typography.bodyMedium, color = colors.onSurfaceVariant)
                            Button(onClick = paste, enabled = downloads.status == null,
                                modifier = Modifier.fillMaxWidth().heightIn(min = 50.dp), shape = RoundedCornerShape(12.dp)) {
                                Icon(painterResource(R.drawable.paste), null, Modifier.size(20.dp))
                                Spacer(Modifier.width(8.dp))
                                Text("貼り付けて保存", style = MaterialTheme.typography.titleMedium)
                            }
                            downloads.status?.let { status ->
                                Row(verticalAlignment = Alignment.CenterVertically) {
                                    Text(status, Modifier.weight(1f), style = MaterialTheme.typography.bodyMedium)
                                    TextButton(onClick = downloads::cancel) { Text("キャンセル") }
                                }
                                val percent = downloads.percent
                                if (percent == null) LinearProgressIndicator(Modifier.fillMaxWidth())
                                else LinearProgressIndicator(progress = { percent / 100f }, modifier = Modifier.fillMaxWidth())
                            }
                            downloads.error?.let { Text(it, color = colors.error, style = MaterialTheme.typography.bodyMedium) }
                        }
                    }
                }
                item {
                    Column(Modifier.padding(top = 18.dp, bottom = 4.dp)) {
                        Text("保存したファイル", style = MaterialTheme.typography.titleLarge, fontWeight = FontWeight.Bold)
                        Text("ダウンロード / ClipDock", color = colors.onSurfaceVariant, style = MaterialTheme.typography.bodyMedium)
                    }
                }
                if (downloads.files.isEmpty()) item {
                    Text("保存したファイルがここに表示されます。", Modifier.padding(vertical = 16.dp),
                        color = colors.onSurfaceVariant, style = MaterialTheme.typography.bodyMedium)
                }
                items(downloads.files, key = { it.uri.toString() }) { file ->
                    Row(Modifier.fillMaxWidth().clip(RoundedCornerShape(14.dp)).background(colors.surface),
                        verticalAlignment = Alignment.CenterVertically) {
                        Row(Modifier.weight(1f).clickable { open(file) }.padding(14.dp),
                            verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                            Surface(shape = RoundedCornerShape(12.dp), color = colors.primaryContainer) {
                                Icon(painterResource(if (file.mime.startsWith("audio/")) R.drawable.audio else R.drawable.play),
                                    null, Modifier.size(42.dp).padding(10.dp), tint = colors.onPrimaryContainer)
                            }
                            Column {
                                Text(file.name, maxLines = 2, overflow = TextOverflow.Ellipsis,
                                    style = MaterialTheme.typography.titleMedium)
                                Text(Formatter.formatShortFileSize(LocalContext.current, file.size),
                                    style = MaterialTheme.typography.bodySmall, color = colors.onSurfaceVariant)
                            }
                        }
                        IconButton(onClick = { share(file) }) {
                            Icon(painterResource(R.drawable.share), "${file.name}を共有", tint = colors.primary)
                        }
                        Spacer(Modifier.width(4.dp))
                    }
                }
            }
        }
    }
}
