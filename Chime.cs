using System.IO;
using System.Media;

namespace DotLight;

/// <summary>Little sounds, made in memory (no sound files to ship).</summary>
static class Chime
{
    const int Rate = 44100;
    static byte[]? chime, pop;

    /// <summary>A soft two-note chime (the focus timer).</summary>
    public static void Play() => Sound(chime ??= Wav(MakeChime()));

    /// <summary>A short bubbly pop (Claude Code needs your OK).</summary>
    public static void Pop() => Sound(pop ??= Wav(MakePop()));

    static void Sound(byte[] wav)
    {
        if (Environment.GetEnvironmentVariable("LIGHT_TEST") != null) return; // silent while testing
        try { new SoundPlayer(new MemoryStream(wav)).Play(); } // async, returns at once
        catch { /* no audio device: the flash still shows */ }
    }

    static short[] MakeChime()
    {
        // two gentle bell tones, the second a fifth above and a little later
        (double freq, double start, double gain)[] notes = { (880, 0, 0.32), (1318.5, 0.16, 0.26) };
        var data = new short[(int)(Rate * 1.6)];
        for (int i = 0; i < data.Length; i++)
        {
            double t = (double)i / Rate, v = 0;
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
        return data;
    }

    static short[] MakePop()
    {
        // two bright bubbly pops ("pop-pop"), the second a little higher, loud enough to notice
        (double start, double baseHz)[] pops = { (0, 760), (0.13, 980) };
        var data = new short[(int)(Rate * 0.36)];
        for (int i = 0; i < data.Length; i++)
        {
            double t = (double)i / Rate, v = 0;
            foreach (var (s, hz) in pops)
            {
                double u = t - s;
                if (u < 0) continue;
                // pitch jumps up and settles, like a bubble; phase is the integral of that sweep
                double phase = 2 * Math.PI * (hz * u + 900 / 45.0 * (1 - Math.Exp(-u * 45)));
                double env = Math.Min(1, u / 0.002) * Math.Exp(-u * 26);
                v += env * (Math.Sin(phase) + 0.3 * Math.Sin(2 * phase) + 0.1 * Math.Sin(3 * phase));
            }
            data[i] = (short)(Math.Clamp(v * 0.95, -1, 1) * short.MaxValue * 0.97);
        }
        return data;
    }

    static byte[] Wav(short[] data)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        int bytes = data.Length * 2;
        w.Write("RIFF"u8); w.Write(36 + bytes); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(Rate); w.Write(Rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(bytes);
        foreach (var s in data) w.Write(s);
        w.Flush();
        return ms.ToArray();
    }
}
