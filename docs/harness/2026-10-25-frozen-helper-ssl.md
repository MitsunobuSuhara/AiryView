# 配布ヘルパーのSSLデータ同梱 — 2026-10-25

- 対象会話: `01a10d91-f752-78c1-964b-7e4d83b7d277`。利用者の「セキュリティを高めるためにできること」「すすめてください」に基づく更新作業。
- 実際の失敗: 署名部品を更新後、Pythonソースからの署名は成功したが、PyInstallerでまとめた実行ファイルではSSLContext作成時にFileNotFoundErrorとなった。最小のSSLContextだけの配布実行ファイルでも再現。
- 原因: 使用中のバンドルPythonのssl.pyは隣の`codex-rejected-cas.pem`を参照する。配布にPythonコードだけが取り込まれ、そのデータが抜けていた。TLSの検証や拒否CA設定を無効にする問題ではなかった。
- 反映: `scripts/build-pdf-helper.ps1`で、実際のPythonのssl.__file__から必要データを探し、存在する場合は同梱する。`docs/pdf-security.md`にもビルド条件を記録。管理されるAGENTS.mdや共通指示は変更しない。
- 適用範囲: この配布ヘルパーと、このSSL実装を持つPythonからのパッケージ化。一般のPythonやすべてのアプリへ固定の例外を広げない。
- 確認: 署名・検証・改ざん検出を含む配布ヘルパー7件、AppContainer内の署名・検証を含む追加21件が成功。ソース実行の成功だけを配布物の成功とは扱わない。
- 証跡: `artifacts/pdf-security/helper-diagnostic.log`、`artifacts/feature-tests.log`、`artifacts/pdf-security/security-test-results.txt`。一時的な詳細例外出力は最終ソースから削除済み。
