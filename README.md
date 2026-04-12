# Datadog IIS & ASP.NET Monitoring — Windows Service Guide

**Use case:** Collect IIS and ASP.NET performance metrics and send them to Datadog using a
lightweight .NET Windows Service. No changes to your application code are required.

This approach uses the official Datadog **DogStatsD-CSharp-Client** library to send metrics
over UDP to the local Datadog Agent.

> **When to use this guide**
> Use this approach when `infrastructure_mode: basic` is set in your `datadog.yaml`.
> This mode blocks native agent integration checks (like `windows_performance_counters`),
> but DogStatsD over UDP 8125 continues to work — making this the correct path for sending
> custom metrics.

---

## How it works

```
IIS / ASP.NET / w3wp
       |
       | Windows Performance Counters
       | (built-in, no app changes)
       v
 DatadogIISMetrics.exe          <- .NET 4.8 Windows Service (this guide)
 (reads counters every 15s)
       |
       | UDP packets to 127.0.0.1:8125
       v
 Datadog Agent (DogStatsD)
       |
       | HTTPS
       v
 Datadog Platform
```

The service reads counters from the operating system. IIS and ASP.NET publish these
counters automatically — there is nothing to configure in your application.

---

## Metrics collected

| Metric name | What it measures |
|---|---|
| `aspnet.requests.current` | Requests being processed right now |
| `aspnet.requests.queued` | Requests waiting in the queue |
| `aspnet.requests.rejected` | Requests rejected due to queue overflow |
| `aspnet.requests.in_queue` | Requests in the native kernel queue |
| `aspnet.request.execution_time` | Average request execution time (ms) |
| `aspnet.request.wait_time` | Average request wait time in queue (ms) |
| `iis.connections.current` | Active HTTP connections (all sites) |
| `iis.requests.get` | Total GET requests served (cumulative) |
| `iis.requests.post` | Total POST requests served (cumulative) |
| `w3wp.cpu_pct` | IIS worker process CPU usage (%) |
| `w3wp.memory_bytes` | IIS worker process memory (bytes) |

---

## Prerequisites

| Requirement | Notes |
|---|---|
| Windows Server 2016 / 2019 / 2022 | |
| IIS 8.5+ with ASP.NET 4.x | Application pool must be running |
| Datadog Agent 7 installed | With `infrastructure_mode: basic` in `datadog.yaml` |
| PowerShell 5.1+ | Run all commands as **Administrator** |
| Outbound HTTPS or pre-downloaded DLL | To fetch the NuGet package in Step 3 |

---

## Step 1 — Confirm Datadog Agent DogStatsD is listening

Open PowerShell **as Administrator** and run:

```powershell
netstat -an | findstr ":8125"
```

You must see:

```
UDP    127.0.0.1:8125    *:*
```

If this line is missing, check `C:\ProgramData\Datadog\datadog.yaml`. DogStatsD is
enabled by default; if it was explicitly disabled, add `use_dogstatsd: true` and restart
the Datadog Agent service.

---

## Step 2 — Create the working directory

```powershell
New-Item -ItemType Directory -Path "C:\DatadogMetrics" -Force
```

All files for this solution live in `C:\DatadogMetrics\`.

---

## Step 3 — Download the StatsdClient DLL

The Datadog DogStatsD-CSharp-Client NuGet package contains the DLL needed to send
metrics. A `.nupkg` file is a zip archive, so you can extract it without NuGet tooling.

```powershell
# Download the NuGet package
$url  = "https://www.nuget.org/api/v2/package/DogStatsD-CSharp-Client/9.0.0"
$pkg  = "C:\DatadogMetrics\dogstatsd.nupkg"
Invoke-WebRequest -Uri $url -OutFile $pkg

# Extract the .NET 4.6.1 compatible DLL (works with .NET 4.8)
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip   = [System.IO.Compression.ZipFile]::OpenRead($pkg)
$entry = $zip.Entries | Where-Object { $_.FullName -like "*/net461/StatsdClient.dll" } |
         Select-Object -First 1
[System.IO.Compression.ZipFileExtensions]::ExtractToFile(
    $entry, "C:\DatadogMetrics\StatsdClient.dll", $true)
