using BarberHub.Web.Domain.Constants;
using BarberHub.Web.Domain.Entities;
using BarberHub.Web.Features.Notifications;
using BarberHub.Web.Features.Payments;
using BarberHub.Web.Shared.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Stripe;

namespace BarberHub.Web.Features.Orders;

[Authorize]
public class OrdersController : Controller
{
    private readonly IOrderService _service;
    private readonly IStripeService _stripe;
    private readonly ICurrentUserService _currentUser;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly INotificationService _notifications;
    private readonly IConfiguration _configuration;
    private readonly ILogger<OrdersController> _logger;

    public OrdersController(
        IOrderService service,
        IStripeService stripe,
        ICurrentUserService currentUser,
        UserManager<ApplicationUser> userManager,
        INotificationService notifications,
        IConfiguration configuration,
        ILogger<OrdersController> logger)
    {
        _service = service;
        _stripe = stripe;
        _currentUser = currentUser;
        _userManager = userManager;
        _notifications = notifications;
        _configuration = configuration;
        _logger = logger;
    }

    [HttpGet, Authorize(Roles = AppRoles.User)]
    public IActionResult Checkout() => View(new CheckoutDto());

    [HttpPost, ValidateAntiForgeryToken, Authorize(Roles = AppRoles.User)]
    public async Task<IActionResult> Checkout(CheckoutDto dto)
    {
        if (!ModelState.IsValid) return View(dto);

        var result = await _service.CheckoutAsync(
            _currentUser.UserId!, _currentUser.Email!, dto);

        if (!result.IsSuccess)
        {
            ModelState.AddModelError(string.Empty, result.Error!);
            return View(dto);
        }

        // Notify barber(s) and/or SuperAdmins that a new order is in
        await _service.NotifyOrderOwnersAsync(result.Value.orderId);

        // For COD, skip Stripe and go straight to success page
        if (result.Value.isCod)
        {
            TempData["Success"] = "Order placed! Pay cash on delivery.";
            return RedirectToAction(nameof(Success), new { id = result.Value.orderId });
        }

        return RedirectToAction(nameof(Pay), new { id = result.Value.orderId, clientSecret = result.Value.clientSecret });
    }

    [HttpGet, Authorize(Roles = AppRoles.User)]
    public IActionResult Pay(Guid id, string clientSecret)
    {
        ViewBag.PublishableKey = _configuration["Stripe:PublishableKey"];
        ViewBag.ClientSecret = clientSecret;
        ViewBag.OrderId = id;
        return View();
    }

    [HttpGet, Authorize(Roles = AppRoles.User)]
    public async Task<IActionResult> Success(Guid id)
    {
        var order = await _service.GetByIdAsync(id);
        if (order is null) return NotFound();
        return View(order);
    }

    [HttpGet, Authorize(Roles = AppRoles.User)]
    public async Task<IActionResult> MyOrders()
    {
        var orders = await _service.GetUserOrdersAsync(_currentUser.UserId!);
        return View(orders);
    }

    [HttpGet, Authorize(Roles = AppRoles.Admin)]
    public async Task<IActionResult> Manage()
    {
        var orders = await _service.GetBarberOrdersAsync(_currentUser.UserId!);
        return View(orders);
    }

    /// <summary>
    /// Seller (barber or SuperAdmin) marks a Cash-on-Delivery order as paid after receiving cash.
    /// </summary>
    [HttpPost, ValidateAntiForgeryToken]
    [Authorize(Roles = AppRoles.Admin + "," + AppRoles.SuperAdmin)]
    public async Task<IActionResult> MarkAsPaid(Guid id, string? returnTo = null)
    {
        var user = await _userManager.GetUserAsync(User);
        var isSuperAdmin = user is not null && await _userManager.IsInRoleAsync(user, AppRoles.SuperAdmin);

        var result = await _service.MarkAsPaidByOwnerAsync(id, _currentUser.UserId!, isSuperAdmin);

        if (result.IsSuccess)
        {
            TempData["Success"] = "Order marked as paid.";
            var order = await _service.GetByIdAsync(id);
            var buyerId = await _service.GetUserIdForOrderAsync(id);
            if (order is not null && !string.IsNullOrEmpty(buyerId))
            {
                await _notifications.NotifyAsync(
                    buyerId,
                    NotificationType.OrderPlaced,
                    "Payment received",
                    $"Your COD order {order.OrderNumber} has been marked as paid by the seller.",
                    "/Orders/MyOrders");
            }
        }
        else
        {
            TempData["Error"] = result.Error;
        }

        // Route back to where they came from
        if (returnTo == "marketplace") return RedirectToAction("Orders", "SuperAdmin");
        return RedirectToAction(isSuperAdmin ? "Orders" : nameof(Manage), isSuperAdmin ? "SuperAdmin" : "Orders");
    }

    // Stripe webhook - must be anonymous
    [HttpPost("stripe/webhook"), AllowAnonymous, IgnoreAntiforgeryToken]
    public async Task<IActionResult> StripeWebhook()
    {
        var json = await new StreamReader(HttpContext.Request.Body).ReadToEndAsync();
        try
        {
            var stripeEvent = _stripe.ConstructWebhookEvent(
                json, Request.Headers["Stripe-Signature"]!);

            if (stripeEvent.Type == "payment_intent.succeeded")
            {
                var intent = stripeEvent.Data.Object as PaymentIntent;
                if (intent is not null)
                    await _service.MarkPaidAsync(intent.Id);
            }

            return Ok();
        }
        catch (StripeException ex)
        {
            _logger.LogError(ex, "Stripe webhook failed");
            return BadRequest();
        }
    }
}
