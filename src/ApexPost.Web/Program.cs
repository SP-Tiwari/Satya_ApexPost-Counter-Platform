using System.Globalization;
using ApexPost.Application.Persistence;
using ApexPost.Application.Caching;
using ApexPost.Application.Courier;
using ApexPost.Application.Events;
using ApexPost.Application.Verification;
using ApexPost.Infrastructure.Courier;
using ApexPost.Infrastructure.Persistence;
using ApexPost.Infrastructure.Persistence.Sql;
using ApexPost.Infrastructure.Verification;
using ApexPost.Web.Authentication;
using ApexPost.Web.Authorization;
using ApexPost.Web.Filters;
using ApexPost.Web.Middleware;
using ApexPost.Web.Notifications;
using Microsoft.AspNetCore.Authentication.Cookies;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllersWithViews(options =>
{
	options.Filters.AddService<AuditActionFilter>();
	options.Filters.AddService<ValidationActionFilter>();
	options.Filters.AddService<PerformanceActionFilter>();
});

builder.Services.Configure<ApiBehaviorOptions>(options =>
	options.SuppressModelStateInvalidFilter = true);

builder.Services.AddProblemDetails(options =>
{
	options.CustomizeProblemDetails = context =>
	{
		// Keeps the specific titles set by ApplicationExceptionHandler.
		if (context.ProblemDetails.Status is null or >= 500)
			context.ProblemDetails.Title = "An unexpected error occurred.";

		SecurityHeadersMiddleware.ApplyHeaders(
			context.HttpContext.Response);

		if (context.HttpContext.Items.TryGetValue(
			CorrelationIdMiddleware.HttpContextItemKey,
			out var correlationId))
		{
			context.HttpContext.Response.Headers[
				CorrelationIdMiddleware.HeaderName] =
				correlationId?.ToString();

			context.ProblemDetails.Extensions["correlationId"] =
				correlationId;
		}
	};
});

builder.Services.AddExceptionHandler<ApplicationExceptionHandler>();

builder.Services
	.AddAuthentication(options =>
	{
		options.DefaultScheme =
			CookieAuthenticationDefaults.AuthenticationScheme;
	})
	.AddCookie(options =>
	{
		options.LoginPath = "/Account/SignIn";
		options.AccessDeniedPath = "/Account/AccessDenied";
		options.Cookie.HttpOnly = true;
		options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
		options.Cookie.SameSite = SameSiteMode.Lax;
		options.ExpireTimeSpan = TimeSpan.FromHours(8);
		options.SlidingExpiration = true;
		options.EventsType = typeof(StaffCookieAuthenticationEvents);
	});

builder.Services.AddAuthorization(options => options.AddApexPostPolicies());
builder.Services.AddScoped<IAuthorizationHandler, SameBranchAuthorizationHandler>();

var parcelTrackingOrigins = builder.Configuration
	.GetSection("ParcelTracking:AllowedOrigins")
	.Get<string[]>() ?? [];
foreach (var configuredOrigin in parcelTrackingOrigins)
{
	if (!Uri.TryCreate(configuredOrigin, UriKind.Absolute, out var origin) ||
		(origin.Scheme != Uri.UriSchemeHttp &&
			origin.Scheme != Uri.UriSchemeHttps) ||
		!string.IsNullOrEmpty(origin.UserInfo) ||
		origin.AbsolutePath != "/" ||
		!string.IsNullOrEmpty(origin.Query) ||
		!string.IsNullOrEmpty(origin.Fragment) ||
		configuredOrigin.Contains('*'))
	{
		throw new InvalidOperationException(
			"ParcelTracking:AllowedOrigins must contain exact HTTP or HTTPS origins without paths, credentials, queries, fragments, or wildcards.");
	}
}

builder.Services.AddCors(options =>
	options.AddPolicy("ParcelTracking", policy =>
	{
		if (parcelTrackingOrigins.Length > 0)
		{
			policy.WithOrigins(parcelTrackingOrigins)
				.WithMethods("GET")
				.WithHeaders("Accept");
		}
	}));

var databaseConnectionString =
	builder.Configuration.GetConnectionString("ApexPostDb");
