using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace FactoryTimer.Controller.Core;

public interface ISelectedInterfaceStore
{
    string? LoadSelectedInterfaceId();
    void SaveSelectedInterfaceId(string interfaceId);
}

public sealed class FileSelectedInterfaceStore : ISelectedInterfaceStore
{
    private readonly string path;

    public FileSelectedInterfaceStore(string? path = null)
    {
        this.path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FactoryCountdownController",
            "selected-network-interface.txt");
    }

    public string? LoadSelectedInterfaceId()
    {
        if (!File.Exists(path)) return null;
        string value = File.ReadAllText(path).Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    public void SaveSelectedInterfaceId(string interfaceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(interfaceId);
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(path, interfaceId);
    }
}

public sealed record NetworkInterfaceSelectionState(
    IReadOnlyList<ControllerNetworkInterface> AvailableInterfaces,
    ControllerNetworkInterface? SelectedInterface,
    string Message);

public sealed class NetworkInterfaceSelectionChangedEventArgs(NetworkInterfaceSelectionState state) : EventArgs
{
    public NetworkInterfaceSelectionState State { get; } = state;
}

public sealed class NetworkInterfaceSelectionManager : IDisposable
{
    private readonly object gate = new();
    private readonly INetworkInterfaceProvider provider;
    private readonly ISelectedInterfaceStore store;
    private string? desiredInterfaceId;
    private string? lastSelectedDisplayName;
    private bool initialized;
    private bool awaitingInitialInterface;
    private bool disposed;

    public NetworkInterfaceSelectionManager(
        INetworkInterfaceProvider provider,
        ISelectedInterfaceStore store)
    {
        this.provider = provider ?? throw new ArgumentNullException(nameof(provider));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        provider.NetworkAddressChanged += Provider_NetworkAddressChanged;
    }

    public event EventHandler<NetworkInterfaceSelectionChangedEventArgs>? StateChanged;

    public NetworkInterfaceSelectionState State { get; private set; } =
        new([], null, "Detecting usable physical network interfaces...");

    public NetworkInterfaceSelectionState Initialize()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (initialized) return State;

            initialized = true;
            string? loadError = null;
            try
            {
                desiredInterfaceId = store.LoadSelectedInterfaceId();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                loadError = $"The saved interface selection could not be read: {exception.Message}";
            }

            IReadOnlyList<ControllerNetworkInterface> available = GetInterfacesSafely(out string? discoveryError);
            ControllerNetworkInterface? selected = FindDesired(available);
            string message;
            if (selected is not null)
            {
                message = $"Restored saved network interface: {selected.DisplayName}.";
            }
            else if (available.Count > 0)
            {
                selected = available[0];
                bool savedWasUnavailable = !string.IsNullOrEmpty(desiredInterfaceId);
                desiredInterfaceId = selected.Id;
                SaveSafely(selected.Id, out string? saveError);
                message = savedWasUnavailable
                    ? $"The saved network interface is unavailable. Switched to {selected.DisplayName}."
                    : $"Selected {selected.DisplayName}.";
                message = AppendError(message, saveError);
            }
            else
            {
                awaitingInitialInterface = true;
                message = !string.IsNullOrEmpty(desiredInterfaceId)
                    ? "The saved network interface is unavailable and no usable physical Wi-Fi or Ethernet interface is active."
                    : "No usable physical Wi-Fi or Ethernet IPv4 interface is active.";
            }

