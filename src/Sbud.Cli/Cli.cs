using System.Text.Json.Nodes;

namespace Sbud.Cli;

/// <summary>
/// The <c>sbud</c> command. It is a client of the sbud service and does no work itself.
/// Results go to standard output; progress, prompts and messages go to standard error, each prefixed <c>sbud: </c>.
/// Exit codes: 0 success, 1 failure, 2 a usage or validation error, 130 declined or cancelled.
/// </summary>
public sealed partial class Cli(SbudClient client, CliHost host)
{
	/// <summary>Success.</summary>
	public const int Ok = 0;
	/// <summary>The work failed.</summary>
	public const int Failed = 1;
	/// <summary>A usage or validation error.</summary>
	public const int Usage = 2;
	/// <summary>The user declined or cancelled.</summary>
	public const int Declined = 130;

	/// <summary>How many times, 100 ms apart, the CLI asks a service it started whether it answers.</summary>
	public const int StartChecks = 100;

	private TextWriter Out => host.Out;

	/// <summary>Runs the command in <paramref name="args"/> and returns its exit code.</summary>
	public async Task<int> RunAsync(string[] args)
	{
		var command = args.Length == 0 ? "" : args[0];
		var rest = args.Skip(1).ToList();
		try
		{
			if (command is "help" or "-h" or "--help")
			{
				Out.Write(Help.Text);
				return Ok;
			}
			if (command == "service")
				return await ServiceAsync(rest);
			await EnsureServiceAsync();
			return command switch
			{
				"" => await OpenHereAsync(),
				"ls" => await ListAsync(),
				"create" => await CreateAsync(rest),
				"attach" or "open" => await TerminalAsync(rest, TerminalKind.Agent),
				"shell" => await TerminalAsync(rest, TerminalKind.Shell),
				"start" => await SimpleAsync(rest, client.StartSandboxAsync, "started"),
				"stop" => await SimpleAsync(rest, client.StopSandboxAsync, "stopped"),
				"rm" => await SimpleAsync(rest, client.DeleteSandboxAsync, "deleted"),
				"kits" => await KitsAsync(rest),
				"auth" => await AuthAsync(rest),
				"api" => await ApiAsync(rest),
				"modify" => await ModifyAsync(rest),
				"update" => await UpdateAsync(rest),
				"repair-ca" => await RepairCaAsync(rest),
				"import-history" => await ImportHistoryAsync(rest),
				_ => UsageError($"unknown command '{command}'."),
			};
		}
		catch (SbudException error)
		{
			return Report(error);
		}
		catch (UsageException error)
		{
			return UsageError(error.Message);
		}
	}

	/// <summary>Prints a refused or failed request and returns the exit code it gets.</summary>
	private int Report(SbudException error)
	{
		if (error.Kind == "signed-out")
		{
			Say("Docker Sandboxes is signed out. Run `sbx login`, then try again.");
			return Failed;
		}
		if (error.Errors.Count > 0)
			foreach (var field in error.Errors)
				Say(field.Message);
		else
			Say(error.Message);
		return error.Kind is "bad-request" or "invalid" or "refused" ? Usage : Failed;
	}

