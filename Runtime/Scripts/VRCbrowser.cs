using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDK3.Components;
using VRC.SDK3.Image;
using VRC.SDK3.StringLoading;
using VRC.SDKBase;
using VRC.Udon.Common.Interfaces;

/// <summary>
/// In-world page viewer: loads a text / Markdown / HTML page or an image from a URL and shows it on a canvas.
/// The current URL is synced, so everyone in the instance sees the same page.
/// Udon cannot build URLs at runtime, so navigation is limited to typed URLs, bookmarks and history.
///
/// HTML is rendered as a vertical flow of text blocks with a subset of CSS (style elements and style attributes only,
/// external stylesheets cannot be fetched): color, font, font-size, font-weight, font-style, line-height, text-align,
/// text-transform, text-indent, white-space, background(-color), margin, padding, width / max-width (for centred or
/// narrowed columns), list-style-type, display, visibility and opacity: 0, with custom properties (var()) and @media
/// by page width. Selectors: tag, .class, #id, compounds and descendant combinations.
/// </summary>
[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class VRCbrowser : UdonSharpBehaviour
{
    // UI
    [SerializeField] private VRCUrlInputField urlInput = null;
    [SerializeField] private Text statusText = null;
    [SerializeField] private ScrollRect scrollRect = null;
    [SerializeField] private Image pageBackground = null;
    [SerializeField] private RectTransform[] blockRects = null;
    [SerializeField] private Image[] blockImages = null;
    [SerializeField] private Text[] blockTexts = null;
    [SerializeField] private RawImage imageView = null;
    [SerializeField] private Vector2 imageMaxSize = new Vector2(1500f, 800f);

    // Pages
    [SerializeField] private VRCUrl homeUrl = VRCUrl.Empty;
    [SerializeField, TextArea(5, 20)] private string welcomePage = "";

    // Bookmarks
    // Sites listed in the menu, filled from the top (up to 8). Leave empty to hide the menu.
    [SerializeField] private VRCUrl[] bookmarkUrls = null;
    // Menu label for the bookmark at the same index. If empty, the URL is shown.
    [SerializeField] private string[] bookmarkLabels = null;

    // Bookmark Menu UI
    [SerializeField] private GameObject[] bookmarkButtons = null;
    [SerializeField] private Text[] bookmarkButtonLabels = null;
    [SerializeField] private GameObject menuButton = null; // opens the list of frequently used sites
    [SerializeField] private GameObject menuPanel = null;
    [SerializeField] private float menuRowHeight = 58f;

    // Appearance
    [SerializeField] private int baseFontSize = 30; // UI units for a 16px CSS font; must match the block Text components
    [SerializeField] private float sidePadding = 16f;
    [SerializeField] private Color plainTextColor = new Color(0.90f, 0.92f, 0.95f);

    // Limits
    [SerializeField] private int maxLines = 1500;
    [SerializeField] private int maxCharsPerBlock = 2500;
    [SerializeField] private int maxHtmlChars = 60000;
    [SerializeField] private int maxHtmlTags = 3000;

    // Frames
    // Pages a <frameset> may pull in. Udon cannot build URLs at runtime, so a frame loads only when its URL is listed
    // here (bookmarks and the home URL count too).
    [SerializeField] private VRCUrl[] frameUrls = null;

    // Scripts
    [SerializeField] private VRCbrowserJs js = null; // optional: runs inline <script> elements; leave empty to ignore scripts

    [UdonSynced] private VRCUrl syncedUrl = VRCUrl.Empty;

    private string loadedUrl = "";
    private VRCUrl[] history = null;
    private int historyCount = 0;
    private VRCImageDownloader imageDownloader = null;

    // frames of the current <frameset> page, loaded one after another and rendered as one document
    private VRCUrl[] framePending = null;
    private int frameTotal = 0;
    private int frameIndex = 0;
    private string frameHtml = "";
    private string frameTitle = "";

    private void Start()
    {
        history = new VRCUrl[16];
        framePending = new VRCUrl[8];
        InitRenderer();

        int bookmarkCount = bookmarkUrls == null ? 0 : bookmarkUrls.Length;
        int usedCount = 0;
        for (int i = 0; i < bookmarkButtons.Length; i++)
        {
            bool used = i < bookmarkCount && bookmarkUrls[i] != null && bookmarkUrls[i].Get().Length > 0;
            bookmarkButtons[i].SetActive(used);
            if (!used) continue;
            usedCount++;
            bool hasLabel = bookmarkLabels != null && i < bookmarkLabels.Length && bookmarkLabels[i].Length > 0;
            bookmarkButtonLabels[i].text = hasLabel ? bookmarkLabels[i] : bookmarkUrls[i].Get();
        }
        menuButton.SetActive(usedCount > 0);
        menuPanel.SetActive(false);
        // shrink the panel to the rows in use (entries are expected to be filled from the top)
        RectTransform panelRect = menuPanel.GetComponent<RectTransform>();
        panelRect.sizeDelta = new Vector2(panelRect.sizeDelta.x, usedCount * menuRowHeight + 20f);

        if (loadedUrl.Length > 0) return; // a synced page already arrived

        if (Networking.IsOwner(gameObject) && HasUrl(homeUrl)) Navigate(homeUrl, false);
        else ShowWelcome();
    }

    private void OnDestroy()
    {
        if (imageDownloader != null) imageDownloader.Dispose();
    }

    // ---------- UI events ----------

    public void OnUrlEntered()
    {
        VRCUrl url = urlInput.GetUrl();
        if (!HasUrl(url) || url.Get() == loadedUrl) return;
        Navigate(url, true);
    }

    public void GoBack()
    {
        if (historyCount == 0) return;
        historyCount--;
        VRCUrl url = history[historyCount];
        history[historyCount] = null;
        Navigate(url, false);
    }

    public void GoHome()
    {
        if (HasUrl(homeUrl)) Navigate(homeUrl, true);
        else Navigate(VRCUrl.Empty, true);
    }

    public void Reload()
    {
        if (loadedUrl.Length == 0) ShowWelcome();
        else Load();
    }

    public void ToggleMenu()
    {
        menuPanel.SetActive(!menuPanel.activeSelf);
    }

    public void Bookmark0() { OpenBookmark(0); }
    public void Bookmark1() { OpenBookmark(1); }
    public void Bookmark2() { OpenBookmark(2); }
    public void Bookmark3() { OpenBookmark(3); }
    public void Bookmark4() { OpenBookmark(4); }
    public void Bookmark5() { OpenBookmark(5); }
    public void Bookmark6() { OpenBookmark(6); }
    public void Bookmark7() { OpenBookmark(7); }

    private void OpenBookmark(int index)
    {
        menuPanel.SetActive(false);
        if (bookmarkUrls == null || index >= bookmarkUrls.Length || !HasUrl(bookmarkUrls[index])) return;
        Navigate(bookmarkUrls[index], true);
    }

    // ---------- navigation / sync ----------

    private bool HasUrl(VRCUrl url)
    {
        return url != null && url.Get().Length > 0;
    }

    private void Navigate(VRCUrl url, bool pushHistory)
    {
        if (pushHistory && HasUrl(syncedUrl))
        {
            if (historyCount == history.Length)
            {
                for (int i = 1; i < history.Length; i++) history[i - 1] = history[i];
                historyCount--;
            }
            history[historyCount] = syncedUrl;
            historyCount++;
        }

        if (!Networking.IsOwner(gameObject)) Networking.SetOwner(Networking.LocalPlayer, gameObject);
        syncedUrl = url;
        RequestSerialization();
        Load();
    }

    public override void OnDeserialization()
    {
        string url = syncedUrl == null ? "" : syncedUrl.Get();
        if (url != loadedUrl) Load();
    }

    private void Load()
    {
        if (js != null) js._Stop();
        frameTotal = 0;
        frameIndex = 0;
        loadedUrl = syncedUrl == null ? "" : syncedUrl.Get();
        if (loadedUrl.Length == 0)
        {
            ShowWelcome();
            return;
        }

        urlInput.SetUrl(syncedUrl);
        statusText.text = "読み込み中…";

        if (IsImageUrl(loadedUrl))
        {
            if (imageDownloader != null) imageDownloader.Dispose();
            imageDownloader = new VRCImageDownloader();
            imageDownloader.DownloadImage(syncedUrl, null, (IUdonEventReceiver)this);
        }
        else
        {
            VRCStringDownloader.LoadUrl(syncedUrl, (IUdonEventReceiver)this);
        }
    }

    private bool IsImageUrl(string url)
    {
        string path = url.ToLower();
        int query = path.IndexOf('?');
        if (query >= 0) path = path.Substring(0, query);
        return path.EndsWith(".png") || path.EndsWith(".jpg") || path.EndsWith(".jpeg");
    }

    // ---------- download callbacks ----------

    public override void OnStringLoadSuccess(IVRCStringDownload result)
    {
        string url = result.Url.Get();
        if (frameIndex < frameTotal && url == framePending[frameIndex].Get())
        {
            AppendFrame(DecodeBody(result));
            NextFrame();
            return;
        }
        if (url != loadedUrl) return; // a newer page was requested meanwhile
        string body = DecodeBody(result);
        statusText.text = "";
        if (LooksLikeHtml(body)) ShowHtml(body);
        else ShowText(body);
    }

    public override void OnStringLoadError(IVRCStringDownload result)
    {
        string url = result.Url.Get();
        if (frameIndex < frameTotal && url == framePending[frameIndex].Get())
        {
            frameHtml = frameHtml + "<p><font color=\"#888888\">(フレームを読み込めませんでした: " + url + ")</font></p><hr>";
            NextFrame();
            return;
        }
        if (url != loadedUrl) return;
        ShowText("# 読み込めませんでした\n" + result.Error);
        statusText.text = "エラー " + result.ErrorCode;
    }

    public override void OnImageLoadSuccess(IVRCImageDownload result)
    {
        if (result.Url.Get() != loadedUrl) return;
        Texture2D texture = result.Result;
        float scale = Mathf.Min(imageMaxSize.x / texture.width, imageMaxSize.y / texture.height);
        imageView.texture = texture;
        imageView.rectTransform.sizeDelta = new Vector2(texture.width * scale, texture.height * scale);
        imageView.gameObject.SetActive(true);
        scrollRect.gameObject.SetActive(false);
        statusText.text = texture.width + " x " + texture.height;
    }

    public override void OnImageLoadError(IVRCImageDownload result)
    {
        if (result.Url.Get() != loadedUrl) return;
        ShowText("# 画像を読み込めませんでした\n" + result.ErrorMessage);
        statusText.text = "エラー";
    }

    private void ShowWelcome()
    {
        if (js != null) js._Stop();
        frameTotal = 0;
        frameIndex = 0;
        loadedUrl = "";
        urlInput.SetUrl(VRCUrl.Empty);
        ShowText(welcomePage);
        statusText.text = "";
    }

    // =====================================================================
    // Block renderer: the page is a vertical stack of text blocks, each with
    // its own alignment, background, padding and gap to the previous block.
    // =====================================================================

    private const int AlignLeft = 0;
    private const int AlignCenter = 1;
    private const int AlignRight = 2;

    private int blockCount = 0;
    private bool blocksTruncated = false;
    private string[] bText = null;
    private int[] bAlign = null;
    private string[] bBg = null;
    private int[] bOwner = null;
    private float[] bPadL = null;
    private float[] bPadR = null;
    private float[] bPadTop = null;
    private float[] bPadBottom = null;
    private float[] bGap = null;
    private float[] bIndentL = null;
    private float[] bIndentR = null;
    private float[] bLine = null;
    private float unitsPerPx = 1.875f;
    private float pagePx = 800f; // width of the page in CSS px

    private void InitRenderer()
    {
        int n = blockTexts.Length;
        bText = new string[n];
        bAlign = new int[n];
        bBg = new string[n];
        bOwner = new int[n];
        bPadL = new float[n];
        bPadR = new float[n];
        bPadTop = new float[n];
        bPadBottom = new float[n];
        bGap = new float[n];
        bIndentL = new float[n];
        bIndentR = new float[n];
        bLine = new float[n];
        unitsPerPx = baseFontSize / 16f;
        float contentWidth = scrollRect.content.rect.width;
        if (contentWidth <= 0f) contentWidth = 1600f;
        pagePx = (contentWidth - 2f * sidePadding) / unitsPerPx;
        InitHtml();
    }

    private void BeginBlocks()
    {
        blockCount = 0;
        blocksTruncated = false;
    }

    /// <summary>
    /// Adds a paragraph. Sizes are in CSS px. owner: id of the element whose background box it sits in (0 = page).
    /// indentL / indentR move the block's edges in, padL / padR pad the text inside it. line: line spacing, 1 = normal.
    /// </summary>
    private void AddBlock(string text, int align, string bg, int owner, float padL, float padR, float padV, float gap, float indentL, float indentR, float line)
    {
        padL *= unitsPerPx;
        padR *= unitsPerPx;
        indentL *= unitsPerPx;
        indentR *= unitsPerPx;
        if (blockCount > 0)
        {
            int last = blockCount - 1;
            if (bOwner[last] == owner && bAlign[last] == align && bBg[last] == bg && bText[last].Length + text.Length < maxCharsPerBlock
                && bIndentL[last] == indentL && bIndentR[last] == indentR && bPadL[last] == padL && bPadR[last] == padR && bLine[last] == line)
            {
                bText[last] = bText[last] + "\n" + Spacer(gap) + text;
                return;
            }
        }
        if (blockCount >= bText.Length)
        {
            blocksTruncated = true;
            return;
        }

        bText[blockCount] = text;
        bAlign[blockCount] = align;
        bBg[blockCount] = bg;
        bOwner[blockCount] = owner;
        bPadL[blockCount] = padL;
        bPadR[blockCount] = padR;
        bIndentL[blockCount] = indentL;
        bIndentR[blockCount] = indentR;
        bLine[blockCount] = line;
        bPadBottom[blockCount] = padV * unitsPerPx;
        if (blockCount > 0 && owner != 0 && bOwner[blockCount - 1] == owner)
        {
            // still inside the same background box: keep the box visually continuous
            bPadTop[blockCount] = gap * unitsPerPx;
            bGap[blockCount] = 0f;
        }
        else
        {
            bPadTop[blockCount] = padV * unitsPerPx;
            bGap[blockCount] = blockCount == 0 ? 0f : gap * unitsPerPx;
        }
        blockCount++;
    }

    /// <summary>
    /// Margin between paragraphs that share one Text. A line is never shorter than the base font's line height,
    /// so the gap is rounded to whole blank lines.
    /// </summary>
    private string Spacer(float gapPx)
    {
        int lines = Mathf.Min(3, Mathf.RoundToInt(gapPx * unitsPerPx / (baseFontSize * 1.15f)));
        if (lines <= 0) return "";
        if (lines == 1) return "\n";
        return lines == 2 ? "\n\n" : "\n\n\n";
    }

    private void LayoutBlocks(string pageBgHex, Color textColor)
    {
        imageView.gameObject.SetActive(false);
        scrollRect.gameObject.SetActive(true);

        if (blocksTruncated && blockCount > 0) bText[blockCount - 1] = bText[blockCount - 1] + "\n<color=#888888>…(以降は省略)</color>";

        pageBackground.color = pageBgHex.Length == 0 ? new Color(0f, 0f, 0f, 0f) : HexToColor(pageBgHex);

        float y = 12f;
        for (int i = 0; i < blockRects.Length; i++)
        {
            RectTransform rect = blockRects[i];
            if (i >= blockCount)
            {
                rect.gameObject.SetActive(false);
                continue;
            }
            rect.gameObject.SetActive(true);

            Text text = blockTexts[i];
            text.text = bText[i];
            text.color = textColor;
            text.alignment = bAlign[i] == AlignCenter ? TextAnchor.UpperCenter : (bAlign[i] == AlignRight ? TextAnchor.UpperRight : TextAnchor.UpperLeft);
            text.lineSpacing = bLine[i];
            RectTransform textRect = text.rectTransform;
            textRect.offsetMin = new Vector2(bPadL[i], bPadBottom[i]);
            textRect.offsetMax = new Vector2(-bPadR[i], -bPadTop[i]);

            // width first, so the text can report the height it needs at that width
            float inset = 2f * sidePadding + bIndentL[i] + bIndentR[i];
            rect.sizeDelta = new Vector2(-inset, 100f);
            float height = text.preferredHeight + bPadTop[i] + bPadBottom[i];
            y += bGap[i];
            rect.anchoredPosition = new Vector2((bIndentL[i] - bIndentR[i]) * 0.5f, -y);
            rect.sizeDelta = new Vector2(-inset, height);
            y += height;

            blockImages[i].color = bBg[i].Length == 0 ? new Color(0f, 0f, 0f, 0f) : HexToColor(bBg[i]);
        }

        RectTransform content = scrollRect.content;
        content.sizeDelta = new Vector2(0f, y + 24f);
        content.anchoredPosition = Vector2.zero;
    }

    // ---------- plain text / Markdown ----------

    private void ShowText(string source)
    {
        BeginBlocks();
        string[] lines = source.Replace("\r", "").Split('\n');
        int lineCount = Mathf.Min(lines.Length, maxLines);
        bool inCode = false;

        for (int i = 0; i < lineCount; i++)
        {
            string line = lines[i];
            if (line.StartsWith("```"))
            {
                inCode = !inCode;
                continue;
            }
            line = inCode ? "<color=#9FD6A0>" + line + "</color>" : FormatMarkdownLine(line);
            AddBlock(line, AlignLeft, "", 0, 0f, 0f, 0f, 0f, 0f, 0f, 1f);
            if (blocksTruncated) break;
        }
        if (lines.Length > maxLines) blocksTruncated = true;

        LayoutBlocks("", plainTextColor);
    }

    private string FormatMarkdownLine(string line)
    {
        string trimmed = line.Trim();
        if (trimmed.Length == 0) return "";
        if (trimmed == "---" || trimmed == "***" || trimmed == "___") return "<color=#666666>────────────────────────</color>";

        if (line.StartsWith("### ")) return "<size=34><b>" + FormatInline(line.Substring(4)) + "</b></size>";
        if (line.StartsWith("## ")) return "<size=42><b>" + FormatInline(line.Substring(3)) + "</b></size>";
        if (line.StartsWith("# ")) return "<size=52><b>" + FormatInline(line.Substring(2)) + "</b></size>";
        if (line.StartsWith("> ")) return "<color=#9AA7B5><i>" + FormatInline(line.Substring(2)) + "</i></color>";

        string indent = line.Substring(0, line.Length - line.TrimStart().Length);
        if (trimmed.StartsWith("- ") || trimmed.StartsWith("* ") || trimmed.StartsWith("+ "))
            return indent + "・" + FormatInline(trimmed.Substring(2));

        return FormatInline(line);
    }

    private string FormatInline(string text)
    {
        // [label](url) -> highlighted label (links cannot be followed from Udon)
        for (int guard = 0; guard < 20; guard++)
        {
            int mid = text.IndexOf("](");
            if (mid < 0) break;
            int open = text.LastIndexOf("[", mid);
            int close = text.IndexOf(")", mid);
            if (open < 0 || close < 0) break;
            text = text.Substring(0, open) + "<color=#7FB4FF>" + text.Substring(open + 1, mid - open - 1) + "</color>" + text.Substring(close + 1);
        }

        text = ReplacePairs(text, "**", "<b>", "</b>");
        text = ReplacePairs(text, "`", "<color=#9FD6A0>", "</color>");
        return text;
    }

    private string ReplacePairs(string text, string marker, string openTag, string closeTag)
    {
        for (int guard = 0; guard < 20; guard++)
        {
            int first = text.IndexOf(marker);
            if (first < 0) break;
            int second = text.IndexOf(marker, first + marker.Length);
            if (second < 0) break;
            text = text.Substring(0, first) + openTag + text.Substring(first + marker.Length, second - first - marker.Length) + closeTag + text.Substring(second + marker.Length);
        }
        return text;
    }

    // =====================================================================
    // HTML + CSS subset
    // =====================================================================

    private const int MaxDepth = 64;
    private const int MaxRules = 256;
    private const float Invalid = -99999f;

    private const string BlockTags = " html body div p h1 h2 h3 h4 h5 h6 ul ol li section article header footer main nav aside blockquote pre table thead tbody tfoot tr hr figure figcaption form fieldset dl dt dd address details summary center caption legend menu dir hgroup search dialog xmp listing plaintext ";
    private const string VoidTags = " br hr img input meta link area base col embed source track wbr ";
    // Shift_JIS (CP932) double-byte characters: 60 lead bytes (81-9F, E0-FC) x 188 trail bytes (40-7E, 80-FC); '?' = unassigned
    private const string Cp932Table = "　、。，．・：；？！゛゜´｀¨＾￣＿ヽヾゝゞ〃仝々〆〇ー―‐／＼～∥｜…‥‘’“”（）〔〕［］｛｝〈〉《》「」『』【】＋－±×÷＝≠＜＞≦≧∞∴♂♀°′″℃￥＄￠￡％＃＆＊＠§☆★○●◎◇◆□■△▲▽▼※〒→←↑↓〓???????????∈∋⊆⊇⊂⊃∪∩????????∧∨￢⇒⇔∀∃???????????∠⊥⌒∂∇≡≒≪≫√∽∝∵∫∬???????Å‰♯♭♪†‡¶????◯???????????????０１２３４５６７８９???????ＡＢＣＤＥＦＧＨＩＪＫＬＭＮＯＰＱＲＳＴＵＶＷＸＹＺ??????ａｂｃｄｅｆｇｈｉｊｋｌｍｎｏｐｑｒｓｔｕｖｗｘｙｚ????ぁあぃいぅうぇえぉおかがきぎくぐけげこごさざしじすずせぜそぞただちぢっつづてでとどなにぬねのはばぱひびぴふぶぷへべぺほぼぽまみむめもゃやゅゆょよらりるれろゎわゐゑをん???????????ァアィイゥウェエォオカガキギクグケゲコゴサザシジスズセゼソゾタダチヂッツヅテデトドナニヌネノハバパヒビピフブプヘベペホボポマミムメモャヤュユョヨラリルレロヮワヰヱヲンヴヵヶ????????ΑΒΓΔΕΖΗΘΙΚΛΜΝΞΟΠΡΣΤΥΦΧΨΩ????????αβγδεζηθικλμνξοπρστυφχψω??????????????????????????????????????АБВГДЕЁЖЗИЙКЛМНОПРСТУФХЦЧШЩЪЫЬЭЮЯ???????????????абвгдеёжзийклмнопрстуфхцчшщъыьэюя?????????????─│┌┐┘└├┬┤┴┼━┃┏┓┛┗┣┳┫┻╋┠┯┨┷┿┝┰┥┸╂??????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????①②③④⑤⑥⑦⑧⑨⑩⑪⑫⑬⑭⑮⑯⑰⑱⑲⑳ⅠⅡⅢⅣⅤⅥⅦⅧⅨⅩ?㍉㌔㌢㍍㌘㌧㌃㌶㍑㍗㌍㌦㌣㌫㍊㌻㎜㎝㎞㎎㎏㏄㎡????????㍻〝〟№㏍℡㊤㊥㊦㊧㊨㈱㈲㈹㍾㍽㍼≒≡∫∮∑√⊥∠∟⊿∵∩∪??????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????亜唖娃阿哀愛挨姶逢葵茜穐悪握渥旭葦芦鯵梓圧斡扱宛姐虻飴絢綾鮎或粟袷安庵按暗案闇鞍杏以伊位依偉囲夷委威尉惟意慰易椅為畏異移維緯胃萎衣謂違遺医井亥域育郁磯一壱溢逸稲茨芋鰯允印咽員因姻引飲淫胤蔭院陰隠韻吋右宇烏羽迂雨卯鵜窺丑碓臼渦嘘唄欝蔚鰻姥厩浦瓜閏噂云運雲荏餌叡営嬰影映曳栄永泳洩瑛盈穎頴英衛詠鋭液疫益駅悦謁越閲榎厭円園堰奄宴延怨掩援沿演炎焔煙燕猿縁艶苑薗遠鉛鴛塩於汚甥凹央奥往応押旺横欧殴王翁襖鴬鴎黄岡沖荻億屋憶臆桶牡乙俺卸恩温穏音下化仮何伽価佳加可嘉夏嫁家寡科暇果架歌河火珂禍禾稼箇花苛茄荷華菓蝦課嘩貨迦過霞蚊俄峨我牙画臥芽蛾賀雅餓駕介会解回塊壊廻快怪悔恢懐戒拐改魁晦械海灰界皆絵芥蟹開階貝凱劾外咳害崖慨概涯碍蓋街該鎧骸浬馨蛙垣柿蛎鈎劃嚇各廓拡撹格核殻獲確穫覚角赫較郭閣隔革学岳楽額顎掛笠樫橿梶鰍潟割喝恰括活渇滑葛褐轄且鰹叶椛樺鞄株兜竃蒲釜鎌噛鴨栢茅萱粥刈苅瓦乾侃冠寒刊勘勧巻喚堪姦完官寛干幹患感慣憾換敢柑桓棺款歓汗漢澗潅環甘監看竿管簡緩缶翰肝艦莞観諌貫還鑑間閑関陥韓館舘丸含岸巌玩癌眼岩翫贋雁頑顔願企伎危喜器基奇嬉寄岐希幾忌揮机旗既期棋棄機帰毅気汽畿祈季稀紀徽規記貴起軌輝飢騎鬼亀偽儀妓宜戯技擬欺犠疑祇義蟻誼議掬菊鞠吉吃喫桔橘詰砧杵黍却客脚虐逆丘久仇休及吸宮弓急救朽求汲泣灸球究窮笈級糾給旧牛去居巨拒拠挙渠虚許距鋸漁禦魚亨享京供侠僑兇競共凶協匡卿叫喬境峡強彊怯恐恭挟教橋況狂狭矯胸脅興蕎郷鏡響饗驚仰凝尭暁業局曲極玉桐粁僅勤均巾錦斤欣欽琴禁禽筋緊芹菌衿襟謹近金吟銀九倶句区狗玖矩苦躯駆駈駒具愚虞喰空偶寓遇隅串櫛釧屑屈掘窟沓靴轡窪熊隈粂栗繰桑鍬勲君薫訓群軍郡卦袈祁係傾刑兄啓圭珪型契形径恵慶慧憩掲携敬景桂渓畦稽系経継繋罫茎荊蛍計詣警軽頚鶏芸迎鯨劇戟撃激隙桁傑欠決潔穴結血訣月件倹倦健兼券剣喧圏堅嫌建憲懸拳捲検権牽犬献研硯絹県肩見謙賢軒遣鍵険顕験鹸元原厳幻弦減源玄現絃舷言諺限乎個古呼固姑孤己庫弧戸故枯湖狐糊袴股胡菰虎誇跨鈷雇顧鼓五互伍午呉吾娯後御悟梧檎瑚碁語誤護醐乞鯉交佼侯候倖光公功効勾厚口向后喉坑垢好孔孝宏工巧巷幸広庚康弘恒慌抗拘控攻昂晃更杭校梗構江洪浩港溝甲皇硬稿糠紅紘絞綱耕考肯肱腔膏航荒行衡講貢購郊酵鉱砿鋼閤降項香高鴻剛劫号合壕拷濠豪轟麹克刻告国穀酷鵠黒獄漉腰甑忽惚骨狛込此頃今困坤墾婚恨懇昏昆根梱混痕紺艮魂些佐叉唆嵯左差査沙瑳砂詐鎖裟坐座挫債催再最哉塞妻宰彩才採栽歳済災采犀砕砦祭斎細菜裁載際剤在材罪財冴坂阪堺榊肴咲崎埼碕鷺作削咋搾昨朔柵窄策索錯桜鮭笹匙冊刷察拶撮擦札殺薩雑皐鯖捌錆鮫皿晒三傘参山惨撒散桟燦珊産算纂蚕讃賛酸餐斬暫残仕仔伺使刺司史嗣四士始姉姿子屍市師志思指支孜斯施旨枝止死氏獅祉私糸紙紫肢脂至視詞詩試誌諮資賜雌飼歯事似侍児字寺慈持時次滋治爾璽痔磁示而耳自蒔辞汐鹿式識鴫竺軸宍雫七叱執失嫉室悉湿漆疾質実蔀篠偲柴芝屡蕊縞舎写射捨赦斜煮社紗者謝車遮蛇邪借勺尺杓灼爵酌釈錫若寂弱惹主取守手朱殊狩珠種腫趣酒首儒受呪寿授樹綬需囚収周宗就州修愁拾洲秀秋終繍習臭舟蒐衆襲讐蹴輯週酋酬集醜什住充十従戎柔汁渋獣縦重銃叔夙宿淑祝縮粛塾熟出術述俊峻春瞬竣舜駿准循旬楯殉淳準潤盾純巡遵醇順処初所暑曙渚庶緒署書薯藷諸助叙女序徐恕鋤除傷償勝匠升召哨商唱嘗奨妾娼宵将小少尚庄床廠彰承抄招掌捷昇昌昭晶松梢樟樵沼消渉湘焼焦照症省硝礁祥称章笑粧紹肖菖蒋蕉衝裳訟証詔詳象賞醤鉦鍾鐘障鞘上丈丞乗冗剰城場壌嬢常情擾条杖浄状畳穣蒸譲醸錠嘱埴飾拭植殖燭織職色触食蝕辱尻伸信侵唇娠寝審心慎振新晋森榛浸深申疹真神秦紳臣芯薪親診身辛進針震人仁刃塵壬尋甚尽腎訊迅陣靭笥諏須酢図厨逗吹垂帥推水炊睡粋翠衰遂酔錐錘随瑞髄崇嵩数枢趨雛据杉椙菅頗雀裾澄摺寸世瀬畝是凄制勢姓征性成政整星晴棲栖正清牲生盛精聖声製西誠誓請逝醒青静斉税脆隻席惜戚斥昔析石積籍績脊責赤跡蹟碩切拙接摂折設窃節説雪絶舌蝉仙先千占宣専尖川戦扇撰栓栴泉浅洗染潜煎煽旋穿箭線繊羨腺舛船薦詮賎践選遷銭銑閃鮮前善漸然全禅繕膳糎噌塑岨措曾曽楚狙疏疎礎祖租粗素組蘇訴阻遡鼠僧創双叢倉喪壮奏爽宋層匝惣想捜掃挿掻操早曹巣槍槽漕燥争痩相窓糟総綜聡草荘葬蒼藻装走送遭鎗霜騒像増憎臓蔵贈造促側則即息捉束測足速俗属賊族続卒袖其揃存孫尊損村遜他多太汰詑唾堕妥惰打柁舵楕陀駄騨体堆対耐岱帯待怠態戴替泰滞胎腿苔袋貸退逮隊黛鯛代台大第醍題鷹滝瀧卓啄宅托択拓沢濯琢託鐸濁諾茸凧蛸只叩但達辰奪脱巽竪辿棚谷狸鱈樽誰丹単嘆坦担探旦歎淡湛炭短端箪綻耽胆蛋誕鍛団壇弾断暖檀段男談値知地弛恥智池痴稚置致蜘遅馳築畜竹筑蓄逐秩窒茶嫡着中仲宙忠抽昼柱注虫衷註酎鋳駐樗瀦猪苧著貯丁兆凋喋寵帖帳庁弔張彫徴懲挑暢朝潮牒町眺聴脹腸蝶調諜超跳銚長頂鳥勅捗直朕沈珍賃鎮陳津墜椎槌追鎚痛通塚栂掴槻佃漬柘辻蔦綴鍔椿潰坪壷嬬紬爪吊釣鶴亭低停偵剃貞呈堤定帝底庭廷弟悌抵挺提梯汀碇禎程締艇訂諦蹄逓邸鄭釘鼎泥摘擢敵滴的笛適鏑溺哲徹撤轍迭鉄典填天展店添纏甜貼転顛点伝殿澱田電兎吐堵塗妬屠徒斗杜渡登菟賭途都鍍砥砺努度土奴怒倒党冬凍刀唐塔塘套宕島嶋悼投搭東桃梼棟盗淘湯涛灯燈当痘祷等答筒糖統到董蕩藤討謄豆踏逃透鐙陶頭騰闘働動同堂導憧撞洞瞳童胴萄道銅峠鴇匿得徳涜特督禿篤毒独読栃橡凸突椴届鳶苫寅酉瀞噸屯惇敦沌豚遁頓呑曇鈍奈那内乍凪薙謎灘捺鍋楢馴縄畷南楠軟難汝二尼弐迩匂賑肉虹廿日乳入如尿韮任妊忍認濡禰祢寧葱猫熱年念捻撚燃粘乃廼之埜嚢悩濃納能脳膿農覗蚤巴把播覇杷波派琶破婆罵芭馬俳廃拝排敗杯盃牌背肺輩配倍培媒梅楳煤狽買売賠陪這蝿秤矧萩伯剥博拍柏泊白箔粕舶薄迫曝漠爆縛莫駁麦函箱硲箸肇筈櫨幡肌畑畠八鉢溌発醗髪伐罰抜筏閥鳩噺塙蛤隼伴判半反叛帆搬斑板氾汎版犯班畔繁般藩販範釆煩頒飯挽晩番盤磐蕃蛮匪卑否妃庇彼悲扉批披斐比泌疲皮碑秘緋罷肥被誹費避非飛樋簸備尾微枇毘琵眉美鼻柊稗匹疋髭彦膝菱肘弼必畢筆逼桧姫媛紐百謬俵彪標氷漂瓢票表評豹廟描病秒苗錨鋲蒜蛭鰭品彬斌浜瀕貧賓頻敏瓶不付埠夫婦富冨布府怖扶敷斧普浮父符腐膚芙譜負賦赴阜附侮撫武舞葡蕪部封楓風葺蕗伏副復幅服福腹複覆淵弗払沸仏物鮒分吻噴墳憤扮焚奮粉糞紛雰文聞丙併兵塀幣平弊柄並蔽閉陛米頁僻壁癖碧別瞥蔑箆偏変片篇編辺返遍便勉娩弁鞭保舗鋪圃捕歩甫補輔穂募墓慕戊暮母簿菩倣俸包呆報奉宝峰峯崩庖抱捧放方朋法泡烹砲縫胞芳萌蓬蜂褒訪豊邦鋒飽鳳鵬乏亡傍剖坊妨帽忘忙房暴望某棒冒紡肪膨謀貌貿鉾防吠頬北僕卜墨撲朴牧睦穆釦勃没殆堀幌奔本翻凡盆摩磨魔麻埋妹昧枚毎哩槙幕膜枕鮪柾鱒桝亦俣又抹末沫迄侭繭麿万慢満漫蔓味未魅巳箕岬密蜜湊蓑稔脈妙粍民眠務夢無牟矛霧鵡椋婿娘冥名命明盟迷銘鳴姪牝滅免棉綿緬面麺摸模茂妄孟毛猛盲網耗蒙儲木黙目杢勿餅尤戻籾貰問悶紋門匁也冶夜爺耶野弥矢厄役約薬訳躍靖柳薮鑓愉愈油癒諭輸唯佑優勇友宥幽悠憂揖有柚湧涌猶猷由祐裕誘遊邑郵雄融夕予余与誉輿預傭幼妖容庸揚揺擁曜楊様洋溶熔用窯羊耀葉蓉要謡踊遥陽養慾抑欲沃浴翌翼淀羅螺裸来莱頼雷洛絡落酪乱卵嵐欄濫藍蘭覧利吏履李梨理璃痢裏裡里離陸律率立葎掠略劉流溜琉留硫粒隆竜龍侶慮旅虜了亮僚両凌寮料梁涼猟療瞭稜糧良諒遼量陵領力緑倫厘林淋燐琳臨輪隣鱗麟瑠塁涙累類令伶例冷励嶺怜玲礼苓鈴隷零霊麗齢暦歴列劣烈裂廉恋憐漣煉簾練聯蓮連錬呂魯櫓炉賂路露労婁廊弄朗楼榔浪漏牢狼篭老聾蝋郎六麓禄肋録論倭和話歪賄脇惑枠鷲亙亘鰐詫藁蕨椀湾碗腕???????????????????????????????????????????弌丐丕个丱丶丼丿乂乖乘亂亅豫亊舒弍于亞亟亠亢亰亳亶从仍仄仆仂仗仞仭仟价伉佚估佛佝佗佇佶侈侏侘佻佩佰侑佯來侖儘俔俟俎俘俛俑俚俐俤俥倚倨倔倪倥倅伜俶倡倩倬俾俯們倆偃假會偕偐偈做偖偬偸傀傚傅傴傲僉僊傳僂僖僞僥僭僣僮價僵儉儁儂儖儕儔儚儡儺儷儼儻儿兀兒兌兔兢竸兩兪兮冀冂囘册冉冏冑冓冕冖冤冦冢冩冪冫决冱冲冰况冽凅凉凛几處凩凭凰凵凾刄刋刔刎刧刪刮刳刹剏剄剋剌剞剔剪剴剩剳剿剽劍劔劒剱劈劑辨辧劬劭劼劵勁勍勗勞勣勦飭勠勳勵勸勹匆匈甸匍匐匏匕匚匣匯匱匳匸區卆卅丗卉卍凖卞卩卮夘卻卷厂厖厠厦厥厮厰厶參簒雙叟曼燮叮叨叭叺吁吽呀听吭吼吮吶吩吝呎咏呵咎呟呱呷呰咒呻咀呶咄咐咆哇咢咸咥咬哄哈咨咫哂咤咾咼哘哥哦唏唔哽哮哭哺哢唹啀啣啌售啜啅啖啗唸唳啝喙喀咯喊喟啻啾喘喞單啼喃喩喇喨嗚嗅嗟嗄嗜嗤嗔嘔嗷嘖嗾嗽嘛嗹噎噐營嘴嘶嘲嘸噫噤嘯噬噪嚆嚀嚊嚠嚔嚏嚥嚮嚶嚴囂嚼囁囃囀囈囎囑囓囗囮囹圀囿圄圉圈國圍圓團圖嗇圜圦圷圸坎圻址坏坩埀垈坡坿垉垓垠垳垤垪垰埃埆埔埒埓堊埖埣堋堙堝塲堡塢塋塰毀塒堽塹墅墹墟墫墺壞墻墸墮壅壓壑壗壙壘壥壜壤壟壯壺壹壻壼壽夂夊夐夛梦夥夬夭夲夸夾竒奕奐奎奚奘奢奠奧奬奩奸妁妝佞侫妣妲姆姨姜妍姙姚娥娟娑娜娉娚婀婬婉娵娶婢婪媚媼媾嫋嫂媽嫣嫗嫦嫩嫖嫺嫻嬌嬋嬖嬲嫐嬪嬶嬾孃孅孀孑孕孚孛孥孩孰孳孵學斈孺宀它宦宸寃寇寉寔寐寤實寢寞寥寫寰寶寳尅將專對尓尠尢尨尸尹屁屆屎屓屐屏孱屬屮乢屶屹岌岑岔妛岫岻岶岼岷峅岾峇峙峩峽峺峭嶌峪崋崕崗嵜崟崛崑崔崢崚崙崘嵌嵒嵎嵋嵬嵳嵶嶇嶄嶂嶢嶝嶬嶮嶽嶐嶷嶼巉巍巓巒巖巛巫已巵帋帚帙帑帛帶帷幄幃幀幎幗幔幟幢幤幇幵并幺麼广庠廁廂廈廐廏廖廣廝廚廛廢廡廨廩廬廱廳廰廴廸廾弃弉彝彜弋弑弖弩弭弸彁彈彌彎弯彑彖彗彙彡彭彳彷徃徂彿徊很徑徇從徙徘徠徨徭徼忖忻忤忸忱忝悳忿怡恠怙怐怩怎怱怛怕怫怦怏怺恚恁恪恷恟恊恆恍恣恃恤恂恬恫恙悁悍惧悃悚悄悛悖悗悒悧悋惡悸惠惓悴忰悽惆悵惘慍愕愆惶惷愀惴惺愃愡惻惱愍愎慇愾愨愧慊愿愼愬愴愽慂慄慳慷慘慙慚慫慴慯慥慱慟慝慓慵憙憖憇憬憔憚憊憑憫憮懌懊應懷懈懃懆憺懋罹懍懦懣懶懺懴懿懽懼懾戀戈戉戍戌戔戛戞戡截戮戰戲戳扁扎扞扣扛扠扨扼抂抉找抒抓抖拔抃抔拗拑抻拏拿拆擔拈拜拌拊拂拇抛拉挌拮拱挧挂挈拯拵捐挾捍搜捏掖掎掀掫捶掣掏掉掟掵捫捩掾揩揀揆揣揉插揶揄搖搴搆搓搦搶攝搗搨搏摧摯摶摎攪撕撓撥撩撈撼據擒擅擇撻擘擂擱擧舉擠擡抬擣擯攬擶擴擲擺攀擽攘攜攅攤攣攫攴攵攷收攸畋效敖敕敍敘敞敝敲數斂斃變斛斟斫斷旃旆旁旄旌旒旛旙无旡旱杲昊昃旻杳昵昶昴昜晏晄晉晁晞晝晤晧晨晟晢晰暃暈暎暉暄暘暝曁暹曉暾暼曄暸曖曚曠昿曦曩曰曵曷朏朖朞朦朧霸朮朿朶杁朸朷杆杞杠杙杣杤枉杰枩杼杪枌枋枦枡枅枷柯枴柬枳柩枸柤柞柝柢柮枹柎柆柧檜栞框栩桀桍栲桎梳栫桙档桷桿梟梏梭梔條梛梃檮梹桴梵梠梺椏梍桾椁棊椈棘椢椦棡椌棍棔棧棕椶椒椄棗棣椥棹棠棯椨椪椚椣椡棆楹楷楜楸楫楔楾楮椹楴椽楙椰楡楞楝榁楪榲榮槐榿槁槓榾槎寨槊槝榻槃榧樮榑榠榜榕榴槞槨樂樛槿權槹槲槧樅榱樞槭樔槫樊樒櫁樣樓橄樌橲樶橸橇橢橙橦橈樸樢檐檍檠檄檢檣檗蘗檻櫃櫂檸檳檬櫞櫑櫟檪櫚櫪櫻欅蘖櫺欒欖鬱欟欸欷盜欹飮歇歃歉歐歙歔歛歟歡歸歹歿殀殄殃殍殘殕殞殤殪殫殯殲殱殳殷殼毆毋毓毟毬毫毳毯麾氈氓气氛氤氣汞汕汢汪沂沍沚沁沛汾汨汳沒沐泄泱泓沽泗泅泝沮沱沾沺泛泯泙泪洟衍洶洫洽洸洙洵洳洒洌浣涓浤浚浹浙涎涕濤涅淹渕渊涵淇淦涸淆淬淞淌淨淒淅淺淙淤淕淪淮渭湮渮渙湲湟渾渣湫渫湶湍渟湃渺湎渤滿渝游溂溪溘滉溷滓溽溯滄溲滔滕溏溥滂溟潁漑灌滬滸滾漿滲漱滯漲滌漾漓滷澆潺潸澁澀潯潛濳潭澂潼潘澎澑濂潦澳澣澡澤澹濆澪濟濕濬濔濘濱濮濛瀉瀋濺瀑瀁瀏濾瀛瀚潴瀝瀘瀟瀰瀾瀲灑灣炙炒炯烱炬炸炳炮烟烋烝烙焉烽焜焙煥煕熈煦煢煌煖煬熏燻熄熕熨熬燗熹熾燒燉燔燎燠燬燧燵燼燹燿爍爐爛爨爭爬爰爲爻爼爿牀牆牋牘牴牾犂犁犇犒犖犢犧犹犲狃狆狄狎狒狢狠狡狹狷倏猗猊猜猖猝猴猯猩猥猾獎獏默獗獪獨獰獸獵獻獺珈玳珎玻珀珥珮珞璢琅瑯琥珸琲琺瑕琿瑟瑙瑁瑜瑩瑰瑣瑪瑶瑾璋璞璧瓊瓏瓔珱瓠瓣瓧瓩瓮瓲瓰瓱瓸瓷甄甃甅甌甎甍甕甓甞甦甬甼畄畍畊畉畛畆畚畩畤畧畫畭畸當疆疇畴疊疉疂疔疚疝疥疣痂疳痃疵疽疸疼疱痍痊痒痙痣痞痾痿痼瘁痰痺痲痳瘋瘍瘉瘟瘧瘠瘡瘢瘤瘴瘰瘻癇癈癆癜癘癡癢癨癩癪癧癬癰癲癶癸發皀皃皈皋皎皖皓皙皚皰皴皸皹皺盂盍盖盒盞盡盥盧盪蘯盻眈眇眄眩眤眞眥眦眛眷眸睇睚睨睫睛睥睿睾睹瞎瞋瞑瞠瞞瞰瞶瞹瞿瞼瞽瞻矇矍矗矚矜矣矮矼砌砒礦砠礪硅碎硴碆硼碚碌碣碵碪碯磑磆磋磔碾碼磅磊磬磧磚磽磴礇礒礑礙礬礫祀祠祗祟祚祕祓祺祿禊禝禧齋禪禮禳禹禺秉秕秧秬秡秣稈稍稘稙稠稟禀稱稻稾稷穃穗穉穡穢穩龝穰穹穽窈窗窕窘窖窩竈窰窶竅竄窿邃竇竊竍竏竕竓站竚竝竡竢竦竭竰笂笏笊笆笳笘笙笞笵笨笶筐筺笄筍笋筌筅筵筥筴筧筰筱筬筮箝箘箟箍箜箚箋箒箏筝箙篋篁篌篏箴篆篝篩簑簔篦篥籠簀簇簓篳篷簗簍篶簣簧簪簟簷簫簽籌籃籔籏籀籐籘籟籤籖籥籬籵粃粐粤粭粢粫粡粨粳粲粱粮粹粽糀糅糂糘糒糜糢鬻糯糲糴糶糺紆紂紜紕紊絅絋紮紲紿紵絆絳絖絎絲絨絮絏絣經綉絛綏絽綛綺綮綣綵緇綽綫總綢綯緜綸綟綰緘緝緤緞緻緲緡縅縊縣縡縒縱縟縉縋縢繆繦縻縵縹繃縷縲縺繧繝繖繞繙繚繹繪繩繼繻纃緕繽辮繿纈纉續纒纐纓纔纖纎纛纜缸缺罅罌罍罎罐网罕罔罘罟罠罨罩罧罸羂羆羃羈羇羌羔羞羝羚羣羯羲羹羮羶羸譱翅翆翊翕翔翡翦翩翳翹飜耆耄耋耒耘耙耜耡耨耿耻聊聆聒聘聚聟聢聨聳聲聰聶聹聽聿肄肆肅肛肓肚肭冐肬胛胥胙胝胄胚胖脉胯胱脛脩脣脯腋隋腆脾腓腑胼腱腮腥腦腴膃膈膊膀膂膠膕膤膣腟膓膩膰膵膾膸膽臀臂膺臉臍臑臙臘臈臚臟臠臧臺臻臾舁舂舅與舊舍舐舖舩舫舸舳艀艙艘艝艚艟艤艢艨艪艫舮艱艷艸艾芍芒芫芟芻芬苡苣苟苒苴苳苺莓范苻苹苞茆苜茉苙茵茴茖茲茱荀茹荐荅茯茫茗茘莅莚莪莟莢莖茣莎莇莊荼莵荳荵莠莉莨菴萓菫菎菽萃菘萋菁菷萇菠菲萍萢萠莽萸蔆菻葭萪萼蕚蒄葷葫蒭葮蒂葩葆萬葯葹萵蓊葢蒹蒿蒟蓙蓍蒻蓚蓐蓁蓆蓖蒡蔡蓿蓴蔗蔘蔬蔟蔕蔔蓼蕀蕣蕘蕈蕁蘂蕋蕕薀薤薈薑薊薨蕭薔薛藪薇薜蕷蕾薐藉薺藏薹藐藕藝藥藜藹蘊蘓蘋藾藺蘆蘢蘚蘰蘿虍乕虔號虧虱蚓蚣蚩蚪蚋蚌蚶蚯蛄蛆蚰蛉蠣蚫蛔蛞蛩蛬蛟蛛蛯蜒蜆蜈蜀蜃蛻蜑蜉蜍蛹蜊蜴蜿蜷蜻蜥蜩蜚蝠蝟蝸蝌蝎蝴蝗蝨蝮蝙蝓蝣蝪蠅螢螟螂螯蟋螽蟀蟐雖螫蟄螳蟇蟆螻蟯蟲蟠蠏蠍蟾蟶蟷蠎蟒蠑蠖蠕蠢蠡蠱蠶蠹蠧蠻衄衂衒衙衞衢衫袁衾袞衵衽袵衲袂袗袒袮袙袢袍袤袰袿袱裃裄裔裘裙裝裹褂裼裴裨裲褄褌褊褓襃褞褥褪褫襁襄褻褶褸襌褝襠襞襦襤襭襪襯襴襷襾覃覈覊覓覘覡覩覦覬覯覲覺覽覿觀觚觜觝觧觴觸訃訖訐訌訛訝訥訶詁詛詒詆詈詼詭詬詢誅誂誄誨誡誑誥誦誚誣諄諍諂諚諫諳諧諤諱謔諠諢諷諞諛謌謇謚諡謖謐謗謠謳鞫謦謫謾謨譁譌譏譎證譖譛譚譫譟譬譯譴譽讀讌讎讒讓讖讙讚谺豁谿豈豌豎豐豕豢豬豸豺貂貉貅貊貍貎貔豼貘戝貭貪貽貲貳貮貶賈賁賤賣賚賽賺賻贄贅贊贇贏贍贐齎贓賍贔贖赧赭赱赳趁趙跂趾趺跏跚跖跌跛跋跪跫跟跣跼踈踉跿踝踞踐踟蹂踵踰踴蹊蹇蹉蹌蹐蹈蹙蹤蹠踪蹣蹕蹶蹲蹼躁躇躅躄躋躊躓躑躔躙躪躡躬躰軆躱躾軅軈軋軛軣軼軻軫軾輊輅輕輒輙輓輜輟輛輌輦輳輻輹轅轂輾轌轉轆轎轗轜轢轣轤辜辟辣辭辯辷迚迥迢迪迯邇迴逅迹迺逑逕逡逍逞逖逋逧逶逵逹迸遏遐遑遒逎遉逾遖遘遞遨遯遶隨遲邂遽邁邀邊邉邏邨邯邱邵郢郤扈郛鄂鄒鄙鄲鄰酊酖酘酣酥酩酳酲醋醉醂醢醫醯醪醵醴醺釀釁釉釋釐釖釟釡釛釼釵釶鈞釿鈔鈬鈕鈑鉞鉗鉅鉉鉤鉈銕鈿鉋鉐銜銖銓銛鉚鋏銹銷鋩錏鋺鍄錮錙錢錚錣錺錵錻鍜鍠鍼鍮鍖鎰鎬鎭鎔鎹鏖鏗鏨鏥鏘鏃鏝鏐鏈鏤鐚鐔鐓鐃鐇鐐鐶鐫鐵鐡鐺鑁鑒鑄鑛鑠鑢鑞鑪鈩鑰鑵鑷鑽鑚鑼鑾钁鑿閂閇閊閔閖閘閙閠閨閧閭閼閻閹閾闊濶闃闍闌闕闔闖關闡闥闢阡阨阮阯陂陌陏陋陷陜陞陝陟陦陲陬隍隘隕隗險隧隱隲隰隴隶隸隹雎雋雉雍襍雜霍雕雹霄霆霈霓霎霑霏霖霙霤霪霰霹霽霾靄靆靈靂靉靜靠靤靦靨勒靫靱靹鞅靼鞁靺鞆鞋鞏鞐鞜鞨鞦鞣鞳鞴韃韆韈韋韜韭齏韲竟韶韵頏頌頸頤頡頷頽顆顏顋顫顯顰顱顴顳颪颯颱颶飄飃飆飩飫餃餉餒餔餘餡餝餞餤餠餬餮餽餾饂饉饅饐饋饑饒饌饕馗馘馥馭馮馼駟駛駝駘駑駭駮駱駲駻駸騁騏騅駢騙騫騷驅驂驀驃騾驕驍驛驗驟驢驥驤驩驫驪骭骰骼髀髏髑髓體髞髟髢髣髦髯髫髮髴髱髷髻鬆鬘鬚鬟鬢鬣鬥鬧鬨鬩鬪鬮鬯鬲魄魃魏魍魎魑魘魴鮓鮃鮑鮖鮗鮟鮠鮨鮴鯀鯊鮹鯆鯏鯑鯒鯣鯢鯤鯔鯡鰺鯲鯱鯰鰕鰔鰉鰓鰌鰆鰈鰒鰊鰄鰮鰛鰥鰤鰡鰰鱇鰲鱆鰾鱚鱠鱧鱶鱸鳧鳬鳰鴉鴈鳫鴃鴆鴪鴦鶯鴣鴟鵄鴕鴒鵁鴿鴾鵆鵈鵝鵞鵤鵑鵐鵙鵲鶉鶇鶫鵯鵺鶚鶤鶩鶲鷄鷁鶻鶸鶺鷆鷏鷂鷙鷓鷸鷦鷭鷯鷽鸚鸛鸞鹵鹹鹽麁麈麋麌麒麕麑麝麥麩麸麪麭靡黌黎黏黐黔黜點黝黠黥黨黯黴黶黷黹黻黼黽鼇鼈皷鼕鼡鼬鼾齊齒齔齣齟齠齡齦齧齬齪齷齲齶龕龜龠堯槇遙瑤凜熙????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????纊褜鍈銈蓜俉炻昱棈鋹曻彅丨仡仼伀伃伹佖侒侊侚侔俍偀倢俿倞偆偰偂傔僴僘兊兤冝冾凬刕劜劦勀勛匀匇匤卲厓厲叝﨎咜咊咩哿喆坙坥垬埈埇﨏塚增墲夋奓奛奝奣妤妺孖寀甯寘寬尞岦岺峵崧嵓﨑嵂嵭嶸嶹巐弡弴彧德忞恝悅悊惞惕愠惲愑愷愰憘戓抦揵摠撝擎敎昀昕昻昉昮昞昤晥晗晙晴晳暙暠暲暿曺朎朗杦枻桒柀栁桄棏﨓楨﨔榘槢樰橫橆橳橾櫢櫤毖氿汜沆汯泚洄涇浯涖涬淏淸淲淼渹湜渧渼溿澈澵濵瀅瀇瀨炅炫焏焄煜煆煇凞燁燾犱犾猤猪獷玽珉珖珣珒琇珵琦琪琩琮瑢璉璟甁畯皂皜皞皛皦益睆劯砡硎硤硺礰礼神祥禔福禛竑竧靖竫箞精絈絜綷綠緖繒罇羡羽茁荢荿菇菶葈蒴蕓蕙蕫﨟薰蘒﨡蠇裵訒訷詹誧誾諟諸諶譓譿賰賴贒赶﨣軏﨤逸遧郞都鄕鄧釚釗釞釭釮釤釥鈆鈐鈊鈺鉀鈼鉎鉙鉑鈹鉧銧鉷鉸鋧鋗鋙鋐﨧鋕鋠鋓錥錡鋻﨨錞鋿錝錂鍰鍗鎤鏆鏞鏸鐱鑅鑈閒隆﨩隝隯霳霻靃靍靏靑靕顗顥飯飼餧館馞驎髙髜魵魲鮏鮱鮻鰀鵰鵫鶴鸙黑??ⅰⅱⅲⅳⅴⅵⅶⅷⅸⅹ￢￤＇＂????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????ⅰⅱⅲⅳⅴⅵⅶⅷⅸⅹⅠⅡⅢⅣⅤⅥⅦⅧⅨⅩ￢￤＇＂㈱№℡∵纊褜鍈銈蓜俉炻昱棈鋹曻彅丨仡仼伀伃伹佖侒侊侚侔俍偀倢俿倞偆偰偂傔僴僘兊兤冝冾凬刕劜劦勀勛匀匇匤卲厓厲叝﨎咜咊咩哿喆坙坥垬埈埇﨏塚增墲夋奓奛奝奣妤妺孖寀甯寘寬尞岦岺峵崧嵓﨑嵂嵭嶸嶹巐弡弴彧德忞恝悅悊惞惕愠惲愑愷愰憘戓抦揵摠撝擎敎昀昕昻昉昮昞昤晥晗晙晴晳暙暠暲暿曺朎朗杦枻桒柀栁桄棏﨓楨﨔榘槢樰橫橆橳橾櫢櫤毖氿汜沆汯泚洄涇浯涖涬淏淸淲淼渹湜渧渼溿澈澵濵瀅瀇瀨炅炫焏焄煜煆煇凞燁燾犱犾猤猪獷玽珉珖珣珒琇珵琦琪琩琮瑢璉璟甁畯皂皜皞皛皦益睆劯砡硎硤硺礰礼神祥禔福禛竑竧靖竫箞精絈絜綷綠緖繒罇羡羽茁荢荿菇菶葈蒴蕓蕙蕫﨟薰蘒﨡蠇裵訒訷詹誧誾諟諸諶譓譿賰賴贒赶﨣軏﨤逸遧郞都鄕鄧釚釗釞釭釮釤釥鈆鈐鈊鈺鉀鈼鉎鉙鉑鈹鉧銧鉷鉸鋧鋗鋙鋐﨧鋕鋠鋓錥錡鋻﨨錞鋿錝錂鍰鍗鎤鏆鏞鏸鐱鑅鑈閒隆﨩隝隯霳霻靃靍靏靑靕顗顥飯飼餧館馞驎髙髜魵魲鮏鮱鮻鰀鵰鵫鶴鸙黑????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????????";
    private const string HexDigits = "0123456789ABCDEF";
    private const string EntityTable = ";nbsp= ;lt=<;gt=>;quot=\";apos=';amp=&;copy=©;reg=®;trade=™;mdash=—;ndash=–;hellip=…;laquo=«;raquo=»;lsquo=‘;rsquo=’;sbquo=‚;ldquo=“;rdquo=”;bdquo=„;bull=•;middot=·;times=×;divide=÷;minus=−;deg=°;plusmn=±;frac12=½;frac14=¼;frac34=¾;sup2=²;sup3=³;micro=µ;yen=¥;euro=€;pound=£;cent=¢;sect=§;para=¶;dagger=†;larr=←;rarr=→;uarr=↑;darr=↓;harr=↔;rArr=⇒;hArr=⇔;ne=≠;le=≤;ge=≥;asymp=≈;infin=∞;hearts=♥;spades=♠;clubs=♣;diams=♦;check=✓;cross=✗;star=☆;starf=★;ensp= ;emsp= ;thinsp= ;zwj=;zwnj=;shy=;iexcl=¡;iquest=¿;agrave=à;aacute=á;acirc=â;atilde=ã;auml=ä;aring=å;aelig=æ;ccedil=ç;egrave=è;eacute=é;ecirc=ê;euml=ë;igrave=ì;iacute=í;icirc=î;iuml=ï;ntilde=ñ;ograve=ò;oacute=ó;ocirc=ô;otilde=õ;ouml=ö;oslash=ø;ugrave=ù;uacute=ú;ucirc=û;uuml=ü;szlig=ß;Agrave=À;Aacute=Á;Auml=Ä;Aring=Å;Ccedil=Ç;Egrave=È;Eacute=É;Ntilde=Ñ;Ouml=Ö;Oslash=Ø;Uuml=Ü;alpha=α;beta=β;gamma=γ;delta=δ;lambda=λ;mu=μ;pi=π;sigma=σ;omega=ω;Omega=Ω;";
    private const string ListStyles = " none disc circle square decimal decimal-leading-zero lower-alpha upper-alpha lower-latin upper-latin lower-roman upper-roman ";
    private const string FontSizeKeywords = " xx-small x-small small medium large x-large xx-large smaller larger ";
    private const string Letters = "abcdefghijklmnopqrstuvwxyz";
    private const string NamedColors = ";black=#000000;white=#FFFFFF;red=#FF0000;green=#008000;blue=#0000FF;yellow=#FFFF00;orange=#FFA500;purple=#800080;gray=#808080;grey=#808080;silver=#C0C0C0;navy=#000080;teal=#008080;maroon=#800000;lime=#00FF00;aqua=#00FFFF;cyan=#00FFFF;fuchsia=#FF00FF;magenta=#FF00FF;pink=#FFC0CB;brown=#A52A2A;olive=#808000;gold=#FFD700;indigo=#4B0082;violet=#EE82EE;tomato=#FF6347;crimson=#DC143C;coral=#FF7F50;salmon=#FA8072;khaki=#F0E68C;beige=#F5F5DC;ivory=#FFFFF0;lightgray=#D3D3D3;lightgrey=#D3D3D3;darkgray=#A9A9A9;darkgrey=#A9A9A9;dimgray=#696969;whitesmoke=#F5F5F5;skyblue=#87CEEB;royalblue=#4169E1;steelblue=#4682B4;darkblue=#00008B;lightblue=#ADD8E6;darkgreen=#006400;lightgreen=#90EE90;darkred=#8B0000;hotpink=#FF69B4;aliceblue=#F0F8FF;antiquewhite=#FAEBD7;aquamarine=#7FFFD4;azure=#F0FFFF;bisque=#FFE4C4;blanchedalmond=#FFEBCD;blueviolet=#8A2BE2;burlywood=#DEB887;cadetblue=#5F9EA0;chartreuse=#7FFF00;chocolate=#D2691E;cornflowerblue=#6495ED;cornsilk=#FFF8DC;darkcyan=#008B8B;darkgoldenrod=#B8860B;darkkhaki=#BDB76B;darkmagenta=#8B008B;darkolivegreen=#556B2F;darkorange=#FF8C00;darkorchid=#9932CC;darksalmon=#E9967A;darkseagreen=#8FBC8F;darkslateblue=#483D8B;darkslategray=#2F4F4F;darkslategrey=#2F4F4F;darkturquoise=#00CED1;darkviolet=#9400D3;deeppink=#FF1493;deepskyblue=#00BFFF;dimgrey=#696969;dodgerblue=#1E90FF;firebrick=#B22222;floralwhite=#FFFAF0;forestgreen=#228B22;gainsboro=#DCDCDC;ghostwhite=#F8F8FF;goldenrod=#DAA520;greenyellow=#ADFF2F;honeydew=#F0FFF0;indianred=#CD5C5C;lavender=#E6E6FA;lavenderblush=#FFF0F5;lawngreen=#7CFC00;lemonchiffon=#FFFACD;lightcoral=#F08080;lightcyan=#E0FFFF;lightgoldenrodyellow=#FAFAD2;lightpink=#FFB6C1;lightsalmon=#FFA07A;lightseagreen=#20B2AA;lightskyblue=#87CEFA;lightslategray=#778899;lightslategrey=#778899;lightsteelblue=#B0C4DE;lightyellow=#FFFFE0;limegreen=#32CD32;linen=#FAF0E6;mediumaquamarine=#66CDAA;mediumblue=#0000CD;mediumorchid=#BA55D3;mediumpurple=#9370DB;mediumseagreen=#3CB371;mediumslateblue=#7B68EE;mediumspringgreen=#00FA9A;mediumturquoise=#48D1CC;mediumvioletred=#C71585;midnightblue=#191970;mintcream=#F5FFFA;mistyrose=#FFE4E1;moccasin=#FFE4B5;navajowhite=#FFDEAD;oldlace=#FDF5E6;olivedrab=#6B8E23;orangered=#FF4500;orchid=#DA70D6;palegoldenrod=#EEE8AA;palegreen=#98FB98;paleturquoise=#AFEEEE;palevioletred=#DB7093;papayawhip=#FFEFD5;peachpuff=#FFDAB9;peru=#CD853F;plum=#DDA0DD;powderblue=#B0E0E6;rebeccapurple=#663399;rosybrown=#BC8F8F;saddlebrown=#8B4513;sandybrown=#F4A460;seagreen=#2E8B57;seashell=#FFF5EE;sienna=#A0522D;slateblue=#6A5ACD;slategray=#708090;slategrey=#708090;snow=#FFFAFA;springgreen=#00FF7F;tan=#D2B48C;thistle=#D8BFD8;turquoise=#40E0D0;wheat=#F5DEB3;yellowgreen=#9ACD32;";

    // property slots
    private const int PColor = 0;
    private const int PFontSize = 1;
    private const int PWeight = 2;
    private const int PStyle = 3;
    private const int PAlign = 4;
    private const int PBackground = 5;
    private const int PDisplay = 6;
    private const int PMarginTop = 7;
    private const int PMarginBottom = 8;
    private const int PPadV = 9;
    private const int PPadLeft = 10;
    private const int PPadRight = 11;
    private const int PMarginLeft = 12;
    private const int PMarginRight = 13;
    private const int PWidth = 14;
    private const int PMaxWidth = 15;
    private const int PLineHeight = 16;
    private const int PTransform = 17;
    private const int PWhiteSpace = 18;
    private const int PListStyle = 19;
    private const int PTextIndent = 20;
    private const int PropCount = 21;

    // stylesheet
    private int ruleCount = 0;
    private string[][] rTags = null;   // per rule: tag of each compound ("" = any)
    private string[][] rIds = null;    // per rule: id of each compound
    private string[][] rClasses = null; // per rule: classes of each compound, " a b"
    private string[][] rNames = null;  // per rule: property names
    private string[][] rValues = null; // per rule: property values
    private int[] rSpec = null;
    private int[] tagOnlyRules = null; // rules whose last compound has no class/id (the only ones a plain element can match)
    private int tagOnlyCount = 0;

    // custom properties declared on :root / html / body
    private const int MaxVars = 128;
    private string[] varNames = null;
    private string[] varValues = null;
    private int varCount = 0;

    // scratch: parsed declarations
    private string[] declNames = null;
    private string[] declValues = null;
    private int declCount = 0;

    // scratch: style being computed for the element on top of the stack
    private string[] rawValue = null;
    private int[] rawSpec = null;

    // open element stack; index 0 is a virtual root
    private int depth = 0;
    private int nextSerial = 1;
    private string[] elTag = null;
    private string[] elId = null;
    private string[] elClasses = null; // " a b "
    private int[] elSerial = null;
    private bool[] elBlock = null;
    private bool[] elHidden = null;
    private bool[] elPre = null;
    private string[] elColor = null;
    private float[] elSize = null;
    private bool[] elBold = null;
    private bool[] elItalic = null;
    private int[] elAlign = null;
    private string[] elBg = null;
    private float[] elPadL = null;
    private float[] elPadR = null;
    private float[] elMarginL = null;
    private float[] elMarginR = null;
    private float[] elAvail = null;     // width left for the element's content, CSS px
    private float[] elLine = null;      // line-height as a multiple of the font size, 0 = normal
    private int[] elTransform = null;   // text-transform: 0 none, 1 uppercase, 2 lowercase, 3 capitalize
    private float[] elTextIndent = null;
    private string[] elListStyle = null; // list-style-type, "" = default for the list
    private float[] elPadV = null;
    private float[] elMarginTop = null;
    private float[] elMarginBottom = null;
    private int[] elListType = null; // 0 none, 1 ul, 2 ol
    private int[] elListCount = null;

    // document being parsed
    private string hHtml = "";
    private string hLower = "";
    private string hCollected = "";
    private string pageColor = "#000000";
    private string pageBg = "";

    // paragraph being built
    private string curText = "";
    private bool curStarted = false;
    private bool curEndsWithSpace = true;
    private int curAlign = 0;
    private string curBg = "";
    private int curOwner = 0;
    private float curPadL = 0f;
    private float curPadR = 0f;
    private float curIndentL = 0f;
    private float curIndentR = 0f;
    private float curLine = 1f;
    private float curPadV = 0f;
    private float pendingGap = 0f;
    private string pendingPrefix = "";

    private int[] romanValues = null;
    private string[] romanSymbols = null;
    private int[] fontSteps = null; // px for <font size="1".."7">

    // number parser output
    private float numValue = 0f;
    private int numEnd = 0;
    private bool numOk = false;

    private void InitHtml()
    {
        rTags = new string[MaxRules][];
        rIds = new string[MaxRules][];
        rClasses = new string[MaxRules][];
        rNames = new string[MaxRules][];
        rValues = new string[MaxRules][];
        rSpec = new int[MaxRules];
        tagOnlyRules = new int[MaxRules];
        declNames = new string[32];
        declValues = new string[32];
        rawValue = new string[PropCount];
        rawSpec = new int[PropCount];
        varNames = new string[MaxVars];
        varValues = new string[MaxVars];
        romanValues = new int[] { 1000, 900, 500, 400, 100, 90, 50, 40, 10, 9, 5, 4, 1 };
        romanSymbols = new string[] { "m", "cm", "d", "cd", "c", "xc", "l", "xl", "x", "ix", "v", "iv", "i" };
        fontSteps = new int[] { 10, 13, 16, 18, 24, 32, 48 };

        elTag = new string[MaxDepth];
        elId = new string[MaxDepth];
        elClasses = new string[MaxDepth];
        elSerial = new int[MaxDepth];
        elBlock = new bool[MaxDepth];
        elHidden = new bool[MaxDepth];
        elPre = new bool[MaxDepth];
        elColor = new string[MaxDepth];
        elSize = new float[MaxDepth];
        elBold = new bool[MaxDepth];
        elItalic = new bool[MaxDepth];
        elAlign = new int[MaxDepth];
        elBg = new string[MaxDepth];
        elPadL = new float[MaxDepth];
        elPadR = new float[MaxDepth];
        elMarginL = new float[MaxDepth];
        elMarginR = new float[MaxDepth];
        elAvail = new float[MaxDepth];
        elLine = new float[MaxDepth];
        elTransform = new int[MaxDepth];
        elTextIndent = new float[MaxDepth];
        elListStyle = new string[MaxDepth];
        elPadV = new float[MaxDepth];
        elMarginTop = new float[MaxDepth];
        elMarginBottom = new float[MaxDepth];
        elListType = new int[MaxDepth];
        elListCount = new int[MaxDepth];
    }

    private bool LooksLikeHtml(string body)
    {
        string head = body.Substring(0, Mathf.Min(body.Length, 500)).ToLower();
        return head.Contains("<!doctype html") || head.Contains("<html") || head.Contains("<head") || head.Contains("<body");
    }

    private void ShowHtml(string html)
    {
        if (html.Length > maxHtmlChars) html = html.Substring(0, maxHtmlChars);
        if (html.ToLower().Contains("<frameset"))
        {
            StartFrames(html);
            return;
        }
        if (js != null && html.ToLower().Contains("<script")) html = js._Prepare(html, loadedUrl);
        RenderHtml(html, true);
    }

    // ---------- frames ----------

    /// <summary>A frameset page has no content of its own: load the frames that are known URLs and show them one below the other.</summary>
    private void StartFrames(string html)
    {
        string lower = html.ToLower();
        frameTotal = 0;
        frameIndex = 0;
        frameHtml = "";
        frameTitle = "";
        int titleStart = lower.IndexOf("<title");
        int titleOpenEnd = titleStart < 0 ? -1 : lower.IndexOf(">", titleStart);
        int titleEnd = titleStart < 0 ? -1 : lower.IndexOf("</title", titleStart);
        if (titleOpenEnd >= 0 && titleEnd > titleOpenEnd) frameTitle = DecodeEntities(html.Substring(titleOpenEnd + 1, titleEnd - titleOpenEnd - 1).Trim());

        string missing = "";
        int pos = 0;
        for (int guard = 0; guard < 32; guard++)
        {
            int start = lower.IndexOf("<frame", pos);
            if (start < 0) break;
            int end = lower.IndexOf(">", start);
            if (end < 0) break;
            pos = end + 1;
            if (lower[start + 6] == 's') continue; // <frameset>
            string attrs = html.Substring(start + 6, end - start - 6).Replace('\n', ' ').Replace('\t', ' ').Replace('\r', ' ');
            string src = GetAttribute(attrs, attrs.ToLower(), "src");
            if (src.Length == 0) continue;
            string url = ResolveUrl(loadedUrl, src);
            VRCUrl known = FindKnownUrl(url);
            if (known != null && frameTotal < framePending.Length)
            {
                framePending[frameTotal] = known;
                frameTotal++;
            }
            else missing = missing + "<li>" + url + "</li>";
        }

        if (missing.Length > 0) frameHtml = "<p><font color=\"#888888\">このページはフレームで構成されています。次のフレームは登録されていないため読み込めません。URL を直接入力すると開けます。</font></p><ul>" + missing + "</ul><hr>";
        if (frameTotal == 0)
        {
            if (missing.Length == 0) frameHtml = "";
            FinishFrames();
            return;
        }
        statusText.text = "フレーム読み込み中… (1/" + frameTotal + ")";
        VRCStringDownloader.LoadUrl(framePending[0], (IUdonEventReceiver)this);
    }

    private void AppendFrame(string body)
    {
        if (body.Length > maxHtmlChars) body = body.Substring(0, maxHtmlChars);
        if (!LooksLikeHtml(body))
        {
            frameHtml = frameHtml + "<pre>" + body.Replace("&", "&amp;").Replace("<", "&lt;") + "</pre><hr>";
            return;
        }
        // the frame's body becomes a box of the combined page, so its bgcolor stays local to the frame
        body = ReplaceIgnoreCase(body, "<body", "<div");
        body = ReplaceIgnoreCase(body, "</body", "</div");
        frameHtml = frameHtml + body + "<hr>";
    }

    private void NextFrame()
    {
        frameIndex++;
        if (frameIndex >= frameTotal)
        {
            FinishFrames();
            return;
        }
        statusText.text = "フレーム読み込み中… (" + (frameIndex + 1) + "/" + frameTotal + ")";
        VRCStringDownloader.LoadUrl(framePending[frameIndex], (IUdonEventReceiver)this);
    }

    private void FinishFrames()
    {
        frameTotal = 0;
        frameIndex = 0;
        if (frameHtml.EndsWith("<hr>")) frameHtml = frameHtml.Substring(0, frameHtml.Length - 4);
        RenderHtml(frameHtml, false);
        statusText.text = frameTitle.Length > 40 ? frameTitle.Substring(0, 40) + "…" : frameTitle;
        frameHtml = "";
    }

    private string ReplaceIgnoreCase(string text, string lowerFind, string replacement)
    {
        string lower = text.ToLower();
        int at = lower.LastIndexOf(lowerFind);
        for (int guard = 0; guard < 16 && at >= 0; guard++)
        {
            text = text.Substring(0, at) + replacement + text.Substring(at + lowerFind.Length);
            if (at == 0) break;
            at = lower.LastIndexOf(lowerFind, at - 1);
        }
        return text;
    }

    private VRCUrl FindKnownUrl(string url)
    {
        if (frameUrls != null)
        {
            for (int i = 0; i < frameUrls.Length; i++)
            {
                if (frameUrls[i] != null && frameUrls[i].Get() == url) return frameUrls[i];
            }
        }
        if (bookmarkUrls != null)
        {
            for (int i = 0; i < bookmarkUrls.Length; i++)
            {
                if (bookmarkUrls[i] != null && bookmarkUrls[i].Get() == url) return bookmarkUrls[i];
            }
        }
        if (homeUrl != null && homeUrl.Get() == url) return homeUrl;
        return null;
    }

    /// <summary>Absolute form of a link found in the page at baseUrl.</summary>
    private string ResolveUrl(string baseUrl, string rel)
    {
        rel = rel.Trim();
        if (rel.Contains("://")) return rel;
        int schemeEnd = baseUrl.IndexOf("://");
        if (schemeEnd < 0) return rel;
        if (rel.StartsWith("//")) return baseUrl.Substring(0, schemeEnd + 1) + rel;

        int cut = baseUrl.IndexOf('#');
        if (cut >= 0) baseUrl = baseUrl.Substring(0, cut);
        cut = baseUrl.IndexOf('?');
        if (cut >= 0) baseUrl = baseUrl.Substring(0, cut);

        int pathStart = baseUrl.IndexOf('/', schemeEnd + 3);
        string origin = pathStart < 0 ? baseUrl : baseUrl.Substring(0, pathStart);
        if (rel.StartsWith("/")) return origin + rel;
        string dir = pathStart < 0 ? "/" : baseUrl.Substring(pathStart, baseUrl.LastIndexOf('/') - pathStart + 1);
        for (int guard = 0; guard < 16; guard++)
        {
            if (rel.StartsWith("./")) rel = rel.Substring(2);
            else if (rel.StartsWith("../"))
            {
                rel = rel.Substring(3);
                int up = dir.Length > 1 ? dir.LastIndexOf('/', dir.Length - 2) : -1;
                if (up >= 0) dir = dir.Substring(0, up + 1);
            }
            else break;
        }
        return origin + dir + rel;
    }

    // ---------- character encodings ----------

    /// <summary>
    /// The downloader decodes everything as UTF-8. Pages that declare Shift_JIS or EUC-JP in a meta element are
    /// decoded here from the raw bytes instead.
    /// </summary>
    private string DecodeBody(IVRCStringDownload result)
    {
        byte[] bytes = result.ResultBytes;
        if (bytes == null || bytes.Length == 0) return result.Result;

        int headLength = Mathf.Min(bytes.Length, 1024);
        char[] headChars = new char[headLength];
        for (int i = 0; i < headLength; i++)
        {
            int b = bytes[i];
            headChars[i] = b < 0x80 ? (char)b : '?';
        }
        string head = new string(headChars).ToLower();
        int at = head.IndexOf("charset=");
        if (at < 0) return result.Result;
        string name = head.Substring(at + 8, Mathf.Min(16, head.Length - at - 8)).Replace("\"", "").Replace("'", "").Trim();
        if (name.StartsWith("shift_jis") || name.StartsWith("shift-jis") || name.StartsWith("sjis") || name.StartsWith("x-sjis") || name.StartsWith("windows-31j") || name.StartsWith("cp932") || name.StartsWith("ms932")) return DecodeJapanese(bytes, false);
        if (name.StartsWith("euc-jp") || name.StartsWith("x-euc-jp")) return DecodeJapanese(bytes, true);
        return result.Result;
    }

    private string DecodeJapanese(byte[] bytes, bool euc)
    {
        int length = Mathf.Min(bytes.Length, maxHtmlChars * 2);
        char[] chars = new char[length];
        int n = 0;
        int i = 0;
        while (i < length)
        {
            int b = bytes[i];
            i++;
            if (b < 0x80)
            {
                chars[n] = (char)b;
                n++;
                continue;
            }

            int index = -1; // into Cp932Table: 188 cells per Shift_JIS lead byte
            if (euc)
            {
                if (b == 0x8E && i < length)
                {
                    chars[n] = (char)(0xFF61 + bytes[i] - 0xA1); // half-width katakana
                    n++;
                    i++;
                    continue;
                }
                if (b == 0x8F) i += 2; // JIS X 0212: not in the table
                else if (b >= 0xA1 && b <= 0xFE && i < length)
                {
                    int t = bytes[i];
                    i++;
                    int ku = b - 0xA1;
                    if (t >= 0xA1 && t <= 0xFE) index = (ku / 2) * 188 + (ku % 2) * 94 + (t - 0xA1);
                }
            }
            else
            {
                if (b >= 0xA1 && b <= 0xDF)
                {
                    chars[n] = (char)(0xFF61 + b - 0xA1); // half-width katakana
                    n++;
                    continue;
                }
                int lead = b <= 0x9F ? b - 0x81 : b - 0xC1;
                if (lead >= 0 && lead < 60 && i < length)
                {
                    int t = bytes[i];
                    if (t >= 0x40 && t <= 0xFC && t != 0x7F)
                    {
                        i++;
                        index = lead * 188 + (t < 0x7F ? t - 0x40 : t - 0x41);
                    }
                }
            }
            chars[n] = index >= 0 && index < Cp932Table.Length ? Cp932Table[index] : '?';
            n++;
        }
        return new string(chars, 0, n);
    }

    /// <summary>Called by the script runtime after a script changed the document.</summary>
    public void _JsRender(string html)
    {
        Vector2 scroll = scrollRect.content.anchoredPosition;
        RenderHtml(html, false);
        scrollRect.content.anchoredPosition = scroll;
    }

    /// <summary>Called by the script runtime: errors, alert() and document.title.</summary>
    public void _JsStatus(string message)
    {
        statusText.text = message.Length > 60 ? message.Substring(0, 60) + "…" : message;
    }

    private void RenderHtml(string html, bool showTitle)
    {
        hHtml = html;
        hLower = html.ToLower();

        // title -> status bar
        int titleStart = showTitle ? hLower.IndexOf("<title") : -1;
        if (titleStart >= 0)
        {
            int titleOpenEnd = hLower.IndexOf(">", titleStart);
            int titleEnd = hLower.IndexOf("</title", titleStart);
            if (titleOpenEnd >= 0 && titleEnd > titleOpenEnd)
            {
                string title = DecodeEntities(hHtml.Substring(titleOpenEnd + 1, titleEnd - titleOpenEnd - 1).Trim());
                statusText.text = title.Length > 40 ? title.Substring(0, 40) + "…" : title;
            }
        }

        // stylesheets, then drop everything that never renders
        ruleCount = 0;
        tagOnlyCount = 0;
        varCount = 0;
        hCollected = "";
        StripElements("<style", "</style>", true);
        ParseCss(hCollected);
        StripElements("<script", "</script>", false);
        StripElements("<!--", "-->", false);
        StripElements("<head>", "</head>", false); // not "<head": that would also match <header>
        StripElements("<head ", "</head>", false);
        StripElements("<noscript", "</noscript>", false);
        StripElements("<svg", "</svg>", false);
        StripElements("<template", "</template>", false);

        BeginBlocks();
        pageColor = "#000000";
        pageBg = "#FFFFFF";
        nextSerial = 1;
        depth = 0;
        elTag[0] = "#root";
        elId[0] = "";
        elClasses[0] = "";
        elSerial[0] = 0;
        elBlock[0] = true;
        elHidden[0] = false;
        elPre[0] = false;
        elColor[0] = pageColor;
        elSize[0] = 16f;
        elBold[0] = false;
        elItalic[0] = false;
        elAlign[0] = AlignLeft;
        elBg[0] = "";
        elPadL[0] = 0f;
        elPadR[0] = 0f;
        elMarginL[0] = 0f;
        elMarginR[0] = 0f;
        elAvail[0] = pagePx;
        elLine[0] = 0f;
        elTransform[0] = 0;
        elTextIndent[0] = 0f;
        elListStyle[0] = "";
        elPadV[0] = 0f;
        elMarginTop[0] = 0f;
        elMarginBottom[0] = 0f;
        elListType[0] = 0;
        elListCount[0] = 0;
        ResetParagraph();
        pendingGap = 0f;
        pendingPrefix = "";

        int pos = 0;
        int length = hHtml.Length;
        for (int guard = 0; guard < maxHtmlTags && pos < length; guard++)
        {
            int lt = hHtml.IndexOf('<', pos);
            if (lt < 0)
            {
                AddText(hHtml.Substring(pos));
                pos = length;
                break;
            }
            if (lt > pos) AddText(hHtml.Substring(pos, lt - pos));

            int gt = hHtml.IndexOf('>', lt + 1);
            if (gt < 0)
            {
                pos = length;
                break;
            }
            pos = gt + 1;
            if (gt == lt + 1) continue;

            string lowerInner = hLower.Substring(lt + 1, gt - lt - 1);
            char first = lowerInner[0];
            if (first == '/')
            {
                CloseTag(lowerInner.Substring(1).Trim());
                continue;
            }
            if ("abcdefghijklmnopqrstuvwxyz".IndexOf(first) < 0)
            {
                // not a tag ("a < b", doctype, processing instruction)
                if (first != '!' && first != '?')
                {
                    AddText("<");
                    pos = lt + 1;
                }
                continue;
            }

            // attributes: whitespace normalised without changing the length, so both copies keep the same indices
            string inner = hHtml.Substring(lt + 1, gt - lt - 1).Replace('\n', ' ').Replace('\t', ' ').Replace('\r', ' ');
            lowerInner = lowerInner.Replace('\n', ' ').Replace('\t', ' ').Replace('\r', ' ');
            int nameEnd = lowerInner.IndexOf(' ');
            if (nameEnd < 0) nameEnd = lowerInner.Length;
            string tag = lowerInner.Substring(0, nameEnd);
            if (tag.EndsWith("/")) tag = tag.Substring(0, tag.Length - 1);
            OpenTag(tag, inner.Substring(nameEnd), lowerInner.Substring(nameEnd));
            if (blocksTruncated) break;
        }
        while (depth > 0) PopElement();
        FlushParagraph();
        if (pos < length) blocksTruncated = true;

        if (blockCount == 0) AddBlock("<color=#888888>(表示できる内容がありません)</color>", AlignLeft, "", 0, 0f, 0f, 0f, 0f, 0f, 0f, 1f);
        LayoutBlocks(pageBg, HexToColor(pageColor));
    }

    /// <summary>Removes every openMarker..closeMarker range from the document, optionally collecting the inner text.</summary>
    private void StripElements(string openMarker, string closeMarker, bool collect)
    {
        for (int guard = 0; guard < 200; guard++)
        {
            int start = hLower.IndexOf(openMarker);
            if (start < 0) break;
            int end = hLower.IndexOf(closeMarker, start);
            if (end < 0)
            {
                hHtml = hHtml.Substring(0, start);
                hLower = hLower.Substring(0, start);
                break;
            }
            if (collect)
            {
                int bodyStart = hLower.IndexOf(">", start);
                if (bodyStart >= 0 && bodyStart < end) hCollected = hCollected + "\n" + hHtml.Substring(bodyStart + 1, end - bodyStart - 1);
            }
            int after = end + closeMarker.Length;
            hHtml = hHtml.Substring(0, start) + hHtml.Substring(after);
            hLower = hLower.Substring(0, start) + hLower.Substring(after);
        }
    }

    // ---------- elements ----------

    private void OpenTag(string tag, string attrs, string lowerAttrs)
    {
        if (tag == "br")
        {
            if (curStarted) curText = curText + "\n";
            curEndsWithSpace = true;
            return;
        }
        if (tag == "hr")
        {
            FlushParagraph();
            if (!elHidden[depth])
            {
                pendingGap = Mathf.Max(pendingGap, 8f);
                AddText("────────────────────────");
                FlushParagraph();
                pendingGap = 8f;
            }
            return;
        }
        if (tag == "img")
        {
            string alt = GetAttribute(attrs, lowerAttrs, "alt");
            AddText(alt.Length > 0 ? "[画像: " + alt + "]" : "[画像]");
            return;
        }
        if (tag == "input")
        {
            AddInput(attrs, lowerAttrs);
            return;
        }
        if (VoidTags.Contains(" " + tag + " ")) return;

        AutoClose(tag);
        if (depth >= MaxDepth - 1) return; // too deep: treat as transparent

        depth++;
        elTag[depth] = tag;
        elSerial[depth] = nextSerial;
        nextSerial++;
        bool hasAttributes = lowerAttrs.Trim().Length > 0;
        elId[depth] = hasAttributes ? GetAttribute(attrs, lowerAttrs, "id") : "";
        string classes = hasAttributes ? GetAttribute(attrs, lowerAttrs, "class") : "";
        elClasses[depth] = classes.Length > 0 ? " " + classes + " " : "";
        ComputeStyle(tag, hasAttributes ? LegacyStyle(tag, attrs, lowerAttrs) + GetAttribute(attrs, lowerAttrs, "style") : "");

        if (tag == "dialog" && hasAttributes)
        {
            // hidden by default; an open dialog shows unless something else hides it
            string padded = lowerAttrs + " ";
            bool open = padded.Contains(" open ") || padded.Contains(" open=");
            if (open && !padded.Contains(" hidden") && rawValue[PDisplay] != "none") elHidden[depth] = elHidden[depth - 1];
        }

        if (tag == "body" || tag == "html")
        {
            pageColor = elColor[depth];
            if (elBg[depth].Length > 0) pageBg = elBg[depth];
        }

        if (elBlock[depth])
        {
            FlushParagraph();
            pendingGap = Mathf.Max(pendingGap, elMarginTop[depth]);
        }

        if (tag == "ul") elListType[depth] = 1;
        else if (tag == "ol") elListType[depth] = 2;
        else elListType[depth] = 0;
        elListCount[depth] = 0;
        if (tag == "ol" && hasAttributes)
        {
            string start = GetAttribute(attrs, lowerAttrs, "start");
            if (start.Length > 0)
            {
                ParseNumber(start);
                if (numOk) elListCount[depth] = Mathf.RoundToInt(numValue) - 1;
            }
        }

        if (tag == "li")
        {
            if (!elBlock[depth]) return; // display: inline (menus): no marker
            int listDepth = 0;
            int nesting = 0;
            for (int d = depth - 1; d >= 1; d--)
            {
                if (elListType[d] == 0) continue;
                if (listDepth == 0) listDepth = d;
                else nesting++;
            }
            int number = 1;
            if (listDepth > 0)
            {
                number = elListCount[listDepth] + 1;
                string itemValue = hasAttributes ? GetAttribute(attrs, lowerAttrs, "value") : "";
                if (itemValue.Length > 0)
                {
                    ParseNumber(itemValue);
                    if (numOk) number = Mathf.RoundToInt(numValue);
                }
                elListCount[listDepth] = number;
            }
            pendingPrefix = ListMarker(elListStyle[depth], listDepth > 0 && elListType[listDepth] == 2, number, nesting);
        }
        else if ((tag == "td" || tag == "th") && curStarted)
        {
            curText = curText + "　";
            curEndsWithSpace = true;
        }
        else if (tag == "summary") pendingPrefix = "▼ ";
        else if (tag == "q") AddText("“");
        else if (tag == "rt") AddText("(");
        else if (tag == "button") AddText("[");
    }

    /// <summary>Form controls cannot be used; they are drawn as text so a form still reads like a form.</summary>
    private void AddInput(string attrs, string lowerAttrs)
    {
        string type = GetAttribute(attrs, lowerAttrs, "type").ToLower();
        if (type == "hidden") return;
        string padded = lowerAttrs + " ";
        if (padded.Contains(" hidden ") || padded.Contains(" hidden=")) return;
        bool isChecked = padded.Contains(" checked ") || padded.Contains(" checked=");
        if (type == "checkbox")
        {
            AddText(isChecked ? "☑ " : "☐ ");
            return;
        }
        if (type == "radio")
        {
            AddText(isChecked ? "◉ " : "○ ");
            return;
        }
        string value = GetAttribute(attrs, lowerAttrs, "value");
        if (type == "submit" || type == "reset" || type == "button" || type == "image")
        {
            if (value.Length == 0) value = type == "reset" ? "リセット" : (type == "button" ? "　" : "送信");
            AddText("[" + value + "] ");
            return;
        }
        if (value.Length == 0) value = GetAttribute(attrs, lowerAttrs, "placeholder");
        AddText("[" + (value.Length > 0 ? value : "　　　　　　") + "] ");
    }

    /// <summary>Text in front of a list item. style: list-style-type, "" = the list's default.</summary>
    private string ListMarker(string style, bool ordered, int number, int nesting)
    {
        if (style == "none") return "";
        if (style.Length == 0) style = ordered ? "decimal" : (nesting == 0 ? "disc" : (nesting == 1 ? "circle" : "square"));

        if (style == "disc") return "• ";
        if (style == "circle") return "◦ ";
        if (style == "square") return "▪ ";
        if (style == "decimal-leading-zero") return (number >= 0 && number < 10 ? "0" : "") + number + ". ";
        if (style == "decimal" || number <= 0) return number + ". ";

        string text;
        if (style.EndsWith("roman"))
        {
            if (number >= 4000) return number + ". ";
            text = "";
            int rest = number;
            for (int i = 0; i < romanValues.Length; i++)
            {
                while (rest >= romanValues[i])
                {
                    text = text + romanSymbols[i];
                    rest -= romanValues[i];
                }
            }
        }
        else
        {
            // a..z, aa..zz
            int index = number - 1;
            text = Letters.Substring(index % 26, 1);
            if (index >= 26) text = Letters.Substring((index / 26 - 1) % 26, 1) + text;
        }
        if (style.StartsWith("upper")) text = text.ToUpper();
        return text + ". ";
    }

    /// <summary>Implicit end tags: a new block ends an open p, a new li ends the previous li, and so on.</summary>
    private void AutoClose(string tag)
    {
        if (elTag[depth] == "p" && BlockTags.Contains(" " + tag + " ")) PopElement();

        if (tag == "li") CloseOpen(" li ", " ul ol ");
        else if (tag == "td" || tag == "th") CloseOpen(" td th ", " tr table ");
        else if (tag == "tr") CloseOpen(" tr ", " table thead tbody tfoot ");
        else if (tag == "dt" || tag == "dd") CloseOpen(" dt dd ", " dl ");
        else if (tag == "option") CloseOpen(" option ", " select ");
    }

    private void CloseOpen(string closeSet, string stopSet)
    {
        for (int d = depth; d >= 1; d--)
        {
            string key = " " + elTag[d] + " ";
            if (stopSet.Contains(key)) return;
            if (closeSet.Contains(key))
            {
                while (depth >= d) PopElement();
                return;
            }
        }
    }

    private void CloseTag(string tag)
    {
        for (int d = depth; d >= 1; d--)
        {
            if (elTag[d] != tag) continue;
            while (depth >= d) PopElement();
            return;
        }
    }

    private void PopElement()
    {
        if (depth <= 0) return;
        string tag = elTag[depth];
        if (tag == "q") AddText("”");
        else if (tag == "rt") AddText(")");
        else if (tag == "button") AddText("] ");
        if (elBlock[depth])
        {
            FlushParagraph();
            pendingGap = Mathf.Max(pendingGap, elMarginBottom[depth]);
        }
        depth--;
    }

    /// <summary>Presentational attributes (bgcolor, align, font color / size, body text, list type, hidden) as CSS declarations.</summary>
    private string LegacyStyle(string tag, string attrs, string lowerAttrs)
    {
        string style = "";
        string value = GetAttribute(attrs, lowerAttrs, "bgcolor");
        if (value.Length > 0) style = style + "background-color:" + value + ";";
        if (tag == "font") value = GetAttribute(attrs, lowerAttrs, "color");
        else if (tag == "body") value = GetAttribute(attrs, lowerAttrs, "text");
        else value = "";
        if (value.Length > 0) style = style + "color:" + value + ";";
        if (tag != "table") // align on a table places the table itself, not its text
        {
            value = GetAttribute(attrs, lowerAttrs, "align").ToLower();
            if (value == "center" || value == "right" || value == "left") style = style + "text-align:" + value + ";";
        }
        if (tag == "font")
        {
            value = GetAttribute(attrs, lowerAttrs, "size");
            if (value.Length > 0)
            {
                ParseNumber(value);
                if (numOk)
                {
                    int step = Mathf.RoundToInt(numValue);
                    if (value[0] == '+' || value[0] == '-') step += 3; // relative to the default size 3
                    style = style + "font-size:" + fontSteps[Mathf.Clamp(step, 1, 7) - 1] + "px;";
                }
            }
        }
        else if (tag == "ol" || tag == "ul" || tag == "li")
        {
            value = GetAttribute(attrs, lowerAttrs, "type");
            if (value == "1") value = "decimal";
            else if (value == "a") value = "lower-alpha";
            else if (value == "A") value = "upper-alpha";
            else if (value == "i") value = "lower-roman";
            else if (value == "I") value = "upper-roman";
            else value = value.ToLower();
            if (value.Length > 0 && ListStyles.Contains(" " + value + " ")) style = style + "list-style-type:" + value + ";";
        }
        string padded = lowerAttrs + " ";
        if (padded.Contains(" hidden ") || padded.Contains(" hidden=")) style = style + "display:none;";
        return style;
    }

    private string GetAttribute(string attrs, string lowerAttrs, string name)
    {
        int at = lowerAttrs.IndexOf(" " + name + "=");
        if (at < 0) return "";
        int valueStart = at + name.Length + 2;
        if (valueStart >= attrs.Length) return "";
        char quote = attrs[valueStart];
        if (quote == '"' || quote == '\'')
        {
            int end = attrs.IndexOf(quote, valueStart + 1);
            if (end < 0) return "";
            return attrs.Substring(valueStart + 1, end - valueStart - 1);
        }
        int space = attrs.IndexOf(' ', valueStart);
        if (space < 0) space = attrs.Length;
        string value = attrs.Substring(valueStart, space - valueStart);
        if (value.EndsWith("/")) value = value.Substring(0, value.Length - 1);
        return value;
    }

    // ---------- text ----------

    private void ResetParagraph()
    {
        curText = "";
        curStarted = false;
        curEndsWithSpace = true;
    }

    private void AddText(string raw)
    {
        if (elHidden[depth]) return;

        string text = raw;
        if (!elPre[depth])
        {
            text = text.Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ');
            for (int guard = 0; guard < 8 && text.Contains("  "); guard++) text = text.Replace("  ", " ");
            if (curEndsWithSpace && text.StartsWith(" ")) text = text.Substring(1);
        }
        if (text.Length == 0) return;
        if (!curStarted && text.Trim().Length == 0) return;

        if (text.IndexOf('&') >= 0) text = DecodeEntities(text);
        int transform = elTransform[depth];
        if (transform == 1) text = text.ToUpper();
        else if (transform == 2) text = text.ToLower();
        else if (transform == 3) text = Capitalize(text);
        if (text.IndexOf('<') >= 0) text = text.Replace("<", "<​"); // literal "<" must not be read as a rich text tag

        if (!curStarted)
        {
            curStarted = true;
            curAlign = elAlign[depth];
            curLine = elLine[depth] <= 0f ? 1f : Mathf.Clamp(elLine[depth] / 1.2f, 0.7f, 3f);
            curOwner = 0;
            curBg = "";
            curPadV = 0f;
            int ownerDepth = 0;
            for (int d = depth; d >= 1; d--)
            {
                if (!elBlock[d] || elBg[d].Length == 0 || elTag[d] == "body" || elTag[d] == "html") continue;
                ownerDepth = d;
                curOwner = elSerial[d];
                curBg = elBg[d];
                curPadV = elPadV[d];
                break;
            }

            // horizontal space: what lies outside the background box moves the block's edges, the rest pads its text
            curIndentL = 0f;
            curIndentR = 0f;
            curPadL = 0f;
            curPadR = 0f;
            for (int d = 1; d <= depth; d++)
            {
                if (!elBlock[d]) continue;
                if (ownerDepth == 0 || d < ownerDepth)
                {
                    curIndentL += elMarginL[d] + elPadL[d];
                    curIndentR += elMarginR[d] + elPadR[d];
                }
                else if (d == ownerDepth)
                {
                    curIndentL += elMarginL[d];
                    curIndentR += elMarginR[d];
                    curPadL += elPadL[d];
                    curPadR += elPadR[d];
                }
                else
                {
                    curPadL += elMarginL[d] + elPadL[d];
                    curPadR += elMarginR[d] + elPadR[d];
                }
            }
            float used = curIndentL + curIndentR + curPadL + curPadR;
            float limit = pagePx * 0.6f; // always leave room for the text
            if (used > limit)
            {
                float scale = limit / used;
                curIndentL *= scale;
                curIndentR *= scale;
                curPadL *= scale;
                curPadR *= scale;
            }

            curText = pendingPrefix;
            pendingPrefix = "";
            int indentCells = Mathf.Min(8, Mathf.RoundToInt(elTextIndent[depth] / elSize[depth]));
            if (indentCells > 0) curText = "　　　　　　　　".Substring(0, indentCells) + curText;
        }

        curEndsWithSpace = text.EndsWith(" ") || text.EndsWith("\n");

        if (elBold[depth]) text = "<b>" + text + "</b>";
        if (elItalic[depth]) text = "<i>" + text + "</i>";
        int size = Mathf.RoundToInt(elSize[depth] * unitsPerPx);
        if (size != baseFontSize) text = "<size=" + Mathf.Clamp(size, 6, 200) + ">" + text + "</size>";
        if (elColor[depth] != pageColor) text = "<color=" + elColor[depth] + ">" + text + "</color>";
        curText = curText + text;
    }

    private void FlushParagraph()
    {
        if (curStarted && curText.Trim().Length > 0)
        {
            AddBlock(curText.TrimEnd(), curAlign, curBg, curOwner, curPadL, curPadR, curPadV, pendingGap, curIndentL, curIndentR, curLine);
            pendingGap = 0f;
        }
        ResetParagraph();
    }

    /// <summary>Named entities from EntityTable and numeric ones (&amp;#8212; / &amp;#x2014;). Unknown entities stay as written.</summary>
    private string DecodeEntities(string text)
    {
        int amp = text.IndexOf('&');
        if (amp < 0) return text;
        string result = "";
        int pos = 0;
        for (int guard = 0; guard < 400 && amp >= 0; guard++)
        {
            int semicolon = text.IndexOf(';', amp + 1);
            string replacement = null;
            if (semicolon > amp + 1 && semicolon - amp <= 10)
            {
                string name = text.Substring(amp + 1, semicolon - amp - 1);
                if (name[0] == '#')
                {
                    bool hex = name.Length > 1 && (name[1] == 'x' || name[1] == 'X');
                    string digits = name.Substring(hex ? 2 : 1).ToUpper();
                    int code = 0;
                    bool ok = digits.Length > 0;
                    for (int i = 0; i < digits.Length && ok; i++)
                    {
                        int digit = HexDigits.IndexOf(digits[i]);
                        if (digit < 0 || (!hex && digit > 9)) ok = false;
                        else code = code * (hex ? 16 : 10) + digit;
                    }
                    if (ok && code > 0 && code < 0xFFFF) replacement = ((char)code).ToString();
                }
                else
                {
                    int at = EntityTable.IndexOf(";" + name + "=");
                    if (at >= 0)
                    {
                        int valueStart = at + name.Length + 2;
                        replacement = EntityTable.Substring(valueStart, EntityTable.IndexOf(';', valueStart) - valueStart);
                    }
                }
            }
            if (replacement == null)
            {
                amp = text.IndexOf('&', amp + 1);
                continue;
            }
            result = result + text.Substring(pos, amp - pos) + replacement;
            pos = semicolon + 1;
            amp = text.IndexOf('&', pos);
        }
        return result + text.Substring(pos);
    }

    private string Capitalize(string text)
    {
        string[] words = text.Split(' ');
        string result = "";
        for (int i = 0; i < words.Length; i++)
        {
            string word = words[i];
            if (word.Length > 0) word = word.Substring(0, 1).ToUpper() + word.Substring(1);
            result = i == 0 ? word : result + " " + word;
        }
        return result;
    }

    // ---------- style computation ----------

    private void ComputeStyle(string tag, string inlineStyle)
    {
        int parent = depth - 1;
        float parentSize = elSize[parent];

        // inherited
        string color = elColor[parent];
        float size = parentSize;
        bool bold = elBold[parent];
        bool italic = elItalic[parent];
        int align = elAlign[parent];
        bool pre = elPre[parent];
        float line = elLine[parent];
        int transform = elTransform[parent];
        float textIndent = elTextIndent[parent];
        string listStyle = elListStyle[parent];
        // not inherited
        bool block = BlockTags.Contains(" " + tag + " ");
        bool hidden = elHidden[parent];
        float marginEm = 0f;
        float marginL = 0f;
        float marginR = 0f;
        float padL = 0f;
        float padR = 0f;

        // browser defaults
        if (tag == "h1") { size = parentSize * 2f; bold = true; marginEm = 0.67f; }
        else if (tag == "h2") { size = parentSize * 1.5f; bold = true; marginEm = 0.83f; }
        else if (tag == "h3") { size = parentSize * 1.17f; bold = true; marginEm = 1f; }
        else if (tag == "h4") { bold = true; marginEm = 1.33f; }
        else if (tag == "h5") { size = parentSize * 0.83f; bold = true; marginEm = 1.67f; }
        else if (tag == "h6") { size = parentSize * 0.67f; bold = true; marginEm = 2.33f; }
        else if (tag == "p" || tag == "dl") marginEm = 1f;
        else if (tag == "ul" || tag == "ol" || tag == "menu" || tag == "dir")
        {
            // the marker is part of the text here, so the list is indented less than a browser's 40px
            padL = 24f;
            if (elTag[parent] != "li") marginEm = 1f;
        }
        else if (tag == "blockquote" || tag == "figure") { marginEm = 1f; marginL = 40f; marginR = 40f; }
        else if (tag == "dd") marginL = 40f;
        else if (tag == "pre" || tag == "xmp" || tag == "listing" || tag == "plaintext") { marginEm = 1f; pre = true; }
        else if (tag == "b" || tag == "strong") bold = true;
        else if (tag == "th") { bold = true; }
        else if (tag == "i" || tag == "em" || tag == "cite" || tag == "var" || tag == "dfn" || tag == "address") italic = true;
        else if (tag == "a") color = "#0000EE";
        else if (tag == "small" || tag == "sub" || tag == "sup") size = parentSize * 0.83f;
        else if (tag == "big") size = parentSize * 1.2f;
        else if (tag == "center" || tag == "caption") align = AlignCenter;
        else if (tag == "select" || tag == "option" || tag == "datalist" || tag == "iframe" || tag == "object" || tag == "canvas" || tag == "video" || tag == "audio"
            || tag == "title" || tag == "rp" || tag == "dialog" || tag == "map") hidden = true;

        // author styles: matching rules in source order, then the style attribute; higher specificity wins per property
        for (int p = 0; p < PropCount; p++)
        {
            rawValue[p] = null;
            rawSpec[p] = -1;
        }
        if (elClasses[depth].Length > 0 || elId[depth].Length > 0)
        {
            for (int r = 0; r < ruleCount; r++) if (RuleMatches(r)) ApplyRule(r);
        }
        else
        {
            for (int i = 0; i < tagOnlyCount; i++)
            {
                int r = tagOnlyRules[i];
                if (RuleMatches(r)) ApplyRule(r);
            }
        }
        if (inlineStyle.Length > 0)
        {
            ParseDeclarations(inlineStyle);
            for (int i = 0; i < declCount; i++) ApplyDeclaration(declNames[i], declValues[i], 1000);
        }

        string value = rawValue[PFontSize];
        if (value != null) size = ResolveFontSize(value, parentSize);

        value = rawValue[PColor];
        if (value != null)
        {
            string parsed = ParseColor(value);
            if (parsed.Length > 0) color = parsed;
        }

        value = rawValue[PWeight];
        if (value != null)
        {
            if (value == "bold" || value == "bolder") bold = true;
            else if (value == "normal" || value == "lighter") bold = false;
            else
            {
                ParseNumber(value);
                if (numOk) bold = numValue >= 600f;
            }
        }

        value = rawValue[PStyle];
        if (value != null) italic = value == "italic" || value == "oblique";

        value = rawValue[PAlign];
        if (value != null)
        {
            if (value == "center") align = AlignCenter;
            else if (value == "right" || value == "end") align = AlignRight;
            else if (value == "left" || value == "start" || value == "justify") align = AlignLeft;
        }

        string bg = "";
        value = rawValue[PBackground];
        if (value != null) bg = ParseBackground(value);

        value = rawValue[PDisplay];
        if (value != null)
        {
            if (value == "none") hidden = true;
            else if (value.StartsWith("inline") || value == "contents") block = false;
            else block = true;
        }

        float marginTop = marginEm * size;
        float marginBottom = marginEm * size;
        value = rawValue[PMarginTop];
        if (value != null)
        {
            float px = ParseLength(value, size);
            if (px != Invalid) marginTop = Mathf.Max(0f, px);
        }
        value = rawValue[PMarginBottom];
        if (value != null)
        {
            float px = ParseLength(value, size);
            if (px != Invalid) marginBottom = Mathf.Max(0f, px);
        }

        float padV = 0f;
        value = rawValue[PPadV];
        if (value != null)
        {
            float px = ParseLength(value, size);
            if (px != Invalid) padV = Mathf.Max(0f, px);
        }

        // horizontal box; only blocks take part in layout
        float avail = elAvail[parent];
        if (block)
        {
            bool autoL = false;
            bool autoR = false;
            value = rawValue[PMarginLeft];
            if (value != null)
            {
                if (value == "auto") autoL = true;
                else
                {
                    float px = ParseBoxLength(value, size, avail);
                    if (px != Invalid) marginL = Mathf.Max(0f, px);
                }
            }
            value = rawValue[PMarginRight];
            if (value != null)
            {
                if (value == "auto") autoR = true;
                else
                {
                    float px = ParseBoxLength(value, size, avail);
                    if (px != Invalid) marginR = Mathf.Max(0f, px);
                }
            }
            value = rawValue[PPadLeft];
            if (value != null)
            {
                float px = ParseBoxLength(value, size, avail);
                if (px != Invalid) padL = Mathf.Max(0f, px);
            }
            value = rawValue[PPadRight];
            if (value != null)
            {
                float px = ParseBoxLength(value, size, avail);
                if (px != Invalid) padR = Mathf.Max(0f, px);
            }

            // max-width always narrows the box. width only does so next to an auto margin (the centring idiom):
            // widths of floats and flex items would otherwise squeeze text that a browser lays out side by side.
            float inner = avail - marginL - marginR;
            float want = inner;
            value = rawValue[PMaxWidth];
            if (value != null)
            {
                float px = ParseBoxLength(value, size, avail);
                if (px != Invalid && px > 0f && px < want) want = px;
            }
            value = rawValue[PWidth];
            if (value != null && (autoL || autoR))
            {
                float px = ParseBoxLength(value, size, avail);
                if (px != Invalid && px > 0f && px < want) want = px;
            }
            if (want < inner)
            {
                float extra = inner - want;
                if (autoL && autoR)
                {
                    marginL += extra * 0.5f;
                    marginR += extra * 0.5f;
                }
                else if (autoL) marginL += extra;
                else marginR += extra;
            }
            avail = Mathf.Max(80f, avail - marginL - marginR - padL - padR);
        }
        else
        {
            marginL = 0f;
            marginR = 0f;
            padL = 0f;
            padR = 0f;
        }

        value = rawValue[PLineHeight];
        if (value != null)
        {
            if (value == "normal") line = 0f;
            else
            {
                ParseNumber(value);
                if (numOk && numEnd == value.Length) line = numValue; // unitless: a multiple of the font size
                else
                {
                    float px = ParseLength(value, size);
                    if (px != Invalid && px > 0f) line = px / size;
                }
            }
        }

        value = rawValue[PTransform];
        if (value != null)
        {
            if (value == "uppercase") transform = 1;
            else if (value == "lowercase") transform = 2;
            else if (value == "capitalize") transform = 3;
            else if (value == "none") transform = 0;
        }

        value = rawValue[PWhiteSpace];
        if (value != null) pre = value.StartsWith("pre") || value == "break-spaces";

        value = rawValue[PTextIndent];
        if (value != null)
        {
            float px = ParseLength(value, size);
            if (px != Invalid) textIndent = Mathf.Max(0f, px);
        }

        value = rawValue[PListStyle];
        if (value != null && ListStyles.Contains(" " + value + " "))
        {
            if (value == "lower-latin") listStyle = "lower-alpha";
            else if (value == "upper-latin") listStyle = "upper-alpha";
            else listStyle = value;
        }

        elColor[depth] = color;
        elSize[depth] = Mathf.Clamp(size, 4f, 100f);
        elBold[depth] = bold;
        elItalic[depth] = italic;
        elAlign[depth] = align;
        elPre[depth] = pre;
        elBlock[depth] = block;
        elHidden[depth] = hidden;
        elBg[depth] = bg;
        elPadL[depth] = padL;
        elPadR[depth] = padR;
        elMarginL[depth] = marginL;
        elMarginR[depth] = marginR;
        elAvail[depth] = avail;
        elLine[depth] = line;
        elTransform[depth] = transform;
        elTextIndent[depth] = textIndent;
        elListStyle[depth] = listStyle;
        elPadV[depth] = padV;
        elMarginTop[depth] = marginTop;
        elMarginBottom[depth] = marginBottom;
    }

    private void ApplyRule(int rule)
    {
        string[] names = rNames[rule];
        string[] values = rValues[rule];
        int spec = rSpec[rule];
        for (int i = 0; i < names.Length; i++) ApplyDeclaration(names[i], values[i], spec);
    }

    private void SetRaw(int slot, string value, int spec)
    {
        if (spec < rawSpec[slot]) return;
        rawSpec[slot] = spec;
        rawValue[slot] = value;
    }

    private void ApplyDeclaration(string name, string value, int spec)
    {
        if (value.EndsWith("!important"))
        {
            value = value.Substring(0, value.Length - 10).Trim();
            spec += 10000;
        }
        if (value.IndexOf("var(") >= 0) value = ResolveVars(value);
        if (value.Length == 0 || value == "inherit" || value == "initial" || value == "unset") return;

        if (name == "color") SetRaw(PColor, value, spec);
        else if (name == "font-size") SetRaw(PFontSize, value, spec);
        else if (name == "font-weight") SetRaw(PWeight, value, spec);
        else if (name == "font-style") SetRaw(PStyle, value, spec);
        else if (name == "text-align") SetRaw(PAlign, value, spec);
        else if (name == "background-color" || name == "background") SetRaw(PBackground, value, spec);
        else if (name == "display") SetRaw(PDisplay, value, spec);
        else if (name == "visibility")
        {
            if (value == "hidden" || value == "collapse") SetRaw(PDisplay, "none", spec);
        }
        else if (name == "opacity")
        {
            ParseNumber(value);
            if (numOk && numValue <= 0f) SetRaw(PDisplay, "none", spec);
        }
        else if (name == "margin-top" || name == "margin-block-start") SetRaw(PMarginTop, value, spec);
        else if (name == "margin-bottom" || name == "margin-block-end") SetRaw(PMarginBottom, value, spec);
        else if (name == "margin-left" || name == "margin-inline-start") SetRaw(PMarginLeft, value, spec);
        else if (name == "margin-right" || name == "margin-inline-end") SetRaw(PMarginRight, value, spec);
        else if (name == "padding-top" || name == "padding-bottom" || name == "padding-block") SetRaw(PPadV, Token(value, 0), spec);
        else if (name == "padding-left" || name == "padding-inline-start") SetRaw(PPadLeft, value, spec);
        else if (name == "padding-right" || name == "padding-inline-end") SetRaw(PPadRight, value, spec);
        else if (name == "margin" || name == "padding")
        {
            // 1-4 values: top [right [bottom [left]]]
            string top = Token(value, 0);
            string right = Token(value, 1);
            string bottom = Token(value, 2);
            string left = Token(value, 3);
            if (top == null) return;
            if (right == null) right = top;
            if (bottom == null) bottom = top;
            if (left == null) left = right;
            if (name == "margin")
            {
                SetRaw(PMarginTop, top, spec);
                SetRaw(PMarginBottom, bottom, spec);
                SetRaw(PMarginLeft, left, spec);
                SetRaw(PMarginRight, right, spec);
            }
            else
            {
                SetRaw(PPadV, top, spec);
                SetRaw(PPadLeft, left, spec);
                SetRaw(PPadRight, right, spec);
            }
        }
        else if (name == "margin-inline" || name == "padding-inline" || name == "margin-block")
        {
            // 1-2 values: start [end]
            string start = Token(value, 0);
            string end = Token(value, 1);
            if (start == null) return;
            if (end == null) end = start;
            if (name == "margin-block")
            {
                SetRaw(PMarginTop, start, spec);
                SetRaw(PMarginBottom, end, spec);
            }
            else
            {
                SetRaw(name == "margin-inline" ? PMarginLeft : PPadLeft, start, spec);
                SetRaw(name == "margin-inline" ? PMarginRight : PPadRight, end, spec);
            }
        }
        else if (name == "width") SetRaw(PWidth, value, spec);
        else if (name == "max-width") SetRaw(PMaxWidth, value, spec);
        else if (name == "line-height") SetRaw(PLineHeight, value, spec);
        else if (name == "text-transform") SetRaw(PTransform, value, spec);
        else if (name == "white-space") SetRaw(PWhiteSpace, value, spec);
        else if (name == "text-indent") SetRaw(PTextIndent, value, spec);
        else if (name == "list-style-type") SetRaw(PListStyle, value, spec);
        else if (name == "list-style")
        {
            // type, position and image in any order: take the first word that names a type
            for (int i = 0; i < 3; i++)
            {
                string part = Token(value, i);
                if (part == null) break;
                if (!ListStyles.Contains(" " + part + " ")) continue;
                SetRaw(PListStyle, part, spec);
                break;
            }
        }
        else if (name == "font") ApplyFont(value, spec);
    }

    /// <summary>font shorthand: [style] [weight] size[/line-height] family. Everything from the family on is ignored.</summary>
    private void ApplyFont(string value, int spec)
    {
        SetRaw(PStyle, "normal", spec);
        SetRaw(PWeight, "normal", spec);
        string[] parts = value.Split(' ');
        for (int i = 0; i < parts.Length; i++)
        {
            string part = parts[i];
            if (part.Length == 0 || part == "normal" || part == "small-caps") continue;
            if (part == "italic" || part == "oblique")
            {
                SetRaw(PStyle, part, spec);
                continue;
            }
            if (part == "bold" || part == "bolder" || part == "lighter")
            {
                SetRaw(PWeight, part, spec);
                continue;
            }
            int slash = part.IndexOf('/');
            string sizePart = slash < 0 ? part : part.Substring(0, slash);
            ParseNumber(sizePart);
            if (numOk && numEnd == sizePart.Length && slash < 0)
            {
                SetRaw(PWeight, sizePart, spec); // a bare number in front of the size is the weight
                continue;
            }
            if (numOk || FontSizeKeywords.Contains(" " + sizePart + " "))
            {
                SetRaw(PFontSize, sizePart, spec);
                if (slash >= 0 && slash < part.Length - 1) SetRaw(PLineHeight, part.Substring(slash + 1), spec);
            }
            return;
        }
    }

    /// <summary>The index-th word of a space separated value, or null.</summary>
    private string Token(string value, int index)
    {
        string[] parts = value.Split(' ');
        int found = 0;
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length == 0) continue;
            if (found == index) return parts[i];
            found++;
        }
        return null;
    }

    /// <summary>Replaces var(--name, fallback) with the custom property's value, the fallback or nothing.</summary>
    private string ResolveVars(string value)
    {
        for (int guard = 0; guard < 6; guard++)
        {
            int start = value.IndexOf("var(");
            if (start < 0) break;
            int level = 1;
            int i = start + 4;
            while (i < value.Length && level > 0)
            {
                char ch = value[i];
                if (ch == '(') level++;
                else if (ch == ')') level--;
                i++;
            }
            if (level != 0) return "";

            string inner = value.Substring(start + 4, i - start - 5);
            int comma = inner.IndexOf(',');
            string varName = (comma < 0 ? inner : inner.Substring(0, comma)).Trim();
            string replacement = comma < 0 ? "" : inner.Substring(comma + 1).Trim();
            for (int v = varCount - 1; v >= 0; v--)
            {
                if (varNames[v] != varName) continue;
                replacement = varValues[v];
                break;
            }
            value = value.Substring(0, start) + replacement + value.Substring(i);
        }
        return value.Trim();
    }

    // ---------- stylesheet ----------

    private void ParseCss(string css)
    {
        for (int guard = 0; guard < 300; guard++)
        {
            int start = css.IndexOf("/*");
            if (start < 0) break;
            int end = css.IndexOf("*/", start + 2);
            if (end < 0)
            {
                css = css.Substring(0, start);
                break;
            }
            css = css.Substring(0, start) + css.Substring(end + 2);
        }

        int pos = 0;
        int length = css.Length;
        for (int guard = 0; guard < 2000 && pos < length; guard++)
        {
            int open = css.IndexOf('{', pos);
            if (open < 0) break;
            string selector = css.Substring(pos, open - pos).Trim();

            // statements such as "@import ...;" in front of the rule
            int semicolon = selector.LastIndexOf(";");
            if (semicolon >= 0) selector = selector.Substring(semicolon + 1).Trim();
            // the "}" that ends an @media block whose rules were read
            int brace = selector.LastIndexOf("}");
            if (brace >= 0) selector = selector.Substring(brace + 1).Trim();

            if (selector.StartsWith("@"))
            {
                // a matching @media (and @supports / @layer) block: read the rules inside as if they were top level
                string atRule = selector.ToLower();
                if ((atRule.StartsWith("@media") && MediaMatches(atRule.Substring(6))) || atRule.StartsWith("@supports") || atRule.StartsWith("@layer"))
                {
                    pos = open + 1;
                    continue;
                }

                // other @media, @keyframes, @font-face...: skip the whole block including nested rules
                int level = 1;
                int i = open + 1;
                while (i < length && level > 0)
                {
                    int nextOpen = css.IndexOf('{', i);
                    int nextClose = css.IndexOf('}', i);
                    if (nextClose < 0)
                    {
                        i = length;
                        break;
                    }
                    if (nextOpen >= 0 && nextOpen < nextClose)
                    {
                        level++;
                        i = nextOpen + 1;
                    }
                    else
                    {
                        level--;
                        i = nextClose + 1;
                    }
                }
                pos = i;
                continue;
            }

            int close = css.IndexOf('}', open);
            if (close < 0) break;
            pos = close + 1;
            AddRules(selector, css.Substring(open + 1, close - open - 1));
        }
    }

    /// <summary>Media query against a screen pagePx wide. Anything but screen / all / min-width / max-width does not match.</summary>
    private bool MediaMatches(string query)
    {
        string[] alternatives = query.Split(',');
        for (int a = 0; a < alternatives.Length; a++)
        {
            string[] terms = alternatives[a].Replace(" and ", "&").Split('&');
            bool ok = true;
            bool any = false;
            for (int t = 0; t < terms.Length && ok; t++)
            {
                string term = terms[t].Trim();
                if (term.Length == 0) continue;
                any = true;
                if (term == "screen" || term == "all" || term == "only screen" || term == "only all") continue;
                ok = false;
                if (!term.EndsWith(")") || !(term.StartsWith("(min-width:") || term.StartsWith("(max-width:"))) continue;
                float px = ParseLength(term.Substring(11, term.Length - 12).Trim(), 16f);
                if (px == Invalid) continue;
                ok = term[2] == 'i' ? pagePx >= px : pagePx <= px;
            }
            if (ok && any) return true;
        }
        return false;
    }

    private void ParseDeclarations(string body)
    {
        declCount = 0;
        string[] parts = body.Split(';');
        for (int i = 0; i < parts.Length && declCount < declNames.Length; i++)
        {
            string part = parts[i];
            int colon = part.IndexOf(':');
            if (colon <= 0) continue;
            string name = part.Substring(0, colon).Trim().ToLower();
            string value = part.Substring(colon + 1).Replace('\n', ' ').Replace('\t', ' ').Replace('\r', ' ').Trim().ToLower();
            if (name.Length == 0 || value.Length == 0) continue;
            declNames[declCount] = name;
            declValues[declCount] = value;
            declCount++;
        }
    }

    private void AddRules(string selectorList, string body)
    {
        ParseDeclarations(body);
        if (declCount == 0) return;
        string[] names = new string[declCount];
        string[] values = new string[declCount];
        for (int i = 0; i < declCount; i++)
        {
            names[i] = declNames[i];
            values[i] = declValues[i];
        }

        string[] selectors = selectorList.Split(',');
        for (int s = 0; s < selectors.Length && ruleCount < MaxRules; s++)
        {
            string selector = selectors[s].Replace('\n', ' ').Replace('\t', ' ').Replace('\r', ' ').Trim();
            if (selector.Length == 0) continue;
            if (selector.IndexOf(':') >= 0)
            {
                // :root is the html element, and every link counts as unvisited
                selector = selector.Replace(":root", "html").Replace(":any-link", "").Replace(":link", "");
                if (selector.Length == 0) continue;
            }
            if (selector == "html" || selector == "body" || selector == "*")
            {
                for (int i = 0; i < declCount && varCount < MaxVars; i++)
                {
                    if (!names[i].StartsWith("--")) continue;
                    varNames[varCount] = names[i];
                    varValues[varCount] = values[i];
                    varCount++;
                }
            }
            // other pseudo-classes and attribute selectors are not supported: such rules are skipped rather than over-applied
            if (selector.IndexOf(':') >= 0 || selector.IndexOf('[') >= 0) continue;
            // child / sibling combinators are treated as descendant combinators
            selector = selector.Replace('>', ' ').Replace('+', ' ').Replace('~', ' ');

            string[] parts = selector.Split(' ');
            int count = 0;
            for (int i = 0; i < parts.Length; i++) if (parts[i].Length > 0) count++;
            if (count == 0) continue;

            string[] tags = new string[count];
            string[] ids = new string[count];
            string[] classes = new string[count];
            int spec = 0;
            int c = 0;
            for (int i = 0; i < parts.Length; i++)
            {
                string compound = parts[i];
                if (compound.Length == 0) continue;

                int dot = compound.IndexOf('.');
                int hash = compound.IndexOf('#');
                int first = dot < 0 ? hash : (hash < 0 ? dot : Mathf.Min(dot, hash));
                string tag = first < 0 ? compound : compound.Substring(0, first);
                string rest = first < 0 ? "" : compound.Substring(first);
                tag = tag.ToLower();
                if (tag == "*") tag = "";
                string id = "";
                string cls = "";
                for (int guard = 0; guard < 8 && rest.Length > 1; guard++)
                {
                    char kind = rest[0];
                    int nextDot = rest.IndexOf('.', 1);
                    int nextHash = rest.IndexOf('#', 1);
                    int next = nextDot < 0 ? nextHash : (nextHash < 0 ? nextDot : Mathf.Min(nextDot, nextHash));
                    if (next < 0) next = rest.Length;
                    string name = rest.Substring(1, next - 1);
                    if (kind == '.')
                    {
                        cls = cls + " " + name;
                        spec += 10;
                    }
                    else
                    {
                        id = name;
                        spec += 100;
                    }
                    rest = rest.Substring(next);
                }
                if (tag.Length > 0) spec += 1;

                tags[c] = tag;
                ids[c] = id;
                classes[c] = cls;
                c++;
            }

            rTags[ruleCount] = tags;
            rIds[ruleCount] = ids;
            rClasses[ruleCount] = classes;
            rNames[ruleCount] = names;
            rValues[ruleCount] = values;
            rSpec[ruleCount] = spec;
            if (ids[count - 1].Length == 0 && classes[count - 1].Length == 0)
            {
                tagOnlyRules[tagOnlyCount] = ruleCount;
                tagOnlyCount++;
            }
            ruleCount++;
        }
    }

    private bool CompoundMatches(string tag, string id, string classes, int d)
    {
        if (tag.Length > 0 && tag != elTag[d]) return false;
        if (id.Length > 0 && id != elId[d]) return false;
        if (classes.Length > 0)
        {
            string elementClasses = elClasses[d];
            if (elementClasses.Length == 0) return false;
            string[] names = classes.Split(' ');
            for (int i = 0; i < names.Length; i++)
            {
                if (names[i].Length == 0) continue;
                if (!elementClasses.Contains(" " + names[i] + " ")) return false;
            }
        }
        return true;
    }

    /// <summary>Does the rule match the element on top of the stack? Earlier compounds must match some ancestor, in order.</summary>
    private bool RuleMatches(int rule)
    {
        string[] tags = rTags[rule];
        string[] ids = rIds[rule];
        string[] classes = rClasses[rule];
        int last = tags.Length - 1;
        if (!CompoundMatches(tags[last], ids[last], classes[last], depth)) return false;

        int d = depth - 1;
        for (int c = last - 1; c >= 0; c--)
        {
            bool found = false;
            while (d >= 1)
            {
                bool match = CompoundMatches(tags[c], ids[c], classes[c], d);
                d--;
                if (match)
                {
                    found = true;
                    break;
                }
            }
            if (!found) return false;
        }
        return true;
    }

    // ---------- values ----------

    private void ParseNumber(string text)
    {
        numOk = false;
        numValue = 0f;
        int i = 0;
        int length = text.Length;
        bool negative = false;
        if (i < length && (text[i] == '-' || text[i] == '+'))
        {
            negative = text[i] == '-';
            i++;
        }
        float value = 0f;
        float scale = 1f;
        bool fraction = false;
        bool any = false;
        while (i < length)
        {
            char ch = text[i];
            if (ch == '.')
            {
                if (fraction) break;
                fraction = true;
                i++;
                continue;
            }
            int digit = "0123456789".IndexOf(ch);
            if (digit < 0) break;
            any = true;
            if (fraction)
            {
                scale *= 0.1f;
                value += digit * scale;
            }
            else value = value * 10f + digit;
            i++;
        }
        numOk = any;
        numValue = negative ? -value : value;
        numEnd = i;
    }

    /// <summary>CSS length in px, or Invalid. em and % are relative to emBase.</summary>
    private float ParseLength(string value, float emBase)
    {
        ParseNumber(value);
        if (!numOk) return Invalid;
        string unit = value.Substring(numEnd).Trim();
        if (unit.Length == 0 || unit == "px") return numValue;
        if (unit == "em") return numValue * emBase;
        if (unit == "rem") return numValue * 16f;
        if (unit == "%") return numValue * 0.01f * emBase;
        if (unit == "pt") return numValue * 1.3333f;
        if (unit == "vw") return numValue * 0.01f * pagePx;
        if (unit == "ch" || unit == "ex") return numValue * 0.5f * emBase;
        return Invalid;
    }

    /// <summary>Horizontal length (margin, padding, width) in px, or Invalid. % is relative to the containing block.</summary>
    private float ParseBoxLength(string value, float emBase, float containerWidth)
    {
        if (!value.EndsWith("%")) return ParseLength(value, emBase);
        ParseNumber(value);
        return numOk ? numValue * 0.01f * containerWidth : Invalid;
    }

    private float ResolveFontSize(string value, float parentSize)
    {
        if (value == "medium") return 16f;
        if (value == "small") return 13f;
        if (value == "x-small") return 10f;
        if (value == "xx-small") return 9f;
        if (value == "large") return 18f;
        if (value == "x-large") return 24f;
        if (value == "xx-large") return 32f;
        if (value == "smaller") return parentSize * 0.83f;
        if (value == "larger") return parentSize * 1.2f;
        float px = ParseLength(value, parentSize);
        return px == Invalid || px <= 0f ? parentSize : px;
    }

    /// <summary>The colour of a background / background-color value, or "" (none, images and gradients are ignored).</summary>
    private string ParseBackground(string value)
    {
        string whole = ParseColor(value);
        if (whole.Length > 0) return whole;
        if (value.IndexOf('(') >= 0) return ""; // url(...), gradients
        string[] parts = value.Split(' ');
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length == 0) continue;
            string parsed = ParseColor(parts[i]);
            if (parsed.Length > 0) return parsed;
        }
        return "";
    }

    /// <summary>Normalises a CSS colour to "#RRGGBB", or "" when it is not a supported (opaque) colour.</summary>
    private string ParseColor(string value)
    {
        value = value.Trim();
        if (value.Length == 0) return "";

        if (value[0] == '#')
        {
            string hex = value.Substring(1).ToUpper();
            if (hex.Length == 3 || hex.Length == 4)
                hex = hex.Substring(0, 1) + hex.Substring(0, 1) + hex.Substring(1, 1) + hex.Substring(1, 1) + hex.Substring(2, 1) + hex.Substring(2, 1);
            else if (hex.Length == 8) hex = hex.Substring(0, 6);
            if (hex.Length != 6) return "";
            for (int i = 0; i < 6; i++) if (HexDigits.IndexOf(hex[i]) < 0) return "";
            return "#" + hex;
        }

        if (value.StartsWith("rgb"))
        {
            int open = value.IndexOf('(');
            int close = value.IndexOf(')');
            if (open < 0 || close < open) return "";
            string[] parts = value.Substring(open + 1, close - open - 1).Replace('/', ',').Replace(' ', ',').Split(',');
            string result = "#";
            int found = 0;
            for (int i = 0; i < parts.Length && found < 3; i++)
            {
                if (parts[i].Length == 0) continue;
                ParseNumber(parts[i]);
                if (!numOk) return "";
                float channel = parts[i].EndsWith("%") ? numValue * 2.55f : numValue;
                result = result + ByteHex(channel);
                found++;
            }
            return found == 3 ? result : "";
        }

        if (value.StartsWith("hsl"))
        {
            int open = value.IndexOf('(');
            int close = value.IndexOf(')');
            if (open < 0 || close < open) return "";
            string[] parts = value.Substring(open + 1, close - open - 1).Replace('/', ',').Replace(' ', ',').Split(',');
            float hue = 0f;
            float saturation = 0f;
            float lightness = 0f;
            int found = 0;
            for (int i = 0; i < parts.Length && found < 3; i++)
            {
                if (parts[i].Length == 0) continue;
                ParseNumber(parts[i]);
                if (!numOk) return "";
                if (found == 0) hue = numValue;
                else if (found == 1) saturation = Mathf.Clamp01(numValue * 0.01f);
                else lightness = Mathf.Clamp01(numValue * 0.01f);
                found++;
            }
            if (found != 3) return "";
            // HSL -> RGB
            hue = (hue % 360f + 360f) % 360f / 60f;
            float chroma = (1f - Mathf.Abs(2f * lightness - 1f)) * saturation;
            float second = chroma * (1f - Mathf.Abs(hue % 2f - 1f));
            float r = 0f;
            float g = 0f;
            float b = 0f;
            if (hue < 1f) { r = chroma; g = second; }
            else if (hue < 2f) { r = second; g = chroma; }
            else if (hue < 3f) { g = chroma; b = second; }
            else if (hue < 4f) { g = second; b = chroma; }
            else if (hue < 5f) { r = second; b = chroma; }
            else { r = chroma; b = second; }
            float lift = lightness - chroma * 0.5f;
            return "#" + ByteHex((r + lift) * 255f) + ByteHex((g + lift) * 255f) + ByteHex((b + lift) * 255f);
        }

        int at = NamedColors.IndexOf(";" + value + "=");
        if (at < 0) return "";
        return NamedColors.Substring(at + value.Length + 2, 7);
    }

    private string ByteHex(float channel)
    {
        int n = Mathf.Clamp(Mathf.RoundToInt(channel), 0, 255);
        return HexDigits.Substring(n / 16, 1) + HexDigits.Substring(n % 16, 1);
    }

    private Color HexToColor(string hex)
    {
        if (hex.Length != 7) return Color.black;
        float r = (HexDigits.IndexOf(hex[1]) * 16 + HexDigits.IndexOf(hex[2])) / 255f;
        float g = (HexDigits.IndexOf(hex[3]) * 16 + HexDigits.IndexOf(hex[4])) / 255f;
        float b = (HexDigits.IndexOf(hex[5]) * 16 + HexDigits.IndexOf(hex[6])) / 255f;
        return new Color(r, g, b, 1f);
    }
}