            lastSelectedDisplayName = selected?.DisplayName;
            message = AppendError(message, discoveryError);
            message = AppendError(message, loadError);
            SetState(new(available, selected, message));
            return State;
        }
    }

    public void Select(string interfaceId)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            ArgumentException.ThrowIfNullOrWhiteSpace(interfaceId);
            ControllerNetworkInterface selected = State.AvailableInterfaces
                .FirstOrDefault(candidate => StringComparer.Ordinal.Equals(candidate.Id, interfaceId))
                ?? throw new ArgumentException("The selected interface is not currently available.", nameof(interfaceId));

            desiredInterfaceId = selected.Id;
            awaitingInitialInterface = false;
            lastSelectedDisplayName = selected.DisplayName;
            SaveSafely(selected.Id, out string? saveError);
            SetState(new(
                State.AvailableInterfaces,
                selected,
                AppendError($"Using {selected.DisplayName}.", saveError)));
        }
    }

    public NetworkInterfaceSelectionState Refresh()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!initialized) return Initialize();

            IReadOnlyList<ControllerNetworkInterface> available = GetInterfacesSafely(out string? discoveryError);
            ControllerNetworkInterface? previous = State.SelectedInterface;
            ControllerNetworkInterface? selected = FindDesired(available);
            string message;

            if (selected is not null)
            {
                if (previous is null)
                {
                    message = $"Network interface is available again: {selected.DisplayName}.";
                }
                else if (previous != selected)
                {
                    message = $"The selected interface address changed. Now using {selected.DisplayName}.";
                }
                else
                {
                    message = $"Using {selected.DisplayName}.";
                }
                awaitingInitialInterface = false;
                lastSelectedDisplayName = selected.DisplayName;
            }
            else if (awaitingInitialInterface && available.Count > 0)
            {
                selected = available[0];
                bool savedWasUnavailable = !string.IsNullOrEmpty(desiredInterfaceId);
                desiredInterfaceId = selected.Id;
                awaitingInitialInterface = false;
                lastSelectedDisplayName = selected.DisplayName;
                SaveSafely(selected.Id, out string? saveError);
                message = savedWasUnavailable
                    ? $"The saved network interface is unavailable. Switched to {selected.DisplayName}."
                    : $"Selected newly available interface {selected.DisplayName}.";
                message = AppendError(message, saveError);
            }
            else if (!string.IsNullOrEmpty(desiredInterfaceId))
            {
                string unavailableName = lastSelectedDisplayName ?? desiredInterfaceId;
                message = $"Selected network interface {unavailableName} is unavailable. Select another active interface.";
            }
            else
            {
                message = "No usable physical Wi-Fi or Ethernet IPv4 interface is active.";
            }

            message = AppendError(message, discoveryError);
            SetState(new(available, selected, message));
            return State;
        }
    }

    private ControllerNetworkInterface? FindDesired(IReadOnlyList<ControllerNetworkInterface> available) =>
        string.IsNullOrEmpty(desiredInterfaceId)
            ? null
            : available.FirstOrDefault(candidate =>
                StringComparer.Ordinal.Equals(candidate.Id, desiredInterfaceId));

    private IReadOnlyList<ControllerNetworkInterface> GetInterfacesSafely(out string? error)
    {
        try
        {
            error = null;
            return provider.GetUsableInterfaces();
        }
        catch (Exception exception) when (exception is NetworkInformationException or SocketException)
        {
            error = $"Interface discovery failed: {exception.Message}";
            return [];
        }
    }

    private void SaveSafely(string id, out string? error)
    {
        try
        {
            store.SaveSelectedInterfaceId(id);
            error = null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            error = $"The interface selection could not be saved: {exception.Message}";
        }
    }

    private static string AppendError(string message, string? error) =>
        string.IsNullOrWhiteSpace(error) ? message : $"{message} {error}";

    private void SetState(NetworkInterfaceSelectionState state)
    {
        State = state;
        StateChanged?.Invoke(this, new NetworkInterfaceSelectionChangedEventArgs(state));
    }

    private void Provider_NetworkAddressChanged(object? sender, EventArgs e)
    {
        try
        {
            Refresh();
        }
        catch (ObjectDisposedException)
        {
            // A queued Windows address-change callback can arrive while the app is closing.
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            provider.NetworkAddressChanged -= Provider_NetworkAddressChanged;
            provider.Dispose();
        }
        GC.SuppressFinalize(this);
    }
}
