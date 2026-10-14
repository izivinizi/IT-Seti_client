param([string]$Root = (Split-Path $PSScriptRoot -Parent))
$ErrorActionPreference = 'Stop'
$binaryRoot = Join-Path $Root 'src\ITSeti.Maintenance.Win7.Port\bin\Release\net48'
Add-Type -AssemblyName System.ServiceProcess
[void][Reflection.Assembly]::LoadFrom((Join-Path $binaryRoot 'ITSeti.Maintenance.Core.dll'))
[void][Reflection.Assembly]::LoadFrom((Join-Path $binaryRoot 'ITSeti.Maintenance.Win7.Port.exe'))
Add-Type -ReferencedAssemblies System.ServiceProcess -TypeDefinition @'
using System;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

public static class Win7PipeLifecycleFixture
{
    public static void Run(string assemblyPath)
    {
        var assembly = Assembly.LoadFrom(assemblyPath);
        var type = assembly.GetType("ITSeti.Maintenance.App.Win7MaintenanceServiceHost+Win7MaintenanceService", true);
        var pipeName = "ITSeti-test-" + Guid.NewGuid().ToString("N");
        var flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        using (var service = (IDisposable)Activator.CreateInstance(type, flags, null, new object[] { pipeName }, null))
        using (var stop = new CancellationTokenSource())
        {
            var loop = (Task)type.GetMethod("PipeLoop", flags).Invoke(service, new object[] { stop.Token });
            try
            {
                for (var i = 0; i < 100; i++)
                {
                    using (var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
                    {
                        client.Connect(3000);
                        using (var writer = new StreamWriter(client, new UTF8Encoding(false), 4096, true) { AutoFlush = true })
                        using (var reader = new StreamReader(client, Encoding.UTF8, true, 4096, true))
                        {
                            writer.WriteLine("{\"command\":\"ping\"}");
                            var response = reader.ReadLineAsync();
                            if (!response.Wait(3000) || response.Result == null || !response.Result.Contains("\"success\":true"))
                                throw new Exception("Win7 pipe did not answer ping " + i);
                        }
                    }
                }
            }
            finally
            {
                stop.Cancel();
                if (!loop.Wait(3000)) throw new Exception("Win7 idle pipe did not stop after cancellation.");
            }
        }
    }
}
'@
[Win7PipeLifecycleFixture]::Run((Join-Path $binaryRoot 'ITSeti.Maintenance.Win7.Port.exe'))
'PASS Win7 net48: 100 isolated IPC connections and cancellation of idle pipe; installed service untouched'
