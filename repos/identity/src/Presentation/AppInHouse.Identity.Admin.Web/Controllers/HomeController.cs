using System.Diagnostics;
using AppInHouse.Identity.Admin.Web.Infrastructure;
using AppInHouse.Identity.Admin.Web.ViewModels;
using AppInHouse.Identity.Application.Admin.Dashboard;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AppInHouse.Identity.Admin.Web.Controllers;

public class HomeController : PortalController
{
    private readonly IDashboardService _dashboard;

    public HomeController(IDashboardService dashboard) => _dashboard = dashboard;

    public async Task<IActionResult> Index(CancellationToken cancellationToken)
        => View(await _dashboard.GetAsync(cancellationToken));

    [AllowAnonymous]
    public IActionResult AccessDenied() => View();

    [AllowAnonymous]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
        => View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
}
