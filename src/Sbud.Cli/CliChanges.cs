namespace Sbud.Cli;

/// <summary>The commands that change a sandbox, repair problems and sign logins in.</summary>
public sealed partial class Cli
{
	/// <summary>
	/// <c>sbud modify NAME [changes]</c>: prints the plan; with <c>--dry-run</c> stops there. When the plan stops running
	/// sandboxes, asks first, and a no exits 130. Then follows the change, and opens the result of a clone or rebuild
	/// unless <c>--detach</c> is given.
	/// </summary>
	private async Task<int> ModifyAsync(List<string> args)
	{
		if (args is ["-h"] or ["--help"])
		{
			Out.Write(Help.Modify);
			return Ok;
		}
		var options = ModifyOptions.Parse(args, host.CurrentFolder);
		var sandbox = await client.GetSandboxAsync(options.Name);
		var ops = options.Operations(sandbox);
		var plan = await client.PlanChangeAsync(options.Name, ops, options.Mode, options.Fresh);
		foreach (var line in plan.Steps)
			Out.WriteLine(line);
		foreach (var note in plan.Notes)
			Say(note);
		if (options.DryRun)
			return Ok;
		if (plan.StopsSandboxes.Count > 0 && !Confirm(plan.RestartsDaemon
				? $"This restarts Docker Sandboxes, which stops every running sandbox ({string.Join(", ", plan.StopsSandboxes)}). Go ahead?"
				: $"This stops {string.Join(", ", plan.StopsSandboxes)}. Go ahead?"))
		{
			Say("nothing has changed.");
			return Declined;
		}
		var done = await FollowAsync(await client.ChangeSandboxAsync(options.Name, ops, options.Mode, options.Fresh, plan.StopsSandboxes));
		if (done.State != TaskState.Done)
			return Failed;
		return options.Detach || options.Mode == ChangeMode.InPlace && !options.Fresh ? Ok : await OpenAsync(plan.Name, TerminalKind.Agent);
	}

	/// <summary>
	/// <c>sbud update NAME [KIT...]</c>: reinstalls the named kits at their current version, or every kit the sandbox
	/// has. One kit goes through the single-kit route; several go as one change.
	/// </summary>
	private async Task<int> UpdateAsync(List<string> args)
	{
		if (args.Count == 0)
			throw new UsageException("usage: sbud update NAME [KIT...]");
		var name = args[0];
		var kits = args.Skip(1).ToList();
		TaskInfo task;
		if (kits.Count == 1)
			task = await client.PutSandboxKitAsync(name, kits[0]);
		else
		{
			if (kits.Count == 0)
				kits = [.. (await client.GetSandboxAsync(name)).Kits.Keys];
			if (kits.Count == 0)
				throw new UsageException($"no kits are recorded for '{name}'; name them: sbud update {name} KIT...");
			task = await client.ChangeSandboxAsync(name, [.. kits.Select(k => new PatchOperation("replace", $"/kits/{k.Replace("~", "~0").Replace("/", "~1")}/current", true))]);
		}
		var done = await FollowAsync(task);
		if (done.State != TaskState.Done)
			return Failed;
		Say($"'{name}' is up to date.");
		return Ok;
	}

	/// <summary>
	/// <c>sbud repair-ca [--dry-run] [NAME...]</c>: gives each sandbox's proxy back the certificate the sandbox trusts,
	/// for every sandbox that needs it unless some are named. It restarts Docker Sandboxes, so it asks first when any
	/// sandbox is running.
	/// </summary>
	private async Task<int> RepairCaAsync(List<string> args)
	{
		var dry = args.Remove("--dry-run");
		if (args.Any(a => a.StartsWith('-')))
			throw new UsageException("usage: sbud repair-ca [--dry-run] [NAME...]");
		var wrong = (await client.GetDiagnosticsAsync()).Where(p => p.Kind == ProblemKind.ProxyCa && p.Sandbox is not null)
			.Where(p => args.Count == 0 || args.Contains(p.Sandbox!)).ToList();
		if (wrong.Count == 0)
		{
			Say("every sandbox checked has a proxy that signs with the CA it trusts.");
			return Ok;
		}
		Say($"these sandboxes trust a CA their proxy no longer uses: {string.Join(", ", wrong.Select(p => p.Sandbox))}.");
		if (dry)
			return Ok;
		var running = (await client.GetSandboxesAsync()).Where(s => s.State == SandboxState.Running).Select(s => s.Name).ToList();
		if (running.Count > 0 && !Confirm($"This restarts Docker Sandboxes, which stops every running sandbox ({string.Join(", ", running)}). Go ahead?"))
		{
			Say("nothing has changed.");
			return Declined;
		}
		var done = await FollowAsync(await client.RepairAsync(new ProxyCaRepair([.. wrong.Select(p => p.Sandbox!)], running)));
		return done.State == TaskState.Done ? Ok : Failed;
	}

	/// <summary><c>sbud import-history NAME FILE</c>: restores agent history saved by a failed rebuild.</summary>
	private async Task<int> ImportHistoryAsync(List<string> args)
	{
		if (args.Count != 2)
			throw new UsageException("usage: sbud import-history NAME FILE");
		var done = await FollowAsync(await client.RepairAsync(new HistoryRestoreRepair(args[0], Path.GetFullPath(args[1], host.CurrentFolder))));
		return done.State == TaskState.Done ? Ok : Failed;
	}

	/// <summary>
	/// <c>sbud auth add LABEL [claude|codex]</c>: prints the address to open, asks for the code when the browser shows
	/// one, then reports the login stored and pushed. Ctrl+C cancels the sign-in in the service and exits 130.
	/// </summary>
	private async Task<int> SignInAsync(string label, Agent agent)
	{
		var signIn = await client.StartSignInAsync(label, agent);
		var cancelled = false;
		using var cancel = host.OnInterrupt(() =>
		{
			cancelled = true;
			client.CancelSignInAsync(signIn.Id).GetAwaiter().GetResult();
		});
		var shownUrl = false;
		string? shownRetry = null;
		while (true)
		{
			if (!shownUrl && signIn.Url is { } url)
			{
				Say("open this address to sign in:");
				host.Error.WriteLine("  " + url);
				shownUrl = true;
			}
			if (signIn.State == SignInState.NeedCode)
			{
				if (signIn.Retry is { } retry && retry != shownRetry)
				{
					Say(retry);
					shownRetry = retry;
				}
				host.Error.Write($"sbud: {signIn.Prompt} ");
				var code = host.In.ReadLine();
				if (code is null)
				{
					await client.CancelSignInAsync(signIn.Id);
					Say("Sign-in cancelled; the existing login is unchanged.");
					return Declined;
				}
				await client.SendSignInCodeAsync(signIn.Id, code);
			}
			if (signIn.State is SignInState.Done or SignInState.Failed or SignInState.Cancelled)
				break;
			await host.PauseAsync(TimeSpan.FromMilliseconds(300), CancellationToken.None);
			signIn = await client.GetSignInAsync(signIn.Id);
		}
		switch (signIn.State)
		{
			case SignInState.Done:
				Say($"stored the {Agent(agent)} login as '{label}' and copied it into the sandboxes assigned that label.");
				return Ok;
			case SignInState.Cancelled:
				Say(signIn.Message ?? "Sign-in cancelled; the existing login is unchanged.");
				return Declined;
			default:
				Say(signIn.Message ?? "the sign-in failed.");
				return cancelled ? Declined : Failed;
		}
	}
}
