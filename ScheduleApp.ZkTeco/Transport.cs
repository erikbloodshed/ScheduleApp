using System.Net;
using System.Net.Sockets;

namespace ScheduleApp.ZkTeco;

/// <summary>
/// A transport sends one ZK protocol packet (8-byte header + payload, no TCP wrapper)
/// and returns the corresponding response packet (also unwrapped).
/// </summary>
internal interface ITransport : IDisposable
{
    byte[] Send(byte[] packet);

    /// <summary>
    /// Reads the next inbound packet without sending anything first. Used to
    /// read the single closing CMD_ACK_OK packet that terminates a bulk data
    /// transfer (e.g. attendance logs) - this final packet is fully framed
    /// like any other reply. It is NOT used for the data itself; see ReadRaw.
    /// </summary>
    byte[] Receive();

    /// <summary>
    /// Reads exactly <paramref name="count"/> raw bytes directly off the wire,
    /// with no ZK packet header and, for TCP, no magic/length wrapper either.
    ///
    /// NOTE: this is NOT used for bulk data transfers (attendance logs, etc.)
    /// against at least the MB560-VL - a raw capture showed the device
    /// actually DOES wrap every ~64KB chunk in its own fully-framed CMD_DATA
    /// packet (TCP magic+length wrapper plus its own 8-byte ZK header), so
    /// treating the transfer as one continuous unframed byte stream corrupts
    /// record alignment at every chunk boundary. ZkDevice.ReadBufferedData
    /// now reads each chunk via Receive() instead. This method is kept as a
    /// low-level primitive in case a firmware genuinely needs a raw
    /// continuation read, but it should not be assumed to be the norm.
    /// </summary>
    byte[] ReadRaw(int count);
}

/// <summary>
/// UDP transport. Many ZKTeco terminals accept plain UDP on port 4370 with no
/// extra framing - the packet is simply the datagram payload.
/// </summary>
internal sealed class UdpTransport : ITransport
{
    private readonly UdpClient _udp;
    private readonly IPEndPoint _endpoint;

    public UdpTransport(string ip, int port, int timeoutMs)
    {
        _udp = new UdpClient();
        _udp.Client.ReceiveTimeout = timeoutMs;
        _udp.Client.SendTimeout = timeoutMs;
        _endpoint = new IPEndPoint(IPAddress.Parse(ip), port);
    }

    public byte[] Send(byte[] packet)
    {
        try
        {
            _udp.Send(packet, packet.Length, _endpoint);
            return Receive();
        }
        catch (SocketException ex)
        {
            throw new TimeoutException(
                $"No UDP response from {_endpoint.Address}:{_endpoint.Port}. " +
                "The device may be configured for TCP-only communication, or the IP/port is wrong.", ex);
        }
    }

    public byte[] Receive()
    {
        try
        {
            IPEndPoint remote = new(IPAddress.Any, 0);
            return _udp.Receive(ref remote);
        }
        catch (SocketException ex)
        {
            throw new TimeoutException(
                $"No UDP response from {_endpoint.Address}:{_endpoint.Port}. " +
                "The device may be configured for TCP-only communication, or the IP/port is wrong.", ex);
        }
    }

    /// <summary>
    /// UDP datagrams are already "raw" (no TCP wrapper to strip), but a single
    /// datagram is capped well below most transfer sizes, so multiple
    /// datagrams may need to be concatenated to reach <paramref name="count"/>
    /// bytes. The device doesn't put a ZK header on these - just accumulate.
    /// </summary>
    public byte[] ReadRaw(int count)
    {
        var buffer = new List<byte>(count);
        while (buffer.Count < count)
        {
            buffer.AddRange(Receive());
        }

        // Some firmware pads the final datagram; trim to the exact count the
        // caller asked for so it lines up with the announced total size.
        return buffer.Count == count ? [.. buffer] : [.. buffer.Take(count)];
    }

    public void Dispose() => _udp.Dispose();
}

/// <summary>
/// TCP transport. Each packet is wrapped with an extra 8-byte prefix: the fixed
/// magic bytes 50 50 82 7D, followed by a little-endian uint32 giving the length
/// of the header+payload that follows. This framing has been confirmed against
/// real device packet captures used by the open-source pyzk project.
/// </summary>
internal sealed class TcpTransport : ITransport
{
    private static readonly byte[] Magic = [0x50, 0x50, 0x82, 0x7D];

    private readonly TcpClient _client;
    private readonly NetworkStream _stream;

