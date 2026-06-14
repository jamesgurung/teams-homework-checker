using Azure;
using Microsoft.Extensions.Logging;
using Microsoft.Kiota.Abstractions;
using System.Net;

namespace TeamsHomeworkChecker;

internal static class Resilience
{
  private const int MaxAttempts = 4;
  private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(60);
  private static readonly HashSet<int> TransientStatusCodes = [(int)HttpStatusCode.RequestTimeout, (int)HttpStatusCode.TooManyRequests, 500, (int)HttpStatusCode.BadGateway, (int)HttpStatusCode.ServiceUnavailable, (int)HttpStatusCode.GatewayTimeout];

  public static async Task ExecuteAsync(Func<Task> action, ILogger logger, string operation, Func<Exception, bool> isTransient = null, Func<Exception, TimeSpan> getRetryAfter = null)
  {
    await ExecuteAsync(async () => { await action(); return true; }, logger, operation, isTransient, getRetryAfter);
  }

  public static async Task<T> ExecuteAsync<T>(Func<Task<T>> action, ILogger logger, string operation, Func<Exception, bool> isTransient = null, Func<Exception, TimeSpan> getRetryAfter = null)
  {
    for (var attempt = 1; ; attempt++)
    {
      try
      {
        return await action();
      }
      catch (Exception ex) when (attempt < MaxAttempts && (isTransient ?? IsTransientException)(ex))
      {
        var delay = GetDelay(attempt, getRetryAfter?.Invoke(ex) ?? TimeSpan.Zero);

        logger.LogWarning(ex, "{Operation} failed on attempt {Attempt}/{MaxAttempts}; retrying in {DelayMs}ms.", operation, attempt, MaxAttempts, (int)delay.TotalMilliseconds);
        await Task.Delay(delay);
      }
    }
  }

  public static bool IsTransientStatus(int statusCode) => TransientStatusCodes.Contains(statusCode);

  public static TimeSpan GetDelay(int failedAttempt, TimeSpan retryAfter)
  {
    if (retryAfter > TimeSpan.Zero) return retryAfter;
    var delay = TimeSpan.FromSeconds(Math.Pow(2, failedAttempt - 1)) + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 250));
    return delay > MaxDelay ? MaxDelay : delay;
  }

  public static bool IsTransientException(Exception ex) => ex switch
  {
    RequestFailedException requestFailed => IsTransientStatus(requestFailed.Status),
    ApiException apiException => IsTransientStatus(apiException.ResponseStatusCode),
    HttpRequestException => true,
    TimeoutException => true,
    TaskCanceledException => true,
    _ => false
  };

  public static TimeSpan GetRetryAfter(Exception ex) => ex switch
  {
    ApiException apiException => GetRetryAfter(apiException.ResponseHeaders),
    _ => TimeSpan.Zero
  };

  public static TimeSpan GetRetryAfter(IDictionary<string, IEnumerable<string>> headers)
  {
    if (headers is null || !headers.TryGetValue("Retry-After", out var values)) return TimeSpan.Zero;
    var value = values.FirstOrDefault();
    if (int.TryParse(value, out var seconds)) return TimeSpan.FromSeconds(seconds);
    if (DateTimeOffset.TryParse(value, out var date)) return date - DateTimeOffset.UtcNow;
    return TimeSpan.Zero;
  }
}
