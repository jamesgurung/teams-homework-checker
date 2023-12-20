using PostmarkDotNet;

namespace TeamsHomeworkChecker;

public class Mailer(string postmarkServerToken, string schoolCode, string fromEmail, string replyToEmail, string debugEmail)
{
  private readonly PostmarkClient _client = new(postmarkServerToken);
  private readonly List<PostmarkMessage> _messages = [];
  private int _totalMessages;

  public void Enqueue(string toEmail, string subject, string body)
  {
    if (debugEmail is not null && ++_totalMessages > 2) return;
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
    await _client.SendMessagesAsync(_messages);
    _messages.Clear();
  }
}