using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;
using ScheduleApp.Core.Attendance;

namespace ScheduleApp.Data.Attendance;

/// <summary>
/// Parses a ZKTeco-style .dat punch export: tab-delimited, no header row,
/// column 0 = employee code, column 1 = timestamp, column 3 = punch type.
/// </summary>
public class AttendanceLogReader
{
    private sealed class AttendanceLogMap : ClassMap<AttendanceLog>
    {
        public AttendanceLogMap()
        {
            Map(m => m.EmployeeId).Index(0);
            Map(m => m.Timestamp).Index(1);
            Map(m => m.PunchType).Index(3);
        }
    }

    public static List<AttendanceLog> ReadAttendanceLogs(string filePath)
    {
        if (!File.Exists(filePath)) return [];

        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            Delimiter = "\t",
            HasHeaderRecord = false,
            MissingFieldFound = null,
            BadDataFound = null,
            ReadingExceptionOccurred = args => false
        };

        using var reader = new StreamReader(filePath);
        using var csv = new CsvReader(reader, config);
        csv.Context.RegisterClassMap<AttendanceLogMap>();
        List<AttendanceLog> logs = [.. csv.GetRecords<AttendanceLog>()];

        return logs;
    }
}
