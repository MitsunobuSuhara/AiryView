# テーマの表示名 — 2026-10-12

- 利用者向け表示をモダン／クラシックからダークモード／ライトモードへ変更。切り替えボタンの案内・ツールチップ3箇所が対象。
- 2.1.69のビルド・publish成功、既存テーマテスト75件成功。内部の保存値は維持。
- 利用者の保存・終了確認後にProgram Files版2.1.69を導入、インストーラー終了0、通常起動先とタイトル2.1.69を確認。
- 作成版と導入DLL SHA256一致：DD584FB9A98AC701DC54F579A61015B01C4D3FBE93AABEB580D3054B51779D22。旧版退避：artifacts/theme-names-session/install-backup-2.1.68。
- 既存未コミット変更は維持し、表示名と版表示だけを分離してGit保存。
- GitHubの最新版リリースv2.1.69公開完了：https://github.com/MitsunobuSuhara/AiryView/releases/tag/v2.1.69 。対象コミットeef584fb5f801f06d01274625012c10ad394f123。ZIPとSHA256SUMS.txtのuploaded、draft=false、prerelease=false、LatestをAPIで確認。
- ZIP：AiryView-2.1.69-20261012-162519.zip。SHA256：f89fe0b90deb2364e3f8b0bafbcd3f5286bdd3ddee55dc65067b913c9263342d。公開ZIPのdigestが一致し、ZIP内DLLが導入版と一致。セットアップcmd・PDF helper同梱を確認。既存UI調整を含むためタグだけでは同一バイナリを再現できないことを説明に明記。
