using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Sbud.Cli.Tests;

public sealed class ChangeCommandTests
{
	private const string Folder = "/w/shop";

	private static readonly object ShopSandbox = new
	{
		name = "shop-claude", agent = "claude", state = "running", ramGiB = 24,
		workspace = new { hostPath = "/w/shop", readOnly = false },
		mounts = new Dictionary<string, object> { ["w/shared"] = new { hostPath = "/w/shared", readOnly = false } },
		login = "work",
		kits = new Dictionary<string, object> { ["A"] = new { version = "1", current = true }, ["B"] = new { version = "2", current = false } },
		openChats = 0, openConsoles = 0, standardName = "shop-claude", model = "", unmanagedKits = Array.Empty<string>(),
		historyVolume = (object?)null, dockerVolume = (object?)null, proxyCa = "ok",
	};

	private static object Plan(bool restarts, params string[] stops) => new
	{
		name = "shop-claude", mode = "in-place", fresh = false, changes = Array.Empty<object>(),
		steps = new[] { "modify:   changed in 'shop-claude' without a rebuild:", "ram:      8 GiB (was 24)" },
		restartsDaemon = restarts, stopsSandboxes = stops, growsVolumes = false, notes = new[] { "a note" },
	};

	private static object Task(string state) =>
		new { id = "t1", title = "Change shop-claude", sandbox = "shop-claude", state, lines = new[] { "Done the thing." }, cancellable = false, startedAt = DateTimeOffset.UnixEpoch };

	private static async Task<(int Code, TestHost Host)> RunAsync(StubService service, string[] args, params string[] answers)
	{
		var host = new TestHost(Folder, answers);
		using var client = new SbudClient(service.Endpoint);
		return (await new Cli(client, host).RunAsync(args), host);
	}

	[Fact]
	public async Task Modify_sends_every_option_as_one_patch_and_asks_before_stopping_sandboxes()
	{
		await using var service = await StubService.StartAsync(app =>
		{
			app.MapGet("/v1/sandboxes/shop-claude", () => Results.Json(ShopSandbox));
			app.MapPatch("/v1/sandboxes/shop-claude", (bool? dryRun) => dryRun == true
				? Results.Json(Plan(true, "shop-claude", "other"))
				: Results.Json(Task("done"), statusCode: 202));
			app.MapGet("/v1/tasks/t1", () => Results.Json(Task("done")));
		});

		var (code, host) = await RunAsync(service,
			["modify", "shop-claude", "--ram", "8", "--kit", "C", "--no-kit", "A", "--update-kits", "--workspace", "../new", "--unmount", "../shared", "--mount", "../assets:ro", "--standard-name"], "n");
		Assert.Equal(130, code);
		Assert.Contains("ram:      8 GiB (was 24)", host.Printed);
		Assert.Contains("This restarts Docker Sandboxes, which stops every running sandbox (shop-claude, other). Go ahead?", host.Said);
		var patch = JsonNode.Parse(service.Requests.Single(r => r.Method == "PATCH").Body)!.AsArray();
		Assert.Equal(
			[
				"replace /ramGiB 8", "add /kits/C {}", "remove /kits/A ", "replace /kits/B/current true",
				"replace /workspace {\"hostPath\":\"/w/new\",\"readOnly\":false}", "remove /mounts/w~1shared ",
				"add /mounts/w~1assets {\"hostPath\":\"/w/assets\",\"readOnly\":true}", "replace /name \"new-claude\"",
			],
			patch.Select(o => $"{o!["op"]} {o["path"]} {o["value"]?.ToJsonString()}".Replace("replace /ramGiB 8", "replace /ramGiB 8")));
		Assert.Contains("/v1/sandboxes/shop-claude?mode=in-place&dryRun=true", service.Requests.Single(r => r.Method == "PATCH").Path);

		(code, host) = await RunAsync(service, ["modify", "shop-claude", "--ram", "8"], "yes");
		Assert.Equal(0, code);
		Assert.Contains(service.Requests, r => r.Method == "PATCH" && r.Path == "/v1/sandboxes/shop-claude?mode=in-place&confirmStops=shop-claude%2Cother");
		Assert.Contains("sbud: Done the thing.", host.Said);
	}

