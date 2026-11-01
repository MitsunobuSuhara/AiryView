# AiryView 2026-11-02

## 完了したこと

- 2.1.74: 線幅の増減ボタンと上下キーを0.25刻み、最小0.25へ変更。主ツールバーと選択図形の書式で共通適用。PDFはpt、画像はpx。文字サイズの増減は従来どおり。
- ビルド・publish成功（既存WFO0003警告のみ）。編集303件成功。0.25/0.5/0.75の操作、下限、細線の形状、PDF保存・再読込後に0.25ptが1ptより細いことを検証。
- 利用者が全画面を保存して閉じた後、インストール終了0。Program Filesの2.1.74.0と候補DLLが一致し、導入版でも303件成功。
- ログ: `C:\dev\projects\utilities\AiryView\artifacts\editor-test-results.txt`。細線PDF: `C:\dev\projects\utilities\AiryView\artifacts\editor-tests\arrow-tips.pdf`。

## 次に着手すべき

- 利用者の実書類で好みの線幅を確認。実プリンターの印刷は未確認。

## 重要な設計原則・未確定論点

- 既存のデザイン等の未commit変更を保護し、今回の差分だけ選択stage。
- 形状の最初のテストは既定の0.25許容誤差で失敗したため、測定用許容誤差を0.001に限定した。通常権限制限下ではAppContainer作成が失敗したため、Windows通常環境で再検証して成功。隔離の設定は変更していない。
