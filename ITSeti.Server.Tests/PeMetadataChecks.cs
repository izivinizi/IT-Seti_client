using ITSeti.Server;

internal static class PeMetadataChecks
{
    public static void Run(string installer)
    {
        var result=PackageVersionDetector.Detect(installer,"ITSeti-Maintenance-Setup.exe");
        if((result.Version?.Major!=2||result.Version.Minor!=4||result.Version.Build!=11||result.Version.Revision>0)||result.Name!="ИТ-Сети Обслуживание ПК")
            throw new Exception($"PE metadata mismatch: {result.Name}, {result.Version}");
        var renamed=PackageVersionDetector.Detect(installer,"unrelated-9.9.9.exe");
        if(renamed!=result)throw new Exception("Filename replaced embedded PE metadata.");
        var missing=PackageVersionDetector.Detect(installer+".missing","Fallback-1.2.3.exe");
        if(missing.Version!=new Version(1,2,3))throw new Exception("Filename fallback failed.");
        var invalid=Path.GetTempFileName();
        try
        {
            var truncatedPe = new byte[4096];
            using(var source=File.OpenRead(installer)) source.ReadExactly(truncatedPe);
            foreach(var bytes in new[]{Array.Empty<byte>(),new byte[]{77,90,0,0},new byte[1024],truncatedPe})
            {
                File.WriteAllBytes(invalid,bytes);
                if(PackageVersionDetector.Detect(invalid,"Broken-1.2.3.exe").Version!=new Version(1,2,3))
                    throw new Exception("Malformed installer broke safe filename fallback.");
            }
        }
        finally {File.Delete(invalid);}
        Console.WriteLine($"PASS {System.Runtime.InteropServices.RuntimeInformation.OSDescription}: canonical EXE -> {result.Name} {result.Version}; embedded metadata priority and malformed/missing fallbacks");
    }
}
