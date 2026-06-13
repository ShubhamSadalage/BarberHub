using BarberHub.Web.Domain.Constants;
using BarberHub.Web.Domain.Entities;
using BarberHub.Web.Features.Auth.Dtos;
using BarberHub.Web.Features.Auth.Services;
using BarberHub.Web.Features.Notifications;
using BarberHub.Web.Shared.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace BarberHub.Web.Features.Auth.Controllers;

public class AuthController : Controller
{
    private readonly IAuthService _authService;
    private readonly ICurrentUserService _currentUser;
    private readonly IWebHostEnvironment _env;
    private readonly INotificationService _notifications;
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly ITwoFactorService _twoFactor;
    private readonly IOtpService _otp;
    private readonly ILogger<AuthController> _logger;

    public AuthController(
        IAuthService authService,
        ICurrentUserService currentUser,
        IWebHostEnvironment env,
        INotificationService notifications,
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager,
        ITwoFactorService twoFactor,
        IOtpService otp,
        ILogger<AuthController> logger)
    {
        _authService = authService;
        _currentUser = currentUser;
        _env = env;
        _notifications = notifications;
        _userManager = userManager;
        _signInManager = signInManager;
        _twoFactor = twoFactor;
        _otp = otp;
        _logger = logger;
    }
    [HttpGet]
    public IActionResult Register() => View(new RegisterDto());

    [HttpGet, Authorize]
    public async Task<IActionResult> PendingApproval()
    {
        var userId = _currentUser.UserId!;
        var user = await _authService.GetByIdAsync(userId);
        if (user is null) return RedirectToAction(nameof(Login));
        return View(user);
    }

