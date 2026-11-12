using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Sbud.Cli.Tests;

public sealed class CliTests
{
	private const string Folder = "/w/shop";

	private static object Summary(string name, string state = "running", string? proxyCa = null) => new
	{
		name,
		agent = "claude",
		state,
		ramGiB = 24,
		workspace = new { hostPath = Folder, readOnly = false },
		mounts = new { },
		login = "work",
		kits = new Dictionary<string, object> { ["Agentic"] = new { version = "abc1234", current = true }, ["net"] = new { } },
		openChats = 0,
		openConsoles = 0,
		proxyCa,
	};

	private static async Task<(int Code, TestHost Host)> RunAsync(StubService service, string[] args, params string[] answers)
	{
		var host = new TestHost(Folder, answers);
		using var client = new SbudClient(service.Endpoint);
		var code = await new Cli(client, host).RunAsync(args);
		return (code, host);
	}

	[Fact]
	public async Task Ls_prints_one_tab_separated_line_per_sandbox_and_flags_a_wrong_proxy_CA()
	{
		await using var service = await StubService.StartAsync(app =>
			app.MapGet("/v1/sandboxes", () => Results.Json(new[] { Summary("shop-claude", proxyCa: "wrong"), Summary("shop2-claude", "stopped") })));
		var (code, host) = await RunAsync(service, ["ls"]);

		Assert.Equal(0, code);
		Assert.Equal("shop-claude\tclaude\trunning\t24G\twork\tAgentic,net\nshop2-claude\tclaude\tstopped\t24G\twork\tAgentic,net\n", host.Printed);
		Assert.Contains("sbud: shop-claude: proxy CA not trusted ('sbud repair-ca shop-claude')", host.Said);
		Assert.Contains(service.Requests, r => r.Path == "/v1/sandboxes?workspace=%2Fw%2Fshop&include=volumes,proxy-ca");
	}

	[Fact]
	public async Task Create_applies_the_options_to_this_folders_defaults()
	{
		await using var service = await StubService.StartAsync(app =>
		{
			app.MapGet("/v1/sandbox-defaults/for-folder", (string folder, string? agent) => Results.Json(new
			{
				agent = agent ?? "claude", name = "shop-" + (agent ?? "claude"), model = "", ramGiB = 24, login = "work",
				kits = new[] { new { name = "Agentic", path = "/k/agentic" } },
				workspace = new { hostPath = folder, readOnly = false }, mounts = Array.Empty<object>(),
			}));
			app.MapPost("/v1/sandboxes", () => Results.Json(new { lines = new[] { "login: work   model: agent default", "sbx create ..." } }));
		});

		var (code, host) = await RunAsync(service,
			["create", "--agent", "codex", "--ram", "8", "--kit", "Agentic", "--kit", "./kits/extra", "--mount", "../shared:ro", "--no-workspace", "--dry-run"]);

		Assert.Equal(0, code);
		Assert.Equal("login: work   model: agent default\nsbx create ...\n", host.Printed);
		Assert.Contains(service.Requests, r => r.Path == "/v1/sandbox-defaults/for-folder?folder=%2Fw%2Fshop&agent=codex");
		var body = service.BodiesTo("POST", "/v1/sandboxes?dryRun=true").Single();
		Assert.Equal("codex", (string)body["agent"]!);
		Assert.Equal("shop-codex", (string)body["name"]!);
		Assert.Equal(8, (int)body["ramGiB"]!);
		Assert.Null(body["workspace"]);
		Assert.Equal("/w/shared", (string)body["mounts"]![0]!["hostPath"]!);
		Assert.True((bool)body["mounts"]![0]!["readOnly"]!);
		Assert.Equal("Agentic", (string)body["kits"]![0]!["name"]!);
		Assert.Equal("/w/shop/kits/extra", (string)body["kits"]![1]!["path"]!);
	}

