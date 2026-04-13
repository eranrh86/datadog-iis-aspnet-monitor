"""
Datadog IIS/ASP.NET Metrics Collector — Python approach
Reads Windows Performance Counters via typeperf.exe and sends to Datadog via DogStatsD UDP.
Deploy as a Windows Scheduled Task (see README for setup commands).

Requirements:
  - Python 3.8+ (https://www.python.org/downloads/)
  - No extra packages — uses only stdlib (socket, subprocess)
  - Windows only (uses typeperf.exe, a built-in Windows tool)
"""

import socket
import subprocess
import time
import sys

# ── CUSTOMIZE ─────────────────────────────────────────────────────────────────
TAGS     = "env:production,app:my-app,tech:aspnet,tech:iis,collector:python"
INTERVAL = 15        # seconds between collections
STATSD   = ("127.0.0.1", 8125)
# ──────────────────────────────────────────────────────────────────────────────


def gauge(metric, value):
    """Send a DogStatsD gauge over UDP."""
    payload = "{}:{}|g|#{}".format(metric, value, TAGS)
    try:
        s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        s.sendto(payload.encode("utf-8"), STATSD)
        s.close()
    except Exception as e:
        sys.stderr.write("UDP error [{}]: {}\n".format(metric, e))


def typeperf(counter_path):
    """
    Read one Windows Performance Counter using typeperf.exe.
    Returns a float, or None if the counter is unavailable.
    typeperf is built into Windows — no install needed.
    """
    try:
        r = subprocess.run(
            ["typeperf", counter_path, "-sc", "1"],
            capture_output=True, text=True, timeout=15
        )
        for line in r.stdout.splitlines():
            # Output lines look like:  "timestamp","value"
            # Skip the header line that starts with "(PDH..."
            if "," in line and not line.startswith('"(PDH'):
                parts = line.strip().split(",")
                if len(parts) >= 2:
                    val_str = parts[-1].strip().strip('"')
                    if val_str not in ("", " "):
                        try:
                            return float(val_str)
                        except ValueError:
                            pass
        return None
    except Exception as e:
        sys.stderr.write("typeperf error [{}]: {}\n".format(counter_path, e))
        return None


def collect():
    # ── ASP.NET v4 counters ────────────────────────────────────────────────────
    aspnet_counters = [
        ("aspnet.requests.current",       "\\ASP.NET v4.0.30319\\Requests Current"),
        ("aspnet.requests.queued",         "\\ASP.NET v4.0.30319\\Requests Queued"),
        ("aspnet.requests.rejected",       "\\ASP.NET v4.0.30319\\Requests Rejected"),
        ("aspnet.request.execution_time",  "\\ASP.NET v4.0.30319\\Request Execution Time"),
        ("aspnet.request.wait_time",       "\\ASP.NET v4.0.30319\\Request Wait Time"),
        ("aspnet.requests.in_queue",       "\\ASP.NET v4.0.30319\\Requests In Native Queue"),
    ]
    for metric, path in aspnet_counters:
        v = typeperf(path)
        if v is not None:
            gauge(metric, v)

    # ── IIS Web Service counters (_Total = all sites combined) ─────────────────
    iis_counters = [
        ("iis.connections.current", "\\Web Service(_Total)\\Current Connections"),
        ("iis.requests.get",        "\\Web Service(_Total)\\Total Get Requests"),
        ("iis.requests.post",       "\\Web Service(_Total)\\Total Post Requests"),
    ]
    for metric, path in iis_counters:
        v = typeperf(path)
        if v is not None:
            gauge(metric, v)

    # ── w3wp worker process — sum all running IIS worker instances ─────────────
    w3wp_cpu   = 0.0
    w3wp_mem   = 0.0
    found      = False
    for i in range(8):
        inst = "w3wp" if i == 0 else "w3wp#{}".format(i)
        cpu  = typeperf("\\Process({})\\% Processor Time".format(inst))
        mem  = typeperf("\\Process({})\\Working Set".format(inst))
        if cpu is not None:
            w3wp_cpu += cpu
            w3wp_mem += (mem or 0.0)
            found = True
    if found:
        gauge("w3wp.cpu_pct",      w3wp_cpu)
        gauge("w3wp.memory_bytes", w3wp_mem)


if __name__ == "__main__":
    sys.stdout.write("py_metrics.py starting — sending to {}:{}\n".format(*STATSD))
    sys.stdout.flush()
    while True:
        try:
            collect()
        except Exception as e:
            sys.stderr.write("collect() error: {}\n".format(e))
        time.sleep(INTERVAL)
