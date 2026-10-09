namespace ProxyHarbor.Infrastructure;

/// <summary>Distinguishes visible publisher containers from quoted or inert HTML.</summary>
internal static class HtmlFeedContext
{
    internal static bool IsNonContent(string content, int index)
    {
        string? rawTag = null;
        var templateDepth = 0;
        for (var position = 0; position < index;)
        {
            if (rawTag is not null)
            {
                var closing = content.IndexOf("</" + rawTag, position, StringComparison.OrdinalIgnoreCase);
                if (closing < 0 || closing >= index) return true;
                var next = closing + rawTag.Length + 2;
                if (next < content.Length && !char.IsWhiteSpace(content[next]) && content[next] != '>')
                {
                    position = next;
                    continue;
                }
                position = closing;
                rawTag = null;
            }
            if (content[position] != '<') { position++; continue; }
            if (content.AsSpan(position).StartsWith("<!--", StringComparison.Ordinal))
            {
                var closing = content.IndexOf("-->", position + 4, StringComparison.Ordinal);
                if (closing < 0 || closing >= index) return true;
                position = closing + 3;
                continue;
            }
            var tagStart = position + 1;
            var end = tagStart;
            var quote = '\0';
            for (; end < index; end++)
            {
                var value = content[end];
                if (quote != '\0') { if (value == quote) quote = '\0'; }
                else if (value is '\'' or '"') quote = value;
                else if (value == '>') break;
            }
            if (end >= index) return true;
            var closingTag = content[tagStart] == '/';
            var nameStart = closingTag ? tagStart + 1 : tagStart;
            var nameEnd = nameStart;
            while (nameEnd < end && char.IsAsciiLetter(content[nameEnd])) nameEnd++;
            var tag = content[nameStart..nameEnd].ToLowerInvariant();
            if (tag == "template")
            {
                templateDepth += closingTag ? -1 : 1;
                if (templateDepth < 0) return true;
            }
            if (!closingTag && tag == "plaintext") return true;
            if (!closingTag && tag is "script" or "style" or "textarea" or "title" or "noscript" or "xmp" or "iframe" or "noembed" or "noframes") rawTag = tag;
            position = end + 1;
        }
        return rawTag is not null || templateDepth != 0;
    }

}
