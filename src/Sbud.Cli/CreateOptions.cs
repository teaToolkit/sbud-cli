namespace Sbud.Cli;

/// <summary>
/// The options of <c>sbud create</c>. Each one overrides what a new
/// sandbox in this folder would get.
/// </summary>
public sealed class CreateOptions
{
	/// <summary>The agent, when given.</summary>
	public Agent? Agent { get; private set; }

	/// <summary>Whether to show the plan and change nothing.</summary>
	public bool DryRun { get; private set; }

	/// <summary>Whether to create without opening the sandbox.</summary>
	public bool Detach { get; private set; }

	private string? _model;
	private int? _ram;
	private int? _dockerGiB;
	private string? _name;
	private string? _login;
	private readonly List<string> _kits = [];
	private Folder? _workspace;
	private bool _noWorkspace;
	private readonly List<Folder> _mounts = [];
	private string? _template;
	private string _folder = "";

	/// <summary>Reads the options in <paramref name="args"/>. Relative paths are resolved against <paramref name="folder"/>.</summary>
	public static CreateOptions Parse(IReadOnlyList<string> args, string folder)
	{
		var options = new CreateOptions { _folder = folder };
		for (var i = 0; i < args.Count; i++)
		{
			string Value() => i + 1 < args.Count ? args[++i] : throw new UsageException($"{args[i]} needs a value.");
			switch (args[i])
			{
				case "--agent" or "--provider":
					options.Agent = Value() switch
					{
						"claude" => Sbud.Agent.Claude,
						"codex" => Sbud.Agent.Codex,
						var other => throw new UsageException($"unknown agent '{other}' (expected claude or codex)."),
					};
					break;
				case "--model":
					options._model = Value();
					break;
				case "--ram":
					options._ram = Size(args[i], Value(), "--ram must be a whole number of GiB.");
					break;
				case "--docker-gb":
					options._dockerGiB = Size(args[i], Value(), "--docker-gb must be a whole number of GiB.");
					break;
				case "--name":
					options._name = Value();
					break;
				case "--auth":
					options._login = Value();
					break;
				case "--kit":
					options._kits.Add(Value());
					break;
				case "--workspace":
					options._workspace = FolderOption(Value(), folder);
					options._noWorkspace = false;
					break;
				case "--no-workspace":
					options._noWorkspace = true;
					break;
				case "--mount":
					options._mounts.Add(FolderOption(Value(), folder));
					break;
				case "--template":
					options._template = Value();
					break;
				case "--detach":
					options.Detach = true;
					break;
				case "--dry-run":
					options.DryRun = true;
					break;
				default:
					throw new UsageException($"sbud create: unknown option '{args[i]}'.");
			}
		}
		return options;
	}

	/// <summary>
	/// <paramref name="defaults"/> with the options applied. A sandbox gets the kits named with
	/// <c>--kit</c> and no others. A kit is a catalogue name, or a path whose last part becomes its name.
	/// </summary>
	public NewSandbox Apply(NewSandbox defaults) => defaults with
	{
		Model = _model ?? defaults.Model,
		RamGiB = _ram ?? defaults.RamGiB,
		DockerGiB = _dockerGiB ?? defaults.DockerGiB,
		Name = _name ?? defaults.Name,
		Login = _login ?? defaults.Login,
		Kits = [.. _kits.Select(k => LooksLikePath(k) ? new KitRef("", Path.GetFullPath(k, _folder)) : new KitRef(k, ""))],
		Workspace = _noWorkspace ? null : _workspace ?? defaults.Workspace,
		Mounts = [.. _mounts],
		Template = _template,
	};

	private static bool LooksLikePath(string kit) =>
		kit.Contains('/') || kit.Contains('\\') || kit.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || kit is "." or "..";

	/// <summary><c>PATH[:ro]</c>, with a relative path made absolute.</summary>
	private static Folder FolderOption(string value, string folder)
	{
		var readOnly = value.Length > 3 && value.EndsWith(":ro", StringComparison.Ordinal);
		var path = readOnly ? value[..^3] : value;
		return new Folder(Path.GetFullPath(path, folder), readOnly);
	}

	private static int Size(string option, string value, string message) =>
		int.TryParse(value, System.Globalization.NumberStyles.None, null, out var size) && size > 0 ? size : throw new UsageException(message);
}
