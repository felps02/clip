using ClipForge.Domain.Common;
using FluentValidation;

namespace ClipForge.Api.Common;

/// <summary>
/// Endpoint filter that validates every handler argument with a registered <see cref="IValidator{T}"/>.
/// Failures produce 400 validation_failed with an "errors" dictionary.
/// </summary>
public sealed class ValidationFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var services = context.HttpContext.RequestServices;
        Dictionary<string, string[]>? errors = null;

        foreach (var argument in context.Arguments)
        {
            if (argument is null) continue;
            var type = argument.GetType();
            if (type.IsPrimitive || type == typeof(string) || type == typeof(Guid) || type == typeof(CancellationToken)) continue;
            if (type.Namespace?.StartsWith("Microsoft.", StringComparison.Ordinal) == true || type.Namespace?.StartsWith("System.", StringComparison.Ordinal) == true) continue;

            if (services.GetService(typeof(IValidator<>).MakeGenericType(type)) is not IValidator validator) continue;

            var result = await validator.ValidateAsync(new ValidationContext<object>(argument), context.HttpContext.RequestAborted);
            if (result.IsValid) continue;

            errors ??= new Dictionary<string, string[]>();
            foreach (var group in result.Errors.GroupBy(e => ToCamel(e.PropertyName)))
            {
                errors[group.Key] = group.Select(e => e.ErrorMessage).ToArray();
            }
        }

        if (errors is not null)
        {
            return Problems.Result(400, ErrorCodes.ValidationFailed, "Dados inválidos.", errors);
        }

        return await next(context);
    }

    private static string ToCamel(string propertyName)
    {
        if (string.IsNullOrEmpty(propertyName)) return "request";
        var parts = propertyName.Split('.');
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length > 0) parts[i] = char.ToLowerInvariant(parts[i][0]) + parts[i][1..];
        }
        return string.Join('.', parts);
    }
}

public static class ValidationExtensions
{
    public static RouteGroupBuilder WithValidation(this RouteGroupBuilder group)
    {
        group.AddEndpointFilter<ValidationFilter>();
        return group;
    }

    /// <summary>Registers every IValidator&lt;T&gt; implementation found in the API assembly.</summary>
    public static IServiceCollection AddApiValidators(this IServiceCollection services)
    {
        var validatorTypes = typeof(ValidationFilter).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false })
            .SelectMany(t => t.GetInterfaces()
                .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IValidator<>))
                .Select(i => (Service: i, Implementation: t)));
        foreach (var (service, implementation) in validatorTypes)
        {
            services.AddScoped(service, implementation);
        }
        return services;
    }
}