if (string.IsNullOrWhiteSpace(databaseConnectionString))
{
	throw new InvalidOperationException(
		"ConnectionStrings:ApexPostDb must be configured.");
}

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentStaffAccessor, HttpContextStaffAccessor>();
builder.Services.AddApexPostSqlPersistence(databaseConnectionString);

// Development-only, because each request opens a database connection.
var databaseHealthEnabled = builder.Environment.IsDevelopment();
if (databaseHealthEnabled)
{
	builder.Services.AddHealthChecks().AddCheck(
		"sql-server",
		new SqlServerConnectivityHealthCheck(databaseConnectionString),
		timeout: TimeSpan.FromSeconds(10));
}

// Demo logins: the password comes from user-secrets (never source control)
// and is only honoured in Development, so a stray setting cannot create
// known-password accounts in another environment.
var demoStaffPassword = builder.Configuration["DemoData:StaffPassword"];
if (demoStaffPassword is not null)
{
	if (string.IsNullOrWhiteSpace(demoStaffPassword))
		throw new InvalidOperationException(
			"DemoData:StaffPassword must not be blank when set.");
	if (!builder.Environment.IsDevelopment())
		throw new InvalidOperationException(
			"DemoData:StaffPassword is only supported in the Development environment.");
}

var referenceDataSeconds = builder.Configuration.GetValue<double>(
	"Caching:ReferenceDataSeconds", 0d);
if (!double.IsFinite(referenceDataSeconds) || referenceDataSeconds < 0d)
{
	throw new InvalidOperationException(
		"Caching:ReferenceDataSeconds must be a non-negative number (0 disables caching).");
}
var referenceDataLifetime = TimeSpan.FromSeconds(referenceDataSeconds);
builder.Services.AddSingleton<ReferenceDataCache>(services =>
	new ReferenceDataCache(
		services.GetRequiredService<IReferenceDataSource>(),
		services.GetRequiredService<TimeProvider>(),
		referenceDataLifetime));

var courierTrackingStatus =
	builder.Configuration["Courier:SimulatedTrackingStatus"];
if (!string.IsNullOrWhiteSpace(courierTrackingStatus))
{
	var courierTimeoutSeconds = builder.Configuration.GetValue<double?>(
		"Courier:TimeoutSeconds");
	var courierMaxConcurrentLookups = builder.Configuration.GetValue<int?>(
		"Courier:MaxConcurrentLookups");
	var courierLatencyMilliseconds = builder.Configuration.GetValue<double?>(
		"Courier:SimulatedLatencyMilliseconds") ?? 0d;
	if (courierTimeoutSeconds is not { } timeoutSeconds ||
		!double.IsFinite(timeoutSeconds) || timeoutSeconds <= 0d ||
		courierMaxConcurrentLookups is not > 0 ||
		!double.IsFinite(courierLatencyMilliseconds) ||
		courierLatencyMilliseconds < 0d)
	{
		throw new InvalidOperationException(
			"Configure a positive Courier:TimeoutSeconds and Courier:MaxConcurrentLookups (and a non-negative SimulatedLatencyMilliseconds, if set) when Courier:SimulatedTrackingStatus is configured.");
	}

	var courierTimeout = TimeSpan.FromSeconds(timeoutSeconds);
	var courierLatency = TimeSpan.FromMilliseconds(courierLatencyMilliseconds);
	var courierConcurrency = courierMaxConcurrentLookups.Value;
	builder.Services.AddSingleton<ICourierTrackingService>(services =>
		new SimulatedCourierTrackingService(
			courierTrackingStatus,
			courierConcurrency,
			courierTimeout,
			courierLatency,
			services.GetRequiredService<TimeProvider>()));
}

builder.Services.AddScoped<AdministrationApplicationService>();
builder.Services.AddScoped<StaffAuthenticationService>();
builder.Services.AddScoped<StaffCookieAuthenticationEvents>();
builder.Services.AddScoped<AuditActionFilter>();
builder.Services.AddScoped<ValidationActionFilter>();

var lowStockThresholdValue =
	builder.Configuration["Reports:LowStockThreshold"];
