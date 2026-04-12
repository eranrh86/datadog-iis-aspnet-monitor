# Datadog IIS & ASP.NET Monitoring — Windows Service

Send IIS and ASP.NET performance metrics to Datadog from a Windows Server.
**No changes to your application code are required.**

---

## What this does

It installs a small background service on your Windows Server that reads IIS/ASP.NET performance data every 15 seconds and sends it to Datadog automatically.

```
IIS / ASP.NET / w3wp (your app)
         |
         | Windows reads built-in performance counters
         v
 DatadogIISMetrics.exe          ← the service you install in this guide
 (runs every 15 seconds)
         |
         | UDP → 127.0.0.1:8125
         v
 Datadog Agent (already installed on your server)
         |
         | HTTPS
         v
 Datadog dashboard
```

---

## Metrics you will get

| Metric | What it means |
|---|---|
| `aspnet.requests.current` | Requests being processed right now |
| `aspnet.requests.queued` | Requests waiting in line |
| `aspnet.requests.rejected` | Requests dropped because the queue was full |
| `aspnet.requests.in_queue` | Requests in the Windows kernel queue |
| `aspnet.request.execution_time` | How long requests take to execute (ms) |
| `aspnet.request.wait_time` | How long requests wait before starting (ms) |
| `iis.connections.current` | Active HTTP connections right now |
| `iis.requests.get` | Total GET requests served so far |
| `iis.requests.post` | Total POST requests served so far |
| `w3wp.cpu_pct` | CPU % used by the IIS worker process |
| `w3wp.memory_bytes` | Memory used by the IIS worker process |

---

## Before you start — checklist

- [ ] Windows Server 2016 / 2019 / 2022
- [ ] IIS installed and running with at least one app pool
- [ ] Datadog Agent 7 installed on the server
- [ ] You can open PowerShell **as Administrator**
- [ ] The server has internet access (or see the offline option in Step 3)

> **Using `infrastructure_mode: basic`?**
> This mode blocks most Datadog integrations, but DogStatsD (UDP port 8125) still works.
> This guide uses DogStatsD — so it works perfectly in that mode.

---

## Step 1 — Open PowerShell as Administrator

Click **Start**, type `PowerShell`, right-click **Windows PowerShell**, choose **Run as administrator**.

All commands in this guide must be run in that Administrator PowerShell window.

---

## Step 2 — Confirm Datadog is ready to receive metrics

Run this:

```powershell
netstat -an | findstr ":8125"
```

You must see this line in the output:

```
UDP    127.0.0.1:8125    *:*
```

**If you see nothing:** The Datadog Agent DogStatsD listener is not running. Restart it:

```powershell
Restart-Service datadogagent
```

Then run the `netstat` command again.

---

## Step 3 — Create a folder for the files

```powershell
New-Item -ItemType Directory -Path "C:\DatadogMetrics" -Force
```

This creates `C:\DatadogMetrics\`. All files for this solution will live there.

---

## Step 4 — Download the Datadog DLL

The service needs one library file (`StatsdClient.dll`) to talk to Datadog. This step downloads it. The file comes from the official Datadog NuGet package.

**Copy and paste the entire block below into PowerShell:**

```powershell
# Download the package (it's a zip file with a .nupkg extension)
$url = "https://www.nuget.org/api/v2/package/DogStatsD-CSharp-Client/9.0.0"
$pkg = "C:\DatadogMetrics\dogstatsd.nupkg"
Invoke-WebRequest -Uri $url -OutFile $pkg

# Open the zip and extract just the DLL we need
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip   = [System.IO.Compression.ZipFile]::OpenRead($pkg)
$entry = $zip.Entries | Where-Object { $_.FullName -like "*/net461/StatsdClient.dll" } |
         Select-Object -First 1
[System.IO.Compression.ZipFileExtensions]::ExtractToFile(
    $entry, "C:\DatadogMetrics\StatsdClient.dll", $true)
$zip.Dispose()

# Confirm it worked
Write-Host "DLL size: $((Get-Item 'C:\DatadogMetrics\StatsdClient.dll').Length) bytes"
```

**Expected output:**
```
DLL size: 67072 bytes
```

If you see `67072 bytes` — you're good. If you see an error, see the troubleshooting section.

---

### What does that script actually do? (plain English)

| Line | What it does |
|---|---|
| `Invoke-WebRequest` | Downloads the package from nuget.org and saves it as a `.nupkg` file. A `.nupkg` file is just a regular zip file with a different name. |
| `Add-Type` | Loads PowerShell's built-in zip library so it can open the file. |
| `ZipFile::OpenRead` | Opens the downloaded zip. |
| `Where-Object ... net461` | The zip contains DLLs for many .NET versions. This line finds the right one — `net461` — which works with .NET 4.8. |
| `ExtractToFile` | Copies just that one DLL out of the zip and saves it to `C:\DatadogMetrics\StatsdClient.dll`. |
| `$zip.Dispose()` | Closes the zip file. |
| `Write-Host` | Prints the file size so you can confirm it downloaded correctly. |

**After this step you should have:**
```
C:\DatadogMetrics\
    dogstatsd.nupkg        ← the downloaded zip (you can delete this later)
    StatsdClient.dll       ← the file you actually need (67072 bytes)
