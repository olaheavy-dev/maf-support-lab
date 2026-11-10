namespace Support.Api.Modules.Agents.Infrastructure.ModelProvider;

internal static partial class ModelProviderLog
{
    /// <summary>Logs which model mode the API starts in. Never logs the model key.</summary>
    [LoggerMessage(Level = LogLevel.Information, Message = "Support API starting. Model provider mode: {ModelMode}")]
    public static partial void StartingInMode(ILogger logger, ModelMode modelMode);
}
