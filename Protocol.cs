using System.Buffers.Binary;
using System.Text;

namespace N64Studio;

record Notch(int X, int Y, float Radians);
record Profile(int Schema, Notch[] Notches, float Threshold, float CutoffHz, bool Gate, int[] Bindings)
{
    public static readonly string[] Directions = ["Right", "Up-right", "Up", "Up-left", "Left", "Down-left", "Down", "Down-right"];
    public static readonly string[] Buttons = ["A", "B", "C Up", "C Down", "C Left", "C Right", "Start", "L", "R", "Z", "D Down", "D Left", "D Right", "D Up", "GPIO 3", "GPIO 4", "GPIO 5", "GPIO 6", "GPIO 7", "GPIO 8", "GPIO 9", "GPIO 10"];
    public static Profile Defaults() => new(1,
        [new(85,0,0), new(70,70,0), new(0,85,0), new(-70,70,0), new(-85,0,0), new(-70,-70,0), new(0,-85,0), new(70,-70,0)],
        .8f, 450, false, new int[32]);

    public void Validate()
    {
        if (Schema != 1 || Notches?.Length != 8 || Bindings?.Length != 32 ||
            Notches.Any(n => n is null || n.X is < -128 or > 127 || n.Y is < -128 or > 127 ||
                !float.IsFinite(n.Radians) || n.Radians is < 0 or > MathF.PI) ||
            !float.IsFinite(Threshold) || Threshold is < 0 or > 1 ||
            !float.IsFinite(CutoffHz) || CutoffHz < 0 ||
            Bindings.Any(b => b is not (>= 0 and <= 32 or 255)))
            throw new InvalidDataException("Invalid profile. Check the schema, notch values, filter settings and mappings.");
    }

    public bool Matches(Profile other) => Threshold == other.Threshold && CutoffHz == other.CutoffHz &&
        Gate == other.Gate && Notches.SequenceEqual(other.Notches) && Bindings.SequenceEqual(other.Bindings);
}

interface ITransport : IDisposable
{
    void Send(byte[] bytes);
    byte[] Receive();
    InputFrame? Input { get; }
}

record InputFrame(int X, int Y, float RawX, float RawY, uint Buttons, long Time)
{
    public static InputFrame? Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length < 20 || data[0] != 4) return null;
        float x = BinaryPrimitives.ReadSingleLittleEndian(data[12..]);
        float y = BinaryPrimitives.ReadSingleLittleEndian(data[16..]);
        if (!float.IsFinite(x) || !float.IsFinite(y)) return null;
        return new((sbyte)data[1], -(sbyte)data[2], x, y,
            BinaryPrimitives.ReadUInt32LittleEndian(data[8..]), Environment.TickCount64);
    }
}

record Snapshot(Profile Profile, byte[] OtherBank, bool Calibrated, int Step)
{
    public bool Matches(Snapshot other) => Profile.Matches(other.Profile) &&
        OtherBank.SequenceEqual(other.OtherBank) && Calibrated == other.Calibrated && Step == other.Step;
}

sealed class Device(ITransport link) : IDisposable
{
    public InputFrame? Input => link.Input;

    byte[] Request(byte command, int length, params byte[] arguments)
    {
        link.Send([command, ..arguments]);
        // Ignore USB zero-length packets and queued responses from an earlier session.
        for (int attempt = 0; attempt < 6; attempt++)
        {
            byte[] result = link.Receive();
            if (result.Length == 0 || result[0] != command) continue;
            if (command == 0xA5 && result.Length >= 2 && result[1] != arguments[0]) continue;
            if (result.Length >= length) return result;
            break;
        }
        throw new IOException($"Unexpected response to USB command 0x{command:X2}. Reconnect and read the device.");
    }

    public string Version()
    {
        byte[] reply = Request(0xA1, 3);
        string commit = Encoding.ASCII.GetString(reply.AsSpan(3)).Split('\0')[0];
        string version = $"v{reply[2]}.{reply[1] >> 4}.{reply[1] & 15}";
        return commit.Length == 0 || commit == version ? version : $"{version}  •  {commit}";
    }

    public (bool Calibrated, int Step) Calibration()
    {
        byte[] status = Request(0xA2, 3);
        if (status[1] > 1 || status[2] > 16) throw new IOException("Unsupported calibration status.");
        return (status[1] != 0, status[2]);
    }

    byte[] ReadBank(byte bank)
    {
        byte[] response = Request(0xA5, 34, bank);
        if (response[1] != bank) throw new IOException("Unexpected remap table.");
        return response[2..34];
    }

