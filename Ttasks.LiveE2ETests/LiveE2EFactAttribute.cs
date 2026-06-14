namespace Ttasks.LiveE2ETests;

public sealed class LiveE2EFactAttribute : FactAttribute
{
    public LiveE2EFactAttribute()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("TTASKS_LIVE_E2E"), "1", StringComparison.Ordinal))
            Skip = "Set TTASKS_LIVE_E2E=1 to run live E2E tests.";
    }
}
