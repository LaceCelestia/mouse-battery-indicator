# Xenics TITAN GE AIR の残量取得

実装はメーカーソフトの静的な確認に基づきます。GE AIR の実機では未確認です。

[メーカー配布ページ](https://www.xenics.co.kr/board/view.php?bdId=download&sno=565)の v1.0（2022-02-07）、`TITAN GE AIR WIRELESS Setup v1.0 20211229.exe` に含まれる `Cfg.ini` は `25A7:FA7B`、`25A7:FA7C` を指定しています。内部の機種識別値は CID `0A`、MID `03` です。

この USB ID は他社の OEM マウスでも使われます。機種名は USB の製品名を使い、取得できなければ `Xenics / OEM マウス` と表示します。ID だけで GE AIR と断定しません。

## 送受信

メーカーの `GetPower` 処理は次の Feature Report を送ります。17 bytes、先頭の Report ID を含みます。

```text
08 04 00 00 00 00 00 00 00 00 00 00 00 00 00 00 49
```

末尾は全バイトの和が下位8 bitで `55` となるチェックサムです。応答は17 bytesの Input Report です。

| オフセット | 意味 |
| --- | --- |
| 0 | Report ID `09` |
| 1 | コマンド `04` |
| 2 | 成功時 `00` |
| 6 | 残量、0～100 |
| 7 | 充電中は非0 |

応答の長さ・Report ID・コマンド・状態・残量の範囲を検証します。メーカー処理に応答チェックサムの検証は見つからなかったため、未確認の制約は追加していません。

Feature `08` と Input `09` が別の HID collection に属する場合は、同じ物理 Container ID と USB ID の組み合わせだけを使います。Vendor usage page の collection に絞り、マウスやキーボードの入力は読み取りません。両方の Report ID が HID descriptor に存在することも確認します。

Feature送信には [IOCTL_HID_SET_FEATURE](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/hidclass/ni-hidclass-ioctl_hid_set_feature) を使用します。[Microsoft の hidclass.h](https://github.com/microsoft/win32metadata/blob/main/generation/WinSDK/RecompiledIdlHeaders/shared/hidclass.h) にある `HID_IN_CTL_CODE(100)` は `0x000B0191` です。送受信を overlapped I/O にし、時間切れではキャンセルします。キャンセルが未完了ならバッファとハンドルを保持し、次の照会を止めます。

## 未確認の点

- 実機の HID collection 構成と、USB・レシーバー両方での動作。
- メーカーソフトが行う初期認証なしで残量を取得できるか。
- スリープ・切断時のレシーバーの応答。

初期認証、ペアリング、設定変更のコマンドは実装していません。読み取れなければ値を作らず、診断に理由を記録します。メーカーのバイナリや展開ファイルはアプリに含めていません。

確認した ZIP の SHA256 は `985D285DFD438BFE25A3679AB01A2C5AB433D3165591AFCE1600F5BA1A60B108`、内部 `OemDrv.exe` は `020E7A12B37339D3102CEB2FE3C5E444B6266F2284A16473AC061F3D1CD9FA84` です。
