using System.Text;

namespace TeamsHomeworkChecker;

public class MessageGenerator
{
  private static readonly string _htmlStart = "<html><body style=\"font-family: Arial; font-size: 11pt\">";
  private static readonly string _tableStart = "<table style=\"border-collapse:collapse\">";
  private static readonly string _htmlEnd = "<br/><br/><br/></body></html>";

  private static readonly string _td = "border: 1px solid black; padding: 6px; vertical-align: top; font-family: Arial; font-size: 10pt";
  private static readonly string _firstTdStyle = $"style=\"{_td}; width: 15%; border-right: 1px solid #ccc\"";
  private static readonly string _middleTdStyle = $"style=\"{_td}; width: 15%; border-left: 1px solid #ccc; border-right: 1px solid #ccc\"";
  private static readonly string _rightTdStyle = $"style=\"{_td}; width: 15%; border-left: 1px solid #ccc\"";
  private static readonly string _singleTdStyle = $"style=\"{_td}; width: 15%\"";

  private static readonly string _tick = "&#9989;";
  private static readonly string _cross = "&#10060;";

  private readonly string _schoolName;
  private readonly DateOnly _endDate;
  private readonly byte _defaultWeeks;
  private readonly List<Class> _allClasses;
  private readonly StringBuilder _seniorTeam;

  private readonly int _overallPercentage;

  public MessageGenerator(string schoolName, DateOnly endDate, byte defaultWeeks, List<Class> allClasses)
  {
    _schoolName = schoolName;
    _endDate = endDate;
    _defaultWeeks = defaultWeeks;
    _allClasses = allClasses;
    var (perc, ks3perc, ks4perc, ks5perc) = GetPercentages(allClasses);    
    _seniorTeam = new($"{_htmlStart}{_tableStart}<tr>" +
    $"<td style=\"{_td}; text-align:center; width: 10%; background-color: {GetColour(perc, true)}\"></td>" +
    $"<td colspan=\"3\" style=\"{_td}; text-align:center; background-color: {GetColour(ks3perc)}\"><b>Key Stage 3</b> ({ks3perc}%)</td>" +
    $"<td colspan=\"2\" style=\"{_td}; text-align:center; background-color: {GetColour(ks4perc)}\"><b>Key Stage 4</b> ({ks4perc}%)</td>" +
    $"<td style=\"{_td}; text-align:center; background-color: {GetColour(ks5perc)}\"><b>Key Stage 5</b> ({ks5perc}%)</td>" +
    $"</tr>");
    _overallPercentage = perc;
  }

  public (string Body, int Percentage) GenerateDepartmentEmail(Department department, string curriculumLeaderFirstName, List<Class> classes)
  {
    var classesByYear = classes.ToLookup(o => o.Year == 13 ? 12 : o.Year);
    var perc = GetPercentage(classes);
    var name = string.IsNullOrEmpty(department.CurriculumLeader) ? department.Name : $"{department.Name} ({department.CurriculumLeader})";
    var body = new StringBuilder($"<tr><td style=\"{_td}; width: 10%; background-color: {GetColour(perc)}\"><b>{name}</b><br/>{perc}%</td>");
    for (var year = 7; year <= 12; year++)
    {
      var colStyle = year == 12 ? _singleTdStyle : year is 7 or 10 ? _firstTdStyle : year == 8 ? _middleTdStyle : _rightTdStyle;
      body.Append($"<td {colStyle}>");
      var classesInYear = classesByYear[year].ToList();
      for (var i = 0; i < classesInYear.Count; i++)
      {
        var cls = classesInYear[i];
        body.Append($"{(cls.HasCurrentHomework ? _tick : _cross)} {cls.Name} ({cls.TeacherCodes[0]}){GetSuperscript(cls.Weeks, true)}{GetStreakText(cls)}");
        if (i < classesInYear.Count - 1)
          body.Append("<br/>");
      }
      body.Append("</td>");
    }
    body.Append("</tr>");
    _seniorTeam.Append(body);
    
    body.Insert(0, $"{_htmlStart}Hi {curriculumLeaderFirstName}<br/><br/>This table shows which {department.Name} classes have had homework due recently." +
      $"<br/><br/>{_tableStart}<tr><td style=\"{_td}; text-align:center; width: 10%\"></td><td colspan=\"3\" style=\"{_td}; text-align:center\"><b>Key Stage 3</b></td>" +
      $"<td colspan=\"2\" style=\"{_td}; text-align:center\"><b>Key Stage 4</b></td><td style=\"{_td}; text-align:center\"><b>Key Stage 5</b></td></tr>");
    
    body.Append("</tr></table><br/>");
    AppendCheckingDatesDescription(body, classes);
    body.Append("<br/><br/>");

    for (var year = 13; year >= 7; year--)
    {
      if (!classesByYear[year].Any(o => o.HasCurrentHomework)) continue;
      body.Append($"{(year == 12 ? "Sixth Form" : $"Year {year}")} tasks:<br/><br/><ul style=\"margin: 0\">");
      foreach (var cls in classesByYear[year])
      {
        foreach (var hw in cls.CurrentHomework)
        {
          body.Append($"<li><b>{cls.Name} &ndash; {hw.Title}</b> &ndash; {hw.Instructions} <i>(due {hw.DueDate:d MMM})</i></li>");
        }
      }
      body.Append("</ul><br/>");
    }
    body.Append($"Best wishes<br/><br/>{_schoolName}{_htmlEnd}");

    return (body.ToString(), perc);
  }

