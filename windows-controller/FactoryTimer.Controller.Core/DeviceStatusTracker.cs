namespace FactoryTimer.Controller.Core;

public enum DeviceAvailability
{
    Searching,
    Online,
    Offline,
}

public sealed class DeviceStatusTracker
{
    public static readonly TimeSpan OfflineTimeout = TimeSpan.FromSeconds(6);

    private DateTimeOffset searchStartedAt;
    private DateTimeOffset lastStatusAt;
    private bool forcedOffline = true;
    private bool intentionalStatusPauseActive;

    public void BeginSearching(DateTimeOffset now)
    {
        searchStartedAt = now;
        lastStatusAt = default;
        forcedOffline = false;
        intentionalStatusPauseActive = false;
    }

    public void RecordValidStatus(DateTimeOffset now)
    {
        lastStatusAt = now;
        searchStartedAt = default;
        forcedOffline = false;
    }

    public void MarkOffline()
    {
        searchStartedAt = default;
        lastStatusAt = default;
        forcedOffline = true;
        intentionalStatusPauseActive = false;
    }

    public void BeginIntentionalStatusPause()
    {
        if (!forcedOffline && lastStatusAt != default)
        {
            intentionalStatusPauseActive = true;
        }
    }

    public void EndIntentionalStatusPause() => intentionalStatusPauseActive = false;

    public DeviceAvailability GetAvailability(DateTimeOffset now)
    {
        if (forcedOffline) return DeviceAvailability.Offline;
        if (intentionalStatusPauseActive && lastStatusAt != default)
        {
            return DeviceAvailability.Online;
        }
        if (lastStatusAt != default)
        {
            return now - lastStatusAt < OfflineTimeout
                ? DeviceAvailability.Online
                : DeviceAvailability.Offline;
        }
        if (searchStartedAt != default && now - searchStartedAt < OfflineTimeout)
        {
            return DeviceAvailability.Searching;
        }
        return DeviceAvailability.Offline;
    }
}
