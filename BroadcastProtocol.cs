using System.Text;

namespace AccOverlay;

// ACC broadcast protocol v4 parsing (see ACC-Race-Control for the reference
// implementation). Strings = u16 LE length + UTF-8. All ints little-endian.
public static class BroadcastProtocol
{
    public const byte ProtocolVersion = 4;

    public static void WriteRegister(BinaryWriter w, string displayName, string connectionPassword, int intervalMs)
    {
        w.Write((byte)1); // REGISTER_COMMAND_APPLICATION
        w.Write(ProtocolVersion);
        WriteString(w, displayName);
        WriteString(w, connectionPassword);
        w.Write(intervalMs);
        WriteString(w, ""); // commandPassword
    }

    public static void WriteString(BinaryWriter w, string s)
    {
        var b = Encoding.UTF8.GetBytes(s);
        w.Write(Convert.ToUInt16(b.Length));
        w.Write(b);
    }

    public static string ReadString(BinaryReader r)
    {
        int len = r.ReadUInt16();
        return Encoding.UTF8.GetString(r.ReadBytes(len));
    }

    public static bool TryParseRegResult(byte[] data, out int id, out bool ok, out bool readOnly, out string err)
    {
        id = -1; ok = false; readOnly = true; err = "";
        try
        {
            using var ms = new MemoryStream(data, 1, data.Length - 1);
            using var r = new BinaryReader(ms);
            id = r.ReadInt32();
            ok = r.ReadByte() > 0;
            readOnly = r.ReadByte() == 0;
            err = ReadString(r);
            return true;
        }
        catch { return false; }
    }

    public static bool TryParseRealtime(byte[] data, out float sessMs, out bool replay, out float replayT, out float rem)
    {
        sessMs = 0; replay = false; replayT = -1; rem = 0;
        try
        {
            using var ms = new MemoryStream(data, 1, data.Length - 1);
            using var r = new BinaryReader(ms);
            r.ReadUInt16(); r.ReadUInt16(); r.ReadByte(); r.ReadByte();
            sessMs = r.ReadSingle();
            r.ReadSingle();
            r.ReadInt32();
            ReadString(r); ReadString(r); ReadString(r);
            replay = r.ReadByte() > 0;
            if (replay) { replayT = r.ReadSingle(); rem = r.ReadSingle(); }
            return true;
        }
        catch { return false; }
    }

    public static bool TryParseCar(byte[] data, out int car, out int kmh, out int gear, out int laps, out int? curLapMs)
    {
        car = 0; kmh = 0; gear = 0; laps = 0; curLapMs = null;
        try
        {
            using var ms = new MemoryStream(data, 1, data.Length - 1);
            using var r = new BinaryReader(ms);
            car = r.ReadUInt16(); r.ReadUInt16(); r.ReadByte(); // car, driver, driverCount
            gear = r.ReadByte() - 2;
            r.ReadSingle(); r.ReadSingle(); r.ReadSingle(); r.ReadByte();
            kmh = r.ReadUInt16();
            r.ReadUInt16(); r.ReadUInt16(); r.ReadUInt16(); r.ReadSingle();
            laps = r.ReadUInt16();
            r.ReadInt32();
            SkipLap(r); SkipLap(r);
            int m = r.ReadInt32();
            SkipLapRest(r);
            curLapMs = m == int.MaxValue ? null : m;
            return true;
        }
        catch { return false; }
    }

    private static void SkipLap(BinaryReader r)
    {
        r.ReadInt32(); r.ReadUInt16(); r.ReadUInt16();
        int n = r.ReadByte();
        for (int i = 0; i < n; i++) r.ReadInt32();
        r.ReadByte(); r.ReadByte(); r.ReadByte(); r.ReadByte();
    }

    private static void SkipLapRest(BinaryReader r)
    {
        r.ReadUInt16(); r.ReadUInt16();
        int n = r.ReadByte();
        for (int i = 0; i < n; i++) r.ReadInt32();
        r.ReadByte(); r.ReadByte(); r.ReadByte(); r.ReadByte();
    }
}
