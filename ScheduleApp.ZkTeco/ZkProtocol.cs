using System.Text;

namespace ScheduleApp.ZkTeco;

/// <summary>
/// Low-level building blocks for the ZKTeco/ZKSoftware proprietary device protocol
/// (the same wire protocol used by the vendor's SDKs and by well-known open-source
/// clients such as pyzk). This talks to the device directly over TCP or UDP on
/// port 4370, which is what ZKTeco face/fingerprint terminals like the MB560-VL use.
///
/// Packet layout (little-endian), 8-byte header + payload:
///   ushort command   (or reply code, for responses)
///   ushort checksum
///   ushort sessionId
///   ushort replyId
///   byte[] payload
///
/// When sent over TCP, the whole header+payload above is additionally wrapped in
/// an extra 8-byte prefix: 4 magic bytes (50 50 82 7D) + uint32 payload length.
/// UDP does not use that extra wrapper - the header+payload is sent as-is.
///
/// This was originally validated stand-alone in a "ZkTecoTest" connectivity
/// console app against a real MB560-VL (serial CJIN220360423, platform
/// ZAM170_TFT) before being carried over into ScheduleApp -- the record-layout
/// and chunk-framing notes below reflect fixes found by comparing a raw byte
/// capture against that same device's USB-exported attlog.dat, not guesswork.
/// </summary>
public static class ZkProtocol
{
    // Commands
    public const ushort CMD_CONNECT = 1000;
    public const ushort CMD_EXIT = 1001;
    public const ushort CMD_ENABLEDEVICE = 1002;
    public const ushort CMD_DISABLEDEVICE = 1003;
    public const ushort CMD_AUTH = 1102;
    public const ushort CMD_GET_TIME = 201;
    public const ushort CMD_OPTIONS_RRQ = 11; // read a device parameter/option
    public const ushort CMD_ATTLOG_RRQ = 13;  // read attendance log
    public const ushort CMD_CLEAR_ATTLOG = 15; // clear attendance log (not used here, kept for reference)

    // Bulk-data transfer (used for anything too big for one packet, e.g. attendance logs)
    public const ushort CMD_PREPARE_DATA = 1500; // device -> host: "here comes N bytes"
    public const ushort CMD_DATA = 1501;          // device -> host: a chunk of that data
    public const ushort CMD_FREE_DATA = 1502;     // host -> device: release the buffer when done

    // Reply codes
    public const ushort CMD_ACK_OK = 2000;
    public const ushort CMD_ACK_ERROR = 2001;
    public const ushort CMD_ACK_DATA = 2002;
    public const ushort CMD_ACK_UNAUTH = 2005;

    private const int USHRT_MAX = 65535;

    /// <summary>
    /// Builds the 8-byte header + payload packet (no TCP wrapper).
    /// <paramref name="replyId"/> is the "current" reply id counter (as tracked by the
    /// caller before this call). The checksum is computed over the header with THIS
    /// value embedded, then the reply id field actually written to the packet is
    /// bumped by one (wrapping at 65535) - the checksum is not recomputed afterwards.
    /// This matches the reference protocol implementation exactly; devices reject or
    /// silently ignore packets built any other way.
    /// </summary>
    public static byte[] BuildPacket(ushort command, ushort sessionId, ushort replyId, byte[]? payload)
    {
        payload ??= [];

        byte[] buf = new byte[8 + payload.Length];
        WriteHeader(buf, command, 0, sessionId, replyId);
        Buffer.BlockCopy(payload, 0, buf, 8, payload.Length);

        ushort checksum = ComputeChecksum(buf);

        int newReplyId = replyId + 1;
        if (newReplyId >= USHRT_MAX) newReplyId -= USHRT_MAX;

        WriteHeader(buf, command, checksum, sessionId, (ushort)newReplyId);

        return buf;
    }

    private static void WriteHeader(byte[] dest, ushort command, ushort checksum, ushort sessionId, ushort replyId)
    {
        BitConverter.GetBytes(command).CopyTo(dest, 0);
        BitConverter.GetBytes(checksum).CopyTo(dest, 2);
        BitConverter.GetBytes(sessionId).CopyTo(dest, 4);
        BitConverter.GetBytes(replyId).CopyTo(dest, 6);
    }

