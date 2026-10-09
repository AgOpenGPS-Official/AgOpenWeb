using AgOpenWeb.Models.Pipeline;
using AgOpenWeb.Models.State;
using AgOpenWeb.Services;
using AgOpenWeb.Services.Interfaces;
using AgOpenWeb.Services.FieldAssistants.Modules;
using AgOpenWeb.ViewModels;
using Microsoft.Extensions.DependencyInjection;
namespace AgOpenWeb.RemoteWiring;
internal sealed partial class FieldAssistantsRuntime {
    partial void AddSavedRows(IServiceProvider sp,MainViewModel vm) {
        registry.Add(new SavedRowsModule(new SavedRowsHost(sp.GetRequiredService<IUiDispatcher>(),vm,sp.GetRequiredService<ApplicationState>(),sp.GetRequiredService<IFieldService>(),sp.GetRequiredService<IPipelineIntents>()),()=>Volatile.Read(ref snapshot)));
    }
}
