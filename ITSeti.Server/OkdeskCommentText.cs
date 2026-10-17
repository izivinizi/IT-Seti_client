using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace ITSeti.Server;

public static class OkdeskCommentText
{
    // The raw remote content stays in the database. Only the API view is converted to plain text.
    public static string ForDisplay(string content, bool remoteComment)
    {
        if (!remoteComment || string.IsNullOrEmpty(content)) return content;
        using var document = new HtmlParser().ParseDocument(content);
        var text = new StringBuilder();
        void NewLine()
        {
            if (text.Length > 0 && text[^1] != '\n') text.Append('\n');
        }
        void Visit(INode node)
        {
            if (node is IText value) { text.Append(value.Data.Replace('\u00a0',' ')); return; }
            var tag = (node as IElement)?.LocalName;
            if (tag is "script" or "style" or "template" or "iframe" or "object" or "noscript") return;
            if (tag is "br" or "hr") { NewLine(); return; }
            var block = tag is "p" or "div" or "section" or "blockquote" or "li" or "ul" or "ol" or "tr"
                or "h1" or "h2" or "h3" or "h4" or "pre";
            if (block) NewLine();
            if (tag == "li") text.Append("• ");
            foreach (var child in node.ChildNodes) Visit(child);
            if (tag is "td" or "th") text.Append('\t');
            if (block) NewLine();
        }
        if (document.Body is { } body) Visit(body);
        return Regex.Replace(string.Join('\n',text.ToString().Replace("\r\n","\n").Replace('\r','\n')
            .Split('\n').Select(line=>line.TrimEnd())),"\n{3,}","\n\n").Trim();
    }
}
