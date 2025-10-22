using DatumIngest.Compute.Grpc;
using DatumIngest.Shell;
using Grpc.Core;
using Grpc.Net.Client;
using Spectre.Console;

using GrpcClient = global::DatumIngest.Compute.Grpc.DatumCompute.DatumComputeClient;

string? serverUrl = null;
string? token = null;
string role = "admin";
string? datasetId = null;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--server":
            if (i + 1 >= args.Length) return FailUsage("--server requires a URL argument");
            serverUrl = args[++i];
            break;

        case "--token":
            if (i + 1 >= args.Length) return FailUsage("--token requires a bearer value");
            token = args[++i];
            break;

        case "--role":
            if (i + 1 >= args.Length) return FailUsage("--role requires a value");
            role = args[++i];
            break;

        case "--dataset":
            if (i + 1 >= args.Length) return FailUsage("--dataset requires a dataset id");
            datasetId = args[++i];
            break;

        case "--help":
        case "-h":
            PrintUsage();
            return 0;

        default:
            return FailUsage($"Unknown argument: {args[i]}");
    }
}

if (string.IsNullOrWhiteSpace(serverUrl))
{
    serverUrl = Environment.GetEnvironmentVariable("DATUM_SERVER") ?? "http://localhost:5000";
}

token ??= Environment.GetEnvironmentVariable("DATUM_TOKEN");

GrpcChannelOptions channelOptions = new();
if (!string.IsNullOrEmpty(token))
{
    CallCredentials credentials = CallCredentials.FromInterceptor((_, metadata) =>
    {
        metadata.Add("authorization", $"Bearer {token}");
        return Task.CompletedTask;
    });
    channelOptions.Credentials = ChannelCredentials.Create(ChannelCredentials.SecureSsl, credentials);
}

using GrpcChannel channel = GrpcChannel.ForAddress(serverUrl, channelOptions);
GrpcClient client = new(channel);

CreateSessionRequest sessionRequest = new() { Role = role };
if (datasetId is not null)
{
    sessionRequest.DatasetId = datasetId;
}

CreateSessionResponse sessionResp;
try
{
    sessionResp = await client.CreateSessionAsync(sessionRequest);
}
catch (RpcException ex)
{
    AnsiConsole.MarkupLine($"[red]Failed to create session on {serverUrl}: {Markup.Escape(ex.Status.Detail)}[/]");
    return 1;
}

CreateQueryContextResponse contextResp = await client.CreateQueryContextAsync(new CreateQueryContextRequest
{
    SessionId = sessionResp.SessionId,
    Label = "shell",
});

AnsiConsole.MarkupLine($"[grey]Connected to {serverUrl}  session={sessionResp.SessionId[..Math.Min(8, sessionResp.SessionId.Length)]}…[/]");

InteractiveShell shell = new(client, sessionResp.SessionId, contextResp.ContextId);
return await shell.RunAsync(CancellationToken.None);

static int FailUsage(string message)
{
    Console.Error.WriteLine($"Error: {message}");
    PrintUsage();
    return 1;
}

static void PrintUsage()
{
    Console.Error.WriteLine("Usage: datum-shell [options]");
    Console.Error.WriteLine();
    Console.Error.WriteLine("Options:");
    Console.Error.WriteLine("  --server <url>       gRPC server URL (default: $DATUM_SERVER or http://localhost:5000)");
    Console.Error.WriteLine("  --token <bearer>     Bearer token (default: $DATUM_TOKEN)");
    Console.Error.WriteLine("  --role <role>        Session role (default: admin)");
    Console.Error.WriteLine("  --dataset <id>       Dataset identifier");
    Console.Error.WriteLine("  --help, -h           Show this help");
}