	[Fact]
	public async Task Create_follows_the_task_then_opens_the_sandbox()
	{
		var polls = 0;
		await using var service = await StubService.StartAsync(app =>
		{
			app.MapGet("/v1/sandbox-defaults/for-folder", () => Results.Json(new
			{
				agent = "claude", name = "shop-claude", model = "", ramGiB = 24, login = "none",
				kits = Array.Empty<object>(), workspace = new { hostPath = Folder, readOnly = false }, mounts = Array.Empty<object>(),
			}));
			app.MapPost("/v1/sandboxes", () => Results.Json(Task("running", ["Checking the claude sandbox base."]), statusCode: 202));
			app.MapGet("/v1/tasks/t1", () => ++polls < 2
				? Results.Json(Task("running", ["Checking the claude sandbox base.", "Creating 'shop-claude'."]))
				: Results.Json(Task("done", ["Checking the claude sandbox base.", "Creating 'shop-claude'.", "Created 'shop-claude'."])));
			app.MapPost("/v1/sandboxes/shop-claude/terminal-sessions", () => Results.Json(new { id = "s1", argv = new[] { "sbx", "run", "--name", "shop-claude" } }, statusCode: 201));
			app.MapDelete("/v1/sandboxes/shop-claude/terminal-sessions/s1", () => Results.NoContent());
		});

		var (code, host) = await RunAsync(service, ["create"]);
		Assert.Equal(0, code);
		Assert.Equal(
			"sbud: Checking the claude sandbox base.\nsbud: Creating 'shop-claude'.\nsbud: Created 'shop-claude'.\n",
			host.Said);
		Assert.Equal(["sbx", "run", "--name", "shop-claude"], Assert.Single(host.Attached));
		Assert.Contains(service.Requests, r => r.Method == "DELETE" && r.Path == "/v1/sandboxes/shop-claude/terminal-sessions/s1");

		static object Task(string state, string[] lines) =>
			new { id = "t1", title = "Create shop-claude", sandbox = "shop-claude", state, lines, cancellable = false, startedAt = DateTimeOffset.UnixEpoch };
	}

	[Fact]
	public async Task Invalid_fields_are_printed_one_by_one_and_exit_2()
	{
		await using var service = await StubService.StartAsync(app =>
		{
			app.MapGet("/v1/sandbox-defaults/for-folder", () => Results.Json(new
			{
				agent = "claude", name = "shop-claude", model = "", ramGiB = 24, login = "none",
				kits = Array.Empty<object>(), workspace = (object?)null, mounts = Array.Empty<object>(),
			}));
			app.MapPost("/v1/sandboxes", () => StubService.Problem(422, "invalid", "Some fields are wrong.", new[]
			{
				new { field = "name", message = "A sandbox named 'shop-claude' already exists." },
				new { field = "ramGiB", message = "RAM must be a whole number of GiB." },
			}));
		});
		var (code, host) = await RunAsync(service, ["create", "--dry-run"]);
		Assert.Equal(2, code);
		Assert.Equal("sbud: A sandbox named 'shop-claude' already exists.\nsbud: RAM must be a whole number of GiB.\n", host.Said);
	}

	[Fact]
	public async Task Signed_out_gets_the_specs_sentence_and_exit_1()
	{
		await using var service = await StubService.StartAsync(app =>
			app.MapPost("/v1/sandboxes/x/start", () => StubService.Problem(503, "signed-out", "Docker Sandboxes is signed out.")));
		var (code, host) = await RunAsync(service, ["start", "x"]);
		Assert.Equal(1, code);
		Assert.Equal("sbud: Docker Sandboxes is signed out. Run `sbx login`, then try again.\n", host.Said);
	}

	[Fact]
	public async Task Plain_sbud_opens_the_only_sandbox_or_asks_which()
	{
		var count = 1;
		await using var service = await StubService.StartAsync(app =>
		{
			app.MapGet("/v1/sandboxes", () => Results.Json(count == 1 ? new[] { Summary("a-claude") } : new[] { Summary("a-claude"), Summary("b-claude") }));
			app.MapPost("/v1/sandboxes/{name}/terminal-sessions", (string name) => Results.Json(new { id = "s", argv = new[] { "sbx", "run", "--name", name } }, statusCode: 201));
			app.MapDelete("/v1/sandboxes/{name}/terminal-sessions/s", () => Results.NoContent());
		});

		var (code, host) = await RunAsync(service, []);
		Assert.Equal(0, code);
		Assert.Equal("a-claude", Assert.Single(host.Attached)[3]);

		count = 2;
		(code, host) = await RunAsync(service, [], "2");
		Assert.Equal("b-claude", Assert.Single(host.Attached)[3]);
		Assert.Contains("  2) b-claude  claude  running", host.Said);
	}

	[Fact]
	public async Task Attach_exits_with_sbxs_code()
	{
		await using var service = await StubService.StartAsync(app =>
		{
			app.MapPost("/v1/sandboxes/a/terminal-sessions", () => Results.Json(new { id = "s", argv = new[] { "sbx", "exec", "-it", "a", "bash", "-l" } }, statusCode: 201));
			app.MapDelete("/v1/sandboxes/a/terminal-sessions/s", () => Results.NoContent());
		});
		var host = new TestHost(Folder) { AttachedExitCode = 7 };
		using var client = new SbudClient(service.Endpoint);
		Assert.Equal(7, await new Cli(client, host).RunAsync(["shell", "a"]));
		Assert.Equal("""{"kind":"shell"}""", service.Requests.Single(r => r.Method == "POST").Body);
	}

