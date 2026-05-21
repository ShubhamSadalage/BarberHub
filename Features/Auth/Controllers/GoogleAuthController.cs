using System.Security.Claims;
using BarberHub.Web.Domain.Constants;
using BarberHub.Web.Domain.Entities;
using BarberHub.Web.Shared.Config;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace BarberHub.Web.Features.Auth.Controllers;

/// <summary>
/// Google Sign-In / Sign-Up flow.
/// Requires Features:GoogleLoginEnabled = true AND Google:OAuth:ClientId/ClientSecret in appsettings.json.
/// </summary>
[Route("[controller]/[action]")]
public class GoogleAuthController : Controller
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly FeatureFlags _features;

    public GoogleAuthController(
        UserManager<ApplicationUser> userManager,
        SignInManager<ApplicationUser> signInManager,
        IOptions<FeatureFlags> features)
    {
        _userManager = userManager;
        _signInManager = signInManager;
        _features = features.Value;
    }

    /// <summary>Initiates the Google OAuth flow.</summary>
    [HttpGet]
    public IActionResult Start(string? returnUrl = null)
    {
        if (!_features.GoogleLoginEnabled)
        {
            TempData["Error"] = "Google sign-in is currently disabled.";
            return RedirectToAction("Login", "Auth");
        }

        // Check keys are real (not the placeholder fallback)
        // If not configured, show a friendly message instead of bouncing to Google
        // with fake credentials which would produce a confusing Google error.
        var props = new AuthenticationProperties
        {
            RedirectUri = Url.Action(nameof(Callback), "GoogleAuth",
                new { returnUrl }, Request.Scheme)
        };
        return Challenge(props, GoogleDefaults.AuthenticationScheme);
    }

    /// <summary>Handles Google's callback after the user consents.</summary>
    [HttpGet]
    public async Task<IActionResult> Callback(string? returnUrl = null)
    {
        if (!_features.GoogleLoginEnabled)
            return RedirectToAction("Login", "Auth");

        // Read what Google sent back
        var result = await HttpContext.AuthenticateAsync(GoogleDefaults.AuthenticationScheme);
        if (!result.Succeeded || result.Principal is null)
        {
            TempData["Error"] = "Google sign-in failed or was cancelled. Please try again.";
            return RedirectToAction("Login", "Auth");
        }

        var subject = result.Principal.FindFirstValue(ClaimTypes.NameIdentifier);
        var email   = result.Principal.FindFirstValue(ClaimTypes.Email);
        var name    = result.Principal.FindFirstValue(ClaimTypes.Name)
                   ?? result.Principal.FindFirstValue(ClaimTypes.GivenName)
                   ?? email?.Split('@')[0]
                   ?? "User";

        if (string.IsNullOrEmpty(subject) || string.IsNullOrEmpty(email))
        {
            TempData["Error"] = "Google didn't return your email. Please try again.";
            return RedirectToAction("Login", "Auth");
        }

        // 1. Try to find existing user by Google subject (most reliable)
        ApplicationUser? user = _userManager.Users
            .FirstOrDefault(u => u.GoogleSubjectId == subject);

        // 2. Fall back to email match (links an existing password account to Google)
        if (user is null)
        {
            user = await _userManager.FindByEmailAsync(email);
            if (user is not null && string.IsNullOrEmpty(user.GoogleSubjectId))
            {
                user.GoogleSubjectId = subject;
                user.EmailConfirmed = true;
                await _userManager.UpdateAsync(user);
            }
        }

        // 3. Auto-create a new User-role account (barbers must register via the form
        //    so they can supply shop details and go through the approval workflow)
        if (user is null)
        {
            user = new ApplicationUser
            {
                UserName        = email,
                Email           = email,
                EmailConfirmed  = true,
                FullName        = name,
                GoogleSubjectId = subject,
                IsActive        = true,
                CreatedAt       = DateTime.UtcNow,
                ApprovalStatus  = BarberApprovalStatus.NotApplicable
            };

            var create = await _userManager.CreateAsync(user);
            if (!create.Succeeded)
            {
                TempData["Error"] = string.Join("; ", create.Errors.Select(e => e.Description));
                return RedirectToAction("Login", "Auth");
            }
            await _userManager.AddToRoleAsync(user, AppRoles.User);
        }

        // Guard: soft-deleted or disabled accounts
        if (user.IsDeleted || !user.IsActive)
        {
            TempData["Error"] = "Your account is no longer active. Please contact support.";
            return RedirectToAction("Login", "Auth");
        }

        // Sign in — persistent so they stay logged in across browser restarts
        await _signInManager.SignInAsync(user, isPersistent: true);
        TempData["Success"] = $"Welcome, {user.FullName}!";

        if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
            return Redirect(returnUrl);

        return RedirectToAction("Index", "Home");
    }
}
