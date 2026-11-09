// IHaxSdk on top of HAX's updated card_7k.dll (2.1.2.0 delivery, hax_7k_sdk.h): the five Device_* exports, all __stdcall with
// undecorated names, 32-bit. The old card_7k.dll has other Device_* exports (Device_Init, Device_StartScan, ...)
// but none of these five, so each name is looked up exactly.
using System.Runtime.InteropServices;

namespace GateTcpTest;

sealed unsafe class NativeHaxSdk : IHaxSdk
{
    public const string MissingFunctionsMessage = "card_7k.dll has no Device_* functions. You need HAX's updated DLL.";

    static readonly string[] Exports =
    {
        "Device_PackServerControl", "Device_UnPackServerControl", "Device_PackDeviceAlarm",
        "Device_UnPackDeviceAlarm", "Device_UnPackEpcId",
    };

    readonly delegate* unmanaged[Stdcall]<int, byte*, int, int*, int> packServerControl;
    readonly delegate* unmanaged[Stdcall]<byte*, int, int> unpackServerControl;
    readonly delegate* unmanaged[Stdcall]<DeviceAlarmConfig*, byte*, int, int*, int> packDeviceAlarm;
    readonly delegate* unmanaged[Stdcall]<byte*, int, int> unpackDeviceAlarm;
    readonly delegate* unmanaged[Stdcall]<byte*, int, byte*, int, int*, int> unpackEpcId;

    // Several gates run at once, and HAX does not say whether the DLL is thread-safe, so it gets one call at a time.
    // Each call only packs or parses a few hundred bytes, so this costs nothing.
    readonly object sync = new();

    NativeHaxSdk(IntPtr[] f)
    {
        packServerControl = (delegate* unmanaged[Stdcall]<int, byte*, int, int*, int>)f[0];
        unpackServerControl = (delegate* unmanaged[Stdcall]<byte*, int, int>)f[1];
        packDeviceAlarm = (delegate* unmanaged[Stdcall]<DeviceAlarmConfig*, byte*, int, int*, int>)f[2];
        unpackDeviceAlarm = (delegate* unmanaged[Stdcall]<byte*, int, int>)f[3];
        unpackEpcId = (delegate* unmanaged[Stdcall]<byte*, int, byte*, int, int*, int>)f[4];
    }

    // library is the handle from NativeLibrary.Load. Null when any of the five exports is missing.
    public static NativeHaxSdk? TryCreate(IntPtr library)
    {
        var functions = new IntPtr[Exports.Length];
        for (int i = 0; i < Exports.Length; i++)
            if (!NativeLibrary.TryGetExport(library, Exports[i], out functions[i])) return null;
        return new NativeHaxSdk(functions);
    }

    public int PackServerControl(int value, Span<byte> outBuf, out int outLen)
    {
        int len = 0, rc;
        lock (sync)
            fixed (byte* p = outBuf) rc = packServerControl(value, p, outBuf.Length, &len);
        outLen = len;
        return rc;
    }

    public int PackDeviceAlarm(DeviceAlarmConfig config, Span<byte> outBuf, out int outLen)
    {
        int len = 0, rc;
        lock (sync)
            fixed (byte* p = outBuf) rc = packDeviceAlarm(&config, p, outBuf.Length, &len);
        outLen = len;
        return rc;
    }

    public int UnPackServerControl(ReadOnlySpan<byte> reply) => Unpack(unpackServerControl, reply);

    public int UnPackDeviceAlarm(ReadOnlySpan<byte> reply) => Unpack(unpackDeviceAlarm, reply);

    // The header takes a non-const buffer ("decrypts ... buf"), so the DLL may decrypt in place. It always gets a
    // copy: the caller's bytes stay as received, which the buffer-too-small retry of UnPackEpcId relies on.
    int Unpack(delegate* unmanaged[Stdcall]<byte*, int, int> fn, ReadOnlySpan<byte> data)
    {
        byte[] copy = data.ToArray();
        lock (sync)
            fixed (byte* p = copy) return fn(p, copy.Length);
    }

    public int UnPackEpcId(ReadOnlySpan<byte> packet, Span<byte> jsonBuf, out int jsonLen)
    {
        byte[] copy = packet.ToArray();
        int len = 0, rc;
        lock (sync)
            fixed (byte* p = copy)
            fixed (byte* j = jsonBuf)
                rc = unpackEpcId(p, copy.Length, j, jsonBuf.Length, &len);
        jsonLen = len;
        return rc;
    }
}
