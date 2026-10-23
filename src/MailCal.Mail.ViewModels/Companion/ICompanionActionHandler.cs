#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using MailCal.Calendar.ViewModels.Data;

namespace MailCal.Mail.ViewModels.Companion;

public interface ICompanionActionHandler
{
    Task OpenMailCalAsync(CancellationToken cancellationToken);
    Task OpenCalendarAsync(CancellationToken cancellationToken);
    Task OpenCalendarEventAsync(CalendarItemViewModel calendarItem, CancellationToken cancellationToken);
    Task OpenEventEditorAsync(CompanionEventDraft draft, CancellationToken cancellationToken);
    Task OpenSettingsAsync(CancellationToken cancellationToken);

    /// <summary>Opens an online meeting link with the system's handler for it.</summary>
    Task JoinOnlineAsync(Uri joinUri, CancellationToken cancellationToken);

    /// <summary>Creates the event in its calendar. Throws a validation exception with a user-facing message.</summary>
    Task CreateQuickEventAsync(CompanionEventDraft draft, CancellationToken cancellationToken);
}