$zip.Dispose()

# Confirm the file is present
Write-Host "DLL size: $((Get-Item 'C:\DatadogMetrics\StatsdClient.dll').Length) bytes"
# Expected: 67072 bytes
```

> **Air-gapped / offline servers**
> Download the package on a machine with internet access, extract `lib/net461/StatsdClient.dll`
> from the `.nupkg` (rename it to `.zip` if needed), and copy the DLL to
> `C:\DatadogMetrics\StatsdClient.dll` on the target server.

---

## Step 4 — Create the C# source file

Run the block below in PowerShell. Before running, **edit the `Tags` array** on the lines
marked `CUSTOMIZE` to match your environment.

```powershell
$src = @'
using System;
using System.Diagnostics;
using System.ServiceProcess;
using System.Threading;
using StatsdClient;

public class IISMetricsService : ServiceBase
{
    private Thread         _thread;
    private volatile bool  _running;
    private DogStatsdService _statsd;

    // ── CUSTOMIZE ───────────────────────────────────────────────────
    // Replace these tag values with your own environment and app name.
    private static readonly string[] Tags = {
        "env:production",   // CUSTOMIZE
        "app:my-app",       // CUSTOMIZE
        "tech:aspnet",
        "tech:iis"
    };
    // ────────────────────────────────────────────────────────────────

    private const string SVC_NAME  = "DatadogIISMetrics";
    private const string EVT_SRC   = "DatadogIISMetrics";
    private const int    INTERVAL  = 15000; // milliseconds between collections

    public IISMetricsService() { ServiceName = SVC_NAME; }

    static void Main() { ServiceBase.Run(new IISMetricsService()); }

    // ── Service lifecycle ────────────────────────────────────────────

    protected override void OnStart(string[] args)
    {
        EnsureEventSource();
        _statsd = new DogStatsdService();
        _statsd.Configure(new StatsdConfig {
            StatsdServerName = "127.0.0.1",
            StatsdPort       = 8125
        });
        _running = true;
        _thread  = new Thread(RunLoop) { IsBackground = true };
        _thread.Start();
        EventLog.WriteEntry(EVT_SRC, "DatadogIISMetrics started.", EventLogEntryType.Information, 1);
    }

    protected override void OnStop()
    {
        _running = false;
        if (_statsd != null) { _statsd.Dispose(); }
    }

    // ── Collection loop ──────────────────────────────────────────────

    private void RunLoop()
    {
        while (_running)
        {
            try   { Collect(); }
            catch (Exception ex)
            {
                EventLog.WriteEntry(EVT_SRC, "Collect() error: " + ex.Message,
                    EventLogEntryType.Error, 10);
            }
            Thread.Sleep(INTERVAL);
        }
    }

    private void Collect()
    {
        // ASP.NET v4 application counters
        Gauge("aspnet.requests.current",       "ASP.NET v4.0.30319", "Requests Current",        null);
        Gauge("aspnet.requests.queued",        "ASP.NET v4.0.30319", "Requests Queued",          null);
        Gauge("aspnet.requests.rejected",      "ASP.NET v4.0.30319", "Requests Rejected",        null);
        Gauge("aspnet.request.execution_time", "ASP.NET v4.0.30319", "Request Execution Time",   null);
        Gauge("aspnet.request.wait_time",      "ASP.NET v4.0.30319", "Request Wait Time",        null);
        Gauge("aspnet.requests.in_queue",      "ASP.NET v4.0.30319", "Requests In Native Queue", null);

        // IIS Web Service counters (all sites combined via _Total)
        Gauge("iis.connections.current", "Web Service", "Current Connections", "_Total");
        Gauge("iis.requests.get",        "Web Service", "Total Get Requests",  "_Total");
        Gauge("iis.requests.post",       "Web Service", "Total Post Requests", "_Total");

        // w3wp worker process counters (all IIS worker processes summed)
        GaugeW3wp("w3wp.cpu_pct",      "Process", "% Processor Time");
        GaugeW3wp("w3wp.memory_bytes", "Process", "Working Set");
    }

    // ── Helpers ──────────────────────────────────────────────────────

