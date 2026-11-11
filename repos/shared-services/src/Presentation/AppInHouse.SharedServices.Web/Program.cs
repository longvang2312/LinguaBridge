using System.Text.Encodings.Web;
using System.Text.Unicode;
using AppInHouse.SharedServices.ClientSdk;
using Microsoft.AspNetCore.DataProtection;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSharedServicesClient(options =>
    builder.Configuration.GetSection(SharedServicesClientOptions.SectionName).Bind(options));

// Razor Pages validate the anti-forgery token of every POST by default. The cookie is for this site only and never sent along
// with a request that comes from another one.
builder.Services.AddRazorPages();

// The default HTML encoder writes every character outside Basic Latin as an entity, which turns Vietnamese text and the secret
// mask into "&#x1EA5;" and "&#x2022;". Allowing all of Unicode keeps the page source readable; the characters that matter to
// HTML (< > & " ') are still escaped.
builder.Services.AddWebEncoders(options => options.TextEncoderSettings = new TextEncoderSettings(UnicodeRanges.All));
builder.Services.AddAntiforgery(options =>
{
    options.Cookie.Name = "AppInHouse.SharedServices.Antiforgery";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
});

// The anti-forgery keys live in the Data Protection ring; keeping it in a folder (a volume in a container) means a restart does
// not invalidate the forms that are open.
var keysPath = builder.Configuration["DataProtection:KeysPath"];
var dataProtection = builder.Services.AddDataProtection()
    .SetApplicationName(builder.Configuration["DataProtection:ApplicationName"] ?? "AppInHouse.SharedServices.Web");
if (!string.IsNullOrWhiteSpace(keysPath))
{
    dataProtection.PersistKeysToFileSystem(Directory.CreateDirectory(Path.GetFullPath(keysPath, builder.Environment.ContentRootPath)));

    // Keys in a folder are plain XML unless something encrypts them: DPAPI on Windows; elsewhere protect the folder itself.
    if (OperatingSystem.IsWindows()) dataProtection.ProtectKeysWithDpapi();
}

var app = builder.Build();

if (!app.Environment.IsDevelopment())
    app.UseExceptionHandler("/Error");

app.UseStaticFiles();
app.UseRouting();
app.MapRazorPages();

app.Run();
