using System.Text.Json;

namespace ITSeti.Server;

public sealed record ProcessPolicyDocument(string[] AllowedNames, string[] AllowedPublishers);

public sealed class ProcessPolicy
{
    private readonly string path;
    private readonly SemaphoreSlim gate = new(1, 1);
    private ProcessPolicyDocument current;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public ProcessPolicy(IConfiguration configuration, ILogger<ProcessPolicy>? logger = null)
    {
        path = Path.Combine(configuration["DataRoot"] ?? "/data", "process-policy.json");
        var defaults = Path.Combine(AppContext.BaseDirectory, "policy-defaults");
        using var names = JsonDocument.Parse(File.ReadAllText(Path.Combine(defaults, "allowed-processes.json")));
        current = new ProcessPolicyDocument(JsonRead.StringList(names.RootElement, "allowedNames").ToArray(),
            File.ReadAllLines(Path.Combine(defaults, "allowed-publishers.txt")).Select(x => x.Trim()).Where(x => x.Length > 0 && !x.StartsWith('#')).ToArray());
        if (File.Exists(path))
        {
            try
            {
                if (new FileInfo(path).Length > 2 * 1024 * 1024) throw new InvalidDataException("Process policy exceeds 2 MB.");
                current = Normalize(JsonSerializer.Deserialize<ProcessPolicyDocument>(File.ReadAllText(path), JsonOptions)
                    ?? throw new InvalidDataException("Invalid process policy."));
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException or IOException or UnauthorizedAccessException)
            {
                logger?.LogWarning(ex, "Cannot read saved process policy; bundled policy remains active. Saved file was not changed.");
            }
        }
    }

    public ProcessPolicyDocument Read() => new(current.AllowedNames.ToArray(), current.AllowedPublishers.ToArray());

    public async Task WriteAsync(ProcessPolicyDocument value)
    {
        value = Normalize(value);
        await gate.WaitAsync();
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(value, JsonOptions));
            File.Move(temporary, path, true);
            current = value;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); gate.Release(); }
    }

    public bool IsAllowed(JsonElement process)
    {
        var policy = current;
        var name = NormalizeName(JsonRead.String(process, "Name", "ProcessName") ?? "");
        if (policy.AllowedNames.Any(x => NormalizeName(x).Equals(name, StringComparison.OrdinalIgnoreCase))) return true;
        if (!string.Equals(JsonRead.String(process, "Signature"), "Valid", StringComparison.OrdinalIgnoreCase)) return false;
        var signer = JsonRead.String(process, "Signer", "Publisher") ?? "";
        static string NormalizePublisher(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
        var normalized = NormalizePublisher(signer);
        return policy.AllowedPublishers.Any(x => NormalizePublisher(x) is { Length: > 0 } entry &&
            (x.EndsWith('*') ? normalized.StartsWith(entry, StringComparison.Ordinal) : normalized == entry));
    }

    private static string NormalizeName(string name) => name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    private static ProcessPolicyDocument Normalize(ProcessPolicyDocument value)
    {
        static string[] Clean(string[]? entries)
        {
            if (entries is null || entries.Length > 5000) throw new ArgumentException("Список должен содержать не более 5000 строк.");
            if (entries.Any(x => x is null || x.Length > 200 || x.Any(char.IsControl))) throw new ArgumentException("Недопустимая строка в списке.");
            return entries.Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        return new(Clean(value.AllowedNames), Clean(value.AllowedPublishers));
    }
}
