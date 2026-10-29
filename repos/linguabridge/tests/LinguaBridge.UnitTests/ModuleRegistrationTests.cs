using LinguaBridge.Application;
using LinguaBridge.Application.Messaging;
using LinguaBridge.Application.Modules;
using LinguaBridge.Modules.AiTutor;
using LinguaBridge.Modules.Billing;
using LinguaBridge.Modules.Content;
using LinguaBridge.Modules.Learning;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LinguaBridge.UnitTests;

public class ModuleRegistrationTests
{
    private static readonly IConfiguration EmptyConfiguration = new ConfigurationBuilder().Build();

    [Fact]
    public void All_modules_register_and_container_validates()
    {
        var services = new ServiceCollection()
            .AddContentModule(EmptyConfiguration)
            .AddLearningModule(EmptyConfiguration)
            .AddAiTutorModule(EmptyConfiguration)
            .AddBillingModule(EmptyConfiguration);

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        using var scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IEventBus>());
        var modules = provider.GetServices<IModuleDescriptor>().Select(m => m.Name).Order().ToArray();
        Assert.Equal(["AiTutor", "Billing", "Content", "Learning"], modules);
    }

    [Fact]
    public void Modules_own_disjoint_schemas()
    {
        var services = new ServiceCollection()
            .AddContentModule(EmptyConfiguration)
            .AddLearningModule(EmptyConfiguration)
            .AddAiTutorModule(EmptyConfiguration)
            .AddBillingModule(EmptyConfiguration);

        using var provider = services.BuildServiceProvider();
        var schemas = provider.GetServices<IModuleDescriptor>().SelectMany(m => m.Schemas).ToArray();

        Assert.Equal(schemas.Length, schemas.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains("srs", provider.GetServices<IModuleDescriptor>().Single(m => m.Name == LearningModule.Name).Schemas);
    }

    [Fact]
    public void Adding_a_module_twice_registers_it_once()
    {
        var services = new ServiceCollection()
            .AddBillingModule(EmptyConfiguration)
            .AddBillingModule(EmptyConfiguration);

        Assert.Single(services, d => d.ServiceType == typeof(IModuleDescriptor));
        Assert.Single(services, d => d.ServiceType == typeof(IEventBus));
    }

    [Fact]
    public void Module_registration_requires_configuration()
    {
        Assert.Throws<ArgumentNullException>(() => new ServiceCollection().AddContentModule(null!));
    }

    [Fact]
    public async Task Event_bus_delivers_to_every_registered_handler()
    {
        var received = new List<string>();
        var services = new ServiceCollection().AddApplicationCore();
        services.AddSingleton<IIntegrationEventHandler<SampleEvent>>(new RecordingHandler("learning", received));
        services.AddSingleton<IIntegrationEventHandler<SampleEvent>>(new RecordingHandler("billing", received));

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IEventBus>().PublishAsync(new SampleEvent(Guid.NewGuid(), DateTimeOffset.UtcNow));

        Assert.Equal(["learning", "billing"], received);
    }

    [Fact]
    public async Task Event_bus_with_no_handlers_is_a_no_op()
    {
        using var provider = new ServiceCollection().AddApplicationCore().BuildServiceProvider();
        using var scope = provider.CreateScope();

        await scope.ServiceProvider.GetRequiredService<IEventBus>().PublishAsync(new SampleEvent(Guid.NewGuid(), DateTimeOffset.UtcNow));
    }

    private sealed record SampleEvent(Guid EventId, DateTimeOffset OccurredAt) : IIntegrationEvent;

    private sealed class RecordingHandler(string name, List<string> received) : IIntegrationEventHandler<SampleEvent>
    {
        public Task HandleAsync(SampleEvent integrationEvent, CancellationToken cancellationToken = default)
        {
            received.Add(name);
            return Task.CompletedTask;
        }
    }
}
