using System.Text;

namespace ScheduleApp.ZkTeco;

public enum ZkTransportKind
{
    Tcp,
    Udp
}

/// <summary>
/// Minimal client for ZKTeco/ZKSoftware terminals (e.g. the MB560-VL face/fingerprint
/// terminal) that speaks the vendor's proprietary device protocol directly over the
/// network. This is not the official Windows-only "zkemkeeper" COM SDK - it talks the
/// wire protocol itself, so it runs cross-platform under .NET.
///
/// Typical usage:
///   using var device = new ZkDevice("192.168.1.201");
///   device.Connect();
///   Console.WriteLine(device.GetDeviceTime());
///   device.Disconnect();
///
/// Within ScheduleApp, callers don't normally use this class directly -- see
/// ScheduleApp.Data.Attendance.ZkTecoAttendanceLogReader, which wraps Connect/
/// GetAttendanceLogs/Disconnect into a single call and converts the result into
/// this app's own AttendanceLog model.
/// </summary>
public sealed class ZkDevice : IDisposable
{
    private readonly string _ip;
    private readonly int _port;
    private readonly uint _commKey;
    private readonly int _timeoutMs;
    private readonly ZkTransportKind _transportKind;
    private readonly CancellationToken _cancellationToken;
    private readonly CancellationTokenRegistration _cancellationRegistration;

    private ITransport? _transport;

    // This mirrors the reference client's "current reply id" state: it starts at
    // 65534 for a fresh connection, and after every exchange is set to whatever
    // reply number the device echoed back in its response (not just incremented
    // locally) - see BuildPacket's doc comment for why this matters.
    private ushort _replyId;

    public ushort SessionId { get; private set; }
    public bool IsConnected { get; private set; }

    /// <summary>
    /// cancellationToken defaults to CancellationToken.None (never fires) so existing
    /// callers that don't care about cancellation are unaffected. When it IS cancelled,
    /// this closes _transport out from under whichever blocking socket call happens to be
    /// in flight at that moment -- Connect's handshake, GetDeviceParam, or a
    /// ReadBufferedData chunk read alike -- rather than only being checked between
    /// discrete steps. The registration is created here, before _transport necessarily
    /// exists yet, because the callback reads the _transport field at invocation time (not
    /// registration time): if cancellation fires before Connect() has assigned it, the
    /// callback is simply a no-op for that field, and it's the cancellationToken passed
    /// into TcpTransport's own constructor (see Connect() below) that covers that earlier
    /// window instead, since the transport being constructed can't be reached from outside
    /// its own not-yet-returned constructor call.
    /// </summary>
    public ZkDevice(
        string ip,
        int port = 4370,
        uint commKey = 0,
        int timeoutMs = 4000,
        ZkTransportKind transportKind = ZkTransportKind.Tcp,
        CancellationToken cancellationToken = default)
    {
        _ip = ip;
        _port = port;
        _commKey = commKey;
        _timeoutMs = timeoutMs;
        _transportKind = transportKind;
        _cancellationToken = cancellationToken;
        _cancellationRegistration = cancellationToken.Register(() => _transport?.Dispose());
    }

    public void Connect()
    {
        _transport = _transportKind switch
        {
            // Only TcpTransport's constructor gets the token -- it's the one with its own
            // blocking wait (the initial connect handshake) that isn't reachable via the
            // _transport field yet; UdpTransport's constructor never blocks (UDP has no
            // connection to establish), so there's nothing there for a token to interrupt.
            ZkTransportKind.Tcp => new TcpTransport(_ip, _port, _timeoutMs, _cancellationToken),
            ZkTransportKind.Udp => new UdpTransport(_ip, _port, _timeoutMs),
            _ => throw new ArgumentOutOfRangeException(nameof(_transportKind))
        };

        _replyId = ushort.MaxValue - 1; // 65534
        SessionId = 0;

        var (command, _, sessionId, _) = ZkProtocol.ParseHeader(SendAndReceive(ZkProtocol.CMD_CONNECT, []));

        if (command == ZkProtocol.CMD_ACK_UNAUTH)
        {
            SessionId = sessionId;
            byte[] key = ZkProtocol.MakeCommKey(_commKey, sessionId);
            var (authCmd, _, _, _) = ZkProtocol.ParseHeader(SendAndReceive(ZkProtocol.CMD_AUTH, key));
            if (authCmd != ZkProtocol.CMD_ACK_OK)
                throw new InvalidOperationException(
                    "Device requires a communication password and authentication failed. " +
                    "Check the comm key configured on the device (Menu > Comm > Ethernet/Comm Key).");
        }
        else if (command != ZkProtocol.CMD_ACK_OK)
        {
            throw new InvalidOperationException($"Device rejected the connection (reply code {command}).");
        }

        SessionId = sessionId;
        IsConnected = true;
    }

