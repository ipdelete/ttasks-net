namespace Ttasks.ChatApp.Services;

public sealed class ChatAppOptions
{
    public string Model { get; set; } = "gpt-5.5";
    public string SystemMessagePath { get; set; } = "system-message.md";
    public string StorePath { get; set; } = "data\\ttasks-chat.db";
    public int MaxTasks { get; set; } = 8;
    public int MaxWorkers { get; set; } = 3;
    public int DefaultTimeoutSeconds { get; set; } = 30;
    public int MaxGraphRepairAttempts { get; set; } = 2;
    public int MaxTeamsReadMessages { get; set; } = 20;
}