int? lowStockThreshold = null;
if (lowStockThresholdValue is not null)
{
	if (!int.TryParse(
		lowStockThresholdValue,
		NumberStyles.Integer,
		CultureInfo.InvariantCulture,
		out var configuredLowStockThreshold) ||
		configuredLowStockThreshold < 0)
	{
		throw new InvalidOperationException(
			"Reports:LowStockThreshold must be a non-negative integer.");
	}

	lowStockThreshold = configuredLowStockThreshold;
}

// Domain events are published after each change is saved; the simulated
// sender turns them into notification log entries.
builder.Services.AddSingleton<DomainEventPublisher>(services =>
{
	var logger = services.GetRequiredService<ILogger<DomainEventPublisher>>();
	return new DomainEventPublisher((exception, domainEvent) =>
		logger.LogError(
			exception,
			"A notification subscriber failed while handling {DomainEventType}.",
			domainEvent.GetType().Name));
});
builder.Services.AddSingleton<SimulatedNotificationSender>();

builder.Services.AddScoped<TransactionApplicationService>(services =>
	new TransactionApplicationService(
		services.GetRequiredService<IStaffRepository>(),
		services.GetRequiredService<IBranchRepository>(),
		services.GetRequiredService<IStockRepository>(),
		services.GetRequiredService<ICounterTransactionRepository>(),
		services.GetRequiredService<IUnitOfWork>(),
		services.GetRequiredService<TimeProvider>(),
		services.GetRequiredService<DomainEventPublisher>(),
		lowStockThreshold));
builder.Services.AddScoped<StockAdjustmentApplicationService>(services =>
	new StockAdjustmentApplicationService(
		services.GetRequiredService<IStaffRepository>(),
		services.GetRequiredService<IBranchRepository>(),
		services.GetRequiredService<IProductRepository>(),
		services.GetRequiredService<IStockRepository>(),
		services.GetRequiredService<IUnitOfWork>(),
		services.GetRequiredService<DomainEventPublisher>(),
		lowStockThreshold,
		services.GetRequiredService<TimeProvider>()));

builder.Services.AddScoped<LowStockReportApplicationService>(services =>
	new LowStockReportApplicationService(
		services.GetRequiredService<IStaffRepository>(),
		services.GetRequiredService<IReportQueryService>(),
		lowStockThreshold));
builder.Services.AddScoped<BranchDashboardApplicationService>(services =>
	new BranchDashboardApplicationService(
		services.GetRequiredService<IStaffRepository>(),
		services.GetRequiredService<IReportQueryService>(),
		services.GetRequiredService<IScreenQueryService>(),
		services.GetRequiredService<ReferenceDataCache>(),
		services.GetRequiredService<TimeProvider>(),
		lowStockThreshold,
		services.GetService<ICourierTrackingService>()));
builder.Services.AddScoped<ProductCatalogueApplicationService>();
builder.Services.AddScoped<ScreenQueryApplicationService>();
builder.Services.AddScoped<ParcelStatusApplicationService>(services =>
	new ParcelStatusApplicationService(
		services.GetRequiredService<ICounterTransactionRepository>(),
		services.GetService<ICourierTrackingService>(),
		services.GetRequiredService<IScreenQueryService>()));
builder.Services.AddScoped<ShippingVolumeReportApplicationService>();
builder.Services.AddScoped<BranchAuditorReportApplicationService>();
builder.Services.AddScoped<DailyBranchSalesReportApplicationService>();

var slowActionThresholdMilliseconds = builder.Configuration.GetValue<double>(
	"Performance:SlowActionThresholdMilliseconds", 500d);
var slowActionThreshold = PerformanceActionFilter.ParseThresholdMilliseconds(
	slowActionThresholdMilliseconds);

builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddScoped<PerformanceActionFilter>(services =>
	new PerformanceActionFilter(
		slowActionThreshold,
		services.GetRequiredService<TimeProvider>(),
		services.GetRequiredService<ILogger<PerformanceActionFilter>>()));

var moneyOrderThresholdAmount =
	builder.Configuration["MoneyOrders:HighValueThreshold:Amount"];
var moneyOrderThresholdCurrency =
	builder.Configuration["MoneyOrders:HighValueThreshold:Currency"];
Money? moneyOrderHighValueThreshold = null;

