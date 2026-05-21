using System.ComponentModel.DataAnnotations;
using BarberHub.Web.Domain.Constants;
using BarberHub.Web.Domain.Entities;
using BarberHub.Web.Infrastructure.Persistence;
using BarberHub.Web.Shared.Filters;
using BarberHub.Web.Shared.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BarberHub.Web.Features.Services;

// =================== Default service templates ===================
// Shown to a new barber so they can quickly add common services
// instead of typing them from scratch.

public static class ServiceTemplates
{
    public static readonly List<ServiceTemplateDto> All = new()
    {
        // Hair
        new("Hair Cut",              "Hair",   150, 30,  "Classic haircut with scissor or clipper finish."),
        new("Hair Cut + Wash",        "Hair",   200, 45,  "Haircut with a relaxing shampoo wash."),
        new("Hair Styling",           "Hair",   100, 20,  "Blow-dry and style — no cut."),
        new("Hair Colour",            "Hair",   500, 60,  "Full hair colour treatment."),
        new("Hair Highlights",        "Hair",   700, 90,  "Partial or full highlights."),
        new("Keratin Treatment",      "Hair",  1200,120,  "Smooth, frizz-free hair treatment."),

        // Beard
        new("Beard Trim",             "Beard",  100, 20,  "Neat beard shaping and trimming."),
        new("Beard Trim + Shave",     "Beard",  150, 30,  "Beard trim with a clean razor shave."),
        new("Clean Shave",            "Beard",  100, 20,  "Full razor shave with hot towel."),
        new("Beard Colour",           "Beard",  200, 30,  "Beard colouring and conditioning."),

        // Combo
        new("Hair Cut + Beard Trim",  "Combo",  220, 50,  "Full hair and beard grooming combo."),
        new("Grooming Package",       "Combo",  350, 75,  "Hair, beard, and face care combined."),

        // Kids
        new("Kids Hair Cut",          "Kids",   100, 25,  "Gentle haircut for children under 12."),

        // Face & Skin
        new("Face Clean Up",          "Skin",   250, 30,  "Deep facial cleansing and scrub."),
        new("D-Tan Face Pack",        "Skin",   300, 40,  "De-tanning pack for brighter skin."),
        new("Head Massage",           "Skin",   200, 30,  "Relaxing scalp and head massage."),
    };
}

public class ServiceTemplateDto
{
    public string Name { get; set; }
    public string Category { get; set; }
    public decimal DefaultPrice { get; set; }
    public int DefaultDuration { get; set; }
    public string Description { get; set; }

    public ServiceTemplateDto(string name, string category, decimal price, int duration, string desc)
    {
        Name = name; Category = category; DefaultPrice = price;
        DefaultDuration = duration; Description = desc;
    }
}

public class ServiceUpsertDto
{
    public Guid? Id { get; set; }

    [Required, StringLength(200)]
    public string Name { get; set; } = string.Empty;

    [StringLength(500)]
    public string? Description { get; set; }

    [Required, StringLength(50)]
    public string Category { get; set; } = "Hair";

    [Required, Range(1, 99999)]
    public decimal Price { get; set; }

    [Required, Range(5, 480)]
    [Display(Name = "Duration (minutes)")]
    public int DurationMinutes { get; set; } = 30;

    public bool IsActive { get; set; } = true;
}

// Batch-add from templates: checkboxes with price overrides
public class TemplateActivateDto
{
    public List<TemplateItem> Items { get; set; } = new();
}

public class TemplateItem
{
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool Selected { get; set; }
    public decimal Price { get; set; }
    public int DurationMinutes { get; set; }
}

// =================== Controller ===================

[Authorize(Roles = AppRoles.Admin)]
[RequireApprovedBarber]
public class ServicesController : Controller
{
    private readonly ApplicationDbContext _db;
    private readonly ICurrentUserService _currentUser;

    public ServicesController(ApplicationDbContext db, ICurrentUserService currentUser)
    {
        _db = db;
        _currentUser = currentUser;
    }

    // ----------- List my services -----------
    [HttpGet]
    public async Task<IActionResult> Index()
    {
        var barberId = _currentUser.UserId!;
        var services = await _db.Services
            .Where(s => s.BarberId == barberId)
            .OrderBy(s => s.Category).ThenBy(s => s.Name)
            .ToListAsync();
        return View(services);
    }

