
using System.Threading;
using System.Threading.Tasks;
using Andril.Communication.Notifications;

namespace Andril.Communication.Interfaces;

/// <summary>
/// Reads trusted notification channel preferences
/// for an authorized private module profile.
/// </summary>
public interface INotificationChannelPreferenceReader
{
    Task<NotificationChannelPreferences?> GetForScopeAsync(
        NotificationModuleScope authorizedScope,
        CancellationToken cancellationToken = default);
}