if (!string.IsNullOrWhiteSpace(moneyOrderThresholdAmount) ||
	!string.IsNullOrWhiteSpace(moneyOrderThresholdCurrency))
{
	if (string.IsNullOrWhiteSpace(moneyOrderThresholdAmount) ||
		string.IsNullOrWhiteSpace(moneyOrderThresholdCurrency) ||
		!decimal.TryParse(
			moneyOrderThresholdAmount,
			NumberStyles.Number,
			CultureInfo.InvariantCulture,
			out var thresholdAmount))
	{
		throw new InvalidOperationException(
			"Configure both MoneyOrders:HighValueThreshold:Amount and Currency with valid values.");
	}

	moneyOrderHighValueThreshold = new Money(
		thresholdAmount, moneyOrderThresholdCurrency);
}

var moneyOrderFeeAmount =
	builder.Configuration["MoneyOrders:Fee:Amount"];
var moneyOrderFeeTaxCategory =
	builder.Configuration["MoneyOrders:Fee:TaxCategory"];
MoneyOrderBasketSettings? moneyOrderBasketSettings = null;
if (!string.IsNullOrWhiteSpace(moneyOrderFeeAmount) ||
	!string.IsNullOrWhiteSpace(moneyOrderFeeTaxCategory))
{
	if (moneyOrderHighValueThreshold is null ||
		!decimal.TryParse(
			moneyOrderFeeAmount,
			NumberStyles.Number,
			CultureInfo.InvariantCulture,
			out var feeAmount) ||
		feeAmount < 0m ||
		!Enum.TryParse<TaxCategory>(
			moneyOrderFeeTaxCategory,
			ignoreCase: true,
			out var feeTaxCategory) ||
		!Enum.IsDefined(feeTaxCategory))
	{
		throw new InvalidOperationException(
			"Configure MoneyOrders:HighValueThreshold, a non-negative MoneyOrders:Fee:Amount, and a defined MoneyOrders:Fee:TaxCategory together.");
	}

	moneyOrderBasketSettings = new MoneyOrderBasketSettings(
		moneyOrderHighValueThreshold,
		new Money(feeAmount, moneyOrderHighValueThreshold.Currency),
		feeTaxCategory);
}

var acceptedDocumentTypes = builder.Configuration
	.GetSection("MoneyOrders:Verification:AcceptedDocumentTypes")
	.Get<string[]>() ?? [];
if (acceptedDocumentTypes.Length > 0)
{
	var verificationTimeoutSeconds = builder.Configuration.GetValue<double?>(
		"MoneyOrders:Verification:TimeoutSeconds");
	var simulatedLatencyMilliseconds = builder.Configuration.GetValue<double?>(
		"MoneyOrders:Verification:SimulatedLatencyMilliseconds") ?? 0d;
	if (verificationTimeoutSeconds is not { } timeoutSeconds ||
		!double.IsFinite(timeoutSeconds) || timeoutSeconds <= 0d ||
		!double.IsFinite(simulatedLatencyMilliseconds) ||
		simulatedLatencyMilliseconds < 0d)
	{
		throw new InvalidOperationException(
			"Configure a positive MoneyOrders:Verification:TimeoutSeconds (and a non-negative SimulatedLatencyMilliseconds, if set) when accepted document types are configured.");
	}

	var verificationTimeout = TimeSpan.FromSeconds(timeoutSeconds);
	var simulatedLatency = TimeSpan.FromMilliseconds(
		simulatedLatencyMilliseconds);
	builder.Services.AddSingleton<IIdentityVerificationService>(services =>
		new SimulatedIdentityVerificationService(
			acceptedDocumentTypes,
			verificationTimeout,
			simulatedLatency,
			services.GetRequiredService<TimeProvider>()));
}

builder.Services.AddScoped<MoneyOrderApplicationService>(services =>
	new MoneyOrderApplicationService(
		services.GetRequiredService<IStaffRepository>(),
		services.GetRequiredService<ICounterTransactionRepository>(),
		moneyOrderHighValueThreshold));

var configuredTaxRates = new Dictionary<TaxCategory, decimal>();
foreach (var taxCategory in Enum.GetValues<TaxCategory>())
{
	var configuredRate = builder.Configuration[
		$"Transactions:TaxRates:{taxCategory}"];
	if (configuredRate is null)
		continue;
	if (!decimal.TryParse(
		configuredRate,
		NumberStyles.Number,
		CultureInfo.InvariantCulture,
		out var taxRate) ||
		taxRate < 0m)
	{
		throw new InvalidOperationException(
			$"Transactions:TaxRates:{taxCategory} must be a non-negative decimal.");
	}

	configuredTaxRates.Add(taxCategory, taxRate);
}

