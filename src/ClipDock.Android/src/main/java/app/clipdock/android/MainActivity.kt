package app.clipdock.android

import android.content.ActivityNotFoundException
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Intent
import android.os.Bundle
import android.widget.Toast
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.*
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp

class MainActivity : ComponentActivity() {
    private val downloads get() = (application as ClipDock).downloads

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        setContent {
            val colors = if (isSystemInDarkTheme()) {
                darkColorScheme(primary = Color(0xFF409CFF), onPrimary = Color.Black,
                    surface = Color(0xFF1C1C1E), background = Color.Black, onSurfaceVariant = Color(0xFFAEAEB2))
            } else {
                lightColorScheme(primary = Color(0xFF007AFF), onPrimary = Color.White,
                    surface = Color.White, background = Color(0xFFF2F2F7), onSurface = Color(0xFF111114),
                    onSurfaceVariant = Color(0xFF636368), outlineVariant = Color(0xFFD5D5D9))
            }
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
    val sectionShape = RoundedCornerShape(24.dp)
    Surface(modifier = Modifier.fillMaxSize(), color = colors.background) {
        Box(Modifier.safeDrawingPadding(), contentAlignment = Alignment.TopCenter) {
            LazyColumn(modifier = Modifier.widthIn(max = 640.dp).fillMaxSize(),
                contentPadding = PaddingValues(start = 16.dp, end = 16.dp, top = 76.dp, bottom = 32.dp)) {
                item {
                    Text("ClipDock", fontSize = 34.sp, lineHeight = 41.sp, fontWeight = FontWeight.Bold,
                        letterSpacing = (-0.8).sp, modifier = Modifier.padding(bottom = 8.dp))
                }
                item {
                    Surface(shape = sectionShape, color = colors.surface) {
                        Column(Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 16.dp),
                            verticalArrangement = Arrangement.spacedBy(10.dp)) {
                            Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(16.dp)) {
                                Icon(painterResource(R.drawable.download), null, Modifier.size(24.dp), tint = colors.primary)
                                Text("リンクから保存", fontSize = 20.sp, fontWeight = FontWeight.Bold)
                            }
                            Text("動画や音声のリンクをコピーして、ペーストすると保存が始まります。",
                                fontSize = 15.sp, lineHeight = 20.sp, color = colors.onSurfaceVariant)
                            Button(onClick = paste, enabled = downloads.status == null,
                                shape = RoundedCornerShape(8.dp), contentPadding = PaddingValues(horizontal = 12.dp, vertical = 5.dp)) {
                                Icon(painterResource(R.drawable.paste), null, Modifier.size(18.dp))
                                Spacer(Modifier.width(5.dp))
                                Text("ペースト", fontSize = 17.sp)
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
                            downloads.error?.let { Text(it, color = colors.error, fontSize = 15.sp, lineHeight = 20.sp) }
                        }
                    }
                }
                item {
                    Text("保存したファイル", fontSize = 15.sp, fontWeight = FontWeight.SemiBold, color = colors.onSurfaceVariant,
                        modifier = Modifier.padding(start = 16.dp, top = 26.dp, bottom = 8.dp))
                }
                if (downloads.files.isEmpty()) item {
                    Surface(shape = sectionShape, color = colors.surface) {
                        Text("保存したファイルがここに表示されます。",
                            Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 14.dp),
                            fontSize = 15.sp, color = colors.onSurfaceVariant)
                    }
                }
                itemsIndexed(downloads.files, key = { _, file -> file.uri.toString() }) { index, file ->
                    val top = if (index == 0) 24.dp else 0.dp
                    val bottom = if (index == downloads.files.lastIndex) 24.dp else 0.dp
                    Column(Modifier.clip(RoundedCornerShape(topStart = top, topEnd = top,
                        bottomStart = bottom, bottomEnd = bottom)).background(colors.surface)) {
                        Row(Modifier.fillMaxWidth().padding(start = 16.dp, end = 4.dp),
                            verticalAlignment = Alignment.CenterVertically) {
                            Row(Modifier.weight(1f).clickable { open(file) }.heightIn(min = 54.dp),
                                verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(16.dp)) {
                                Icon(painterResource(R.drawable.play), null, Modifier.size(23.dp), tint = colors.primary)
                                Text(file.name, maxLines = 2, overflow = TextOverflow.Ellipsis, fontSize = 17.sp,
                                    lineHeight = 22.sp, color = colors.primary)
                            }
                            IconButton(onClick = { share(file) }) {
                                Icon(painterResource(R.drawable.share), "${file.name}を共有",
                                    Modifier.size(22.dp), tint = colors.primary)
                            }
                        }
                        if (index < downloads.files.lastIndex) HorizontalDivider(Modifier.padding(start = 56.dp, end = 16.dp),
                            thickness = 0.5.dp, color = colors.outlineVariant)
                    }
                }
                item {
                    Text("「ファイル」アプリの「ダウンロード」→「ClipDock」からも開けます。",
                        fontSize = 13.sp, lineHeight = 17.sp, color = colors.onSurfaceVariant,
                        modifier = Modifier.padding(start = 16.dp, end = 16.dp, top = 6.dp))
                }
            }
        }
    }
}
