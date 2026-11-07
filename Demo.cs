using System.Buffers.Binary;

namespace N64Studio;

sealed class Demo : ITransport
{
    Profile state = Profile.Defaults();
    readonly byte[] other = Enumerable.Repeat((byte)7, 32).ToArray();
    byte[]? reply;
    bool calibrated;
    int step;
    public int Saves { get; private set; }
    public bool IgnoreNextGateWrite { get; set; }
    public InputFrame Input
    {
        get
        {
            long now = Environment.TickCount64;
            double t = now / 950.0;
            return new((int)(75*Math.Cos(t)), (int)(75*Math.Sin(t)),
                .53f*(float)Math.Cos(t+.23), .53f*(float)Math.Sin(t+.23), 1u << ((int)(t % 14)), now);
        }
    }
    public void Send(byte[] data)
    {
        if (reply is not null) throw new IOException("Unconsumed demo reply.");
        byte command = data[0];
        if (command >= 0xA0 && command < 0xF0 || command is 2 or 3)
        {
            reply = new byte[64];
            reply[0] = command;
        }
        switch (command)
        {
            case 0xA1: reply![2] = 1; "demo"u8.CopyTo(reply.AsSpan(3)); break;
            case 0xA2: reply![1] = calibrated ? (byte)1 : (byte)0; reply[2] = (byte)step; break;
            case 0xA4:
                for (int i = 0; i < 8; i++)
                {
                    Notch n = state.Notches[i];
                    reply![1+6*i] = unchecked((byte)n.X);
                    reply[2+6*i] = unchecked((byte)n.Y);
                    BinaryPrimitives.WriteSingleLittleEndian(reply.AsSpan(3+6*i), n.Radians);
                }
                break;
            case 0xA5:
                reply![1] = data[1];
                (data[1] == 0 ? state.Bindings.Select(b => (byte)b).ToArray() : other).CopyTo(reply, 2);
                break;
            case 0xA6: BinaryPrimitives.WriteSingleLittleEndian(reply.AsSpan(4), state.Threshold); break;
            case 0xA7: reply![1] = state.Gate ? (byte)1 : (byte)0; break;
            case 0xA8: BinaryPrimitives.WriteSingleLittleEndian(reply.AsSpan(4), state.CutoffHz); break;
            case 1: step = 1; break;
            case 2: if (++step == 17) { step = 0; calibrated = true; } break;
            case 3: step--; break;
            case 4: state.Notches[data[1]] = new((sbyte)data[2], (sbyte)data[3], Device.ReadFloat(data, 4)); break;
            case 5:
                if (data[1] == 0) state.Bindings[data[2]] = data[3];
                other[data[2]] = data[3];
                break;
            case 6: state = state with { Threshold = Device.ReadFloat(data, 4) }; break;
            case 7:
                if (!IgnoreNextGateWrite) state = state with { Gate = data[1] != 0 };
                IgnoreNextGateWrite = false;
                break;
            case 8: state = state with { CutoffHz = Device.ReadFloat(data, 4) }; break;
            case 0xF1: break;
            case 0xF2: Saves++; break;
            case 0xF3: state = Profile.Defaults(); Array.Clear(other); calibrated = false; break;
            default: throw new IOException("Unknown demo command.");
        }
    }

    public byte[] Receive()
    {
        byte[] result = reply ?? throw new IOException("No demo response.");
        reply = null;
        return result;
    }
    public void Dispose() { }
}
