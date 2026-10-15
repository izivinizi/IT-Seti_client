namespace ITSeti.Server;

internal static class MonitoringKinds
{
    public static readonly string[] All = ["events", "cpu", "memory", "temperature", "voltage", "disks", "identity", "other"];
    public const string Sql = """
        CASE WHEN a.category='event' THEN 'events'
        WHEN a.category='identity' THEN 'identity'
        WHEN a.identity ILIKE '%temperature%' OR a.title ILIKE '%температур%' OR a.detail ILIKE '%температур%' THEN 'temperature'
        WHEN a.identity ILIKE '%voltage%' OR a.identity ILIKE '%rail-%' OR a.title ILIKE '%напряж%' OR a.detail ILIKE '%напряж%' THEN 'voltage'
        WHEN a.identity ILIKE 'cpu:%' OR a.title ILIKE '%загрузка CPU%' OR a.title ILIKE '%загрузка процессора%' THEN 'cpu'
        WHEN a.identity ILIKE 'memory:%' OR a.title ILIKE '%оперативн%памят%' OR a.title ILIKE '%ОЗУ%' THEN 'memory'
        WHEN a.identity ILIKE 'disk:%' OR a.title ILIKE '%накопител%' OR a.title ILIKE '%диск%' OR a.detail ILIKE 'SMART%' THEN 'disks'
        ELSE 'other' END
        """;
}
