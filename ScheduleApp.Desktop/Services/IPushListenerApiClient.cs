using ScheduleApp.Desktop.Models.PushListener;

namespace ScheduleApp.Desktop.Services;

/// <summary>A running ScheduleApp.PushListener instance's /admin/* endpoints -- see
/// <see cref="PushListenerApiClient"/>, the HTTP implementation, for what each call does.
/// PushListenerViewModel talks to this, so its polling and commands can be exercised without a
/// listener running.</summary>
public interface IPushListenerApiClient : IDisposable
{
    string BaseUrl { get; }

    void UpdateBaseUrl(string baseUrl);

    Task<bool> TestConnectionAsync();

    Task<List<PushListenerDeviceInfo>> GetDevicesAsync();

    Task<PushListenerHealthInfo?> GetHealthAsync();

    Task<List<PushListenerLogEntry>> GetLogsAsync(string? level = null, string? sn = null, int take = 200);

    Task<List<PushListenerAttendanceLogInfo>> GetAttendanceAsync(
        string? sn = null, string? pin = null, int take = 200, string? startTime = null, string? endTime = null);

    Task ResyncAttendanceAsync(string sn, string? startTime = null, string? endTime = null);

    Task ForceRecheckAsync(string sn);
}
