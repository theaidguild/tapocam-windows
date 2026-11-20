namespace TapoCam.Core;

public sealed class RetryBackoff
{
    private int _attempts;

    public int NextSeconds
    {
        get
        {
            var delay = Math.Min(60, 5 * Math.Pow(2, _attempts));
            _attempts = Math.Min(_attempts + 1, 5);
            return (int)delay;
        }
    }

    public void Reset() => _attempts = 0;
}