  public (string Body, int Percentage) GenerateSeniorTeamEmail()
  {
    _seniorTeam.Append($"</table><br/>");
    AppendCheckingDatesDescription(_seniorTeam, _allClasses);
    _seniorTeam.Append(_htmlEnd);
    return (_seniorTeam.ToString(), _overallPercentage);
  }

  public string GenerateTeacherEmail(Teacher teacher, List<Class> classes)
  {
    var body = new StringBuilder(_htmlStart);
    body.Append($"Hi {teacher.First}<br/><br/>Here is a summary of which classes have had homework recently.<br/><br/>");
    foreach (var cls in classes)
    {
      body.Append($"{(cls.HasCurrentHomework ? _tick : _cross)} {cls.Name}{GetSuperscript(cls.Weeks, true)}{GetStreakText(cls)}<br/>");
    }
    body.Append("<br/>");
    AppendCheckingDatesDescription(body, classes);
    body.Append($"<br/>Best wishes<br/><br/>{_schoolName}{_htmlEnd}");
    return body.ToString();
  }

  private void AppendCheckingDatesDescription(StringBuilder sb, List<Class> classes)
  {
    var periods = classes.Select(o => (o.Weeks, o.StartDate)).DistinctBy(o => o.Weeks).OrderBy(o => o.Weeks);
    var defaultPeriod = periods.FirstOrDefault(o => o.Weeks == _defaultWeeks);
    if (defaultPeriod != default)
    {
      sb.Append($"Each class shows a tick if there was at least one assignment with a due date between {defaultPeriod.StartDate:d MMMM} and {_endDate:d MMMM}.<br/>");
    }
    foreach (var (weeks, startDate) in periods.Where(o => o.Weeks != _defaultWeeks))
    {
      var title = weeks == 1 ? "weekly" : weeks == 2 ? "fortnightly" : $"{weeks}-weekly";
      sb.Append($"{GetSuperscript(weeks)} Note: {title} homework shows a tick if there was at least one assignment " +
        $"with a due date between {startDate:d MMMM} and {_endDate:d MMMM}.<br/>");
    }
  }

  private string GetSuperscript(int weeks, bool includeSpace = false) =>
    weeks == _defaultWeeks ? string.Empty : $"{(includeSpace ? " " : string.Empty)}<sup>{(weeks == 1 ? "W" : weeks == 2 ? "F" : weeks)}</sup>";

  private static (int Overall, int KS3, int KS4, int KS5) GetPercentages(IEnumerable<Class> classes) =>
    (
      GetPercentage(classes),
      GetPercentage(classes.Where(o => o.Year <= 9)),
      GetPercentage(classes.Where(o => o.Year is 10 or 11)),
      GetPercentage(classes.Where(o => o.Year >= 12))
    );

  private static int GetPercentage(IEnumerable<Class> classes) =>
    (int)Math.Round(classes.Average(o => o.HasCurrentHomework ? 1 : 0) * 100, 0);

  private static string GetColour(int perc, bool isPrimary = false) =>
    perc switch
    {
      < 50 => isPrimary ? "#ff2f2f" : "#ffcccc",
      < 80 => isPrimary ? "#ffcd33" : "#fff0c2",
      _ => isPrimary ? "#3aff2a" : "#d0ffcc"
    };

  private static string GetStreakText(Class cls) =>
    cls.Streak == 1 ? string.Empty : (cls.HasCurrentHomework
      ? $" <b>&#x1F525;{cls.Streak}</b>"
      : $" <span style=\"font-weight: bold; color: red\">({cls.Streak})</span>");
}
