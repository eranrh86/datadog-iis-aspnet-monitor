# Datadog IIS & ASP.NET Monitoring — Windows Server

Send IIS, ASP.NET, and w3wp worker-process metrics to Datadog from a Windows Server.
**No changes to your application code are required.**

Three approaches are provided. Pick the one that fits your environment:

| Approach | File | Best for |
|---|---|---|
| **A — C# Windows Service** | `IISMetricsService.cs` | Production. Runs as a real Windows Service, auto-restarts on crash. |
| **B — PowerShell Scheduled Task** | `ps_metrics.ps1` | Quick setup. No compiler needed. Pure PowerShell. |
| **C — Python Scheduled Task** | `py_metrics.py` | Teams already using Python. No extra packages (stdlib only). |

All three approaches collect the same 11 metrics and send them the same way — via **DogStatsD UDP** to the Datadog Agent on port 8125.

---

## How it works

```
IIS / ASP.NET / w3wp  (your existing app — nothing changes here)
         |
         | Windows reads built-in Performance Counters automatically
         v
  your collector script  ← THIS is what you deploy using this guide
  (runs every 15 seconds)
         |
         | UDP packets → 127.0.0.1:8125
         v
  Datadog Agent  (already on your server)
         |
         | HTTPS
         v
  Datadog dashboard
```

---

## Metrics collected

| Metric | What it measures |
|---|---|
| `aspnet.requests.current` | Requests being processed right now |
| `aspnet.requests.queued` | Requests waiting in line |
| `aspnet.requests.rejected` | Requests dropped because the queue was full |
| `aspnet.requests.in_queue` | Requests in the Windows kernel queue |
| `aspnet.request.execution_time` | How long requests take to execute (ms) |
| `aspnet.request.wait_time` | How long requests wait before starting (ms) |
| `iis.connections.current` | Active HTTP connections right now |
| `iis.requests.get` | Total GET requests served |
| `iis.requests.post` | Total POST requests served |
| `w3wp.cpu_pct` | CPU % used by the IIS worker process |
| `w3wp.memory_bytes` | Memory used by the IIS worker process |

---

## Before you start — checklist

- [ ] Windows Server 2016 / 2019 / 2022
- [ ] IIS installed and running with at least one app pool
- [ ] Datadog Agent 7 installed on the server
- [ ] PowerShell open **as Administrator**
- [ ] Internet access (or use the offline options below)

> **Using `infrastructure_mode: basic`?**
> This mode blocks most Datadog integrations, but DogStatsD (UDP port 8125) still works.
> All three approaches use DogStatsD — so they work correctly in this mode.

---

## Step 1 — Open PowerShell as Administrator

Click **Start** → type `PowerShell` → right-click **Windows PowerShell** → **Run as administrator**.

All commands in this guide must run in that window.

---

## Step 2 — Confirm Datadog is ready

```powershell
netstat -an | findstr ":8125"
```

You must see:
```
UDP    127.0.0.1:8125    *:*
```

If nothing appears, restart the Datadog Agent:
```powershell
Restart-Service datadogagent
```

---

## Step 3 — Create the working folder

```powershell
New-Item -ItemType Directory -Path "C:\DatadogMetrics" -Force
```

---

---

# Approach A — C# Windows Service

Best for production. Compiles to a native `.exe`, registered as a Windows Service that auto-starts on reboot and auto-restarts on crash.

---

## A-1 — Download the Datadog DLL

The C# service needs `StatsdClient.dll` from the official Datadog NuGet package.

