using System.Net.Sockets;
using ScheduleApp.Core.Attendance;
using ScheduleApp.ZkTeco;

namespace ScheduleApp.Data.Attendance;

/// <summary>
/// Pulls the attendance log directly from a ZKTeco terminal (e.g. the MB560-VL)
/// over the network and converts it into this app's own AttendanceLog rows --
/// the live-device counterpart to AttendanceLogReader, which parses an
/// already-exported .dat file instead. Both feed the same
/// IAttendanceLogRepository.AddLogsAsync afterward (see AttendanceViewModel),
/// so a punch already on file from one path is recognized as a duplicate of
/// the same punch arriving via the other -- the unique index in
/// ScheduleDbContext is on (EmployeeId, Timestamp, PunchType) alone, not on
/// Source, deliberately.
///
/// Unlike AttendanceLogReader.ReadAttendanceLogs (a fast, local file parse),
/// this makes a live network connection to the terminal and can take a while
/// on a large log or a slow link -- callers on a UI thread should run it via
/// Task.Run (see AttendanceViewModel.FetchFromDeviceAsync) rather than await
/// it directly.
/// </summary>
public static class ZkTecoAttendanceLogReader
{
    /// <summary>
    /// EmployeeId here matches the .dat file's column-3 punch type convention
    /// (0=check-in, 1=check-out) one-for-one, since both are read from the
    /// same status byte the terminal itself assigns to a punch -- see
    /// ZkProtocol.AttendanceRecordSize's doc comment for how that byte was
    /// confirmed against a real device.
    /// </summary>
    public sealed class FetchResult
    {
        public required List<AttendanceLog> Logs { get; init; }

        /// <summary>Records the device returned whose user id wasn't a plain
        /// integer, so they couldn't be matched to Employee.Pin and were
        /// left out of Logs. Not necessarily an error -- some terminals ship
        /// with a handful of factory-test enrollments -- but worth surfacing
        /// in the run log rather than silently dropping them.</summary>
        public required int SkippedNonNumericUserIds { get; init; }

        public required string DeviceSerialNumber { get; init; }
        public required string DeviceName { get; init; }
    }

    /// <summary>
    /// Connects to the terminal, reads its serial number/name and its full
    /// attendance log (the device protocol has no server-side date filter --
    /// see ZkDevice.GetAttendanceLogs -- so this always fetches everything
    /// currently stored on the device), and disconnects again. Throws if the
    /// connection or handshake fails; callers should catch and surface
    /// ex.Message the same way ImportPunchLogAsync already does for file
    /// import failures.
    ///
    /// cancellationToken is honored throughout the whole call, not just at
    /// well-defined checkpoints between steps: it's passed into ZkDevice's own
    /// constructor, which registers a callback that closes the underlying socket the
    /// moment cancellation fires, out from under whichever blocking call (the connect
    /// handshake, a GetDeviceParam round trip, or a bulk-transfer chunk read) happens to
    /// be in flight at that instant -- see ZkDevice's constructor doc comment. That
    /// surfaces here as one of a few different exception types depending on exactly
    /// where the call was blocked (an OperationCanceledException already, straight out
    /// of TcpTransport's own connect-wait; or ObjectDisposedException/IOException/
    /// SocketException/TimeoutException from a socket that got closed mid-read/write) --
    /// the catch below normalizes all of those into a single OperationCanceledException
    /// whenever cancellation was actually requested, so DeviceFetchViewModel only ever
    /// has to catch the one exception type, the same as every other cancellable
    /// operation in this app.
    /// </summary>
    public static FetchResult FetchAttendanceLogs(
        string ip,
        int port,
        uint commKey,
        ZkTransportKind transportKind,
        int timeoutMs = 4000,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var device = new ZkDevice(ip, port, commKey, timeoutMs, transportKind, cancellationToken);
        try
        {
            device.Connect();

            string serial = device.GetDeviceParam("~SerialNumber");
            string name = device.GetDeviceParam("~DeviceName");
            var records = device.GetAttendanceLogs(cancellationToken);

            var logs = new List<AttendanceLog>(records.Count);
            int skipped = 0;

            foreach (var record in records)
            {
                // The device's user id is the same punch-clock employee code
                // that Employee.Pin matches against (see
                // AttendanceWorkflowService) -- ScheduleApp stores it as an
                // int, same as the .dat import path (AttendanceLogReader),
                // so the two sources land in identical rows either way.
                if (!int.TryParse(record.UserId, out int employeeId))
                {
                    skipped++;
                    continue;
                }

                logs.Add(new AttendanceLog
                {
                    EmployeeId = employeeId,
                    Timestamp = record.Timestamp,
                    PunchType = record.Status,
                    Source = AttendanceLogSource.Network,
                    DeviceSerialNumber = serial
                });
            }

            return new FetchResult
            {
                Logs = logs,
                SkippedNonNumericUserIds = skipped,
                DeviceSerialNumber = serial,
                DeviceName = name
            };
        }
        catch (Exception ex) when (cancellationToken.IsCancellationRequested &&
            ex is ObjectDisposedException or IOException or SocketException or TimeoutException)
        {
            // See this method's own doc comment for where each of these can come from.
            // The IsCancellationRequested guard matters: without it, a genuine timeout or
            // dropped connection that has nothing to do with a Cancel click would get
            // silently misreported as a cancellation instead of surfacing as the real
            // error it is.
            throw new OperationCanceledException("The device fetch was cancelled.", ex, cancellationToken);
        }
        finally
        {
            // Safe to call even if Connect() above never completed (or got interrupted
            // by cancellation) -- Disconnect() no-ops on IsConnected == false, and even if
            // IsConnected is somehow true against an already-closed transport, it just
            // best-effort swallows whatever that attempt throws (see Disconnect()).
            device.Disconnect();
        }
    }
}