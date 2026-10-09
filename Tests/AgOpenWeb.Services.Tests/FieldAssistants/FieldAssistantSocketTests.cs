using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AgOpenWeb.RemoteServer;
using NUnit.Framework;

namespace AgOpenWeb.Services.Tests.FieldAssistants;
[TestFixture]
public sealed class FieldAssistantSocketTests
{
    [Test] public async Task FragmentedRpcKeepsJsonIntactAndAuthorityIsFreshAtDispatch()
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
        using var clientTcp=new TcpClient();
        var accepting=listener.AcceptTcpClientAsync(timeout.Token);
        await clientTcp.ConnectAsync(IPAddress.Loopback,((IPEndPoint)listener.LocalEndpoint).Port,timeout.Token);
        using var serverTcp=await accepting;
        using var client=WebSocket.CreateFromStream(clientTcp.GetStream(),false,null,Timeout.InfiniteTimeSpan);
        using var server=WebSocket.CreateFromStream(serverTcp.GetStream(),true,null,Timeout.InfiniteTimeSpan);
        var authority=new ControlAuthority();var hub=new WebSocketHub(authority);
        var started=new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool before=false;string payload="";
        hub.ModuleRequestHandler=async(id,hasControl,json,ct)=>{
            payload=json;before=hasControl();started.SetResult(id);await resume.Task.WaitAsync(ct);
            return JsonSerializer.Serialize(new{id=JsonDocument.Parse(json).RootElement.GetProperty("id").GetString(),ok=hasControl()});
        };
        var serving=hub.HandleAsync(server,timeout.Token);
        try{
            const string json="{\"id\":\"proposal\",\"module\":\"guidance\",\"operation\":\"context\",\"args\":{}}";
            var data=Encoding.UTF8.GetBytes("assistant.rpc|"+json);
            await client.SendAsync(data.AsMemory(0,18),WebSocketMessageType.Text,false,timeout.Token);
            await client.SendAsync(data.AsMemory(18),WebSocketMessageType.Text,true,timeout.Token);
            var id=await started.Task.WaitAsync(timeout.Token);authority.Release(id);resume.SetResult();
            var buffer=new byte[4096];string reply="";
            while(true){var message=await client.ReceiveAsync(buffer.AsMemory(),timeout.Token);if(message.MessageType==WebSocketMessageType.Text){reply+=Encoding.UTF8.GetString(buffer,0,message.Count);if(message.EndOfMessage)break;}}
            using var response=JsonDocument.Parse(reply);
            Assert.Multiple(()=>{
                Assert.That(payload,Is.EqualTo(json),"Do not hard-code the previous RPC prefix length");
                Assert.That(before,Is.True);Assert.That(response.RootElement.GetProperty("ok").GetBoolean(),Is.False);
                Assert.That(response.RootElement.GetProperty("id").GetString(),Is.EqualTo("proposal"));
            });
        }finally{client.Abort();server.Abort();timeout.Cancel();await serving;}
    }
}