```powershell
# Download the package (it is a zip file renamed .nupkg)
$url = "https://www.nuget.org/api/v2/package/DogStatsD-CSharp-Client/9.0.0"
$pkg = "C:\DatadogMetrics\dogstatsd.nupkg"
Invoke-WebRequest -Uri $url -OutFile $pkg

# Open the zip and extract just the DLL for .NET 4.x
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip   = [System.IO.Compression.ZipFile]::OpenRead($pkg)
$entry = $zip.Entries | Where-Object { $_.FullName -like "*/net461/StatsdClient.dll" } |
         Select-Object -First 1
[System.IO.Compression.ZipFileExtensions]::ExtractToFile(
    $entry, "C:\DatadogMetrics\StatsdClient.dll", $true)
$zip.Dispose()

Write-Host "DLL size: $((Get-Item 'C:\DatadogMetrics\StatsdClient.dll').Length) bytes"
# Expected: 67072 bytes
```

**What each line does:**

| Line | Plain-English explanation |
|---|---|
| `Invoke-WebRequest` | Downloads the package from nuget.org — it is just a zip file |
| `Add-Type` | Loads PowerShell's built-in zip library |
| `ZipFile::OpenRead` | Opens the downloaded zip |
| `Where-Object ... net461` | Finds the right DLL inside the zip (the one for .NET 4.x) |
| `ExtractToFile` | Copies just that one file out of the zip |
| `Write-Host` | Confirms the file size — should be 67072 bytes |

**No internet? (offline server):**
On any machine with internet → go to `https://www.nuget.org/packages/DogStatsD-CSharp-Client/9.0.0` → click Download → rename `.nupkg` to `.zip` → open with 7-Zip → go to `lib\net461\` → copy `StatsdClient.dll` → paste it to `C:\DatadogMetrics\StatsdClient.dll` on the server.

---

## A-2 — Write the C# source file

Copy `IISMetricsService.cs` from this repository to `C:\DatadogMetrics\IISMetricsService.cs`.

**Edit the `Tags` array** near the top to match your environment:
```csharp
private static readonly string[] Tags = {
    "env:production",   // ← change this
    "app:my-app",       // ← change this
    "tech:aspnet",
    "tech:iis"
};
```

Or write it directly from PowerShell:
```powershell
# Download from GitHub
Invoke-WebRequest -Uri "https://raw.githubusercontent.com/eranrh86/datadog-iis-aspnet-monitor/main/IISMetricsService.cs" -OutFile "C:\DatadogMetrics\IISMetricsService.cs"
```

---

## A-3 — Compile

Windows Server already includes the C# compiler (`csc.exe`). No Visual Studio needed.

```powershell
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

& $csc /target:exe `
       /out:C:\DatadogMetrics\DatadogIISMetrics.exe `
       /reference:C:\DatadogMetrics\StatsdClient.dll `
       /reference:C:\Windows\Microsoft.NET\Framework64\v4.0.30319\System.ServiceProcess.dll `
       /reference:C:\Windows\Microsoft.NET\Framework64\v4.0.30319\System.dll `
       C:\DatadogMetrics\IISMetricsService.cs

Write-Host "EXE size: $((Get-Item 'C:\DatadogMetrics\DatadogIISMetrics.exe').Length) bytes"
# Expected: ~8192 bytes
```

A successful compile shows only the compiler version line — no red error lines.

---

## A-4 — Install and start the Windows Service

```powershell
sc.exe create DatadogIISMetrics `
    binPath= "C:\DatadogMetrics\DatadogIISMetrics.exe" `
    start=   auto `
    obj=     LocalSystem `
    DisplayName= "Datadog IIS Metrics"

sc.exe description DatadogIISMetrics "Collects IIS and ASP.NET metrics and sends them to Datadog via DogStatsD"

Start-Service DatadogIISMetrics
Get-Service DatadogIISMetrics | Select-Object Name, Status
```

Expected:
```
Name               Status
----               ------
DatadogIISMetrics Running
```

---

## A-5 — Check it started

```powershell
Get-EventLog -LogName Application -Source DatadogIISMetrics -Newest 5 | Format-List
```

Expected:
```
EntryType : Information
Message   : DatadogIISMetrics started.
```

---

---

# Approach B — PowerShell Scheduled Task

