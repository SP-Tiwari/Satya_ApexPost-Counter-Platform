using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Time.Testing;

namespace ApexPost.Courier.Sync.Tests;

// D10 substitute for SecureStorage: the token is stored encrypted with DPAPI
// for the current Windows user. Each test uses its own temporary folder, never
// the real %LOCALAPPDATA%\ApexPost\Courier.
[SupportedOSPlatform("windows")]
public sealed class DpapiTokenStoreTests : IDisposable
{
	private readonly string _directory = Path.Combine(
		Path.GetTempPath(),
		"ApexPost.Courier.Sync.Tests",
		Guid.NewGuid().ToString("N"));

	private readonly FakeTimeProvider _time = new(TestValues.Now);
	private readonly RecordingLoggerProvider _logs = new();

	private string TokenFile => Path.Combine(_directory, DpapiTokenStore.FileName);

	[Fact]
	public void SavedToken_IsReadBack_ByANewStoreInstance()
	{
		var token = TestValues.ValidToken();
		Store().SaveToken(token);

		Assert.Equal(token, Store().GetToken());
	}

	[Fact]
	public void Save_CreatesTheFolder_AndReplacesAnEarlierToken()
	{
		Assert.False(Directory.Exists(_directory));
		var store = Store();

		store.SaveToken(TestValues.ValidToken("first-token"));
		store.SaveToken(TestValues.ValidToken("second-token"));

		Assert.Equal("second-token", store.GetToken()!.Value);
		Assert.Equal([TokenFile], Directory.GetFiles(_directory));
	}

	[Fact]
	public void TheFile_HoldsNoPlaintextToken()
	{
		Store().SaveToken(TestValues.ValidToken());

		var bytes = File.ReadAllBytes(TokenFile);
		foreach (var encoding in new Encoding[] { Encoding.UTF8, Encoding.Unicode })
			Assert.DoesNotContain(TestValues.Token, encoding.GetString(bytes));
		Assert.DoesNotContain(
			Convert.ToBase64String(Encoding.UTF8.GetBytes(TestValues.Token)),
			Encoding.UTF8.GetString(bytes));
	}

	[Fact]
	public void TheFile_IsDecryptableOnlyWithTheStoresEntropy()
	{
		Store().SaveToken(TestValues.ValidToken());

		Assert.ThrowsAny<CryptographicException>(
			() => ProtectedData.Unprotect(File.ReadAllBytes(TokenFile), null, DataProtectionScope.CurrentUser));
	}

	[Fact]
	public void NothingStored_ReturnsNull()
	{
		Assert.Null(Store().GetToken());
	}

	[Fact]
	public void ExpiredToken_ReturnsNull_AndIsDeleted()
	{
		var store = Store();
		store.SaveToken(new AccessToken(TestValues.Token, TestValues.Now.AddMinutes(5)));
		Assert.NotNull(store.GetToken());

		_time.SetUtcNow(TestValues.Now.AddMinutes(5));

		Assert.Null(store.GetToken());
		Assert.False(File.Exists(TokenFile));
	}

	[Fact]
	public void CorruptFile_ReturnsNull_AndIsDeleted()
	{
		Directory.CreateDirectory(_directory);
		File.WriteAllBytes(TokenFile, [1, 2, 3, 4, 5]);

		Assert.Null(Store().GetToken());
		Assert.False(File.Exists(TokenFile));
	}

	[Fact]
	public void PlaintextOrForeignDpapiData_ReturnsNull_AndIsDeleted()
	{
		Directory.CreateDirectory(_directory);
		var payload = Encoding.UTF8.GetBytes($$"""{"accessToken":"{{TestValues.Token}}","expiresAtUtc":"2030-01-01T00:00:00Z"}""");

		File.WriteAllBytes(TokenFile, payload);
		Assert.Null(Store().GetToken());
		Assert.False(File.Exists(TokenFile));

		File.WriteAllBytes(TokenFile, ProtectedData.Protect(payload, null, DataProtectionScope.CurrentUser));
		Assert.Null(Store().GetToken());
		Assert.False(File.Exists(TokenFile));
	}

	[Theory]
	[InlineData("""{"accessToken":"","expiresAtUtc":"2030-01-01T00:00:00Z"}""")]
	[InlineData("""{"accessToken":"t"}""")]
	[InlineData("""{"accessToken":"t","expiresAtUtc":"never"}""")]
	[InlineData("not json")]
	public void IncompleteDecryptedData_ReturnsNull_AndIsDeleted(string json)
	{
		Directory.CreateDirectory(_directory);
		File.WriteAllBytes(
			TokenFile,
			ProtectedData.Protect(
				Encoding.UTF8.GetBytes(json),
				"ApexPost.Courier.AccessToken"u8.ToArray(),
				DataProtectionScope.CurrentUser));

		Assert.Null(Store().GetToken());
		Assert.False(File.Exists(TokenFile));
	}

	[Fact]
	public void UnreadableFile_ReturnsNull_AndIsKept()
	{
		var store = Store();
		store.SaveToken(TestValues.ValidToken());

		using (new FileStream(TokenFile, FileMode.Open, FileAccess.Read, FileShare.None))
			Assert.Null(store.GetToken());

		Assert.True(File.Exists(TokenFile));
		Assert.Equal(TestValues.Token, store.GetToken()!.Value);
	}

	[Fact]
	public void Clear_DeletesTheToken_AndDoesNothingWhenNoneIsStored()
	{
		var store = Store();
		store.ClearToken();
		store.SaveToken(TestValues.ValidToken());

		store.ClearToken();

		Assert.False(File.Exists(TokenFile));
		Assert.Null(store.GetToken());
	}

	[Fact]
	public void NoLogMessage_ContainsTheToken()
	{
		var store = Store();
		store.SaveToken(TestValues.ValidToken());
		store.GetToken();
		File.WriteAllBytes(TokenFile, [9, 9, 9]);
		store.GetToken();

		Assert.NotEmpty(_logs.Messages);
		Assert.DoesNotContain(_logs.Messages, m => m.Contains(TestValues.Token, StringComparison.Ordinal));
	}

	[Fact]
	public void DefaultDirectory_IsApexPostCourier_InLocalApplicationData()
	{
		Assert.Equal(
			Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
				"ApexPost",
				"Courier"),
			DpapiTokenStore.DefaultDirectory);
	}

	public void Dispose()
	{
		if (Directory.Exists(_directory))
			Directory.Delete(_directory, recursive: true);
	}

	private DpapiTokenStore Store() =>
		new(_directory, _time, new Microsoft.Extensions.Logging.Logger<DpapiTokenStore>(
			new Microsoft.Extensions.Logging.LoggerFactory([_logs])));
}
