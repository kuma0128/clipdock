# 配布手順（Windows・macOS）

## 公式サイトとダウンロード

公式サイトはCloudflare Pagesの `clipdock` プロジェクトへ配置します。R2の `clipdock-releases` バケットは非公開のまま使用し、Pages Functionsの `CLIPDOCK_RELEASES` バインディングから配布ファイルだけを読み出します。

公開するWindows用EXE、Mac用DMG（Appleシリコン / Intel）とそれぞれの `.sha256` をバケット直下にアップロードします。公開可能なファイル名は `functions/downloads/[[file]].js` で指定し、新しいバージョンではこの一覧も更新してください。

ホームページ（4言語）はSHA-256を `.sha256` へのリンクではなく、値そのものを `<code class="sha256" data-file="<ファイル名>">` に表示します。新しいバージョンでは4言語すべての値と `data-file` を公開用ファイルのSHA-256に更新します。`npm test` は、各ページが現行バージョンの3ファイル分の値を表示し、4言語で一致することを確認します。値が実際のファイルと一致するかは、公開後に公開URLから取得したファイルで照合してください。

アプリの自動アップデートは `website/updates/latest.json` を読みます。新しいバージョンでは `version` と、`win-x64`・`mac-arm64`・`mac-x64` それぞれの `url`・`size`（バイト数）・`sha256` を更新します。サイトを配置すると、起動中のClipDock（自動アップデートを含む版）が1日以内に新しいバージョンを取得して自動でインストールします。そのため、配布ファイルをR2へアップロードし、公開URLから取得したファイルのサイズとSHA-256が `latest.json` と一致することを確かめてから、サイトを配置してください。`npm test` は、`latest.json` のバージョンが `Directory.Build.props` と一致し、SHA-256がホームページの表示と一致することを確認します。アプリは公式サイト以外のURLや、`/downloads/ClipDock-<version>-` で始まらないファイルを受け付けません。

```powershell
npm ci
$env:CLOUDFLARE_ACCOUNT_ID = '<公開先のアカウントID>'
npm run deploy:site
```

Cloudflareへログイン済みの環境で実行します。サイトは `website/`、配布ルートは `/downloads/<ファイル名>` です。公開URLから取得したEXE・DMGのSHA-256と、公開用ファイルのSHA-256が一致することを確認してください。R2やPages Functionsの利用量はCloudflareダッシュボードで確認できます。

既定のビルドは公開CAのコード署名証明書を要求します。未署名で正式配布する場合は `-AllowUnsignedRelease` を明示します。署名に失敗しても未署名へ自動切り替えはしません。

## 必要なもの