    // Many ZKTeco terminals (this has been observed on the MB560-VL) are slow
    // to answer the very first TCP SYN after sitting idle - as if the very
    // first connection attempt in a while has to wake the device's embedded
    // network stack up. That first attempt can time out even though the
    // device is reachable and perfectly healthy, while a second attempt made
    // right after succeeds almost instantly. Without a retry here, that
    // first-attempt timeout surfaces to the user as a spurious connection
    // error, even though simply trying again (previously: closing and
    // reopening the whole app, which happens to also try again) would have
    // worked. One quiet retry absorbs this instead of bothering the user
    // with it.
    private const int MaxConnectAttempts = 2;

    public TcpTransport(string ip, int port, int timeoutMs, CancellationToken cancellationToken = default)
    {
        Exception lastError = new TimeoutException($"Could not open a TCP connection to {ip}:{port}.");

        for (int attempt = 1; attempt <= MaxConnectAttempts; attempt++)
        {
            var client = new TcpClient
            {
                ReceiveTimeout = timeoutMs,
                SendTimeout = timeoutMs
            };

            try
            {
                var connectTask = client.ConnectAsync(ip, port);
                if (!connectTask.Wait(timeoutMs, cancellationToken))
                {
                    throw new TimeoutException(
                        $"Could not open a TCP connection to {ip}:{port} within {timeoutMs} ms " +
                        $"(attempt {attempt} of {MaxConnectAttempts}).");
                }

                _client = client;
                _stream = client.GetStream();
                return;
            }
            catch (OperationCanceledException)
            {
                // A deliberate cancellation, not a flaky first-connection timeout --
                // propagate immediately rather than treating it as "attempt N failed,
                // try attempt N+1" (see MaxConnectAttempts's own doc comment for why
                // that retry normally exists; it isn't meant to apply here).
                client.Dispose();
                throw;
            }
            catch (AggregateException ex)
            {
                client.Dispose();
                lastError = ex.InnerException ?? ex;
            }
            catch (Exception ex)
            {
                client.Dispose();
                lastError = ex;
            }
        }

        throw lastError;
    }

    public byte[] Send(byte[] packet)
    {
        byte[] frame = new byte[8 + packet.Length];
        Magic.CopyTo(frame, 0);
        BitConverter.GetBytes((uint)packet.Length).CopyTo(frame, 4);
        packet.CopyTo(frame, 8);

        try
        {
            _stream.Write(frame, 0, frame.Length);
            return Receive();
        }
        catch (IOException ex)
        {
            throw new TimeoutException(
                "No TCP response from the device (connection closed or timed out). " +
                "The device may be configured for UDP-only communication.", ex);
        }
    }

    /// <summary>
    /// Reads one TCP-wrapped frame (8-byte magic+length prefix, then that many
    /// bytes of header+payload) without writing anything first. Used when the
    /// device streams multiple unsolicited packets after a single request, as
    /// it does for bulk data such as attendance logs.
    /// </summary>
    public byte[] Receive()
    {
        try
        {
            byte[] prefix = ReadExact(8);
            for (int i = 0; i < 4; i++)
            {
                if (prefix[i] != Magic[i])
                    throw new InvalidOperationException("Response did not start with the expected TCP magic bytes.");
            }

            uint length = BitConverter.ToUInt32(prefix, 4);
            return ReadExact((int)length);
        }
        catch (IOException ex)
        {
            throw new TimeoutException(
                "No TCP response from the device (connection closed or timed out). " +
                "The device may be configured for UDP-only communication.", ex);
        }
    }

    /// <summary>
    /// Reads raw payload bytes directly off the TCP stream - no magic bytes,
    /// no length prefix, no ZK packet header. This is what the device
    /// actually sends for the body of a bulk transfer: everything past the
    /// initial CMD_PREPARE_DATA reply is just a continuous byte stream until
    /// the announced total size is reached, followed by one final framed
    /// closing packet (read separately via Receive()).
    /// </summary>
    public byte[] ReadRaw(int count)
    {
        try
        {
            return ReadExact(count);
        }
        catch (IOException ex)
        {
            throw new TimeoutException(
                "Connection closed or timed out while reading bulk data from the device.", ex);
        }
    }

    private byte[] ReadExact(int count)
    {
        byte[] buf = new byte[count];
        int offset = 0;
        while (offset < count)
        {
            int read = _stream.Read(buf, offset, count - offset);
            if (read == 0)
                throw new IOException("Connection closed by device before the full response was received.");
            offset += read;
        }
        return buf;
    }

    public void Dispose()
    {
        _stream.Dispose();
        _client.Dispose();
    }
}