	/// <summary>
	/// Makes sure the service answers. When none does, starts <c>sbudd</c> from the CLI's own folder and waits for it.
	/// </summary>
	private async Task EnsureServiceAsync()
	{
		try
		{
			await client.GetServiceAsync();
			return;
		}
		catch (SbudException error) when (error.Kind == "unavailable")
		{
			if (!host.StartBeside("sbudd"))
				throw new SbudException("unavailable", "The sbud service is not running, and sbudd is not installed beside sbud.");
		}
		for (var check = 0; check < StartChecks; check++)
		{
			try
			{
				await client.GetServiceAsync();
				return;
			}
			catch (SbudException error) when (error.Kind == "unavailable")
			{
				await host.PauseAsync(TimeSpan.FromMilliseconds(100), CancellationToken.None);
			}
		}
		var home = host.Variable("SBUD_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".sbud");
		throw new SbudException("unavailable", $"The sbud service did not start. Its log is {Path.Combine(home, "service.log")}.");
	}

	/// <summary><c>sbud service status</c> and <c>sbud service stop</c>, which never start the service.</summary>
	private async Task<int> ServiceAsync(List<string> args)
	{
		var sub = args.FirstOrDefault() ?? "status";
		ServiceInfo info;
		try
		{
			info = await client.GetServiceAsync();
		}
		catch (SbudException error) when (error.Kind == "unavailable")
		{
			if (sub == "stop")
			{
				Say("the service is not running.");
				return Ok;
			}
			Out.WriteLine("not running");
			return Failed;
		}
		switch (sub)
		{
			case "status":
				Out.WriteLine($"running (pid {info.Pid}) on {info.Endpoint}");
				return Ok;
			case "stop":
				await client.StopServiceAsync();
				Say("the service is stopping.");
				return Ok;
			default:
				return UsageError($"unknown service command '{sub}'.");
		}
	}

	/// <summary>
	/// Plain <c>sbud</c>: opens this folder's sandbox. With several, offers a choice; with none, starts the desktop app on
	/// its New sandbox page.
	/// </summary>
	private async Task<int> OpenHereAsync()
	{
		var here = await client.GetSandboxesAsync(host.CurrentFolder);
		if (here.Count == 1)
			return await OpenAsync(here[0].Name, TerminalKind.Agent);
		if (here.Count == 0)
		{
			if (host.StartBeside("sbud-desktop", "--new-sandbox", host.CurrentFolder))
				return Ok;
			Say("this folder has no sandbox, and the desktop app is not installed. Run 'sbud create' to make one.");
			return Failed;
		}
		if (!host.Interactive)
		{
			Say($"this folder has {here.Count} sandboxes; open one with 'sbud attach NAME'.");
			return Usage;
		}
		for (var i = 0; i < here.Count; i++)
			host.Error.WriteLine($"  {i + 1}) {here[i].Name}  {Agent(here[i].Agent)}  {here[i].State.ToString().ToLowerInvariant()}");
		host.Error.Write("sbud: open which? ");
		var answer = host.In.ReadLine();
		if (!int.TryParse(answer, out var choice) || choice < 1 || choice > here.Count)
		{
			Say("nothing opened.");
			return Declined;
		}
		return await OpenAsync(here[choice - 1].Name, TerminalKind.Agent);
	}

	/// <summary>
	/// <c>sbud ls</c>: one tab-separated line per sandbox of this folder: name, agent, state, RAM, login and kits. A
	/// sandbox whose proxy CA is wrong is flagged.
	/// </summary>
	private async Task<int> ListAsync()
	{
		var list = await client.GetSandboxesAsync(host.CurrentFolder, volumes: true, proxyCa: true);
		if (list.Count == 0)
		{
			Say("no sandboxes for this folder.");
			return Ok;
		}
		foreach (var s in list)
		{
			var kits = s.Kits.Count == 0 ? "-" : string.Join(',', s.Kits.Keys);
			Out.WriteLine(string.Join('\t', s.Name, Agent(s.Agent), s.State.ToString().ToLowerInvariant(), s.RamGiB is { } r ? $"{r}G" : "-", s.Login ?? "none", kits));
			if (s.ProxyCa == ProxyCaState.Wrong)
				Say($"{s.Name}: proxy CA not trusted ('sbud repair-ca {s.Name}')");
		}
		return Ok;
	}

	/// <summary>
	/// <c>sbud create [options]</c>: starts from what a new sandbox in this folder would get, applies the options, then
	/// creates the sandbox, printing the task's lines as they arrive. Then opens it, unless <c>--detach</c> is given.
	/// </summary>
	private async Task<int> CreateAsync(List<string> args)
	{
		var options = CreateOptions.Parse(args, host.CurrentFolder);
		var defaults = await client.GetDefaultsForFolderAsync(host.CurrentFolder, options.Agent);
		var request = options.Apply(defaults);
		if (options.DryRun)
		{
			foreach (var line in await client.PlanSandboxAsync(request))
				Out.WriteLine(line);
			return Ok;
		}
		var task = await client.CreateSandboxAsync(request);
		var done = await FollowAsync(task);
		if (done.State != TaskState.Done)
			return Failed;
		return options.Detach ? Ok : await OpenAsync(request.Name, TerminalKind.Agent);
	}

	/// <summary>Prints a task's lines as they arrive, and its error if it fails. Returns the finished task.</summary>
	private async Task<TaskInfo> FollowAsync(TaskInfo task)
	{
		var shown = 0;
		while (true)
		{
			for (; shown < task.Lines.Count; shown++)
				Say(task.Lines[shown]);
			if (task.State != TaskState.Running)
				break;
			await host.PauseAsync(TimeSpan.FromMilliseconds(300), CancellationToken.None);
			task = await client.GetTaskAsync(task.Id);
		}
		if (task.State == TaskState.Failed)
		{
			Say(task.Error ?? "the task failed.");
			foreach (var command in task.Recovery ?? [])
				host.Error.WriteLine("  " + command);
		}
		else if (task.State == TaskState.Cancelled)
			Say("cancelled.");
		return task;
	}

	/// <summary><c>sbud attach NAME</c> and <c>sbud shell NAME</c>.</summary>
	private async Task<int> TerminalAsync(List<string> args, TerminalKind kind)
	{
		if (args.Count != 1)
			throw new UsageException(kind == TerminalKind.Agent ? "usage: sbud attach NAME" : "usage: sbud shell NAME");
		return await OpenAsync(args[0], kind);
	}

	/// <summary>
	/// Has the service get the sandbox ready, runs sbx in this terminal, then tells the service the session ended.
	/// Exits with sbx's code.
	/// </summary>
	private async Task<int> OpenAsync(string name, TerminalKind kind)
	{
		var session = await client.OpenTerminalSessionAsync(name, kind);
		var code = host.RunAttached(session.Argv);
		try
		{
			await client.CloseTerminalSessionAsync(name, session.Id);
		}
		catch (SbudException error)
		{
			Say($"could not tell the service the session ended: {error.Message}");
		}
		return code;
	}

	private async Task<int> SimpleAsync(List<string> args, Func<string, CancellationToken, Task> action, string done)
	{
		if (args.Count != 1)
			throw new UsageException("name one sandbox.");
		await action(args[0], CancellationToken.None);
		Say($"{done} '{args[0]}'.");
		return Ok;
	}

	/// <summary><c>sbud kits ls|add|rm|mv|path|default</c>: the kit catalogue.</summary>
	private async Task<int> KitsAsync(List<string> args)
	{
		switch (args)
		{
			case [] or ["ls"]:
				foreach (var kit in await client.GetKitsAsync())
					Out.WriteLine($"{kit.Name}\t{(kit.Default ? "default on" : "default off")}\t{kit.Path}");
				return Ok;
			case ["add", var name, var path, ..] when args.Count <= 4:
				var on = args.Count == 4 ? OnOff(args[3]) : false;
				var added = await client.AddKitAsync(new Kit(name, Path.GetFullPath(path, host.CurrentFolder), on));
				Say($"added '{added.Name}' ({added.Path}).");
				return Ok;
			case ["rm", var name]:
				await client.RemoveKitAsync(name);
				Say($"removed '{name}' from the catalogue; its files and the sandboxes that have it are untouched.");
				return Ok;
			case ["mv", var from, var to]:
				await client.PatchKitAsync(from, new JsonObject { ["name"] = to });
				Say($"renamed '{from}' to '{to}'.");
				return Ok;
			case ["path", var name, var path]:
				var moved = await client.PatchKitAsync(name, new JsonObject { ["path"] = Path.GetFullPath(path, host.CurrentFolder) });
				Say($"'{name}' is now {moved.Path}.");
				return Ok;
			case ["default", var name, var value]:
				await client.PatchKitAsync(name, new JsonObject { ["default"] = OnOff(value) });
				Say($"new sandboxes {(OnOff(value) ? "get" : "do not get")} '{name}'.");
				return Ok;
			default:
				throw new UsageException("usage: sbud kits [ls | add NAME PATH [on|off] | rm NAME | mv OLD NEW | path NAME PATH | default NAME on|off]");
		}
	}

	/// <summary><c>sbud auth ls|save|push|rm|mv|dirs</c>: logins and their labels.</summary>
	private async Task<int> AuthAsync(List<string> args)
	{
		switch (args)
		{
			case [] or ["ls"]:
				var labels = await client.GetLoginsAsync();
				if (labels.Count == 0)
					Say("no logins are stored.");
				foreach (var label in labels)
					foreach (var login in label.Logins)
						Out.WriteLine(string.Join('\t', label.Name, Agent(login.Agent), login.Account ?? "-",
							login.ExpiresAt?.ToString("yyyy-MM-dd") ?? "-", label.Sandboxes.Count.ToString()));
				return Ok;
			case ["save", var label, ..] when args.Count <= 3:
				return await SaveAsync(label, args.Count == 3 ? ParseAgent(args[2]) : Sbud.Agent.Claude);
			case ["push", var label]:
				return PrintPush(label, await client.PushLoginAsync(label));
			case ["rm", var label, var agent]:
				await client.DeleteLoginAsync(label, ParseAgent(agent));
				Say($"deleted the {agent} login for '{label}'.");
				return Ok;
			case ["mv", var from, var to]:
				await client.PatchLoginAsync(from, new JsonObject { ["label"] = to });
				Say($"renamed '{from}' to '{to}'.");
				return Ok;
			case ["dirs", var label]:
				Out.WriteLine(string.Join(' ', (await client.GetLoginAsync(label)).Patterns));
				return Ok;
			case ["dirs", var label, "--clear"]:
				await client.PatchLoginAsync(label, new JsonObject { ["patterns"] = new JsonArray() });
				return Ok;
			case ["dirs", var label, ..]:
				await client.PatchLoginAsync(label, new JsonObject { ["patterns"] = new JsonArray([.. args.Skip(2).Select(p => (JsonNode)p)]) });
				return Ok;
			case ["add", var label, ..] when args.Count <= 3:
				return await SignInAsync(label, args.Count == 3 ? ParseAgent(args[2]) : Sbud.Agent.Claude);
			default:
				throw new UsageException("usage: sbud auth [ls | save LABEL [claude|codex] | push LABEL | rm LABEL claude|codex | mv OLD NEW | dirs LABEL [PATTERN...|--clear]]");
		}
	}

	/// <summary>
	/// <c>sbud auth save</c>: asks before storing the host's own login, because the file belongs to whichever account
	/// this computer is signed in to.
	/// </summary>
	private async Task<int> SaveAsync(string label, Agent agent)
	{
		var hostLogin = await client.GetHostLoginAsync(agent);
		if (!Confirm($"Store the {Agent(agent)} login in {hostLogin.File} as '{label}'? It belongs to whichever account this computer is signed in to."))
			return Declined;
		var saved = await client.CopyLoginFromHostAsync(label, agent);
		Say($"stored the {Agent(agent)} login as '{label}'.");
		return PrintPush(label, saved.Push);
	}

	private int PrintPush(string label, PushReport report)
	{
		foreach (var failure in report.Failed)
			Say($"failed on {failure.Sandbox}: {failure.Reason}");
		foreach (var skip in report.Skipped)
			Say($"skipped {skip.Sandbox}: {skip.Reason}");
		Say($"'{label}' pushed to {report.Pushed.Count} sandbox(es); {report.Failed.Count} failed; {report.Skipped.Count} skipped; {report.Dropped.Count} dropped from sbud's records.");
		return report.Failed.Count == 0 ? Ok : Failed;
	}

	/// <summary><c>sbud api METHOD PATH [JSON]</c>: sends one request and prints the raw status and body, for debugging.</summary>
	private async Task<int> ApiAsync(List<string> args)
	{
		if (args.Count is < 2 or > 3)
			throw new UsageException("usage: sbud api METHOD PATH [JSON]");
		var (status, body) = await client.SendRawJsonAsync(new HttpMethod(args[0].ToUpperInvariant()), args[1], args.Count == 3 ? args[2] : null);
		Out.WriteLine(status);
		if (body.Length > 0)
			Out.WriteLine(body);
		return status < 400 ? Ok : Failed;
	}

	/// <summary>A yes/no question: <c>y</c> or <c>yes</c> in any case is yes; <c>SBUD_ASSUME_YES=1</c> answers yes without asking.</summary>
	private bool Confirm(string question)
	{
		if (host.Variable("SBUD_ASSUME_YES") == "1")
			return true;
		host.Error.Write($"sbud: {question} [y/N] ");
		var answer = host.In.ReadLine()?.Trim().ToLowerInvariant();
		return answer is "y" or "yes";
	}

	private int UsageError(string message)
	{
		Say(message);
		host.Error.Write(Help.Text);
		return Usage;
	}

	private void Say(string message) => host.Error.WriteLine("sbud: " + message);

	private static string Agent(Agent? agent) => agent switch
	{
		Sbud.Agent.Claude => "claude",
		Sbud.Agent.Codex => "codex",
		_ => "-",
	};

	private static Agent ParseAgent(string text) => text switch
	{
		"claude" => Sbud.Agent.Claude,
		"codex" => Sbud.Agent.Codex,
		_ => throw new UsageException($"unknown agent '{text}' (expected claude or codex)."),
	};

	private static bool OnOff(string text) => text switch
	{
		"on" => true,
		"off" => false,
		_ => throw new UsageException($"expected on or off, not '{text}'."),
	};
}

/// <summary>A command line the CLI cannot use. The message says what is wrong.</summary>
public sealed class UsageException(string message) : Exception(message);
