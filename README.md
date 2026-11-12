# sbud CLI

The `sbud` command. It manages sandboxes through the sbud service, using the C# SDK, Sbud.Contracts, and starts the service (`sbudd`, installed beside it) when none is running.

Commands: plain `sbud` (open this folder's sandbox), `ls`, `create`, `attach`, `shell`, `start`, `stop`, `rm`, `modify`, `update`, `repair-ca`, `import-history`, `kits`, `auth` (including `auth add`, which signs a login in), `service status|stop`, and `api`, which sends one request to the service for debugging. Run `sbud help`, or `sbud modify -h` for the changes `modify` takes.

Build and test with `dotnet test Sbud.Cli.slnx`. The project references Sbud.Contracts by relative path, `../sbud-dotnet`, until it is on NuGet.

The sbud CLI has not been released yet. It has been tested on Linux against the service, with a stand-in for Docker Sandboxes.
