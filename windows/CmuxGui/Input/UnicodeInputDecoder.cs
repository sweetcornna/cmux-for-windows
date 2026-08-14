using System;

namespace CmuxGui.Input;

internal sealed class UnicodeInputDecoder
{
    private char? _highSurrogate;

    public string DecodeUtf16Unit(char value)
    {
        if (char.IsHighSurrogate(value))
        {
            _highSurrogate = value;
            return string.Empty;
        }

        if (char.IsLowSurrogate(value))
        {
            if (_highSurrogate is not { } high)
            {
                return string.Empty;
            }
            _highSurrogate = null;
            return new string([high, value]);
        }

        _highSurrogate = null;
        return value.ToString();
    }

    public static string DecodeScalar(uint value) => value is > char.MaxValue and <= 0x10FFFF
        && (value < 0xD800 || value > 0xDFFF)
            ? char.ConvertFromUtf32((int)value)
            : value <= char.MaxValue && !char.IsSurrogate((char)value)
                ? ((char)value).ToString()
                : string.Empty;

    public void Reset() => _highSurrogate = null;
}
