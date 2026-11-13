# AiryView 2026-11-14 横スクロール

## 完了したこと

- 2.1.78: WPF標準で通知されないWM_MOUSEHWHEELをウィンドウのフックで受け、ポインター下の横移動可能なScrollViewerへ転送。WindowTitleBar共通導入箇所へ接続し、閲覧・編集・印刷画面に適用。
- Shift＋縦ホイールにも対応。Ctrl＋ホイールの拡大縮小と通常の縦ホイールは維持。画面座標をDPI対応で変換し、小さいdeltaも比例移動。横端で縦送りへ流さない。
- publish成功（既存WFO0003警告のみ）。Windowsメッセージ経路の検証11件成功。左右・微小delta・端・Shift・Ctrl・収まる内容・MainWindow画像/PDFの閲覧領域を検証。PDF領域は合成した表示内容を使用し、PDF解析はこのテストの対象外。
- ログ: `C:\dev\projects\utilities\AiryView\artifacts\horizontal-scroll\results.txt`。
- 導入終了0。Program Filesの2.1.78.0と検証済みDLLが一致し、導入版でも横スクロール11件が成功。

## 次に着手すべき

- 利用者の実マウスで横ホイールの追従を確認する。検証はWindowsメッセージ送信までで、実機の入力操作は未確認。

## 重要な設計原則・未確定論点

- 開始時の既存変更は未追跡outputのみ。利用者の画像・PDFをテストやGitへ混ぜない。テスト時は利用履歴・ウィンドウ設定を書き込まない。
