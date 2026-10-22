namespace Jarvis.Core;

/// <summary>Current Gemini models that can answer text conversations in JARVIS.</summary>
public static class GeminiModelCatalog
{
    public const string DefaultModel = "gemini-3.8-flash";

    /// <summary>
    /// Current text-generation models from Google's Gemini API catalog. Specialized image,
    /// speech, and Live API models use different request formats and are not chat models here.
    /// </summary>
    public static readonly IReadOnlyList<string> ApiKeyModels = Array.AsReadOnly(new[]
    {
        "gemini-4-argon",
        "gemini-3.8-flash",
        "gemini-3.6-flash",
        "gemini-3.5-flash-lite",
        "gemini-3.1-pro-preview",
        "gemini-3.1-flash-lite",
        "gemini-3-flash-preview"
    });

    public static bool IsApiKeyModel(string? model) =>
        !string.IsNullOrWhiteSpace(model) && ApiKeyModels.Contains(model.Trim(), StringComparer.OrdinalIgnoreCase);

    public static bool IsGemini2Family(string? model) =>
        model?.Trim().StartsWith("gemini-2", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// Moves a saved Gemini 2 selection off the old model family without changing custom
    /// models configured for other providers.
    /// </summary>
    public static bool MigrateLegacySelection(AppSettings settings)
    {
        var migrated = false;

        // Keep legacy consumer-login settings on the Antigravity CLI provider. The CLI owns the
        // browser login and quota session, so Jarvis never asks for or stores an API key.
        if (settings.Provider is ProviderKind.Antigravity or ProviderKind.GoogleAccount ||
            settings.Preset is "antigravity" or "googleaccount")
        {
            settings.Provider = ProviderKind.AntigravityCli;
            settings.Preset = "antigravity-cli";
            settings.BaseUrl = "agy://local";
            settings.Model = "gemini-3.8-flash-medium";
            migrated = true;
        }

        if (settings.Provider == ProviderKind.AntigravityCli &&
            (string.IsNullOrWhiteSpace(settings.Model) || settings.Model.Equals("auto", StringComparison.OrdinalIgnoreCase)))
        {
            settings.Model = "gemini-3.8-flash-medium";
            migrated = true;
        }

        var isGooglePreset = settings.Preset == "gemini";

        if (isGooglePreset && IsGemini2Family(settings.Model))
        {
            settings.Model = DefaultModel;
            migrated = true;
        }

        return migrated;
    }

    private static string StripLegacyEffortSuffix(string model)
    {
        foreach (var effort in new[] { "-low", "-medium", "-high", "-max" })
            if (model.EndsWith(effort, StringComparison.OrdinalIgnoreCase))
                return model[..^effort.Length];
        return model;
    }
}