No compiler needed. Just a PowerShell script set to run as a Scheduled Task.

---

## B-1 — Copy the script

Download `ps_metrics.ps1` from this repo:

```powershell
Invoke-WebRequest -Uri "https://raw.githubusercontent.com/eranrh86/datadog-iis-aspnet-monitor/main/ps_metrics.ps1" -OutFile "C:\DatadogMetrics\ps_metrics.ps1"
```

**Edit the tags** at the top of the file:
```powershell
$tags = "env:production,app:my-app,tech:aspnet,tech:iis,collector:powershell"
```

---

## B-2 — Install as a Scheduled Task

```powershell
$action   = New-ScheduledTaskAction -Execute "powershell.exe" `
              -Argument "-NonInteractive -ExecutionPolicy Bypass -File C:\DatadogMetrics\ps_metrics.ps1"
$trigger  = New-ScheduledTaskTrigger -AtStartup
$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit 0 -RestartCount 3 `
              -RestartInterval (New-TimeSpan -Minutes 1)

Register-ScheduledTask -TaskName "DD-PowerShell-Metrics" `
    -Action $action -Trigger $trigger `
    -RunLevel Highest -User "SYSTEM" -Settings $settings -Force

Start-ScheduledTask -TaskName "DD-PowerShell-Metrics"
```

---

## B-3 — Confirm it is running

```powershell
(Get-ScheduledTask -TaskName "DD-PowerShell-Metrics").State
# Expected: Running
```

---

---

# Approach C — Python Scheduled Task

Uses only Python stdlib — no pip packages required. `typeperf.exe` is built into Windows and reads the performance counters.

---

## C-1 — Install Python

```powershell
# Download Python 3.11
Invoke-WebRequest -Uri "https://www.python.org/ftp/python/3.11.9/python-3.11.9-amd64.exe" `
    -OutFile "C:\temp\python-installer.exe"

# Silent install (installs for all users, adds to PATH)
Start-Process "C:\temp\python-installer.exe" `
    -ArgumentList "/quiet InstallAllUsers=1 PrependPath=1 TargetDir=C:\Python311" -Wait

# Confirm
C:\Python311\python.exe --version
# Expected: Python 3.11.9
```

---

## C-2 — Copy the script

```powershell
Invoke-WebRequest -Uri "https://raw.githubusercontent.com/eranrh86/datadog-iis-aspnet-monitor/main/py_metrics.py" -OutFile "C:\DatadogMetrics\py_metrics.py"
```

**Edit the tags** near the top of the file:
```python
TAGS = "env:production,app:my-app,tech:aspnet,tech:iis,collector:python"
```

---

## C-3 — Install as a Scheduled Task

```powershell
$action   = New-ScheduledTaskAction -Execute "C:\Python311\python.exe" `
              -Argument "C:\DatadogMetrics\py_metrics.py"
$trigger  = New-ScheduledTaskTrigger -AtStartup
$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit 0 -RestartCount 3 `
              -RestartInterval (New-TimeSpan -Minutes 1)

Register-ScheduledTask -TaskName "DD-Python-Metrics" `
    -Action $action -Trigger $trigger `
    -RunLevel Highest -User "SYSTEM" -Settings $settings -Force

Start-ScheduledTask -TaskName "DD-Python-Metrics"
```

---

## C-4 — Confirm it is running

```powershell
(Get-ScheduledTask -TaskName "DD-Python-Metrics").State
# Expected: Running
```

---

---

# Validate all metrics in Datadog

After starting any of the three collectors, wait 30–60 seconds then go to:
`app.datadoghq.com/metric/explorer`

Search for: `aspnet.requests.current` — all 11 metrics should appear.

**Quick pipeline test** — sends one test metric directly via UDP to confirm the path to Datadog works:

