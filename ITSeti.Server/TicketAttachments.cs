namespace ITSeti.Server;

public sealed record TicketAttachmentInput(string FileName, string ContentBase64);
public sealed record TicketAttachment(Guid Id, string FileName, string ContentType, byte[] Bytes);

public static class TicketAttachments
{
    private static readonly IReadOnlyDictionary<string, string> Types = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg",
        [".pdf"] = "application/pdf", [".txt"] = "text/plain", [".log"] = "text/plain",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
    };

    public static IReadOnlyList<TicketAttachment> Decode(IReadOnlyList<TicketAttachmentInput>? inputs)
    {
        if (inputs is null) return [];
        if (inputs.Count > 3) throw new ArgumentException("Слишком много вложений.");
        var files = new List<TicketAttachment>(inputs.Count);
        long total = 0;
        foreach (var input in inputs)
        {
            if (input is null || string.IsNullOrEmpty(input.FileName) || string.IsNullOrEmpty(input.ContentBase64))
                throw new ArgumentException("Недопустимое вложение.");
            var name = Path.GetFileName(input.FileName);
            if (name != input.FileName || name.Length is < 1 or > 120 ||
                name.Any(c => char.IsControl(c) || c is '/' or '\\' or '"') ||
                !Types.TryGetValue(Path.GetExtension(name), out var contentType) ||
                input.ContentBase64.Length > 7_000_000) throw new ArgumentException("Недопустимое вложение.");
            byte[] bytes;
            try { bytes = Convert.FromBase64String(input.ContentBase64); }
            catch (FormatException) { throw new ArgumentException("Неверный формат вложения."); }
            total += bytes.Length;
            if (bytes.Length is < 1 or > 5 * 1024 * 1024 || total > 10 * 1024 * 1024)
                throw new ArgumentException("Превышен размер вложений.");
            files.Add(new(Guid.NewGuid(), name, contentType, bytes));
        }
        return files;
    }
}
