# Chromeウェブストア提出手順

初回アップロードは完了しています。ストアの拡張IDは `okehkphoehepefjddoiinohnhpjghefe` です。公開鍵から算出したIDも一致しています。ストア版拡張での接続確認、1.0.2への更新、審査への提出が残っています。

## 更新用ZIP（1.0.2）

```powershell
node scripts/package-extension.mjs
Add-Type -AssemblyName System.IO.Compression.FileSystem
$package = Join-Path $PWD 'artifacts\webstore\package'
$archive = Join-Path $PWD 'artifacts\webstore\ClipDock-webstore-1.0.2.zip'
[System.IO.Compression.ZipFile]::CreateFromDirectory($package, $archive)
```

ZIPの直下に `manifest.json` があることを確認してください。同名ZIPが既にある場合は、新しいファイル名を使います。掲載画像は [1280 × 800の画面例](webstore-assets/listing-1280x800.jpg)、アイコンは [128 × 128 PNG](../extension/icons/128.png) です。画面例にはサンプルデータを使用しています。

## 提出前に必要な作業

1. 上記のコマンドで公開鍵入りのパッケージを生成します。`artifacts/webstore/package` をChromeに読み込み、拡張IDが `okehkphoehepefjddoiinohnhpjghefe` と一致することを確認します。
2. ストアIDに対応したWindowsアプリ1.0.2をインストールし、「Chrome連携を設定」を実行します。ストアIDからの要求にYouTube除外が適用されることも確認します。
3. 下記の公開サンプル動画を使い、ストア版拡張との接続・保存を確認して、対応するWindowsアプリを配布します。
4. 1.0.2の更新用ZIPを、既存のアイテム `okehkphoehepefjddoiinohnhpjghefe` にアップロードします。新しいアイテムは作りません。
5. 掲載画像、[掲載情報](webstore-listing-ja.md)、[プライバシーポリシー](../PRIVACY.md)、権限とデータ利用の申告を管理画面に入力します。
6. 公開範囲を「公開」にして審査へ提出します。管理画面への入力と提出は手動で行います。アップロードだけでは公開されません。

## 審査用メモ

この拡張は、動画の保存処理をPC内のClipDock Windowsアプリへ送るためのものです。Windows 10 / 11の64bit環境とGoogle Chromeが必要です。拡張単独では動作せず、別途Windowsアプリのインストールが必要です。

アプリ配布先: https://github.com/kuma0128/clipdock/releases/latest

Windowsアプリ1.0.2以降のZIPを展開して `INSTALL.cmd` を実行し、ClipDockで「Chrome連携を設定」を押してください。管理者権限は不要です。ストアの拡張を導入済みなら、同梱のローカル拡張を追加で読み込む必要はありません。アプリの「エンジンを準備 / 更新」で保存エンジンを取得し、拡張を開き直すと「ClipDockに接続済み」と表示されます。

拡張のコードはすべてパッケージ内に含まれます。外部で配布するWindowsアプリと保存エンジンの役割は掲載文とプライバシーポリシーに記載しています。拡張経由のYouTube保存、DRMの解除、アクセス制限の解除は提供しません。

確認用動画は [MDNのflower.mp4](https://interactive-examples.mdn.mozilla.net/media/cc0-videos/flower.mp4) です。ログインは不要です。[公式リポジトリの動画](https://github.com/mdn/interactive-examples/blob/main/live-examples/media/cc0-videos/flower.mp4) は [CC0 1.0 Universal](https://github.com/mdn/interactive-examples/blob/main/LICENSE) で公開されています。

## 動作確認

1. 上記の公開サンプル動画をChromeで開き、再生します。拡張にMP4の候補が表示されることを確認します。
2. 動画上の「MP4で保存」、ポップアップの候補、右クリックメニューからそれぞれ保存します。ClipDockに項目が追加され、保存したMP4を再生できることを確認します。
3. ポップアップで画質を変えて保存します。アプリに指定した画質上限が渡ることを確認します。
4. 通常保存でCookie権限を要求しないことを確認します。HTTPSのテストページで「この保存にページのログイン情報を使う」をONにした場合だけ権限を要求し、1回の保存後は選択がOFFへ戻ることを確認します。テストには個人のログイン情報を使用しません。
5. 保存の完了・失敗・キャンセル後に `%LOCALAPPDATA%\ClipDock\sessions` の一時Cookieファイルが残らないことを確認します。
6. 別のページへ移動すると古い候補が消え、移動前の応答が後から到着しても新しい候補に混ざらないことを確認します。
7. YouTubeのページでは保存ボタンや検出候補を表示せず、ページ・リンク・動画URLのどの保存経路からも要求を受け付けないことを確認します。
8. アプリを終了してから保存操作を行い、アプリが起動して要求を受け取ることを確認します。アプリ未インストール時は、導入案内へ進めることを確認します。

## 公開時の確認

| 項目 | 完了条件 |
| --- | --- |
| 拡張ID | `okehkphoehepefjddoiinohnhpjghefe` とWindowsアプリの許可リストを照合済み |
| 配布アプリ | ストアIDとYouTube除外に対応した版を公開済み |
| 接続・保存 | ストア版拡張と配布アプリで保存まで確認済み |
| 画像 | 個人の閲覧履歴・ログイン情報を含まない、機能が分かる画像を用意済み |
| 確認用動画 | 審査用メモの公開URLから再生・保存できることを確認済み |
| プライバシー | 公開URLからポリシーを閲覧でき、フォームのデータ申告と一致 |
| 公開状態 | 審査への提出と公開完了を区別して確認 |