	[Fact]
	public async Task Update_with_one_kit_uses_the_single_kit_route()
	{
		await using var service = await StubService.StartAsync(app =>
		{
			app.MapPut("/v1/sandboxes/shop-claude/kits/A", () => Results.Json(Task("done"), statusCode: 202));
			app.MapGet("/v1/tasks/t1", () => Results.Json(Task("done")));
		});
		var (code, host) = await RunAsync(service, ["update", "shop-claude", "A"]);
		Assert.Equal(0, code);
		Assert.Contains("sbud: 'shop-claude' is up to date.", host.Said);
	}

	[Fact]
	public async Task Auth_add_prints_the_address_asks_for_the_code_and_reports_the_login()
	{
		var polls = 0;
		await using var service = await StubService.StartAsync(app =>
		{
			app.MapPost("/v1/logins/work/claude/sign-ins", () => Results.Json(new { id = "s1", label = "work", agent = "claude", mode = "terminal", state = "running" }, statusCode: 202));
			app.MapGet("/v1/sign-ins/s1", () => ++polls switch
			{
				1 => Results.Json(new { id = "s1", label = "work", agent = "claude", mode = "terminal", state = "need-code", url = "https://claude.ai/oauth/authorize?x", prompt = "When the browser shows a code, paste it here." }),
				2 => Results.Json(new { id = "s1", label = "work", agent = "claude", mode = "terminal", state = "finishing", url = "https://claude.ai/oauth/authorize?x" }),
				_ => Results.Json(new { id = "s1", label = "work", agent = "claude", mode = "terminal", state = "done", url = "https://claude.ai/oauth/authorize?x" }),
			});
			app.MapPost("/v1/sign-ins/s1/code", () => Results.NoContent());
		});
		var (code, host) = await RunAsync(service, ["auth", "add", "work"], "the-code");
		Assert.Equal(0, code);
		Assert.Contains("https://claude.ai/oauth/authorize?x", host.Said);
		Assert.Contains("sbud: When the browser shows a code, paste it here.", host.Said);
		Assert.Equal("""{"code":"the-code"}""", service.Requests.Single(r => r.Path == "/v1/sign-ins/s1/code").Body);
		Assert.Contains("stored the claude login as 'work'", host.Said);
	}

	[Fact]
	public async Task Repair_ca_dry_run_lists_without_repairing_and_import_history_starts_the_repair()
	{
		await using var service = await StubService.StartAsync(app =>
		{
			app.MapGet("/v1/diagnostics", () => Results.Json(new object[]
			{
				new { kind = "proxy-ca", sandbox = "a-claude", title = "x", repair = new { type = "proxy-ca", sandboxes = new[] { "a-claude" }, confirmStops = Array.Empty<string>() } },
			}));
			app.MapPost("/v1/repairs", () => Results.Json(Task("done"), statusCode: 202));
			app.MapGet("/v1/tasks/t1", () => Results.Json(Task("done")));
		});
		var (code, host) = await RunAsync(service, ["repair-ca", "--dry-run"]);
		Assert.Equal(0, code);
		Assert.Contains("these sandboxes trust a CA their proxy no longer uses: a-claude.", host.Said);
		Assert.DoesNotContain(service.Requests, r => r.Path == "/v1/repairs");

		(code, _) = await RunAsync(service, ["import-history", "shop-claude", "saved.tar"]);
		Assert.Equal(0, code);
		var body = JsonNode.Parse(service.Requests.Single(r => r.Path == "/v1/repairs").Body)!;
		Assert.Equal("history-restore", (string)body["type"]!);
		Assert.Equal("/w/shop/saved.tar", (string)body["file"]!);
	}
}
