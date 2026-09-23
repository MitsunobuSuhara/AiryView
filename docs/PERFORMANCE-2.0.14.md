# AiryView 2.0.14 速度改善（2026-09-24）

PDFの寸法取得で本文解析を省き、取得済み寸法を再利用する。回転時に該当ページの寸法を無効化する。WebP・SVGはPNGへの圧縮と再デコードを廃止し、元の解像度・透過を保った画素をWPFへコピーする。通常のラスター画像は寸法検査用デコーダーを再利用するが、ICC付き画像は従来のBitmapImage色補正を維持する。TXT/Markdownの内容と変更検知ハッシュを同じ読込データから作り、TXTの表示には不要な印刷用FlowDocumentを作らない。先頭位置の行番号計算に伴うレイアウトも省く。

## 計測結果

同じPC、合成データ、各5回の中央値。初回1回を除いたウォーム計測であり、Explorerからの起動時間・OSのディスクキャッシュが空の状態・全実ファイルを保証する数字ではない。画像は1600×1200、SVGは800×600を従来同様4倍で描画、TXTは2000行、MDは100節。PDFは各ページ2000図形。媒体ごとの測定範囲が異なるため形式間の比較には使わない。

|形式|変更前 ms|変更後 ms|
|---|---:|---:|
|PDF 5ページ|26.529|17.314|
|PDF 100ページ|206.438|31.502|
|JPEG|16.544|21.879|
|PNG|23.532|18.037|
|BMP|21.085|21.569|
|TIFF|33.289|26.987|
|GIF|23.180|19.500|
|ICO|7.267|7.835|
|WebP|243.611|19.773|
|SVG|274.387|29.513|
|TXT|104.975|109.183|
|Markdown|41.767|44.298|

PDFはOpenPathsAsync完了まで、その他はレイアウトとContextIdle到達まで。PDFの寸法取得だけでは100ページ192.259→1.996ms。PDF/WebP/SVGは大幅改善。JPEG・BMP・ICO・文章では明確な改善を確認できず、数msの変動を高速化実績に含めない。JPEGは中間測定で15.265msだったため、実画面の計測にはばらつきがある。文章はWPFのレイアウトが残る。

## 検証

- PDF・印刷自己テスト42件、UIテスト149件成功。
- CropBox、継承寸法、4方向回転、回転後の再取得、解放後のアクセス拒否。
- JPEG/PNG/BMP/TIFF/GIF/ICO/WebPは従来デコードと全画素一致、寸法・DPI保持。
- ICC付き144dpi JPEGの全画素一致、SVGの半透明・透明領域保持。
- 文字コード、外部変更検出、原本保護、表示・回転・SVG拡大再描画の既存テスト成功。
- Releaseビルドと.NET同梱publish成功。NuGetの脆弱性情報を取得できないNU1900警告あり。コンパイルエラーなし。

計測コードはSelfTest.RunOpenBenchmarkAsync / RunMediaBenchmarkAsync。実行オプションは --open-benchmark / --media-benchmark。生データはartifacts/open-benchmark-{before,after}.csv、artifacts/media-benchmark-{before,after}.csv。後者の再実行は比較用ファイル・画面を生成する。

参照: [PDFiumの寸法API](https://pdfium.googlesource.com/pdfium/+/main/public/fpdfview.h)、[WPF BitmapImageの色補正処理](https://github.com/dotnet/wpf/blob/main/src/Microsoft.DotNet.Wpf/src/PresentationCore/System/Windows/Media/Imaging/BitmapImage.cs)。