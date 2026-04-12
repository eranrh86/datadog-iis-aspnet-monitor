using System;
using System.Diagnostics;
using System.ServiceProcess;
using System.Threading;
using StatsdClient;

public class IISMetricsService : ServiceBase
{
    private Thread _thread;
    private volatile bool _running;
    private DogStatsdService _statsd;
    private static readonly string[] Tags = { "env:demo", "app:nba-store", "tech:aspnet", "tech:iis" };
    private const string EvtSource = "DatadogIISMetrics";
    private const string EvtLog   = "Application";

    public IISMetricsService() { ServiceName = "DatadogIISMetrics"; }
    static void Main() { ServiceBase.Run(new IISMetricsService()); }

    protected override void OnStart(string[] args)
    {
        EnsureEventSource();
        _statsd = new DogStatsdService();
        _statsd.Configure(new StatsdConfig { StatsdServerName = "127.0.0.1", StatsdPort = 8125 });
        _running = true;
        _thread = new Thread(RunLoop) { IsBackground = true };
        _thread.Start();
        EventLog.WriteEntry(EvtSource, "DatadogIISMetrics service started.", EventLogEntryType.Information, 1);
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
            try { Collect(); }
            catch (Exception ex)
            {
                EventLog.WriteEntry(EvtSource, "Collect() error: " + ex.Message + "\r\n" + ex.StackTrace, EventLogEntryType.Error, 10);
            }
            Thread.Sleep(15000);
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
        Gauge("iis.connections.current",       "Web Service",        "Current Connections",      "_Total");
        Gauge("iis.requests.get",              "Web Service",        "Total Get Requests",       "_Total");
        Gauge("iis.requests.post",             "Web Service",        "Total Post Requests",      "_Total");
        GaugeW3wp("w3wp.cpu_pct",      "Process", "% Processor Time");
        GaugeW3wp("w3wp.memory_bytes", "Process", "Working Set");
    }

    private void Gauge(string metric, string category, string counter, string instance)
    {
        try
        {
            PerformanceCounter pc;
            if (instance != null)
                pc = new PerformanceCounter(category, counter, instance, true);
            else
                pc = new PerformanceCounter(category, counter, true);
            double val = pc.NextValue();
            _statsd.Gauge(metric, val, tags: Tags);
        }
        catch (Exception ex)
        {
            EventLog.WriteEntry(EvtSource, "Gauge failed [" + metric + "]: " + ex.Message, EventLogEntryType.Warning, 20);
        }
    }

    private void GaugeW3wp(string metric, string category, string counter)
    {
        try
        {
            double sum = 0;
            bool found = false;

            // Try up to 8 numbered w3wp instances (w3wp, w3wp#1 ... w3wp#7)
            for (int i = 0; i < 8; i++)
            {
                string instName = (i == 0) ? "w3wp" : "w3wp#" + i;
                try
                {
                    if (!PerformanceCounterCategory.CounterExists(counter, category))
                        break;
                    if (!PerformanceCounterCategory.InstanceExists(instName, category))
                        break;
                    var pc = new PerformanceCounter(category, counter, instName, true);
                    sum += pc.NextValue();
                    found = true;
                }
                catch (Exception)
                {
                    // No more instances
                    break;
                }
            }

            if (found)
            {
                _statsd.Gauge(metric, sum, tags: Tags);
            }
            else
            {
                // Log once so we can diagnose — enumerate what instances exist
                try
                {
                    string[] names = PerformanceCounterCategory.GetCategories() != null
                        ? new PerformanceCounterCategory(category).GetInstanceNames()
                        : new string[0];
                    string list = string.Join(", ", names);
                    EventLog.WriteEntry(EvtSource,
                        "GaugeW3wp: no w3wp instance found for " + metric +
                        ". Process instances: " + list,
                        EventLogEntryType.Warning, 30);
                }
                catch (Exception ex2)
                {
                    EventLog.WriteEntry(EvtSource,
                        "GaugeW3wp: no w3wp instance + GetInstanceNames failed: " + ex2.Message,
                        EventLogEntryType.Warning, 31);
                }
            }
        }
        catch (Exception ex)
        {
            EventLog.WriteEntry(EvtSource, "GaugeW3wp failed [" + metric + "]: " + ex.Message, EventLogEntryType.Error, 40);
        }
    }

    private void EnsureEventSource()
    {
        try
        {
            if (!EventLog.SourceExists(EvtSource))
                EventLog.CreateEventSource(EvtSource, EvtLog);
        }
        catch (Exception) { }
    }
}
