using ApexPost.Courier.Offline.Persistence;

namespace ApexPost.Courier.Offline.Tests;

// Criterion: change listener and cleanup.
public sealed class ChangeListenerTests
{
	private static readonly TimeSpan QuietPeriod = TimeSpan.FromMilliseconds(750);

	[Fact]
	public async Task Listener_ReportsTheChangedIdsAndThePendingCount()
	{
		using var context = new CourierTestContext();
		var recorder = new NotificationRecorder();
		context.Database.StartChangeListener(recorder.Record);
		var first = Scans.New();
		var second = Scans.New();

		context.Scans.Create(first);
		await recorder.WaitForDocumentAsync(first.Id);
		context.Scans.Create(second);
		var afterSecond = await recorder.WaitForDocumentAsync(second.Id);
		context.Scans.Update(first.MarkSynced());
		var afterSync = await recorder.WaitForAsync(
			notification => notification.DocumentIds.Contains(first.Id) && notification.PendingCount == 1);

		Assert.Equal(2, afterSecond.PendingCount);
		Assert.Equal(1, afterSync.PendingCount);
	}

	[Fact]
	public async Task Listener_LogsTheChangedIdsAndThePendingCount()
	{
		using var context = new CourierTestContext();
		var recorder = new NotificationRecorder();
		context.Database.StartChangeListener(recorder.Record);
		var scan = Scans.New();

		context.Scans.Create(scan);
		await recorder.WaitForDocumentAsync(scan.Id);

		Assert.Contains(
			context.DatabaseLog.Messages,
			message => message.Contains($"Changed documents: {scan.Id}")
				&& message.Contains("Pending scans: 1"));
	}

	[Fact]
	public async Task Listener_ReportsADeletedDocument()
	{
		using var context = new CourierTestContext();
		var scan = Scans.New();
		context.Scans.Create(scan);
		var recorder = new NotificationRecorder();
		context.Database.StartChangeListener(recorder.Record);

		context.Scans.Delete(scan.Id);
		var notification = await recorder.WaitForDocumentAsync(scan.Id);

		Assert.Equal(0, notification.PendingCount);
	}

	[Fact]
	public void StartChangeListener_RefusesADuplicateRegistration()
	{
		using var context = new CourierTestContext();
		context.Database.StartChangeListener();

		Assert.Throws<InvalidOperationException>(() => context.Database.StartChangeListener());
		Assert.True(context.Database.IsChangeListenerRegistered);
	}

	[Fact]
	public async Task StopChangeListener_StopsNotifications()
	{
		using var context = new CourierTestContext();
		var recorder = new NotificationRecorder();
		context.Database.StartChangeListener(recorder.Record);
		context.Database.StopChangeListener();

		context.Scans.Create(Scans.New());

		Assert.False(context.Database.IsChangeListenerRegistered);
		Assert.False(await recorder.AnyWithinAsync(QuietPeriod));
	}

	[Fact]
	public async Task StartChangeListener_CanRegisterAgainAfterStop()
	{
		using var context = new CourierTestContext();
		context.Database.StartChangeListener();
		context.Database.StopChangeListener();
		var recorder = new NotificationRecorder();
		context.Database.StartChangeListener(recorder.Record);
		var scan = Scans.New();

		context.Scans.Create(scan);

		Assert.Equal(1, (await recorder.WaitForDocumentAsync(scan.Id)).PendingCount);
	}

	[Fact]
	public async Task Dispose_RemovesTheListenerToken()
	{
		using var folder = new CourierDatabaseFolder();
		var log = new RecordingLogger<CourierDatabase>();
		var database = new CourierDatabase(folder.Path, log);
		var recorder = new NotificationRecorder();
		database.StartChangeListener(recorder.Record);

		database.Dispose();

		Assert.False(database.IsChangeListenerRegistered);
		Assert.Contains(log.Messages, message => message.Contains("Change listener removed"));

		// Another owner changes the same files: the disposed listener hears nothing.
		using (var reopened = new CourierDatabase(folder.Path))
			new CouchbaseScanRepository(reopened).Create(Scans.New());
		Assert.False(await recorder.AnyWithinAsync(QuietPeriod));
	}

	[Fact]
	public async Task HandlerException_DoesNotBreakTheListener()
	{
		using var context = new CourierTestContext();
		var calls = 0;
		var recorder = new NotificationRecorder();
		context.Database.StartChangeListener(notification =>
		{
			if (Interlocked.Increment(ref calls) == 1)
				throw new InvalidOperationException("handler failure");
			recorder.Record(notification);
		});
		var first = Scans.New();
		context.Scans.Create(first);
		await WaitUntilAsync(() => Volatile.Read(ref calls) >= 1);
		var second = Scans.New();

		context.Scans.Create(second);

		await recorder.WaitForDocumentAsync(second.Id);
		Assert.Contains(context.DatabaseLog.Messages, message => message.Contains("handler threw"));
	}

	private static async Task WaitUntilAsync(Func<bool> condition)
	{
		var deadline = DateTime.UtcNow.AddSeconds(10);
		while (!condition())
		{
			if (DateTime.UtcNow > deadline)
				throw new TimeoutException("The condition was not met.");
			await Task.Delay(20);
		}
	}
}
