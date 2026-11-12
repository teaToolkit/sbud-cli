using System.Text;
using System.Text.Json.Nodes;

namespace Sbud.Cli;

/// <summary>
/// The options of <c>sbud modify</c>, turned into one JSON Patch so the
/// service plans every change together: one confirmation, one restart, steps in the right order.
/// </summary>
public sealed class ModifyOptions
{
	/// <summary>The sandbox to change.</summary>
	public string Name { get; private set; } = "";

	/// <summary>Clone, rebuild or in place.</summary>
	public ChangeMode Mode { get; private set; } = ChangeMode.InPlace;

	/// <summary>Whether the new sandbox starts from the managed template instead of a snapshot.</summary>
	public bool Fresh { get; private set; }

	/// <summary>Whether to show the plan and change nothing.</summary>
	public bool DryRun { get; private set; }

	/// <summary>Whether not to open the result of a clone or rebuild.</summary>
	public bool Detach { get; private set; }

	/// <summary>Whether to update every kit the sandbox has.</summary>
	public bool UpdateAllKits { get; private set; }

	/// <summary>Whether to rename the sandbox to its standard name.</summary>
	public bool StandardName { get; private set; }

	private readonly List<(string Path, JsonNode? Value)> _replace = [];
	private readonly List<string> _kits = [];
	private readonly List<string> _noKits = [];
	private readonly List<string> _updateKits = [];
	private Folder? _workspace;
	private readonly List<Folder> _mounts = [];
	private readonly List<string> _unmounts = [];
	private string? _name;
	private string _folder = "";

	/// <summary>Reads <paramref name="args"/>. Relative paths are resolved against <paramref name="folder"/>.</summary>
	public static ModifyOptions Parse(IReadOnlyList<string> args, string folder)
	{
		var o = new ModifyOptions { _folder = folder };
		bool clone = false, rebuild = false;
		for (var i = 0; i < args.Count; i++)
		{
			string Value() => i + 1 < args.Count ? args[++i] : throw new UsageException($"{args[i]} needs a value.");
			switch (args[i])
			{
				case "--ram":
					o._replace.Add(("/ramGiB", Size(Value(), "--ram")));
					break;
				case "--model":
					o._replace.Add(("/model", Value()));
					break;
				case "--auth":
					o._replace.Add(("/login", Value()));
					break;
				case "--history-gb":
					o._replace.Add(("/historyGiB", Size(Value(), "--history-gb")));
					break;
				case "--docker-gb":
					o._replace.Add(("/dockerGiB", Size(Value(), "--docker-gb")));
					break;
				case "--kit":
					o._kits.Add(Value());
					break;
				case "--no-kit":
					o._noKits.Add(Value());
					break;
				case "--update-kit":
					o._updateKits.Add(Value());
					break;
				case "--update-kits":
					o.UpdateAllKits = true;
					break;
				case "--workspace":
					o._workspace = FolderOption(Value(), folder);
					break;
				case "--mount":
					o._mounts.Add(FolderOption(Value(), folder));
					break;
				case "--unmount":
					o._unmounts.Add(Path.GetFullPath(StripReadOnly(Value()), folder));
					break;
				case "--name":
					o._name = Value();
					break;
				case "--standard-name":
					o.StandardName = true;
					break;
				case "--clone":
					clone = true;
					break;
				case "--rebuild":
					rebuild = true;
					break;
				case "--fresh":
					o.Fresh = true;
					break;
				case "--detach":
					o.Detach = true;
					break;
				case "--dry-run":
					o.DryRun = true;
					break;
				default:
					if (args[i].StartsWith('-'))
						throw new UsageException($"sbud modify: unknown option '{args[i]}'.");
					if (o.Name.Length > 0)
						throw new UsageException($"sbud modify: unexpected '{args[i]}'.");
					o.Name = args[i];
					break;
			}
		}
		if (o.Name.Length == 0)
			throw new UsageException("usage: sbud modify NAME [changes]");
		o.Mode = clone ? ChangeMode.Clone : rebuild || o.Fresh ? ChangeMode.Rebuild : ChangeMode.InPlace;
		return o;
	}

	/// <summary>
	/// The patch, for <paramref name="sandbox"/> as it is now. The workspace is applied before
	/// <c>--unmount</c> and <c>--mount</c>, and <c>--name</c> wins over <c>--standard-name</c>.
	/// </summary>
	public List<PatchOperation> Operations(Sandbox sandbox)
	{
		var ops = _replace.Select(r => new PatchOperation("replace", r.Path, r.Value)).ToList();
		foreach (var kit in _kits)
		{
			var looksLikePath = kit.Contains('/') || kit.Contains('\\') || kit.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
			if (looksLikePath)
			{
				var path = Path.GetFullPath(kit, _folder);
				var kitName = new string(Path.GetFileName(path.TrimEnd('/', '\\')).Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-').ToArray());
				ops.Add(new PatchOperation("add", "/kits/" + Escape(kitName), new JsonObject { ["path"] = path }));
			}
			else
				ops.Add(new PatchOperation("add", "/kits/" + Escape(kit), new JsonObject()));
		}
		ops.AddRange(_noKits.Select(k => new PatchOperation("remove", "/kits/" + Escape(k))));
		var updates = UpdateAllKits ? sandbox.Kits.Keys.Where(k => !_noKits.Contains(k)) : _updateKits;
		ops.AddRange(updates.Select(k => new PatchOperation("replace", $"/kits/{Escape(k)}/current", true)));

		var workspace = sandbox.Workspace?.HostPath;
		if (_workspace is { } w)
		{
			ops.Add(new PatchOperation("replace", "/workspace", Node(w)));
			workspace = w.HostPath;
		}
		foreach (var path in _unmounts)
			ops.Add(new PatchOperation("remove", MountId.PatchPath(MountId.For(path))));
		foreach (var mount in _mounts)
			ops.Add(new PatchOperation("add", MountId.PatchPath(MountId.For(mount.HostPath)), Node(mount)));

		var name = _name ?? (StandardName ? Standard(workspace ?? throw new UsageException("--standard-name needs a main workspace to name the sandbox after."), sandbox.Agent) : null);
		if (name is not null && name != sandbox.Name)
			ops.Add(new PatchOperation("replace", "/name", name));
		return ops;
	}

	/// <summary>The standard name: the folder's name, lower-cased, keeping a-z, 0-9, '.' and '-', then '-' and the agent.</summary>
	public static string Standard(string folder, Agent? agent)
	{
		var trimmed = folder.Replace('\\', '/').TrimEnd('/');
		var last = trimmed[(trimmed.LastIndexOf('/') + 1)..].ToLowerInvariant();
		var name = new StringBuilder();
		foreach (var c in last)
			if (c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-')
				name.Append(c);
		return $"{name}-{(agent == Agent.Codex ? "codex" : "claude")}";
	}

	private static string Escape(string key) => key.Replace("~", "~0").Replace("/", "~1");

	private static JsonObject Node(Folder f) => new() { ["hostPath"] = f.HostPath, ["readOnly"] = f.ReadOnly };

	private static string StripReadOnly(string value) => value.Length > 3 && value.EndsWith(":ro", StringComparison.Ordinal) ? value[..^3] : value;

	private static Folder FolderOption(string value, string folder) =>
		new(Path.GetFullPath(StripReadOnly(value), folder), value.Length > 3 && value.EndsWith(":ro", StringComparison.Ordinal));

	private static int Size(string value, string option) =>
		int.TryParse(value, System.Globalization.NumberStyles.None, null, out var size) && size > 0
			? size
			: throw new UsageException($"{option} must be a whole number of GiB.");
}
