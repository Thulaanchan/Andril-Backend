using Andril.Communication.Interfaces;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using ModuleNames = Andril.Common.Accounts.StarterModules.StarterModules;

namespace Andril.Persistence.Configurations;

/// <summary>Strict candidate Chat storage conversion; not production registration.</summary>
internal sealed class ChatModuleValueConverter : ValueConverter<ChatModule, string>
{
    internal ChatModuleValueConverter()
        : base(module => ToStorage(module), module => FromStorage(module))
    {
    }

    private static string ToStorage(ChatModule module) => module switch
    {
        ChatModule.Learning => ModuleNames.Learning,
        ChatModule.Hobbies => ModuleNames.Hobbies,
        ChatModule.Social => ModuleNames.Social,
        _ => throw new ArgumentOutOfRangeException(nameof(module), "Unsupported Chat module.")
    };

    private static ChatModule FromStorage(string module) => module switch
    {
        ModuleNames.Learning => ChatModule.Learning,
        ModuleNames.Hobbies => ChatModule.Hobbies,
        ModuleNames.Social => ChatModule.Social,
        _ => throw new ArgumentException("Unsupported stored Chat module.", nameof(module))
    };
}
