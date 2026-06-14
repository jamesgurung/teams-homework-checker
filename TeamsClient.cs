using Azure.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Graph.Beta;
using Microsoft.Graph.Beta.Models;
using Microsoft.Kiota.Abstractions.Serialization;
using System.Net;
using System.Text.RegularExpressions;

namespace TeamsHomeworkChecker;

public partial class TeamsClient(ClientSecretCredential credential, ILogger logger)
{
  private const int MaxBatchAttempts = 4;
  private readonly GraphServiceClient _client = new(credential);

  public async Task<List<TeamsClass>> ListClassesAsync(string classFilter) {
    var response = await Resilience.ExecuteAsync(() => _client.Education.Classes.GetAsync(config => {
      config.QueryParameters.Filter = $"startswith(externalId,'{classFilter}')";
      config.QueryParameters.Select = ["id", "externalId"];
      config.QueryParameters.Top = 999;
    }), logger, "List Teams classes", Resilience.IsTransientException, Resilience.GetRetryAfter);
    var classes = await IterateAsync<EducationClass, EducationClassCollectionResponse>(response, "List Teams classes pages");
    return [.. classes.Select(o => new TeamsClass(o.Id, o.ExternalId))];
  }

  public async Task PopulateHomeworkAsync(IEnumerable<Class> classes, DateOnly endDate)
  {
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

      var response = await Resilience.ExecuteAsync(() => _client.Batch.PostAsync(batchContent), logger, "Retrieve homework batch", Resilience.IsTransientException, Resilience.GetRetryAfter);
      for (var attempt = 1; ; attempt++)
      {
        var transientRequestIds = (await response.GetResponsesStatusCodesAsync())
          .Where(o => Resilience.IsTransientStatus((int)o.Value)).Select(o => o.Key).ToList();
        if (transientRequestIds.Count == 0) break;
        if (attempt == MaxBatchAttempts)
          throw new HttpRequestException($"Graph batch contained transient responses after {MaxBatchAttempts} attempts: {string.Join(", ", transientRequestIds)}");

        var retryAfter = TimeSpan.Zero;
        foreach (var id in transientRequestIds)
        {
          using var transientResponse = await response.GetResponseByIdAsync(id);
          var delay = transientResponse.Headers?.RetryAfter?.Delta ?? transientResponse.Headers?.RetryAfter?.Date - DateTimeOffset.UtcNow ?? TimeSpan.Zero;
          if (delay > retryAfter) retryAfter = delay;
        }

        var retryDelay = Resilience.GetDelay(attempt, retryAfter);
        logger.LogWarning("Retrieve homework batch had {TransientResponses} transient responses on attempt {Attempt}/{MaxAttempts}; retrying in {DelayMs}ms.", transientRequestIds.Count, attempt, MaxBatchAttempts, (int)retryDelay.TotalMilliseconds);
        await Task.Delay(retryDelay);
        response = await Resilience.ExecuteAsync(() => _client.Batch.PostAsync(batchContent), logger, "Retrieve homework batch", Resilience.IsTransientException, Resilience.GetRetryAfter);
      }

      foreach (var (cls, requestId) in requestIds)
      {
        var assignmentsResponse = await response.GetResponseByIdAsync<EducationAssignmentCollectionResponse>(requestId);
        var assignments = assignmentsResponse?.Value;
        if (assignments is null) continue;
        foreach (var assignment in assignments)
        {
          if (!assignment.DueDateTime.HasValue) continue;
          var content = assignment.Instructions?.Content ?? string.Empty;
          var bodyTag = content.IndexOf("<body>", StringComparison.OrdinalIgnoreCase);
          if (bodyTag > 0) content = content[bodyTag..];
          var instructions = HtmlTagRegex().Replace(content, " ");
          instructions = MultipleWhiteSpaceRegex().Replace(instructions, " ").Trim();
          if (cls.ExcludeText is not null && instructions.Contains(cls.ExcludeText, StringComparison.OrdinalIgnoreCase)) continue;
          if (instructions.Length > 200) instructions = instructions[..197].Trim() + "...";
          var title = string.IsNullOrWhiteSpace(assignment.DisplayName) ? "Untitled assignment" : assignment.DisplayName.Trim();
          cls.Homework.Add(new(title, instructions, DateOnly.FromDateTime(assignment.DueDateTime.Value.Date)));
        }
      }
    }
  }

  public async Task ListSchoolsAsync() {
    var response = await Resilience.ExecuteAsync(() => _client.Education.Schools.GetAsync(config => {
      config.QueryParameters.Select = ["id", "displayName"];
      config.QueryParameters.Top = 999;
    }), logger, "List Teams schools", Resilience.IsTransientException, Resilience.GetRetryAfter);
    var schools = await IterateAsync<EducationSchool, EducationSchoolCollectionResponse>(response, "List Teams schools pages");
    foreach (var school in schools) {
      Console.WriteLine($"{school.DisplayName} - {school.Id}");
    }
  }

  private async Task<List<TEntity>> IterateAsync<TEntity, TCollectionPage>(TCollectionPage response, string operation) where TCollectionPage : IParsable, IAdditionalDataHolder, new()
  {
    var items = new List<TEntity>();
    var iterator = PageIterator<TEntity, TCollectionPage>.CreatePageIterator(_client, response, o => { items.Add(o); return true; });
    await Resilience.ExecuteAsync(() => iterator.IterateAsync(), logger, operation, Resilience.IsTransientException, Resilience.GetRetryAfter);
    return items;
  }

  [GeneratedRegex("<.*?>")]
  private static partial Regex HtmlTagRegex();

  [GeneratedRegex("\\s+")]
  private static partial Regex MultipleWhiteSpaceRegex();
}
