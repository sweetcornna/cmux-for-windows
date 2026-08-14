namespace CmuxGui.Input;

internal sealed class WheelDeltaAccumulator
{
    private const int WheelDelta = 120;
    private const int RowsPerNotch = 3;
    private int _remainder;

    public int Add(int delta)
    {
        _remainder += delta;
        var notches = _remainder / WheelDelta;
        _remainder %= WheelDelta;
        return -notches * RowsPerNotch;
    }
}