    // Read a single Performance Counter instance and send as a Gauge
    private void Gauge(string metric, string category, string counter, string instance)
    {
        try
        {
            PerformanceCounter pc = (instance != null)
                ? new PerformanceCounter(category, counter, instance, true)
                : new PerformanceCounter(category, counter, true);
            _statsd.Gauge(metric, pc.NextValue(), tags: Tags);
        }
        catch (Exception ex)
        {
            EventLog.WriteEntry(EVT_SRC, "Gauge failed [" + metric + "]: " + ex.Message,
                EventLogEntryType.Warning, 20);
        }
    }

    // Sum all w3wp process instances (handles w3wp, w3wp#1, w3wp#2 ...)
    private void GaugeW3wp(string metric, string category, string counter)
    {
        try
        {
            double sum   = 0;
            bool   found = false;
            for (int i = 0; i < 8; i++)
            {
                string inst = (i == 0) ? "w3wp" : "w3wp#" + i;
                try
                {
                    if (!PerformanceCounterCategory.InstanceExists(inst, category)) break;
                    sum  += new PerformanceCounter(category, counter, inst, true).NextValue();
                    found = true;
                }
                catch (Exception) { break; }
            }
            if (found)
                _statsd.Gauge(metric, sum, tags: Tags);
        }
        catch (Exception ex)
        {
            EventLog.WriteEntry(EVT_SRC, "GaugeW3wp failed [" + metric + "]: " + ex.Message,
                EventLogEntryType.Error, 40);
        }
    }

    private void EnsureEventSource()
    {
        try
        {
            if (!EventLog.SourceExists(EVT_SRC))
                EventLog.CreateEventSource(EVT_SRC, "Application");
        }
        catch (Exception) { }
    }
}
'@

[System.IO.File]::WriteAllText("C:\DatadogMetrics\IISMetricsService.cs", $src)
Write-Host "Source file written to C:\DatadogMetrics\IISMetricsService.cs"
```

---

## Step 5 — Compile the service

Windows Server ships with the .NET Framework C# compiler (`csc.exe`). No Visual Studio or
SDK is required.

```powershell
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

& $csc /target:exe `
       /out:C:\DatadogMetrics\DatadogIISMetrics.exe `
       /reference:C:\DatadogMetrics\StatsdClient.dll `
       /reference:C:\Windows\Microsoft.NET\Framework64\v4.0.30319\System.ServiceProcess.dll `
       /reference:C:\Windows\Microsoft.NET\Framework64\v4.0.30319\System.dll `
       C:\DatadogMetrics\IISMetricsService.cs

# Verify the binary was created
Write-Host "EXE size: $((Get-Item 'C:\DatadogMetrics\DatadogIISMetrics.exe').Length) bytes"
# Expected: ~8192 bytes
```

