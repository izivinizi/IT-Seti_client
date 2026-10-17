using ITSeti.Server;
using System.Text.Json;

internal static class TicketMergeChecks
{
    public static void Run()
    {
        const string merged="""<span class="in-comment-issue">Комментарий добавлен при объединении с заявкой <i>№24821</i> от <i>14.10.2026</i></span><br><b>Тема заявки</b>: установка серверной<br><br><b>Описание заявки</b>: диски &amp; почта""";
        var text=OkdeskCommentText.ForDisplay(merged,true);
        if(text.Contains('<')||text.Contains('>')||!text.Contains("№24821")||!text.Contains("14.10.2026")||
            !text.Contains("\nТема заявки: установка серверной\nОписание заявки: диски & почта"))
            throw new Exception("Merged comment text lost origin, line breaks or HTML entity decoding.");
        if(OkdeskCommentText.ForDisplay("<p>2 &lt; 3&nbsp;и 5 &gt; 4</p><script>alert(1)</script>",true)!="2 < 3 и 5 > 4")
            throw new Exception("Rich text conversion retained executable content or damaged comparisons.");
        if(OkdeskCommentText.ForDisplay("<span>Это текст пользователя</span>",false)!="<span>Это текст пользователя</span>")
            throw new Exception("Local plain text was interpreted as remote HTML.");
        if(OkdeskCommentText.ForDisplay("<p>Пункт</p><ul><li>Первый</li><li>Второй</li></ul>",true)!="Пункт\n• Первый\n• Второй")
            throw new Exception("Comment paragraphs or lists were flattened.");
        using var record=JsonDocument.Parse("""{"type":"AddCommentEvent","state":"published","isPublic":true,"commentId":42,"content":"ok","createdAt":"2026-10-14T10:15:00+03:00","author":{"type":"Employee","id":10}}""");
        if(OkdeskWebClient.ReadPublicComment(record.RootElement).CreatedAt!=DateTimeOffset.Parse("2026-10-14T07:15:00Z"))
            throw new Exception("Remote comment timestamp was lost or its offset changed.");
        Console.WriteLine("PASS merged ticket comments: safe plain text, source reference, line breaks, entities, local literal text and original timestamps");
    }
}
