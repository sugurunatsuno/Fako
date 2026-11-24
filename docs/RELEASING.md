# リリース手順

Fako の CI/CD は `.github/workflows/ci-cd.yml` で定義しています。main ブランチへの push を起点に、ビルドと GitHub Release の作成が自動で走ります。

## 自動で行われること

| トリガー | 実行される内容 |
|---|---|
| `pull_request`（main 宛） | `build` ジョブのみ |
| `push`（main） | `build` ジョブ → 成功したら `release` ジョブ |
| `workflow_dispatch`（手動） | `build` ジョブのみ（Release は作りません） |

### build ジョブ

1. `src/Fako/Fako.csproj` を Release で restore / build
2. `tests/Fako.RemoteTests/Fako.RemoteTests.csproj` を Release で build
3. `tests/Fako.Tests/Fako.Tests.csproj` が存在する場合だけ `dotnet test` を実行
   （テストプロジェクトがまだ無い間はスキップされ、失敗しません）

### release ジョブ

`build` が成功し、かつ main への push のときだけ動きます。権限は `contents: write` のみをこのジョブに付与しています。

1. `src/Fako/Fako.csproj` の `<Version>` を `dotnet msbuild -getProperty:Version` で取得
2. タグ `v<version>` の Release が既にあるか `gh release view` で確認し、あれば以降をすべてスキップ
3. `win-x64` 向けの自己完結・単一ファイル exe を publish
4. リリース資産を生成
5. `gh release create "v<version>"` で Release を作成（`v<version>` タグが無ければ GitHub 側で自動作成）

同じバージョンのまま main に push しても、Release が既に存在すれば何も作りません。新しい Release が必要なときは、必ず `<Version>` を上げてください。

## バージョンを上げてリリースする

1. `src/Fako/Fako.csproj` の `<Version>` を更新する（例: `1.0.0` → `1.0.1`）。必要に応じて `DOCUMENTATION.md` などの版表記も合わせる
2. 変更を main にマージする（または main へ push する）
3. GitHub Actions の `ci-cd` ワークフローが成功したことを確認する
4. Releases に `v<version>` が作られていることを確認する

リリースノートは、ダウンロード方法・「Windows 10/11 向け、.NET 不要」の案内を先頭に付け、その後に `--generate-notes` による自動生成ノートを連結したものになります。リリースタイトルは `Fako <version>` です。

## リリース資産

| ファイル | 内容 |
|---|---|
| `Fako.exe` | `win-x64` 向け・自己完結・単一ファイル。.NET ランタイムのインストールは不要 |
| `Fako.exe.sha256` | `Fako.exe` の SHA256。形式は `<ハッシュ>  Fako.exe`（UTF-8 / LF） |
| `Fako-<version>-win-x64.zip` | `Fako.exe`、`README.md`、`はじめにお読みください.md` を同梱 |

`Fako.exe.sha256` はアプリ側の更新ファイル検証で使う想定です。手元で確認する場合:

```powershell
(Get-FileHash .\Fako.exe -Algorithm SHA256).Hash.ToLowerInvariant()
Get-Content .\Fako.exe.sha256
```

## 失敗したとき

- `build` が失敗した場合、`release` は実行されません。原因を直して main に push してください。
- `release` の途中で失敗した場合、Release が作られていないか、下書きのまま残っていないかを確認してください。`gh release create` は資産のアップロード中を下書きとして扱い、失敗時は後始末します。
- Release が既に存在する状態で `release` ジョブだけを再実行しても、存在チェックでスキップされます。作り直す場合は該当 Release（とタグ）を削除してから再実行するか、バージョンを上げて push してください。

  ```powershell
  gh release delete v1.0.0 --cleanup-tag --yes
  ```

  その後、Actions の該当 run を Re-run します（push 由来の run なら `release` ジョブも再実行されます）。
- `release` ジョブには `concurrency: release-main` を設定してあり、複数の push が同時に Release を作らないよう直列化されます。

## ローカルで同じ publish を試す

```powershell
dotnet publish src/Fako/Fako.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o out
```

CI では `out/Fako.exe` をそのまま Release にアップロードします。
