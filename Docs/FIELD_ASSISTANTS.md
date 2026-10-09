# Field assistant extension boundary

Shared/AgOpenWeb.Services/FieldAssistants owns analysis, recording and RPC validation.
Shared/AgOpenWeb.RemoteWiring/FieldAssistantsRuntime captures immutable pose snapshots
on the host owner loop and schedules recording on a separate worker. Its feature
partial classes register modules; the browser discovers them through system/modules.
No feature adds a platform UI or an ASP.NET dependency.

The existing authority-bearing WebSocket carries assistant.rpc requests and correlated
JSON replies. Write operations recheck fresh control authority at dispatch, while
GPS-cycle changes use IPipelineIntents. Each connection has at most four outstanding
requests and disconnect cancels its work. Static assets stay embedded and filename-only.
The runtime cancels and drains requests when the host stops.

Each feature is a separate module, native host bridge and browser ES module. This small
common boundary is shared by the missed-strip and individual-row contributions.
