# ClipDock

Chromeで開いている動画を、WindowsアプリでMP4として保存する個人用ダウンローダーです。RealPlayer Downloadsのような「動画の上から保存する」操作を目指した独立したアプリで、RealPlayerとは関係ありません。

## はじめに

対応環境: **Windows 10 / 11、x64、Google Chrome**。

1. 配布ZIPをすべて展開し、`INSTALL.cmd` をダブルクリックします。管理者権限は不要です。
2. Chromeのアドレスバーに `chrome://extensions` を入力します。
3. 右上の「デベロッパーモード」をONにし、「パッケージ化されていない拡張機能を読み込む」をクリックします。
4. `%LOCALAPPDATA%\Programs\ClipDock\extension` を選びます。アプリの「拡張フォルダーを開く」からも場所を確認できます。
5. Chromeの拡張メニューでClipDockをピン留めし、すでに開いていた動画ページを再読み込みします。
6. アプリ左下が「エンジンの準備完了」でなければ、「エンジンを準備 / 更新」をクリックします。

アプリはデスクトップ／スタートメニューのClipDockから起動できます。.NETやPythonの別途インストールは不要です。エンジン未同梱版では初回にインターネット接続が必要です。

ポータブル利用も可能です。展開先の `ClipDock.exe` を起動して「Chrome連携を設定」を押し、同じ場所の `extension` フォルダーをChromeに読み込んでください。アプリを移動したら、Chrome連携の設定と拡張の読み込みをやり直します。

## 使い方

- **動画上のボタン:** 動画にマウスを乗せて「↓ MP4で保存」を押します。ClipDockが起動して待機リストに追加されます。
- **ページから保存:** 拡張アイコンを開き、「このページの動画を保存」を押します。yt-dlpがページを解析します。
- **検出した動画から保存:** 動画を一度再生し、拡張内のMP4・HLS・DASHの候補から「保存」を押します。広告や別解像度が候補に混ざることがあります。
- **右クリック:** 動画・リンクまたはページのコンテキストメニューから保存できます。失敗した場合は拡張アイコンに「!」が付き、アイコンにマウスを乗せると理由が表示されます。
- **URL貼り付け:** WindowsアプリにページURLや動画URLを貼り付け、画質を指定して追加できます。

初期保存先は `Downloads\ClipDock`。保存先の変更は以降に追加する動画へ適用されます。動画は1件ずつ順番に処理します。最高画質／2160p／1080p／720p／480pを選べます。指定解像度は上限で、元動画によってはそれ以下になります。

映像と音声が分かれている場合は結合し、必要に応じてMP4に変換します。進捗が100%になった後も結合・変換は続くため、**「保存完了」になるまで待ってください**。MP4はコンテナ形式で、元からMP4の場合は映像コーデックを保持することがあります。画質は解像度を優先して選び、同じ解像度ならH.264 / AACを優先します。そのため、H.264が1080pまでのサイトで2160pや最高画質を選ぶと、映像がVP9やAV1になることがあります。再生できない機器向けには1080p以下を選んでください。

キャンセル後の「再試行」は取得済みの断片からの再開を試みます。サイトや期限付きURLによっては最初からの取得や、Chromeからの再送信が必要です。ダウンロード中やエンジンの準備中はアプリを最小化してください。閉じる場合は中断確認が出ます。再起動後に中断された項目が勝手に再開されることはありません。

## ログインが必要な動画

拡張ポップアップの「この保存にページのログイン情報を使う」をONにすると、その保存操作に限り、ページと選んだ動画・フレームのURLに対応するCookieをアプリに渡します。初回はChromeのCookie権限ダイアログが表示されます。動画上のボタン・右クリックではCookieを送りません。

Cookieは待機中の受け渡しファイルではWindows DPAPIで暗号化し、実行時だけユーザープロファイル内の一時Cookieファイルに展開します。処理終了・失敗・キャンセル時に削除し、異常終了で残ったものは次回起動時に削除します。Cookieを履歴に保存しません。再試行にログイン情報が必要なら、Chromeから再送信してください。Partitioned Cookie、Authorizationヘッダー、複雑な認証フローには対応していません。

## 対応範囲

- 通常のHTTP(S)動画、yt-dlpの対応サイト、非DRMのHLS/DASHを対象とします。
- DRM保護された動画の解除・保存は行いません。サイト側の変更や制限により、すべての動画を取得できるわけではありません。
- `blob:` URLそのものは保存できません。「このページの動画を保存」で解析するか、検出したHLS/DASHを選んでください。
- Chrome内部ページ、Chrome Web Store、シークレットウィンドウには対応しません。
- ページの独自プレイヤー・閉じたShadow DOM・全画面表示では、動画上のボタンが出ない場合があります。拡張ポップアップを使ってください。
- 再生中のライブ配信は終了まで長時間動作する場合があります。ライブの録画専用アプリではありません。
- ダウンロードする権利のある動画に利用してください。

## 困ったとき

