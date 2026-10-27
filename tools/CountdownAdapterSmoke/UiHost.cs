// Host-only stand-ins for dispatcher/brush types. Controller/core/UDP/serial
// source is real; this does not compile WinUI XAML or validate native rendering.
namespace Microsoft.UI.Dispatching
{
    public sealed class DispatcherQueue
    {
        public bool HasThreadAccess => true;
        public DispatcherQueueTimer CreateTimer() => new();
        public bool TryEnqueue(Action callback) { callback(); return true; }
    }
    public sealed class DispatcherQueueTimer
    {
        public TimeSpan Interval { get; set; }
        public event Action<DispatcherQueueTimer, object>? Tick;
        public void Start() { }
        public void Stop() { }
        public void Fire() => Tick?.Invoke(this, new object());
    }
}
namespace Microsoft.UI
{
    public static class Colors
    {
        public static object LimeGreen { get; } = new();
        public static object Gray { get; } = new();
    }
}
namespace Microsoft.UI.Xaml
{
    public enum Visibility { Visible, Collapsed }
}
namespace Microsoft.UI.Xaml.Media
{
    public abstract class Brush { }
    public sealed class SolidColorBrush(object color) : Brush
    {
        public object Color { get; } = color;
    }
}
