# =============================================================================
# Datadog IIS/ASP.NET Metrics Collector — PowerShell approach
# Reads Windows Performance Counters and sends to Datadog via DogStatsD UDP
# Deploy as a Windows Scheduled Task (see README for setup commands)
# =============================================================================

$dogstatsd_host = "127.0.0.1"
$dogstatsd_port  = 8125
$interval        = 15   # seconds between collections

# ── CUSTOMIZE ─────────────────────────────────────────────────────────────────
$tags = "env:production,app:my-app,tech:aspnet,tech:iis,collector:powershell"
# ──────────────────────────────────────────────────────────────────────────────

# Register Windows Event Log source (once, ignore if already exists)
try {
    if (-not [System.Diagnostics.EventLog]::SourceExists("DD-PSMetrics")) {
        New-EventLog -LogName Application -Source "DD-PSMetrics"
    }
} catch {}

function Send-DogStatsD {
    param([string]$metric, [double]$value, [string]$tags)
    try {
        $udp     = New-Object System.Net.Sockets.UdpClient
        $udp.Connect($dogstatsd_host, $dogstatsd_port)
        $payload = "${metric}:${value}|g|#${tags}"
        $bytes   = [System.Text.Encoding]::UTF8.GetBytes($payload)
        $udp.Send($bytes, $bytes.Length) | Out-Null
        $udp.Close()
    } catch {
        Write-EventLog -LogName Application -Source "DD-PSMetrics" -EventId 1001 `
            -EntryType Warning -Message "Send-DogStatsD error [${metric}]: $_" `
            -ErrorAction SilentlyContinue
    }
}

function Get-PerfCounter {
    param([string]$category, [string]$counter, [string]$instance = $null)
    try {
        if ($instance) {
            $pc = New-Object System.Diagnostics.PerformanceCounter($category, $counter, $instance, $true)
        } else {
            $pc = New-Object System.Diagnostics.PerformanceCounter($category, $counter, "", $true)
        }
        $pc.NextValue() | Out-Null          # first call always returns 0 — discard
        Start-Sleep -Milliseconds 100
        $val = $pc.NextValue()
        $pc.Close()
        $pc.Dispose()
        return $val
    } catch {
        return $null
    }
}

Write-Host "DD-PSMetrics collector starting. Interval=${interval}s"

while ($true) {
    $ts = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    Write-Host "[$ts] Collecting..."

    # ── ASP.NET v4 counters ────────────────────────────────────────────────────
    $aspnet = @{
        "aspnet.requests.current"       = @("ASP.NET v4.0.30319", "Requests Current")
        "aspnet.requests.queued"        = @("ASP.NET v4.0.30319", "Requests Queued")
        "aspnet.requests.rejected"      = @("ASP.NET v4.0.30319", "Requests Rejected")
        "aspnet.request.execution_time" = @("ASP.NET v4.0.30319", "Request Execution Time")
        "aspnet.request.wait_time"      = @("ASP.NET v4.0.30319", "Request Wait Time")
        "aspnet.requests.in_queue"      = @("ASP.NET v4.0.30319", "Requests In Native Queue")
    }
    foreach ($m in $aspnet.GetEnumerator()) {
        $val = Get-PerfCounter -category $m.Value[0] -counter $m.Value[1]
        if ($val -ne $null) { Send-DogStatsD -metric $m.Key -value $val -tags $tags }
    }

    # ── IIS Web Service counters (_Total = all sites combined) ─────────────────
    $iis = @{
        "iis.connections.current" = @("Web Service", "Current Connections")
        "iis.requests.get"        = @("Web Service", "Total Get Requests")
        "iis.requests.post"       = @("Web Service", "Total Post Requests")
    }
    foreach ($m in $iis.GetEnumerator()) {
        $val = Get-PerfCounter -category $m.Value[0] -counter $m.Value[1] -instance "_Total"
        if ($val -ne $null) { Send-DogStatsD -metric $m.Key -value $val -tags $tags }
    }

    # ── w3wp worker process — sum all running IIS worker instances ─────────────
    $w3wp_cpu   = 0.0
    $w3wp_mem   = 0.0
    $w3wp_found = 0
    for ($i = 0; $i -le 7; $i++) {
        $inst = if ($i -eq 0) { "w3wp" } else { "w3wp#$i" }
        $cpu  = Get-PerfCounter -category "Process" -counter "% Processor Time" -instance $inst
        $mem  = Get-PerfCounter -category "Process" -counter "Working Set"       -instance $inst
        if ($cpu -ne $null) { $w3wp_cpu += $cpu; $w3wp_mem += $mem; $w3wp_found++ }
    }
    if ($w3wp_found -gt 0) {
        Send-DogStatsD -metric "w3wp.cpu_pct"      -value $w3wp_cpu -tags $tags
        Send-DogStatsD -metric "w3wp.memory_bytes" -value $w3wp_mem -tags $tags
    }

    Write-Host "[$ts] Done. Sleeping ${interval}s..."
    Start-Sleep -Seconds $interval
}
