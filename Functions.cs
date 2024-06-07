using Azure.Identity;
using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using System.Text.Json;

namespace TeamsHomeworkChecker;

public class Functions
{
  [Function("SendWeeklyEmails")]
  public static async Task SendWeeklyEmails([TimerTrigger("0 0 8 * * 1", RunOnStartup = isDebug)] TimerInfo timer, [BlobInput("config")] BlobContainerClient container)
  {
    var tenantId = Environment.GetEnvironmentVariable("TenantId");
    var clientId = Environment.GetEnvironmentVariable("ClientId");
    var clientSecret = Environment.GetEnvironmentVariable("ClientSecret");
    var postmarkServerToken = Environment.GetEnvironmentVariable("PostmarkServerToken");
    var debugEmail = Environment.GetEnvironmentVariable("DebugEmail");

    var credentials = new ClientSecretCredential(tenantId, clientId, clientSecret);
    var teams = new TeamsClient(credentials);
    var blobs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    await foreach (var blob in container.GetBlobsAsync())
    {
      var download = await container.GetBlobClient(blob.Name).DownloadContentAsync();
      blobs.Add(blob.Name, download.Value.Content.ToString());
    }

    var schoolCodes = blobs.Keys.Select(o => o.Split('-')[0]).Distinct().Select(o => o.ToUpperInvariant()).ToList();

    foreach (var schoolCode in schoolCodes) {
      var school = JsonSerializer.Deserialize<School>(blobs[$"{schoolCode}-settings.json"], new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
      school.TeacherCodesByClass = GetCsv(blobs[$"{schoolCode}-classes.csv"]).Select(o => new ClassWithTeacher(o[0], o[1]))
        .Where(o => !string.IsNullOrWhiteSpace(o.TeacherCode)).GroupBy(o => o)
        .OrderByDescending(o => o.Count()).ThenBy(o => o.Key.TeacherCode).ToLookup(o => o.Key.ClassName, o => o.Key.TeacherCode);
      school.WorkingDays = GetLines(blobs[$"{schoolCode}-days.csv"]).Select(o => DateOnly.ParseExact(o, "yyyy-MM-dd")).ToList();
      school.Departments = GetCsv(blobs[$"{schoolCode}-departments.csv"]).Select(o => new Department(o[0], o[1], [.. o[2].Split(';')])).ToList();
      school.TeachersByCode = GetCsv(blobs[$"{schoolCode}-teachers.csv"]).ToDictionary(o => o[0], o => new Teacher(o[0], o[1], o[2]));

      var missingTeachers = school.TeacherCodesByClass.SelectMany(o => o).Concat(school.Departments.Select(o => o.CurriculumLeader))
        .Concat(school.SeniorTeam).Concat([school.ReplyTo]).Distinct().Where(o => !string.IsNullOrEmpty(o) && !school.TeachersByCode.ContainsKey(o)).ToList();

      if (missingTeachers.Count > 0)
      {
        Console.WriteLine($"{schoolCode} - Skipped: missing teachers {string.Join(", ", missingTeachers)}.");
        continue;
      }
      if (!school.WorkingDays.Contains(Today))
      {
        Console.WriteLine($"{schoolCode} - Skipped: not a working day.");
        continue;
      }
      var days = school.WorkingDays.Where(o => o < Today).OrderDescending().ToList();
      var maxDaysNeeded = Math.Max(school.DefaultDays, school.CustomDays.Max(o => o.Days));
      if (days.Count < maxDaysNeeded)
      {
        Console.WriteLine($"{schoolCode} - Skipped: fewer than {maxDaysNeeded} days available.");
        continue;
      }
      var endDate = Today.AddDays(-1);
      var startDate = days[school.DefaultDays - 1];
      var title = $"Homework due {startDate:d MMM} to {endDate:d MMM}";

      Console.WriteLine($"{schoolCode} - Retrieving classes...");
      var teamsClasses = await teams.ListClassesAsync(school.Id, school.ClassFilter);
      var isSummer = Today.Month is 6 or 7;
      var classes = new List<Class>();

      foreach (var teamsClass in teamsClasses)
      {
        var name = teamsClass.Name[(teamsClass.Name.LastIndexOf(' ') + 1)..].Replace('_', '/');
        if (!byte.TryParse(new string(name.TakeWhile(char.IsDigit).ToArray()), out var year)) continue;
        if (isSummer && year is 11 or 13) continue;
        var teacherCodes = school.TeacherCodesByClass[name].ToList();
        if (teacherCodes.Count == 0) continue;
        var slash = name.IndexOf('/');
        if (slash < 0 || name.Length < slash + 3) continue;
        var subject = name[(slash + 1)..(slash + 3)];
        var departmentName = school.Departments.FirstOrDefault(o => o.Subjects.Contains(subject))?.Name;
        if (departmentName is null) continue;
        var customDays = school.CustomDays.FirstOrDefault(o => o.Year == year && o.Subject == subject)?.Days;
        var hasCustomDays = customDays is not null;
        var cls = new Class(teamsClass.Id, name, year, teacherCodes, departmentName, hasCustomDays ? days[customDays.Value - 1] : startDate, hasCustomDays);
        classes.Add(cls);
      }

      Console.WriteLine($"{schoolCode} - Retrieving homework...");
      await teams.PopulateHomeworkAsync(classes, endDate);

      Console.WriteLine($"{schoolCode} - Sending emails...");
      var mailer = new Mailer(Environment.GetEnvironmentVariable("PostmarkServerToken"), schoolCode, $"{school.Name} <{school.FromEmail}>", school.TeachersByCode[school.ReplyTo].Email, debugEmail);
      var messageGenerator = new MessageGenerator(school.Name, startDate, endDate, classes);

      var departments = classes.OrderBy(o => o.Year).ThenBy(o => o.Name).GroupBy(o => o.DepartmentName)
        .OrderByDescending(d => d.Average(c => c.Homework.Count > 0 ? 1 : 0)).ThenByDescending(d => d.Sum(c => c.Homework.Count > 0 ? 1 : 0));

      foreach (var departmentClasses in departments)
      {
        var department = school.Departments.First(o => o.Name == departmentClasses.Key);
        var curriculumLeaderFirstName = string.IsNullOrEmpty(department.CurriculumLeader) ? null : school.TeachersByCode[department.CurriculumLeader].First;
        var (body, perc) = messageGenerator.GenerateDepartmentEmail(department, curriculumLeaderFirstName, [.. departmentClasses]);
        if (string.IsNullOrEmpty(department.CurriculumLeader)) continue;
        var to = school.TeachersByCode[department.CurriculumLeader].Email;
        mailer.Enqueue(to, $"{department.Name} {title} ({perc}%)", body);
      }

      var (seniorTeamBody, seniorTeamPerc) = messageGenerator.GenerateSeniorTeamEmail();
      var seniorTeamTo = string.Join(',', school.SeniorTeam.Select(o => school.TeachersByCode[o].Email));
      mailer.Enqueue(seniorTeamTo, $"{title} ({seniorTeamPerc}%)", seniorTeamBody);

      foreach (var teacher in school.TeachersByCode.Values)
      {
        var teacherClasses = classes.Where(o => o.TeacherCodes.Contains(teacher.Code)).OrderBy(o => o.Year).ThenBy(o => o.Name).ToList();
        if (teacherClasses.Count == 0) continue;
        var body = messageGenerator.GenerateTeacherEmail(teacher, teacherClasses);
        mailer.Enqueue(teacher.Email, title, body);
      }

      await mailer.SendAsync();
    }
  }

  private static IEnumerable<string> GetLines(string text) => text.TrimEnd().Split('\n').Skip(1).Select(o => o.Trim());
  private static IEnumerable<string[]> GetCsv(string text) => GetLines(text).Select(o => o.Split(','));

  #if DEBUG
    const bool isDebug = true;
    public static DateOnly Today { get; } = new(2024, 6, 9);
  #else
    const bool isDebug = false;
    public static DateOnly Today { get; } = DateOnly.FromDateTime(DateTime.Today);
  #endif
}