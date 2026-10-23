#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Itenso.TimePeriod;
using Microsoft.Extensions.DependencyInjection;
using MailCal.Calendar.ViewModels.Data;
using MailCal.Core.Domain;
using MailCal.Core.Domain.Entities.Calendar;
using MailCal.Core.Domain.Entities.Shared;
using MailCal.Core.Domain.Enums;
using MailCal.Core.Domain.Exceptions;
using MailCal.Core.Domain.Interfaces;
using MailCal.Core.Domain.Models.Calendar;
using MailCal.Messaging.Client.Calendar;
using MailCal.Messaging.Client.Shell;

namespace MailCal.Mail.ViewModels.Companion;

/// <summary>
/// The tray companion: a month calendar, the events of the selected day from every calendar the
/// calendar page shows, and a quick-add form for that day. State is exposed as plain booleans;
/// the host maps them to its own visibility type.
/// </summary>
public partial class CompanionDashboardViewModel : ObservableObject, IDisposable
{
    /// <summary>Messages are coalesced for this long before the content reloads.</summary>
    public static readonly TimeSpan RefreshDebounce = TimeSpan.FromMilliseconds(250);

    private static readonly TimeSpan LatestQuickStart = new(23, 0, 0);
    private static readonly TimeSpan LatestQuickEnd = new(23, 59, 0);
    private static readonly TimeSpan OtherDayQuickStart = new(9, 0, 0);

    /// <summary>The month calendar shows at most this many event colors per day.</summary>
    private const int MaxDayIndicatorColors = 3;

    /// <summary>When widening the loaded period would exceed this many months, it restarts around the requested day.</summary>
    private const int MaxLoadedMonths = 12;

    private readonly IServiceProvider _services;
    private readonly ICompanionActionHandler _actions;
    private readonly IMessenger _messenger;
    private readonly IDispatcher _dispatcher;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private CancellationTokenSource? _visibilityCancellation;
    private CancellationTokenSource? _debounceCancellation;
    private CalendarSettings? _calendarSettings;
    private long _generation;
    private bool _recipientsRegistered;
    private bool _disposed;

    // Events are loaded for a period of months around the shown month. The day list reads from it,
    // and the month calendar marks the days that have events. The requested period can be wider than
    // the loaded one while a reload is pending.
    private DateTime _requestedStart;
    private DateTime _requestedEnd;
    private DateTime _loadedStart;
    private DateTime _loadedEnd;
    private List<CalendarItem> _loadedItems = [];
    private Dictionary<DateTime, string[]> _dayIndicators = [];

