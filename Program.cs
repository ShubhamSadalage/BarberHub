using BarberHub.Web.Domain.Entities;
using BarberHub.Web.Features.Auth.Services;
using BarberHub.Web.Features.Barbers.Repositories;
using BarberHub.Web.Features.Barbers.Services;
using BarberHub.Web.Features.Bookings.Repositories;
using BarberHub.Web.Features.Bookings.Services;
using BarberHub.Web.Features.Cart;
using BarberHub.Web.Features.Chat;
using BarberHub.Web.Features.Orders;
using BarberHub.Web.Features.Payments;
using BarberHub.Web.Features.Products.Repositories;
using BarberHub.Web.Features.Products.Services;
using BarberHub.Web.Infrastructure.Persistence;
using BarberHub.Web.Shared.Repositories;
using BarberHub.Web.Shared.Services;
using FluentValidation;
using FluentValidation.AspNetCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// ----- Database -----
builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

// ----- Strongly-typed configuration -----
builder.Services.Configure<BarberHub.Web.Shared.Config.FeatureFlags>(
    builder.Configuration.GetSection("Features"));
builder.Services.Configure<BarberHub.Web.Shared.Config.OtpOptions>(
    builder.Configuration.GetSection("Otp"));
builder.Services.Configure<BarberHub.Web.Shared.Config.SmsOptions>(
    builder.Configuration.GetSection("Sms"));
builder.Services.Configure<BarberHub.Web.Shared.Config.GoogleOptions>(
    builder.Configuration.GetSection("Google"));
builder.Services.Configure<BarberHub.Web.Shared.Config.WebPushOptions>(
    builder.Configuration.GetSection("WebPush"));
builder.Services.Configure<BarberHub.Web.Shared.Config.QueueOptions>(
    builder.Configuration.GetSection("Queue"));
builder.Services.Configure<BarberHub.Web.Shared.Config.RateLimitOptions>(
    builder.Configuration.GetSection("RateLimit"));

// ----- Identity -----
builder.Services
    .AddIdentity<ApplicationUser, IdentityRole>(options =>
    {
        options.Password.RequireDigit = true;
        options.Password.RequiredLength = 6;
        options.Password.RequireNonAlphanumeric = false;
        options.Password.RequireUppercase = false;
        options.Password.RequireLowercase = false;

        options.User.RequireUniqueEmail = true;
        options.SignIn.RequireConfirmedAccount = false;

        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
        options.Lockout.MaxFailedAccessAttempts = 5;
    })
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddDefaultTokenProviders();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/Auth/Login";
    options.LogoutPath = "/Auth/Logout";
    options.AccessDeniedPath = "/Auth/AccessDenied";
    options.ExpireTimeSpan = TimeSpan.FromDays(30);
    options.SlidingExpiration = true;
});

// ----- Google authentication -----
// Always register the Google handler so the scheme exists.
// If keys are missing/empty the handler is registered but GoogleAuthController
// will catch that and show a friendly error instead of crashing.
{
    var googleClientId = builder.Configuration["Google:OAuth:ClientId"] ?? "";
    var googleSecret   = builder.Configuration["Google:OAuth:ClientSecret"] ?? "";

    // Use placeholder values when keys are not configured so the scheme is still
    // registered — clicking the button will hit GoogleAuthController which checks
    // the feature flag and shows a "not configured" message instead of throwing.
        builder.Services.AddAuthentication()
            .AddGoogle(options =>
            {
            options.ClientId     = string.IsNullOrEmpty(googleClientId) ? "not-configured" : googleClientId;
            options.ClientSecret = string.IsNullOrEmpty(googleSecret)   ? "not-configured" : googleSecret;
            options.CallbackPath = "/GoogleAuth/Callback";
            options.SaveTokens   = true;
            });
    }

// ----- MVC + Views -----
builder.Services.AddControllersWithViews();
builder.Services.AddRazorPages();
builder.Services.AddHttpContextAccessor();

// ----- AutoMapper -----
builder.Services.AddAutoMapper(typeof(Program).Assembly);

