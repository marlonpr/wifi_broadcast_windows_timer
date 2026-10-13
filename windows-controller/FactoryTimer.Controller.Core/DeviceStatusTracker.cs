namespace FactoryTimer.Controller.Core;

public enum DeviceAvailability
{
    Searching,
    Online,
    Offline,
}

public sealed class DeviceStatusTracker
{
    public static readonly TimeSpan OfflineTimeout = TimeSpan.FromSeconds(48);

    private DateTimeOffset searchStartedAt;
    private DateTimeOffset lastStatusAt;
    private bool forcedOffline = true;
    private bool intentionalStatusPauseActive;
    private DateTimeOffset? pauseEndedAt;

    public void BeginSearching(DateTimeOffset now)
    {
        searchStartedAt = now;
        lastStatusAt = default;
        forcedOffline = false;
        intentionalStatusPauseActive = false;
        pauseEndedAt = null;
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
        pauseEndedAt = null;
    }

    public void BeginIntentionalStatusPause()
    {
        if (!forcedOffline && lastStatusAt != default)
        {
            intentionalStatusPauseActive = true;
        }
    }

    public void EndIntentionalStatusPause(DateTimeOffset? resumedAt = null)
    {
        if (intentionalStatusPauseActive) pauseEndedAt = resumedAt ?? DateTimeOffset.UtcNow;
        intentionalStatusPauseActive = false;
    }

    public DeviceAvailability GetAvailability(DateTimeOffset now)
    {
        if (forcedOffline) return DeviceAvailability.Offline;
        if (intentionalStatusPauseActive && lastStatusAt != default)
        {
            return DeviceAvailability.Online;
        }
        if (pauseEndedAt.HasValue && lastStatusAt != default && now - pauseEndedAt.Value < OfflineTimeout)
            return DeviceAvailability.Online;
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