```

---

### No internet access on the server?

Do this on any machine that has internet access:

1. Open your browser and go to:
   `https://www.nuget.org/packages/DogStatsD-CSharp-Client/9.0.0`
2. Click **Download package** — you get a `.nupkg` file
3. Rename it to `.zip`
4. Open it with Windows Explorer or 7-Zip
5. Navigate inside to `lib\net461\`
6. Copy `StatsdClient.dll`
7. Paste it on the server at `C:\DatadogMetrics\StatsdClient.dll`

You are done — skip the PowerShell script above entirely.

---

## Step 5 — Create the C# source file

This writes the service source code to disk. Before running, find the two lines marked `CUSTOMIZE` and change them to match your environment.

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
    private static readonly string[] Tags = {
        "env:production",   // CUSTOMIZE: change to your environment (e.g. env:staging)
        "app:my-app",       // CUSTOMIZE: change to your application name
        "tech:aspnet",
        "tech:iis"
    };
    // ────────────────────────────────────────────────────────────────

    private const string SVC_NAME = "DatadogIISMetrics";
    private const string EVT_SRC  = "DatadogIISMetrics";
    private const int    INTERVAL = 15000; // collect every 15 seconds

    public IISMetricsService() { ServiceName = SVC_NAME; }

    static void Main() { ServiceBase.Run(new IISMetricsService()); }

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
        Gauge("aspnet.requests.current",       "ASP.NET v4.0.30319", "Requests Current",        null);
        Gauge("aspnet.requests.queued",        "ASP.NET v4.0.30319", "Requests Queued",          null);
        Gauge("aspnet.requests.rejected",      "ASP.NET v4.0.30319", "Requests Rejected",        null);
        Gauge("aspnet.request.execution_time", "ASP.NET v4.0.30319", "Request Execution Time",   null);
        Gauge("aspnet.request.wait_time",      "ASP.NET v4.0.30319", "Request Wait Time",        null);
        Gauge("aspnet.requests.in_queue",      "ASP.NET v4.0.30319", "Requests In Native Queue", null);
        Gauge("iis.connections.current", "Web Service", "Current Connections", "_Total");
        Gauge("iis.requests.get",        "Web Service", "Total Get Requests",  "_Total");
        Gauge("iis.requests.post",       "Web Service", "Total Post Requests", "_Total");
        GaugeW3wp("w3wp.cpu_pct",      "Process", "% Processor Time");
        GaugeW3wp("w3wp.memory_bytes", "Process", "Working Set");
    }

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
Write-Host "Source file written."
```

---

## Step 6 — Compile the service

Windows Server already has the C# compiler built in. You do not need Visual Studio.

```powershell
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

& $csc /target:exe `
       /out:C:\DatadogMetrics\DatadogIISMetrics.exe `
       /reference:C:\DatadogMetrics\StatsdClient.dll `
       /reference:C:\Windows\Microsoft.NET\Framework64\v4.0.30319\System.ServiceProcess.dll `
       /reference:C:\Windows\Microsoft.NET\Framework64\v4.0.30319\System.dll `
       C:\DatadogMetrics\IISMetricsService.cs

Write-Host "EXE size: $((Get-Item 'C:\DatadogMetrics\DatadogIISMetrics.exe').Length) bytes"
```

**Expected output:** Only the compiler version banner (no red errors). The EXE file should be around 8192 bytes.

If you see red error lines, see the Troubleshooting section.

---

## Step 7 — Install and start the Windows Service

```powershell
# Register the service
sc.exe create DatadogIISMetrics `
    binPath= "C:\DatadogMetrics\DatadogIISMetrics.exe" `
    start=   auto `
    obj=     LocalSystem `
    DisplayName= "Datadog IIS Metrics"

sc.exe description DatadogIISMetrics `
    "Collects IIS and ASP.NET metrics and sends them to Datadog via DogStatsD"

# Start it
Start-Service DatadogIISMetrics

# Confirm it is running
Get-Service DatadogIISMetrics | Select-Object Name, Status
```

**Expected output:**
```
Name               Status
----               ------
DatadogIISMetrics Running
```

The service is set to **auto-start** — it will start automatically after every server reboot.

---

## Step 8 — Confirm the service started

```powershell
Get-EventLog -LogName Application -Source DatadogIISMetrics -Newest 5 |
    Select-Object TimeGenerated, EntryType, Message |
    Format-List
```

**Expected output:**
```
TimeGenerated : 4/12/2026 11:53:43 AM
EntryType     : Information
Message       : DatadogIISMetrics started.
```

If you see Warning or Error entries, see the Troubleshooting section.

---

## Step 9 — Verify metrics appear in Datadog

1. Go to `app.datadoghq.com/metric/explorer`
2. Search for `aspnet.requests.current`
3. All 11 metrics should appear within **30–60 seconds**

You can also send a quick test metric manually to confirm the pipeline works end to end:

