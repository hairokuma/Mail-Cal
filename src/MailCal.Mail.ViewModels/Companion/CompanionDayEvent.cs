#nullable enable
using System;
using MailCal.Calendar.ViewModels.Data;
using MailCal.Core.Domain;
using MailCal.Core.Domain.Entities.Calendar;
using MailCal.Core.Domain.Models.Calendar;

namespace MailCal.Mail.ViewModels.Companion;

/// <summary>One event in the companion's day list, with its text for that day.</summary>
public sealed class CompanionDayEvent
{
    public CompanionDayEvent(CalendarItemViewModel item, DateTime day, CalendarSettings settings)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(settings);

        Item = item;
        var dayStart = day.Date;
        var dayEnd = dayStart.AddDays(1);
        IsAllDay = item.IsAllDayEvent || (item.StartDate <= dayStart && item.EndDate >= dayEnd);

        // A timed event that spans midnight shows only its part of this day.
        var start = item.StartDate < dayStart ? dayStart : item.StartDate;
        var end = item.EndDate > dayEnd ? dayEnd : item.EndDate;
        TimeText = IsAllDay
            ? Translator.QuickEventDialog_IsAllDay
            : $"{settings.GetTimeString(start.TimeOfDay)} – {settings.GetTimeString(end.TimeOfDay)}";

        JoinUri = CalendarJoinLinkResolver.TryGetMeetingJoinUri(item.CalendarItem, out var joinUri) ? joinUri : null;
    }

    public CalendarItemViewModel Item { get; }

    /// <summary>The online meeting link of the event, from the provider or found in its description; null without one.</summary>
    public Uri? JoinUri { get; }
    public bool HasJoinLink => JoinUri is not null;
    public bool IsAllDay { get; }
    public string TimeText { get; }
    public string Title => string.IsNullOrWhiteSpace(Item.Title) ? Translator.Companion_UntitledEvent : Item.Title;
    public string Location => Item.Location ?? string.Empty;
    public bool HasLocation => !string.IsNullOrWhiteSpace(Item.Location);
    public string CalendarName => Item.AssignedCalendar?.Name ?? string.Empty;

    public string ColorHex => GetColorHex(Item.CalendarItem);

    /// <summary>The event's own color, otherwise its calendar's; empty when neither is set.</summary>
    public static string GetColorHex(CalendarItem item)
    {
        var customColor = item.CustomEventColorHex;
        return !string.IsNullOrWhiteSpace(customColor)
            ? customColor
            : item.AssignedCalendar?.BackgroundColorHex ?? string.Empty;
    }
}
