namespace ApexPost.Courier.Offline.Tests;

public class CourierTestStorageTests
{
	private const string TempDirectory = @"C:\Temp";

	[Fact]
	public void NoVariable_WithDriveE_UsesTheDevelopmentFolder()
	{
		var root = CourierTestStorage.Resolve(null, _ => true, TempDirectory);

		Assert.Equal(CourierTestStorage.DevelopmentDirectory, root);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("   ")]
	public void NoVariable_WithoutDriveE_UsesTheTemporaryFolder(string? configured)
	{
		var root = CourierTestStorage.Resolve(configured, _ => false, TempDirectory);

		Assert.Equal(Path.Combine(TempDirectory, CourierTestStorage.TempFolderName), root);
	}

	[Fact]
	public void Variable_OverridesTheDefault()
	{
		var root = CourierTestStorage.Resolve(@"D:\CourierTests", _ => true, TempDirectory);

		Assert.Equal(@"D:\CourierTests", root);
	}

	[Theory]
	[InlineData(@"CourierTests")]
	[InlineData(@"\CourierTests")]
	[InlineData(@"D:CourierTests")]
	public void Variable_NotAnAbsolutePath_Throws(string configured)
	{
		var exception = Assert.Throws<InvalidOperationException>(() =>
			CourierTestStorage.Resolve(configured, _ => true, TempDirectory));

		Assert.Contains(CourierTestStorage.DirectoryVariable, exception.Message);
	}

	[Fact]
	public void Variable_OnAMissingDrive_ThrowsInsteadOfFallingBack()
	{
		var exception = Assert.Throws<InvalidOperationException>(() =>
			CourierTestStorage.Resolve(@"Q:\CourierTests", root => root != @"Q:\", TempDirectory));

		Assert.Contains(CourierTestStorage.DirectoryVariable, exception.Message);
	}
}
