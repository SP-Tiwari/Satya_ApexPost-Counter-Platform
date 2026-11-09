using System.Net.NetworkInformation;

namespace ApexPost.Courier.Sync;

// Whether the device has a network connection, and when that changes. The
// console client's substitute for MAUI Connectivity.ConnectivityChanged (D9).
public interface INetworkStatus
{
	bool IsAvailable { get; }

	// Raised with true when a network becomes available and false when the
	// last one is lost. It may be raised on any thread.
	event EventHandler<bool>? AvailabilityChanged;
}

// INetworkStatus from the operating system: NetworkChange's
// NetworkAvailabilityChanged event and NetworkInterface.GetIsNetworkAvailable().
// It sees network adapters only, not whether the API answers; the circuit
// breaker covers an API that is down.
public sealed class SystemNetworkStatus : INetworkStatus, IDisposable
{
	public SystemNetworkStatus() =>
		NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;

	public bool IsAvailable => NetworkInterface.GetIsNetworkAvailable();

	public event EventHandler<bool>? AvailabilityChanged;

	public void Dispose() =>
		NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;

	private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) =>
		AvailabilityChanged?.Invoke(this, e.IsAvailable);
}
