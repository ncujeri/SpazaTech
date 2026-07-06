using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Microsoft.EntityFrameworkCore;
using SpazaHub.Client;
using SpazaHub.Client.Data;
using SpazaHub.Client.Sync;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// The API origin is configurable (wwwroot/appsettings.json); defaults to same origin.
string? configuredApi = builder.Configuration["ApiBaseUrl"];
string apiBaseUrl = string.IsNullOrWhiteSpace(configuredApi)
    ? builder.HostEnvironment.BaseAddress
    : configuredApi;
builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(apiBaseUrl) });

builder.Services.AddDbContextFactory<ClientDbContext>(options =>
    options.UseSqlite($"Data Source={OpfsDbPersistence.DatabaseFileName}"));

builder.Services.AddSingleton<AccessTokenStore>();
builder.Services.AddSingleton<OpfsDbPersistence>();
builder.Services.AddSingleton<LocalStore>();
builder.Services.AddSingleton<SyncApiClient>();
builder.Services.AddSingleton<BackgroundSyncService>();

var host = builder.Build();

// Restore the local database from OPFS before anything opens it, then start the
// sync loop. The loop idles until the login flow (Phase 3) provides a token.
var persistence = host.Services.GetRequiredService<OpfsDbPersistence>();
await persistence.RestoreAsync();
host.Services.GetRequiredService<BackgroundSyncService>().Start();

await host.RunAsync();
