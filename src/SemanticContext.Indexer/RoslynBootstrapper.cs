using Microsoft.Build.Locator;

namespace SemanticContext.Indexer;

internal static class RoslynBootstrapper
{
    private static readonly object Gate = new();
    private static bool _registered;

    public static void EnsureRegistered()
    {
        if (_registered)
        {
            return;
        }

        lock (Gate)
        {
            if (_registered)
            {
                return;
            }

            if (!MSBuildLocator.IsRegistered)
            {
                MSBuildLocator.RegisterDefaults();
            }

            _registered = true;
        }
    }
}