	[Fact]
	public async Task Auth_save_asks_first_and_a_no_exits_130()
	{
		await using var service = await StubService.StartAsync(app =>
		{
			app.MapGet("/v1/host-logins/codex", () => Results.Json(new { agent = "codex", file = "/home/me/.codex/auth.json", exists = true }));
			app.MapPost("/v1/logins/work/codex/copy-from-host", () => Results.Json(new
			{
				label = new { label = "work", patterns = Array.Empty<string>(), sandboxes = Array.Empty<string>(), logins = Array.Empty<object>() },
				push = new { pushed = new[] { new { sandbox = "a", agent = "codex" } }, failed = Array.Empty<object>(), skipped = Array.Empty<object>(), dropped = Array.Empty<string>() },
			}));
		});
		var (code, host) = await RunAsync(service, ["auth", "save", "work", "codex"], "n");
		Assert.Equal(130, code);
		Assert.Contains("Store the codex login in /home/me/.codex/auth.json as 'work'?", host.Said);
		Assert.DoesNotContain(service.Requests, r => r.Path.EndsWith("copy-from-host"));

		(code, host) = await RunAsync(service, ["auth", "save", "work", "codex"], "YES");
		Assert.Equal(0, code);
		Assert.Contains("sbud: 'work' pushed to 1 sandbox(es); 0 failed; 0 skipped; 0 dropped from sbud's records.", host.Said);
	}

	[Fact]
	public async Task Auth_ls_and_kits_ls_print_their_columns()
	{
		await using var service = await StubService.StartAsync(app =>
		{
			app.MapGet("/v1/logins", () => Results.Json(new[]
			{
				new
				{
					label = "work", patterns = new[] { "/w/*" }, sandboxes = new[] { "a", "b" },
					logins = new object[] { new { agent = "claude" }, new { agent = "codex", account = "acct", expiresAt = new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero) } },
				},
			}));
			app.MapGet("/v1/kits", () => Results.Json(new[] { new { name = "Agentic", path = "/k/a", @default = true } }));
		});
		var (_, host) = await RunAsync(service, ["auth", "ls"]);
		Assert.Equal("work\tclaude\t-\t-\t2\nwork\tcodex\tacct\t2026-12-01\t2\n", host.Printed);
		(_, host) = await RunAsync(service, ["kits", "ls"]);
		Assert.Equal("Agentic\tdefault on\t/k/a\n", host.Printed);
	}

	[Fact]
	public async Task Service_status_never_starts_the_service()
	{
		var host = new TestHost(Folder);
		using var client = new SbudClient(Path.Combine(Path.GetTempPath(), "no-such-sbud.sock"));
		Assert.Equal(1, await new Cli(client, host).RunAsync(["service", "status"]));
		Assert.Equal("not running\n", host.Printed);
		Assert.Empty(host.Started);
		Assert.Equal(0, await new Cli(client, host).RunAsync(["service", "stop"]));
		Assert.Contains("sbud: the service is not running.", host.Said);
	}

	[Fact]
	public async Task Other_commands_start_the_service_when_none_answers()
	{
		var host = new TestHost(Folder);
		using var client = new SbudClient(Path.Combine(Path.GetTempPath(), "no-such-sbud.sock"));
		Assert.Equal(1, await new Cli(client, host).RunAsync(["ls"]));
		Assert.Equal(["sbudd"], host.Started);
		Assert.Contains("sbudd is not installed beside sbud", host.Said);
	}

	[Fact]
	public async Task A_started_service_that_never_answers_is_reported_with_its_log()
	{
		var host = new TestHost(Folder) { CanStart = _ => true };
		host.Variables["SBUD_HOME"] = "/home/me/.sbud";
		using var client = new SbudClient(Path.Combine(Path.GetTempPath(), "no-such-sbud.sock"));
		Assert.Equal(1, await new Cli(client, host).RunAsync(["ls"]));
		Assert.Contains("sbud: The sbud service did not start. Its log is /home/me/.sbud/service.log.", host.Said);
	}

	[Fact]
	public async Task An_unknown_command_or_option_is_a_usage_error()
	{
		await using var service = await StubService.StartAsync(_ => { });
		Assert.Equal(2, (await RunAsync(service, ["frobnicate"])).Code);
		Assert.Equal(2, (await RunAsync(service, ["create", "--ram", "0"])).Code);
		Assert.Equal(2, (await RunAsync(service, ["create", "--colour"])).Code);
	}
}
