using Microsoft.AspNetCore.Authentication.Cookies;
using Orapmshms.Services;
using Orapmshms.Filters;
using Orapmshms.Services.AvailabilityJobs;

var builder = WebApplication.CreateBuilder(args);

// MVC
builder.Services.AddControllersWithViews(options =>
{
    // ASP.NET Core replacement for Site.Master Page_Load validation on all PMS pages.
    options.Filters.AddService<PmsMasterActionFilter>();
});

// Cache + Session
builder.Services.AddMemoryCache();
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromMinutes(40);
    options.Cookie.Name = ".ORAPMS.Session";
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
});

// Cookie authentication
builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/LoginHMS";
        options.AccessDeniedPath = "/LoginHMS";
        options.Cookie.Name = ".ORAPMS.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.SlidingExpiration = true;
        options.ExpireTimeSpan = TimeSpan.FromMinutes(40);
    });

builder.Services.AddHttpContextAccessor();
//builder.Services.Configure<SmtpOptions>(builder.Configuration.GetSection("Smtp"));
builder.Services.AddHttpClient("AutoChargeWorker", client =>
{
    client.Timeout = TimeSpan.FromSeconds(15);
});
builder.Services.AddHttpClient("ChannelManager", client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
});

// =========================================================
// ORAPMS application services
// These registrations MUST be before builder.Build().
// =========================================================
builder.Services.AddSingleton<ILegacyUrlSigner, LegacyUrlSigner>();
builder.Services.AddSingleton<IHotelClock, HotelClock>();
builder.Services.AddSingleton<LoginBackgroundWorker>();
builder.Services.AddSingleton<ILoginBackgroundQueue>(sp => sp.GetRequiredService<LoginBackgroundWorker>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<LoginBackgroundWorker>());
builder.Services.AddScoped<ILoginService, LoginService>();
builder.Services.AddScoped<IYourHotelsService, YourHotelsService>();
builder.Services.AddScoped<IAvailabilityService, AvailabilityService>();
builder.Services.AddSingleton<AvailabilityAutoUpdateWorker>();
builder.Services.AddSingleton<IAvailabilityAutoUpdateQueue>(sp => sp.GetRequiredService<AvailabilityAutoUpdateWorker>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<AvailabilityAutoUpdateWorker>());
builder.Services.AddSingleton<AvailabilityChannelSyncWorker>();
builder.Services.AddSingleton<IAvailabilityChannelSyncQueue>(sp => sp.GetRequiredService<AvailabilityChannelSyncWorker>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<AvailabilityChannelSyncWorker>());



builder.Services.AddScoped<IPmsMasterService, PmsMasterService>();
builder.Services.AddScoped<PmsMasterActionFilter>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/LoginHMS/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

// Session must run before MVC actions that access HttpContext.Session.
app.UseSession();

// Authentication must run before Authorization.
app.UseAuthentication();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=LoginHMS}/{action=Index}/{id?}");

app.Run();