- Windows、PowerShell 7、.NET 10 SDK、Node.js 22以降。
- [Inno Setup 7](https://jrsoftware.org/inno-setup/downloads) の `ISCC.exe`。7.1.1以上の7.xを使用します。

署名付きで配布する場合は、次も必要です。

- Windows SDKに含まれる [Microsoft SignTool](https://learn.microsoft.com/en-us/windows/win32/seccrypto/signtool)。
- 有効期限内の公開CA発行コード署名証明書と、SignToolから利用できる秘密鍵。証明書は `Cert:\CurrentUser\My` または `Cert:\LocalMachine\My` に登録します。自己署名証明書と独自の信頼ルートは使用できません。
- タイムスタンプ取得と証明書の失効確認に使うインターネット接続。

秘密鍵をリポジトリや配布物に含めないでください。現在のスクリプトはWindows証明書ストアから拇印で証明書を選択します。

## 未署名版の正式配布

リポジトリのルートでPowerShell 7から実行します。証明書とSignToolは不要です。

```powershell
.\scripts\build.ps1
.\scripts\build-installer.ps1 -AllowUnsignedRelease
```

出力先は `artifacts\installer\release\ClipDock-<version>-win-x64-unsigned-setup.exe` と、その `.sha256` ファイルです。インストーラーとアンインストーラーは未署名です。`-CertificateThumbprint` や `-AllowUnsignedDevelopment` とは併用できません。

配布ページには未署名であることを明記し、Windowsの警告と操作方法を案内します。ファイル名の `unsigned` を削除したり、署名済みと表示したりしないでください。

```powershell
$installerFile = '.\artifacts\installer\release\ClipDock-<version>-win-x64-unsigned-setup.exe'
Get-AuthenticodeSignature -LiteralPath $installerFile | Select-Object Status
Get-FileHash -LiteralPath $installerFile -Algorithm SHA256
```

署名状態が `NotSigned` であることと、SHA-256が `.sha256` ファイルと一致することを確認します。公開先から取得したEXEも照合します。SHA-256はファイルの一致を確認するもので、発行元を証明する署名ではありません。

## 署名付きの正式配布

リポジトリのルートでPowerShell 7から実行します。証明書の拇印とSignToolのパスは実際の値に置き換えてください。

```powershell
.\scripts\build.ps1

$installerOptions = @{
    CertificateThumbprint = '<証明書の40桁のSHA-1拇印>'
    SignToolPath = '<Windows SDKのsigntool.exeのフルパス>'
    CompilerPath = 'C:\Program Files\Inno Setup 7\ISCC.exe'
}
.\scripts\build-installer.ps1 @installerOptions
```

`build.ps1` はアプリのビルド、自動チェック、自己完結型パッケージの作成を行います。保存エンジンはアプリの初回設定で取得します。

`build-installer.ps1` はパッケージを作業フォルダーにコピーしてから署名・コンパイルします。元のパッケージは書き換えません。必要なファイルが欠けている場合は停止します。

| 引数 | 指定内容 |
| --- | --- |
| `-PackageDirectory` | 元のパッケージ。省略時は `artifacts\ClipDock-win-x64` |
| `-CompilerPath` | Inno Setup 7の `ISCC.exe`。省略時はPATH、Program Files、LocalAppDataの標準インストール先を検索 |
| `-SignToolPath` | `signtool.exe`。省略時はPATHを検索 |
| `-CertificateThumbprint` | 署名証明書のSHA-1拇印。署名付きビルドでは必須。ファイル署名のハッシュ方式はSHA-256 |
| `-CertificateStoreLocation` | `CurrentUser`（既定）または `LocalMachine` |
| `-TimestampUrl` | RFC 3161サービス。既定値は `http://timestamp.digicert.com` |

成功時の出力は `artifacts\installer\release\ClipDock-<version>-win-x64-setup.exe` と、その `.sha256` ファイルです。作業ファイルと署名設定は `artifacts\installer\work-<GUID>` に残ります。作業フォルダーは配布しません。

## 署名付きファイルの検証

署名対象は `ClipDock.exe`、`ClipDock.dll`、`ClipDock.Core.dll`、インストーラー、アンインストーラーです。SHA-256署名とRFC 3161のSHA-256タイムスタンプを付けます。

`sign-artifact.ps1` は次を確認し、不一致なら終了コード1で停止します。

- SignToolの `verify /pa /all /tw` が成功すること。
- Authenticodeの状態が `Valid` で、署名者の拇印が設定値と一致し、タイムスタンプがあること。
- コード署名用の証明書チェーンがオンライン失効確認を通り、Windowsの `LocalMachine\AuthRoot` ストアのルートに到達すること。

単体で再検証する場合は、ビルド時に生成された `signing.json` を指定します。次のパスは対象のビルドに置き換えてください。

```powershell
$signatureConfig = '.\artifacts\installer\work-<GUID>\signing.json'
$installerFile = '.\artifacts\installer\release\ClipDock-<version>-win-x64-setup.exe'
.\scripts\sign-artifact.ps1 -ConfigurationPath $signatureConfig -FilePath $installerFile -VerifyOnly
Get-FileHash -LiteralPath $installerFile -Algorithm SHA256
```

`-VerifyOnly` を外すと、指定ファイルへの署名・タイムスタンプ付与を行ってから検証します。設定ファイルの項目は `signToolPath`、`certificateThumbprint`、`certificateStoreLocation`、`timestampUrl`、`evidenceDirectory` です。秘密鍵は含みません。

署名付き配布では、公開先から取得したEXEも上記の方法で再検証します。

## 配布前の確認

署名の有無にかかわらず、標準ユーザーでの新規インストール、更新、URLからのMP4・MP3保存、アンインストールを確認します。公開先から取得したEXEのSHA-256が配布用ファイルと一致してから、[公式サイト](https://clipdock-d6z.pages.dev/)のダウンロードリンクを有効にします。

未署名版では、発行元を証明書で確認できず、WindowsのSmartScreenに警告が出る場合があります。利用者には次のように案内します。

1. 公式サイトから取得したファイルであることを確認します。
2. 実行する場合は、SmartScreenの「詳細情報」→「実行」を選びます。
3. 「実行」が表示されない場合は中止します。Smart App Controlや組織の設定によって実行できないことがあります。保護機能を無効にせず、端末の管理者に確認してください。

SmartScreenの操作は [Microsoftの案内](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/publish-first-app) を参照してください。

## ローカル開発用

証明書なしでインストール動作を確認する場合に限り、次を使用します。

```powershell
.\scripts\build.ps1
.\scripts\build-installer.ps1 -AllowUnsignedDevelopment
```

出力先は `artifacts\installer\development\ClipDock-<version>-win-x64-unsigned-dev-setup.exe` です。インストーラーとアンインストーラーは未署名です。`-CertificateThumbprint` や `-AllowUnsignedRelease` とは併用できません。このモードの成果物を正式リリースや公式サイトに掲載しないでください。

## macOS版

macOS版は `src/ClipDock.Mac`（Avalonia）を自己完結型でビルドし、アプリバンドルとディスクイメージにまとめます。ビルドにはmacOSと.NET 10 SDKが必要です。Windowsからは作成できません。

```bash
bash scripts/build-mac.sh arm64
bash scripts/build-mac.sh x64
bash scripts/test-mac.sh arm64
```

出力は `artifacts/mac/ClipDock-<version>-mac-<arch>-unsigned.dmg` と、その `.sha256` ファイルです。GitHub Actionsの `macos` ジョブも同じスクリプトを実行し、成果物 `ClipDock-mac` として両方のアーキテクチャを保存します。

`test-mac.sh` は次を確認し、不一致なら停止します。

- バンドルとディスクイメージ内のアプリの署名が壊れていないこと、バージョン、アイコン、同梱ライセンス、実行ファイルのアーキテクチャ。
- ディスクイメージのSHA-256と内容（アプリと「アプリケーション」へのリンク）。
- 実行中のMacと同じアーキテクチャの場合：実際の保存エンジンの取得・再利用と、合成した動画のMP4・MP3保存。
- 同じく：メイン画面とログイン画面を開き、ページの読み込みとURLの取得ができること。設定・履歴のアクセス権が現在のユーザーに限定されていること。

### 署名と公証

現在のスクリプトはアドホック署名（`codesign --sign -`）だけを行います。Appleシリコンで実行するために必要な最小限の署名で、発行元は証明しません。Developer IDによる署名とAppleの公証は行っていないため、ファイル名に `unsigned` を付けています。この表記を削除したり、署名済み・公証済みと表示したりしないでください。

未署名版の利用者には次のように案内します。

1. 公式の配布元から取得したファイルであることを確認します。SHA-256で配布物と一致するか確認できます。
2. ディスクイメージを開き、`ClipDock` を「アプリケーション」へドラッグして起動します。
3. macOSが開発元を確認できないという警告を表示したら、いったん閉じます。開く場合は「システム設定」→「プライバシーとセキュリティ」の「このまま開く」を選びます。
4. この項目が表示されない場合は中止します。Gatekeeperの無効化や、隔離属性を削除するコマンドは案内しません。

Developer IDで配布する場合は、Apple Developer Programの証明書でバンドル内のライブラリとアプリに署名し（Hardened Runtimeを有効化）、`notarytool` で公証を受けてからディスクイメージへ結果を添付します。その際はファイル名から `unsigned` を外します。

### 公開

言語切り替えを追加した1.0.4以降は、各言語でメイン画面・ログイン画面・進捗・エラーが表示され、再起動後も選択が保持されることを確認します。

ディスクイメージはmacOSのジョブでだけ作れます。リリースタグ（`v<version>`）をpushすると、macOSジョブが確認済みのDMGと `.sha256` を同じ名前のGitHubリリース（なければ下書きで作成）に添付します。Actionsの成果物は保存容量の上限があるため使いません。`gh release download v<version> --pattern "*.dmg*"` で取得し、`shasum -a 256 -c` または `Get-FileHash` で照合します。

公開する場合は、Windows版と同じ手順でR2バケットへディスクイメージと `.sha256` をアップロードしてからサイトを更新します。1.0.3・1.0.4のディスクイメージは `functions/downloads/[[file]].js` の公開ファイル名一覧に登録済みです。新しいバージョンではこの一覧も更新してください。公開前に、両アーキテクチャのDMGとチェックサムがR2にあることを確認します。GitHub Actionsの対象コミットと配布ファイルの版数を揃えてください。公開先から取得したファイルのSHA-256が配布用ファイルと一致することを確認してください。実機のMacで、新規インストール、URLからのMP4・MP3保存、ログイン保存を確認してから公開します。
