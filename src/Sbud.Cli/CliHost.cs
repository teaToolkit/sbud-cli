using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Sbud.Cli;

/// <summary>Everything the CLI touches outside the service: its streams, folder, environment and the programs it starts. Tests replace it.</summary>
public class CliHost
{
	/// <summary>Results.</summary>
	public virtual TextWriter Out => Console.Out;

	/// <summary>Progress, prompts and messages.</summary>
	public virtual TextWriter Error => Console.Error;

	/// <summary>Answers to prompts.</summary>
	public virtual TextReader In => Console.In;

	/// <summary>Whether a person can answer a prompt.</summary>
	public virtual bool Interactive => !Console.IsInputRedirected;

	/// <summary>The folder the command runs in.</summary>
	public virtual string CurrentFolder => Environment.CurrentDirectory;

	/// <summary>An environment variable, or null.</summary>
	public virtual string? Variable(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;

	/// <summary>
	/// Runs <paramref name="argv"/> attached to this terminal and returns its exit code. While it runs, Ctrl+C goes to
	/// it alone, so the CLI is still there to tidy up when it exits.
	/// </summary>
	public virtual int RunAttached(IReadOnlyList<string> argv)
	{
		var start = new ProcessStartInfo(argv[0]) { UseShellExecute = false };
		foreach (var arg in argv.Skip(1))
			start.ArgumentList.Add(arg);
		using var ignore = PosixSignalRegistration.Create(PosixSignal.SIGINT, context => context.Cancel = true);
		ConsoleCancelEventHandler keep = (_, e) => e.Cancel = true;
		Console.CancelKeyPress += keep;
		try
		{
			using var process = Process.Start(start)!;
			process.WaitForExit();
			return process.ExitCode;
		}
		catch (System.ComponentModel.Win32Exception error)
		{
			Error.WriteLine($"sbud: could not run {argv[0]}: {error.Message}");
			return 1;
		}
		finally
		{
			Console.CancelKeyPress -= keep;
		}
	}

	/// <summary>
	/// Starts the program <paramref name="name"/> that is installed beside the CLI, detached, hidden and without
	/// standard streams. Returns false when it is not there.
	/// </summary>
	public virtual bool StartBeside(string name, params string[] args)
	{
		var file = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? name + ".exe" : name);
		if (!File.Exists(file))
			return false;
		var start = new ProcessStartInfo(file)
		{
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			WorkingDirectory = Path.GetTempPath(),
		};
		foreach (var arg in args)
			start.ArgumentList.Add(arg);
		var process = Process.Start(start);
		if (process is null)
			return false;
		process.StandardInput.Close();
		process.StandardOutput.Close();
		process.StandardError.Close();
		return true;
	}

	/// <summary>Runs <paramref name="action"/> when the user presses Ctrl+C, instead of ending the CLI, until the result is disposed.</summary>
	public virtual IDisposable OnInterrupt(Action action)
	{
		ConsoleCancelEventHandler handler = (_, e) =>
		{
			e.Cancel = true;
			action();
		};
		Console.CancelKeyPress += handler;
		return new Unsubscribe(() => Console.CancelKeyPress -= handler);
	}

	private sealed class Unsubscribe(Action undo) : IDisposable
	{
		public void Dispose() => undo();
	}

	/// <summary>Waits before asking the service again.</summary>
	public virtual Task PauseAsync(TimeSpan time, CancellationToken cancel) => Task.Delay(time, cancel);
}
