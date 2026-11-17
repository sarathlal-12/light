using System.IO;
using System.Media;

namespace DotLight;

/// <summary>A soft two-note chime, made in memory (no sound files to ship).</summary>
static class Chime
{
    static byte[]? wav;

    public static void Play()
    {
        if (Environment.GetEnvironmentVariable("LIGHT_TEST") != null) return; // silent while testing
        try
        {
            wav ??= Make();
            var player = new SoundPlayer(new MemoryStream(wav));
            player.Play(); // async, returns at once
        }
        catch { /* no audio device: the flash still shows */ }
    }

    static byte[] Make()
    {
        const int rate = 44100;
        // two gentle bell tones, the second a fifth above and a little later
        (double freq, double start, double gain)[] notes = { (880, 0, 0.32), (1318.5, 0.16, 0.26) };
        int samples = (int)(rate * 1.6);
        var data = new short[samples];
        for (int i = 0; i < samples; i++)
        {
            double t = (double)i / rate, v = 0;
            foreach (var (f, s, g) in notes)
            {
                double u = t - s;
                if (u < 0) continue;
                double attack = Math.Min(1, u / 0.012);
                double env = attack * Math.Exp(-u * 3.2);
                v += g * env * (Math.Sin(2 * Math.PI * f * u) + 0.25 * Math.Sin(2 * Math.PI * f * 2 * u) * Math.Exp(-u * 6));
            }
            data[i] = (short)(Math.Clamp(v, -1, 1) * short.MaxValue * 0.8);
        }

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        int bytes = samples * 2;
        w.Write("RIFF"u8); w.Write(36 + bytes); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(bytes);
        foreach (var s in data) w.Write(s);
        w.Flush();
        return ms.ToArray();
    }
}
