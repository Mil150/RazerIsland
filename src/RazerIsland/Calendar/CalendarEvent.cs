namespace RazerIsland.Calendar;

/// <summary>Подія календаря в локальному часі.</summary>
public sealed record CalendarEvent(DateTime Start, DateTime End, bool AllDay, string Title, string Location, string Calendar = "");
