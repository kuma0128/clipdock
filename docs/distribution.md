# Windowsインストーラーの配布

## 公式サイトとダウンロード

公式サイトはCloudflare Pagesの `clipdock` プロジェクトへ配置します。R2の `clipdock-releases` バケットは非公開のまま使用し、Pages Functionsの `CLIPDOCK_RELEASES` バインディングから配布ファイルだけを読み出します。

公開するEXEと `.exe.sha256` をバケット直下にアップロードします。公開可能なファイル名は `functions/downloads/[[file]].js` で指定し、新しいバージョンではこの一覧も更新してください。

```powershell
npm ci
$env:CLOUDFLARE_ACCOUNT_ID = '<公開先のアカウントID>'
npm run deploy:site
```

Cloudflareへログイン済みの環境で実行します。サイトは `website/`、配布ルートは `/downloads/<ファイル名>` です。公開URLから取得したEXEのSHA-256と、公開用ファイルのSHA-256が一致することを確認してください。R2やPages Functionsの利用量はCloudflareダッシュボードで確認できます。

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
.\scripts\build.ps1 -BundleTools
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
.\scripts\build.ps1 -BundleTools

$installerOptions = @{
    CertificateThumbprint = '<証明書の40桁のSHA-1拇印>'
    SignToolPath = '<Windows SDKのsigntool.exeのフルパス>'
    CompilerPath = 'C:\Program Files\Inno Setup 7\ISCC.exe'
}
.\scripts\build-installer.ps1 @installerOptions
```

`build.ps1` はアプリのビルド、自動チェック、自己完結型パッケージの作成を行います。保存エンジンを同梱しない場合は `-BundleTools` を外します。

`build-installer.ps1` はパッケージを作業フォルダーにコピーしてから署名・コンパイルします。元のパッケージは書き換えません。拡張とアプリの版数が一致しない場合や、必要なファイルが欠けている場合は停止します。

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

署名対象は `ClipDock.exe`、`ClipDock.dll`、`ClipDock.Core.dll`、`ClipDock.NativeHost.exe`、`ClipDock.NativeHost.dll`、インストーラー、アンインストーラーです。SHA-256署名とRFC 3161のSHA-256タイムスタンプを付けます。

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

署名の有無にかかわらず、標準ユーザーでの新規インストール、更新、Chrome連携、動画保存、アンインストールを確認します。公開先から取得したEXEのSHA-256が配布用ファイルと一致してから、[公式サイト](https://clipdock-d6z.pages.dev/)のダウンロードリンクを有効にします。

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