```powershell
$udp = New-Object System.Net.Sockets.UdpClient
$udp.Connect("127.0.0.1", 8125)
$b = [System.Text.Encoding]::UTF8.GetBytes("iis.healthcheck:1|g|#env:production")
$udp.Send($b, $b.Length) | Out-Null
$udp.Close()
Write-Host "Test metric sent — search for iis.healthcheck in Metrics Explorer."
```

---

## Day-to-day operations

### Stop / Start / Restart

```powershell
Stop-Service  DatadogIISMetrics -Force
Start-Service DatadogIISMetrics
Restart-Service DatadogIISMetrics
```

### View service logs

```powershell
Get-EventLog -LogName Application -Source DatadogIISMetrics -Newest 20 | Format-List
```

### Change tags or add metrics

```powershell
# 1. Stop the service
Stop-Service DatadogIISMetrics -Force

# 2. Edit the source file (opens Notepad)
notepad C:\DatadogMetrics\IISMetricsService.cs

# 3. Recompile
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
& $csc /target:exe `
       /out:C:\DatadogMetrics\DatadogIISMetrics.exe `
       /reference:C:\DatadogMetrics\StatsdClient.dll `
       /reference:C:\Windows\Microsoft.NET\Framework64\v4.0.30319\System.ServiceProcess.dll `
       /reference:C:\Windows\Microsoft.NET\Framework64\v4.0.30319\System.dll `
       C:\DatadogMetrics\IISMetricsService.cs

# 4. Restart
Start-Service DatadogIISMetrics
```

### Remove everything

```powershell
Stop-Service DatadogIISMetrics -Force
sc.exe delete DatadogIISMetrics
Remove-Item "C:\DatadogMetrics" -Recurse -Force
```

---

## Troubleshooting

### No metrics appear in Datadog after 2 minutes

**Check 1 — Is DogStatsD listening?**
```powershell
netstat -an | findstr ":8125"
```
Must show `UDP 127.0.0.1:8125 *:*`. If not:
```powershell
Restart-Service datadogagent
```

**Check 2 — Is the service running?**
```powershell
Get-Service DatadogIISMetrics
```

**Check 3 — Does the DogStatsD pipeline work at all?**
```powershell
$udp = New-Object System.Net.Sockets.UdpClient
$udp.Connect("127.0.0.1", 8125)
$b = [System.Text.Encoding]::UTF8.GetBytes("iis.test:42|g|#env:production")
$udp.Send($b, $b.Length) | Out-Null
$udp.Close()
```
Search for `iis.test` in Metrics Explorer. If it shows up, the pipeline works and the problem is specific to the performance counters.

---

### `w3wp.cpu_pct` and `w3wp.memory_bytes` show no data

**Why this happens:** IIS shuts down the `w3wp.exe` worker process after 20 minutes of no traffic (the default idle timeout). When w3wp is not running there is nothing to measure.

**Fix — send a few requests to wake up the worker process:**
```powershell
1..10 | ForEach-Object { Invoke-WebRequest http://localhost/ -UseBasicParsing | Out-Null }
```
Metrics will resume within 15 seconds.

**Prevent it in production:**
Open IIS Manager → Application Pools → right-click your pool → Advanced Settings
→ set **Idle Time-out (minutes)** to `0`

---

### Compile errors

| Error | Cause | Fix |
|---|---|---|
| `error CS1525: Invalid expression term '.'` | Used `?.` syntax (not supported by this compiler) | Replace `x?.Method()` with `if (x != null) { x.Method(); }` |
| `error CS0234: type or namespace does not exist` | Missing `/reference:` in compile command | Add the missing reference |
| `The system cannot find the path specified` | Wrong `csc.exe` path | Run: `Test-Path "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"` |
| `error CS0006: Metadata file 'StatsdClient.dll' could not be found` | DLL is missing | Re-run Step 4 |

> The compiler included with Windows (csc.exe) supports **C# 5 only**. Do not use `?.`, `$"..."` string interpolation, or `nameof()`.

---

### Event Log error codes

```powershell
Get-EventLog -LogName Application -Source DatadogIISMetrics -Newest 20 | Format-List
```

| EventID | Type | Meaning |
|---|---|---|
| 1 | Information | Service started OK |
| 10 | Error | Crash in the collection loop — read the message for details |
| 20 | Warning | One counter failed to read — others still work |
| 40 | Error | w3wp counter failed — likely a permissions issue |

---

### Service exists but won't start (Error 1053)

Startup timed out. Check:
1. `C:\DatadogMetrics\DatadogIISMetrics.exe` exists
2. `C:\DatadogMetrics\StatsdClient.dll` exists in the **same folder** as the EXE
3. The EXE was compiled with the same `StatsdClient.dll` that is present now

---

## Files created by this guide

| File | What it is |
|---|---|
| `C:\DatadogMetrics\IISMetricsService.cs` | The C# source — edit this to change tags or add metrics |
| `C:\DatadogMetrics\StatsdClient.dll` | Datadog DogStatsD library (downloaded in Step 4) |
| `C:\DatadogMetrics\DatadogIISMetrics.exe` | The compiled service (built in Step 6) |
| `C:\ProgramData\Datadog\datadog.yaml` | Datadog Agent config — this guide does not change it |