var configuredMaximumParcelWeights =
	new Dictionary<ShippingServiceLevel, Weight>();
foreach (var serviceLevel in Enum.GetValues<ShippingServiceLevel>())
{
	var configuredWeight = builder.Configuration[
		$"Shipping:MaximumParcelWeightKilograms:{serviceLevel}"];
	if (configuredWeight is null)
		continue;
	if (!decimal.TryParse(
		configuredWeight,
		NumberStyles.Number,
		CultureInfo.InvariantCulture,
		out var kilograms) ||
		kilograms <= 0m)
	{
		throw new InvalidOperationException(
			$"Shipping:MaximumParcelWeightKilograms:{serviceLevel} must be a positive decimal.");
	}

	configuredMaximumParcelWeights.Add(
		serviceLevel, new Weight(kilograms));
}

// Payment method names only (for example as shown on the counter screen);
// no defaults, so counter sales stay disabled until configured.
var configuredPaymentMethods = builder.Configuration
	.GetSection("Transactions:PaymentMethods")
	.Get<string[]>() ?? [];
if (configuredPaymentMethods.Any(string.IsNullOrWhiteSpace) ||
	configuredPaymentMethods
		.Select(method => method.Trim())
		.Distinct(StringComparer.OrdinalIgnoreCase)
		.Count() != configuredPaymentMethods.Length)
{
	throw new InvalidOperationException(
		"Transactions:PaymentMethods must contain distinct, non-blank names.");
}

TaxCategory? configuredShippingTaxCategory = null;
var configuredShippingTaxCategoryValue =
	builder.Configuration["Shipping:TaxCategory"];
if (configuredShippingTaxCategoryValue is not null)
{
	if (!Enum.TryParse<TaxCategory>(
		configuredShippingTaxCategoryValue,
		ignoreCase: true,
		out var shippingTaxCategory) ||
		!Enum.IsDefined(shippingTaxCategory))
	{
		throw new InvalidOperationException(
			"Shipping:TaxCategory must name a defined tax category.");
	}

	configuredShippingTaxCategory = shippingTaxCategory;
}

builder.Services.AddScoped<MixedBasketApplicationService>(services =>
	new MixedBasketApplicationService(
		services.GetRequiredService<IStaffRepository>(),
		services.GetRequiredService<IBranchRepository>(),
		services.GetRequiredService<IProductRepository>(),
		services.GetRequiredService<IStockRepository>(),
		services.GetRequiredService<ICounterTransactionRepository>(),
		services.GetRequiredService<IUnitOfWork>(),
		configuredTaxRates,
		configuredMaximumParcelWeights,
		configuredShippingTaxCategory,
		services.GetRequiredService<TimeProvider>(),
		moneyOrderBasketSettings,
		services.GetService<IIdentityVerificationService>(),
		services.GetRequiredService<DomainEventPublisher>(),
		lowStockThreshold,
		configuredPaymentMethods,
		services.GetRequiredService<IScreenQueryService>()));

var signInPermitLimit = builder.Configuration.GetValue<int>(
	"Authentication:SignInRateLimit:PermitLimit", 5);

var signInWindowSeconds = builder.Configuration.GetValue<int>(
	"Authentication:SignInRateLimit:WindowSeconds", 60);

if (signInPermitLimit <= 0 || signInWindowSeconds <= 0)
{
	throw new InvalidOperationException(
		"Sign-in rate limit and window settings must be positive.");
}

builder.Services.AddRateLimiter(options =>
{
	options.RejectionStatusCode =
		StatusCodes.Status429TooManyRequests;

	options.AddPolicy("staff-sign-in", context =>
		RateLimitPartition.GetFixedWindowLimiter(
			context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
			_ => new FixedWindowRateLimiterOptions
			{
				PermitLimit = signInPermitLimit,
				Window = TimeSpan.FromSeconds(signInWindowSeconds),
				QueueLimit = 0,
				AutoReplenishment = true
			}));
});

