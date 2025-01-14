using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Azure.Identity;
using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;

namespace TeamsHomeworkChecker;

public class Functions
{
  [Function("SendWeeklyEmails")]
  [SuppressMessage("Style", "IDE0060:Remove unused parameter")]
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
    var jsonCamelCase = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    var endDate = Today.AddDays(-1);

    foreach (var schoolCode in schoolCodes) {
      var school = JsonSerializer.Deserialize<School>(blobs[$"{schoolCode}-settings.json"], jsonCamelCase);
      school.TeacherCodesByClass = GetCsv(blobs[$"{schoolCode}-classes.csv"]).Select(o => new ClassWithTeacher(o[0], o[1]))
        .Where(o => !string.IsNullOrWhiteSpace(o.TeacherCode)).GroupBy(o => o)
        .OrderByDescending(o => o.Count()).ThenBy(o => o.Key.TeacherCode).ToLookup(o => o.Key.ClassName, o => o.Key.TeacherCode);
      school.WorkingDays = [.. GetLines(blobs[$"{schoolCode}-days.csv"]).Select(o => DateOnly.ParseExact(o, "yyyy-MM-dd"))];
      school.Departments = [.. GetCsv(blobs[$"{schoolCode}-departments.csv"]).Select(o => new Department(o[0], o[1], [.. o[2].Split(';')]))];
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

      var pastMondays = school.WorkingDays.Where(o => o < Today).Select(o => o.AddDays(-((int)o.DayOfWeek + 6) % 7)).Distinct().OrderDescending().ToList();
      var weeksNeeded = Math.Max(school.DefaultWeeks, school.CustomWeeks.Max(o => o.Weeks));
      if (pastMondays.Count < weeksNeeded)
      {
        Console.WriteLine($"{schoolCode} - Skipped: fewer than {weeksNeeded} weeks available.");
        continue;
      }

      Console.WriteLine($"{schoolCode} - Retrieving classes...");
      var teamsClasses = await teams.ListClassesAsync(school.ClassFilter);
      var isSummer = Today.Month is 6 or 7;
      var classes = new List<Class>();

      foreach (var teamsClass in teamsClasses)
      {
        var name = teamsClass.Name[(teamsClass.Name.LastIndexOf(' ') + 1)..].Replace('_', '/');
        if (!byte.TryParse(new string([.. name.TakeWhile(char.IsDigit)]), out var year)) continue;
        if (isSummer && year is 11 or 13) continue;
        var teacherCodes = school.TeacherCodesByClass[name].ToList();
        if (teacherCodes.Count == 0) continue;
        var slash = name.IndexOf('/');
        if (slash < 0 || name.Length < slash + 3) continue;
        var subject = name[(slash + 1)..(slash + 3)];
        var departmentName = school.Departments.FirstOrDefault(o => o.Subjects.Contains(subject))?.Name;
        if (departmentName is null) continue;
        var classWeeks = school.CustomWeeks.FirstOrDefault(o => o.Year == year && o.Subject == subject)?.Weeks ?? school.DefaultWeeks;
        if (classWeeks == 0) continue;
        var excludeText = school.Excludes.FirstOrDefault(o => o.Year == year && o.Subject == subject)?.Content;
        var cls = new Class(teamsClass.Id, name, year, teacherCodes, departmentName, classWeeks, excludeText);
        classes.Add(cls);
      }

      Console.WriteLine($"{schoolCode} - Retrieving homework...");
      await teams.PopulateHomeworkAsync(classes, endDate);

      foreach (var cls in classes)
      {
        var oldIndex = cls.Weeks - 1;
        cls.StartDate = pastMondays[oldIndex];
        cls.CurrentHomework = [.. cls.Homework.Where(o => o.DueDate >= cls.StartDate)];
        cls.HasCurrentHomework = cls.CurrentHomework.Count > 0;
        cls.Streak = 1;
        var index = oldIndex + cls.Weeks;
        while (index < pastMondays.Count && cls.Homework.Any(o => o.DueDate >= pastMondays[index] && o.DueDate < pastMondays[oldIndex]) == cls.HasCurrentHomework)
        {
          cls.Streak++;
          oldIndex = index;
          index += cls.Weeks;
        }
      }

      Console.WriteLine($"{schoolCode} - Sending emails...");
      var mailer = new Mailer(Environment.GetEnvironmentVariable("PostmarkServerToken"), schoolCode, $"{school.Name} <{school.FromEmail}>", school.TeachersByCode[school.ReplyTo].Email, debugEmail);
      var messageGenerator = new MessageGenerator(school.Name, endDate, school.DefaultWeeks, classes);

      var departments = classes.OrderBy(o => o.Year).ThenBy(o => o.Name).GroupBy(o => o.DepartmentName)
        .OrderByDescending(d => d.Average(c => c.HasCurrentHomework ? 1 : 0))
        .ThenByDescending(d => d.Sum(c => c.HasCurrentHomework ? c.Streak : -c.Streak))
        .ThenByDescending(d => d.Sum(c => c.HasCurrentHomework ? 1 : 0));

      foreach (var departmentClasses in departments)
      {
        var department = school.Departments.First(o => o.Name == departmentClasses.Key);
        var curriculumLeaderFirstName = string.IsNullOrEmpty(department.CurriculumLeader) ? null : school.TeachersByCode[department.CurriculumLeader].First;
        var (body, perc) = messageGenerator.GenerateDepartmentEmail(department, curriculumLeaderFirstName, [.. departmentClasses]);
        if (string.IsNullOrEmpty(department.CurriculumLeader)) continue;
        var to = school.TeachersByCode[department.CurriculumLeader].Email;
        mailer.Enqueue(to, $"{department.Name} Homework Tracker ({perc}%)", body);
      }

      var (seniorTeamBody, seniorTeamPerc) = messageGenerator.GenerateSeniorTeamEmail();
      var seniorTeamTo = string.Join(',', school.SeniorTeam.Select(o => school.TeachersByCode[o].Email));
      mailer.Enqueue(seniorTeamTo, $"Weekly Homework Tracker ({seniorTeamPerc}%)", seniorTeamBody);

      foreach (var teacher in school.TeachersByCode.Values)
      {
        var teacherClasses = classes.Where(o => o.TeacherCodes.Contains(teacher.Code)).OrderBy(o => o.Year).ThenBy(o => o.Name).ToList();
        if (teacherClasses.Count == 0) continue;
        var body = messageGenerator.GenerateTeacherEmail(teacher, teacherClasses);
        mailer.Enqueue(teacher.Email, "Your homework set", body);
      }

      await mailer.SendAsync();
    }
  }

  private static IEnumerable<string> GetLines(string text) => text.TrimEnd().Split('\n').Skip(1).Select(o => o.Trim());
  private static IEnumerable<string[]> GetCsv(string text) => GetLines(text).Select(o => o.Split(','));

  #if DEBUG
    const bool isDebug = true;
    public static DateOnly Today { get; } = new(2025, 1, 13);
  #else
    const bool isDebug = false;
    public static DateOnly Today { get; } = DateOnly.FromDateTime(DateTime.Today);
  #endif
}