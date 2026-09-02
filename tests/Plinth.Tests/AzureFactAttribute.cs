namespace Plinth.Tests;

/// <summary>Runs only when PLINTH_TEST_AZURE_CONNECTION names a real storage account (Azurite works too).</summary>
public sealed class AzureFactAttribute : FactAttribute
{
    public AzureFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PLINTH_TEST_AZURE_CONNECTION")))
            Skip = "PLINTH_TEST_AZURE_CONNECTION not set";
    }
}