A successful compile shows only the compiler version banner — no errors or warnings.
If you see red error lines, refer to the [Troubleshooting](#troubleshooting) section.

---

## Step 6 — Install the Windows Service

```powershell
# Register the service
sc.exe create DatadogIISMetrics `
    binPath= "C:\DatadogMetrics\DatadogIISMetrics.exe" `
    start=   auto `
    obj=     LocalSystem `
    DisplayName= "Datadog IIS Metrics"

# Add a description
sc.exe description DatadogIISMetrics `
    "Collects IIS and ASP.NET Performance Counter metrics and sends them to Datadog via DogStatsD"

# Start the service
Start-Service DatadogIISMetrics

# Confirm it is running
Get-Service DatadogIISMetrics | Select-Object Name, Status
```

Expected output:

```
Name               Status
----               ------
DatadogIISMetrics Running
```

The service is now set to **auto-start** — it will restart automatically after a reboot
or if the server is restarted.

---

## Step 7 — Confirm the service started successfully

Check the Windows Application Event Log for the startup message written by the service:

```powershell
Get-EventLog -LogName Application -Source DatadogIISMetrics -Newest 5 |
    Select-Object TimeGenerated, EntryType, Message |
    Format-List
```

Expected:

```
TimeGenerated : 4/12/2026 11:53:43 AM
EntryType     : Information
Message       : DatadogIISMetrics started.
```

If you see **Warning** or **Error** entries instead, the counter name or instance may not
match your server configuration. Refer to the [Troubleshooting](#troubleshooting) section.

---

## Step 8 — Validate metrics in Datadog

Open **Metrics Explorer** in Datadog (`app.datadoghq.com/metric/explorer`) and search for
each metric. All 11 should appear within **30–60 seconds** of the service starting.

**Quick validation query** — paste into the Metrics Explorer search:

```
avg:aspnet.requests.current{*}
avg:iis.requests.get{*}
avg:w3wp.memory_bytes{*}
```

You can also use the **Datadog CLI MCP** or run the check directly on the server:

```powershell
# Send a one-off test metric via UDP to confirm DogStatsD is reachable
$udp = New-Object System.Net.Sockets.UdpClient
$udp.Connect("127.0.0.1", 8125)
$b = [System.Text.Encoding]::UTF8.GetBytes("iis.healthcheck:1|g|#env:production")
$udp.Send($b, $b.Length) | Out-Null
$udp.Close()
Write-Host "Test metric sent. Look for 'iis.healthcheck' in Datadog Metrics Explorer."
```

---

## Day-2 operations

### Stop / Start / Restart

```powershell
Stop-Service  DatadogIISMetrics -Force
Start-Service DatadogIISMetrics
Restart-Service DatadogIISMetrics
```

### View logs

```powershell
Get-EventLog -LogName Application -Source DatadogIISMetrics -Newest 20 | Format-List
```

### Update tags or add metrics (recompile)

```powershell
# 1. Stop the service
Stop-Service DatadogIISMetrics -Force

# 2. Edit the source file
#    notepad C:\DatadogMetrics\IISMetricsService.cs

# 3. Recompile
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
& $csc /target:exe `
       /out:C:\DatadogMetrics\DatadogIISMetrics.exe `
       /reference:C:\DatadogMetrics\StatsdClient.dll `
       /reference:C:\Windows\Microsoft.NET\Framework64\v4.0.30319\System.ServiceProcess.dll `
       /reference:C:\Windows\Microsoft.NET\Framework64\v4.0.30319\System.dll `
       C:\DatadogMetrics\IISMetricsService.cs

# 4. Start the service
Start-Service DatadogIISMetrics
```

### Uninstall

```powershell
Stop-Service DatadogIISMetrics -Force
sc.exe delete DatadogIISMetrics
Remove-Item "C:\DatadogMetrics" -Recurse -Force
```

---

## Troubleshooting

### No metrics appear in Datadog after 2 minutes

**1. Confirm DogStatsD port is open**
```powershell
netstat -an | findstr ":8125"
```
Must show `UDP 127.0.0.1:8125 *:*`. If not, restart the Datadog Agent:
```powershell
Restart-Service datadogagent
```

**2. Confirm the service is running**
```powershell
Get-Service DatadogIISMetrics
```

**3. Send a test metric manually and look for it in Datadog**
```powershell
$udp = New-Object System.Net.Sockets.UdpClient
$udp.Connect("127.0.0.1", 8125)
$b = [System.Text.Encoding]::UTF8.GetBytes("iis.test:42|g|#env:production")
$udp.Send($b, $b.Length) | Out-Null
$udp.Close()
```
Search for `iis.test` in Metrics Explorer. If it appears, the pipeline works and the issue
is specific to how the counters are being read.

---

### `w3wp.cpu_pct` and `w3wp.memory_bytes` show no data

The IIS application pool shuts down the `w3wp.exe` worker process after **20 minutes of
no traffic** (IIS idle timeout default). When w3wp is not running, there are no Performance
Counter instances to read, so these two metrics produce no data.

**Solution:** send a few HTTP requests to the site to restart the worker process:
```powershell
1..10 | ForEach-Object { Invoke-WebRequest http://localhost/ -UseBasicParsing | Out-Null }
```
The metrics will resume within one collection cycle (15 seconds).

To prevent this in production, disable the idle timeout in IIS Manager:
`Application Pools → your pool → Advanced Settings → Idle Time-out (minutes) → set to 0`

---

### Compile errors

| Error message | Cause | Fix |
|---|---|---|
| `error CS1525: Invalid expression term '.'` | Using `?.` null-conditional (C# 6+) | Replace `x?.Method()` with `if (x != null) { x.Method(); }` |
| `error CS0234: The type or namespace name ... does not exist` | Missing `/reference:` flag | Add the missing reference to the compile command |
| `The system cannot find the path specified` | Wrong `csc.exe` path | Verify: `Test-Path "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"` |
| `error CS0006: Metadata file 'StatsdClient.dll' could not be found` | DLL missing | Re-run Step 3 to extract the DLL |

> **Important:** `csc.exe` from `Framework64\v4.0.30319` supports **C# 5 only**.
> Do not use string interpolation (`$"..."`), null-conditional (`?.`), or `nameof()`.

---

### Event Log shows Warning or Error entries

```powershell
Get-EventLog -LogName Application -Source DatadogIISMetrics -Newest 20 | Format-List
```

| EventID | Type | Meaning |
|---|---|---|
| 1 | Information | Service started successfully |
| 10 | Error | Unhandled exception in `Collect()` — see Message for details |
| 20 | Warning | A single counter could not be read (others still work) |
| 40 | Error | `GaugeW3wp` failed entirely — likely a permissions issue |

If EventID 20 warns about a specific counter, the Performance Counter name may differ on
your server. Open **Performance Monitor** (`perfmon.exe`) and browse
`Monitoring Tools → Performance Monitor → Add Counters` to find the exact category and
counter name for your environment.

---

### Service exists but won't start (Error 1053)

The service timed out during startup. Check:
1. `C:\DatadogMetrics\DatadogIISMetrics.exe` exists
2. `C:\DatadogMetrics\StatsdClient.dll` exists in the **same directory** as the EXE
3. The EXE was compiled against the `StatsdClient.dll` that is currently present

---

## Files reference

| File | Purpose |
|---|---|
| `C:\DatadogMetrics\IISMetricsService.cs` | C# source — edit to customize tags or add metrics |
| `C:\DatadogMetrics\StatsdClient.dll` | DogStatsD-CSharp-Client v9.0.0 (net461) |
| `C:\DatadogMetrics\DatadogIISMetrics.exe` | Compiled Windows Service binary |
| `C:\ProgramData\Datadog\datadog.yaml` | Datadog Agent configuration (not modified by this guide) |

---

## Quick-reference: all commands in order

```powershell
# 1. Confirm DogStatsD is listening
netstat -an | findstr ":8125"

# 2. Create directory
New-Item -ItemType Directory -Path "C:\DatadogMetrics" -Force

# 3. Download StatsdClient.dll
$url = "https://www.nuget.org/api/v2/package/DogStatsD-CSharp-Client/9.0.0"
Invoke-WebRequest -Uri $url -OutFile "C:\DatadogMetrics\dogstatsd.nupkg"
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::OpenRead("C:\DatadogMetrics\dogstatsd.nupkg")
$entry = $zip.Entries | Where-Object { $_.FullName -like "*/net461/StatsdClient.dll" } | Select-Object -First 1
[System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, "C:\DatadogMetrics\StatsdClient.dll", $true)
$zip.Dispose()

# 4. Write source file  →  (run the $src block from Step 4)

# 5. Compile
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
& $csc /target:exe /out:C:\DatadogMetrics\DatadogIISMetrics.exe `
    /reference:C:\DatadogMetrics\StatsdClient.dll `
    /reference:C:\Windows\Microsoft.NET\Framework64\v4.0.30319\System.ServiceProcess.dll `
    /reference:C:\Windows\Microsoft.NET\Framework64\v4.0.30319\System.dll `
    C:\DatadogMetrics\IISMetricsService.cs

# 6. Install and start
sc.exe create DatadogIISMetrics binPath= "C:\DatadogMetrics\DatadogIISMetrics.exe" start= auto obj= LocalSystem DisplayName= "Datadog IIS Metrics"
Start-Service DatadogIISMetrics

# 7. Verify
Get-Service DatadogIISMetrics
Get-EventLog -LogName Application -Source DatadogIISMetrics -Newest 3 | Format-List
```
