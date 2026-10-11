# VRCbrowser

An in-world page viewer for VRChat worlds. Enter a URL and it shows text, Markdown, simple HTML or an image on a canvas. The current page is synced to everyone in the instance.

[Documentation](https://apps.nknighta.me/VRCbrowser/docs/en.html) · [Add to VCC](https://apps.nknighta.me/VRCbrowser/install.html) · [日本語](#日本語)

## Features

- Shows text, Markdown, JSON, HTML and images (`.png` / `.jpg`)
- HTML supports a subset of in-page CSS: text styling, margins and padding, centred or narrowed columns, lists, custom properties and `@media` (see below)
- Pages in Shift_JIS or EUC-JP (declared with `charset=`) are decoded, and `<frameset>` pages are shown as one document
- The current URL is synced to all players
- Back / Home / Reload buttons and a "frequently used sites" menu (up to 8 entries)
- A small JavaScript runtime for inline `<script>` elements (see below)
- Two UdonSharp scripts, one prefab and a custom inspector, nothing else

## Requirements

- Unity 2022.3
- VRChat SDK - Worlds 3.5.0 or later (tested on 3.10.5)

## Installation

**VCC / ALCOM (VPM)**

1. Open **[Add to VCC](https://apps.nknighta.me/VRCbrowser/install.html)** and press the button; VCC or ALCOM opens and asks to add the repository.
   To add it by hand instead, use this URL as a repository:
   `https://raw.githubusercontent.com/nk4dev/VRCbrowser/main/index.json`
2. Add **VRCbrowser** to your project.

**unitypackage**

Download `VRCbrowser-<version>.unitypackage` from [Releases](https://github.com/nk4dev/VRCbrowser/releases) and import it.

## Usage

1. Place `Packages/VRCbrowser/Runtime/Prefabs/VRCbrowser.prefab` in your scene.
2. Configure the `VRCbrowser` component in the inspector.

| Section | Description |
|---|---|
| Pages | **Home Url**: page opened by the Home button and on start. If empty, the Welcome Page is shown. **Welcome Page**: text (Markdown) shown when no URL is set |
| Bookmarks | Sites for the "frequently used sites" menu, up to 8. Each row has a label and a URL; use **Add Bookmark**, ▲ / ▼ and ✕ to add, reorder and remove. If the label is empty the URL is shown |
| Frames | **Frame Urls**: pages a `<frameset>` may load. A frame is shown only when its URL is listed here, in the bookmarks or as the Home Url; other frames are listed by address |
| Scripts | The `JsEngine` that runs inline scripts (see below) |
| Appearance | Maximum image size, font size, padding and plain text color |
| Limits | Limits on lines, block length and HTML size |
| UI References | Objects inside the prefab. Only change these if you rebuild its UI |

The prefab ships without bookmarks, and the menu button stays hidden until you add one.

The button labels and the default Welcome Page are in Japanese; edit the prefab to change them.

## HTML and CSS

A page is drawn as a vertical flow of text blocks. Styles come from `<style>` elements and `style` attributes.

| | Supported |
|---|---|
| Text | `color`, `font-size`, `font-weight`, `font-style`, `font` (shorthand), `line-height`, `text-align`, `text-transform`, `text-indent`, `white-space` |
| Box | `background` / `background-color` (solid colors), `margin`, `padding` (all sides and the `-inline` / `-block` forms), `max-width`, `width` together with an `auto` margin |
| Lists | `list-style` / `list-style-type`: `disc`, `circle`, `square`, `decimal`, `decimal-leading-zero`, `lower-` / `upper-alpha`, `lower-` / `upper-roman`, `none` |
| Visibility | `display`, `visibility`, `opacity: 0`, the `hidden` attribute |
| Values | `px`, `em`, `rem`, `%`, `pt`, `vw`, `ch`; hex, `rgb()`, `hsl()` and named colors; `var(--name, fallback)` for custom properties set on `:root`, `html` or `body` |
| Selectors | tag, `.class`, `#id`, compounds, descendant combinations, `:root`, `:link`. Rules with other pseudo-classes or attribute selectors are skipped |
| At-rules | `@media` with `screen`, `all`, `min-width` and `max-width` (the page is about 800 CSS px wide with the default prefab); `@supports` and `@layer` blocks are read as plain rules |

Besides the usual headings, paragraphs, lists and inline text elements, these get their own treatment: `blockquote`, `dl` / `dd` and `figure` are indented; `ol` honours `start` and `type`, `li` honours `value`; `q` adds quotation marks; `ruby` shows the reading in parentheses; `details` / `summary`, `dialog open`, `caption`, `big`, `font size` and named and numeric character entities are handled; form controls (`input`, `button`) are drawn as text such as `[name]` or `☑` and cannot be used.

## JavaScript

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

## Limitations

- Links inside a page cannot be opened (Udon cannot build URLs at runtime). Type a URL or use a bookmark.
- No external stylesheets, inline HTML images or complex layout (floats, flexbox, grid, positioning, tables as grids). Borders, underlines and font families are not drawn. Images inside HTML are shown as `[画像: alt]`.
- Domains outside VRChat's allowlist only load for players who enabled "Allow Untrusted URLs".
- One load per roughly 5 seconds (VRChat limit).
- Images up to 2048×2048 (VRChat limit).

## License

[MIT](LICENSE.md)

---

## 日本語

VRChat ワールド用のインワールド・ページビューアです。URL を入力すると、テキスト / Markdown / 簡単な HTML / 画像をキャンバスに表示します。表示中のページはインスタンス内の全員に同期されます。

[ドキュメント](https://apps.nknighta.me/VRCbrowser/docs/) · [VCC に追加](https://apps.nknighta.me/VRCbrowser/install.html)

### 特長

- テキスト、Markdown、JSON、HTML、画像(`.png` / `.jpg`)を表示
- HTML はページ内に書かれた CSS の一部に対応(文字の装飾、余白、中央寄せや幅を絞った段組み、リスト、カスタムプロパティ、`@media`。詳細は下記)
- Shift_JIS / EUC-JP のページ(`charset=` の指定があるもの)を文字化けせずに表示。`<frameset>` のページは 1 つの文書としてまとめて表示
- 表示中の URL を全員に同期
- 戻る / ホーム / 更新ボタン、「よく使うサイト」メニュー(最大 8 件)
- ページ内の `<script>` を実行する簡易 JavaScript ランタイム(下記)
- UdonSharp スクリプト 2 本、プレハブ 1 つ、専用インスペクターだけで完結

### 必要環境

- Unity 2022.3
- VRChat SDK - Worlds 3.5.0 以降(動作確認は 3.10.5)

### 導入

#### VCC / ALCOM(VPM)

1. **[VCC に追加](https://apps.nknighta.me/VRCbrowser/install.html)** のページを開いてボタンを押すと、VCC または ALCOM が開き、リポジトリの追加を確認する画面が出ます。
   手動で追加する場合は、次の URL をリポジトリとして追加します。
   `https://raw.githubusercontent.com/nk4dev/VRCbrowser/main/index.json`
2. プロジェクトの管理画面で **VRCbrowser** を追加します。

#### unitypackage

[Releases](https://github.com/nk4dev/VRCbrowser/releases) から `VRCbrowser-<version>.unitypackage` をダウンロードしてインポートします。

### 使い方

1. `Packages/VRCbrowser/Runtime/Prefabs/VRCbrowser.prefab` をシーンに置きます。
2. `VRCbrowser` コンポーネントのインスペクターで設定します。

| セクション | 内容 |
|---|---|
| Pages | **Home Url**:ホームボタンと起動時に開くページ。空なら Welcome Page を表示。**Welcome Page**:URL 未指定のときに表示する文章(Markdown) |
| Bookmarks | 「よく使うサイト」メニューに並べるサイト(最大 8 件)。1 件ごとに表示名と URL を入力し、**Add Bookmark**、▲ / ▼、✕ で追加・並べ替え・削除します。表示名が空なら URL を表示 |
| Frames | **Frame Urls**:`<frameset>` のページが読み込めるフレームの URL。ここ、ブックマーク、Home Url のいずれかに登録された URL のフレームだけを表示し、それ以外は URL の一覧として表示します |
| Scripts | ページ内のスクリプトを実行する `JsEngine`(下記) |
| Appearance | 画像の最大サイズ、文字サイズ、余白、プレーンテキストの文字色 |
| Limits | 読み込む行数、ブロックの長さ、HTML サイズの上限 |
| UI References | プレハブ内のオブジェクトへの参照。UI を作り直す場合だけ変更します |

プレハブにブックマークは入っていません。1 件以上追加するまで、メニューのボタンは表示されません。

### HTML と CSS

ページは、テキストのブロックを縦に並べて描画します。スタイルは `<style>` 要素と `style` 属性から読み取ります。

| | 対応しているもの |
|---|---|
| 文字 | `color`、`font-size`、`font-weight`、`font-style`、`font`(一括指定)、`line-height`、`text-align`、`text-transform`、`text-indent`、`white-space` |
| ボックス | `background` / `background-color`(単色)、`margin`、`padding`(上下左右と `-inline` / `-block`)、`max-width`、`auto` のマージンと併用した `width` |
| リスト | `list-style` / `list-style-type`:`disc`、`circle`、`square`、`decimal`、`decimal-leading-zero`、`lower-` / `upper-alpha`、`lower-` / `upper-roman`、`none` |
| 表示・非表示 | `display`、`visibility`、`opacity: 0`、`hidden` 属性 |
| 値 | `px`、`em`、`rem`、`%`、`pt`、`vw`、`ch`。16 進、`rgb()`、`hsl()`、色名。`:root` / `html` / `body` に書いたカスタムプロパティを `var(--name, 代替値)` で参照 |
| セレクター | タグ、`.class`、`#id`、それらの組み合わせ、子孫結合、`:root`、`:link`。ほかの擬似クラスや属性セレクターを含むルールは無視します |
| @ ルール | `@media` の `screen`、`all`、`min-width`、`max-width`(既定のプレハブでページ幅は約 800 CSS px)。`@supports` と `@layer` の中身は通常のルールとして読みます |

見出し、段落、リスト、インラインの文字要素のほか、次の要素を個別に扱います。`blockquote`、`dl` / `dd`、`figure` は字下げします。`ol` は `start` と `type`、`li` は `value` に対応します。`q` は引用符を付け、`ruby` は読みを括弧書きで表示します。`details` / `summary`、`dialog open`、`caption`、`big`、`font size`、名前付き・数値の文字参照にも対応します。フォーム部品(`input`、`button`)は `[name]` や `☑` のような文字として表示され、操作はできません。

### JavaScript

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

### 制限

- ページ内のリンクは開けません(Udon は実行時に URL を作れないため)。URL を入力するか、ブックマークを使ってください。
- 外部スタイルシート、HTML 内の画像、複雑なレイアウト(float、flexbox、grid、position、表組み)には対応していません。枠線、下線、フォントの種類は描画されません。HTML 内の画像は `[画像: alt]` と表示されます。
- VRChat の許可リストにないドメインは、設定で「Allow Untrusted URLs」を有効にした人だけ読み込めます。
- 読み込みは約 5 秒に 1 回までです(VRChat の制限)。
- 画像は最大 2048×2048 です(VRChat の制限)。

### ライセンス

[MIT](LICENSE.md)
