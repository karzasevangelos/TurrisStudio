using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TurrisStudio.Core.Usb;

public delegate (int consumed, bool done, T? value) BufferParser<T>(ReadOnlySpan<byte> buffer);

/// <summary>A TRYX display found on the USB printer-class interface.</summary>
public sealed record UsbPrinterDevice(string DevicePath, ushort VendorId, ushort ProductId)
{
    public bool IsTurris => ProductId == UsbPrinterTransport.TurrisProductId;
    public Protocol.DeviceModel? Model => Protocol.DeviceModel.ForProductId(ProductId);
    public bool IsSupported => Model != null;
}

/// <summary>
/// Raw bidirectional byte pipe to the display through the Windows usbprint.sys
/// driver: WriteFile goes to the bulk OUT endpoint, ReadFile reads bulk IN.
/// No driver replacement (Zadig/WinUSB) is needed.
/// </summary>
public sealed class UsbPrinterTransport : IDisposable
{
    public const ushort TryxVendorId = 0x391A;
    public const ushort TurrisProductId = 0x2011;

    // GUID_DEVINTERFACE_USBPRINT
    private static readonly Guid UsbPrintInterface = new("28d78fad-5a12-11d1-ae5b-0000f803a8c2");

    private readonly SafeFileHandle _handle;
    private readonly FileStream _stream;
    private readonly CancellationTokenSource _readerCts = new();
    private readonly Task _readerTask;
    private readonly object _bufferLock = new();
    private readonly SemaphoreSlim _dataAvailable = new(0, int.MaxValue);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private byte[] _buffer = new byte[256 * 1024];
    private int _bufferLength;
    private volatile Exception? _readerFault;
    private bool _disposed;

    public string DevicePath { get; }
    public bool IsFaulted => _readerFault != null;

    private UsbPrinterTransport(string devicePath, SafeFileHandle handle)
    {
        DevicePath = devicePath;
        _handle = handle;
        _stream = new FileStream(handle, FileAccess.ReadWrite, bufferSize: 0, isAsync: true);
        _readerTask = Task.Run(ReaderLoopAsync);
    }

    public static IReadOnlyList<UsbPrinterDevice> Discover()
    {
        var result = new List<UsbPrinterDevice>();
        foreach (var path in EnumerateInterfacePaths(UsbPrintInterface))
        {
            if (TryParseVidPid(path, out var vid, out var pid) && vid == TryxVendorId)
                result.Add(new UsbPrinterDevice(path, vid, pid));
        }
        return result;
    }

    public static UsbPrinterTransport Open(string devicePath)
    {
        var handle = Native.CreateFileW(devicePath,
            Native.GENERIC_READ | Native.GENERIC_WRITE,
            Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
            IntPtr.Zero, Native.OPEN_EXISTING, Native.FILE_FLAG_OVERLAPPED, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            var message = error switch
            {
                5 or 32 => "The display is in use by another program (close KANALI and try again).",
                2 or 3 => "The display was not found (unplugged?).",
                _ => new Win32Exception(error).Message,
            };
            throw new IOException(message, error);
        }
        return new UsbPrinterTransport(devicePath, handle);
    }

    public async Task WriteAsync(ReadOnlyMemory<byte> data, TimeSpan timeout, CancellationToken ct = default)
    {
        ThrowIfFaulted();
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            try
            {
                await _stream.WriteAsync(data, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"USB write of {data.Length} bytes timed out.");
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Discard everything received so far.</summary>
    public void DiscardInput()
    {
        lock (_bufferLock) _bufferLength = 0;
    }

    /// <summary>
    /// Lets a parser inspect the receive buffer. The callback returns how many
    /// bytes to consume from the front. Waits for new data until the deadline when
    /// the callback returns a negative value ("need more").
    /// </summary>
    public async Task<T?> ReadUntilAsync<T>(BufferParser<T> parser,
        TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            lock (_bufferLock)
            {
                while (true)
                {
                    var (consumed, done, value) = parser(new ReadOnlySpan<byte>(_buffer, 0, _bufferLength));
                    if (consumed > 0)
                    {
                        Buffer.BlockCopy(_buffer, consumed, _buffer, 0, _bufferLength - consumed);
                        _bufferLength -= consumed;
                    }
                    if (done) return value;
                    if (consumed <= 0) break;
                }
            }
            ThrowIfFaulted();
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) throw new TimeoutException("Timed out waiting for a response from the display.");
            await _dataAvailable.WaitAsync(remaining < TimeSpan.FromMilliseconds(250) ? remaining : TimeSpan.FromMilliseconds(250), ct)
                .ConfigureAwait(false);
        }
    }

