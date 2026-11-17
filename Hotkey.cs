using System.Windows.Input;

namespace DotLight;

/// <summary>A global shortcut like "Ctrl+Alt+T", stored as text in settings.</summary>
public readonly record struct Hotkey(ModifierKeys Mods, Key Key)
{
    public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;

    public uint NativeMods =>
        ((Mods & ModifierKeys.Alt) != 0 ? MOD_ALT : 0) |
        ((Mods & ModifierKeys.Control) != 0 ? MOD_CONTROL : 0) |
        ((Mods & ModifierKeys.Shift) != 0 ? MOD_SHIFT : 0) |
        ((Mods & ModifierKeys.Windows) != 0 ? MOD_WIN : 0) | MOD_NOREPEAT;

    public uint NativeKey => (uint)KeyInterop.VirtualKeyFromKey(Key);

    /// <summary>Needs at least one modifier (a bare letter would block typing it everywhere).</summary>
    public bool IsValid => Mods != ModifierKeys.None && Key != Key.None && !IsModifier(Key);

    public override string ToString()
    {
        var parts = new List<string>();
        if ((Mods & ModifierKeys.Control) != 0) parts.Add("Ctrl");
        if ((Mods & ModifierKeys.Alt) != 0) parts.Add("Alt");
        if ((Mods & ModifierKeys.Shift) != 0) parts.Add("Shift");
        if ((Mods & ModifierKeys.Windows) != 0) parts.Add("Win");
        parts.Add(KeyName(Key));
        return string.Join("+", parts);
    }

    static string KeyName(Key k) => k switch
    {
        >= Key.D0 and <= Key.D9 => ((int)(k - Key.D0)).ToString(),
        Key.OemComma => ",", Key.OemPeriod => ".", Key.OemMinus => "-", Key.OemPlus => "=",
        Key.OemQuestion => "/", Key.OemSemicolon => ";", Key.OemQuotes => "'", Key.OemTilde => "`",
        Key.OemOpenBrackets => "[", Key.OemCloseBrackets => "]", Key.OemPipe => "\\",
        Key.Return => "Enter", Key.Back => "Backspace", Key.Next => "PageDown", Key.Prior => "PageUp",
        _ => k.ToString(),
    };

    public static bool IsModifier(Key k) => k is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
        or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System;

    public static Hotkey? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var mods = ModifierKeys.None;
        Key key = Key.None;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl": case "control": mods |= ModifierKeys.Control; break;
                case "alt": mods |= ModifierKeys.Alt; break;
                case "shift": mods |= ModifierKeys.Shift; break;
                case "win": mods |= ModifierKeys.Windows; break;
                default:
                    if (raw.Length == 1 && char.IsDigit(raw[0])) key = Key.D0 + (raw[0] - '0');
                    else
                    {
                        var known = Enum.GetValues<Key>().FirstOrDefault(k => KeyName(k).Equals(raw, StringComparison.OrdinalIgnoreCase));
                        if (known != Key.None) key = known;
                        else if (!Enum.TryParse(raw, true, out key)) return null;
                    }
                    break;
            }
        }
        var h = new Hotkey(mods, key);
        return h.IsValid ? h : null;
    }
}
