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
  private readonly DateOnly _startDate;
  private readonly DateOnly _endDate;
  private readonly StringBuilder _seniorTeam;

  private readonly int _overallPercentage;

  public MessageGenerator(string schoolName, DateOnly startDate, DateOnly endDate, List<Class> allClasses)
  {
    _schoolName = schoolName;
    _startDate = startDate;
    _endDate = endDate;
    var (perc, ks3perc, ks4perc, ks5perc) = GetPercentages(allClasses);    
    _seniorTeam = new($"{_htmlStart}{_tableStart}<tr>" +
    $"<td style=\"{_td}; text-align:center; width: 10%; background-color: {GetColour(perc, true)}\"><b>HOMEWORK</b></td>" +
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
        body.Append($"{(cls.Homework.Count > 0 ? _tick : _cross)} {cls.Name} ({cls.TeacherCodes[0]})");
        if (i < classesInYear.Count - 1)
          body.Append("<br/>");
      }
      body.Append("</td>");
    }
    _seniorTeam.Append(body);
    body.Insert(0, $"{_htmlStart}Hi {curriculumLeaderFirstName}<br/><br/>This table shows which {department.Name} classes have been set homework recently." +
      $"<br/><br/>{_tableStart}");
    body.Append("</tr></table><br/><br/>");
    for (var year = 13; year >= 7; year--)
    {
      if (!classesByYear[year].SelectMany(o => o.Homework).Any()) continue;
      body.Append($"<b>{(year == 12 ? "Sixth Form" : $"Year {year}")} tasks:</b><br/><br/><ul style=\"margin: 0\">");
      foreach (var cls in classesByYear[year])
      {
        foreach (var hw in cls.Homework)
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
    _seniorTeam.Append($"</table>{_htmlEnd}");
    return (_seniorTeam.ToString(), _overallPercentage);
  }

  public string GenerateTeacherEmail(Teacher teacher, List<Class> classes)
  {
    var body = new StringBuilder(_htmlStart);
    body.Append($"Hi {teacher.First}<br/><br/>Here is a summary of which classes have had homework set recently (due dates in the period {_startDate:d MMM} to {_endDate:d MMM}).<br/><br/>");
    foreach (var cls in classes)
    {
      body.Append($"{(cls.Homework.Count > 0 ? _tick : _cross)} {cls.Name}{(cls.HasCustomDays ? " *" : string.Empty)}<br/>");
    }
    if (classes.Any(o => o.HasCustomDays))
    {
      body.Append($"<br/>* = A customised range of due dates was used when checking this class.<br/>");
    }
    body.Append($"<br/>Best wishes<br/><br/>{_schoolName}{_htmlEnd}");
    return body.ToString();
  }

  private static (int Overall, int KS3, int KS4, int KS5) GetPercentages(IEnumerable<Class> classes) =>
    (
      GetPercentage(classes),
      GetPercentage(classes.Where(o => o.Year <= 9)),
      GetPercentage(classes.Where(o => o.Year is 10 or 11)),
      GetPercentage(classes.Where(o => o.Year >= 12))
    );

  private static int GetPercentage(IEnumerable<Class> classes) =>
    (int)Math.Round(classes.Average(o => o.Homework.Count > 0 ? 1 : 0) * 100, 0);

  private static string GetColour(int perc, bool isPrimary = false) =>
    perc switch
    {
      < 50 => isPrimary ? "#ff2f2f" : "#ffcccc",
      < 80 => isPrimary ? "#ffcd33" : "#fff0c2",
      _ => isPrimary ? "#3aff2a" : "#d0ffcc"
    };
}
