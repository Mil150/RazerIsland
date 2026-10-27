using System.Net;
using System.Net.Http;
using RazerIsland.Core;

namespace RazerIsland.Calendar;

public enum CalStatus { Loading, Ok, Error }
public enum CalError { None, Offline, Timeout, BadLink, Server, NotIcs, Parse }

/// <summary>Стан одного iCal-посилання: результат останнього завантаження й кеш подій.</summary>
public sealed class CalendarSource
{
    public required string Url { get; init; }
    public CalStatus Status { get; set; } = CalStatus.Loading;
    public CalError Error { get; set; }
    public List<CalendarEvent> Events { get; set; } = [];
    public DateTime? LastSuccess { get; set; }
    public string? Name { get; set; }
    /// <summary>Завантаження вже йде (Status при цьому лишається попереднім, щоб статус не блимав).</summary>
    internal bool Busy { get; set; }

    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(24);
    public bool IsStale => LastSuccess is not { } t || DateTime.Now - t > StaleAfter;

    /// <summary>Назва для показу; без X-WR-CALNAME — «Календар N», як у налаштуваннях.</summary>
    public string Title(int index) => Name ?? $"{L.T("Календар", "Calendar")} {index + 1}";

    public static CalError Classify(HttpStatusCode code) => (int)code switch
    {
        400 or 401 or 403 or 404 or 410 => CalError.BadLink,
        _ => CalError.Server,
    };

    public static CalError Classify(Exception e) => e switch
    {
        HttpRequestException { StatusCode: { } code } => Classify(code),
        HttpRequestException => CalError.Offline,
        TaskCanceledException or TimeoutException => CalError.Timeout,
        UriFormatException => CalError.BadLink,
        _ => CalError.Parse,
    };

    public static string Describe(CalError e) => e switch
    {
        CalError.Offline => L.T("Немає інтернету", "No internet connection"),
        CalError.Timeout => L.T("Сервер не відповідає", "Server not responding"),
        CalError.BadLink => L.T("Посилання недійсне або закрите", "Link is invalid or private"),
        CalError.Server => L.T("Помилка сервера календаря", "Calendar server error"),
        CalError.NotIcs => L.T("Це не посилання iCal", "Not an iCal link"),
        CalError.Parse => L.T("Не вдалося прочитати календар", "Could not read the calendar"),
        _ => "",
    };
}