| 症状 | 対処 |
| --- | --- |
| 拡張が「接続設定が必要」と表示 | アプリの「Chrome連携を設定」→ 拡張を開き直す。アプリの移動後も再設定が必要 |
| 動画上にボタンがない | ページを再読み込み。拡張の「動画上に保存ボタン」を確認。拡張アイコンから保存も可能 |
| 検出一覧が空 | 動画を再生し「再検出」。上の「このページの動画を保存」も試す |
| 403・ログイン要求 | ページを再読み込みし、CookieオプションをONにして再送信 |
| 待機したまま | アプリでエンジンを準備。エンジンの取得中はキューが待機する |
| 変換中の表示が続く | CPUで再エンコードしている可能性あり。時間がかかる場合がある |
| yt-dlpの対応サイトで失敗 | アプリの「エンジンを準備 / 更新」を実行。サイト自体の制約で取得できないこともある |
| 再試行後に認証エラー | Cookieは処理後に消去されるため、拡張からログイン情報付きで再送信 |
| 途中ファイルが残っている | `.part`、断片、一時メディアは再開用。不要ならダウンロード停止後に保存先から手動削除 |

エラーの詳細は履歴内の説明にマウスを乗せると確認できます。エンジンの取得元やバージョン・SHA-256は `tools\versions.json` に記録されます。更新後は `%LOCALAPPDATA%\ClipDock\tools\versions.json` を確認してください。

## 保存データ・権限

- アプリ設定: `%LOCALAPPDATA%\ClipDock\settings.json`
- 暗号化した履歴: `%LOCALAPPDATA%\ClipDock\history.dat`（最大300件、Cookieを除くURL・タイトル・状態・保存先）
- 暗号化した受信キュー: `%LOCALAPPDATA%\ClipDock\inbox`
- 一時Cookie: `%LOCALAPPDATA%\ClipDock\sessions`（実行中のみ）
- 更新したエンジン: `%LOCALAPPDATA%\ClipDock\tools`
- Chrome連携: `HKCU\Software\Google\Chrome\NativeMessagingHosts\com.clipdock.downloader`

拡張の全HTTP(S)サイト権限は、動画上のボタンとメディア通信の検出に使います。検出一覧はChromeのセッションストレージにタブごと最大50件まで置き、ページ遷移・タブ終了で破棄します。Cookie権限はオプションです。シークレットモードは無効です。

外部の収集サーバーやテレメトリーは使いません。通信先は選んだ動画の配信元、yt-dlpが解析で必要とするサイト、およびエンジン取得時のGitHubです。受信ブリッジはNative Messagingを使い、localhostのHTTPポートは開きません。入力URLはHTTP(S)に限定し、プロセスは引数配列で起動します。NativeHostの発信元IDも固定して照合します。

アンインストールする場合は、Chromeから拡張を削除してアプリを閉じ、インストール先（`%LOCALAPPDATA%\Programs\ClipDock`）の `UNINSTALL.cmd` をダブルクリックしてください。保存動画とアプリ設定・履歴は残します。不要な場合は `%LOCALAPPDATA%\ClipDock` を別途削除できます。

## 開発

必要なもの: Windows、.NET 10 SDK、Node.js 22以降。外部NuGet/npmパッケージへの依存はありません。

```powershell
# ビルド + 自動チェック + 自己完結型パッケージ
.\scripts\build.ps1 -Zip

# yt-dlp / FFmpeg / ffprobe / Denoも同梱する
.\scripts\build.ps1 -BundleTools -Zip

# 個別チェック
dotnet build ClipDock.slnx -c Release
dotnet run --project tests/ClipDock.Checks -c Release
npm test
npm run check
```

生成先は `artifacts\ClipDock-win-x64` と `artifacts\ClipDock-win-x64.zip` です。GitHub Actionsでも同じチェックとエンジン未同梱パッケージの生成を行います。バイナリはソースリポジトリにコミットしません。コード署名やChrome Web Storeへの登録は行っていません。

構成: `ClipDock.App`（WPF UI・キュー）、`ClipDock.NativeHost`（Chromeとのプロトコル・起動）、`ClipDock.Core`（検証・コマンド生成・DPAPI・ツール取得）、`extension`（Manifest V3）。各ダウンロードに識別子を付け、同名動画の衝突を避けています。

自動チェックはURL／Cookie検証、引数の分離、進捗解析、出力先制約、Native Messagingの断片読み取り／サイズ制限、暗号化、拡張の検出／遷移／Cookie同意／通信を確認します。**実際のChrome操作・実サイトからの動画保存・UI表示は手動確認の対象です。** 手順は [docs/manual-checks.md](docs/manual-checks.md) を参照してください。

実装時に参照した公式資料: [Chrome Native Messaging](https://developer.chrome.com/docs/extensions/develop/concepts/native-messaging)、[webRequest](https://developer.chrome.com/docs/extensions/reference/api/webRequest)、[yt-dlp](https://github.com/yt-dlp/yt-dlp)、[yt-dlp EJS / Deno](https://github.com/yt-dlp/yt-dlp/wiki/EJS)。
