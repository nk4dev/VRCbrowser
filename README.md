# VRCbrowser

VRChat ワールド用のインワールド・ページビューアです。URL を入力すると、テキスト / Markdown / 簡単な HTML / 画像をキャンバスに表示します。表示中のページはインスタンス内の全員に同期されます。

[English](#english)

## 特長

- テキスト、Markdown、JSON、HTML、画像(`.png` / `.jpg`)を表示
- HTML はページ内に書かれた CSS の一部に対応(文字色・サイズ・太字・斜体・行揃え・背景色・余白・`display`)
- 表示中の URL を全員に同期
- 戻る / ホーム / 更新ボタン、「よく使うサイト」メニュー(最大 8 件)
- ページ内の `<script>` を実行する簡易 JavaScript ランタイム(下記)
- UdonSharp スクリプト 2 本とプレハブ 1 つだけで完結

## 必要環境

- Unity 2022.3
- VRChat SDK - Worlds 3.5.0 以降(動作確認は 3.10.5)

## 導入

### VCC / ALCOM(VPM)

1. 次の URL をリポジトリとして追加します。
   `https://raw.githubusercontent.com/nk4dev/VRCbrowser/main/index.json`
2. プロジェクトの管理画面で **VRCbrowser** を追加します。

### unitypackage

[Releases](https://github.com/nk4dev/VRCbrowser/releases) から `VRCbrowser-<version>.unitypackage` をダウンロードしてインポートします。

## 使い方

1. `Packages/VRCbrowser/Runtime/Prefabs/VRCbrowser.prefab` をシーンに置きます。
2. `VRCbrowser` コンポーネントのインスペクターで設定します。

| 項目 | 内容 |
|---|---|
| Home Url | ホームボタンと起動時に開くページ。空なら Welcome Page を表示 |
| Bookmark Urls / Bookmark Labels | 「よく使うサイト」メニューの URL と表示名(上から詰めて最大 8 件) |
| Welcome Page | URL 未指定のときに表示する文章(Markdown) |
| Image Max Size | 画像表示の最大サイズ |
| Appearance / Limits | 文字サイズ、余白、読み込む行数や HTML の上限 |

## JavaScript

ページ内に直接書かれた `<script>` を、プレハブ内の `JsEngine`(`VRCbrowserJs`)が実行します。自作ページ向けのサブセットで、一般の Web サイトのスクリプトはほとんど動きません(動かなくてもページの表示は変わりません)。スクリプトは各プレイヤーの手元で実行され、同期されるのは URL だけです。

対応しているもの:

- `var` / `let` / `const`、関数、クロージャ、アロー関数、`if` / `for` / `for...of` / `for...in` / `while` / `do...while` / `switch`、テンプレート文字列
- 配列・オブジェクト・文字列の主なメソッド、`Math`、`JSON`、`Date`、`console.log`、`parseInt` / `parseFloat`
- `document.write`、`getElementById`、`querySelector(All)`(`tag` / `#id` / `.class` / `tag.class`)
- 要素の `textContent` / `innerHTML` / `style` / `classList` / `setAttribute` など
- `setTimeout` / `setInterval`、`window.onload`、`DOMContentLoaded`、`<body onload>`

対応していないもの:

- 外部スクリプト(`<script src>`)、`fetch` / `XMLHttpRequest`、クリックなどのイベント
- `class`、プロトタイプ、`new`(`Date` / `Array` / `Object` / `Error` 以外)、正規表現、分割代入、スプレッド構文、`async` / `await`
- `try...catch` の `catch` 節(エラーが起きるとそのスクリプトは中断されます)
- `let` / `const` のブロックスコープ(関数スコープとして扱います)

`VRCbrowserJs` のインスペクター:

| 項目 | 内容 |
|---|---|
| Show Errors | スクリプトのエラーをステータス欄に表示(ページ作成時のデバッグ用。既定はオフで、エラーはログにだけ出ます) |
| Max Script Chars | 1 つの `<script>` の最大文字数 |
| Frame Budget Ms | 1 フレームあたりにスクリプトへ割く時間。処理は複数フレームに分割されます |
| Max Steps Per Run | 1 回の実行で処理する命令数の上限。超えると中断します |
| Min Render Interval | スクリプトによる再描画の最短間隔(秒) |
| Min Timer Delay | `setTimeout` / `setInterval` の最短間隔(秒) |

スクリプトを使わない場合は、`VRCbrowser` の `Js` 欄を空にすると無効になります。

## 制限

- ページ内のリンクは開けません(Udon は実行時に URL を作れないため)。URL を入力するか、ブックマークを使ってください。
- 外部スタイルシート、HTML 内の画像、複雑なレイアウトには対応していません。HTML 内の画像は `[画像: alt]` と表示されます。
- VRChat の許可リストにないドメインは、設定で「Allow Untrusted URLs」を有効にした人だけ読み込めます。
- 読み込みは約 5 秒に 1 回までです(VRChat の制限)。
- 画像は最大 2048×2048 です(VRChat の制限)。

## ライセンス

[MIT](LICENSE.md)

---

## English

An in-world page viewer for VRChat worlds. Enter a URL and it shows text, Markdown, simple HTML or an image on a canvas. The current page is synced to everyone in the instance.

### Features

- Shows text, Markdown, JSON, HTML and images (`.png` / `.jpg`)
- HTML supports a subset of in-page CSS (color, font size, bold, italic, text-align, background color, margins, padding, `display`)
- The current URL is synced to all players
- Back / Home / Reload buttons and a "frequently used sites" menu (up to 8 entries)
- A small JavaScript runtime for inline `<script>` elements (see below)
- Two UdonSharp scripts and one prefab, nothing else

### Requirements

- Unity 2022.3
- VRChat SDK - Worlds 3.5.0 or later (tested on 3.10.5)

### Installation

**VCC / ALCOM (VPM)**

1. Add this URL as a repository:
   `https://raw.githubusercontent.com/nk4dev/VRCbrowser/main/index.json`
2. Add **VRCbrowser** to your project.

**unitypackage**

Download `VRCbrowser-<version>.unitypackage` from [Releases](https://github.com/nk4dev/VRCbrowser/releases) and import it.

### Usage

1. Place `Packages/VRCbrowser/Runtime/Prefabs/VRCbrowser.prefab` in your scene.
2. Configure the `VRCbrowser` component in the inspector.

| Field | Description |
|---|---|
| Home Url | Page opened by the Home button and on start. If empty, the Welcome Page is shown |
| Bookmark Urls / Bookmark Labels | URLs and labels for the menu (fill from the top, up to 8) |
| Welcome Page | Text (Markdown) shown when no URL is set |
| Image Max Size | Maximum display size for images |
| Appearance / Limits | Font size, padding, and limits on lines and HTML size |

The button labels and the default Welcome Page are in Japanese; edit the prefab to change them.

### JavaScript

Inline `<script>` elements are run by `JsEngine` (`VRCbrowserJs`) inside the prefab. It is a subset meant for pages you write yourself; scripts of ordinary websites mostly fail (the page still renders the same). Scripts run locally for each player; only the URL is synced.

Supported:

- `var` / `let` / `const`, functions, closures, arrow functions, `if` / `for` / `for...of` / `for...in` / `while` / `do...while` / `switch`, template literals
- Common array, object and string methods, `Math`, `JSON`, `Date`, `console.log`, `parseInt` / `parseFloat`
- `document.write`, `getElementById`, `querySelector(All)` (`tag` / `#id` / `.class` / `tag.class`)
- Element `textContent` / `innerHTML` / `style` / `classList` / `setAttribute` and friends
- `setTimeout` / `setInterval`, `window.onload`, `DOMContentLoaded`, `<body onload>`

Not supported:

- External scripts (`<script src>`), `fetch` / `XMLHttpRequest`, click and other events
- `class`, prototypes, `new` (except `Date` / `Array` / `Object` / `Error`), regular expressions, destructuring, spread, `async` / `await`
- The `catch` clause of `try...catch` (an error aborts that script)
- Block scoping of `let` / `const` (they are function scoped)

`VRCbrowserJs` inspector:

| Field | Description |
|---|---|
| Show Errors | Show script errors in the status bar (for debugging your page; off by default, errors always go to the log) |
| Max Script Chars | Maximum length of one `<script>` |
| Frame Budget Ms | Time given to scripts per frame; work is spread over frames |
| Max Steps Per Run | Instruction limit per run; longer scripts are aborted |
| Min Render Interval | Minimum seconds between re-renders caused by scripts |
| Min Timer Delay | Minimum delay for `setTimeout` / `setInterval`, seconds |

To turn scripts off, clear the `Js` field on `VRCbrowser`.

### Limitations

- Links inside a page cannot be opened (Udon cannot build URLs at runtime). Type a URL or use a bookmark.
- No external stylesheets, inline HTML images or complex layout. Images inside HTML are shown as `[画像: alt]`.
- Domains outside VRChat's allowlist only load for players who enabled "Allow Untrusted URLs".
- One load per roughly 5 seconds (VRChat limit).
- Images up to 2048×2048 (VRChat limit).

### License

[MIT](LICENSE.md)
