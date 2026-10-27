using AppInHouse.Identity.Infrastructure.Identity.Models;
using AppInHouse.Identity.Infrastructure.Persistence.Configurations;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace AppInHouse.Identity.Infrastructure.Persistence.Contexts;

/// <summary>Owns the ASP.NET Core Identity tables (AspNetUsers, AspNetRoles, ...) and the OpenIddict tables.</summary>
public class AppIdentityDbContext : IdentityDbContext<ApplicationUser, ApplicationRole, string>
{
    public AppIdentityDbContext(DbContextOptions<AppIdentityDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.ApplyConfiguration(new ApplicationUserConfiguration());
        builder.ApplyConfiguration(new ApplicationRoleConfiguration());

        // Registers the OpenIddict Applications / Authorizations / Scopes / Tokens entities.
        builder.UseOpenIddict();
    }
}
