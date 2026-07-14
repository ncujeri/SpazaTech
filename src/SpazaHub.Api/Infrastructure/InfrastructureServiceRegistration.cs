using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SpazaHub.Api.Common.Interfaces;
using SpazaHub.Api.Sync;
using SpazaHub.Api.Auth;
using SpazaHub.Api.Identity;
using Polly;
using Polly.Extensions.Http;
using SpazaHub.Api.Messaging;
using SpazaHub.Api.Persistence;

namespace SpazaHub.Api;

public static class InfrastructureServiceRegistration
{
    /// <summary>
    /// Wires persistence, identity, tokens, and messaging. Database:Provider selects
    /// SqlServer (production default) or Sqlite (local development and CI demos).
    /// Connection strings are placeholders in appsettings; real values come from
    /// user-secrets locally and Key Vault in production.
    /// </summary>
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<JwtOptions>()
            .Bind(configuration.GetSection(JwtOptions.SectionName));

        services.AddOptions<AuthOptions>()
            .Bind(configuration.GetSection(AuthOptions.SectionName));

        services.AddSingleton(TimeProvider.System);

        services.AddScoped<TenantStampInterceptor>();
        services.AddScoped<ChangeLogInterceptor>();
        services.AddScoped<ISyncDeviceContext, SyncDeviceContext>();

        string provider = configuration["Database:Provider"] ?? "SqlServer";

        services.AddDbContext<AppDbContext>((sp, options) =>
        {
            // Order matters: TenantId is stamped before the change log snapshots payloads.
            options.AddInterceptors(
                sp.GetRequiredService<TenantStampInterceptor>(),
                sp.GetRequiredService<ChangeLogInterceptor>());

            if (string.Equals(provider, "Sqlite", StringComparison.OrdinalIgnoreCase))
            {
                options.UseSqlite(
                    configuration.GetConnectionString("Sqlite") ?? "Data Source=spazahub-dev.db");
            }
            else
            {
                options.UseSqlServer(
                    configuration.GetConnectionString("SqlServer")
                    ?? throw new InvalidOperationException(
                        "ConnectionStrings:SqlServer is not configured. Use user-secrets locally or Key Vault in production."));
            }
        });

        services.AddIdentityCore<ApplicationUser>(options =>
            {
                options.User.AllowedUserNameCharacters = "0123456789+";
            })
            .AddEntityFrameworkStores<AppDbContext>();

        services.AddScoped<JwtTokenService>();
        services.AddScoped<IAuthService, AuthService>();
        // SMS: SMSFlow in production, the logging dev provider until it is configured.
        services.AddOptions<SmsFlowOptions>().Bind(configuration.GetSection(SmsFlowOptions.SectionName));
        services.AddScoped<SmsWebhookService>();

        if (string.Equals(configuration["Messaging:Provider"], "SmsFlow", StringComparison.OrdinalIgnoreCase))
        {
            services.AddHttpClient<IMessagingProvider, SmsFlowProvider>()
                .AddPolicyHandler(HttpPolicyExtensions
                    .HandleTransientHttpError()
                    .OrResult(r => r.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                    .WaitAndRetryAsync(3, attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt))));
        }
        else
        {
            services.AddScoped<IMessagingProvider, DevLogSmsProvider>();
        }
        services.AddScoped<ISyncService, SyncService>();

        return services;
    }
}
