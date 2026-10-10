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
/// external stylesheets cannot be fetched): color, font-size, font-weight, font-style, text-align, background(-color),
/// margin-top/bottom, padding and display. Selectors: tag, .class, #id, compounds and descendant combinations.
/// </summary>
[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class VRCbrowser : UdonSharpBehaviour
{
    [Header("UI")]
    [SerializeField] private VRCUrlInputField urlInput = null;
    [SerializeField] private Text statusText = null;
    [SerializeField] private ScrollRect scrollRect = null;
    [SerializeField] private Image pageBackground = null;
    [SerializeField] private RectTransform[] blockRects = null;
    [SerializeField] private Image[] blockImages = null;
    [SerializeField] private Text[] blockTexts = null;
    [SerializeField] private RawImage imageView = null;
    [SerializeField] private Vector2 imageMaxSize = new Vector2(1500f, 800f);

    [Header("Pages")]
    [SerializeField] private VRCUrl homeUrl = VRCUrl.Empty;
    [SerializeField] private VRCUrl[] bookmarkUrls = null;
    [SerializeField] private string[] bookmarkLabels = null;
    [SerializeField] private GameObject[] bookmarkButtons = null;
    [SerializeField] private Text[] bookmarkButtonLabels = null;
    [SerializeField] private GameObject menuButton = null; // opens the list of frequently used sites
    [SerializeField] private GameObject menuPanel = null;
    [SerializeField] private float menuRowHeight = 58f;
    [SerializeField, TextArea(5, 20)] private string welcomePage = "";

    [Header("Appearance")]
    [SerializeField] private int baseFontSize = 30; // UI units for a 16px CSS font; must match the block Text components
    [SerializeField] private float sidePadding = 16f;
    [SerializeField] private Color plainTextColor = new Color(0.90f, 0.92f, 0.95f);

    [Header("Limits")]
    [SerializeField] private int maxLines = 1500;
    [SerializeField] private int maxCharsPerBlock = 2500;
    [SerializeField] private int maxHtmlChars = 60000;
    [SerializeField] private int maxHtmlTags = 3000;

    [Header("Scripts")]
    [SerializeField] private VRCbrowserJs js = null; // optional: runs inline <script> elements; leave empty to ignore scripts

    [UdonSynced] private VRCUrl syncedUrl = VRCUrl.Empty;

    private string loadedUrl = "";
    private VRCUrl[] history = null;
    private int historyCount = 0;
    private VRCImageDownloader imageDownloader = null;

    private void Start()
    {
        history = new VRCUrl[16];
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
        if (result.Url.Get() != loadedUrl) return; // a newer page was requested meanwhile
        string body = result.Result;
        statusText.text = "";
        if (LooksLikeHtml(body)) ShowHtml(body);
        else ShowText(body);
    }

    public override void OnStringLoadError(IVRCStringDownload result)
    {
        if (result.Url.Get() != loadedUrl) return;
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
    private float[] bPadH = null;
    private float[] bPadTop = null;
    private float[] bPadBottom = null;
    private float[] bGap = null;
    private float unitsPerPx = 1.875f;

    private void InitRenderer()
    {
        int n = blockTexts.Length;
        bText = new string[n];
        bAlign = new int[n];
        bBg = new string[n];
        bOwner = new int[n];
        bPadH = new float[n];
        bPadTop = new float[n];
        bPadBottom = new float[n];
        bGap = new float[n];
        unitsPerPx = baseFontSize / 16f;
        InitHtml();
    }

    private void BeginBlocks()
    {
        blockCount = 0;
        blocksTruncated = false;
    }

    /// <summary>Adds a paragraph. Sizes are in CSS px. owner: id of the element whose background box it sits in (0 = page).</summary>
    private void AddBlock(string text, int align, string bg, int owner, float padH, float padV, float gap)
    {
        if (blockCount > 0)
        {
            int last = blockCount - 1;
            if (bOwner[last] == owner && bAlign[last] == align && bBg[last] == bg && bText[last].Length + text.Length < maxCharsPerBlock)
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
        bPadH[blockCount] = padH * unitsPerPx;
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
            RectTransform textRect = text.rectTransform;
            textRect.offsetMin = new Vector2(bPadH[i], bPadBottom[i]);
            textRect.offsetMax = new Vector2(-bPadH[i], -bPadTop[i]);

            // width first, so the text can report the height it needs at that width
            rect.sizeDelta = new Vector2(-2f * sidePadding, 100f);
            float height = text.preferredHeight + bPadTop[i] + bPadBottom[i];
            y += bGap[i];
            rect.anchoredPosition = new Vector2(0f, -y);
            rect.sizeDelta = new Vector2(-2f * sidePadding, height);
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
            AddBlock(line, AlignLeft, "", 0, 0f, 0f, 0f);
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

    private const string BlockTags = " html body div p h1 h2 h3 h4 h5 h6 ul ol li section article header footer main nav aside blockquote pre table thead tbody tfoot tr hr figure figcaption form fieldset dl dt dd address details summary center ";
    private const string VoidTags = " br hr img input meta link area base col embed source track wbr ";
    private const string HexDigits = "0123456789ABCDEF";
    private const string NamedColors = ";black=#000000;white=#FFFFFF;red=#FF0000;green=#008000;blue=#0000FF;yellow=#FFFF00;orange=#FFA500;purple=#800080;gray=#808080;grey=#808080;silver=#C0C0C0;navy=#000080;teal=#008080;maroon=#800000;lime=#00FF00;aqua=#00FFFF;cyan=#00FFFF;fuchsia=#FF00FF;magenta=#FF00FF;pink=#FFC0CB;brown=#A52A2A;olive=#808000;gold=#FFD700;indigo=#4B0082;violet=#EE82EE;tomato=#FF6347;crimson=#DC143C;coral=#FF7F50;salmon=#FA8072;khaki=#F0E68C;beige=#F5F5DC;ivory=#FFFFF0;lightgray=#D3D3D3;lightgrey=#D3D3D3;darkgray=#A9A9A9;darkgrey=#A9A9A9;dimgray=#696969;whitesmoke=#F5F5F5;skyblue=#87CEEB;royalblue=#4169E1;steelblue=#4682B4;darkblue=#00008B;lightblue=#ADD8E6;darkgreen=#006400;lightgreen=#90EE90;darkred=#8B0000;hotpink=#FF69B4;";

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
    private const int PPadH = 10;
    private const int PropCount = 11;

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
    private float[] elPadH = null;
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
    private float curPadH = 0f;
    private float curPadV = 0f;
    private float pendingGap = 0f;
    private string pendingPrefix = "";

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
        elPadH = new float[MaxDepth];
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
        if (js != null && html.ToLower().Contains("<script")) html = js._Prepare(html, loadedUrl);
        RenderHtml(html, true);
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
        elPadH[0] = 0f;
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

        if (blockCount == 0) AddBlock("<color=#888888>(表示できる内容がありません)</color>", AlignLeft, "", 0, 0f, 0f, 0f);
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
        if (VoidTags.Contains(" " + tag + " ")) return;

        AutoClose(tag);
        if (depth >= MaxDepth - 1) return; // too deep: treat as transparent

        depth++;
        elTag[depth] = tag;
        elSerial[depth] = nextSerial;
        nextSerial++;
        bool hasAttributes = lowerAttrs.IndexOf('=') >= 0;
        elId[depth] = hasAttributes ? GetAttribute(attrs, lowerAttrs, "id") : "";
        string classes = hasAttributes ? GetAttribute(attrs, lowerAttrs, "class") : "";
        elClasses[depth] = classes.Length > 0 ? " " + classes + " " : "";
        ComputeStyle(tag, hasAttributes ? GetAttribute(attrs, lowerAttrs, "style") : "");

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

        if (tag == "li")
        {
            string indent = "";
            string bullet = "• ";
            bool found = false;
            for (int d = depth - 1; d >= 1; d--)
            {
                if (elListType[d] == 0) continue;
                if (!found)
                {
                    found = true;
                    if (elListType[d] == 2)
                    {
                        elListCount[d] = elListCount[d] + 1;
                        bullet = elListCount[d] + ". ";
                    }
                }
                else indent = indent + "　";
            }
            pendingPrefix = indent + bullet;
        }
        else if ((tag == "td" || tag == "th") && curStarted)
        {
            curText = curText + "　";
            curEndsWithSpace = true;
        }
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
        if (elBlock[depth])
        {
            FlushParagraph();
            pendingGap = Mathf.Max(pendingGap, elMarginBottom[depth]);
        }
        depth--;
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
        if (text.IndexOf('<') >= 0) text = text.Replace("<", "<​"); // literal "<" must not be read as a rich text tag

        if (!curStarted)
        {
            curStarted = true;
            curAlign = elAlign[depth];
            curOwner = 0;
            curBg = "";
            curPadH = 0f;
            curPadV = 0f;
            for (int d = depth; d >= 1; d--)
            {
                if (!elBlock[d] || elBg[d].Length == 0 || elTag[d] == "body" || elTag[d] == "html") continue;
                curOwner = elSerial[d];
                curBg = elBg[d];
                curPadH = elPadH[d];
                curPadV = elPadV[d];
                break;
            }
            curText = pendingPrefix;
            pendingPrefix = "";
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
            AddBlock(curText.TrimEnd(), curAlign, curBg, curOwner, curPadH, curPadV, pendingGap);
            pendingGap = 0f;
        }
        ResetParagraph();
    }

    private string DecodeEntities(string text)
    {
        return text.Replace("&nbsp;", " ").Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"").Replace("&#39;", "'").Replace("&#x27;", "'").Replace("&apos;", "'").Replace("&#34;", "\"").Replace("&#x2F;", "/")
            .Replace("&copy;", "©").Replace("&mdash;", "—").Replace("&ndash;", "–").Replace("&hellip;", "…").Replace("&laquo;", "«").Replace("&raquo;", "»")
            .Replace("&amp;", "&");
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
        // not inherited
        bool block = BlockTags.Contains(" " + tag + " ");
        bool hidden = elHidden[parent];
        float marginEm = 0f;

        // browser defaults
        if (tag == "h1") { size = parentSize * 2f; bold = true; marginEm = 0.67f; }
        else if (tag == "h2") { size = parentSize * 1.5f; bold = true; marginEm = 0.83f; }
        else if (tag == "h3") { size = parentSize * 1.17f; bold = true; marginEm = 1f; }
        else if (tag == "h4") { bold = true; marginEm = 1.33f; }
        else if (tag == "h5") { size = parentSize * 0.83f; bold = true; marginEm = 1.67f; }
        else if (tag == "h6") { size = parentSize * 0.67f; bold = true; marginEm = 2.33f; }
        else if (tag == "p" || tag == "ul" || tag == "ol" || tag == "blockquote" || tag == "dl" || tag == "figure") marginEm = 1f;
        else if (tag == "pre") { marginEm = 1f; pre = true; }
        else if (tag == "b" || tag == "strong") bold = true;
        else if (tag == "th") { bold = true; }
        else if (tag == "i" || tag == "em" || tag == "cite" || tag == "var") italic = true;
        else if (tag == "a") color = "#0000EE";
        else if (tag == "small" || tag == "sub" || tag == "sup") size = parentSize * 0.83f;
        else if (tag == "center") align = AlignCenter;
        else if (tag == "select" || tag == "option" || tag == "datalist" || tag == "iframe" || tag == "object" || tag == "canvas" || tag == "video" || tag == "audio") hidden = true;

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
        float padH = 0f;
        value = rawValue[PPadV];
        if (value != null)
        {
            float px = ParseLength(value, size);
            if (px != Invalid) padV = Mathf.Max(0f, px);
        }
        value = rawValue[PPadH];
        if (value != null)
        {
            float px = ParseLength(value, size);
            if (px != Invalid) padH = Mathf.Max(0f, px);
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
        elPadH[depth] = padH;
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
        else if (name == "margin-top") SetRaw(PMarginTop, value, spec);
        else if (name == "margin-bottom") SetRaw(PMarginBottom, value, spec);
        else if (name == "padding-top" || name == "padding-bottom") SetRaw(PPadV, value, spec);
        else if (name == "padding-left" || name == "padding-right") SetRaw(PPadH, value, spec);
        else if (name == "margin" || name == "padding")
        {
            // 1-4 values: top [right [bottom [left]]]
            string[] parts = value.Split(' ');
            string top = null;
            string right = null;
            string bottom = null;
            int found = 0;
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i].Length == 0) continue;
                if (found == 0) top = parts[i];
                else if (found == 1) right = parts[i];
                else if (found == 2) bottom = parts[i];
                found++;
            }
            if (top == null) return;
            if (right == null) right = top;
            if (bottom == null) bottom = top;
            if (name == "margin")
            {
                SetRaw(PMarginTop, top, spec);
                SetRaw(PMarginBottom, bottom, spec);
            }
            else
            {
                SetRaw(PPadV, top, spec);
                SetRaw(PPadH, right, spec);
            }
        }
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

            if (selector.StartsWith("@"))
            {
                // @media, @keyframes, @font-face...: skip the whole block including nested rules
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
            // pseudo-classes and attribute selectors are not supported: such rules are skipped rather than over-applied
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
        return Invalid;
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
                int n = Mathf.Clamp(Mathf.RoundToInt(channel), 0, 255);
                result = result + HexDigits.Substring(n / 16, 1) + HexDigits.Substring(n % 16, 1);
                found++;
            }
            return found == 3 ? result : "";
        }

        int at = NamedColors.IndexOf(";" + value + "=");
        if (at < 0) return "";
        return NamedColors.Substring(at + value.Length + 2, 7);
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