    // ----------- Add from templates (bulk) -----------
    [HttpGet]
    public async Task<IActionResult> Templates()
    {
        var barberId = _currentUser.UserId!;
        var existing = await _db.Services
            .Where(s => s.BarberId == barberId)
            .Select(s => s.Name.ToLower())
            .ToListAsync();

        // Build the template list, marking ones already added
        var items = ServiceTemplates.All.Select(t => new TemplateItem
        {
            Name = t.Name,
            Category = t.Category,
            Description = t.Description,
            Selected = !existing.Contains(t.Name.ToLower()),  // pre-check if not yet added
            Price = t.DefaultPrice,
            DurationMinutes = t.DefaultDuration
        }).ToList();

        return View(new TemplateActivateDto { Items = items });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Templates(TemplateActivateDto dto)
    {
        var barberId = _currentUser.UserId!;
        var selected = dto.Items.Where(i => i.Selected).ToList();
        if (!selected.Any())
        {
            TempData["Error"] = "Please select at least one service.";
            return View(dto);
        }

        var existing = await _db.Services
            .Where(s => s.BarberId == barberId)
            .Select(s => s.Name.ToLower())
            .ToListAsync();

        int added = 0;
        foreach (var item in selected)
        {
            if (existing.Contains(item.Name.ToLower())) continue;
            await _db.Services.AddAsync(new Service
            {
                Name = item.Name,
                Category = item.Category,
                Description = item.Description,
                Price = item.Price,
                DurationMinutes = item.DurationMinutes,
                BarberId = barberId,
                IsActive = true
            });
            added++;
        }
        await _db.SaveChangesAsync();
        TempData["Success"] = $"{added} service{(added == 1 ? "" : "s")} added to your menu.";
        return RedirectToAction(nameof(Index));
    }

    // ----------- Create custom service -----------
    [HttpGet]
    public IActionResult Create() => View(new ServiceUpsertDto());

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(ServiceUpsertDto dto)
    {
        if (!ModelState.IsValid) return View(dto);

        var barberId = _currentUser.UserId!;
        var dupe = await _db.Services
            .AnyAsync(s => s.BarberId == barberId && s.Name.ToLower() == dto.Name.ToLower());
        if (dupe)
        {
            ModelState.AddModelError(nameof(dto.Name), "You already have a service with this name.");
            return View(dto);
        }

        await _db.Services.AddAsync(new Service
        {
            Name = dto.Name,
            Description = dto.Description,
            Category = dto.Category,
            Price = dto.Price,
            DurationMinutes = dto.DurationMinutes,
            IsActive = dto.IsActive,
            BarberId = barberId
        });
        await _db.SaveChangesAsync();
        TempData["Success"] = $"Service '{dto.Name}' created.";
        return RedirectToAction(nameof(Index));
    }

    // ----------- Edit service -----------
    [HttpGet]
    public async Task<IActionResult> Edit(Guid id)
    {
        var s = await _db.Services.FirstOrDefaultAsync(x => x.Id == id && x.BarberId == _currentUser.UserId);
        if (s is null) return NotFound();
        return View(new ServiceUpsertDto
        {
            Id = s.Id, Name = s.Name, Description = s.Description,
            Category = s.Category, Price = s.Price,
            DurationMinutes = s.DurationMinutes, IsActive = s.IsActive
        });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(ServiceUpsertDto dto)
    {
        if (!ModelState.IsValid) return View(dto);
        var s = await _db.Services.FirstOrDefaultAsync(x => x.Id == dto.Id && x.BarberId == _currentUser.UserId);
        if (s is null) return NotFound();

        s.Name = dto.Name;
        s.Description = dto.Description;
        s.Category = dto.Category;
        s.Price = dto.Price;
        s.DurationMinutes = dto.DurationMinutes;
        s.IsActive = dto.IsActive;
        s.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        TempData["Success"] = "Service updated.";
        return RedirectToAction(nameof(Index));
    }

    // ----------- Toggle active/inactive -----------
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Toggle(Guid id)
    {
        var s = await _db.Services.FirstOrDefaultAsync(x => x.Id == id && x.BarberId == _currentUser.UserId);
        if (s is not null)
        {
            s.IsActive = !s.IsActive;
            s.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            TempData["Success"] = s.IsActive ? $"'{s.Name}' is now visible to customers." : $"'{s.Name}' is now hidden.";
        }
        return RedirectToAction(nameof(Index));
    }

    // ----------- Delete -----------
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(Guid id)
    {
        var s = await _db.Services.FirstOrDefaultAsync(x => x.Id == id && x.BarberId == _currentUser.UserId);
        if (s is null) return NotFound();

        // Don't hard-delete if it has bookings
        var hasBookings = await _db.Bookings.AnyAsync(b => b.ServiceId == id);
        if (hasBookings)
        {
            // Soft-hide instead of delete so history is preserved
            s.IsActive = false;
            s.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            TempData["Success"] = $"'{s.Name}' has bookings and was hidden instead of deleted.";
        }
        else
        {
            _db.Services.Remove(s);
            await _db.SaveChangesAsync();
            TempData["Success"] = $"'{s.Name}' deleted.";
        }
        return RedirectToAction(nameof(Index));
    }
}
