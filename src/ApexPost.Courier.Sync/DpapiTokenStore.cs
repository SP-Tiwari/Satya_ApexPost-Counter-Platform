using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ApexPost.Courier.Sync;

// The access token, encrypted with Windows DPAPI for the current Windows user
// (DataProtectionScope.CurrentUser), in one file under
// %LOCALAPPDATA%\ApexPost\Courier; the plaintext token is never written to
// disk. Other Windows users cannot decrypt it, but DPAPI does not isolate
// applications: another process running as the same user may be able to.
// This is the console client's substitute for MAUI SecureStorage. DPAPI exists
// only on Windows, so the store refuses to run anywhere else instead of
// falling back to a plaintext file. The token and the file's contents are
// never logged.
[SupportedOSPlatform("windows")]
public sealed class DpapiTokenStore : ITokenStore
{
	public const string FileName = "access-token.bin";

	// Ties the encrypted data to this purpose, so another DPAPI blob of the
	// same user cannot be passed off as the token file.
	private static readonly byte[] Entropy = "ApexPost.Courier.AccessToken"u8.ToArray();

	private readonly Lock _gate = new();
	private readonly string _directory;
	private readonly string _path;
	private readonly TimeProvider _timeProvider;
	private readonly ILogger<DpapiTokenStore> _logger;

	// Also used by AddCourierSync, which checks the platform before creating
	// the store.
	internal const string UnsupportedPlatformMessage =
		"The courier client stores its access token with Windows DPAPI, which is available only on Windows.";

	public DpapiTokenStore(string directory, TimeProvider timeProvider, ILogger<DpapiTokenStore> logger)
	{
		if (!OperatingSystem.IsWindows())
			throw new PlatformNotSupportedException(UnsupportedPlatformMessage);

		ArgumentException.ThrowIfNullOrWhiteSpace(directory);
		_directory = Path.GetFullPath(directory);
		_path = Path.Combine(_directory, FileName);
		_timeProvider = timeProvider;
		_logger = logger;
	}

	public static string DefaultDirectory { get; } = Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
		"ApexPost",
		"Courier");

	public AccessToken? GetToken()
	{
		lock (_gate)
		{
			byte[] encrypted;
			try
			{
				if (!File.Exists(_path))
					return null;

				encrypted = File.ReadAllBytes(_path);
			}
			catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
			{
				_logger.LogWarning("The stored access token could not be read; sign-in is required.");
				return null;
			}

			var token = Decrypt(encrypted);
			if (token is null)
			{
				_logger.LogWarning("The stored access token was unreadable and has been deleted; sign-in is required.");
				DeleteFile(_path);
				return null;
			}

			if (token.IsExpired(_timeProvider.GetUtcNow()))
			{
				_logger.LogInformation("The stored access token has expired and has been deleted; sign-in is required.");
				DeleteFile(_path);
				return null;
			}

			return token;
		}
	}

	public void SaveToken(AccessToken token)
	{
		ArgumentNullException.ThrowIfNull(token);

		var plaintext = JsonSerializer.SerializeToUtf8Bytes(
			new StoredToken(token.Value, token.ExpiresAtUtc),
			CourierJsonContext.Default.StoredToken);
		try
		{
			var encrypted = ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);
			lock (_gate)
			{
				Directory.CreateDirectory(_directory);

				// Written beside the target and then moved over it, so a crash
				// leaves either the old file or the new one, never half of one.
				// A failed write or move removes the temporary file.
				try
				{
					File.WriteAllBytes(TemporaryPath, encrypted);
					File.Move(TemporaryPath, _path, overwrite: true);
				}
				catch
				{
					DeleteFile(TemporaryPath);
					throw;
				}
			}
		}
		finally
		{
			CryptographicOperations.ZeroMemory(plaintext);
		}
	}

	public void ClearToken()
	{
		lock (_gate)
		{
			DeleteFile(_path);
			DeleteFile(TemporaryPath);
		}
	}

	public bool ClearToken(string expectedValue)
	{
		lock (_gate)
		{
			if (GetToken()?.Value != expectedValue)
				return false;

			DeleteFile(_path);
			return true;
		}
	}

	private string TemporaryPath => _path + ".tmp";

	// Null when the data is not a DPAPI blob of this user with this entropy, or
	// does not hold a complete token.
	private static AccessToken? Decrypt(byte[] encrypted)
	{
		byte[]? plaintext = null;
		try
		{
			plaintext = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
			var stored = JsonSerializer.Deserialize(plaintext, CourierJsonContext.Default.StoredToken);
			return stored is { AccessToken: { } value, ExpiresAtUtc: { } expiresAtUtc }
				&& !string.IsNullOrWhiteSpace(value)
				? new AccessToken(value, expiresAtUtc)
				: null;
		}
		catch (Exception exception) when (exception is CryptographicException or JsonException)
		{
			return null;
		}
		finally
		{
			if (plaintext is not null)
				CryptographicOperations.ZeroMemory(plaintext);
		}
	}

	private void DeleteFile(string path)
	{
		try
		{
			File.Delete(path);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			_logger.LogWarning("The stored access token file could not be deleted.");
		}
	}
}