```powershell
$udp = New-Object System.Net.Sockets.UdpClient
$udp.Connect("127.0.0.1", 8125)
$b = [System.Text.Encoding]::UTF8.GetBytes("iis.healthcheck:1|g|#env:production")
$udp.Send($b, $b.Length) | Out-Null
$udp.Close()
Write-Host "Test sent — search for iis.healthcheck in Metrics Explorer"
```

---

# Day-to-day operations

## Approach A — Windows Service

```powershell
# Stop / Start / Restart
Stop-Service  DatadogIISMetrics -Force
Start-Service DatadogIISMetrics
Restart-Service DatadogIISMetrics

# View logs
Get-EventLog -LogName Application -Source DatadogIISMetrics -Newest 20 | Format-List

# Uninstall
Stop-Service DatadogIISMetrics -Force
sc.exe delete DatadogIISMetrics
```

## Approach B — PowerShell Task

```powershell
# Stop / Start
Stop-ScheduledTask  -TaskName "DD-PowerShell-Metrics"
Start-ScheduledTask -TaskName "DD-PowerShell-Metrics"

# Remove
Unregister-ScheduledTask -TaskName "DD-PowerShell-Metrics" -Confirm:$false
```

## Approach C — Python Task

```powershell
# Stop / Start
Stop-ScheduledTask  -TaskName "DD-Python-Metrics"
Start-ScheduledTask -TaskName "DD-Python-Metrics"

# Remove
Unregister-ScheduledTask -TaskName "DD-Python-Metrics" -Confirm:$false
```

---

# Troubleshooting

## No metrics after 2 minutes

**Check 1 — Is DogStatsD listening?**
```powershell
netstat -an | findstr ":8125"
```
Must show `UDP 127.0.0.1:8125 *:*`. If not: `Restart-Service datadogagent`

**Check 2 — Is your collector running?**
```powershell
Get-Service DatadogIISMetrics                             # Approach A
(Get-ScheduledTask "DD-PowerShell-Metrics").State         # Approach B
(Get-ScheduledTask "DD-Python-Metrics").State             # Approach C
```

**Check 3 — Does the DogStatsD pipeline work at all?**
Run the quick pipeline test above. If `iis.healthcheck` appears in Datadog, the pipeline works and the problem is with reading the performance counters.

---

## `w3wp.cpu_pct` and `w3wp.memory_bytes` show no data

IIS shuts down the `w3wp.exe` worker process after **20 minutes of no traffic** (the default idle timeout). When w3wp is not running there is nothing to measure.

**Fix — generate traffic to restart the worker:**
```powershell
1..10 | ForEach-Object { Invoke-WebRequest http://localhost/ -UseBasicParsing | Out-Null }
```
Metrics resume within one 15-second collection cycle.

**Prevent it permanently:**
IIS Manager → Application Pools → right-click your pool → Advanced Settings → **Idle Time-out (minutes)** → set to `0`

---

## Compile errors (Approach A only)

| Error | Cause | Fix |
|---|---|---|
| `error CS1525: Invalid expression term '.'` | Used `?.` syntax (C# 6+, not supported) | Replace `x?.Method()` with `if (x != null) { x.Method(); }` |
| `error CS0006: Metadata file 'StatsdClient.dll' could not be found` | DLL missing | Re-run Step A-1 |
| `The system cannot find the path specified` | Wrong `csc.exe` path | Run: `Test-Path "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"` |

> `csc.exe` from `Framework64\v4.0.30319` supports **C# 5 only** — no `?.`, no `$"..."`, no `nameof()`.

---

## Files in this repository

| File | What it is |
|---|---|
| `IISMetricsService.cs` | Approach A — C# source. Edit to customize tags or add metrics. |
| `ps_metrics.ps1` | Approach B — PowerShell script. Edit `$tags` at the top. |
| `py_metrics.py` | Approach C — Python script. Edit `TAGS` at the top. |
| `IIS_ASP_NET_Datadog_Guide.pdf` | PDF version of this guide for printing or sharing. |
