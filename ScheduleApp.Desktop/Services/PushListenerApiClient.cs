using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using ScheduleApp.Desktop.Models.PushListener;

namespace ScheduleApp.Desktop.Services;

/// <summary>
/// Thin wrapper around a running ScheduleApp.PushListener instance's /admin/* HTTP
/// endpoints -- moved here from the now-redundant standalone ScheduleApp.PushListener.ControlPanel
/// project. No project reference to ScheduleApp.PushListener itself: this app talks to it
/// purely over HTTP, exactly as it would if PushListener were running on a different machine
/// -- which, since PushListener is meant to run headless on the SQL Server Express box
/// independent of whether anyone has this Desktop app open, it very well might be.
/// </summary>
public class PushListenerApiClient : IDisposable
{
    // ASP.NET Core's controllers serialize JSON with camelCase property names by default
    // (e.g. "serialNumber", not "SerialNumber"). The DTOs here are PascalCase, matching
    // normal C# convention -- without this, System.Text.Json's default options are
    // case-SENSITIVE and every property below would silently deserialize to null/0/default
    // instead of throwing. JsonSerializerDefaults.Web is the same preset ASP.NET Core itself
    // uses, so this matches the server side exactly rather than just happening to also work.
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private HttpClient _http;

    public string BaseUrl { get; private set; }

    public PushListenerApiClient(string baseUrl)
    {
        BaseUrl = baseUrl;
        _http = new HttpClient
        {
            BaseAddress = new Uri(baseUrl),
            Timeout = TimeSpan.FromSeconds(10)
        };
    }

    /// <summary>
    /// Repoints this client at a different server without the caller needing to construct a
    /// new instance -- lets PushListenerViewModel keep one long-lived client for the tab's
    /// whole lifetime, even if the URL box gets edited.
    /// </summary>
    public void UpdateBaseUrl(string baseUrl)
    {
        if (baseUrl == BaseUrl)
        {
            return;
        }

        _http.Dispose();
        BaseUrl = baseUrl;
        _http = new HttpClient
        {
            BaseAddress = new Uri(baseUrl),
            Timeout = TimeSpan.FromSeconds(10)
        };
    }

    public async Task<bool> TestConnectionAsync()
    {
        try
        {
            using var response = await _http.GetAsync("/admin/devices");
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<List<PushListenerDeviceInfo>> GetDevicesAsync() =>
        await _http.GetFromJsonAsync<List<PushListenerDeviceInfo>>("/admin/devices", JsonOptions) ?? [];

    /// <summary>
    /// Returns null on any failure (unreachable server, non-success status, bad JSON) rather
    /// than throwing -- unlike GetDevicesAsync/GetAttendanceAsync, this is meant to be polled
    /// silently on the same auto-refresh timer as the Devices tab, and a health check that
    /// itself throws on the exact condition it exists to report ("the server isn't answering
    /// right now") would need the same try/catch at every call site anyway.
    /// </summary>
    public async Task<PushListenerHealthInfo?> GetHealthAsync()
    {
        try
        {
            using var response = await _http.GetAsync("/admin/health");
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }
            return await response.Content.ReadFromJsonAsync<PushListenerHealthInfo>(JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// "level" is a minimum severity (Information/Warning/Error/...), not an exact match --
    /// see AdminController.GetLogs. Unlike GetHealthAsync, this is only called on an explicit
    /// Refresh click or opt-in auto-refresh for the Logs tab, so failures propagate normally
    /// (as an HttpRequestException) for PushListenerViewModel to surface, the same as
    /// GetDevicesAsync/GetAttendanceAsync.
    /// </summary>
    public async Task<List<PushListenerLogEntry>> GetLogsAsync(string? level = null, string? sn = null, int take = 200)
    {
        var query = new List<string> { $"take={take}" };
        if (!string.IsNullOrWhiteSpace(level))
        {
            query.Add($"level={Uri.EscapeDataString(level)}");
        }
        if (!string.IsNullOrWhiteSpace(sn))
        {
            query.Add($"sn={Uri.EscapeDataString(sn)}");
        }

        var url = "/admin/logs?" + string.Join("&", query);
        return await _http.GetFromJsonAsync<List<PushListenerLogEntry>>(url, JsonOptions) ?? [];
    }

    /// <summary>
    /// "pin" (kept as the parameter name here to match PushListener's own /admin/attendance
    /// query string, which speaks device/ADMS terminology on purpose) must be numeric if
    /// given at all; the server returns 400 otherwise, which surfaces here as an
    /// HttpRequestException, same as a malformed startTime/endTime.
    /// </summary>
    public async Task<List<PushListenerAttendanceLogInfo>> GetAttendanceAsync(
        string? sn = null, string? pin = null, int take = 200, string? startTime = null, string? endTime = null)
    {
        var query = new List<string> { $"take={take}" };
        if (!string.IsNullOrWhiteSpace(sn))
        {
            query.Add($"sn={Uri.EscapeDataString(sn)}");
        }
        if (!string.IsNullOrWhiteSpace(pin))
        {
            query.Add($"pin={Uri.EscapeDataString(pin)}");
        }
        if (!string.IsNullOrWhiteSpace(startTime))
        {
            query.Add($"startTime={Uri.EscapeDataString(startTime)}");
        }
        if (!string.IsNullOrWhiteSpace(endTime))
        {
            query.Add($"endTime={Uri.EscapeDataString(endTime)}");
        }

        var url = "/admin/attendance?" + string.Join("&", query);
        return await _http.GetFromJsonAsync<List<PushListenerAttendanceLogInfo>>(url, JsonOptions) ?? [];
    }

    /// <summary>
    /// Queues the DATA QUERY ATTLOG resync command for a device -- picked up on its next
    /// /iclock/getrequest poll, not immediate.
    /// </summary>
    public async Task ResyncAttendanceAsync(string sn, string? startTime = null, string? endTime = null)
    {
        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(startTime))
        {
            query.Add($"startTime={Uri.EscapeDataString(startTime)}");
        }
        if (!string.IsNullOrWhiteSpace(endTime))
        {
            query.Add($"endTime={Uri.EscapeDataString(endTime)}");
        }

        var url = $"/admin/devices/{Uri.EscapeDataString(sn)}/resync-attendance";
        if (query.Count > 0)
        {
            url += "?" + string.Join("&", query);
        }

        using var response = await _http.PostAsync(url, content: null);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Resets sync stamps to 0 and queues a CHECK command -- device re-uploads its full history on next poll.</summary>
    public async Task ForceRecheckAsync(string sn)
    {
        using var response = await _http.PostAsync($"/admin/devices/{Uri.EscapeDataString(sn)}/force-recheck", content: null);
        response.EnsureSuccessStatusCode();
    }

    public void Dispose() => _http.Dispose();
}
