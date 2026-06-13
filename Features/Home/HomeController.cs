using BarberHub.Web.Features.Barbers.Services;
using BarberHub.Web.Shared.Services;
using Microsoft.AspNetCore.Mvc;

namespace BarberHub.Web.Features.Home;

public class HomeController : Controller
{
    private readonly IBarberService _barbers;
    private readonly ICurrentUserService _currentUser;

    private readonly ILogger<HomeController> _logger;

    public HomeController(
        IBarberService barbers,
        ICurrentUserService currentUser,
        ILogger<HomeController> logger)
    {
        _barbers = barbers;
        _currentUser = currentUser;
        _logger = logger;
    }

    /// <summary>
    /// Home. Optional ?lat=&amp;lng=... query string lets the page re-render
    /// with live-location results after the user grants geolocation permission.
    /// </summary>
    public async Task<IActionResult> Index(double? lat = null, double? lng = null)
    {
        try
        {
            _logger.LogInformation(
                "Home page requested. UserId={UserId}, Lat={Lat}, Lng={Lng}",
                _currentUser.UserId,
                lat,
                lng);

            var data = await _barbers.GetForHomeAsync(
                _currentUser.UserId,
                lat,
                lng,
                limit: 12);

            _logger.LogInformation(
                "Home page loaded successfully. UserId={UserId}, BarberCount={Count}",
                _currentUser.UserId,
                data?.Items?.Count ?? 0);

            return View(data);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to load home page. UserId={UserId}, Lat={Lat}, Lng={Lng}",
                _currentUser.UserId,
                lat,
                lng);

            throw;
        }
    }

    public IActionResult Privacy()
    {
        _logger.LogInformation(
            "Privacy page viewed. UserId={UserId}",
            _currentUser.UserId);

        return View();
    }
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        _logger.LogWarning(
            "Error page displayed. UserId={UserId}",
            _currentUser.UserId);

        return View();
    }
}
