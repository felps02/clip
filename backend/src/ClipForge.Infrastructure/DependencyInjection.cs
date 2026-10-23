using ClipForge.Domain.Common;
using ClipForge.Infrastructure.Email;
using ClipForge.Infrastructure.Payments;
using ClipForge.Infrastructure.Persistence;
using ClipForge.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ClipForge.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IClock, SystemClock>();

        var connectionString = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException("ConnectionStrings:Default is not configured.");
        services.AddDbContext<AppDbContext>(options => AppDbContextFactory.Configure(options, connectionString));

        services.Configure<StorageOptions>(configuration.GetSection(StorageOptions.SectionName));
        services.Configure<ApiUrlOptions>(configuration.GetSection(ApiUrlOptions.SectionName));
        services.Configure<EmailOptions>(configuration.GetSection(EmailOptions.SectionName));
        services.Configure<PaymentsOptions>(configuration.GetSection(PaymentsOptions.SectionName));

        services.AddSingleton<ISignedUrlService, HmacSignedUrlService>();

        var storage = configuration.GetSection(StorageOptions.SectionName).Get<StorageOptions>() ?? new StorageOptions();
        if (storage.IsS3)
        {
            services.AddSingleton<IObjectStorage, S3ObjectStorage>();
        }
        else
        {
            services.AddSingleton<LocalFileObjectStorage>();
            services.AddSingleton<IObjectStorage>(sp => sp.GetRequiredService<LocalFileObjectStorage>());
        }

        var email = configuration.GetSection(EmailOptions.SectionName).Get<EmailOptions>() ?? new EmailOptions();
        if (string.Equals(email.Provider, "Smtp", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IEmailSender, SmtpEmailSender>();
        }
        else
        {
            services.AddSingleton<LogEmailSender>();
            services.AddSingleton<IEmailSender>(sp => sp.GetRequiredService<LogEmailSender>());
        }

        var payments = configuration.GetSection(PaymentsOptions.SectionName).Get<PaymentsOptions>() ?? new PaymentsOptions();
        if (payments.IsStripe)
        {
            services.AddSingleton<IPaymentProvider, StripePaymentProvider>();
        }
        else
        {
            services.AddSingleton<IPaymentProvider, NonePaymentProvider>();
        }

        return services;
    }
}
