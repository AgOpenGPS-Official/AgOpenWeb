using AgOpenWeb.Models.Configuration;
using AgOpenWeb.Models.Pipeline;
using AgOpenWeb.Models.State;
using AgOpenWeb.Services;
using AgOpenWeb.Services.Interfaces;
using AgOpenWeb.Services.FieldAssistants.Modules;
using AgOpenWeb.ViewModels;
using Microsoft.Extensions.DependencyInjection;
namespace AgOpenWeb.RemoteWiring;
internal sealed partial class FieldAssistantsRuntime {
    partial void AddGapless(IServiceProvider sp,MainViewModel vm) {
        var fields=sp.GetRequiredService<IFieldService>();
        registry.Add(new GuidanceModule(new GuidanceAssistantHost(sp.GetRequiredService<IUiDispatcher>(),sp.GetRequiredService<ApplicationState>(),sp.GetRequiredService<ConfigurationStore>(),fields,sp.GetRequiredService<ICoverageMapService>(),sp.GetRequiredService<IPipelineIntents>(),()=>Volatile.Read(ref snapshot).FieldDirectory,vm)));
    }
}