    public CompanionDashboardViewModel(
        IServiceProvider services,
        ICompanionActionHandler actions,
        IDispatcher dispatcher)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _actions = actions ?? throw new ArgumentNullException(nameof(actions));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _messenger = services.GetRequiredService<IMessenger>();
        ResetQuickTimes();
    }

    /// <summary>Raised after a navigation command succeeded, so the host can close its surface.</summary>
    public event EventHandler? NavigationCompleted;

    /// <summary>Raised after the content was reloaded (successfully or not). Hosts may resize.</summary>
    public event EventHandler? ContentRefreshed;

    /// <summary>Raised when the colors returned by <see cref="GetDayIndicatorColors"/> changed.</summary>
    public event EventHandler? DayIndicatorsChanged;

    public ObservableCollection<CompanionDayEvent> DayEvents { get; } = [];
    public ObservableCollection<CompanionCalendarOption> WritableCalendars { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInitializing))]
    [NotifyPropertyChangedFor(nameof(IsNoAccounts))]
    [NotifyPropertyChangedFor(nameof(IsReady))]
    [NotifyPropertyChangedFor(nameof(IsUnavailable))]
    public partial CompanionSurfaceState SurfaceState { get; set; } = CompanionSurfaceState.Initializing;

    /// <summary>The day the list and the quick-add form are for. Always a date without time.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedDateText))]
    [NotifyPropertyChangedFor(nameof(IsTodaySelected))]
    public partial DateTime SelectedDate { get; set; } = DateTime.Today;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateQuickEventCommand))]
    public partial string QuickTitle { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateQuickEventCommand))]
    public partial bool QuickIsAllDay { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateQuickEventCommand))]
    public partial TimeSpan? QuickStart { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateQuickEventCommand))]
    public partial TimeSpan? QuickEnd { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateQuickEventCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenEventEditorCommand))]
    public partial CompanionCalendarOption? SelectedQuickCalendar { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateQuickEventCommand))]
    public partial bool IsCreatingQuickEvent { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string ErrorText { get; set; } = string.Empty;

    public bool IsInitializing => SurfaceState == CompanionSurfaceState.Initializing;
    public bool IsNoAccounts => SurfaceState == CompanionSurfaceState.NoAccounts;
    public bool IsReady => SurfaceState == CompanionSurfaceState.Ready;
    public bool IsUnavailable => SurfaceState == CompanionSurfaceState.Unavailable;
    public bool HasDayEvents => DayEvents.Count > 0;
    public bool HasWritableCalendars => WritableCalendars.Count > 0;
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorText);
    public bool IsTodaySelected => SelectedDate == DateTime.Today;

    public string SelectedDateText
    {
        get
        {
            var format = CultureInfo.CurrentCulture.DateTimeFormat;
            return SelectedDate.ToString($"dddd, {format.MonthDayPattern}", CultureInfo.CurrentCulture);
        }
    }

    /// <summary>"24HourClock" or "12HourClock", following the calendar settings.</summary>
    public string ClockIdentifier => _calendarSettings?.DayHeaderDisplayType == DayHeaderDisplayType.TwentyFourHour
        ? "24HourClock"
        : "12HourClock";

    /// <summary>The first day of the week from Settings > Calendar, otherwise the culture's.</summary>
    public DayOfWeek FirstDayOfWeek => _calendarSettings?.FirstDayOfWeek
                                       ?? CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;

    /// <summary>True between <see cref="OpenAsync"/> and <see cref="Close"/>.</summary>
    public bool IsOpen => _recipientsRegistered;

    /// <summary>
    /// The colors of the first events on <paramref name="day"/>, one per calendar color, at most three.
    /// An empty string stands for an event without a color. Empty when the day has no events.
    /// </summary>
    public string[] GetDayIndicatorColors(DateTime day)
        => _dayIndicators.TryGetValue(day.Date, out var colors) ? colors : [];

    /// <summary>
    /// Called by the host for each day the month calendar shows. Loads the events around that day
    /// when it is outside the loaded period, then raises <see cref="DayIndicatorsChanged"/>.
    /// </summary>
    public void EnsureDayIndicators(DateTime day)
    {
        day = day.Date;
        if (SurfaceState != CompanionSurfaceState.Ready || IsRequested(day))
            return;

        RequestPeriodAround(day);
        ScheduleRefresh();
    }

    /// <summary>Loads the surface for the host's current readiness and follows changes until <see cref="Close"/>.</summary>
    public async Task OpenAsync(CompanionReadinessState readiness, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        _visibilityCancellation?.Cancel();
        _visibilityCancellation?.Dispose();
        _visibilityCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        RegisterRecipients();

        // Each opening starts on today, with the events of the months around it.
        _requestedStart = _requestedEnd = default;
        RequestPeriodAround(DateTime.Today);
        if (SelectedDate != DateTime.Today)
            SelectedDate = DateTime.Today;
        else
            ResetQuickTimes();

        switch (readiness)
        {
            case CompanionReadinessState.NoAccounts:
                ClearContent();
                SurfaceState = CompanionSurfaceState.NoAccounts;
                break;
            case CompanionReadinessState.Ready:
                await RefreshAsync(_visibilityCancellation.Token);
                break;
            default:
                ClearContent();
                SurfaceState = CompanionSurfaceState.Initializing;
                break;
        }
    }

    public void Close()
    {
        _generation++;
        _debounceCancellation?.Cancel();
        _visibilityCancellation?.Cancel();
        UnregisterRecipients();
    }

    public void ReportActionError(Exception exception)
        => ErrorText = exception.Message;

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        var generation = ++_generation;
        if (!IsRequested(SelectedDate))
            RequestPeriodAround(SelectedDate);

        var periodStart = _requestedStart;
        var periodEnd = _requestedEnd;

        // Only the first load shows the progress state; later reloads keep the content in place.
        if (SurfaceState != CompanionSurfaceState.Ready)
            SurfaceState = CompanionSurfaceState.Initializing;

        await _refreshGate.WaitAsync(cancellationToken);

        try
        {
            var accounts = await _services.GetRequiredService<IAccountService>().GetAccountsAsync();
            cancellationToken.ThrowIfCancellationRequested();
            if (accounts.Count == 0)
            {
                await RunOnUIAsync(() =>
                {
                    ClearContent();
                    SurfaceState = CompanionSurfaceState.NoAccounts;
                });
                return;
            }

            var settings = _services.GetRequiredService<IPreferencesService>().GetCurrentCalendarSettings();
            var (items, calendars) = await LoadPeriodAsync(accounts, periodStart, periodEnd, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            if (generation != _generation)
                return;

            await RunOnUIAsync(() => ApplyResults(settings, periodStart, periodEnd, items, calendars));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            await RunOnUIAsync(() =>
            {
                ClearContent();
                ErrorText = ex.Message;
                SurfaceState = CompanionSurfaceState.Unavailable;
            });
        }
        finally
        {
            _refreshGate.Release();
            ContentRefreshed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Events between <paramref name="start"/> and <paramref name="end"/> in every calendar the calendar
    /// page shows, without declined ones, and the writable calendars among those calendars.
    /// </summary>
    private async Task<(List<CalendarItem> Items, List<CompanionCalendarOption> Calendars)> LoadPeriodAsync(
        IReadOnlyList<MailAccount> accounts,
        DateTime start,
        DateTime end,
        CancellationToken cancellationToken)
    {
        var calendarService = _services.GetRequiredService<ICalendarService>();
        var period = new TimeRange(start, end);
        var loaded = new Dictionary<Guid, CalendarItem>();
        var calendars = new List<CompanionCalendarOption>();

        foreach (var account in accounts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var accountCalendars = await calendarService.GetAccountCalendarsAsync(account.Id);

            // IsExtended is the calendar's "shown" checkbox on the calendar page.
            foreach (var calendar in accountCalendars.Where(static calendar => calendar.IsExtended))
            {
                if (!calendar.IsReadOnly)
                    calendars.Add(new CompanionCalendarOption(calendar, account.Name ?? account.Address ?? string.Empty));

                var items = await calendarService.GetCalendarEventsAsync(calendar, period);
                foreach (var item in items)
                {
                    if (item.IsRecurringParent || item.IsHidden || loaded.ContainsKey(item.Id))
                        continue;

                    item.AssignedCalendar ??= calendar;
                    if (await IsDeclinedAsync(calendarService, item, account))
                        continue;

                    loaded.Add(item.Id, item);
                }
            }
        }

        var orderedCalendars = calendars
            .OrderByDescending(static option => option.Calendar.IsPrimary)
            .ThenBy(static option => option.AccountName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(static option => option.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        return ([.. loaded.Values], orderedCalendars);
    }

    /// <summary>The rows of the day list: all-day events first, then by time.</summary>
    private static List<CompanionDayEvent> BuildDayEvents(IEnumerable<CalendarItem> items, DateTime day, CalendarSettings settings)
    {
        var dayEnd = day.AddDays(1);
        var period = new TimeRange(day, dayEnd);

        return items
            .Where(item => Overlaps(item, day, dayEnd))
            .Select(item => new CompanionDayEvent(new CalendarItemViewModel(item) { DisplayingPeriod = period }, day, settings))
            .OrderByDescending(static row => row.IsAllDay)
            .ThenBy(static row => row.Item.StartDate)
            .ThenBy(static row => row.Item.EndDate)
            .ThenBy(static row => row.Item.Id)
            .ToList();
    }

    /// <summary>Every day between <paramref name="start"/> and <paramref name="end"/> with events, mapped to their first colors.</summary>
    private static Dictionary<DateTime, string[]> BuildDayIndicators(IEnumerable<CalendarItem> items, DateTime start, DateTime end)
    {
        var colorsByDay = new Dictionary<DateTime, List<string>>();

        foreach (var item in items.OrderBy(static item => item.LocalStartDate))
        {
            var first = item.LocalStartDate.Date < start ? start : item.LocalStartDate.Date;

            // The end is exclusive: an event that ends at midnight does not mark the next day.
            var last = item.LocalEndDate > item.LocalStartDate ? item.LocalEndDate.AddTicks(-1).Date : item.LocalStartDate.Date;
            if (last >= end)
                last = end.AddDays(-1);

            var color = CompanionDayEvent.GetColorHex(item);
            for (var day = first; day <= last; day = day.AddDays(1))
            {
                if (!colorsByDay.TryGetValue(day, out var colors))
                    colorsByDay[day] = colors = [];

                if (colors.Count < MaxDayIndicatorColors && !colors.Contains(color, StringComparer.OrdinalIgnoreCase))
                    colors.Add(color);
            }
        }

        return colorsByDay.ToDictionary(static pair => pair.Key, static pair => pair.Value.ToArray());
    }

    private static bool Overlaps(CalendarItem item, DateTime start, DateTime end)
        => item.LocalStartDate < end &&
           (item.LocalEndDate > start || (item.LocalEndDate <= item.LocalStartDate && item.LocalStartDate >= start));

    private bool IsRequested(DateTime day) => day >= _requestedStart && day < _requestedEnd;

    private bool IsLoaded(DateTime day) => day >= _loadedStart && day < _loadedEnd;

    /// <summary>
    /// Widens the requested period to the month of <paramref name="day"/> and one month on each side.
    /// When that would cover more than <see cref="MaxLoadedMonths"/>, the period restarts around the day.
    /// </summary>
    private void RequestPeriodAround(DateTime day)
    {
        var month = new DateTime(day.Year, day.Month, 1);
        var start = month.AddMonths(-1);
        var end = month.AddMonths(2);

        if (_requestedEnd > _requestedStart)
        {
            var widenedStart = start < _requestedStart ? start : _requestedStart;
            var widenedEnd = end > _requestedEnd ? end : _requestedEnd;
            if (widenedEnd <= widenedStart.AddMonths(MaxLoadedMonths))
            {
                start = widenedStart;
                end = widenedEnd;
            }
        }

        _requestedStart = start;
        _requestedEnd = end;
    }

    private static async Task<bool> IsDeclinedAsync(ICalendarService calendarService, CalendarItem item, MailAccount account)
    {
        var attendees = await calendarService.GetAttendeesAsync(item.EventTrackingId);
        return attendees.Any(attendee =>
            attendee.AttendenceStatus == AttendeeStatus.Declined &&
            string.Equals(attendee.Email?.Trim(), account.Address?.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private void ApplyResults(
        CalendarSettings settings,
        DateTime periodStart,
        DateTime periodEnd,
        List<CalendarItem> items,
        IReadOnlyList<CompanionCalendarOption> calendars)
    {
        _calendarSettings = settings;
        _loadedStart = periodStart;
        _loadedEnd = periodEnd;
        _loadedItems = items;
        _dayIndicators = BuildDayIndicators(items, periodStart, periodEnd);
        Replace(DayEvents, BuildDayEvents(items, SelectedDate, settings));
        DayIndicatorsChanged?.Invoke(this, EventArgs.Empty);

        var selectedId = SelectedQuickCalendar?.Id;
        Replace(WritableCalendars, calendars);
        SelectedQuickCalendar = WritableCalendars.FirstOrDefault(option => option.Id == selectedId)
                                ?? WritableCalendars.FirstOrDefault();

        ErrorText = string.Empty;
        RaiseContentProperties();
        OnPropertyChanged(nameof(ClockIdentifier));
        OnPropertyChanged(nameof(FirstDayOfWeek));
        SurfaceState = CompanionSurfaceState.Ready;
    }

    private void ClearContent()
    {
        DayEvents.Clear();
        WritableCalendars.Clear();
        SelectedQuickCalendar = null;
        _loadedStart = _loadedEnd = default;
        _loadedItems = [];
        _dayIndicators = [];
        DayIndicatorsChanged?.Invoke(this, EventArgs.Empty);
        RaiseContentProperties();
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> values)
    {
        target.Clear();
        foreach (var value in values)
            target.Add(value);
    }

    private void RaiseContentProperties()
    {
        OnPropertyChanged(nameof(HasDayEvents));
        OnPropertyChanged(nameof(HasWritableCalendars));
    }

    partial void OnSelectedDateChanged(DateTime value)
    {
        if (value.TimeOfDay != TimeSpan.Zero)
        {
            SelectedDate = value.Date;
            return;
        }

        ResetQuickTimes();
        if (SurfaceState != CompanionSurfaceState.Ready)
            return;

        // A day inside the loaded period is shown from the loaded events without a reload.
        if (IsLoaded(value) && IsRequested(value) && _calendarSettings is { } settings)
        {
            Replace(DayEvents, BuildDayEvents(_loadedItems, value, settings));
            RaiseContentProperties();
            ContentRefreshed?.Invoke(this, EventArgs.Empty);
            return;
        }

        RequestPeriodAround(value);
        ScheduleRefresh(immediately: true);
    }

    /// <summary>
    /// Today: the next full hour, for one hour. Another day: 09:00 to 10:00. The latest start is 23:00,
    /// so the event still ends on the same day.
    /// </summary>
    private void ResetQuickTimes()
    {
        TimeSpan start;
        if (SelectedDate == DateTime.Today)
        {
            var nextHour = TimeSpan.FromHours(DateTime.Now.Hour + 1);
            start = nextHour > LatestQuickStart ? LatestQuickStart : nextHour;
        }
        else
        {
            start = OtherDayQuickStart;
        }

        var end = start + TimeSpan.FromHours(1);
        QuickStart = start;
        QuickEnd = end > LatestQuickEnd ? LatestQuickEnd : end;
    }

    private void RegisterRecipients()
    {
        if (_recipientsRegistered)
            return;

        _recipientsRegistered = true;
        _messenger.Register<CompanionDashboardViewModel, CalendarItemAdded>(this, static (recipient, _) => recipient.ScheduleRefresh());
        _messenger.Register<CompanionDashboardViewModel, CalendarItemUpdated>(this, static (recipient, _) => recipient.ScheduleRefresh());
        _messenger.Register<CompanionDashboardViewModel, CalendarItemDeleted>(this, static (recipient, _) => recipient.ScheduleRefresh());
        _messenger.Register<CompanionDashboardViewModel, CalendarListAdded>(this, static (recipient, _) => recipient.ScheduleRefresh());
        _messenger.Register<CompanionDashboardViewModel, CalendarListUpdated>(this, static (recipient, _) => recipient.ScheduleRefresh());
        _messenger.Register<CompanionDashboardViewModel, CalendarListDeleted>(this, static (recipient, _) => recipient.ScheduleRefresh());

        // Time format and other calendar settings apply while the companion is open.
        _messenger.Register<CompanionDashboardViewModel, PreferenceChangedMessage>(this, static (recipient, message) =>
        {
            if (CalendarSettings.PreferenceKeys.Contains(message.PropertyName))
                recipient.ScheduleRefresh();
        });
    }

    private void UnregisterRecipients()
    {
        if (!_recipientsRegistered)
            return;

        _recipientsRegistered = false;
        _messenger.UnregisterAll(this);
    }

    private void ScheduleRefresh(bool immediately = false)
    {
        if (_disposed || _visibilityCancellation is null || _visibilityCancellation.IsCancellationRequested)
            return;

        _debounceCancellation?.Cancel();
        _debounceCancellation?.Dispose();
        _debounceCancellation = CancellationTokenSource.CreateLinkedTokenSource(_visibilityCancellation.Token);
        var token = _debounceCancellation.Token;
        _ = DebounceRefreshAsync(immediately ? TimeSpan.Zero : RefreshDebounce, token);
    }

    private async Task DebounceRefreshAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);

            await RunOnUIAsync(() => _ = RefreshFromUIAsync(cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            await RunOnUIAsync(() =>
            {
                ErrorText = ex.Message;
                SurfaceState = CompanionSurfaceState.Unavailable;
            });
        }
    }

    private async Task RefreshFromUIAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RefreshAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
            SurfaceState = CompanionSurfaceState.Unavailable;
        }
    }

    [RelayCommand]
    private void GoToToday() => SelectedDate = DateTime.Today;

    [RelayCommand]
    private Task OpenMailCalAsync() => ExecuteNavigationAsync(_actions.OpenMailCalAsync);

    [RelayCommand]
    private Task OpenCalendarAsync() => ExecuteNavigationAsync(_actions.OpenCalendarAsync);

    [RelayCommand]
    private Task OpenSettingsAsync() => ExecuteNavigationAsync(_actions.OpenSettingsAsync);

    [RelayCommand]
    private Task OpenCalendarEventAsync(CompanionDayEvent dayEvent)
        => ExecuteNavigationAsync(token => _actions.OpenCalendarEventAsync(dayEvent.Item, token));

    [RelayCommand]
    private Task JoinOnlineAsync(CompanionDayEvent dayEvent)
        => dayEvent.JoinUri is { } joinUri
            ? ExecuteNavigationAsync(token => _actions.JoinOnlineAsync(joinUri, token))
            : Task.CompletedTask;

    private bool CanOpenEventEditor() => SurfaceState == CompanionSurfaceState.Ready;

    [RelayCommand(CanExecute = nameof(CanOpenEventEditor))]
    private Task OpenEventEditorAsync()
        => ExecuteNavigationAsync(token => _actions.OpenEventEditorAsync(CreateDraft(), token));

    private bool CanCreateQuickEvent()
        => !IsCreatingQuickEvent &&
           SelectedQuickCalendar is not null &&
           !string.IsNullOrWhiteSpace(QuickTitle) &&
           (QuickIsAllDay || (QuickStart is { } start && QuickEnd is { } end && end > start));

    [RelayCommand(CanExecute = nameof(CanCreateQuickEvent))]
    private async Task CreateQuickEventAsync()
    {
        IsCreatingQuickEvent = true;
        ErrorText = string.Empty;

        try
        {
            await _actions.CreateQuickEventAsync(CreateDraft(), CurrentToken);

            // The list picks the event up through CalendarItemAdded.
            QuickTitle = string.Empty;
        }
        catch (OperationCanceledException) when (CurrentToken.IsCancellationRequested)
        {
        }
        catch (CalendarEventComposeValidationException ex)
        {
            ErrorText = ex.Message;
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
        }
        finally
        {
            IsCreatingQuickEvent = false;
        }
    }

    private CompanionEventDraft CreateDraft()
    {
        var day = SelectedDate.Date;
        if (QuickIsAllDay)
            return new CompanionEventDraft(SelectedQuickCalendar?.Id, QuickTitle, day, day.AddDays(1), IsAllDay: true);

        var start = day + (QuickStart ?? OtherDayQuickStart);
        var end = day + (QuickEnd ?? (QuickStart ?? OtherDayQuickStart) + TimeSpan.FromHours(1));
        return new CompanionEventDraft(SelectedQuickCalendar?.Id, QuickTitle, start, end, IsAllDay: false);
    }

    partial void OnSurfaceStateChanged(CompanionSurfaceState value) => OpenEventEditorCommand.NotifyCanExecuteChanged();

    private async Task ExecuteNavigationAsync(Func<CancellationToken, Task> action)
    {
        try
        {
            await action(CurrentToken);
            NavigationCompleted?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException) when (CurrentToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
        }
    }

    private CancellationToken CurrentToken => _visibilityCancellation?.Token ?? CancellationToken.None;

    private Task RunOnUIAsync(Action action) => _dispatcher.ExecuteOnUIThread(action);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Close();
        _debounceCancellation?.Dispose();
        _visibilityCancellation?.Dispose();
        _refreshGate.Dispose();
        GC.SuppressFinalize(this);
    }
}
