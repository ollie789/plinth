using System.CommandLine;
using Plinth.Cli;

return await CliApp.Build().Parse(args).InvokeAsync();