    private async Task ReaderLoopAsync()
    {
        var chunk = new byte[64 * 1024];
        var ct = _readerCts.Token;
        var consecutiveErrors = 0;
        while (!ct.IsCancellationRequested)
        {
            int read;
            try
            {
                read = await _stream.ReadAsync(chunk, ct).ConfigureAwait(false);
                consecutiveErrors = 0;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // usbprint can fail an individual read (e.g. timeout); give up only when it keeps failing.
                if (++consecutiveErrors >= 20 || ex is ObjectDisposedException)
                {
                    _readerFault = ex;
                    _dataAvailable.Release();
                    break;
                }
                await Task.Delay(50, CancellationToken.None).ConfigureAwait(false);
                continue;
            }

            if (read <= 0)
            {
                await Task.Delay(2, CancellationToken.None).ConfigureAwait(false);
                continue;
            }
            lock (_bufferLock)
            {
                if (_bufferLength + read > _buffer.Length)
                {
                    var bigger = new byte[Math.Max(_buffer.Length * 2, _bufferLength + read)];
                    Buffer.BlockCopy(_buffer, 0, bigger, 0, _bufferLength);
                    _buffer = bigger;
                }
                Buffer.BlockCopy(chunk, 0, _buffer, _bufferLength, read);
                _bufferLength += read;
                // Bound the buffer: drop stale bytes nobody consumed.
                if (_bufferLength > 8 * 1024 * 1024)
                    _bufferLength = 0;
            }
            _dataAvailable.Release();
        }
    }

    private void ThrowIfFaulted()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_readerFault is { } fault)
            throw new IOException("The USB connection to the display was lost.", fault);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _readerCts.Cancel();
        try { Native.CancelIoEx(_handle, IntPtr.Zero); } catch { /* ignored */ }
        try { _readerTask.Wait(TimeSpan.FromSeconds(2)); } catch { /* ignored */ }
        _stream.Dispose();
        _handle.Dispose();
        _readerCts.Dispose();
    }

    private static bool TryParseVidPid(string path, out ushort vid, out ushort pid)
    {
        vid = pid = 0;
        var lower = path.ToLowerInvariant();
        var v = lower.IndexOf("vid_", StringComparison.Ordinal);
        var p = lower.IndexOf("pid_", StringComparison.Ordinal);
        if (v < 0 || p < 0 || v + 8 > lower.Length || p + 8 > lower.Length) return false;
        return ushort.TryParse(lower.AsSpan(v + 4, 4), System.Globalization.NumberStyles.HexNumber, null, out vid)
               && ushort.TryParse(lower.AsSpan(p + 4, 4), System.Globalization.NumberStyles.HexNumber, null, out pid);
    }

    private static IEnumerable<string> EnumerateInterfacePaths(Guid interfaceGuid)
    {
        var list = new List<string>();
        var set = Native.SetupDiGetClassDevsW(ref interfaceGuid, IntPtr.Zero, IntPtr.Zero,
            Native.DIGCF_PRESENT | Native.DIGCF_DEVICEINTERFACE);
        if (set == IntPtr.Zero || set == new IntPtr(-1)) return list;
        try
        {
            for (uint index = 0; ; index++)
            {
                var data = new Native.SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<Native.SP_DEVICE_INTERFACE_DATA>() };
                if (!Native.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref interfaceGuid, index, ref data)) break;

                Native.SetupDiGetDeviceInterfaceDetailW(set, ref data, IntPtr.Zero, 0, out var required, IntPtr.Zero);
                if (required <= 0) continue;
                var detail = Marshal.AllocHGlobal(required);
                try
                {
                    // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W: 8 on 64-bit, 6 on 32-bit.
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (Native.SetupDiGetDeviceInterfaceDetailW(set, ref data, detail, required, out _, IntPtr.Zero))
                    {
                        var path = Marshal.PtrToStringUni(detail + 4);
                        if (!string.IsNullOrEmpty(path)) list.Add(path);
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(detail);
                }
            }
        }
        finally
        {
            Native.SetupDiDestroyDeviceInfoList(set);
        }
        return list;
    }

    private static class Native
    {
        public const uint GENERIC_READ = 0x80000000;
        public const uint GENERIC_WRITE = 0x40000000;
        public const uint FILE_SHARE_READ = 1;
        public const uint FILE_SHARE_WRITE = 2;
        public const uint OPEN_EXISTING = 3;
        public const uint FILE_FLAG_OVERLAPPED = 0x40000000;
        public const int DIGCF_PRESENT = 0x2;
        public const int DIGCF_DEVICEINTERFACE = 0x10;

        [StructLayout(LayoutKind.Sequential)]
        public struct SP_DEVICE_INTERFACE_DATA
        {
            public int cbSize;
            public Guid InterfaceClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern SafeFileHandle CreateFileW(string fileName, uint access, uint share, IntPtr security,
            uint creation, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CancelIoEx(SafeFileHandle handle, IntPtr overlapped);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, IntPtr enumerator, IntPtr parent, int flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid guid, uint index,
            ref SP_DEVICE_INTERFACE_DATA data);

        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data,
            IntPtr detail, int detailSize, out int requiredSize, IntPtr devInfo);

        [DllImport("setupapi.dll", SetLastError = true)]
        public static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    }
}