    public Snapshot Read()
    {
        var (calibrated, step) = Calibration();
        byte[] packet = Request(0xA4, 49);
        Notch[] notches = Enumerable.Range(0, 8).Select(i => new Notch(
            (sbyte)packet[1 + 6*i], (sbyte)packet[2 + 6*i], ReadFloat(packet, 3 + 6*i))).ToArray();
        float threshold = ReadFloat(Request(0xA6, 8), 4);
        byte gate = Request(0xA7, 2)[1];
        if (gate > 1) throw new IOException("Unexpected gate limiter value.");
        float cutoff = ReadFloat(Request(0xA8, 8), 4);
        var profile = new Profile(1, notches, threshold, cutoff, gate == 1,
            ReadBank(0).Select(b => (int)b).ToArray());
        profile.Validate();
        return new(profile, ReadBank(1), calibrated, step);
    }

    public Snapshot Apply(Profile draft, Snapshot expected)
    {
        draft.Validate();
        Snapshot before = Read();
        if (!before.Matches(expected) || before.Step != 0)
            throw new InvalidOperationException("Device settings changed or calibration is active. Read the device before applying.");
        var desired = before with { Profile = draft };
        try
        {
            WriteChanges(before, desired);
            Snapshot after = Read();
            if (!after.Matches(desired)) throw new IOException("Device readback differed from the draft.");
            return after;
        }
        catch (Exception error)
        {
            try
            {
                Snapshot actual = Read();
                if (actual.Step != 0) throw new IOException("Calibration is active.");
                WriteChanges(actual, before);
                if (!Read().Matches(before)) throw new IOException("Restore readback differed.");
            }
            catch (Exception restoreError)
            {
                throw new IOException($"Apply failed and restore could not be verified. Reconnect and read the device. {error.Message} {restoreError.Message}", error);
            }
            throw new IOException($"Apply failed. The previous settings were restored and verified. {error.Message}", error);
        }
    }

    void WriteChanges(Snapshot current, Snapshot desired)
    {
        Profile from = current.Profile, to = desired.Profile;
        for (byte i = 0; i < 8; i++)
        {
            Notch n = to.Notches[i];
            if (n != from.Notches[i])
                link.Send(FloatPacket(0x04, n.Radians, i, unchecked((byte)n.X), unchecked((byte)n.Y)));
        }
        for (byte i = 0; i < 32; i++)
        {
            bool n64Changed = from.Bindings[i] != to.Bindings[i];
            if (n64Changed) link.Send([0x05, 0, i, (byte)to.Bindings[i]]);
            // Reviewed firmware falls through from bank 0 into bank 1.
            if (n64Changed || current.OtherBank[i] != desired.OtherBank[i])
                link.Send([0x05, 1, i, desired.OtherBank[i]]);
        }
        if (from.Threshold != to.Threshold) link.Send(FloatPacket(0x06, to.Threshold));
        if (from.Gate != to.Gate) link.Send([0x07, to.Gate ? (byte)1 : (byte)0]);
        if (from.CutoffHz != to.CutoffHz) link.Send(FloatPacket(0x08, to.CutoffHz));
    }

    public Snapshot Calibrate(byte command, int currentStep)
    {
        if (Calibration().Step != currentStep) throw new InvalidOperationException("Calibration step changed. Read the device.");
        int target = command switch
        {
            1 when currentStep == 0 => 1,
            2 when currentStep is >= 1 and < 16 => currentStep + 1,
            2 when currentStep == 16 => 0,
            3 when currentStep > 1 => currentStep - 1,
            _ => throw new InvalidOperationException("Calibration action is unavailable.")
        };
        if (command == 1) link.Send([command]);
        else Request(command, 1);
        for (int i = 0; i < 25; i++)
        {
            Thread.Sleep(60);
            var status = Calibration();
            if (status.Step == target && (target != 0 || status.Calibrated)) return Read();
        }
        throw new IOException("Calibration did not reach the expected step. Read the device before continuing.");
    }

    public void Save(Snapshot expected)
    {
        if (expected.Step != 0 || !Read().Matches(expected))
            throw new IOException("Device state changed. Read the device before saving.");
        link.Send([0xF2]);
        Thread.Sleep(400);
        if (!Read().Matches(expected)) throw new IOException("Settings readback changed after the save request.");
    }

    public Snapshot Reset()
    {
        if (Calibration().Step != 0) throw new InvalidOperationException("Finish calibration before resetting.");
        link.Send([0xF3]);
        return Read();
    }

    public void Bootloader() => link.Send([0xF1]);
    public void Dispose() => link.Dispose();

    internal static float ReadFloat(byte[] data, int offset) => BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(offset));
    internal static byte[] FloatPacket(byte command, float value, byte a = 0, byte b = 0, byte c = 0)
    {
        byte[] packet = [command, a, b, c, 0, 0, 0, 0];
        BinaryPrimitives.WriteSingleLittleEndian(packet.AsSpan(4), value);
        return packet;
    }
}



