using Plinth.Core;

namespace Plinth.Tests;

public class EngineTests
{
    [Fact]
    public void Version_is_the_algorithm_version()
    {
        Assert.Equal("1.5", Engine.Version);
    }

    [Fact]
    public void Init_is_idempotent_and_reports_libvips()
    {
        Engine.Init();
        Engine.Init();
        Assert.StartsWith("8.", Engine.LibvipsVersion);
    }

    [Fact]
    public void Warm_up_runs_a_whole_normalise_and_does_not_throw()
    {
        Engine.WarmUp();
        Engine.WarmUp();
    }

    [Fact]
    public void Concurrency_is_explicit_and_a_later_default_init_leaves_it_alone()
    {
        Engine.Init(1);
        Assert.Equal(1, Engine.Concurrency);
        Engine.Init();
        Assert.Equal(1, Engine.Concurrency);
    }
}
