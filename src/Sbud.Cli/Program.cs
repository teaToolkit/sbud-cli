using Sbud;
using Sbud.Cli;

using var client = new SbudClient();
return await new Cli(client, new CliHost()).RunAsync(args);