    /// <summary>
    /// The ZK checksum: an "Internet checksum"-style ones'-complement sum of 16-bit
    /// little-endian words (end-around carry), followed by a final bitwise complement.
    /// </summary>
    public static ushort ComputeChecksum(ReadOnlySpan<byte> data)
    {
        int sum = 0;
        int i = 0;
        int len = data.Length;

        while (len > 1)
        {
            int word = data[i] | (data[i + 1] << 8);
            sum += word;
            if (sum > USHRT_MAX) sum -= USHRT_MAX;
            i += 2;
            len -= 2;
        }

        if (len == 1)
        {
            sum += data[i];
        }

        while (sum > USHRT_MAX) sum -= USHRT_MAX;

        int result = ~sum;
        while (result < 0) result += USHRT_MAX;

        return (ushort)result;
    }

    public static (ushort command, ushort checksum, ushort sessionId, ushort replyId) ParseHeader(byte[] data)
    {
        if (data.Length < 8)
            throw new InvalidOperationException("Response too short to contain a valid ZK header.");

        ushort command = BitConverter.ToUInt16(data, 0);
        ushort checksum = BitConverter.ToUInt16(data, 2);
        ushort sessionId = BitConverter.ToUInt16(data, 4);
        ushort replyId = BitConverter.ToUInt16(data, 6);
        return (command, checksum, sessionId, replyId);
    }

    /// <summary>
    /// Decodes the packed 32-bit time value returned by CMD_GET_TIME into a DateTime.
    /// </summary>
    public static DateTime DecodeTime(uint t)
    {
        int second = (int)(t % 60); t /= 60;
        int minute = (int)(t % 60); t /= 60;
        int hour = (int)(t % 24); t /= 24;
        int day = (int)(t % 31) + 1; t /= 31;
        int month = (int)(t % 12) + 1; t /= 12;
        int year = (int)t + 2000;
        return new DateTime(year, month, day, hour, minute, second);
    }

    /// <summary>
    /// Size in bytes of a single attendance record. CORRECTED against a real,
    /// cleanly-captured raw dump from an MB560-VL (serial CJIN220360423,
    /// platform ZAM170_TFT): it is 49 bytes, not the commonly-quoted 40-byte
    /// layout used by many other ZKTeco terminals.
    ///   [0..24)  user id, ASCII, NUL-padded
    ///   [24]     verify mode (0=password, 1=fingerprint, 2=card, 15=face, etc.)
    ///   [25..29) packed timestamp (same 32-bit packing as CMD_GET_TIME, see DecodeTime)
    ///   [29]     punch/attendance status code (0=check-in, 1=check-out, 255=not set, etc.)
    ///   [30..47) reserved / padding (17 bytes, largely zero in samples seen)
    ///   [47..49) a little-endian uint16 that behaves like a running record
    ///            index: across a 15,351-record capture it increased by
    ///            exactly 1 every single record with zero gaps, which is
    ///            what confirmed this offset/size combination is correct
    ///            (rather than just "divides evenly", which many wrong
    ///            combinations also happen to do).
    /// NOTE: [24] and [29] were originally labeled the other way around
    /// (status at 24, verify mode at 29). Cross-referencing this device's
    /// own USB-exported attlog.dat against the binary capture (matching by
    /// user id + timestamp) showed the two data sources agree with each
    /// other on both fields byte-for-byte - so the byte positions were never
    /// misaligned - but the values at [24] are small codes (1, 15, 3, ...)
    /// that only make sense as verify modes (1=fingerprint, 15=face), while
    /// [29] takes values (0, 1, 255) where 255 is a standard "status not
    /// set" sentinel that no verify-mode enum uses. The labels were swapped;
    /// they are correct now.
    ///
    /// ScheduleApp.Data.Attendance.ZkTecoAttendanceLogReader maps this same
    /// Status byte straight into AttendanceLog.PunchType, matching column 3
    /// of an exported .dat file (see AttendanceLogReader) -- both paths feed
    /// the same 0=in/1=out convention into the same table.
    ///
    /// A different firmware/platform may still use a different size; if
    /// records come back garbled, dump the raw buffer (ZkDevice.GetAttendanceLogsRaw)
    /// and check for a steadily-incrementing small integer near the end of
    /// each candidate record - that's a much stronger signal than byte-count
    /// divisibility alone.
    /// </summary>
    public const int AttendanceRecordSize = 49;

