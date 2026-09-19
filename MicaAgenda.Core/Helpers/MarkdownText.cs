using System.Text;

namespace MicaAgenda.App.Helpers;

/// <summary>
/// 把 Markdown 片段转成「适合直接塞进 TextBlock / 纯文本气泡」的干净文本。
///
/// 背景：AI 助手的回复、以及推送到 IM 的卡片正文，都是模型/构建器产出的 Markdown。
/// 但气泡控件只是普通 <c>TextBlock</c>，没有任何 Markdown 渲染能力 —— 结果就是
/// 用户看到 <c>**重点**</c>、<c>## 标题</c>、<c>- 列表</c> 这些记号<b>原样</b>显示，
/// 满屏星号井号，读起来比不打格式还乱（用户明确反馈过不接受这种原样推送）。
///
/// 这里不去做一个完整的 Markdown 渲染器（那会把一个气泡控件拖成排版引擎），
/// 只做「去噪 + 保留结构感」这一件事：
/// <list type="bullet">
///   <item>去掉成对的强调记号（<c>**</c> / <c>__</c> / <c>*</c> / <c>_</c> / <c>`</c>），保留里面的字；</item>
///   <item>标题的 <c>#</c> 换成纯文本，不靠记号也读得出层级；</item>
///   <item>无序列表 <c>-</c> / <c>*</c> / <c>+</c> 统一换成 <c>•</c>（比原记号更像列表，也不与强调的星号打架）；</item>
///   <item>引用 <c>&gt;</c>、分隔线 <c>---</c> 这类只剩装饰作用的行，清掉记号保留文字；</item>
///   <item>行内链接 <c>[文字](url)</c> 只留文字（气泡里点不开，留着 URL 反而是噪声）。</item>
/// </list>
///
/// <b>刻意保留</b>换行与缩进：气泡里换行本来就是有效的排版手段。
/// </summary>
public static class MarkdownText
{
    /// <summary>把 Markdown 文本规整成适合纯文本显示的形态。</summary>
    public static string ToPlainText(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown))
        {
            return string.Empty;
        }

        var source = markdown.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = source.Split('\n');
        var result = new List<string>(lines.Length);

        var inFence = false;
        foreach (var raw in lines)
        {
            var line = raw;

            // 代码块围栏：里面是代码，不要动任何记号，只把围栏本身去掉。
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
                result.Add(string.Empty);
                continue;
            }

            if (inFence)
            {
                result.Add(line);
                continue;
            }

            // 分隔线整行丢弃（连位置也不留）：它在纯文本里只剩装饰作用，
            // 若改成塞一个空行，效果是用户看到莫名多出来的一段留白。
            if (IsHorizontalRule(line.Trim()))
            {
                continue;
            }

            result.Add(NormalizeLine(line));
        }

        // 压缩连续空行（三个以上空行 → 一个），避免气泡里出现大片留白
        var sb = new StringBuilder();
        var blankRun = 0;
        foreach (var line in result)
        {
            if (line.Length == 0)
            {
                blankRun++;
                if (blankRun > 1)
                {
                    continue;
                }
            }
            else
            {
                blankRun = 0;
            }

            if (sb.Length > 0)
            {
                sb.Append('\n');
            }

            sb.Append(line);
        }

        return sb.ToString().Trim();
    }

    /// <summary>规整单行：处理标题、列表、引用、分隔线与行内强调。</summary>
    private static string NormalizeLine(string line)
    {
        var trimmed = line.TrimStart();
        var indent = line[..(line.Length - trimmed.Length)];
        if (trimmed.Length == 0)
        {
            return string.Empty;
        }

        // 标题：# 一级 … ###### 六级。去掉井号，保留文字（层级靠文字本身表达）
        var hashes = 0;
        while (hashes < trimmed.Length && trimmed[hashes] == '#')
        {
            hashes++;
        }

        if (hashes is > 0 and <= 6
            && hashes < trimmed.Length
            && char.IsWhiteSpace(trimmed[hashes]))
        {
            trimmed = trimmed[hashes..].TrimStart();
        }
        else if (hashes == trimmed.Length)
        {
            // 整行都是 #，没有标题文字，当空行处理
            return string.Empty;
        }

        // 引用：去掉行首的 >
        if (trimmed.StartsWith('>'))
        {
            trimmed = trimmed[1..].TrimStart();
        }

        // 无序列表：- / * / + 后面跟空格 → 换成 •（并保留缩进表示层级）
        if (trimmed.Length >= 2
            && (trimmed[0] == '-' || trimmed[0] == '*' || trimmed[0] == '+')
            && char.IsWhiteSpace(trimmed[1]))
        {
            trimmed = "• " + trimmed[1..].TrimStart();
        }

        // 有序列表保持原样（1. 2. 本来就看得懂），但要防止 "1)" 这种写法被强调处理误伤，
        // 所以放在强调处理之前什么都不做。

        // 行内链接 / 图片：[文字](url) → 文字；![alt](url) 也一并降级成文字
        trimmed = StripLinks(trimmed);

        // 行内强调：**粗体** __粗体__ *斜体* _斜体_ `代码`
        // 必须最后做：前面的结构记号已经处理完，剩下的星号/下划线/反引号都是强调记号。
        trimmed = StripEmphasis(trimmed);

        return indent + trimmed;
    }

    /// <summary>是不是 Markdown 的水平分隔线（<c>---</c> / <c>***</c> / <c>___</c>，至少三个）。</summary>
    private static bool IsHorizontalRule(string line)
    {
        if (line.Length < 3)
        {
            return false;
        }

        var marker = line[0];
        if (marker is not ('-' or '*' or '_'))
        {
            return false;
        }

        foreach (var ch in line)
        {
            if (ch != marker && !char.IsWhiteSpace(ch))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>把 <c>[文字](url)</c> 降级成 <c>文字</c>。嵌套括号不做支持（模型很少产出）。</summary>
    private static string StripLinks(string line)
    {
        if (!line.Contains('[') || !line.Contains("]("))
        {
            return line;
        }

        var sb = new StringBuilder(line.Length);
        var i = 0;
        while (i < line.Length)
        {
            var open = line.IndexOf('[', i);
            if (open < 0)
            {
                sb.Append(line, i, line.Length - i);
                break;
            }

            var close = line.IndexOf(']', open + 1);
            var paren = close >= 0 && close + 1 < line.Length && line[close + 1] == '('
                ? line.IndexOf(')', close + 2)
                : -1;

            if (close < 0 || paren < 0)
            {
                sb.Append(line, i, line.Length - i);
                break;
            }

            sb.Append(line, i, open - i);
            sb.Append(line, open + 1, close - open - 1);
            i = paren + 1;
        }

        return sb.ToString();
    }

    /// <summary>
    /// 去掉成对的强调记号，保留其中文字。
    ///
    /// 只处理「成对」的情况：单独出现的星号（例如数学里的 3*4）不属于 Markdown，
    /// 原样保留 —— 这类误伤在任务标题里很常见，宁可漏剥也不能吃字。
    /// </summary>
    private static string StripEmphasis(string line)
    {
        if (!line.Contains('*') && !line.Contains('_') && !line.Contains('`'))
        {
            return line;
        }

        var result = line;

        // ** / __ 先处理（优先于单字符），否则 ** 会被拆成两个单字符
        result = RemovePaired(result, "**");
        result = RemovePaired(result, "__");

        // 单字符强调：反引号最安全，直接去
        result = RemovePaired(result, "`");

        // 单个 * 与 _ 只在「两侧都不是同类记号、且成对」时剥掉
        result = RemoveSingleEmphasis(result, '*');
        result = RemoveSingleEmphasis(result, '_');

        return result;
    }

    /// <summary>成对删除某个多字符记号（如 <c>**</c>），保留中间内容。</summary>
    private static string RemovePaired(string value, string token)
    {
        if (token.Length == 0 || !value.Contains(token, StringComparison.Ordinal))
        {
            return value;
        }

        var sb = new StringBuilder(value.Length);
        var i = 0;
        while (i < value.Length)
        {
            var start = value.IndexOf(token, i, StringComparison.Ordinal);
            if (start < 0)
            {
                sb.Append(value, i, value.Length - i);
                break;
            }

            var end = value.IndexOf(token, start + token.Length, StringComparison.Ordinal);
            if (end < 0)
            {
                // 落单的记号：不是 Markdown 强调，原样保留
                sb.Append(value, i, value.Length - i);
                break;
            }

            sb.Append(value, i, start - i);
            sb.Append(value, start + token.Length, end - start - token.Length);
            i = end + token.Length;
        }

        return sb.ToString();
    }

    /// <summary>
    /// 剥掉成对的单字符强调记号（<c>*</c> / <c>_</c>）。
    ///
    /// 判定规则刻意做窄，只认「两侧都是边界」的记号对：
    /// <list type="bullet">
    ///   <item>ASCII 字母 / 数字 / 下划线算「词内字符」（<see cref="IsWordChar"/>）；</item>
    ///   <item>开标记左边不能是词内字符；</item>
    ///   <item>闭标记右边不能是词内字符。</item>
    /// </list>
    ///
    /// 这样 <c>3*4*5</c> 里的星号两侧都是数字、不构成记号对，会原样保留 ——
    /// 任务标题里的算术表达式很常见，误剥等于直接改掉用户的内容，比留下记号更糟。
    /// 而中文标点（如全角冒号）不算词内字符，所以 <c>：*评审*</c> 能正常剥掉。
    /// </summary>
    private static string RemoveSingleEmphasis(string value, char marker)
    {
        if (!value.Contains(marker))
        {
            return value;
        }

        var chars = value.ToCharArray();
        var openIndex = -1;

        for (var i = 0; i < chars.Length; i++)
        {
            if (chars[i] != marker)
            {
                continue;
            }

            var prevIsWord = i > 0 && IsWordChar(chars[i - 1]);
            var nextIsWord = i + 1 < chars.Length && IsWordChar(chars[i + 1]);

            if (openIndex < 0)
            {
                // 开标记：左边是边界，且右边不是空白（`* 文字` 不是强调）
                if (!prevIsWord && !nextIsWord && i + 1 < chars.Length && !char.IsWhiteSpace(chars[i + 1]))
                {
                    openIndex = i;
                }
            }
            else
            {
                // 闭标记：左边不是空白，且右边是边界
                if (!char.IsWhiteSpace(chars[i - 1]) && !nextIsWord)
                {
                    chars[openIndex] = '\0';
                    chars[i] = '\0';
                    openIndex = -1;
                }
            }
        }

        var sb = new StringBuilder(chars.Length);
        foreach (var ch in chars)
        {
            if (ch != '\0')
            {
                sb.Append(ch);
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// 算不算「词内字符」：只认 ASCII 字母 / 数字 / 下划线。
    ///
    /// 这里<b>刻意不把中日韩汉字算作词内</b>：CommonMark 的 flanking 规则看的是
    /// "字母 vs 标点"，而中日韩文字在中文排版里紧挨着标点出现（「：」+ 强调），
    /// 若把汉字也算词内，<c>：*评审*</c> 这种最常见的写法就永远剥不掉。
    /// 只认 ASCII 又能保住 <c>3*4*5</c> 这类算术表达式不被误伤。
    /// </summary>
    private static bool IsWordChar(char ch) =>
        (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9') || ch == '_';
}
