using BarberHub.Web.Features.Barbers.Dtos;
using BarberHub.Web.Features.Barbers.Services;
using Microsoft.AspNetCore.Mvc;

namespace BarberHub.Web.Features.Barbers.Controllers;

public class BarbersController : Controller
{
    private readonly IBarberService _service;

    public BarbersController(IBarberService service)
    {
        _service = service;
    }

    [HttpGet]
    public async Task<IActionResult> Index(BarberSearchDto? search)
    {
        search ??= new BarberSearchDto();
        var barbers = await _service.SearchAsync(search);
        ViewBag.Search = search;
        return View(barbers);
    }

    [HttpGet]
    public async Task<IActionResult> Details(string id)
    {
        var barber = await _service.GetDetailsAsync(id);
        if (barber is null) return NotFound();
        return View(barber);
    }

    [HttpGet]
    public async Task<IActionResult> AvailableSlots(string barberId, DateOnly date, Guid serviceId)
    {
        var slots = await _service.GetAvailableSlotsAsync(barberId, date, serviceId);
        return Json(slots);
    }
}