    public readonly record struct AttendanceRecord(string UserId, byte VerifyMethod, DateTime Timestamp, byte Status);

    /// <summary>
    /// Parses a raw buffer returned by the attendance-log bulk transfer into
    /// individual records, using the 49-byte layout described above. Any
    /// trailing bytes that don't make up a full record are ignored.
    /// </summary>
    public static List<AttendanceRecord> ParseAttendanceLogs(byte[] data)
    {
        var records = new List<AttendanceRecord>();

        // The assembled buffer starts with a 10-byte header before the actual
        // record stream begins: a 4-byte value that looks like a fixed
        // per-transfer buffer/chunk-size constant, a 4-byte value equal to
        // (overall payload length - 4) - i.e. another, differently-scoped
        // size announcement - and a 2-byte field, in that order. This was
        // NOT guessed from byte-count divisibility (many wrong (offset,
        // record-size) pairs also divide evenly - that's a weak signal on
        // its own). It was confirmed empirically: each 49-byte record ends
        // in a little-endian uint16 that behaves like a running index, and
        // offset=10 is the only alignment where that counter increases by
        // exactly 1 for all ~15,000+ records in a real capture with zero
        // gaps - anything else (e.g. skipping only 4 bytes, or assuming
        // 40-byte records) breaks that sequence within the first few
        // records and drifts further with every record after.
        int offset = data.Length >= 10 ? 10 : 0;

        while (offset + AttendanceRecordSize <= data.Length)
        {
            string userId = Encoding.ASCII.GetString(data, offset, 24).TrimEnd('\0', ' ');
            byte verifyMethod = data[offset + 24];
            uint packedTime = BitConverter.ToUInt32(data, offset + 25);
            byte status = data[offset + 29];

            DateTime timestamp;
            try
            {
                timestamp = DecodeTime(packedTime);
            }
            catch
            {
                timestamp = default; // malformed/unused slot rather than a fatal error
            }

            records.Add(new AttendanceRecord(userId, verifyMethod, timestamp, status));
            offset += AttendanceRecordSize;
        }

        return records;
    }

    /// <summary>
    /// Computes the communication-key response used by CMD_AUTH when the device has
    /// a communication password ("comm key") configured. Only needed if the device
    /// replies CMD_ACK_UNAUTH to the initial CMD_CONNECT. Most devices ship with the
    /// comm key set to 0 (i.e. no password), in which case this is never used.
    /// </summary>
    public static byte[] MakeCommKey(uint commKey, ushort sessionId, byte ticks = 50)
    {
        uint k = 0;
        for (int i = 0; i < 32; i++)
        {
            k = (commKey & (1u << i)) != 0 ? (k << 1) | 1u : k << 1;
        }
        k = unchecked(k + sessionId);

        byte[] kBytes = BitConverter.GetBytes(k);
        byte[] xored =
        [
            (byte)(kBytes[0] ^ (byte)'Z'),
            (byte)(kBytes[1] ^ (byte)'K'),
            (byte)(kBytes[2] ^ (byte)'S'),
            (byte)(kBytes[3] ^ (byte)'O'),
        ];

        ushort h1 = BitConverter.ToUInt16(xored, 0);
        ushort h2 = BitConverter.ToUInt16(xored, 2);

        byte[] swapped = new byte[4];
        BitConverter.GetBytes(h2).CopyTo(swapped, 0);
        BitConverter.GetBytes(h1).CopyTo(swapped, 2);

        byte b = ticks;
        return
        [
            (byte)(swapped[0] ^ b),
            (byte)(swapped[1] ^ b),
            b,                          // NOT xored with swapped[2] - this matches the original commpro.c MakeKey exactly
            (byte)(swapped[3] ^ b),
        ];
    }
}