    [HttpPost, ValidateAntiForgeryToken]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("otp")]
    public async Task<IActionResult> Register(RegisterDto dto)
    {
        if (!ModelState.IsValid) return View(dto);

        // Pre-flight: email/phone uniqueness
        var pre = await _authService.RegisterAsync(dto);
        if (!pre.IsSuccess)
        {
            foreach (var err in pre.Errors) ModelState.AddModelError(string.Empty, err);
            return View(dto);
        }

        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        var emailOtp = await _otp.IssueAsync(dto.Email, OtpChannel.Email, OtpPurpose.Login, ip);
        var smsOtp = await _otp.IssueAsync(dto.PhoneNumber, OtpChannel.Sms, OtpPurpose.Login, ip);

        if (!emailOtp.Sent)
        {
            ModelState.AddModelError(string.Empty, $"Email code: {emailOtp.Error ?? "could not send."}");
            return View(dto);
        }
        if (!smsOtp.Sent)
        {
            ModelState.AddModelError(string.Empty, $"Mobile code: {smsOtp.Error ?? "could not send."}");
            return View(dto);
        }

        // Stash the registration form in TempData so the next request (Verify) can pull it.
        // TempData is a signed/encrypted cookie by default in ASP.NET Core.
        TempData["pending.register"] = System.Text.Json.JsonSerializer.Serialize(dto);

        TempData["Success"] = $"We sent verification codes to {dto.Email} and {dto.PhoneNumber}.";
        if (!string.IsNullOrEmpty(smsOtp.DebugCode))
        {
            TempData["Success"] += $" (Test mode mobile code: {smsOtp.DebugCode})";
        }

        return RedirectToAction(nameof(RegisterVerify),
            new { email = dto.Email, phone = dto.PhoneNumber });
    }

    [HttpGet]
    public IActionResult RegisterVerify(string email, string phone)
    {
        if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(phone))
            return RedirectToAction(nameof(Register));
        return View(new RegisterVerifyDto { Email = email, PhoneNumber = phone });
    }

    [HttpPost, ValidateAntiForgeryToken, ActionName("RegisterVerify")]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("otp")]
    public async Task<IActionResult> RegisterVerifyPost(RegisterVerifyDto v)
    {
        if (!ModelState.IsValid) return View(v);

        // Pull the stashed form from TempData
        var json = TempData.Peek("pending.register") as string;  // peek so a failed verify can retry
        if (string.IsNullOrEmpty(json))
        {
            TempData["Error"] = "Your registration session has expired. Please start again.";
            return RedirectToAction(nameof(Register));
        }
        var dto = System.Text.Json.JsonSerializer.Deserialize<RegisterDto>(json);
        if (dto is null || dto.Email != v.Email || dto.PhoneNumber != v.PhoneNumber)
        {
            TempData["Error"] = "Verification details don't match. Please start again.";
            TempData.Remove("pending.register");
            return RedirectToAction(nameof(Register));
        }

        // Verify both codes — both must succeed
        var emailCheck = await _otp.VerifyAsync(v.Email, v.EmailCode, OtpPurpose.Login, OtpChannel.Email);
        if (!emailCheck.Ok)
        {
            ModelState.AddModelError(nameof(v.EmailCode), emailCheck.Error ?? "Email code is invalid.");
            return View(v);
        }
        var smsCheck = await _otp.VerifyAsync(v.PhoneNumber, v.MobileCode, OtpPurpose.Login, OtpChannel.Sms);
        if (!smsCheck.Ok)
        {
            ModelState.AddModelError(nameof(v.MobileCode), smsCheck.Error ?? "Mobile code is invalid.");
            return View(v);
        }

        // Both verified — create the account
        TempData.Remove("pending.register");

        var create = await _authService.CompleteRegistrationAsync(dto);
        if (!create.IsSuccess)
        {
            foreach (var err in create.Errors) ModelState.AddModelError(string.Empty, err);
            return View(v);
        }

        var user = create.Value!;

        if (dto.IsBarber)
        {
            await _notifications.NotifyAllSuperAdminsAsync(
                NotificationType.BarberRegistration,
                "New barber awaiting approval",
                $"{dto.FullName} ({dto.ShopName}) has registered and is waiting for approval.",
                "/SuperAdmin/Approvals");
        }

        // Auto sign-in (persistent — stay logged in across browser restarts)
        await _signInManager.SignInAsync(user, isPersistent: true);

        TempData["Success"] = dto.IsBarber
            ? "Welcome! Your barber account is pending SuperAdmin approval."
            : $"Welcome to Barber Hub, {user.FullName}!";
        return RedirectToAction("Index", "Home");
    }

    [HttpGet]
    public IActionResult Login(string? returnUrl = null)
    {
        return View(new LoginDto { ReturnUrl = returnUrl });
    }

    [HttpPost, ValidateAntiForgeryToken]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("login")]
    public async Task<IActionResult> Login(LoginDto dto)
    {
        if (!ModelState.IsValid) return View(dto);

        var result = await _authService.LoginAsync(dto);
        if (!result.IsSuccess)
        {
            ModelState.AddModelError(string.Empty, result.Error ?? "Login failed.");
            return View(dto);
        }

        // 2FA gate for SuperAdmin
        var user = result.Value!;
        var roles = await _userManager.GetRolesAsync(user);
        if (_twoFactor.IsRequiredFor(user, roles))
        {
            // Sign out the cookie; remember which user is mid-flow via a short-lived cookie
            await _signInManager.SignOutAsync();
            Response.Cookies.Append("bh.2fa.uid", user.Id, new CookieOptions
            {
                HttpOnly = true,
                Secure = Request.IsHttps,
                SameSite = SameSiteMode.Lax,
                Expires = DateTimeOffset.UtcNow.AddMinutes(10)
            });
            if (!string.IsNullOrEmpty(dto.ReturnUrl))
            {
                Response.Cookies.Append("bh.2fa.returnUrl", dto.ReturnUrl, new CookieOptions
                {
                    HttpOnly = true, Secure = Request.IsHttps, SameSite = SameSiteMode.Lax,
                    Expires = DateTimeOffset.UtcNow.AddMinutes(10)
                });
            }

            // If they haven't enrolled yet, send to enrollment; otherwise to challenge
            if (!await _userManager.GetTwoFactorEnabledAsync(user))
                return RedirectToAction(nameof(TwoFactorEnroll));
            return RedirectToAction(nameof(TwoFactorChallenge));
        }

        if (!string.IsNullOrEmpty(dto.ReturnUrl) && Url.IsLocalUrl(dto.ReturnUrl))
            return Redirect(dto.ReturnUrl);

        return RedirectToAction("Index", "Home");
    }

    // ============== Two-Factor Auth (SuperAdmin) ==============

    [HttpGet]
    public async Task<IActionResult> TwoFactorEnroll()
    {
        var uid = Request.Cookies["bh.2fa.uid"];
        if (string.IsNullOrEmpty(uid)) return RedirectToAction(nameof(Login));
        var user = await _userManager.FindByIdAsync(uid);
        if (user is null) return RedirectToAction(nameof(Login));

        var setup = await _twoFactor.EnrollAsync(user, "Barber Hub");
        return View(setup);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> TwoFactorEnroll(string code)
    {
        var uid = Request.Cookies["bh.2fa.uid"];
        if (string.IsNullOrEmpty(uid)) return RedirectToAction(nameof(Login));
        var user = await _userManager.FindByIdAsync(uid);
        if (user is null) return RedirectToAction(nameof(Login));

        var secret = await _userManager.GetAuthenticatorKeyAsync(user);
        if (string.IsNullOrEmpty(secret) || !_twoFactor.VerifyCode(secret, code))
        {
            ModelState.AddModelError(string.Empty, "Code is invalid. Please try again.");
            var setup = await _twoFactor.EnrollAsync(user, "Barber Hub");
            return View(setup);
        }

        await _twoFactor.ConfirmEnabledAsync(user);
        await _signInManager.SignInAsync(user, isPersistent: true);
        ConsumeTwoFactorCookies();
        TempData["Success"] = "Two-factor authentication enabled. Welcome.";
        return RedirectToAction("Dashboard", "SuperAdmin");
    }

    [HttpGet]
    public async Task<IActionResult> TwoFactorChallenge()
    {
        var uid = Request.Cookies["bh.2fa.uid"];
        if (string.IsNullOrEmpty(uid)) return RedirectToAction(nameof(Login));
        var user = await _userManager.FindByIdAsync(uid);
        if (user is null) return RedirectToAction(nameof(Login));
        return View();
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> TwoFactorChallenge(string code)
    {
        var uid = Request.Cookies["bh.2fa.uid"];
        if (string.IsNullOrEmpty(uid)) return RedirectToAction(nameof(Login));
        var user = await _userManager.FindByIdAsync(uid);
        if (user is null) return RedirectToAction(nameof(Login));

        var secret = await _userManager.GetAuthenticatorKeyAsync(user);
        if (string.IsNullOrEmpty(secret) || !_twoFactor.VerifyCode(secret, code))
        {
            ModelState.AddModelError(string.Empty, "Code is invalid.");
            return View();
        }

        await _signInManager.SignInAsync(user, isPersistent: true);
        var returnUrl = Request.Cookies["bh.2fa.returnUrl"];
        ConsumeTwoFactorCookies();

        if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
            return Redirect(returnUrl);
        return RedirectToAction("Dashboard", "SuperAdmin");
    }

    private void ConsumeTwoFactorCookies()
    {
        Response.Cookies.Delete("bh.2fa.uid");
        Response.Cookies.Delete("bh.2fa.returnUrl");
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize]
    public async Task<IActionResult> Logout()
    {
        await _authService.LogoutAsync();
        return RedirectToAction("Index", "Home");
    }

    [HttpGet]
    public IActionResult ForgotPassword() => View(new ForgotPasswordDto());

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ForgotPassword(ForgotPasswordDto dto)
    {
        if (!ModelState.IsValid) return View(dto);
        await _authService.ForgotPasswordAsync(dto.Email);
        TempData["Success"] = "If this email exists, we have sent a reset link.";
        return RedirectToAction(nameof(Login));
    }

    [HttpGet]
    public IActionResult ResetPassword(string? email, string? token)
    {
        if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(token))
            return RedirectToAction(nameof(Login));

        return View(new ResetPasswordDto { Email = email, Token = token });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ResetPassword(ResetPasswordDto dto)
    {
        if (!ModelState.IsValid) return View(dto);

        var result = await _authService.ResetPasswordAsync(dto);
        if (!result.IsSuccess)
        {
            foreach (var err in result.Errors) ModelState.AddModelError(string.Empty, err);
            return View(dto);
        }

        TempData["Success"] = "Password reset successful. Please login.";
        return RedirectToAction(nameof(Login));
    }

    [HttpGet, Authorize]
    public IActionResult ChangePassword() => View(new ChangePasswordDto());

    [HttpPost, ValidateAntiForgeryToken, Authorize]
    public async Task<IActionResult> ChangePassword(ChangePasswordDto dto)
    {
        if (!ModelState.IsValid) return View(dto);

        var result = await _authService.ChangePasswordAsync(_currentUser.UserId!, dto);
        if (!result.IsSuccess)
        {
            foreach (var err in result.Errors) ModelState.AddModelError(string.Empty, err);
            return View(dto);
        }

        TempData["Success"] = "Password changed successfully.";
        return RedirectToAction(nameof(Profile));
    }

    [HttpGet, Authorize]
    public async Task<IActionResult> Profile()
    {
        var user = await _authService.GetByIdAsync(_currentUser.UserId!);
        if (user is null) return RedirectToAction(nameof(Login));

        var dto = new EditProfileDto
        {
            FullName = user.FullName,
            PhoneNumber = user.PhoneNumber,
            City = user.City,
            Address = user.Address,
            Latitude = user.Latitude,
            Longitude = user.Longitude,
            ShopName = user.ShopName,
            ShopDescription = user.ShopDescription,
            WeeklyHoliday = user.WeeklyHoliday,
            OpeningTime = user.OpeningTime,
            ClosingTime = user.ClosingTime
        };
        ViewBag.CurrentImage = user.ProfileImageUrl;
        ViewBag.Email = user.Email;
        return View(dto);
    }

    [HttpPost, ValidateAntiForgeryToken, Authorize]
    public async Task<IActionResult> Profile(EditProfileDto dto)
    {
        if (!ModelState.IsValid) return View(dto);

        string? imagePath = null;
        if (dto.ProfileImage is not null && dto.ProfileImage.Length > 0)
        {
            var uploadsDir = Path.Combine(_env.WebRootPath, "uploads", "profiles");
            Directory.CreateDirectory(uploadsDir);
            var fileName = $"{Guid.NewGuid()}{Path.GetExtension(dto.ProfileImage.FileName)}";
            var filePath = Path.Combine(uploadsDir, fileName);
            await using var stream = new FileStream(filePath, FileMode.Create);
            await dto.ProfileImage.CopyToAsync(stream);
            imagePath = $"/uploads/profiles/{fileName}";
        }

        var result = await _authService.UpdateProfileAsync(_currentUser.UserId!, dto, imagePath);
        if (!result.IsSuccess)
        {
            foreach (var err in result.Errors) ModelState.AddModelError(string.Empty, err);
            return View(dto);
        }

        TempData["Success"] = "Profile updated successfully.";
        return RedirectToAction(nameof(Profile));
    }

    public IActionResult AccessDenied() => View();
}
