using System.Buffers.Binary;
using System.Text.Json;

namespace N64Studio;

static class Diagnostics
{
    static void Require(bool result, string message)
    {
        if (!result) throw new Exception(message);
    }

    public static void Run(string path, bool hardware)
    {
        try { File.WriteAllText(path, hardware ? Probe() : Test()); }
        catch (Exception ex) { Environment.ExitCode = 1; File.WriteAllText(path, ex.ToString()); }
    }

    static string Probe()
    {
        using var device = new Device(new WindowsUsb());
        string version = device.Version();
        Snapshot first = device.Read();
        Require(first.Matches(device.Read()), "Repeated hardware read differed.");
        SpinWait.SpinUntil(() => device.Input is not null, 2500);
        return $"{version}\nTwo hardware settings reads matched. Calibrated: {first.Calibrated}. Step: {first.Step}.\nHID: {device.Input?.ToString() ?? "no report"}\n";
    }

    static string Test()
    {
        var demo = new Demo();
        using var device = new Device(demo);
        Require(device.Version().Contains("demo"), "Version decode");
        Snapshot original = device.Read();
        Profile profile = Profile.Defaults() with { Gate = true, Threshold = .65f, CutoffHz = 325f };
        profile.Notches[3] = new(-72, 63, .21f);
        profile.Bindings[0] = 255;
        profile.Bindings[31] = 3;
        var json = new JsonSerializerOptions { RespectRequiredConstructorParameters = true };
        Profile restored = JsonSerializer.Deserialize<Profile>(JsonSerializer.Serialize(profile, json), json)!;
        Require(restored.Matches(profile), "JSON round trip");
        try { JsonSerializer.Deserialize<Profile>("{}", json); throw new Exception("Missing JSON fields accepted."); }
        catch (JsonException) { }
        demo.IgnoreNextGateWrite = true;
        try { device.Apply(profile, original); throw new Exception("Mismatch was accepted."); }
        catch (IOException ex) { Require(ex.Message.Contains("restored and verified"), ex.Message); }
        Require(original.Matches(device.Read()) && demo.Saves == 0, "Rollback or save isolation failed.");
        Snapshot applied = device.Apply(profile, original);
        Require(applied.Profile.Matches(profile) && applied.OtherBank.SequenceEqual(original.OtherBank), "Apply or bank preservation failed.");
        try { device.Save(original); throw new Exception("Stale snapshot saved."); }
        catch (IOException) { }
        Require(demo.Saves == 0, "Unexpected save.");
        device.Save(applied);
        Require(demo.Saves == 1, "Save request missing.");
        Snapshot step = device.Calibrate(1, 0);
        step = device.Calibrate(2, step.Step);
        step = device.Calibrate(3, step.Step);
        for (int i = 1; i <= 16; i++) step = device.Calibrate(2, i);
        Require(step.Step == 0 && step.Calibrated, "Calibration did not complete.");
        Require(!device.Reset().Calibrated, "Reset did not clear calibration.");
        byte[] report = new byte[20];
        report[0] = 4; report[1] = unchecked((byte)-85); report[2] = 70;
        BinaryPrimitives.WriteUInt32LittleEndian(report.AsSpan(8), 0x20001);
        BinaryPrimitives.WriteSingleLittleEndian(report.AsSpan(12), .5f);
        BinaryPrimitives.WriteSingleLittleEndian(report.AsSpan(16), -.25f);
        Require(InputFrame.Decode(report) is { X: -85, Y: -70, RawX: .5f, RawY: -.25f, Buttons: 0x20001 }, "HID decode");
        Require(InputFrame.Decode(report[..19]) is null, "Truncated HID accepted.");
        return "PASS: version, profile round trip and required fields, apply, readback mismatch and rollback, remap table preservation, stale save rejection, save request, 16-step calibration and undo, reset, HID parsing.\n";
    }
}
