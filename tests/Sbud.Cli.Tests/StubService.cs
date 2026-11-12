using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Sbud.Cli.Tests;

/// <summary>
/// A stand-in for the sbud service on a Unix socket. Each test maps the answers it needs; every request is recorded
/// with its body.
/// </summary>
public sealed class StubService : IAsyncDisposable
{
	private readonly WebApplication _app;
	private readonly string _dir;

	public ConcurrentQueue<(string Method, string Path, string Body)> Requests { get; } = new();

	public string Endpoint { get; }

	private StubService(WebApplication app, string dir, string endpoint)
	{
		_app = app;
		_dir = dir;
		Endpoint = endpoint;
	}

	public static async Task<StubService> StartAsync(Action<WebApplication> map)
	{
		var dir = Directory.CreateTempSubdirectory("sbud-cli-").FullName;
		var endpoint = Path.Combine(dir, "sbud.sock");
		var builder = WebApplication.CreateSlimBuilder();
		builder.Logging.ClearProviders();
		builder.WebHost.ConfigureKestrel(k => k.ListenUnixSocket(endpoint));
		var app = builder.Build();
		StubService? self = null;
		app.Use(async (context, next) =>
		{
			context.Request.EnableBuffering();
			var body = await new StreamReader(context.Request.Body).ReadToEndAsync();
			context.Request.Body.Position = 0;
			self!.Requests.Enqueue((context.Request.Method, context.Request.Path + context.Request.QueryString, body));
			await next(context);
		});
		app.MapGet("/v1/service", () => Results.Json(new { apiVersion = 1, version = "0.1.0", pid = 42, endpoint, startedAt = DateTimeOffset.UnixEpoch }));
		map(app);
		self = new StubService(app, dir, endpoint);
		await app.StartAsync();
		return self;
	}

	/// <summary>An RFC 9457 problem as the service sends it.</summary>
	public static IResult Problem(int status, string kind, string title, object? errors = null) =>
		Results.Json(new { status, kind, title, errors }, statusCode: status, contentType: "application/problem+json");

	/// <summary>Bodies of the requests to <paramref name="path"/>, parsed.</summary>
	public List<JsonNode> BodiesTo(string method, string path) =>
		Requests.Where(r => r.Method == method && r.Path == path).Select(r => JsonNode.Parse(r.Body)!).ToList();

	public async ValueTask DisposeAsync()
	{
		await _app.StopAsync();
		await _app.DisposeAsync();
		Directory.Delete(_dir, recursive: true);
	}

	public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}

/// <summary>A terminal the test controls: what the CLI printed, what the user types, and the programs it ran.</summary>
public sealed class TestHost(string folder, params string[] answers) : CliHost
{
	private readonly StringWriter _out = new();
	private readonly StringWriter _error = new();
	private readonly StringReader _in = new(string.Join('\n', answers) + "\n");

	public Dictionary<string, string> Variables { get; } = [];
	public List<IReadOnlyList<string>> Attached { get; } = [];
	public List<string> Started { get; } = [];
	public int AttachedExitCode { get; set; }
	public Func<string, bool> CanStart { get; set; } = _ => false;

	public override TextWriter Out => _out;
	public override TextWriter Error => _error;
	public override TextReader In => _in;
	public override bool Interactive => true;
	public override string CurrentFolder => folder;
	public override string? Variable(string name) => Variables.GetValueOrDefault(name);

	public override int RunAttached(IReadOnlyList<string> argv)
	{
		Attached.Add(argv);
		return AttachedExitCode;
	}

	public override bool StartBeside(string name, params string[] args)
	{
		Started.Add(string.Join(' ', [name, .. args]));
		return CanStart(name);
	}

	public override IDisposable OnInterrupt(Action action) => new Nothing();

	private sealed class Nothing : IDisposable
	{
		public void Dispose()
		{
		}
	}

	public override Task PauseAsync(TimeSpan time, CancellationToken cancel) => Task.Delay(10, cancel);

	public string Printed => _out.ToString();
	public string Said => _error.ToString();
}
