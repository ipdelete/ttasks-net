namespace Ttasks.ChatApp.Services;

public sealed class ChatAppOptions
{
    public string Model { get; set; } = "gpt-5.5";
    public string StorePath { get; set; } = "data\\ttasks-chat.db";
    public int MaxTasks { get; set; } = 8;
    public int MaxWorkers { get; set; } = 3;
    public int DefaultTimeoutSeconds { get; set; } = 30;
    public int MaxGraphRepairAttempts { get; set; } = 2;
    public int MaxContinuationBatches { get; set; } = 0;
    public int LlmTimeoutSeconds { get; set; } = 180;
    public bool AdminWriteLocalOnly { get; set; } = true;
    public string? AdminApiToken { get; set; }
    public List<AllowedToolConfig> AllowedTools { get; set; } = [];
    public List<TaskLibrarySeed> LibrarySeed { get; set; } = [];
}

public sealed class AllowedToolConfig
{
    public string Prefix { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? HelpCommand { get; set; }
    public List<string> Traits { get; set; } = [];
}

public sealed class TaskLibrarySeed
{
    public string Key { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public List<string> ArgsTemplate { get; set; } = [];
    public List<TemplateParameterConfig> Parameters { get; set; } = [];
}

public sealed class TemplateParameterConfig
{
    public string Name { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string? Format { get; set; }
    public object? DefaultValue { get; set; }
}