    public void Disconnect()
    {
        if (!IsConnected) return;
        try
        {
            SendAndReceive(ZkProtocol.CMD_EXIT, []);
        }
        catch
        {
            // Best-effort: we still consider the session over even if the final ack is lost.
        }
        finally
        {
            IsConnected = false;
        }
    }

    /// <summary>Reads the device's current date/time via CMD_GET_TIME.</summary>
    public DateTime GetDeviceTime()
    {
        EnsureConnected();
        byte[] response = SendAndReceive(ZkProtocol.CMD_GET_TIME, []);
        var (command, _, _, _) = ZkProtocol.ParseHeader(response);
        if (command != ZkProtocol.CMD_ACK_OK)
            throw new InvalidOperationException($"Failed to read device time (reply code {command}).");

        uint packed = BitConverter.ToUInt32(response, 8);
        return ZkProtocol.DecodeTime(packed);
    }

    /// <summary>
    /// Reads a single device parameter such as "~SerialNumber", "~Platform",
    /// "FirmVer", or "~DeviceName" via CMD_OPTIONS_RRQ. Supported parameter names
    /// can vary slightly by firmware; this covers the common ones.
    /// </summary>
    public string GetDeviceParam(string paramName)
    {
        EnsureConnected();
        byte[] payload = Encoding.ASCII.GetBytes(paramName + "\0");
        byte[] response = SendAndReceive(ZkProtocol.CMD_OPTIONS_RRQ, payload);
        var (command, _, _, _) = ZkProtocol.ParseHeader(response);
        if (command != ZkProtocol.CMD_ACK_OK)
            return $"<unavailable, reply code {command}>";

        string text = Encoding.ASCII.GetString(response, 8, response.Length - 8).TrimEnd('\0');
        int eq = text.IndexOf('=');
        return eq >= 0 ? text[(eq + 1)..] : text;
    }

    /// <summary>
    /// Reads the device's attendance log. This can be a lot of data (thousands of
    /// records), so unlike GetDeviceTime/GetDeviceParam it goes through the
    /// bulk-data transfer flow: the device first announces a total size via
    /// CMD_PREPARE_DATA, then streams the payload as a raw byte sequence before
    /// a closing CMD_ACK_OK.
    /// </summary>
    public IReadOnlyList<ZkProtocol.AttendanceRecord> GetAttendanceLogs(CancellationToken cancellationToken = default)
    {
        byte[] raw = GetAttendanceLogsRaw(cancellationToken);
        return ZkProtocol.ParseAttendanceLogs(raw);
    }

    /// <summary>
    /// Same bulk transfer as GetAttendanceLogs, but returns the raw bytes
    /// before any record parsing is applied. Useful for diagnosing
    /// record-layout/size mismatches: if decoded records look garbled, dump
    /// this to a file and inspect the actual byte structure directly rather
    /// than reverse-engineering it from corrupted text output.
    /// </summary>
    public byte[] GetAttendanceLogsRaw(CancellationToken cancellationToken = default)
    {
        EnsureConnected();
        return ReadBufferedData(ZkProtocol.CMD_ATTLOG_RRQ, cancellationToken);
    }

