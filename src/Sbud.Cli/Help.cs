namespace Sbud.Cli;

/// <summary>The usage text.</summary>
public static class Help
{
	/// <summary>What <c>sbud help</c> prints.</summary>
	public const string Text = """
		usage: sbud [command]
		  sbud                      open this folder's sandbox; with none, open the desktop app
		  sbud ls                   list this folder's sandboxes
		  sbud create [options]     create a sandbox for this folder, then open it
		  sbud attach NAME          open a sandbox's agent in this terminal
		  sbud shell NAME           open bash in a sandbox
		  sbud start|stop|rm NAME   start, stop or delete a sandbox
		  sbud modify NAME [changes]
		                            change a sandbox (sbud modify -h lists the changes)
		  sbud update NAME [KIT...] reinstall a sandbox's kits at their current version
		  sbud repair-ca [--dry-run] [NAME...]
		                            give proxies back the CA their sandboxes trust
		  sbud import-history NAME FILE
		                            restore history saved by a failed rebuild
		  sbud kits ...             the kit catalogue: ls, add, rm, mv, path, default
		  sbud auth ...             logins: ls, add, save, push, rm, mv, dirs
		  sbud service status|stop  the sbud service
		  sbud api METHOD PATH [JSON]
		                            send one request to the service and print the raw answer

		create options:
		  --agent claude|codex   --model MODEL   --ram GB   --docker-gb GB   --name NAME
		  --auth LABEL|none      --kit NAME|PATH (repeatable; without it, no kits)
		  --workspace PATH[:ro]  --no-workspace  --mount PATH[:ro] (repeatable)
		  --template REF         --detach        --dry-run

		""";

	/// <summary>What <c>sbud modify -h</c> prints.</summary>
	public const string Modify = """
		usage: sbud modify NAME [changes]
		Changes the sandbox NAME in place, keeping everything installed in it and its agent history.
		RAM, folders and the name need Docker Sandboxes restarted, which stops every running sandbox,
		so sbud asks first (SBUD_ASSUME_YES=1 answers yes). --clone, --rebuild and --fresh make a new sandbox.
		  --ram GB              memory in GiB
		  --model MODEL         default model ('' for the agent's own default)
		  --auth LABEL|none     login to use
		  --history-gb GB       grow the Claude history volume (never shrinks; the sandbox must be stopped)
		  --docker-gb GB        grow the Docker volume (never shrinks; the sandbox must be stopped)
		  --kit NAME|PATH       add a kit; repeatable
		  --no-kit NAME         remove a kit; its files go to ~/.kit-backups
		  --update-kit NAME     reinstall a kit at its current version; repeatable
		  --update-kits         reinstall every kit the sandbox has
		  --workspace PATH[:ro] a different main workspace
		  --mount PATH[:ro]     share a folder, or change one already shared; repeatable
		  --unmount PATH        stop sharing a folder; repeatable
		  --name NEW            a new name
		  --standard-name       rename it to <folder>-<agent>, after its main workspace
		  --clone               make a copy with the changes under the new name, keeping NAME
		  --rebuild             remake NAME from a snapshot of itself with the changes
		  --fresh               rebuild from the managed template: newest agent and kits, installed apps lost
		  --detach              after a clone or rebuild, do not open it
		  --dry-run             print the plan and stop

		""";
}
