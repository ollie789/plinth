using System.CommandLine;
using Microsoft.Extensions.Hosting;
using Plinth.Api;
using Plinth.Core;
using Plinth.Pipeline;
using Plinth.Pipeline.Fetch;

namespace Plinth.Cli;

/// <summary>The command tree. Built by a function so tests can inject a fetcher and capture stdout.</summary>
public static class CliApp
{
    public static RootCommand Build(Func<PipelineOptions, ISourceFetcher>? fetcherFactory = null, TextWriter? stdout = null)
    {
        var outWriter = stdout ?? Console.Out;
        var fetchers = fetcherFactory ?? (o => new HttpSourceFetcher(o.Fetch));
        var root = new RootCommand("Plinth puts every product on the same plinth.");

        root.Subcommands.Add(RunCommand.Build(fetchers, outWriter));

        var pathArg = new Argument<string>("file-or-url") { Description = "An image file or an https URL on the allowlist" };
        var recipeOpt = new Option<string?>("--recipe") { Description = "Recipe name from PLINTH_RECIPES, or a recipe JSON file" };

        var key = new Command("key", "Print the output key for a source") { pathArg, recipeOpt };
        key.SetAction(async (pr, ct) =>
        {
            try
            {
                var options = PipelineOptions.FromEnvironment(Environment.GetEnvironmentVariable);
                var recipe = RunCommand.ResolveRecipe(pr.GetValue(recipeOpt), options.Recipes);
                var target = pr.GetValue(pathArg)!;
                var id = target.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                    ? SourceId.FromUrl(target)
                    : SourceId.FromBytes(await File.ReadAllBytesAsync(target, ct));
                outWriter.WriteLine(OutputKey.Compute(id, recipe));
                return 0;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Console.Error.WriteLine($"plinth key: {e.Message}");
                return 2;
            }
        });
        root.Subcommands.Add(key);

        var inspect = new Command("inspect", "Normalise a source and print its result record") { pathArg, recipeOpt };
        inspect.SetAction(async (pr, ct) =>
        {
            try
            {
                var options = PipelineOptions.FromEnvironment(Environment.GetEnvironmentVariable);
                Engine.Init(options.Concurrency);
                var pipeline = new PlinthPipeline(fetchers(options), new Pipeline.Stores.NullStore(), options.Recipes);
                var recipeName = pr.GetValue(recipeOpt);
                var target = pr.GetValue(pathArg)!;
                var result = target.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                    ? await pipeline.ProcessUrlAsync(target, recipeName, ct)
                    : await pipeline.ProcessBytesAsync(await File.ReadAllBytesAsync(target, ct), recipeName, null, ct);
                outWriter.WriteLine(result.Record.ToJson());
                return 0;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Console.Error.WriteLine($"plinth inspect: {e.Message}");
                return 2;
            }
        });
        root.Subcommands.Add(inspect);

        var version = new Command("version", "Print engine and libvips versions");
        version.SetAction(_ => { outWriter.WriteLine($"plinth engine {Engine.Version} libvips {Engine.LibvipsVersion}"); return 0; });
        root.Subcommands.Add(version);

        var api = new Command("api", "Run the HTTP API (reads PLINTH_* from the environment)");
        // WebApplication has its own RunAsync(string? url), which shadows the IHost extension
        // method of the same name; go through IHost explicitly to pass the CancellationToken.
        api.SetAction(async (_, ct) => { await ((IHost)ApiHost.Build([])).RunAsync(ct); return 0; });
        root.Subcommands.Add(api);

        return root;
    }
}
