# Mouse Battery Indicator

対応するワイヤレスマウスの残量を、Windows の通知領域に数字で表示します。

## 使い方

[最新版をダウンロード](https://github.com/LaceCelestia/mouse-battery-indicator/releases/latest)し、`MouseBatteryIndicator-v1.1.2-Setup.exe` を開いてインストールします。管理者権限は不要です。インストール後はスタートメニューの「Mouse Battery Indicator」から起動できます。

インストールせず使う場合は、同じページの `MouseBatteryIndicator-v1.1.2-windows.zip` を展開し、`MouseBatteryIndicator.exe` を開きます。GitHub の「Source code」は開発用ソースなので、通常は Setup.exe を選んでください。

- 数字が隠れている場合は、通知領域の「∧」を開き、数字をタスクバーへドラッグします。
- 左クリックで機種名・残量・充電状態を表示します。
- **右クリック →「表示するマウス」**で、検出したマウスから表示対象を選べます。選択は再起動後も保存されます。
- 「自動」は取得できるマウスを選び、取得できる間は同じマウスを表示します。操作中のマウスを判定する機能ではありません。
- 手動で選んだマウスが切断された場合は `--` になります。
- 数字の下の緑の横線は充電中です。アニメーションはありません。
- 充電していない状態で残量 20% 以下になると数字がオレンジになります。

詳細画面を閉じても常駐します。終了するときは右クリック →「終了」を選びます。

## 対応範囲

| 取得方法 | 対象と制限 |
| --- | --- |
| Razer 専用通信 | 下記の既知モデルの USB 有線／受信機接続。充電式機種では充電状態も取得。 |
| Logitech / ロジクール HID++ 2.0 | USB 接続または受信機経由で、マウス識別と対応バッテリー機能を公開する機種。受信機のスロット 1〜6 を探索。 |
| Xenics TITAN GE AIR 用の取得処理（試験対応） | 公式ソフトの残量照会に基づく USB／受信機の取得処理。**実機未確認**。対象 ID は `25A7:FA7B` / `25A7:FA7C`。対応レポートを持つ機器に限る。 |
| Windows の Bluetooth 残量 | メーカーを問わず、Windows が残量を公開する Bluetooth マウス。充電状態は取得不可。 |

すべてのマウスに共通する取得方法はありません。旧 HID++ 1.0、残量を公開しない機種、未対応メーカーの独自 USB 受信機は対象外です。Bluetooth で使うロジクール製マウスは Windows 側の取得条件に従います。

TITAN GE AIR の取得処理は、実機での検証が必要です。メーカーソフトによる初期化が必要なファームウェアでは取得できない可能性があります。同じ USB ID を使う別機種もあるため、表示名には USB の製品名を使い、ID だけで TITAN GE AIR と決めつけません。[実装の根拠と未確認事項](https://github.com/LaceCelestia/mouse-battery-indicator/blob/main/docs/xenics-protocol.md)。

Razer の対応モデル（USB PID 34 種類）：

- Viper Ultimate / Viper V2 Pro / Viper V3 Pro / Viper V3 HyperSpeed / Viper Mini Signature Edition
- DeathAdder V2 Pro / V2 X HyperSpeed / V3 Pro / V3 HyperSpeed
- Basilisk Ultimate / X HyperSpeed / V3 Pro / V3 X HyperSpeed / V3 Pro 35K
- Naga Pro / Naga V2 Pro / Naga V2 HyperSpeed
- Orochi V2 / Cobra Pro

機種名が特定できない共通 HyperPolling ドングル（PID `00B3`）は対象外です。Bluetooth 接続は Windows 側で取得できる場合に表示します。

Logitech は機種名の固定リストではなく、HID++ の対応機能を調べます。

| 機能 | 表示 |
| --- | --- |
| `1004` Unified Battery / `1000` Battery Level Status | 百分率の報告能力がある場合は数字。それ以外は詳細画面に残量の段階を表示。 |
| `1001` Battery Voltage | 既知の電圧曲線から推定。詳細に「推定値」、ツールチップに「約」を表示。 |
| `1F20` ADC Measurement | 百分率へ換算せず、詳細に「電圧のみ」などを表示。選択した機器の電圧は診断結果で確認可能。 |

数値を取得できないときのトレイ表示は `--` です。問い合わせ失敗時には前回の数値を消し、遅れて返った前回の結果も次回に再利用しません。Windows の Bluetooth 残量は OS が最後に受信した値なので、機器やドライバーによって反映が遅れることがあります。

## 動作確認

- Windows 10 / 11、.NET Framework 4.8 を使用します。追加パッケージや実行時のネットワーク接続は不要です。
- **Razer Viper V3 Pro の受信機（VID `1532` / PID `00C1`）で実機確認済み**。2026-09-14、複数メーカー対応版でも残量・充電状態を読み取り、応答のチェックサムが一致しました。Razer ドライバーが入っている環境で確認しています。
- TITAN GE AIR、ロジクール、Bluetooth マウス、上記以外の Razer は実機未検証です。プロトコル応答・複数台の識別・切断・タイムアウトはテスト用データで検証しています。
- この PC にある Bluetooth 機器を、誤ってマウスとして表示しないことを確認しています。

## 設定

右クリックメニューで変更できます。

| 項目 | 内容 |
| --- | --- |
| 更新間隔 | 15 / 30 / 60 / 120 秒。初期値は 60 秒。 |
| 数字の色 | Windows のテーマに合わせる / ライムグリーン / 白 / 黒。 |
| 数字の太さ | 細め / 標準 / 太め。初期値は標準。 |
| 残量 20% 以下で通知 | 初期値はオン。充電中は通知しません。 |
| Windows 起動時に実行 | 初期値はオフ。 |

設定の保存先は `%LOCALAPPDATA%\MouseBatteryIndicator\settings.xml` です。旧版の設定は初回起動時に引き継ぎ、元の設定ファイルも残します。インストール版の更新では、同じインストール先の自動起動設定と、Windows 側で無効にしていた状態を引き継ぎます。

自動起動を有効にしたあとで exe を移動するときは、移動前に設定をオフにし、移動先のアプリで再度オンにします。

## ビルドと検証

実行中のアプリを終了してから、このフォルダーで PowerShell を開いて実行します。Windows 付属の C# コンパイラーを使うため、.NET SDK は不要です。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Test
```

`dist\MouseBatteryIndicator.exe` を作成します。`-StageOnly` を付けると、実行中の dist を置き換えず `.work` にビルドできます。

検証内容：Razer / HID++ / Xenics の応答照合・値の解釈、Windows 機器と残量の対応付け、マウスの選択、古い値の破棄、ネイティブ I/O のキャンセル、旧設定の移行、配布 exe の製品名と取得処理の同梱、表示とアイコン資源の解放。

## 配布用ファイルの作成

ZIP だけを作る場合：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\package.ps1 -Test
```

インストーラーも作る場合は、[Inno Setup 6](https://jrsoftware.org/isdl.php)（6.7.3 で検証）を導入して実行します。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build-installer.ps1 -Test
```

コンパイラーの場所を指定する場合は `-CompilerPath 'C:\path\to\ISCC.exe'` を追加します。`artifacts` に Setup.exe、ZIP、それぞれの SHA256 ファイルができます。配布物にはアプリ本体と説明書だけを含め、開発環境の設定・診断ログ・接続機器の情報は含めません。

インストーラーは `%LOCALAPPDATA%\Programs\MouseBatteryIndicator` に配置し、スタートメニューとアンインストール項目を登録します。デスクトップショートカットは任意、自動起動はアプリから設定できます。

## 取得できない場合

マウスを動かしてから「今すぐ更新」を押します。接続方式と上記の対応範囲も確認してください。右クリック →「診断結果を保存」で、直近の取得結果と接続中の HID 機器のメーカー・製品 ID をテキストに保存できます。

コマンドから改めて取得する場合：

```powershell
Start-Process .\dist\MouseBatteryIndicator.exe -ArgumentList '--diagnose diagnostics.txt' -WindowStyle Hidden -Wait
Get-Content .\diagnostics.txt -Encoding UTF8
```

例外ログは `%LOCALAPPDATA%\MouseBatteryIndicator\app.log` です。

## 削除

アプリを右クリック →「終了」で終了します。インストール版は Windows の設定 →「アプリ」からアンインストールできます。ZIP版は自動起動をオフにしてから、展開したフォルダーを削除します。

どちらも保存した設定は残ります。設定も不要なら `%LOCALAPPDATA%\MouseBatteryIndicator` を削除できます。旧版を使っていた場合は、引き継ぎ元の設定フォルダーも残ります。

## 参照仕様

- [OpenRazer のマウスドライバー](https://github.com/openrazer/openrazer/blob/master/driver/razermouse_driver.c)と[機種 ID](https://github.com/openrazer/openrazer/blob/master/driver/razermouse_driver.h)：Razer のモデルと問い合わせ方式。
- [Logitech HID++ 2.0 仕様](https://github.com/Logitech/cpg-docs/tree/master/hidpp20)：機能の検出、マウス識別、残量・充電状態。
- [Solaar の HID++ 実装](https://github.com/pwr-Solaar/Solaar/blob/master/lib/logitech_receiver/hidpp20.py)：電圧換算と機能ごとの残量解釈。
- [Microsoft BatteryLife](https://learn.microsoft.com/en-us/windows/win32/properties/props-system-devices-batterylife)、[ContainerId](https://learn.microsoft.com/en-us/windows-hardware/drivers/install/devpkey-device-containerid)：Windows の残量と機器の対応付け。互換用 Bluetooth プロパティ `{104EA319-6EE2-4701-BD47-8DDBF425BBE5}, 2` も参照しますが、このプロパティは公開 SDK の保証対象ではありません。
- [Viper Tray](https://github.com/damianyoko/viper-tray)：Windows のマウス HID コレクションを開く方法。

メーカー公式のアプリではありません。設定変更コマンドは送らず、機器情報と残量を読み取ります。
