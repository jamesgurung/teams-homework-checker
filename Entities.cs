namespace TeamsHomeworkChecker;

public class School
{
  public string Name { get; init; }
  public string Id { get; init; }
  public string FromEmail { get; init; }
  public List<string> SeniorTeam { get; init; }
  public string ReplyTo { get; init; }
  public string ClassFilter { get; init; }
  public byte DefaultWeeks { get; init; }
  public List<CustomWeek> CustomWeeks { get; init; }
  public List<Exclude> Excludes { get; init; }

  public ILookup<string, string> TeacherCodesByClass { get; set; }
  public List<DateOnly> WorkingDays { get; set; }
  public List<Department> Departments { get; set; }
  public Dictionary<string, Teacher> TeachersByCode { get; set; }
}

public class CustomWeek
{
  public byte Year { get; init; }
  public string Subject { get; init; }
  public byte Weeks { get; init; }
}

public class Exclude
{
  public byte Year { get; init; }
  public string Subject { get; init; }
  public string Content { get; init; }
}

public class Department(string name, string curriculumLeader, List<string> subjects)
{
  public string Name { get; } = name;
  public string CurriculumLeader { get; } = curriculumLeader;
  public List<string> Subjects { get; } = subjects;
}

public class Teacher(string code, string first, string email)
{
  public string Code { get; } = code;
  public string First { get; } = first;
  public string Email { get; } = email;
}

public class Class
{
  public Class() { }

  public Class(string id, string name, byte year, List<string> teacherCodes, string departmentName, byte weeks, bool hasCustomWeeks, string excludeText) {
    Id = id;
    Name = name;
    Year = year;
    TeacherCodes = teacherCodes;
    DepartmentName = departmentName;
    Weeks = weeks;
    HasCustomWeeks = hasCustomWeeks;
    ExcludeText = excludeText;
    Homework = [];
  }

  public string Id { get; init; }
  public string Name { get; init; }
  public byte Year { get; init; }
  public List<string> TeacherCodes { get; init; }
  public string DepartmentName { get; init; }
  public List<Homework> Homework { get; init; }
  public byte Weeks { get; init; }
  public bool HasCustomWeeks { get; init; }
  public string ExcludeText { get; set; }

  public List<Homework> CurrentHomework { get; set; }
  public bool HasCurrentHomework { get; set; }
  public byte Streak { get; set; }
}

public record TeamsClass(string Id, string Name);

public record Homework(string Title, string Instructions, DateOnly DueDate);

public record ClassWithTeacher(string ClassName, string TeacherCode);