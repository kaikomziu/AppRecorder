# AppRecorder

指定したアプリの **画面と音だけ** を録画する Windows 用ソフトです。

- 他のウィンドウが上に重なっても、選んだウィンドウだけが映ります
- 録れる音はそのアプリが鳴らした音だけです（Discord の通話・通知音・ほかのアプリの音は入りません）
- MP4（H.264 + AAC）で保存します

## ダウンロード

[Releases](https://github.com/kaikomziu/AppRecorder/releases/latest) から `AppRecorder.exe` をダウンロードして、ダブルクリックで起動します。インストールは不要です。

## 動作環境

- Windows 10 バージョン 2004 以降 / Windows 11（x64）
- [ffmpeg](https://ffmpeg.org/)（エンコードに使います）
  - 見つからない場合は、初回の録画開始時に winget でインストールするか聞かれます
  - 自分で用意する場合は `ffmpeg.exe` を `AppRecorder.exe` と同じフォルダに置くか、PATH を通してください

## 使い方

1. 「録画するウィンドウ」で録画したいアプリを選ぶ（一覧が古いときは「更新」）
2. 必要ならオプションを変える
   - マウスカーソルも録画する / しない
   - そのアプリの音を録音する / しない
   - FPS（10〜60、初期値 30）
   - 保存先（初期値は `ビデオ\AppRecorder`）
3. 「● 録画開始」で録画を始め、「■ 停止」で MP4 に保存
   - 録画中に対象のウィンドウを閉じると、自動で停止して保存します

ファイル名は `プロセス名_日時.mp4` になります。

## しくみ

| 項目 | 使っている機能 |
| --- | --- |
| 映像 | Windows.Graphics.Capture（ウィンドウ単位のキャプチャ） |
| 音声 | WASAPI のプロセス単位ループバック（`VAD\Process_Loopback`）。対象プロセスとその子プロセスの音を録ります |
| 保存 | 映像は ffmpeg（libx264）にパイプで送り、停止時に音声（AAC）と合成して MP4 にします |

対象のアプリが無音の間は無音を挿入して、映像と音声の長さを合わせています。

## 注意

- ウィンドウを最小化している間は、最後に映っていたフレームが続きます
- 映像のサイズは録画開始時のウィンドウサイズで固定です。録画中に大きくすると、はみ出した部分は切れます
- ストアアプリ（UWP）の一部は、ウィンドウのプロセスと音を鳴らすプロセスが別なので、音が録れないことがあります
- 著作権で保護されたコンテンツの録画は、各サービスの規約と法律の範囲で行ってください

## ビルド

.NET 8 SDK が必要です。

```bash
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish
```

## ライセンス

[MIT License](LICENSE)