// ----- FluentValidation -----
builder.Services.AddFluentValidationAutoValidation();
builder.Services.AddValidatorsFromAssemblyContaining<Program>();

// ----- SignalR (Chat) -----
builder.Services.AddSignalR();

// ----- Shared services -----
builder.Services.AddScoped<ICurrentUserService, CurrentUserService>();
builder.Services.AddScoped<IEmailService, EmailService>();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<ISmsService, SmsService>();
builder.Services.AddScoped<IOtpService, OtpService>();
builder.Services.AddScoped<IPushService, PushService>();
builder.Services.AddScoped<ITwoFactorService, TwoFactorService>();
builder.Services.AddScoped<IAuditService, AuditService>();
builder.Services.AddScoped(typeof(IGenericRepository<>), typeof(GenericRepository<>));

// ----- Rate limiting: protect Login + OTP endpoints -----
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    var rateOpts = builder.Configuration.GetSection("RateLimit").Get<BarberHub.Web.Shared.Config.RateLimitOptions>()
                   ?? new BarberHub.Web.Shared.Config.RateLimitOptions();

    options.AddPolicy("login", httpContext =>
        System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "anon",
            factory: _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
            {
                PermitLimit = rateOpts.LoginPerMinute,
                Window = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = System.Threading.RateLimiting.QueueProcessingOrder.OldestFirst,
                QueueLimit = 0
            }));

    options.AddPolicy("otp", httpContext =>
        System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "anon",
            factory: _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
            {
                PermitLimit = rateOpts.OtpPerHour,
                Window = TimeSpan.FromHours(1),
                QueueProcessingOrder = System.Threading.RateLimiting.QueueProcessingOrder.OldestFirst,
                QueueLimit = 0
            }));
});

// ----- Feature: Auth -----
builder.Services.AddScoped<IAuthService, AuthService>();

// ----- Feature: Barbers -----
builder.Services.AddScoped<IBarberRepository, BarberRepository>();
builder.Services.AddScoped<IBarberService, BarberService>();

// ----- Feature: Bookings -----
builder.Services.AddScoped<IBookingRepository, BookingRepository>();
builder.Services.AddScoped<IBookingService, BookingService>();

// ----- Feature: Products -----
builder.Services.AddScoped<IProductRepository, ProductRepository>();
builder.Services.AddScoped<IProductService, ProductService>();

// ----- Feature: Cart + Orders + Payments -----
builder.Services.AddScoped<ICartService, CartService>();
builder.Services.AddScoped<IOrderService, OrderService>();
builder.Services.AddSingleton<IStripeService, StripeService>();

// ----- Feature: Notifications -----
builder.Services.AddScoped<BarberHub.Web.Features.Notifications.INotificationService,
                          BarberHub.Web.Features.Notifications.NotificationService>();

// ----- Background workers -----
builder.Services.AddHostedService<BarberHub.Web.Features.Bookings.Services.BookingQueueWorker>();

var app = builder.Build();

// ----- Pipeline -----
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();

// Serve .well-known/assetlinks.json for TWA (Play Store) verification
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(
        System.IO.Path.Combine(builder.Environment.WebRootPath, ".well-known")),
    RequestPath = "/.well-known",
    ServeUnknownFileTypes = true,  // .json files are known, but this is a safety net
    DefaultContentType = "application/json"
});

app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// Health check — also used by Render keep-alive ping
app.MapGet("/health", () => Results.Ok(new { status = "ok", time = DateTime.UtcNow }));

// Routes
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.MapHub<ChatHub>("/hubs/chat");
app.MapHub<BarberHub.Web.Features.Notifications.NotificationHub>("/hubs/notifications");

// ----- Seed DB -----
using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;
    try
    {
        var context = services.GetRequiredService<ApplicationDbContext>();
        var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();
        var roleManager = services.GetRequiredService<RoleManager<IdentityRole>>();
        await DataSeeder.SeedAsync(context, userManager, roleManager);
    }
    catch (Exception ex)
    {
        var logger = services.GetRequiredService<ILogger<Program>>();
        logger.LogError(ex, "Error seeding database");
    }
}

app.Run();
