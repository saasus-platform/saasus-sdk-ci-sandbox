# saasus-sdk-ci-sandbox

SDK 契約テストの検証用サンドボックス。**配布物ではない。** 各言語の SDK
リポジトリに契約テストを入れる前に、ここで (A)(B)(C) が緑になることを確かめる。

検証する3点は言語共通。

| | 内容 |
|---|---|
| (A) | 候補SDKがビルドできる |
| (B) | 候補SDKのリクエストが最新specのモックに受理され、レスポンスがデシリアライズできる |
| (C) | 直近リリースが最新specに対してなお往復できる（タグ同梱のスイートで検証） |

## 構成

```
.
├── .github/workflows/
│   └── csharp-contract-test.yml    # 言語ごとに1ファイル
└── csharp/                          # saasus-sdk-csharp のルートと1:1
    ├── saasus-sdk-csharp.csproj
    ├── generated/                   # api の spec から generate.sh で生成
    ├── tests/Contract/              # 契約テスト本体
    └── ...
```

言語非依存の処理（spec 取得・Prism 起動・タグ解決）は
[saasus-platform/github-actions](https://github.com/saasus-platform/github-actions)
のコンポジットアクションにあり、ここには置かない。

### 各言語ツリーは対応するSDKリポジトリのミラー

`csharp/` の中身は `saasus-platform/saasus-sdk-csharp` のリポジトリ直下と同じ
レイアウトにしてある。ワークフローとテストを本番へ移植するときの差分を
`working-directory` と `paths` だけに閉じ込めるため。崩さないこと。

### 言語を追加する

1. `<lang>/` に対象SDKリポジトリのルートをそのまま置く
2. `.github/workflows/<lang>-contract-test.yml` を追加する
   - `paths` を `<lang>/**` と当該ワークフローに絞る
   - `concurrency.group` に言語名を入れる
   - ジョブの `name` を `<lang>-contract-test` にする（必須チェック名の衝突を避ける）
   - ビルド/テストのステップに `working-directory: <lang>` を付ける
   - (C) は `working-directory: latest-release/<lang>`
3. main にマージしてからタグを打ち直す（下記）

## タグ運用

(C) は「直近リリースのタグをチェックアウトし、そのタグが同梱するスイートを
最新specに対して走らせる」。タグは git のリポジトリ単位なので、ここでは
**言語横断の単一 `vX.Y.Z` 系列**を使う。タグのツリーには全言語が入っており、
各言語のスイートはその中の自分のサブディレクトリにある。言語ごとの
リリース番号はサンドボックスでは意味を持たないため分けていない
（分けるなら共有アクション `resolve-latest-stable-tag` に prefix 入力の
追加が必要になる）。

スイートを変更して main に入れたら、main にタグを打つ。そのタグが次の PR の
(C) の対象になる。タグが無い、またはタグのツリーにスイートが無い状態では
(C) は設計通り失敗する（スキップ機構は無い）。

## 本番SDKリポジトリへの移植

`csharp/` の例。

1. `csharp/` の中身をSDKリポジトリ直下へ
2. ワークフローから `working-directory` と `on.pull_request.paths` を落とす
3. (C) の `working-directory` を `latest-release` に戻す
4. ジョブ `name` を `contract-test` に戻す

各ワークフローの先頭にサンドボックス固有の差分を列挙してあるので、そこを
参照すること。
