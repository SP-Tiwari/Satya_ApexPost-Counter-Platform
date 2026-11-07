using System.Net;
using System.Net.Http.Json;
using ApexPost.Api.Models;
using ApexPost.Domain.Enums;

namespace ApexPost.Api.IntegrationTests;

// GET /api/shipments?page=&pageSize=&status=&city=
public sealed class ShipmentListTests : IDisposable
{
	private readonly ApiFactory _factory = new();
	private readonly HttpClient _client;

	public ShipmentListTests() => _client = _factory.CreateClient();

	public void Dispose() => _factory.Dispose();

	[Fact]
	public async Task List_DefaultsToTheFirst20_OrderedByTrackingNumber()
	{
		var created = await CreateManyAsync(25);

		var page = await _client.GetFromJsonAsync<ShipmentPageResponse>(Api.Shipments);

		Assert.Equal(1, page!.Page);
		Assert.Equal(20, page.PageSize);
		Assert.Equal(25, page.TotalCount);
		Assert.Equal(created.Take(20), page.Items.Select(item => item.TrackingNumber));
	}

	[Fact]
	public async Task List_SecondPage_ReturnsTheRest()
	{
		var created = await CreateManyAsync(25);

		var page = await _client.GetFromJsonAsync<ShipmentPageResponse>($"{Api.Shipments}?page=2");

		Assert.Equal(created.Skip(20), page!.Items.Select(item => item.TrackingNumber));
		Assert.Equal(25, page.TotalCount);
	}

	[Fact]
	public async Task List_PageSize50_IsTheMaximum()
	{
		await CreateManyAsync(51);

		var page = await _client.GetFromJsonAsync<ShipmentPageResponse>($"{Api.Shipments}?pageSize=50");

		Assert.Equal(50, page!.Items.Count);
		Assert.Equal(51, page.TotalCount);
	}

	[Theory]
	[InlineData("pageSize=51", "pageSize")]
	[InlineData("pageSize=0", "pageSize")]
	[InlineData("page=0", "page")]
	[InlineData("status=Lost", "status")]
	[InlineData("status=99", "status")]
	public async Task List_InvalidQuery_Returns400ValidationProblem(string query, string field)
	{
		var response = await _client.GetAsync($"{Api.Shipments}?{query}");

		await response.AssertValidationErrorAsync(field);
	}

	[Fact]
	public async Task List_FiltersByStatusAndCity()
	{
		await _client.CreateAsync("APX-30000001", city: "Pune");
		await _client.CreateAsync("APX-30000002", city: "Pune");
		await _client.CreateAsync("APX-30000003", city: "Mumbai");
		await _client.AdvanceAsync("APX-30000002", ShipmentStatus.PickedUp);
		await _client.AdvanceAsync("APX-30000003", ShipmentStatus.PickedUp);

		Assert.Equal(["APX-30000002", "APX-30000003"], await TrackingNumbersAsync("status=PickedUp"));
		Assert.Equal(["APX-30000002", "APX-30000003"], await TrackingNumbersAsync("status=1"));
		Assert.Equal(["APX-30000001", "APX-30000002"], await TrackingNumbersAsync("city=Pune"));
		Assert.Equal(["APX-30000002"], await TrackingNumbersAsync("status=PickedUp&city=pune"));
		Assert.Empty(await TrackingNumbersAsync("status=Delivered"));
	}

	private async Task<string[]> TrackingNumbersAsync(string query)
	{
		var page = await _client.GetFromJsonAsync<ShipmentPageResponse>($"{Api.Shipments}?{query}");
		Assert.Equal(page!.Items.Count, page.TotalCount);
		return [.. page.Items.Select(item => item.TrackingNumber)];
	}

	// Created in reverse order, so the ordering comes from the query.
	private async Task<IReadOnlyList<string>> CreateManyAsync(int count)
	{
		var trackingNumbers = Enumerable.Range(1, count)
			.Select(number => $"APX-4{number:D7}")
			.ToArray();
		foreach (var trackingNumber in Enumerable.Reverse(trackingNumbers))
			await _client.CreateAsync(trackingNumber);

		return trackingNumbers;
	}
}