var protectedPartnerPathPrefixes = builder.Configuration
	.GetSection("ApiSecurity:ProtectedPathPrefixes")
	.Get<string[]>() ?? [];
// Partner endpoints ([RequireApiKey] metadata, or a configured path prefix)
// accept requests only when an API key is configured; without one they fail
// closed with 500. Whenever partner access is possible, the per-partner rate
// limit is required, so every validated partner request is rate limited.
var partnerAccessEnabled = protectedPartnerPathPrefixes.Length > 0 ||
	!string.IsNullOrWhiteSpace(builder.Configuration["ApiSecurity:ApiKey"]);
PartitionedRateLimiter<HttpContext>? partnerRateLimiter = null;
if (partnerAccessEnabled)
{
	var partnerPermitLimit = builder.Configuration.GetValue<int?>(
		"ApiSecurity:PartnerRateLimit:PermitLimit");
	var partnerWindowSeconds = builder.Configuration.GetValue<int?>(
		"ApiSecurity:PartnerRateLimit:WindowSeconds");
	if (partnerPermitLimit is null or <= 0 ||
		partnerWindowSeconds is null or <= 0)
	{
		throw new InvalidOperationException(
			"Configure positive ApiSecurity:PartnerRateLimit:PermitLimit and WindowSeconds when a partner API key or partner path prefixes are configured.");
	}

	partnerRateLimiter = PartitionedRateLimiter.Create<HttpContext, string>(
		context =>
		{
			if (!context.Items.TryGetValue(
				PartnerRateLimitingMiddleware.PartnerIdentityItemKey,
				out var identity) ||
				identity is not string partnerIdentity)
			{
				return RateLimitPartition.GetNoLimiter("unvalidated");
			}

			return RateLimitPartition.GetFixedWindowLimiter(
				partnerIdentity,
				_ => new FixedWindowRateLimiterOptions
				{
					PermitLimit = partnerPermitLimit.Value,
					Window = TimeSpan.FromSeconds(partnerWindowSeconds.Value),
					QueueLimit = 0,
					AutoReplenishment = true
				});
		});
	builder.Services.AddSingleton(partnerRateLimiter);
}

var app = builder.Build();

app.Services.GetRequiredService<SimulatedNotificationSender>()
	.Subscribe(app.Services.GetRequiredService<DomainEventPublisher>());

if (app.Configuration.GetValue<bool>("DemoData:Initialize"))
{
	await using var seedScope = app.Services.CreateAsyncScope();
	await seedScope.ServiceProvider
		.GetRequiredService<SqlDemoDataSeeder>()
		.InitializeAsync();

	if (demoStaffPassword is null)
	{
		app.Logger.LogWarning(
			"Demo data is enabled but DemoData:StaffPassword is not set, so demo staff cannot sign in.");
	}
	else
	{
		var added = await seedScope.ServiceProvider
			.GetRequiredService<DemoCredentialProvisioner>()
			.ProvisionAsync(demoStaffPassword);
		app.Logger.LogInformation(
			"Provisioned {Count} demo staff login(s).", added);
	}
}

// Configure the HTTP request pipeline.
app.UseMiddleware<CorrelationIdMiddleware>();

app.UseExceptionHandler();

if (!app.Environment.IsDevelopment())
{
	// Add HSTS in non-development environments.
	app.UseHsts();
}

app.UseMiddleware<SecurityHeadersMiddleware>();

app.UseMiddleware<MaintenanceModeMiddleware>();

app.UseHttpsRedirection();
app.UseRouting();
app.UseCors("ParcelTracking");

app.UseMiddleware<ApiKeyAndIpRestrictionMiddleware>();
app.UseMiddleware<AdminIpRestrictionMiddleware>();
if (partnerRateLimiter is not null)
	app.UseMiddleware<PartnerRateLimitingMiddleware>();

app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapStaticAssets();

app.MapGet("/health", () => Results.Ok(new { status = "Healthy" }));
if (databaseHealthEnabled)
	app.MapHealthChecks("/health/database");

app.MapControllerRoute(
	name: "default",
	pattern: "{controller=Home}/{action=Index}/{id?}")
	.WithStaticAssets();

app.Run();

public partial class Program { }
