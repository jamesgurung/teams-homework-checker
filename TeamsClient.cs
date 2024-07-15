using Azure.Identity;
using Microsoft.Graph;
using Microsoft.Graph.Beta;
using Microsoft.Graph.Beta.Models;
using Microsoft.Kiota.Abstractions.Serialization;
using System.Net;
using System.Text.RegularExpressions;

namespace TeamsHomeworkChecker;

public partial class TeamsClient(ClientSecretCredential credential)
{
  private readonly GraphServiceClient _client = new(credential);

  public async Task<List<TeamsClass>> ListClassesAsync(string schoolId, string classFilter) {
    var response = await _client.Education.Schools[schoolId].Classes.GetAsync(config => {
      config.QueryParameters.Filter = classFilter;
      config.QueryParameters.Select = ["id", "externalName"];
      config.QueryParameters.Top = 999;
    });
    var classes = await IterateAsync<EducationClass, EducationClassCollectionResponse>(response);
    return classes.Select(o => new TeamsClass(o.Id, o.ExternalName)).ToList();
  }

  public async Task PopulateHomeworkAsync(IEnumerable<Class> classes, DateOnly endDate)
  {
    var homework = new List<Homework>();

    foreach (var batch in classes.Chunk(20))
    {
      var batchContent = new BatchRequestContentCollection(_client);
      var requestIds = new Dictionary<Class, string>();

      foreach (var cls in batch)
      {
        var request = _client.Education.Classes[cls.Id].Assignments.ToGetRequestInformation(config =>
        {
          config.QueryParameters.Filter = $"status eq 'assigned' and dueDateTime le {endDate:yyyy-MM-dd}T23:59:59Z";
          config.QueryParameters.Select = ["displayName", "instructions", "dueDateTime"];
          config.QueryParameters.Orderby = ["dueDateTime desc"];
          config.QueryParameters.Top = 999;
        });
        requestIds.Add(cls, await batchContent.AddBatchRequestStepAsync(request));
      }

      var response = await _client.Batch.PostAsync(batchContent);
      var throttledRequestIds = (await response.GetResponsesStatusCodesAsync())
        .Where(o => o.Value == HttpStatusCode.TooManyRequests || o.Value == HttpStatusCode.ServiceUnavailable).Select(o => o.Key).ToList();
      if (throttledRequestIds.Count > 0) {
        var delay = 0;
        foreach (var id in throttledRequestIds) {
          using var throttledResponse = await response.GetResponseByIdAsync(id);
          delay = Math.Max(delay, (int)throttledResponse.Headers.RetryAfter.Delta.Value.TotalMilliseconds);
        }
        Console.WriteLine($"Throttled, waiting {delay}ms...");
        await Task.Delay(delay);
        Console.WriteLine($"Resuming...");
        response = await _client.Batch.PostAsync(batchContent);
      }

      foreach (var (cls, requestId) in requestIds)
      {
        var assignmentsResponse = await response.GetResponseByIdAsync<EducationAssignmentCollectionResponse>(requestId);
        var assignments = assignmentsResponse?.Value;
        if (assignments is null) continue;
        foreach (var assignment in assignments)
        {
          var bodyTag = assignment.Instructions.Content.IndexOf("<body>", StringComparison.OrdinalIgnoreCase);
          if (bodyTag > 0) assignment.Instructions.Content = assignment.Instructions.Content[bodyTag..];
          var instructions = HtmlTagRegex().Replace(assignment.Instructions.Content, " ");
          instructions = MultipleWhiteSpaceRegex().Replace(instructions, " ").Trim();
          if (cls.ExcludeText is not null && instructions.Contains(cls.ExcludeText, StringComparison.OrdinalIgnoreCase)) continue;
          if (instructions.Length > 200) instructions = instructions[..197].Trim() + "...";
          cls.Homework.Add(new(assignment.DisplayName.Trim(), instructions, DateOnly.FromDateTime(assignment.DueDateTime.Value.Date)));
        }
      }
    }
  }

  public async Task ListSchoolsAsync() {
    var response = await _client.Education.Schools.GetAsync(config => {
      config.QueryParameters.Select = ["id", "displayName"];
      config.QueryParameters.Top = 999;
    });
    var schools = await IterateAsync<EducationSchool, EducationSchoolCollectionResponse>(response);
    foreach (var school in schools) {
      Console.WriteLine($"{school.DisplayName} - {school.Id}");
    }
  }

  private async Task<List<TEntity>> IterateAsync<TEntity, TCollectionPage>(TCollectionPage response) where TCollectionPage : IParsable, IAdditionalDataHolder, new()
  {
    var items = new List<TEntity>();
    var iterator = PageIterator<TEntity, TCollectionPage>.CreatePageIterator(_client, response, o => { items.Add(o); return true; });
    await iterator.IterateAsync();
    return items;
  }

  [GeneratedRegex("<.*?>")]
  private static partial Regex HtmlTagRegex();

  [GeneratedRegex("\\s+")]
  private static partial Regex MultipleWhiteSpaceRegex();
}
