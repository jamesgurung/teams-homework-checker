using PostmarkDotNet;
using Microsoft.Extensions.Logging;

namespace TeamsHomeworkChecker;

public class Mailer(string postmarkServerToken, string schoolCode, string fromEmail, string replyToEmail, string debugEmail, ILogger logger)
{
  private readonly PostmarkClient _client = new(postmarkServerToken);
  private readonly List<PostmarkMessage> _messages = [];
  private int _totalMessages;

  public void Enqueue(string toEmail, string subject, string body)
  {
    if (debugEmail is not null && ++_totalMessages > 3) return;
    if (_messages.Count >= 500) throw new InvalidOperationException("Too many messages queued");
    _messages.Add(new PostmarkMessage
    {
      To = debugEmail ?? toEmail,
      From = fromEmail,
      ReplyTo = replyToEmail,
      Subject = subject,
      HtmlBody = body,
      MessageStream = "outbound",
      Tag = $"{schoolCode} Homework{(debugEmail is not null ? " Test" : string.Empty)}",
      TrackOpens = false,
      TrackLinks = LinkTrackingOptions.None
    });
  }

  public async Task SendAsync()
  {
    if (_messages.Count == 0) return;
    await Resilience.ExecuteAsync(async () =>
    {
      var messages = _messages.ToArray();
      var responses = (await _client.SendMessagesAsync(messages)).ToList();
      var failures = responses.Zip(messages).Where(o => o.First.Status != PostmarkStatus.Success).ToList();
      if (failures.Count == 0) return;

      _messages.Clear();
      _messages.AddRange(failures.Select(o => o.Second));

      var failureSummary = string.Join("; ", failures.Select(o => $"{o.First.To}: {o.First.ErrorCode} {o.First.Message}".Trim()));
      throw new PostmarkSendException($"Postmark failed to send {failures.Count} messages: {failureSummary}", failures.All(o => IsTransientPostmarkFailure(o.First)));
    }, logger, "Send Postmark messages", IsTransientPostmarkException);
    _messages.Clear();
  }

  private static bool IsTransientPostmarkException(Exception ex) => ex switch
  {
    PostmarkSendException postmarkException => postmarkException.IsTransient,
    _ => false
  };

  private static bool IsTransientPostmarkFailure(PostmarkResponse response)
  {
    if (response.ErrorCode == 100) return true;
    if (response.Message is null) return false;
    return response.Message.Contains("maintenance", StringComparison.OrdinalIgnoreCase) ||
      response.Message.Contains("service unavailable", StringComparison.OrdinalIgnoreCase);
  }

  private class PostmarkSendException(string message, bool isTransient) : Exception(message)
  {
    public bool IsTransient { get; } = isTransient;
  }
}