    /// <summary>
    /// Reads a bulk-data reply. CONFIRMED FROM A RAW CAPTURE (see the
    /// diagnostic notes below) that the previous assumption here was wrong:
    /// this firmware does NOT stream unframed raw bytes after the initial
    /// CMD_PREPARE_DATA reply. Instead, every ~64KB it sends a brand-new,
    /// fully-framed packet of its own - full TCP magic+length wrapper (or,
    /// over UDP, its own datagram) plus an 8-byte ZK header with
    /// command == CMD_DATA - followed by that chunk's payload, repeating
    /// until the announced total size has been delivered and a final
    /// CMD_ACK_OK closes the transfer.
    ///
    /// Reading this as a raw continuation (ITransport.ReadRaw), the old
    /// approach, captures each chunk's 16 bytes of framing/header as if they
    /// were attendance-record bytes. Since a full chunk's payload (65528
    /// bytes) is not a whole multiple of the 40-byte record size, each chunk
    /// boundary lands mid-record, so every record after the first boundary
    /// is shifted - and the shift compounds at every subsequent boundary.
    /// That's why no single fixed (offset, record-size) pair can ever
    /// describe a raw dump captured with the old code: the corruption is
    /// injected repeatedly through the buffer, not just once at the start.
    ///
    /// The fix: read and parse each subsequent packet as a real framed
    /// packet (via ITransport.Receive, which already strips TCP framing /
    /// returns the raw UDP datagram) and only append the bytes after its
    /// own 8-byte ZK header.
    ///
    /// cancellationToken is checked once per chunk, at the top of the loop below, as a
    /// cheap early exit between reads. The actual, stronger mechanism for interrupting a
    /// read already blocked mid-call is the registration set up in ZkDevice's constructor,
    /// which closes _transport out from under it (see that constructor's doc comment) --
    /// this per-chunk check just avoids relying on that (an exception from a forcibly
    /// closed socket) for the common case where cancellation lands cleanly between two
    /// chunks instead. Worth having either way: a full attendance log has no server-side
    /// date filter (see GetAttendanceLogs's own doc comment), so it can be thousands of
    /// records across many chunks.
    /// </summary>
    private byte[] ReadBufferedData(ushort requestCommand, CancellationToken cancellationToken)
    {
        byte[] response = SendAndReceive(requestCommand, []);
        var (command, _, _, _) = ZkProtocol.ParseHeader(response);

        if (command == ZkProtocol.CMD_ACK_ERROR)
            throw new InvalidOperationException("Device reported an error preparing the data (reply code 2001). " +
                "This can happen if the log is empty on some firmware.");

        // A bare CMD_ACK_OK with no size payload attached means "no data"
        // (e.g. an empty attendance log short-circuits the whole handshake).
        if (command == ZkProtocol.CMD_ACK_OK && response.Length < 12)
            return [];

        if (command != ZkProtocol.CMD_PREPARE_DATA && command != ZkProtocol.CMD_DATA && command != ZkProtocol.CMD_ACK_OK)
            throw new InvalidOperationException($"Device rejected the data request (reply code {command}).");

        if (response.Length < 12)
            return [];

        uint totalSize = BitConverter.ToUInt32(response, 8);
        if (totalSize == 0 || totalSize == uint.MaxValue)
            return [];

        var buffer = new List<byte>((int)Math.Min(totalSize, int.MaxValue));

        // Anything past the 4-byte size field that arrived in this same
        // initial response is already the start (or, for small transfers,
        // the entirety) of the real payload.
        if (response.Length > 12)
            buffer.AddRange(response[12..]);

        // The device pads/rounds some replies, so never keep more than the
        // total size it actually announced.
        if (buffer.Count > totalSize)
            buffer.RemoveRange((int)totalSize, buffer.Count - (int)totalSize);

        // Only read further if the initial response didn't already contain
        // everything. Each subsequent read is a fully-framed packet in its
        // own right, not a raw continuation of the byte stream - see the
        // doc comment above for why that distinction matters.
        bool sawClosingAck = false;
        while (buffer.Count < totalSize)
        {
            cancellationToken.ThrowIfCancellationRequested();

            byte[] packet = _transport!.Receive();
            if (packet.Length < 8)
                throw new InvalidOperationException(
                    "Received a malformed (too short) chunk packet while reading bulk data.");

            var (chunkCommand, _, _, _) = ZkProtocol.ParseHeader(packet);

            if (chunkCommand == ZkProtocol.CMD_DATA || chunkCommand == ZkProtocol.CMD_PREPARE_DATA)
            {
                byte[] payload = packet[8..];
                int remaining = (int)totalSize - buffer.Count;
                buffer.AddRange(payload.Length > remaining ? payload[..remaining] : payload);
            }
            else if (chunkCommand == ZkProtocol.CMD_ACK_OK)
            {
                // The device closed the transfer (possibly short of the
                // announced total, on some firmware) - stop here rather
                // than block waiting for bytes that aren't coming.
                sawClosingAck = true;
                break;
            }
            else
            {
                throw new InvalidOperationException(
                    $"Unexpected packet (reply code {chunkCommand}) while reading bulk data.");
            }
        }

        // Consume the closing ack if the loop above didn't already see it,
        // and release the device's buffer. Neither step is fatal if the
        // firmware skips or already auto-frees it.
        if (!sawClosingAck)
        {
            try { _transport!.Receive(); } catch { /* some firmware doesn't send one */ }
        }
        try { SendAndReceive(ZkProtocol.CMD_FREE_DATA, []); } catch { /* best-effort cleanup */ }

        return [.. buffer];
    }

    private byte[] SendAndReceive(ushort command, byte[] payload)
    {
        if (_transport is null)
            throw new InvalidOperationException("Not connected. Call Connect() first.");

        byte[] packet = ZkProtocol.BuildPacket(command, SessionId, _replyId, payload);
        byte[] response = _transport.Send(packet);

        if (response.Length < 8)
            throw new InvalidOperationException("Received a malformed (too short) response from the device.");

        // Adopt the reply number the device echoed back as the basis for the next
        // request's reply id - this is what BuildPacket expects as its "replyId" input.
        var (_, _, _, echoedReplyId) = ZkProtocol.ParseHeader(response);
        _replyId = echoedReplyId;

        return response;
    }

    private void EnsureConnected()
    {
        if (!IsConnected)
            throw new InvalidOperationException("Not connected. Call Connect() first.");
    }

    public void Dispose()
    {
        try
        {
            if (IsConnected) Disconnect();
        }
        finally
        {
            _transport?.Dispose();
            _cancellationRegistration.Dispose();
        }
    }
}