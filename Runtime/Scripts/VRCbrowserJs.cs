using System;
using UdonSharp;
using UnityEngine;
using VRC.SDK3.Data;

/// <summary>
/// Small JavaScript runtime for VRCbrowser. Inline script elements are compiled to bytecode and run on a stack VM
/// that is time-sliced over frames, so a slow script cannot freeze the instance.
///
/// The "DOM" is the page's HTML source: element lookups search that string and mutations rewrite it, after which
/// the browser renders it again. Scripts run locally for each player; only the URL is synced.
///
/// Supported: var/let/const (function scoped), functions, closures, arrow functions, if/else, for, for-of, for-in,
/// while, do-while, switch, break/continue/return, template literals, arrays, objects, strings, Math, JSON, Date,
/// console, document.write, getElementById / querySelector(All), textContent / innerHTML / style / classList /
/// attributes, setTimeout / setInterval, window.onload. Not supported: external scripts, fetch, classes,
/// prototypes, regular expressions, exceptions (catch blocks are skipped), click events.
/// </summary>
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class VRCbrowserJs : UdonSharpBehaviour
{
    [SerializeField] private VRCbrowser browser = null;
    [Tooltip("Show script errors in the browser's status bar. Useful while writing a page; scripts of ordinary websites fail most of the time, so it is off by default. Errors always go to the log.")]
    [SerializeField] private bool showErrors = false;

    [Header("Limits")]
    [SerializeField] private int maxScriptChars = 8000;     // per script element
    [SerializeField] private float frameBudgetMs = 4f;      // time the VM may use per frame
    [SerializeField] private int maxStepsPerRun = 200000;   // instructions per script / callback before it is aborted
    [SerializeField] private float minRenderInterval = 0.25f; // seconds between two re-renders caused by scripts
    [SerializeField] private float minTimerDelay = 0.05f;   // lower bound for setTimeout / setInterval, seconds

    // ---------- tokens ----------
    private const int T_EOF = 0;
    private const int T_NUM = 1;
    private const int T_STR = 2;
    private const int T_ID = 3;
    private const int T_P = 4;

    private const string IdentStart = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ_$";
    private const string IdentPart = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ_$0123456789";
    private const string Digits = "0123456789";
    private const string HexLower = "0123456789abcdef";
    private const string Punct3 = " === !== **= ... ";
    private const string Punct2 = " == != <= >= && || ?? ++ -- += -= *= /= %= => ** ?. ";

    private int[] tType = null;
    private string[] tStr = null;
    private double[] tNum = null;
    private int[] tLine = null;
    private bool[] tHoisted = null;
    private int tCount = 0;
    private int tp = 0;
    private int[] tplStack = null;
    private int tplCount = 0;

    // ---------- bytecode ----------
    private const int OP_HALT = 0;
    private const int OP_PUSHN = 1;
    private const int OP_PUSHS = 2;
    private const int OP_TRUE = 3;
    private const int OP_FALSE = 4;
    private const int OP_NULL = 5;
    private const int OP_UNDEF = 6;
    private const int OP_LOAD = 7;
    private const int OP_STORE = 8;
    private const int OP_DECL = 9;
    private const int OP_DECLU = 10;
    private const int OP_POP = 11;
    private const int OP_DUP = 12;
    private const int OP_DUP2 = 13;
    private const int OP_GETM = 14;
    private const int OP_SETM = 15;
    private const int OP_GETI = 16;
    private const int OP_SETI = 17;
    private const int OP_ADD = 18;
    private const int OP_SUB = 19;
    private const int OP_MUL = 20;
    private const int OP_DIV = 21;
    private const int OP_MOD = 22;
    private const int OP_POW = 23;
    private const int OP_EQ = 24;
    private const int OP_NE = 25;
    private const int OP_SEQ = 26;
    private const int OP_SNE = 27;
    private const int OP_LT = 28;
    private const int OP_GT = 29;
    private const int OP_LE = 30;
    private const int OP_GE = 31;
    private const int OP_NOT = 32;
    private const int OP_NEG = 33;
    private const int OP_POS = 34;
    private const int OP_TYPEOF = 35;
    private const int OP_JMP = 36;
    private const int OP_JF = 37;
    private const int OP_JFK = 38;  // jump if falsy and keep the value, else pop
    private const int OP_JTK = 39;  // jump if truthy and keep the value, else pop
    private const int OP_JNK = 40;  // jump if not null/undefined and keep the value, else pop
    private const int OP_CALL = 41;
    private const int OP_CALLM = 42;
    private const int OP_NEW = 43;
    private const int OP_RET = 44;
    private const int OP_CLOSURE = 45;
    private const int OP_ARR = 46;
    private const int OP_OBJ = 47;
    private const int OP_STASH = 48;
    private const int OP_UNSTASH = 49;
    private const int OP_KEYS = 50;
    private const int OP_THROW = 51;
    private const int OP_DELM = 52;

    private const int MaxCode = 60000;
    private int[] cOp = null;
    private int[] cA = null;
    private string[] cS = null;
    private double[] cN = null;
    private int[] cLine = null;
    private int cLen = 0;
    private string cErr = null;
    private int hiddenCounter = 0;

    private const int MaxFunctions = 512;
    private int[] fEntry = null;
    private string[][] fParams = null;
    private bool[] fArrow = null;
    private int fCount = 0;

    private int[] brk = null;
    private int brkCount = 0;
    private int[] cnt = null;
    private int cntCount = 0;

    private int preludeLen = 0;
    private int preludeFunctions = 0;

    // ---------- VM ----------
    private const int K_CLOSURE = 1;
    private const int K_HOST = 2;
    private const int K_ELEM = 3;
    private const int K_STYLE = 4;
    private const int K_CLASSLIST = 5;
    private const int K_DATE = 6;

    private const string ParentKey = " p";
    private const string HostNames = " document window globalThis console Math JSON Object Array Number String Boolean Date location navigator parseInt parseFloat isNaN isFinite alert setTimeout setInterval clearTimeout clearInterval requestAnimationFrame ";
    private const string ArrayHof = " forEach map filter find findIndex some every reduce sort ";
    private const string Prelude =
        "function __forEach(a,f){for(var i=0;i<a.length;i++)f(a[i],i,a);}" +
        "function __map(a,f){var r=[];for(var i=0;i<a.length;i++)r.push(f(a[i],i,a));return r;}" +
        "function __filter(a,f){var r=[];for(var i=0;i<a.length;i++)if(f(a[i],i,a))r.push(a[i]);return r;}" +
        "function __find(a,f){for(var i=0;i<a.length;i++)if(f(a[i],i,a))return a[i];}" +
        "function __findIndex(a,f){for(var i=0;i<a.length;i++)if(f(a[i],i,a))return i;return -1;}" +
        "function __some(a,f){for(var i=0;i<a.length;i++)if(f(a[i],i,a))return true;return false;}" +
        "function __every(a,f){for(var i=0;i<a.length;i++)if(!f(a[i],i,a))return false;return true;}" +
        "function __reduce(a,f,s){var i=0;if(s===undefined){s=a[0];i=1;}for(;i<a.length;i++)s=f(s,a[i],i,a);return s;}" +
        "function __sort(a,f){for(var i=1;i<a.length;i++){var v=a[i];var j=i-1;while(j>=0){var c=f?f(a[j],v):(String(a[j])>String(v)?1:-1);if(c<=0)break;a[j+1]=a[j];j--;}a[j+1]=v;}return a;}";

    private const int MaxStack = 512;
    private const int MaxFrames = 64;
    private DataToken[] st = null;
    private int sp = 0;
    private int[] frPc = null;
    private DataDictionary[] frEnv = null;
    private int frCount = 0;
    private int pc = 0;
    private DataDictionary curEnv = null;
    private DataDictionary globalEnv = null;
    private DataDictionary builtinEnv = null;
    private DataToken stash;
    private DataToken tUndef;
    private DataToken tNull;
    private bool running = false;
    private int steps = 0;

    private bool inited = false;
    private bool active = false;
    private bool pumpQueued = false;

    // page
    private string[] scripts = null;
    private int scriptCount = 0;
    private int scriptNext = 0;
    private int writeIndex = -1;
    private int loadStage = 0;
    private DataList pendingCalls = null;
    private string pageUrl = "";

    // timers
    private const int MaxTimers = 16;
    private bool[] tmUsed = null;
    private int[] tmId = null;
    private DataToken[] tmFn = null;
    private float[] tmAt = null;
    private float[] tmEvery = null;
    private int tmNextId = 1;
    private int tmScan = 0;

    // document
    private const int MaxDocChars = 200000;
    private const string TagEnd = " >/\n\t\r";
    private const string AttrLead = " \n\t\r\"'";
    private const string VoidTags = " br hr img input meta link area base col embed source track wbr ";
    private string docHtml = "";
    private string docLower = "";
    private bool docDirty = false;
    private float lastRender = 0f;
    private int selKind = 0; // 0 tag, 1 id, 2 class
    private string selKey = "";
    private string selTag = "";
    private int matchResume = 0;
    private int locOpen = 0;
    private int locOpenEnd = 0;
    private int locCloseStart = 0;
    private int locCloseEnd = 0;
    private string locTag = "";
    private int atStart = 0;
    private int atValStart = 0;
    private int atValEnd = 0;
    private int atEnd = 0;

    // number parser output
    private bool numOk = false;
    private int numEnd = 0;

    // native call arguments
    private int hBase = 0;
    private int hArgc = 0;

    // =====================================================================
    // Public interface (called by VRCbrowser)
    // =====================================================================

    /// <summary>Takes the scripts out of a freshly loaded page and queues them. Returns the HTML to render.</summary>
    public string _Prepare(string html, string url)
    {
        _Stop();
        Init();
        pageUrl = url;

        string lower = html.ToLower();
        string result = "";
        int from = 0;
        scriptCount = 0;
        for (int guard = 0; guard < 64; guard++)
        {
            int s = lower.IndexOf("<script", from);
            if (s < 0) break;
            int openEnd = lower.IndexOf('>', s);
            if (openEnd < 0) break;
            result = result + html.Substring(from, s - from);
            int e = lower.IndexOf("</script", openEnd);
            if (e < 0)
            {
                from = html.Length;
                break;
            }
            int closeEnd = lower.IndexOf('>', e);
            if (closeEnd < 0) closeEnd = html.Length - 1;

            string attrs = lower.Substring(s, openEnd - s);
            bool runnable = !attrs.Contains("src=") && (!attrs.Contains("type=") || attrs.Contains("javascript"));
            if (runnable && scriptCount < scripts.Length)
            {
                string code = html.Substring(openEnd + 1, e - openEnd - 1);
                if (code.Trim().Length > 0)
                {
                    result = result + "<!--js:" + scriptCount + "-->";
                    scripts[scriptCount] = code;
                    scriptCount++;
                }
            }
            from = closeEnd + 1;
        }
        result = result + html.Substring(from);

        docHtml = result;
        docLower = result.ToLower();
        docDirty = false;
        lastRender = Time.time;
        scriptNext = 0;
        loadStage = 0;
        writeIndex = -1;
        cLen = preludeLen;
        fCount = preludeFunctions;
        globalEnv = new DataDictionary();
        globalEnv.SetValue(ParentKey, new DataToken(builtinEnv));
        active = true;
        QueuePump();
        return result;
    }

    /// <summary>Stops every script and timer of the current page.</summary>
    public void _Stop()
    {
        active = false;
        running = false;
        if (!inited) return;
        scriptCount = 0;
        scriptNext = 0;
        sp = 0;
        frCount = 0;
        pendingCalls.Clear();
        for (int i = 0; i < MaxTimers; i++) tmUsed[i] = false;
    }

    public void _Pump()
    {
        pumpQueued = false;
        if (!active) return;

        if (!running) StartNextWork();
        if (running) RunSlice();

        if (!running && docDirty && Time.time - lastRender >= minRenderInterval)
        {
            docDirty = false;
            lastRender = Time.time;
            browser._JsRender(docHtml);
        }

        bool more = running || docDirty || scriptNext < scriptCount || loadStage < 2 || pendingCalls.Count > 0;
        if (!more)
        {
            for (int i = 0; i < MaxTimers; i++)
            {
                if (!tmUsed[i]) continue;
                more = true;
                break;
            }
        }
        if (more) QueuePump();
    }

    private void QueuePump()
    {
        if (pumpQueued) return;
        pumpQueued = true;
        SendCustomEventDelayedFrames(nameof(_Pump), 1);
    }

    private void Init()
    {
        if (inited) return;
        inited = true;

        cOp = new int[4096];
        cA = new int[4096];
        cS = new string[4096];
        cN = new double[4096];
        cLine = new int[4096];
        fEntry = new int[MaxFunctions];
        fParams = new string[MaxFunctions][];
        fArrow = new bool[MaxFunctions];
        brk = new int[128];
        cnt = new int[128];
        tplStack = new int[16];
        st = new DataToken[MaxStack];
        frPc = new int[MaxFrames];
        frEnv = new DataDictionary[MaxFrames];
        scripts = new string[16];
        pendingCalls = new DataList();
        tmUsed = new bool[MaxTimers];
        tmId = new int[MaxTimers];
        tmFn = new DataToken[MaxTimers];
        tmAt = new float[MaxTimers];
        tmEvery = new float[MaxTimers];

        tUndef = new DataToken();
        tNull = new DataToken(DataError.None);
        stash = tUndef;

        // instruction 0 is the address every host-invoked call returns to
        cLen = 0;
        tLine = new int[2];
        tp = 0;
        Emit(OP_HALT, 0);

        builtinEnv = new DataDictionary();
        globalEnv = builtinEnv;
        int entry = CompileProgram(Prelude);
        if (entry >= 0)
        {
            StartProgram(entry);
            steps = -1000000;
            RunLoop(100f);
        }
        else Debug.LogError("[VRCbrowser JS] prelude: " + cErr);
        running = false;
        preludeLen = cLen;
        preludeFunctions = fCount;
        globalEnv = new DataDictionary();
        globalEnv.SetValue(ParentKey, new DataToken(builtinEnv));
    }

    // =====================================================================
    // Scheduler
    // =====================================================================

    private void StartNextWork()
    {
        if (scriptNext < scriptCount)
        {
            int index = scriptNext;
            scriptNext++;
            writeIndex = index;
            int entry = CompileProgram(scripts[index]);
            scripts[index] = null;
            if (entry >= 0) StartProgram(entry);
            else Report("構文エラー: " + cErr);
            return;
        }
        writeIndex = -1;

        if (loadStage == 0)
        {
            loadStage = 1;
            // <body onload="...">
            if (Locate("body", 0) && FindAttr("onload"))
            {
                string code = docHtml.Substring(atValStart, atValEnd - atValStart).Replace("&quot;", "\"");
                int entry = CompileProgram(code);
                if (entry >= 0)
                {
                    StartProgram(entry);
                    return;
                }
                Report("構文エラー: " + cErr);
            }
        }
        if (loadStage == 1)
        {
            loadStage = 2;
            DataToken onload;
            if (globalEnv.TryGetValue("onload", out onload) && IsClosure(onload)) pendingCalls.Add(onload);
        }

        if (pendingCalls.Count > 0)
        {
            DataToken fn = pendingCalls[0];
            pendingCalls.RemoveAt(0);
            StartCall(fn);
            return;
        }

        float now = Time.time;
        for (int n = 0; n < MaxTimers; n++)
        {
            int i = (tmScan + n) % MaxTimers;
            if (!tmUsed[i] || tmAt[i] > now) continue;
            tmScan = i + 1;
            DataToken fn = tmFn[i];
            if (tmEvery[i] > 0f) tmAt[i] = now + tmEvery[i];
            else tmUsed[i] = false;
            StartCall(fn);
            return;
        }
    }

    private void StartProgram(int entry)
    {
        sp = 0;
        frCount = 0;
        curEnv = globalEnv;
        pc = entry;
        steps = 0;
        running = true;
    }

    private void StartCall(DataToken fn)
    {
        if (!IsClosure(fn)) return;
        sp = 0;
        frCount = 0;
        curEnv = globalEnv;
        pc = -1; // EnterClosure stores pc + 1 = 0 (HALT) as the return address
        steps = 0;
        running = true;
        st[0] = fn;
        sp = 1;
        EnterClosure((object[])fn.Reference, 0, 0, false, tUndef);
    }

    private void RunSlice()
    {
        RunLoop(frameBudgetMs);
    }

    private void Fail(string message)
    {
        if (!running) return;
        running = false;
        int line = pc >= 0 && pc < cLen ? cLine[pc] : 0;
        Report("エラー: " + message + " (行 " + line + ")");
    }

    private void Report(string message)
    {
        Debug.LogWarning("[VRCbrowser JS] " + message);
        if (showErrors && browser != null) browser._JsStatus("JS " + message);
    }

    // =====================================================================
    // Tokenizer
    // =====================================================================

    private void AddTok(int type, string s, double n, int line)
    {
        tType[tCount] = type;
        tStr[tCount] = s;
        tNum[tCount] = n;
        tLine[tCount] = line;
        tCount++;
    }

    private bool Tokenize(string src)
    {
        int n = src.Length;
        int cap = n * 2 + 40;
        tType = new int[cap];
        tStr = new string[cap];
        tNum = new double[cap];
        tLine = new int[cap];
        tHoisted = new bool[cap];
        tCount = 0;
        tplCount = 0;
        int braceDepth = 0;
        int line = 1;
        int p = 0;

        while (p < n)
        {
            char c = src[p];
            if (c == ' ' || c == '\t' || c == '\r')
            {
                p++;
                continue;
            }
            if (c == '\n')
            {
                line++;
                p++;
                continue;
            }
            if (c == '/' && p + 1 < n)
            {
                char d = src[p + 1];
                if (d == '/')
                {
                    int e = src.IndexOf('\n', p);
                    p = e < 0 ? n : e;
                    continue;
                }
                if (d == '*')
                {
                    int e = src.IndexOf("*/", p + 2);
                    if (e < 0) e = n - 2;
                    line += CountLines(src, p, e);
                    p = e + 2;
                    continue;
                }
            }
            if (IdentStart.IndexOf(c) >= 0)
            {
                int s = p;
                p++;
                while (p < n && IdentPart.IndexOf(src[p]) >= 0) p++;
                AddTok(T_ID, src.Substring(s, p - s), 0, line);
                continue;
            }
            if (Digits.IndexOf(c) >= 0 || (c == '.' && p + 1 < n && Digits.IndexOf(src[p + 1]) >= 0))
            {
                if (c == '0' && p + 1 < n && (src[p + 1] == 'x' || src[p + 1] == 'X'))
                {
                    p += 2;
                    double hex = 0;
                    while (p < n)
                    {
                        int digit = HexLower.IndexOf(src.Substring(p, 1).ToLower());
                        if (digit < 0) break;
                        hex = hex * 16 + digit;
                        p++;
                    }
                    AddTok(T_NUM, "", hex, line);
                    continue;
                }
                double value = ParseDouble(src.Substring(p, Mathf.Min(40, n - p)));
                p += numEnd;
                AddTok(T_NUM, "", value, line);
                continue;
            }
            if (c == '"' || c == '\'')
            {
                // quoted string
                int q = p + 1;
                string acc = "";
                bool closed = false;
                for (int guard = 0; guard < 4000; guard++)
                {
                    int e = src.IndexOf(c, q);
                    if (e < 0) break;
                    int b = src.IndexOf('\\', q);
                    if (b < 0 || b > e)
                    {
                        acc = acc + src.Substring(q, e - q);
                        p = e + 1;
                        closed = true;
                        break;
                    }
                    acc = acc + src.Substring(q, b - q);
                    if (b + 1 >= n) break;
                    if (src[b + 1] == 'u' && b + 6 <= n)
                    {
                        acc = acc + HexChar(src.Substring(b + 2, 4));
                        q = b + 6;
                    }
                    else
                    {
                        acc = acc + Unescape(src[b + 1]);
                        q = b + 2;
                    }
                }
                if (!closed)
                {
                    cErr = "文字列が閉じられていません (行 " + line + ")";
                    return false;
                }
                AddTok(T_STR, acc, 0, line);
                continue;
            }
            if (c == '`')
            {
                // template literal -> ( "chunk" + ( expr ) + "chunk" )
                AddTok(T_P, "(", 0, line);
                int startLine = line;
                p = ScanTemplateChunk(src, p + 1, line, braceDepth);
                if (p < 0)
                {
                    cErr = "テンプレート文字列が閉じられていません (行 " + startLine + ")";
                    return false;
                }
                line += CountLines(src, 0, p) + 1 - line;
                continue;
            }
            if (c == '{') braceDepth++;
            else if (c == '}')
            {
                if (tplCount > 0 && tplStack[tplCount - 1] == braceDepth)
                {
                    tplCount--;
                    AddTok(T_P, ")", 0, line);
                    AddTok(T_P, "+", 0, line);
                    int startLine = line;
                    p = ScanTemplateChunk(src, p + 1, line, braceDepth);
                    if (p < 0)
                    {
                        cErr = "テンプレート文字列が閉じられていません (行 " + startLine + ")";
                        return false;
                    }
                    line += CountLines(src, 0, p) + 1 - line;
                    continue;
                }
                braceDepth--;
            }

            if (p + 3 <= n)
            {
                string three = src.Substring(p, 3);
                if (Punct3.Contains(" " + three + " "))
                {
                    AddTok(T_P, three, 0, line);
                    p += 3;
                    continue;
                }
            }
            if (p + 2 <= n)
            {
                string two = src.Substring(p, 2);
                if (Punct2.Contains(" " + two + " "))
                {
                    AddTok(T_P, two, 0, line);
                    p += 2;
                    continue;
                }
            }
            AddTok(T_P, src.Substring(p, 1), 0, line);
            p++;
        }
        AddTok(T_EOF, "", 0, line);
        AddTok(T_EOF, "", 0, line);
        AddTok(T_EOF, "", 0, line);
        return true;
    }

    /// <summary>Reads template text up to the next ${ or the closing backtick. Returns the position after it, or -1.</summary>
    private int ScanTemplateChunk(string src, int p, int line, int braceDepth)
    {
        int n = src.Length;
        string acc = "";
        int q = p;
        for (int guard = 0; guard < 4000; guard++)
        {
            int tick = src.IndexOf('`', q);
            if (tick < 0) return -1;
            int sub = src.IndexOf("${", q);
            int esc = src.IndexOf('\\', q);
            int stop = tick;
            if (sub >= 0 && sub < stop) stop = sub;
            if (esc >= 0 && esc < stop)
            {
                acc = acc + src.Substring(q, esc - q);
                if (esc + 1 >= n) return -1;
                acc = acc + Unescape(src[esc + 1]);
                q = esc + 2;
                continue;
            }
            acc = acc + src.Substring(q, stop - q);
            AddTok(T_STR, acc, 0, line);
            if (stop == tick)
            {
                AddTok(T_P, ")", 0, line);
                return tick + 1;
            }
            AddTok(T_P, "+", 0, line);
            AddTok(T_P, "(", 0, line);
            if (tplCount >= tplStack.Length) return -1;
            tplStack[tplCount] = braceDepth;
            tplCount++;
            return sub + 2;
        }
        return -1;
    }

    private int CountLines(string src, int from, int to)
    {
        int count = 0;
        int p = from;
        for (int guard = 0; guard < 100000; guard++)
        {
            int e = src.IndexOf('\n', p);
            if (e < 0 || e >= to) break;
            count++;
            p = e + 1;
        }
        return count;
    }

    private string Unescape(char c)
    {
        if (c == 'n') return "\n";
        if (c == 't') return "\t";
        if (c == 'r') return "";
        if (c == '\n') return "";
        if (c == '0') return "";
        return c.ToString();
    }

    private string HexChar(string hex)
    {
        int code = 0;
        string lower = hex.ToLower();
        for (int i = 0; i < lower.Length; i++)
        {
            int digit = HexLower.IndexOf(lower[i]);
            if (digit < 0) return "?";
            code = code * 16 + digit;
        }
        return Convert.ToChar(code).ToString();
    }

    // =====================================================================
    // Compiler (recursive descent, emits bytecode directly)
    // =====================================================================

    private int Emit(int op, int a)
    {
        if (cLen >= cOp.Length) GrowCode();
        cOp[cLen] = op;
        cA[cLen] = a;
        cS[cLen] = null;
        cN[cLen] = 0;
        cLine[cLen] = tLine[tp];
        cLen++;
        return cLen - 1;
    }

    private int EmitS(int op, string s, int a)
    {
        int at = Emit(op, a);
        cS[at] = s;
        return at;
    }

    private void EmitN(double n)
    {
        int at = Emit(OP_PUSHN, 0);
        cN[at] = n;
    }

    private void GrowCode()
    {
        int size = cOp.Length * 2;
        if (size > MaxCode)
        {
            if (cErr == null) cErr = "スクリプトが大きすぎます";
            tp = tCount - 3;
            cLen = cOp.Length - 1; // keep writing into the last slot; the result is discarded
            return;
        }
        int[] op = new int[size];
        int[] a = new int[size];
        string[] s = new string[size];
        double[] n = new double[size];
        int[] line = new int[size];
        Array.Copy(cOp, op, cLen);
        Array.Copy(cA, a, cLen);
        Array.Copy(cS, s, cLen);
        Array.Copy(cN, n, cLen);
        Array.Copy(cLine, line, cLen);
        cOp = op;
        cA = a;
        cS = s;
        cN = n;
        cLine = line;
    }

    private void Error(string message)
    {
        if (cErr == null) cErr = message + " (行 " + tLine[tp] + ")";
        tp = tCount - 3; // first EOF token: every parse loop stops there
    }

    private bool IsP(string s)
    {
        return tType[tp] == T_P && tStr[tp] == s;
    }

    private bool IsK(string s)
    {
        return tType[tp] == T_ID && tStr[tp] == s;
    }

    private void Expect(string s)
    {
        if (tType[tp] == T_P && tStr[tp] == s) tp++;
        else Error("'" + s + "' が必要です");
    }

    private void EatSemi()
    {
        if (tType[tp] == T_P && tStr[tp] == ";") tp++;
    }

    /// <summary>Index of the token closing the bracket at index open, or -1.</summary>
    private int MatchBracket(int open)
    {
        int depthAll = 0;
        for (int i = open; tType[i] != T_EOF; i++)
        {
            if (tType[i] != T_P) continue;
            string s = tStr[i];
            if (s == "(" || s == "[" || s == "{") depthAll++;
            else if (s == ")" || s == "]" || s == "}")
            {
                depthAll--;
                if (depthAll == 0) return i;
            }
        }
        return -1;
    }

    private int CompileProgram(string src)
    {
        cErr = null;
        if (src.Length > maxScriptChars)
        {
            cErr = "スクリプトが長すぎます (" + src.Length + " 文字、上限 " + maxScriptChars + ")";
            return -1;
        }
        if (!Tokenize(src)) return -1;

        int saveLen = cLen;
        int saveFunctions = fCount;
        tp = 0;
        brkCount = 0;
        cntCount = 0;
        int entry = cLen;
        Hoist();
        while (tType[tp] != T_EOF) ParseStatement();
        Emit(OP_HALT, 0);
        if (cErr != null)
        {
            cLen = saveLen;
            fCount = saveFunctions;
            return -1;
        }
        return entry;
    }

    /// <summary>Compiles the function declarations of the body starting at tp first, so they can be called before they appear.</summary>
    [RecursiveMethod]
    private void Hoist()
    {
        int start = tp;
        int braces = 0;
        int i = tp;
        while (tType[i] != T_EOF)
        {
            if (tType[i] == T_P)
            {
                string s = tStr[i];
                if (s == "{") braces++;
                else if (s == "}")
                {
                    if (braces == 0) break;
                    braces--;
                }
            }
            else if (braces == 0 && tType[i] == T_ID && tStr[i] == "function" && tType[i + 1] == T_ID && !tHoisted[i])
            {
                bool statement = i == start;
                if (!statement && tType[i - 1] == T_P)
                {
                    string prev = tStr[i - 1];
                    statement = prev == ";" || prev == "}" || prev == "{";
                }
                if (statement)
                {
                    int save = tp;
                    tp = i;
                    string name = tStr[i + 1];
                    int fn = ParseFunction();
                    Emit(OP_CLOSURE, fn);
                    EmitS(OP_DECL, name, 0);
                    tHoisted[i] = true;
                    i = tp;
                    tp = save;
                    if (cErr != null) return;
                    continue;
                }
            }
            i++;
        }
    }

    /// <summary>At "function": parses name, parameters and body. Returns the function index.</summary>
    [RecursiveMethod]
    private int ParseFunction()
    {
        tp++;
        if (tType[tp] == T_ID) tp++;
        string[] names = new string[16];
        int count = 0;
        Expect("(");
        while (tType[tp] == T_ID && count < 16)
        {
            names[count] = tStr[tp];
            count++;
            tp++;
            if (IsP(",")) tp++;
        }
        Expect(")");
        if (!IsP("{"))
        {
            Error("関数の本体 '{' が必要です");
            return 0;
        }
        tp++;
        return ParseFunctionBody(names, count, false, false);
    }

    /// <summary>Compiles a function body. Block bodies start after "{"; expression bodies (arrows) are a single expression.</summary>
    [RecursiveMethod]
    private int ParseFunctionBody(string[] names, int count, bool arrow, bool expression)
    {
        if (fCount >= MaxFunctions)
        {
            Error("関数が多すぎます");
            return 0;
        }
        int jump = Emit(OP_JMP, 0);
        int fn = fCount;
        fCount++;
        string[] ps = new string[count];
        for (int i = 0; i < count; i++) ps[i] = names[i];
        fParams[fn] = ps;
        fArrow[fn] = arrow;
        fEntry[fn] = cLen;

        if (expression)
        {
            ParseAssign();
            Emit(OP_RET, 0);
        }
        else
        {
            Hoist();
            while (tType[tp] != T_EOF && !IsP("}")) ParseStatement();
            Expect("}");
            Emit(OP_UNDEF, 0);
            Emit(OP_RET, 0);
        }
        cA[jump] = cLen;
        return fn;
    }

    private void SkipFunction()
    {
        tp++; // function
        if (tType[tp] == T_ID) tp++;
        int close = MatchBracket(tp);
        if (close < 0)
        {
            Error("関数が閉じられていません");
            return;
        }
        tp = close + 1;
        close = MatchBracket(tp);
        if (close < 0)
        {
            Error("関数が閉じられていません");
            return;
        }
        tp = close + 1;
    }

    [RecursiveMethod]
    private void ParseVar()
    {
        tp++;
        while (tType[tp] != T_EOF)
        {
            if (tType[tp] != T_ID)
            {
                Error("変数名が必要です (分割代入は未対応)");
                return;
            }
            string name = tStr[tp];
            tp++;
            if (IsP("="))
            {
                tp++;
                ParseAssign();
                EmitS(OP_DECL, name, 0);
            }
            else EmitS(OP_DECLU, name, 0);
            if (!IsP(",")) break;
            tp++;
        }
    }

    [RecursiveMethod]
    private void ParseStatement()
    {
        int type = tType[tp];
        if (type == T_EOF) return;
        string s = tStr[tp];

        if (type == T_P)
        {
            if (s == "{")
            {
                tp++;
                while (tType[tp] != T_EOF && !IsP("}")) ParseStatement();
                Expect("}");
                return;
            }
            if (s == ";")
            {
                tp++;
                return;
            }
        }
        else if (type == T_ID)
        {
            if (s == "var" || s == "let" || s == "const")
            {
                ParseVar();
                EatSemi();
                return;
            }
            if (s == "function" && tType[tp + 1] == T_ID)
            {
                if (tHoisted[tp])
                {
                    SkipFunction();
                    return;
                }
                string name = tStr[tp + 1];
                int fn = ParseFunction();
                Emit(OP_CLOSURE, fn);
                EmitS(OP_DECL, name, 0);
                return;
            }
            if (s == "if")
            {
                tp++;
                Expect("(");
                ParseExpr();
                Expect(")");
                int jElse = Emit(OP_JF, 0);
                ParseStatement();
                if (IsK("else"))
                {
                    tp++;
                    int jEnd = Emit(OP_JMP, 0);
                    cA[jElse] = cLen;
                    ParseStatement();
                    cA[jEnd] = cLen;
                }
                else cA[jElse] = cLen;
                return;
            }
            if (s == "while")
            {
                tp++;
                int bBase = brkCount;
                int cBase = cntCount;
                int start = cLen;
                Expect("(");
                ParseExpr();
                Expect(")");
                int jEnd = Emit(OP_JF, 0);
                ParseStatement();
                Emit(OP_JMP, start);
                cA[jEnd] = cLen;
                PatchLoop(bBase, cBase, cLen, start);
                return;
            }
            if (s == "do")
            {
                tp++;
                int bBase = brkCount;
                int cBase = cntCount;
                int start = cLen;
                ParseStatement();
                int cond = cLen;
                if (IsK("while")) tp++;
                else Error("'while' が必要です");
                Expect("(");
                ParseExpr();
                Expect(")");
                EatSemi();
                int jEnd = Emit(OP_JF, 0);
                Emit(OP_JMP, start);
                cA[jEnd] = cLen;
                PatchLoop(bBase, cBase, cLen, cond);
                return;
            }
            if (s == "for")
            {
                ParseFor();
                return;
            }
            if (s == "switch")
            {
                ParseSwitch();
                return;
            }
            if (s == "return")
            {
                int line = tLine[tp];
                tp++;
                if (IsP(";") || IsP("}") || tType[tp] == T_EOF || tLine[tp] != line) Emit(OP_UNDEF, 0);
                else ParseExpr();
                Emit(OP_RET, 0);
                EatSemi();
                return;
            }
            if (s == "break" || s == "continue")
            {
                tp++;
                int j = Emit(OP_JMP, 0);
                if (s == "break")
                {
                    if (brkCount < brk.Length)
                    {
                        brk[brkCount] = j;
                        brkCount++;
                    }
                }
                else if (cntCount < cnt.Length)
                {
                    cnt[cntCount] = j;
                    cntCount++;
                }
                EatSemi();
                return;
            }
            if (s == "throw")
            {
                tp++;
                ParseExpr();
                Emit(OP_THROW, 0);
                EatSemi();
                return;
            }
            if (s == "try")
            {
                // errors cannot be caught: the try block runs, the catch block is skipped, finally runs
                tp++;
                ParseStatement();
                if (IsK("catch"))
                {
                    tp++;
                    if (IsP("("))
                    {
                        int close = MatchBracket(tp);
                        tp = close < 0 ? tCount - 3 : close + 1;
                    }
                    int end = MatchBracket(tp);
                    tp = end < 0 ? tCount - 3 : end + 1;
                }
                if (IsK("finally"))
                {
                    tp++;
                    ParseStatement();
                }
                return;
            }
            if (s == "class" || s == "import" || s == "export" || s == "async" || s == "await" || s == "yield")
            {
                Error("'" + s + "' は未対応です");
                return;
            }
        }

        ParseExpr();
        Emit(OP_POP, 0);
        EatSemi();
    }

    private void PatchLoop(int bBase, int cBase, int breakTarget, int continueTarget)
    {
        for (int i = bBase; i < brkCount; i++) cA[brk[i]] = breakTarget;
        brkCount = bBase;
        for (int i = cBase; i < cntCount; i++) cA[cnt[i]] = continueTarget;
        cntCount = cBase;
    }

    [RecursiveMethod]
    private void ParseFor()
    {
        tp++;
        Expect("(");
        int bBase = brkCount;
        int cBase = cntCount;

        int k = tp;
        bool declares = false;
        if (tType[k] == T_ID && (tStr[k] == "var" || tStr[k] == "let" || tStr[k] == "const"))
        {
            declares = true;
            k++;
        }
        if (tType[k] == T_ID && tType[k + 1] == T_ID && (tStr[k + 1] == "of" || tStr[k + 1] == "in"))
        {
            string name = tStr[k];
            bool keys = tStr[k + 1] == "in";
            tp = k + 2;
            ParseExpr();
            Expect(")");
            hiddenCounter++;
            string list = " it" + hiddenCounter;
            string index = " ix" + hiddenCounter;
            Emit(OP_KEYS, keys ? 1 : 0);
            EmitS(OP_DECL, list, 0);
            EmitN(0);
            EmitS(OP_DECL, index, 0);
            int start = cLen;
            EmitS(OP_LOAD, index, 0);
            EmitS(OP_LOAD, list, 0);
            EmitS(OP_GETM, "length", 0);
            Emit(OP_LT, 0);
            int jEnd = Emit(OP_JF, 0);
            EmitS(OP_LOAD, list, 0);
            EmitS(OP_LOAD, index, 0);
            Emit(OP_GETI, 0);
            if (declares) EmitS(OP_DECL, name, 0);
            else
            {
                EmitS(OP_STORE, name, 0);
                Emit(OP_POP, 0);
            }
            ParseStatement();
            int next = cLen;
            EmitS(OP_LOAD, index, 0);
            EmitN(1);
            Emit(OP_ADD, 0);
            EmitS(OP_STORE, index, 0);
            Emit(OP_POP, 0);
            Emit(OP_JMP, start);
            cA[jEnd] = cLen;
            PatchLoop(bBase, cBase, cLen, next);
            return;
        }

        if (!IsP(";"))
        {
            if (declares) ParseVar();
            else
            {
                ParseExpr();
                Emit(OP_POP, 0);
            }
        }
        Expect(";");
        int condition = cLen;
        int jExit = -1;
        if (!IsP(";"))
        {
            ParseExpr();
            jExit = Emit(OP_JF, 0);
        }
        Expect(";");
        int jBody = Emit(OP_JMP, 0);
        int update = cLen;
        if (!IsP(")"))
        {
            ParseExpr();
            Emit(OP_POP, 0);
        }
        Emit(OP_JMP, condition);
        Expect(")");
        cA[jBody] = cLen;
        ParseStatement();
        Emit(OP_JMP, update);
        if (jExit >= 0) cA[jExit] = cLen;
        PatchLoop(bBase, cBase, cLen, update);
    }

    [RecursiveMethod]
    private void ParseSwitch()
    {
        tp++;
        Expect("(");
        ParseExpr();
        Expect(")");
        hiddenCounter++;
        string subject = " sw" + hiddenCounter;
        EmitS(OP_DECL, subject, 0);
        Expect("{");

        int bBase = brkCount;
        int pendingTest = -1;   // JF of the previous case test, taken when it does not match
        int defaultEntry = -1;
        bool first = true;
        while (tType[tp] != T_EOF && !IsP("}"))
        {
            if (IsK("case"))
            {
                tp++;
                int skipTest = first ? -1 : Emit(OP_JMP, 0); // falling through from the previous body
                if (pendingTest >= 0) cA[pendingTest] = cLen;
                EmitS(OP_LOAD, subject, 0);
                ParseExpr();
                Emit(OP_SEQ, 0);
                pendingTest = Emit(OP_JF, 0);
                if (skipTest >= 0) cA[skipTest] = cLen;
                Expect(":");
                first = false;
            }
            else if (IsK("default"))
            {
                tp++;
                Expect(":");
                if (first)
                {
                    // default before any case: tests run first, then come back here
                    pendingTest = Emit(OP_JMP, 0);
                }
                defaultEntry = cLen;
                first = false;
            }
            else ParseStatement();
        }
        Expect("}");
        int jEnd = Emit(OP_JMP, 0);
        if (pendingTest >= 0) cA[pendingTest] = cLen;
        if (defaultEntry >= 0) Emit(OP_JMP, defaultEntry);
        cA[jEnd] = cLen;
        for (int i = bBase; i < brkCount; i++) cA[brk[i]] = cLen;
        brkCount = bBase;
    }

    [RecursiveMethod]
    private void ParseExpr()
    {
        ParseAssign();
        while (IsP(","))
        {
            tp++;
            Emit(OP_POP, 0);
            ParseAssign();
        }
    }

    private int AssignOp(string s)
    {
        if (s == "=") return 0;
        if (s == "+=") return OP_ADD;
        if (s == "-=") return OP_SUB;
        if (s == "*=") return OP_MUL;
        if (s == "/=") return OP_DIV;
        if (s == "%=") return OP_MOD;
        if (s == "**=") return OP_POW;
        return -1;
    }

    [RecursiveMethod]
    private void ParseAssign()
    {
        // arrow functions: x => ...   (a, b) => ...
        if (tType[tp] == T_ID && tType[tp + 1] == T_P && tStr[tp + 1] == "=>")
        {
            string[] one = new string[1];
            one[0] = tStr[tp];
            tp += 2;
            ParseArrowBody(one, 1);
            return;
        }
        if (tType[tp] == T_P && tStr[tp] == "(")
        {
            int close = MatchBracket(tp);
            if (close > 0 && tType[close + 1] == T_P && tStr[close + 1] == "=>")
            {
                string[] names = new string[16];
                int count = 0;
                tp++;
                while (tType[tp] == T_ID && count < 16)
                {
                    names[count] = tStr[tp];
                    count++;
                    tp++;
                    if (IsP(",")) tp++;
                }
                if (tp != close)
                {
                    Error("引数の書き方が未対応です");
                    return;
                }
                tp = close + 2;
                ParseArrowBody(names, count);
                return;
            }
        }

        ParseBinary(1);
        if (tType[tp] != T_P) return;

        if (tStr[tp] == "?")
        {
            tp++;
            int jElse = Emit(OP_JF, 0);
            ParseAssign();
            int jEnd = Emit(OP_JMP, 0);
            cA[jElse] = cLen;
            Expect(":");
            ParseAssign();
            cA[jEnd] = cLen;
            return;
        }

        int bin = AssignOp(tStr[tp]);
        if (bin < 0) return;
        int last = cLen - 1;
        int lop = cOp[last];
        string name = cS[last];
        tp++;
        if (lop == OP_LOAD)
        {
            if (bin == 0) cLen--;
            ParseAssign();
            if (bin > 0) Emit(bin, 0);
            EmitS(OP_STORE, name, 0);
        }
        else if (lop == OP_GETM)
        {
            cLen--;
            if (bin > 0)
            {
                Emit(OP_DUP, 0);
                EmitS(OP_GETM, name, 0);
            }
            ParseAssign();
            if (bin > 0) Emit(bin, 0);
            EmitS(OP_SETM, name, 0);
        }
        else if (lop == OP_GETI)
        {
            cLen--;
            if (bin > 0)
            {
                Emit(OP_DUP2, 0);
                Emit(OP_GETI, 0);
            }
            ParseAssign();
            if (bin > 0) Emit(bin, 0);
            Emit(OP_SETI, 0);
        }
        else Error("代入できない式です");
    }

    [RecursiveMethod]
    private void ParseArrowBody(string[] names, int count)
    {
        int fn;
        if (IsP("{"))
        {
            tp++;
            fn = ParseFunctionBody(names, count, true, false);
        }
        else fn = ParseFunctionBody(names, count, true, true);
        Emit(OP_CLOSURE, fn);
    }

    private int BinPrec(string s)
    {
        if (s == "??") return 1;
        if (s == "||") return 2;
        if (s == "&&") return 3;
        if (s == "==" || s == "!=" || s == "===" || s == "!==") return 7;
        if (s == "<" || s == ">" || s == "<=" || s == ">=") return 8;
        if (s == "+" || s == "-") return 10;
        if (s == "*" || s == "/" || s == "%") return 11;
        if (s == "**") return 12;
        return 0;
    }

    private int BinOp(string s)
    {
        if (s == "+") return OP_ADD;
        if (s == "-") return OP_SUB;
        if (s == "*") return OP_MUL;
        if (s == "/") return OP_DIV;
        if (s == "%") return OP_MOD;
        if (s == "**") return OP_POW;
        if (s == "==") return OP_EQ;
        if (s == "!=") return OP_NE;
        if (s == "===") return OP_SEQ;
        if (s == "!==") return OP_SNE;
        if (s == "<") return OP_LT;
        if (s == ">") return OP_GT;
        if (s == "<=") return OP_LE;
        return OP_GE;
    }

    [RecursiveMethod]
    private void ParseBinary(int minPrec)
    {
        ParseUnary();
        while (tType[tp] == T_P)
        {
            string s = tStr[tp];
            int prec = BinPrec(s);
            if (prec == 0 || prec < minPrec) break;
            tp++;
            if (prec <= 3)
            {
                int j = Emit(prec == 3 ? OP_JFK : (prec == 2 ? OP_JTK : OP_JNK), 0);
                ParseBinary(prec + 1);
                cA[j] = cLen;
            }
            else
            {
                ParseBinary(prec == 12 ? prec : prec + 1);
                Emit(BinOp(s), 0);
            }
        }
    }

    /// <summary>Turns the read that was just emitted (LOAD / GETM / GETI) into "add delta and write back".</summary>
    private void EmitIncrement(double delta, bool postfix)
    {
        int last = cLen - 1;
        int lop = cOp[last];
        string name = cS[last];
        if (lop == OP_LOAD)
        {
            Emit(OP_POS, 0);
            if (postfix) Emit(OP_STASH, 0);
            EmitN(delta);
            Emit(OP_ADD, 0);
            EmitS(OP_STORE, name, 0);
        }
        else if (lop == OP_GETM)
        {
            cLen--;
            Emit(OP_DUP, 0);
            EmitS(OP_GETM, name, 0);
            Emit(OP_POS, 0);
            if (postfix) Emit(OP_STASH, 0);
            EmitN(delta);
            Emit(OP_ADD, 0);
            EmitS(OP_SETM, name, 0);
        }
        else if (lop == OP_GETI)
        {
            cLen--;
            Emit(OP_DUP2, 0);
            Emit(OP_GETI, 0);
            Emit(OP_POS, 0);
            if (postfix) Emit(OP_STASH, 0);
            EmitN(delta);
            Emit(OP_ADD, 0);
            Emit(OP_SETI, 0);
        }
        else
        {
            Error("++ / -- を適用できない式です");
            return;
        }
        if (postfix)
        {
            Emit(OP_POP, 0);
            Emit(OP_UNSTASH, 0);
        }
    }

    [RecursiveMethod]
    private void ParseUnary()
    {
        int type = tType[tp];
        string s = tStr[tp];
        if (type == T_P)
        {
            if (s == "!")
            {
                tp++;
                ParseUnary();
                Emit(OP_NOT, 0);
                return;
            }
            if (s == "-")
            {
                tp++;
                ParseUnary();
                Emit(OP_NEG, 0);
                return;
            }
            if (s == "+")
            {
                tp++;
                ParseUnary();
                Emit(OP_POS, 0);
                return;
            }
            if (s == "++" || s == "--")
            {
                tp++;
                ParseUnary();
                EmitIncrement(s == "++" ? 1 : -1, false);
                return;
            }
        }
        else if (type == T_ID)
        {
            if (s == "typeof")
            {
                tp++;
                ParseUnary();
                if (cOp[cLen - 1] == OP_LOAD) cA[cLen - 1] = 1; // undeclared names are "undefined", not an error
                Emit(OP_TYPEOF, 0);
                return;
            }
            if (s == "delete")
            {
                tp++;
                ParseUnary();
                if (cOp[cLen - 1] == OP_GETM) cOp[cLen - 1] = OP_DELM;
                else Error("delete は obj.name の形だけ対応しています");
                return;
            }
            if (s == "void")
            {
                tp++;
                ParseUnary();
                Emit(OP_POP, 0);
                Emit(OP_UNDEF, 0);
                return;
            }
        }
        ParsePostfix();
    }

    [RecursiveMethod]
    private int ParseArgs()
    {
        int count = 0;
        tp++; // (
        while (tType[tp] != T_EOF && !IsP(")"))
        {
            ParseAssign();
            count++;
            if (IsP(",")) tp++;
            else break;
        }
        Expect(")");
        return count;
    }

    [RecursiveMethod]
    private void ParsePostfix()
    {
        ParsePrimary();
        while (tType[tp] == T_P)
        {
            string s = tStr[tp];
            if (s == "." || s == "?.")
            {
                tp++;
                if (tType[tp] != T_ID)
                {
                    Error("プロパティ名が必要です");
                    return;
                }
                string name = tStr[tp];
                tp++;
                if (IsP("("))
                {
                    int argc = ParseArgs();
                    EmitS(OP_CALLM, name, argc);
                }
                else EmitS(OP_GETM, name, 0);
            }
            else if (s == "[")
            {
                tp++;
                ParseExpr();
                Expect("]");
                Emit(OP_GETI, 0);
            }
            else if (s == "(")
            {
                int argc = ParseArgs();
                Emit(OP_CALL, argc);
            }
            else if (s == "++" || s == "--")
            {
                tp++;
                EmitIncrement(s == "++" ? 1 : -1, true);
                return;
            }
            else return;
        }
    }

    [RecursiveMethod]
    private void ParsePrimary()
    {
        int type = tType[tp];
        string s = tStr[tp];
        if (type == T_NUM)
        {
            EmitN(tNum[tp]);
            tp++;
            return;
        }
        if (type == T_STR)
        {
            EmitS(OP_PUSHS, s, 0);
            tp++;
            return;
        }
        if (type == T_ID)
        {
            if (s == "function")
            {
                int fn = ParseFunction();
                Emit(OP_CLOSURE, fn);
                return;
            }
            if (s == "new")
            {
                tp++;
                if (tType[tp] != T_ID)
                {
                    Error("new の後に名前が必要です");
                    return;
                }
                string name = tStr[tp];
                tp++;
                int argc = 0;
                if (IsP("(")) argc = ParseArgs();
                EmitS(OP_NEW, name, argc);
                return;
            }
            tp++;
            if (s == "true") Emit(OP_TRUE, 0);
            else if (s == "false") Emit(OP_FALSE, 0);
            else if (s == "null") Emit(OP_NULL, 0);
            else if (s == "undefined") Emit(OP_UNDEF, 0);
            else if (s == "NaN") EmitN(double.NaN);
            else if (s == "Infinity") EmitN(double.PositiveInfinity);
            else if (s == "this") EmitS(OP_LOAD, s, 1);
            else EmitS(OP_LOAD, s, 0);
            return;
        }
        if (type == T_P)
        {
            if (s == "(")
            {
                tp++;
                ParseExpr();
                Expect(")");
                return;
            }
            if (s == "[")
            {
                tp++;
                int count = 0;
                while (tType[tp] != T_EOF && !IsP("]"))
                {
                    ParseAssign();
                    count++;
                    if (IsP(",")) tp++;
                    else break;
                }
                Expect("]");
                Emit(OP_ARR, count);
                return;
            }
            if (s == "{")
            {
                tp++;
                int count = 0;
                while (tType[tp] != T_EOF && !IsP("}"))
                {
                    int keyType = tType[tp];
                    string key = keyType == T_NUM ? NumStr(tNum[tp]) : tStr[tp];
                    if (keyType != T_ID && keyType != T_STR && keyType != T_NUM)
                    {
                        Error("オブジェクトのキーが不正です");
                        return;
                    }
                    tp++;
                    EmitS(OP_PUSHS, key, 0);
                    if (IsP(":"))
                    {
                        tp++;
                        ParseAssign();
                    }
                    else if (IsP("("))
                    {
                        // method shorthand: name(a, b) { ... }
                        tp--;
                        int fn = ParseFunction();
                        Emit(OP_CLOSURE, fn);
                    }
                    else EmitS(OP_LOAD, key, 0); // shorthand { key }
                    count++;
                    if (IsP(",")) tp++;
                    else break;
                }
                Expect("}");
                Emit(OP_OBJ, count);
                return;
            }
        }
        if (type == T_EOF) Error("式が途中で終わっています");
        else Error("予期しない '" + s + "' です");
    }

    // =====================================================================
    // Values
    // =====================================================================

    private double ParseDouble(string text)
    {
        numOk = false;
        numEnd = 0;
        int n = text.Length;
        int i = 0;
        bool negative = false;
        if (i < n && (text[i] == '-' || text[i] == '+'))
        {
            negative = text[i] == '-';
            i++;
        }
        if (n - i >= 8 && text.Substring(i, 8) == "Infinity")
        {
            numOk = true;
            numEnd = i + 8;
            return negative ? double.NegativeInfinity : double.PositiveInfinity;
        }
        double value = 0;
        double scale = 1;
        bool fraction = false;
        bool any = false;
        while (i < n)
        {
            char ch = text[i];
            if (ch == '.')
            {
                if (fraction) break;
                fraction = true;
                i++;
                continue;
            }
            int digit = Digits.IndexOf(ch);
            if (digit < 0) break;
            any = true;
            if (fraction)
            {
                scale *= 10;
                value = value * 10 + digit;
            }
            else value = value * 10 + digit;
            i++;
        }
        if (!any) return double.NaN;
        value = value / scale;
        if (i < n && (text[i] == 'e' || text[i] == 'E'))
        {
            int j = i + 1;
            bool expNegative = false;
            if (j < n && (text[j] == '-' || text[j] == '+'))
            {
                expNegative = text[j] == '-';
                j++;
            }
            int exponent = 0;
            bool expAny = false;
            while (j < n)
            {
                int digit = Digits.IndexOf(text[j]);
                if (digit < 0) break;
                exponent = exponent * 10 + digit;
                expAny = true;
                j++;
            }
            if (expAny)
            {
                value = value * Math.Pow(10, expNegative ? -exponent : exponent);
                i = j;
            }
        }
        numOk = true;
        numEnd = i;
        return negative ? -value : value;
    }

    private string NumStr(double d)
    {
        if (double.IsNaN(d)) return "NaN";
        if (double.IsInfinity(d)) return d > 0 ? "Infinity" : "-Infinity";
        if (d == Math.Floor(d) && d > -1e15 && d < 1e15) return Convert.ToInt64(d).ToString();
        return d.ToString().Replace(',', '.');
    }

    private double ToNum(DataToken t)
    {
        TokenType tt = t.TokenType;
        if (tt == TokenType.Double) return t.Double;
        if (tt == TokenType.Boolean) return t.Boolean ? 1 : 0;
        if (tt == TokenType.Error) return 0;
        if (tt == TokenType.String)
        {
            string s = t.String.Trim();
            if (s.Length == 0) return 0;
            double value = ParseDouble(s);
            if (!numOk || numEnd != s.Length) return double.NaN;
            return value;
        }
        return double.NaN;
    }

    private string ToStr(DataToken t)
    {
        TokenType tt = t.TokenType;
        if (tt == TokenType.String) return t.String;
        if (tt == TokenType.Double) return NumStr(t.Double);
        if (tt == TokenType.Boolean) return t.Boolean ? "true" : "false";
        if (tt == TokenType.Null) return "undefined";
        if (tt == TokenType.Error) return "null";
        if (tt == TokenType.DataList) return JoinList(t.DataList, ",");
        if (tt == TokenType.DataDictionary) return "[object Object]";
        if (tt == TokenType.Reference)
        {
            object[] r = (object[])t.Reference;
            int kind = (int)r[0];
            if (kind == K_DATE) return ((DateTime)r[1]).ToString("yyyy/MM/dd HH:mm:ss");
            if (kind == K_ELEM) return "[object HTMLElement]";
            if (kind == K_CLOSURE || kind == K_HOST) return "function";
        }
        return "[object]";
    }

    private string JoinList(DataList list, string separator)
    {
        int n = list.Count;
        if (n > 2000) n = 2000;
        string[] parts = new string[n];
        for (int i = 0; i < n; i++)
        {
            DataToken item = list[i];
            TokenType tt = item.TokenType;
            parts[i] = tt == TokenType.Null || tt == TokenType.Error ? "" : ToStr(item);
        }
        return string.Join(separator, parts);
    }

    private bool Truthy(DataToken t)
    {
        TokenType tt = t.TokenType;
        if (tt == TokenType.Boolean) return t.Boolean;
        if (tt == TokenType.Double)
        {
            double d = t.Double;
            return d != 0 && !double.IsNaN(d);
        }
        if (tt == TokenType.String) return t.String.Length > 0;
        if (tt == TokenType.Null || tt == TokenType.Error) return false;
        return true;
    }

    private bool StrictEq(DataToken a, DataToken b)
    {
        TokenType ta = a.TokenType;
        if (ta != b.TokenType) return false;
        if (ta == TokenType.Double) return a.Double == b.Double;
        if (ta == TokenType.String) return a.String == b.String;
        if (ta == TokenType.Boolean) return a.Boolean == b.Boolean;
        if (ta == TokenType.Null || ta == TokenType.Error) return true;
        if (ta == TokenType.Reference)
        {
            object[] ra = (object[])a.Reference;
            object[] rb = (object[])b.Reference;
            int kind = (int)ra[0];
            if (kind != (int)rb[0]) return false;
            if (kind == K_ELEM) return (string)ra[1] == (string)rb[1] && (int)ra[2] == (int)rb[2];
            if (kind == K_HOST) return (string)ra[1] == (string)rb[1];
            if (kind == K_CLOSURE) return (int)ra[1] == (int)rb[1] && (int)ra[3] == (int)rb[3];
            return false;
        }
        return a.Equals(b);
    }

    private bool LooseEq(DataToken a, DataToken b)
    {
        TokenType ta = a.TokenType;
        TokenType tb = b.TokenType;
        bool nullA = ta == TokenType.Null || ta == TokenType.Error;
        bool nullB = tb == TokenType.Null || tb == TokenType.Error;
        if (nullA || nullB) return nullA && nullB;
        if (ta == tb) return StrictEq(a, b);
        bool primA = ta == TokenType.Double || ta == TokenType.String || ta == TokenType.Boolean;
        bool primB = tb == TokenType.Double || tb == TokenType.String || tb == TokenType.Boolean;
        if (primA && primB) return ToNum(a) == ToNum(b);
        return false;
    }

    private bool IsClosure(DataToken t)
    {
        if (t.TokenType != TokenType.Reference) return false;
        object[] r = (object[])t.Reference;
        return (int)r[0] == K_CLOSURE;
    }

    private DataToken Num(double d)
    {
        return new DataToken(d);
    }

    private DataToken Str(string s)
    {
        return new DataToken(s);
    }

    private DataToken Bool(bool b)
    {
        return new DataToken(b);
    }

    private DataToken MakeRef(int kind, string a, int b)
    {
        object[] r = new object[3];
        r[0] = kind;
        r[1] = a;
        r[2] = b;
        return new DataToken((object)r);
    }

    private int closureSerial = 0;

    // =====================================================================
    // VM
    // =====================================================================

    private void EnterClosure(object[] r, int fnPos, int argc, bool hasThis, DataToken thisValue)
    {
        if (frCount >= MaxFrames)
        {
            Fail("関数の呼び出しが深すぎます");
            return;
        }
        int fn = (int)r[1];
        DataDictionary env = new DataDictionary();
        env.SetValue(ParentKey, new DataToken((DataDictionary)r[2]));
        string[] ps = fParams[fn];
        for (int i = 0; i < ps.Length; i++) env.SetValue(ps[i], i < argc ? st[fnPos + 1 + i] : tUndef);
        if (hasThis && !fArrow[fn]) env.SetValue("this", thisValue);
        frPc[frCount] = pc + 1;
        frEnv[frCount] = curEnv;
        frCount++;
        sp = fnPos;
        curEnv = env;
        pc = fEntry[fn];
    }

    private void RunLoop(float budgetMs)
    {
        float deadline = Time.realtimeSinceStartup + budgetMs * 0.001f;
        int check = 0;
        DataToken a;
        DataToken b;
        DataToken v;
        DataToken pv;
        TokenType ta;
        TokenType tb;
        string name;
        DataDictionary env;
        int op;
        int n;
        bool found;

        while (running)
        {
            check++;
            if (check >= 48)
            {
                check = 0;
                steps += 48;
                if (steps > maxStepsPerRun)
                {
                    Fail("処理が長すぎるため中断しました");
                    return;
                }
                if (Time.realtimeSinceStartup > deadline) return;
                if (sp > MaxStack - 40)
                {
                    Fail("スタックが足りません");
                    return;
                }
            }

            op = cOp[pc];
            switch (op)
            {
                case OP_LOAD:
                    name = cS[pc];
                    env = curEnv;
                    found = false;
                    v = tUndef;
                    while (true)
                    {
                        if (env.TryGetValue(name, out v))
                        {
                            found = true;
                            break;
                        }
                        if (!env.TryGetValue(ParentKey, out pv)) break;
                        env = pv.DataDictionary;
                    }
                    if (!found)
                    {
                        if (HostNames.Contains(" " + name + " ")) v = MakeRef(K_HOST, name, 0);
                        else if (cA[pc] == 1) v = tUndef;
                        else
                        {
                            Fail(name + " は定義されていません");
                            return;
                        }
                    }
                    st[sp] = v;
                    sp++;
                    pc++;
                    break;

                case OP_PUSHN:
                    st[sp] = new DataToken(cN[pc]);
                    sp++;
                    pc++;
                    break;

                case OP_PUSHS:
                    st[sp] = new DataToken(cS[pc]);
                    sp++;
                    pc++;
                    break;

                case OP_STORE:
                    name = cS[pc];
                    env = curEnv;
                    found = false;
                    while (true)
                    {
                        if (env.ContainsKey(name))
                        {
                            found = true;
                            break;
                        }
                        if (!env.TryGetValue(ParentKey, out pv)) break;
                        env = pv.DataDictionary;
                    }
                    if (!found || env == builtinEnv) env = globalEnv;
                    env.SetValue(name, st[sp - 1]);
                    pc++;
                    break;

                case OP_DECL:
                    sp--;
                    curEnv.SetValue(cS[pc], st[sp]);
                    pc++;
                    break;

                case OP_DECLU:
                    if (!curEnv.ContainsKey(cS[pc])) curEnv.SetValue(cS[pc], tUndef);
                    pc++;
                    break;

                case OP_POP:
                    sp--;
                    pc++;
                    break;

                case OP_JMP:
                    pc = cA[pc];
                    break;

                case OP_JF:
                    sp--;
                    if (Truthy(st[sp])) pc++;
                    else pc = cA[pc];
                    break;

                case OP_GETM:
                    a = st[sp - 1];
                    if (a.TokenType == TokenType.DataDictionary)
                    {
                        if (!a.DataDictionary.TryGetValue(cS[pc], out v)) v = tUndef;
                    }
                    else v = GetMember(a, cS[pc]);
                    st[sp - 1] = v;
                    pc++;
                    break;

                case OP_GETI:
                    sp--;
                    b = st[sp];
                    a = st[sp - 1];
                    ta = a.TokenType;
                    if (b.TokenType == TokenType.Double && ta == TokenType.DataList)
                    {
                        n = (int)b.Double;
                        DataList list = a.DataList;
                        v = n >= 0 && n < list.Count ? list[n] : tUndef;
                    }
                    else if (b.TokenType == TokenType.Double && ta == TokenType.String)
                    {
                        n = (int)b.Double;
                        string text = a.String;
                        v = n >= 0 && n < text.Length ? new DataToken(text.Substring(n, 1)) : tUndef;
                    }
                    else v = GetMember(a, ToStr(b));
                    st[sp - 1] = v;
                    pc++;
                    break;

                case OP_SETM:
                    sp--;
                    v = st[sp];
                    SetMember(st[sp - 1], cS[pc], v);
                    st[sp - 1] = v;
                    pc++;
                    break;

                case OP_SETI:
                    sp -= 2;
                    v = st[sp + 1];
                    b = st[sp];
                    a = st[sp - 1];
                    if (b.TokenType == TokenType.Double && a.TokenType == TokenType.DataList)
                    {
                        n = (int)b.Double;
                        DataList list = a.DataList;
                        if (n >= 0 && n < 100000)
                        {
                            while (list.Count <= n) list.Add(tUndef);
                            list.SetValue(n, v);
                        }
                    }
                    else SetMember(a, ToStr(b), v);
                    st[sp - 1] = v;
                    pc++;
                    break;

                case OP_ADD:
                    sp--;
                    b = st[sp];
                    a = st[sp - 1];
                    ta = a.TokenType;
                    tb = b.TokenType;
                    if (ta == TokenType.Double && tb == TokenType.Double) st[sp - 1] = new DataToken(a.Double + b.Double);
                    else if (ta == TokenType.String && tb == TokenType.String) st[sp - 1] = new DataToken(a.String + b.String);
                    else
                    {
                        bool numericA = ta == TokenType.Double || ta == TokenType.Boolean || ta == TokenType.Null || ta == TokenType.Error;
                        bool numericB = tb == TokenType.Double || tb == TokenType.Boolean || tb == TokenType.Null || tb == TokenType.Error;
                        if (numericA && numericB) st[sp - 1] = new DataToken(ToNum(a) + ToNum(b));
                        else st[sp - 1] = new DataToken(ToStr(a) + ToStr(b));
                    }
                    pc++;
                    break;

                case OP_SUB:
                    sp--;
                    st[sp - 1] = new DataToken(ToNum(st[sp - 1]) - ToNum(st[sp]));
                    pc++;
                    break;

                case OP_MUL:
                    sp--;
                    st[sp - 1] = new DataToken(ToNum(st[sp - 1]) * ToNum(st[sp]));
                    pc++;
                    break;

                case OP_DIV:
                    sp--;
                    st[sp - 1] = new DataToken(ToNum(st[sp - 1]) / ToNum(st[sp]));
                    pc++;
                    break;

                case OP_MOD:
                    sp--;
                    st[sp - 1] = new DataToken(ToNum(st[sp - 1]) % ToNum(st[sp]));
                    pc++;
                    break;

                case OP_POW:
                    sp--;
                    st[sp - 1] = new DataToken(Math.Pow(ToNum(st[sp - 1]), ToNum(st[sp])));
                    pc++;
                    break;

                case OP_LT:
                case OP_GT:
                case OP_LE:
                case OP_GE:
                    sp--;
                    b = st[sp];
                    a = st[sp - 1];
                    double cmp;
                    bool comparable = true;
                    if (a.TokenType == TokenType.String && b.TokenType == TokenType.String) cmp = string.CompareOrdinal(a.String, b.String);
                    else
                    {
                        double x = ToNum(a);
                        double y = ToNum(b);
                        comparable = !double.IsNaN(x) && !double.IsNaN(y);
                        cmp = x - y;
                        if (double.IsNaN(cmp)) cmp = x == y ? 0 : (x > y ? 1 : -1); // Infinity - Infinity
                    }
                    bool result = false;
                    if (comparable)
                    {
                        if (op == OP_LT) result = cmp < 0;
                        else if (op == OP_GT) result = cmp > 0;
                        else if (op == OP_LE) result = cmp <= 0;
                        else result = cmp >= 0;
                    }
                    st[sp - 1] = new DataToken(result);
                    pc++;
                    break;

                case OP_SEQ:
                    sp--;
                    st[sp - 1] = new DataToken(StrictEq(st[sp - 1], st[sp]));
                    pc++;
                    break;

                case OP_SNE:
                    sp--;
                    st[sp - 1] = new DataToken(!StrictEq(st[sp - 1], st[sp]));
                    pc++;
                    break;

                case OP_EQ:
                    sp--;
                    st[sp - 1] = new DataToken(LooseEq(st[sp - 1], st[sp]));
                    pc++;
                    break;

                case OP_NE:
                    sp--;
                    st[sp - 1] = new DataToken(!LooseEq(st[sp - 1], st[sp]));
                    pc++;
                    break;

                case OP_NOT:
                    st[sp - 1] = new DataToken(!Truthy(st[sp - 1]));
                    pc++;
                    break;

                case OP_NEG:
                    st[sp - 1] = new DataToken(-ToNum(st[sp - 1]));
                    pc++;
                    break;

                case OP_POS:
                    if (st[sp - 1].TokenType != TokenType.Double) st[sp - 1] = new DataToken(ToNum(st[sp - 1]));
                    pc++;
                    break;

                case OP_TRUE:
                    st[sp] = new DataToken(true);
                    sp++;
                    pc++;
                    break;

                case OP_FALSE:
                    st[sp] = new DataToken(false);
                    sp++;
                    pc++;
                    break;

                case OP_NULL:
                    st[sp] = tNull;
                    sp++;
                    pc++;
                    break;

                case OP_UNDEF:
                    st[sp] = tUndef;
                    sp++;
                    pc++;
                    break;

                case OP_DUP:
                    st[sp] = st[sp - 1];
                    sp++;
                    pc++;
                    break;

                case OP_DUP2:
                    st[sp] = st[sp - 2];
                    st[sp + 1] = st[sp - 1];
                    sp += 2;
                    pc++;
                    break;

                case OP_STASH:
                    stash = st[sp - 1];
                    pc++;
                    break;

                case OP_UNSTASH:
                    st[sp] = stash;
                    sp++;
                    pc++;
                    break;

                case OP_JFK:
                    if (Truthy(st[sp - 1]))
                    {
                        sp--;
                        pc++;
                    }
                    else pc = cA[pc];
                    break;

                case OP_JTK:
                    if (Truthy(st[sp - 1])) pc = cA[pc];
                    else
                    {
                        sp--;
                        pc++;
                    }
                    break;

                case OP_JNK:
                    ta = st[sp - 1].TokenType;
                    if (ta == TokenType.Null || ta == TokenType.Error)
                    {
                        sp--;
                        pc++;
                    }
                    else pc = cA[pc];
                    break;

                case OP_CALL:
                    n = cA[pc];
                    a = st[sp - n - 1];
                    if (a.TokenType != TokenType.Reference)
                    {
                        Fail("関数ではないものを呼び出しました");
                        return;
                    }
                    else
                    {
                        object[] r = (object[])a.Reference;
                        int kind = (int)r[0];
                        if (kind == K_CLOSURE) EnterClosure(r, sp - n - 1, n, false, tUndef);
                        else if (kind == K_HOST)
                        {
                            v = CallHost((string)r[1], sp - n, n);
                            sp = sp - n - 1;
                            st[sp] = v;
                            sp++;
                            pc++;
                        }
                        else
                        {
                            Fail("関数ではないものを呼び出しました");
                            return;
                        }
                    }
                    break;

                case OP_CALLM:
                    CallMethod(cS[pc], cA[pc]);
                    break;

                case OP_RET:
                    sp--;
                    v = st[sp];
                    if (frCount == 0)
                    {
                        running = false;
                        break;
                    }
                    frCount--;
                    pc = frPc[frCount];
                    curEnv = frEnv[frCount];
                    frEnv[frCount] = null;
                    st[sp] = v;
                    sp++;
                    break;

                case OP_CLOSURE:
                    object[] closure = new object[4];
                    closure[0] = K_CLOSURE;
                    closure[1] = cA[pc];
                    closure[2] = curEnv;
                    closureSerial++;
                    closure[3] = closureSerial;
                    st[sp] = new DataToken((object)closure);
                    sp++;
                    pc++;
                    break;

                case OP_ARR:
                    n = cA[pc];
                    DataList made = new DataList();
                    for (int i = sp - n; i < sp; i++) made.Add(st[i]);
                    sp -= n;
                    st[sp] = new DataToken(made);
                    sp++;
                    pc++;
                    break;

                case OP_OBJ:
                    n = cA[pc];
                    DataDictionary dict = new DataDictionary();
                    for (int i = sp - n * 2; i < sp; i += 2) dict.SetValue(st[i], st[i + 1]);
                    sp -= n * 2;
                    st[sp] = new DataToken(dict);
                    sp++;
                    pc++;
                    break;

                case OP_TYPEOF:
                    a = st[sp - 1];
                    ta = a.TokenType;
                    string typeName = "object";
                    if (ta == TokenType.Double) typeName = "number";
                    else if (ta == TokenType.String) typeName = "string";
                    else if (ta == TokenType.Boolean) typeName = "boolean";
                    else if (ta == TokenType.Null) typeName = "undefined";
                    else if (ta == TokenType.Reference)
                    {
                        object[] r = (object[])a.Reference;
                        int kind = (int)r[0];
                        if (kind == K_CLOSURE || (kind == K_HOST && (" " + (string)r[1] + " ").IndexOf('.') >= 0)) typeName = "function";
                        else if (kind == K_HOST && " parseInt parseFloat isNaN isFinite alert setTimeout setInterval clearTimeout clearInterval requestAnimationFrame Number String Boolean Date Array Object ".Contains(" " + (string)r[1] + " ")) typeName = "function";
                    }
                    st[sp - 1] = new DataToken(typeName);
                    pc++;
                    break;

                case OP_NEW:
                    n = cA[pc];
                    v = Construct(cS[pc], sp - n, n);
                    sp -= n;
                    st[sp] = v;
                    sp++;
                    pc++;
                    break;

                case OP_KEYS:
                    st[sp - 1] = Iterable(st[sp - 1], cA[pc] == 1);
                    pc++;
                    break;

                case OP_DELM:
                    a = st[sp - 1];
                    if (a.TokenType == TokenType.DataDictionary) a.DataDictionary.Remove(cS[pc]);
                    st[sp - 1] = new DataToken(true);
                    pc++;
                    break;

                case OP_THROW:
                    sp--;
                    a = st[sp];
                    if (a.TokenType == TokenType.DataDictionary && a.DataDictionary.TryGetValue("message", out v)) a = v;
                    Fail(ToStr(a));
                    return;

                default: // OP_HALT
                    running = false;
                    break;
            }
        }
    }

    private DataToken Iterable(DataToken t, bool keys)
    {
        TokenType tt = t.TokenType;
        DataList list = new DataList();
        if (tt == TokenType.DataList)
        {
            if (!keys) return t;
            int n = t.DataList.Count;
            for (int i = 0; i < n; i++) list.Add(new DataToken((double)i));
        }
        else if (tt == TokenType.DataDictionary)
        {
            return new DataToken(keys ? t.DataDictionary.GetKeys() : t.DataDictionary.GetValues());
        }
        else if (tt == TokenType.String)
        {
            string s = t.String;
            int n = Mathf.Min(s.Length, 5000);
            for (int i = 0; i < n; i++)
            {
                if (keys) list.Add(new DataToken((double)i));
                else list.Add(new DataToken(s.Substring(i, 1)));
            }
        }
        else if (!keys) Fail(ToStr(t) + " は繰り返しできません");
        return new DataToken(list);
    }

    private DataToken Construct(string name, int argBase, int argc)
    {
        if (name == "Date")
        {
            DateTime time = DateTime.Now;
            if (argc == 1 && st[argBase].TokenType == TokenType.Double)
            {
                time = new DateTime(1970, 1, 1).AddMilliseconds(st[argBase].Double).ToLocalTime();
            }
            else if (argc >= 3)
            {
                int hour = argc > 3 ? (int)ToNum(st[argBase + 3]) : 0;
                int minute = argc > 4 ? (int)ToNum(st[argBase + 4]) : 0;
                int second = argc > 5 ? (int)ToNum(st[argBase + 5]) : 0;
                time = new DateTime((int)ToNum(st[argBase]), 1, 1, 0, 0, 0)
                    .AddMonths((int)ToNum(st[argBase + 1])).AddDays(ToNum(st[argBase + 2]) - 1)
                    .AddHours(hour).AddMinutes(minute).AddSeconds(second);
            }
            object[] r = new object[2];
            r[0] = K_DATE;
            r[1] = time;
            return new DataToken((object)r);
        }
        if (name == "Array")
        {
            DataList list = new DataList();
            if (argc == 1 && st[argBase].TokenType == TokenType.Double)
            {
                int n = Mathf.Clamp((int)st[argBase].Double, 0, 10000);
                for (int i = 0; i < n; i++) list.Add(tUndef);
            }
            else
            {
                for (int i = 0; i < argc; i++) list.Add(st[argBase + i]);
            }
            return new DataToken(list);
        }
        if (name == "Object") return new DataToken(new DataDictionary());
        if (name == "Error")
        {
            DataDictionary error = new DataDictionary();
            error.SetValue("message", argc > 0 ? st[argBase] : new DataToken(""));
            return new DataToken(error);
        }
        Fail("new " + name + " は未対応です (Date / Array / Object / Error のみ)");
        return tUndef;
    }

    // ---------- members ----------

    private DataToken GetMember(DataToken obj, string name)
    {
        TokenType tt = obj.TokenType;
        DataToken v;
        if (tt == TokenType.DataDictionary)
        {
            if (obj.DataDictionary.TryGetValue(name, out v)) return v;
            return tUndef;
        }
        if (tt == TokenType.DataList)
        {
            if (name == "length") return new DataToken((double)obj.DataList.Count);
            double index = ParseDouble(name);
            if (numOk && numEnd == name.Length && index >= 0 && index < obj.DataList.Count) return obj.DataList[(int)index];
            return tUndef;
        }
        if (tt == TokenType.String)
        {
            if (name == "length") return new DataToken((double)obj.String.Length);
            return tUndef;
        }
        if (tt == TokenType.Reference)
        {
            object[] r = (object[])obj.Reference;
            int kind = (int)r[0];
            if (kind == K_HOST) return HostGet((string)r[1], name);
            if (kind == K_ELEM) return ElemGet(r, name);
            if (kind == K_STYLE)
            {
                if (!Locate((string)r[1], (int)r[2])) return tUndef;
                return new DataToken(GetStyle(Kebab(name)));
            }
            if (kind == K_CLASSLIST)
            {
                if (name != "length" || !Locate((string)r[1], (int)r[2])) return tUndef;
                string classes = GetAttr("class").Trim();
                return new DataToken(classes.Length == 0 ? 0.0 : (double)classes.Split(' ').Length);
            }
            return tUndef;
        }
        if (tt == TokenType.Null || tt == TokenType.Error)
        {
            Fail(ToStr(obj) + " のプロパティ '" + name + "' は読めません");
            return tUndef;
        }
        return tUndef;
    }

    private void SetMember(DataToken obj, string name, DataToken value)
    {
        TokenType tt = obj.TokenType;
        if (tt == TokenType.DataDictionary)
        {
            obj.DataDictionary.SetValue(name, value);
            return;
        }
        if (tt == TokenType.DataList)
        {
            DataList list = obj.DataList;
            if (name == "length")
            {
                int n = Mathf.Max(0, (int)ToNum(value));
                while (list.Count > n) list.RemoveAt(list.Count - 1);
            }
            return;
        }
        if (tt == TokenType.Reference)
        {
            object[] r = (object[])obj.Reference;
            int kind = (int)r[0];
            if (kind == K_HOST) HostSet((string)r[1], name, value);
            else if (kind == K_ELEM) ElemSet(r, name, value);
            else if (kind == K_STYLE)
            {
                if (!Locate((string)r[1], (int)r[2])) return;
                if (name == "cssText") SetAttr("style", ToStr(value));
                else SetStyle(Kebab(name), ToStr(value));
            }
            return;
        }
        if (tt == TokenType.Null || tt == TokenType.Error) Fail(ToStr(obj) + " のプロパティ '" + name + "' には書き込めません");
    }

    // ---------- method calls ----------

    private void CallMethod(string name, int argc)
    {
        int objPos = sp - argc - 1;
        DataToken obj = st[objPos];
        TokenType tt = obj.TokenType;
        DataToken result = tUndef;
        DataToken member;

        if (tt == TokenType.DataDictionary)
        {
            if (obj.DataDictionary.TryGetValue(name, out member) && member.TokenType == TokenType.Reference)
            {
                object[] r = (object[])member.Reference;
                int kind = (int)r[0];
                if (kind == K_CLOSURE)
                {
                    EnterClosure(r, objPos, argc, true, obj);
                    return;
                }
                if (kind == K_HOST) result = CallHost((string)r[1], objPos + 1, argc);
            }
            else if (name == "hasOwnProperty") result = new DataToken(argc > 0 && obj.DataDictionary.ContainsKey(ToStr(st[objPos + 1])));
            else if (name == "toString") result = new DataToken("[object Object]");
            else
            {
                Fail(name + " は関数ではありません");
                return;
            }
        }
        else if (tt == TokenType.DataList)
        {
            if (ArrayHof.Contains(" " + name + " "))
            {
                DataToken helper;
                if (!builtinEnv.TryGetValue("__" + name, out helper))
                {
                    Fail(name + " は使えません");
                    return;
                }
                // [arr, args...] -> [fn, arr, args...]
                for (int i = sp; i > objPos; i--) st[i] = st[i - 1];
                sp++;
                st[objPos] = helper;
                EnterClosure((object[])helper.Reference, objPos, argc + 1, false, tUndef);
                return;
            }
            hBase = objPos + 1;
            hArgc = argc;
            result = ListMethod(obj.DataList, name);
        }
        else if (tt == TokenType.String)
        {
            hBase = objPos + 1;
            hArgc = argc;
            result = StringMethod(obj.String, name);
        }
        else if (tt == TokenType.Double)
        {
            double d = obj.Double;
            if (name == "toFixed")
            {
                int digits = argc > 0 ? Mathf.Clamp((int)ToNum(st[objPos + 1]), 0, 15) : 0;
                result = new DataToken(d.ToString("F" + digits).Replace(',', '.'));
            }
            else if (name == "toString") result = new DataToken(NumStr(d));
            else
            {
                Fail("数値に " + name + " はありません");
                return;
            }
        }
        else if (tt == TokenType.Boolean)
        {
            result = new DataToken(ToStr(obj));
        }
        else if (tt == TokenType.Reference)
        {
            object[] r = (object[])obj.Reference;
            int kind = (int)r[0];
            hBase = objPos + 1;
            hArgc = argc;
            if (kind == K_HOST)
            {
                string ns = (string)r[1];
                if (ns == "window" || ns == "globalThis")
                {
                    DataToken global;
                    if (globalEnv.TryGetValue(name, out global) && IsClosure(global))
                    {
                        EnterClosure((object[])global.Reference, objPos, argc, false, tUndef);
                        return;
                    }
                    result = CallHost(name, objPos + 1, argc);
                }
                else result = CallHost(ns + "." + name, objPos + 1, argc);
            }
            else if (kind == K_CLOSURE)
            {
                if (name == "call")
                {
                    DataToken self = argc > 0 ? st[objPos + 1] : tUndef;
                    for (int i = objPos + 1; i < sp - 1; i++) st[i] = st[i + 1];
                    if (argc > 0) sp--;
                    EnterClosure(r, objPos, argc > 0 ? argc - 1 : 0, true, self);
                    return;
                }
                Fail("関数の " + name + " は未対応です");
                return;
            }
            else if (kind == K_ELEM) result = ElemMethod(r, name);
            else if (kind == K_STYLE)
            {
                if (Locate((string)r[1], (int)r[2]))
                {
                    if (name == "setProperty" && argc >= 2) SetStyle(ArgStr(0), ArgStr(1));
                    else if (name == "removeProperty" && argc >= 1) SetStyle(ArgStr(0), "");
                    else if (name == "getPropertyValue" && argc >= 1) result = new DataToken(GetStyle(ArgStr(0)));
                }
            }
            else if (kind == K_CLASSLIST) result = ClassListMethod(r, name);
            else if (kind == K_DATE) result = DateMethod((DateTime)r[1], name);
        }
        else
        {
            Fail(ToStr(obj) + " の " + name + " は呼び出せません");
            return;
        }

        if (!running) return;
        sp = objPos;
        st[sp] = result;
        sp++;
        pc++;
    }

    private DataToken Arg(int i)
    {
        return i < hArgc ? st[hBase + i] : tUndef;
    }

    private string ArgStr(int i)
    {
        return i < hArgc ? ToStr(st[hBase + i]) : "";
    }

    private double ArgNum(int i)
    {
        return i < hArgc ? ToNum(st[hBase + i]) : double.NaN;
    }

    private int ClampIndex(double value, int length, int fallback)
    {
        if (double.IsNaN(value)) return fallback;
        if (value < 0) value = length + value;
        if (value < 0) return 0;
        if (value > length) return length;
        return (int)value;
    }

    private DataToken StringMethod(string s, string name)
    {
        int length = s.Length;
        if (name == "indexOf") return Num(s.IndexOf(ArgStr(0)));
        if (name == "includes") return Bool(s.Contains(ArgStr(0)));
        if (name == "slice")
        {
            int from = ClampIndex(ArgNum(0), length, 0);
            int to = ClampIndex(ArgNum(1), length, length);
            return Str(to > from ? s.Substring(from, to - from) : "");
        }
        if (name == "substring")
        {
            int from = Mathf.Clamp((int)ToNum(Arg(0)), 0, length);
            int to = hArgc > 1 ? Mathf.Clamp((int)ToNum(Arg(1)), 0, length) : length;
            if (from > to)
            {
                int swap = from;
                from = to;
                to = swap;
            }
            return Str(s.Substring(from, to - from));
        }
        if (name == "substr")
        {
            int from = ClampIndex(ArgNum(0), length, 0);
            int count = hArgc > 1 ? Mathf.Clamp((int)ToNum(Arg(1)), 0, length - from) : length - from;
            return Str(s.Substring(from, count));
        }
        if (name == "split")
        {
            DataList parts = new DataList();
            string separator = ArgStr(0);
            if (hArgc == 0) parts.Add(Str(s));
            else if (separator.Length == 0)
            {
                int n = Mathf.Min(length, 5000);
                for (int i = 0; i < n; i++) parts.Add(Str(s.Substring(i, 1)));
            }
            else
            {
                int from = 0;
                for (int guard = 0; guard < 5000; guard++)
                {
                    int at = s.IndexOf(separator, from);
                    if (at < 0) break;
                    parts.Add(Str(s.Substring(from, at - from)));
                    from = at + separator.Length;
                }
                parts.Add(Str(s.Substring(from)));
            }
            return new DataToken(parts);
        }
        if (name == "replace")
        {
            string find = ArgStr(0);
            int at = find.Length == 0 ? -1 : s.IndexOf(find);
            if (at < 0) return Str(s);
            return Str(s.Substring(0, at) + ArgStr(1) + s.Substring(at + find.Length));
        }
        if (name == "replaceAll")
        {
            string find = ArgStr(0);
            return Str(find.Length == 0 ? s : s.Replace(find, ArgStr(1)));
        }
        if (name == "toUpperCase") return Str(s.ToUpper());
        if (name == "toLowerCase") return Str(s.ToLower());
        if (name == "trim") return Str(s.Trim());
        if (name == "trimStart") return Str(s.TrimStart());
        if (name == "trimEnd") return Str(s.TrimEnd());
        if (name == "startsWith") return Bool(s.StartsWith(ArgStr(0)));
        if (name == "endsWith") return Bool(s.EndsWith(ArgStr(0)));
        if (name == "charAt")
        {
            int at = hArgc > 0 ? (int)ToNum(Arg(0)) : 0;
            return Str(at >= 0 && at < length ? s.Substring(at, 1) : "");
        }
        if (name == "charCodeAt")
        {
            int at = hArgc > 0 ? (int)ToNum(Arg(0)) : 0;
            if (at < 0 || at >= length) return Num(double.NaN);
            return Num(Convert.ToInt32(s[at]));
        }
        if (name == "lastIndexOf") return Num(s.LastIndexOf(ArgStr(0)));
        if (name == "repeat")
        {
            int count = Mathf.Clamp((int)ToNum(Arg(0)), 0, 2000);
            string text = "";
            for (int i = 0; i < count && text.Length < 20000; i++) text = text + s;
            return Str(text);
        }
        if (name == "padStart" || name == "padEnd")
        {
            int width = Mathf.Clamp((int)ToNum(Arg(0)), 0, 2000);
            string pad = hArgc > 1 ? ArgStr(1) : " ";
            if (pad.Length == 0) return Str(s);
            string text = s;
            for (int guard = 0; guard < 2000 && text.Length < width; guard++)
            {
                string piece = pad.Substring(0, Mathf.Min(pad.Length, width - text.Length));
                text = name == "padStart" ? piece + text : text + piece;
            }
            if (name == "padStart" && pad.Length > 1 && text.Length > length)
            {
                // padding is built left to right even when it is prepended
                int padLength = text.Length - length;
                string padding = "";
                while (padding.Length < padLength) padding = padding + pad;
                text = padding.Substring(0, padLength) + s;
            }
            return Str(text);
        }
        if (name == "concat")
        {
            string text = s;
            for (int i = 0; i < hArgc; i++) text = text + ArgStr(i);
            return Str(text);
        }
        if (name == "toString" || name == "valueOf") return Str(s);
        Fail("文字列に " + name + " はありません");
        return tUndef;
    }

    private DataToken ListMethod(DataList list, string name)
    {
        int count = list.Count;
        if (name == "push")
        {
            for (int i = 0; i < hArgc; i++) list.Add(st[hBase + i]);
            return Num(list.Count);
        }
        if (name == "pop")
        {
            if (count == 0) return tUndef;
            DataToken last = list[count - 1];
            list.RemoveAt(count - 1);
            return last;
        }
        if (name == "shift")
        {
            if (count == 0) return tUndef;
            DataToken first = list[0];
            list.RemoveAt(0);
            return first;
        }
        if (name == "unshift")
        {
            for (int i = 0; i < hArgc; i++) list.Insert(i, st[hBase + i]);
            return Num(list.Count);
        }
        if (name == "join") return Str(JoinList(list, hArgc > 0 ? ArgStr(0) : ","));
        if (name == "indexOf" || name == "includes")
        {
            DataToken needle = Arg(0);
            int at = -1;
            for (int i = 0; i < count; i++)
            {
                if (!StrictEq(list[i], needle)) continue;
                at = i;
                break;
            }
            if (name == "includes") return Bool(at >= 0);
            return Num(at);
        }
        if (name == "slice")
        {
            int from = ClampIndex(ArgNum(0), count, 0);
            int to = ClampIndex(ArgNum(1), count, count);
            DataList part = new DataList();
            for (int i = from; i < to; i++) part.Add(list[i]);
            return new DataToken(part);
        }
        if (name == "splice")
        {
            int from = ClampIndex(ArgNum(0), count, 0);
            int remove = hArgc > 1 ? Mathf.Clamp((int)ToNum(Arg(1)), 0, count - from) : count - from;
            DataList removed = new DataList();
            for (int i = 0; i < remove; i++)
            {
                removed.Add(list[from]);
                list.RemoveAt(from);
            }
            for (int i = 2; i < hArgc; i++) list.Insert(from + i - 2, st[hBase + i]);
            return new DataToken(removed);
        }
        if (name == "concat")
        {
            DataList joined = list.ShallowClone();
            for (int i = 0; i < hArgc; i++)
            {
                DataToken item = st[hBase + i];
                if (item.TokenType == TokenType.DataList) joined.AddRange(item.DataList);
                else joined.Add(item);
            }
            return new DataToken(joined);
        }
        if (name == "reverse")
        {
            list.Reverse();
            return new DataToken(list);
        }
        if (name == "toString") return Str(JoinList(list, ","));
        Fail("配列に " + name + " はありません");
        return tUndef;
    }

    private DataToken DateMethod(DateTime time, string name)
    {
        if (name == "getHours") return Num(time.Hour);
        if (name == "getMinutes") return Num(time.Minute);
        if (name == "getSeconds") return Num(time.Second);
        if (name == "getMilliseconds") return Num(time.Millisecond);
        if (name == "getFullYear") return Num(time.Year);
        if (name == "getMonth") return Num(time.Month - 1);
        if (name == "getDate") return Num(time.Day);
        if (name == "getDay") return Num((int)time.DayOfWeek);
        if (name == "getTime" || name == "valueOf") return Num((time.ToUniversalTime() - new DateTime(1970, 1, 1)).TotalMilliseconds);
        if (name == "toLocaleTimeString") return Str(time.ToString("HH:mm:ss"));
        if (name == "toLocaleDateString") return Str(time.ToString("yyyy/M/d"));
        if (name == "toLocaleString" || name == "toString") return Str(time.ToString("yyyy/M/d HH:mm:ss"));
        if (name == "toDateString") return Str(time.ToString("yyyy-MM-dd"));
        if (name == "toISOString" || name == "toJSON") return Str(time.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ"));
        Fail("Date に " + name + " はありません");
        return tUndef;
    }

    // =====================================================================
    // Host objects
    // =====================================================================

    private DataToken HostGet(string ns, string name)
    {
        if (ns == "Math")
        {
            if (name == "PI") return Num(Math.PI);
            if (name == "E") return Num(Math.E);
        }
        else if (ns == "document")
        {
            if (name == "body") return MakeRef(K_ELEM, "body", 0);
            if (name == "documentElement") return MakeRef(K_ELEM, "html", 0);
            if (name == "head") return MakeRef(K_ELEM, "head", 0);
            if (name == "readyState") return Str("complete");
            if (name == "title")
            {
                if (!Locate("title", 0)) return Str("");
                return Str(InnerText());
            }
        }
        else if (ns == "window" || ns == "globalThis")
        {
            DataToken v;
            if (globalEnv.TryGetValue(name, out v)) return v;
            if (HostNames.Contains(" " + name + " ")) return MakeRef(K_HOST, name, 0);
            return tUndef;
        }
        else if (ns == "location")
        {
            if (name == "href") return Str(pageUrl);
            return Str("");
        }
        else if (ns == "navigator")
        {
            if (name == "userAgent") return Str("VRCbrowser");
            return Str("");
        }
        else if (ns == "Number")
        {
            if (name == "MAX_SAFE_INTEGER") return Num(9007199254740991.0);
        }
        return MakeRef(K_HOST, ns + "." + name, 0);
    }

    private void HostSet(string ns, string name, DataToken value)
    {
        if (ns == "window" || ns == "globalThis") globalEnv.SetValue(name, value);
        else if (ns == "document" && name == "title" && browser != null) browser._JsStatus(ToStr(value));
    }

    private string Display(DataToken t)
    {
        TokenType tt = t.TokenType;
        if (tt == TokenType.DataList || tt == TokenType.DataDictionary)
        {
            DataToken json;
            if (VRCJson.TrySerializeToJson(t, JsonExportType.Minify, out json)) return json.String;
        }
        return ToStr(t);
    }

    private DataToken CallHost(string full, int argBase, int argc)
    {
        hBase = argBase;
        hArgc = argc;
        int dot = full.IndexOf('.');
        if (dot < 0)
        {
            if (full == "parseInt")
            {
                string s = ArgStr(0).Trim();
                double radix = argc > 1 ? ToNum(Arg(1)) : 10;
                if (radix == 16 || s.StartsWith("0x") || s.StartsWith("0X"))
                {
                    string lower = s.ToLower();
                    int at = lower.StartsWith("0x") ? 2 : 0;
                    double hex = 0;
                    bool any = false;
                    while (at < lower.Length)
                    {
                        int digit = HexLower.IndexOf(lower[at]);
                        if (digit < 0) break;
                        hex = hex * 16 + digit;
                        any = true;
                        at++;
                    }
                    return Num(any ? hex : double.NaN);
                }
                double value = ParseDouble(s);
                if (!numOk) return Num(double.NaN);
                return Num(value < 0 ? Math.Ceiling(value) : Math.Floor(value));
            }
            if (full == "parseFloat")
            {
                double value = ParseDouble(ArgStr(0).Trim());
                return Num(numOk ? value : double.NaN);
            }
            if (full == "Number") return Num(argc > 0 ? ToNum(Arg(0)) : 0);
            if (full == "String") return Str(ArgStr(0));
            if (full == "Boolean") return Bool(Truthy(Arg(0)));
            if (full == "isNaN") return Bool(double.IsNaN(ToNum(Arg(0))));
            if (full == "isFinite")
            {
                double value = ToNum(Arg(0));
                return Bool(!double.IsNaN(value) && !double.IsInfinity(value));
            }
            if (full == "alert")
            {
                if (browser != null) browser._JsStatus(ArgStr(0));
                return tUndef;
            }
            if (full == "setTimeout") return AddTimer(Arg(0), ArgNum(1), false);
            if (full == "setInterval") return AddTimer(Arg(0), ArgNum(1), true);
            if (full == "requestAnimationFrame") return AddTimer(Arg(0), 0, false);
            if (full == "clearTimeout" || full == "clearInterval")
            {
                int id = (int)ToNum(Arg(0));
                for (int i = 0; i < MaxTimers; i++)
                {
                    if (tmUsed[i] && tmId[i] == id) tmUsed[i] = false;
                }
                return tUndef;
            }
            if (full == "Array" || full == "Object" || full == "Date") return Construct(full, argBase, argc);
            Fail(full + " は関数として呼び出せません");
            return tUndef;
        }

        string ns = full.Substring(0, dot);
        string fn = full.Substring(dot + 1);
        if (ns == "Math")
        {
            double x = ArgNum(0);
            if (fn == "floor") return Num(Math.Floor(x));
            if (fn == "random") return Num(UnityEngine.Random.value * 0.999999f);
            if (fn == "round") return Num(Math.Floor(x + 0.5));
            if (fn == "ceil") return Num(Math.Ceiling(x));
            if (fn == "abs") return Num(Math.Abs(x));
            if (fn == "min" || fn == "max")
            {
                if (argc == 0) return Num(fn == "min" ? double.PositiveInfinity : double.NegativeInfinity);
                double best = x;
                for (int i = 1; i < argc; i++)
                {
                    double y = ArgNum(i);
                    if (double.IsNaN(y)) return Num(double.NaN);
                    if (fn == "min" ? y < best : y > best) best = y;
                }
                return Num(best);
            }
            if (fn == "sqrt") return Num(Math.Sqrt(x));
            if (fn == "pow") return Num(Math.Pow(x, ArgNum(1)));
            if (fn == "trunc") return Num(x < 0 ? Math.Ceiling(x) : Math.Floor(x));
            if (fn == "sign") return Num(x > 0 ? 1 : (x < 0 ? -1 : 0));
            if (fn == "sin") return Num(Math.Sin(x));
            if (fn == "cos") return Num(Math.Cos(x));
            if (fn == "tan") return Num(Math.Tan(x));
            if (fn == "atan") return Num(Math.Atan(x));
            if (fn == "atan2") return Num(Math.Atan2(x, ArgNum(1)));
            if (fn == "log") return Num(Math.Log(x));
            if (fn == "exp") return Num(Math.Exp(x));
        }
        else if (ns == "console")
        {
            string line = "";
            for (int i = 0; i < argc; i++) line = line + (i > 0 ? " " : "") + Display(st[argBase + i]);
            if (fn == "error" || fn == "warn") Debug.LogWarning("[VRCbrowser JS] " + line);
            else Debug.Log("[VRCbrowser JS] " + line);
            return tUndef;
        }
        else if (ns == "document")
        {
            if (fn == "getElementById")
            {
                string sel = "#" + ArgStr(0);
                return Locate(sel, 0) ? MakeRef(K_ELEM, sel, 0) : tNull;
            }
            if (fn == "querySelector")
            {
                string sel = ArgStr(0).Trim();
                return Locate(sel, 0) ? MakeRef(K_ELEM, sel, 0) : tNull;
            }
            if (fn == "querySelectorAll" || fn == "getElementsByClassName" || fn == "getElementsByTagName")
            {
                string sel = ArgStr(0).Trim();
                if (fn == "getElementsByClassName") sel = "." + sel;
                DataList list = new DataList();
                if (ParseSelector(sel))
                {
                    int from = 0;
                    for (int i = 0; i < 200; i++)
                    {
                        if (NextMatch(from) < 0) break;
                        from = matchResume;
                        list.Add(MakeRef(K_ELEM, sel, i));
                    }
                }
                return new DataToken(list);
            }
            if (fn == "write" || fn == "writeln")
            {
                string text = "";
                for (int i = 0; i < argc; i++) text = text + ArgStr(i);
                if (fn == "writeln") text = text + "\n";
                DocumentWrite(text);
                return tUndef;
            }
            if (fn == "addEventListener")
            {
                string type = ArgStr(0);
                if ((type == "DOMContentLoaded" || type == "load") && IsClosure(Arg(1))) pendingCalls.Add(Arg(1));
                return tUndef;
            }
        }
        else if (ns == "JSON")
        {
            DataToken json;
            if (fn == "stringify")
            {
                DataToken value = Arg(0);
                TokenType tt = value.TokenType;
                if (tt == TokenType.String) return Str("\"" + value.String.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") + "\"");
                if (tt == TokenType.DataList || tt == TokenType.DataDictionary)
                {
                    JsonExportType style = argc > 2 ? JsonExportType.Beautify : JsonExportType.Minify;
                    if (VRCJson.TrySerializeToJson(value, style, out json)) return json;
                    return tUndef;
                }
                if (tt == TokenType.Null) return tUndef;
                return Str(ToStr(value));
            }
            if (fn == "parse")
            {
                if (VRCJson.TryDeserializeFromJson(ArgStr(0), out json)) return json;
                Fail("JSON.parse: JSON として読めません");
                return tUndef;
            }
        }
        else if (ns == "Object")
        {
            DataToken target = Arg(0);
            if (fn == "keys") return Iterable(target, true);
            if (fn == "values") return Iterable(target, false);
            if (fn == "entries")
            {
                DataList entries = new DataList();
                if (target.TokenType == TokenType.DataDictionary)
                {
                    DataList keys = target.DataDictionary.GetKeys();
                    for (int i = 0; i < keys.Count; i++)
                    {
                        DataList pair = new DataList();
                        pair.Add(keys[i]);
                        pair.Add(target.DataDictionary[keys[i]]);
                        entries.Add(new DataToken(pair));
                    }
                }
                return new DataToken(entries);
            }
            if (fn == "assign")
            {
                if (target.TokenType != TokenType.DataDictionary) return target;
                for (int i = 1; i < argc; i++)
                {
                    DataToken source = st[argBase + i];
                    if (source.TokenType != TokenType.DataDictionary) continue;
                    DataList keys = source.DataDictionary.GetKeys();
                    for (int k = 0; k < keys.Count; k++) target.DataDictionary.SetValue(keys[k], source.DataDictionary[keys[k]]);
                }
                return target;
            }
        }
        else if (ns == "Array")
        {
            if (fn == "isArray") return Bool(Arg(0).TokenType == TokenType.DataList);
            if (fn == "from")
            {
                DataToken source = Arg(0);
                if (source.TokenType == TokenType.DataList) return new DataToken(source.DataList.ShallowClone());
                return Iterable(source, false);
            }
        }
        else if (ns == "Date")
        {
            if (fn == "now") return Num((DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds);
        }
        else if (ns == "Number")
        {
            double value = ToNum(Arg(0));
            if (fn == "isInteger") return Bool(Arg(0).TokenType == TokenType.Double && value == Math.Floor(value) && !double.IsInfinity(value));
            if (fn == "isNaN") return Bool(Arg(0).TokenType == TokenType.Double && double.IsNaN(value));
            if (fn == "parseFloat") return CallHost("parseFloat", argBase, argc);
            if (fn == "parseInt") return CallHost("parseInt", argBase, argc);
        }
        else if (ns == "String")
        {
            if (fn == "fromCharCode")
            {
                string text = "";
                for (int i = 0; i < argc; i++) text = text + Convert.ToChar((int)ArgNum(i)).ToString();
                return Str(text);
            }
        }
        else if (ns == "window" || ns == "globalThis")
        {
            if (fn == "addEventListener")
            {
                string type = ArgStr(0);
                if ((type == "DOMContentLoaded" || type == "load") && IsClosure(Arg(1))) pendingCalls.Add(Arg(1));
                return tUndef;
            }
            return CallHost(fn, argBase, argc);
        }
        else if (ns == "location")
        {
            return tUndef; // navigation needs a VRCUrl, which a script cannot create
        }

        Fail(full + " は未対応です");
        return tUndef;
    }

    private DataToken AddTimer(DataToken fn, double ms, bool repeat)
    {
        if (!IsClosure(fn))
        {
            Fail("タイマーには関数を渡してください");
            return tUndef;
        }
        float delay = double.IsNaN(ms) ? 0f : (float)(ms * 0.001);
        if (delay < minTimerDelay) delay = minTimerDelay;
        for (int i = 0; i < MaxTimers; i++)
        {
            if (tmUsed[i]) continue;
            tmUsed[i] = true;
            tmId[i] = tmNextId;
            tmNextId++;
            tmFn[i] = fn;
            tmAt[i] = Time.time + delay;
            tmEvery[i] = repeat ? delay : 0f;
            return Num(tmId[i]);
        }
        Fail("タイマーが多すぎます (上限 " + MaxTimers + ")");
        return tUndef;
    }

    // =====================================================================
    // Document (HTML source) access
    // =====================================================================

    private void Splice(int start, int end, string text)
    {
        docHtml = docHtml.Substring(0, start) + text + docHtml.Substring(end);
        docLower = docHtml.ToLower();
        docDirty = true;
        if (docHtml.Length > MaxDocChars) Fail("ページが大きくなりすぎました");
    }

    private void DocumentWrite(string text)
    {
        if (writeIndex >= 0)
        {
            int marker = docHtml.IndexOf("<!--js:" + writeIndex + "-->");
            if (marker >= 0)
            {
                Splice(marker, marker, text);
                return;
            }
        }
        int body = docLower.LastIndexOf("</body");
        if (body < 0) body = docHtml.Length;
        Splice(body, body, text);
    }

    /// <summary>Supported selectors: tag, #id, .class, tag#id, tag.class.</summary>
    private bool ParseSelector(string sel)
    {
        selTag = "";
        selKey = "";
        if (sel.Length == 0 || sel.IndexOf(' ') >= 0 || sel.IndexOf('>') >= 0 || sel.IndexOf('[') >= 0 || sel.IndexOf(',') >= 0 || sel.IndexOf(':') >= 0 || sel.IndexOf('*') >= 0) return false;
        int hash = sel.IndexOf('#');
        int dot = sel.IndexOf('.');
        if (hash >= 0)
        {
            selKind = 1;
            selTag = sel.Substring(0, hash).ToLower();
            selKey = sel.Substring(hash + 1);
            if (selKey.IndexOf('.') >= 0) return false;
        }
        else if (dot >= 0)
        {
            selKind = 2;
            selTag = sel.Substring(0, dot).ToLower();
            selKey = sel.Substring(dot + 1);
            if (selKey.IndexOf('.') >= 0) return false;
        }
        else
        {
            selKind = 0;
            selKey = sel.ToLower();
        }
        return selKey.Length > 0;
    }

    /// <summary>Position of the "&lt;" of the next element matching the parsed selector, or -1. Continue from matchResume.</summary>
    private int NextMatch(int from)
    {
        int p = from;
        int n = docLower.Length;
        if (selKind == 0)
        {
            string open = "<" + selKey;
            for (int guard = 0; guard < 4000; guard++)
            {
                int i = docLower.IndexOf(open, p);
                if (i < 0) return -1;
                int after = i + open.Length;
                p = i + 1;
                if (after < n && TagEnd.IndexOf(docLower[after]) >= 0)
                {
                    matchResume = p;
                    return i;
                }
            }
            return -1;
        }

        string attr = selKind == 1 ? "id=" : "class=";
        for (int guard = 0; guard < 4000; guard++)
        {
            int a = docLower.IndexOf(attr, p);
            if (a < 0) return -1;
            p = a + attr.Length;
            if (a == 0 || p >= n || AttrLead.IndexOf(docLower[a - 1]) < 0) continue;
            int lt = docHtml.LastIndexOf('<', a);
            if (lt < 0 || docHtml.LastIndexOf('>', a) > lt) continue; // not inside a tag

            string value;
            char quote = docHtml[p];
            if (quote == '"' || quote == '\'')
            {
                int end = docHtml.IndexOf(quote, p + 1);
                if (end < 0) continue;
                value = docHtml.Substring(p + 1, end - p - 1);
            }
            else
            {
                int end = p;
                while (end < n && TagEnd.IndexOf(docHtml[end]) < 0) end++;
                value = docHtml.Substring(p, end - p);
            }

            bool matches = selKind == 1 ? value == selKey : (" " + value.Replace('\n', ' ').Replace('\t', ' ') + " ").Contains(" " + selKey + " ");
            if (!matches) continue;
            if (selTag.Length > 0)
            {
                int after = lt + 1 + selTag.Length;
                if (after >= n || docLower.Substring(lt + 1, selTag.Length) != selTag || TagEnd.IndexOf(docLower[after]) < 0) continue;
            }
            matchResume = p;
            return lt;
        }
        return -1;
    }

    /// <summary>Finds the nth element matching the selector and fills locOpen / locOpenEnd / locCloseStart / locCloseEnd / locTag.</summary>
    private bool Locate(string sel, int nth)
    {
        if (!ParseSelector(sel)) return false;
        int from = 0;
        int lt = -1;
        for (int i = 0; i <= nth; i++)
        {
            lt = NextMatch(from);
            if (lt < 0) return false;
            from = matchResume;
        }

        int n = docLower.Length;
        locOpen = lt;
        locOpenEnd = docHtml.IndexOf('>', lt);
        if (locOpenEnd < 0) return false;
        int e = lt + 1;
        while (e < n && TagEnd.IndexOf(docLower[e]) < 0) e++;
        locTag = docLower.Substring(lt + 1, e - lt - 1);
        if (VoidTags.Contains(" " + locTag + " ") || docHtml[locOpenEnd - 1] == '/')
        {
            locCloseStart = locOpenEnd + 1;
            locCloseEnd = locOpenEnd + 1;
            return true;
        }

        string openMarker = "<" + locTag;
        string closeMarker = "</" + locTag;
        int depthCount = 1;
        int p = locOpenEnd + 1;
        for (int guard = 0; guard < 4000; guard++)
        {
            int c = docLower.IndexOf(closeMarker, p);
            if (c < 0) break;
            int o = docLower.IndexOf(openMarker, p);
            if (o >= 0 && o < c)
            {
                int after = o + openMarker.Length;
                if (after < n && TagEnd.IndexOf(docLower[after]) >= 0) depthCount++;
                p = o + 1;
                continue;
            }
            int afterClose = c + closeMarker.Length;
            p = c + 1;
            if (afterClose < n && TagEnd.IndexOf(docLower[afterClose]) < 0) continue;
            depthCount--;
            if (depthCount > 0) continue;
            locCloseStart = c;
            int gt = docHtml.IndexOf('>', c);
            locCloseEnd = gt < 0 ? n : gt + 1;
            return true;
        }
        locCloseStart = n;
        locCloseEnd = n;
        return true;
    }

    /// <summary>Finds an attribute in the located open tag: atStart (name), atValStart..atValEnd (value), atEnd.</summary>
    private bool FindAttr(string name)
    {
        string key = name.ToLower() + "=";
        int p = locOpen;
        for (int guard = 0; guard < 50; guard++)
        {
            int a = docLower.IndexOf(key, p);
            if (a < 0 || a > locOpenEnd) return false;
            p = a + key.Length;
            if (AttrLead.IndexOf(docLower[a - 1]) < 0) continue;
            atStart = a;
            char quote = docHtml[p];
            if (quote == '"' || quote == '\'')
            {
                int end = docHtml.IndexOf(quote, p + 1);
                if (end < 0 || end > locOpenEnd) return false;
                atValStart = p + 1;
                atValEnd = end;
                atEnd = end + 1;
            }
            else
            {
                int end = p;
                while (end < locOpenEnd && TagEnd.IndexOf(docHtml[end]) < 0) end++;
                atValStart = p;
                atValEnd = end;
                atEnd = end;
            }
            return true;
        }
        return false;
    }

    private string GetAttr(string name)
    {
        if (!FindAttr(name)) return "";
        return docHtml.Substring(atValStart, atValEnd - atValStart).Replace("&quot;", "\"");
    }

    private void SetAttr(string name, string value)
    {
        string text = name + "=\"" + value.Replace("\"", "&quot;") + "\"";
        if (FindAttr(name))
        {
            Splice(atStart, atEnd, text);
            return;
        }
        int at = docHtml[locOpenEnd - 1] == '/' ? locOpenEnd - 1 : locOpenEnd;
        Splice(at, at, " " + text);
    }

    private void RemoveAttr(string name)
    {
        if (!FindAttr(name)) return;
        int from = docHtml[atStart - 1] == ' ' ? atStart - 1 : atStart;
        Splice(from, atEnd, "");
    }

    private string Kebab(string name)
    {
        if (name == name.ToLower()) return name;
        string result = "";
        for (int i = 0; i < name.Length; i++)
        {
            string ch = name.Substring(i, 1);
            string lower = ch.ToLower();
            result = result + (ch == lower ? ch : "-" + lower);
        }
        return result;
    }

    private string GetStyle(string prop)
    {
        string[] parts = GetAttr("style").Split(';');
        for (int i = 0; i < parts.Length; i++)
        {
            int colon = parts[i].IndexOf(':');
            if (colon < 0) continue;
            if (parts[i].Substring(0, colon).Trim().ToLower() == prop) return parts[i].Substring(colon + 1).Trim();
        }
        return "";
    }

    private void SetStyle(string prop, string value)
    {
        string[] parts = GetAttr("style").Split(';');
        string result = "";
        for (int i = 0; i < parts.Length; i++)
        {
            int colon = parts[i].IndexOf(':');
            if (colon < 0) continue;
            if (parts[i].Substring(0, colon).Trim().ToLower() == prop) continue;
            result = result + parts[i].Trim() + ";";
        }
        if (value.Length > 0) result = result + prop + ":" + value + ";";
        SetAttr("style", result);
    }

    private string InnerText()
    {
        string inner = docHtml.Substring(locOpenEnd + 1, locCloseStart - locOpenEnd - 1);
        for (int guard = 0; guard < 4000; guard++)
        {
            int lt = inner.IndexOf('<');
            if (lt < 0) break;
            int gt = inner.IndexOf('>', lt);
            if (gt < 0)
            {
                inner = inner.Substring(0, lt);
                break;
            }
            inner = inner.Substring(0, lt) + inner.Substring(gt + 1);
        }
        return inner.Replace("&nbsp;", " ").Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"").Replace("&#39;", "'").Replace("&amp;", "&");
    }

    private string EscapeText(string text)
    {
        return text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    }

    private DataToken ElemGet(object[] r, string name)
    {
        string sel = (string)r[1];
        int nth = (int)r[2];
        if (name == "style") return MakeRef(K_STYLE, sel, nth);
        if (name == "classList") return MakeRef(K_CLASSLIST, sel, nth);
        if (!Locate(sel, nth)) return tUndef;
        if (name == "textContent" || name == "innerText") return Str(InnerText());
        if (name == "innerHTML") return Str(docHtml.Substring(locOpenEnd + 1, locCloseStart - locOpenEnd - 1));
        if (name == "outerHTML") return Str(docHtml.Substring(locOpen, locCloseEnd - locOpen));
        if (name == "tagName" || name == "nodeName") return Str(locTag.ToUpper());
        if (name == "className") return Str(GetAttr("class"));
        if (name == "hidden") return Bool(GetStyle("display") == "none");
        if (name == "id" || name == "title" || name == "value" || name == "href" || name == "src" || name == "alt" || name == "name") return Str(GetAttr(name));
        return tUndef;
    }

    private void ElemSet(object[] r, string name, DataToken value)
    {
        if (!Locate((string)r[1], (int)r[2])) return;
        if (name == "textContent" || name == "innerText") Splice(locOpenEnd + 1, locCloseStart, EscapeText(ToStr(value)));
        else if (name == "innerHTML") Splice(locOpenEnd + 1, locCloseStart, ToStr(value));
        else if (name == "outerHTML") Splice(locOpen, locCloseEnd, ToStr(value));
        else if (name == "className") SetAttr("class", ToStr(value));
        else if (name == "hidden") SetStyle("display", Truthy(value) ? "none" : "");
        else if (name == "id" || name == "title" || name == "value" || name == "href" || name == "src" || name == "alt" || name == "name") SetAttr(name, ToStr(value));
    }

    private DataToken ElemMethod(object[] r, string name)
    {
        if (name == "addEventListener" || name == "removeEventListener" || name == "focus" || name == "blur" || name == "scrollIntoView") return tUndef;
        if (!Locate((string)r[1], (int)r[2])) return tUndef;
        if (name == "getAttribute") return FindAttr(ArgStr(0)) ? Str(GetAttr(ArgStr(0))) : tNull;
        if (name == "hasAttribute") return Bool(FindAttr(ArgStr(0)));
        if (name == "setAttribute")
        {
            SetAttr(ArgStr(0), ArgStr(1));
            return tUndef;
        }
        if (name == "removeAttribute")
        {
            RemoveAttr(ArgStr(0));
            return tUndef;
        }
        if (name == "remove")
        {
            Splice(locOpen, locCloseEnd, "");
            return tUndef;
        }
        if (name == "append")
        {
            string text = "";
            for (int i = 0; i < hArgc; i++) text = text + EscapeText(ArgStr(i));
            Splice(locCloseStart, locCloseStart, text);
            return tUndef;
        }
        if (name == "insertAdjacentHTML" || name == "insertAdjacentText")
        {
            string where = ArgStr(0).ToLower();
            string text = name == "insertAdjacentText" ? EscapeText(ArgStr(1)) : ArgStr(1);
            int at = locCloseStart;
            if (where == "beforebegin") at = locOpen;
            else if (where == "afterbegin") at = locOpenEnd + 1;
            else if (where == "afterend") at = locCloseEnd;
            Splice(at, at, text);
            return tUndef;
        }
        if (name == "toString") return Str("[object HTMLElement]");
        Fail("要素の " + name + " は未対応です");
        return tUndef;
    }

    private DataToken ClassListMethod(object[] r, string name)
    {
        if (!Locate((string)r[1], (int)r[2])) return tUndef;
        string current = " " + GetAttr("class").Replace('\n', ' ').Replace('\t', ' ').Trim() + " ";
        if (name == "contains") return Bool(current.Contains(" " + ArgStr(0) + " "));
        bool result = false;
        for (int i = 0; i < hArgc; i++)
        {
            string cls = ArgStr(i).Trim();
            if (cls.Length == 0) continue;
            bool has = current.Contains(" " + cls + " ");
            if (name == "add" || (name == "toggle" && !has))
            {
                if (!has) current = current + cls + " ";
                result = true;
            }
            else if (name == "remove" || name == "toggle")
            {
                current = current.Replace(" " + cls + " ", " ");
                result = false;
            }
            if (name == "toggle") break;
        }
        if (name != "add" && name != "remove" && name != "toggle")
        {
            Fail("classList の " + name + " は未対応です");
            return tUndef;
        }
        SetAttr("class", current.Trim());
        return name == "toggle" ? Bool(result) : tUndef;
    }
}
