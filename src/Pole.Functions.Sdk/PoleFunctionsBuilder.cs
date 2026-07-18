using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Pole.Functions.Protocol;

namespace Pole.Functions.Sdk;

public sealed class PoleFunctionsBuilder
{
    internal PoleFunctionsBuilder(IServiceCollection services) => Services = services;

    internal IServiceCollection Services { get; }
}

public static class PoleFunctionsServiceCollectionExtensions
{
    public static PoleFunctionsBuilder AddPoleFunctions(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        AddCoreServices(services);
        services.AddOptions<PoleFunctionsOptions>()
            .Bind(configuration)
            .ValidateOnStart();
        return new PoleFunctionsBuilder(services);
    }

    public static PoleFunctionsBuilder AddPoleFunctions(
        this IServiceCollection services,
        Action<PoleFunctionsOptions> configure)
    {
        AddCoreServices(services);
        services.AddOptions<PoleFunctionsOptions>()
            .Configure(configure)
            .ValidateOnStart();
        return new PoleFunctionsBuilder(services);
    }

    public static PoleFunctionsBuilder AddHttpHandler(
        this PoleFunctionsBuilder builder,
        string name,
        PoleHttpFunctionHandler handler,
        int? maxConcurrencyPerInstance = null)
    {
        builder.Services.AddSingleton<FunctionHandlerDescriptor>(
            new DelegateHttpFunctionHandlerDescriptor(name, maxConcurrencyPerInstance, handler));
        return builder;
    }

    public static PoleFunctionsBuilder UseAgentAuthenticator<TAuthenticator>(
        this PoleFunctionsBuilder builder)
        where TAuthenticator : class, IPoleFunctionAgentAuthenticator
    {
        builder.Services.Replace(
            ServiceDescriptor.Singleton<IPoleFunctionAgentAuthenticator, TAuthenticator>());
        return builder;
    }

    public static PoleFunctionsBuilder AddHttpHandler<THandler>(
        this PoleFunctionsBuilder builder,
        string name,
        int? maxConcurrencyPerInstance = null)
        where THandler : class, IPoleHttpFunctionHandler
    {
        builder.Services.TryAddScoped<THandler>();
        builder.Services.AddSingleton<FunctionHandlerDescriptor>(
            new ServiceHttpFunctionHandlerDescriptor<THandler>(name, maxConcurrencyPerInstance));
        return builder;
    }

    public static PoleFunctionsBuilder AddTimerHandler(
        this PoleFunctionsBuilder builder,
        string name,
        PoleTimerFunctionHandler handler,
        int? maxConcurrencyPerInstance = null)
    {
        builder.Services.AddSingleton<FunctionHandlerDescriptor>(
            new DelegateTimerFunctionHandlerDescriptor(name, maxConcurrencyPerInstance, handler));
        return builder;
    }

    public static PoleFunctionsBuilder AddTimerHandler<THandler>(
        this PoleFunctionsBuilder builder,
        string name,
        int? maxConcurrencyPerInstance = null)
        where THandler : class, IPoleTimerFunctionHandler
    {
        builder.Services.TryAddScoped<THandler>();
        builder.Services.AddSingleton<FunctionHandlerDescriptor>(
            new ServiceTimerFunctionHandlerDescriptor<THandler>(name, maxConcurrencyPerInstance));
        return builder;
    }

    private static void AddCoreServices(IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<PoleFunctionsOptions>, PoleFunctionsOptionsValidator>());
        services.TryAddSingleton<FunctionAgentIdentity>();
        services.TryAddSingleton<FunctionAgentLifecycle>();
        services.TryAddSingleton<FunctionHandlerRegistry>();
        services.TryAddSingleton<FunctionAttemptStore>();
        services.TryAddSingleton<FunctionExecutionTracker>();
        services.TryAddSingleton<FunctionAttemptExecutor>();
        services.TryAddSingleton<IPoleFunctionAgentAuthenticator, PoleFunctionJwtAgentAuthenticator>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, FunctionAttemptCleanupService>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, FunctionAgentLifecycleService>());
        services.ConfigureHttpJsonOptions(options =>
            PoleFunctionProtocolJson.Configure(options.SerializerOptions));
    }
}
