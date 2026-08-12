using System.Globalization;
using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Tessera.Providers;

namespace Tessera.Plugins.Gmail;

public sealed record GmailAccountIdentity(string EmailAddress, long MessagesTotal, long ThreadsTotal, string HistoryId);
public sealed record GmailMessageMetadata(string Id, string ThreadId, IReadOnlyList<string> LabelIds, DateTimeOffset? InternalDate, string? From, string? To, string? Subject, string? Date);
public sealed record GmailIdentityResult(bool Succeeded, GmailAccountIdentity? Identity, string? ErrorCode = null);
public sealed record GmailSearchResult(bool Succeeded, IReadOnlyList<GmailMessageMetadata> Messages, string? NextPageToken, string? ErrorCode = null);
public sealed record GmailAttachmentMetadata(string Filename, string MimeType, long Size);
public sealed record GmailMessageContent(GmailMessageMetadata Metadata, string PlainText, bool Truncated, IReadOnlyList<GmailAttachmentMetadata> Attachments);
public sealed record GmailMessageResult(bool Succeeded, GmailMessageContent? Message, string? ErrorCode = null);
public sealed record GmailThreadResult(bool Succeeded, string? ThreadId, IReadOnlyList<GmailMessageContent> Messages, string? ErrorCode = null);
public sealed record GmailLabelMetadata(string Id, string Name, string Type, long? MessagesTotal, long? MessagesUnread, long? ThreadsTotal, long? ThreadsUnread);
public sealed record GmailLabelsResult(bool Succeeded, IReadOnlyList<GmailLabelMetadata> Labels, string? ErrorCode = null);
public sealed record GmailMailEnvelope(string From, IReadOnlyList<string> To, IReadOnlyList<string> Cc, IReadOnlyList<string> Bcc, string Subject, string PlainText);
public sealed record GmailMutationResult(bool Succeeded, bool UnknownOutcome, string? ProviderId, string? MessageId, string? ThreadId, string? ErrorCode = null);
public sealed record GmailHistoryResult(bool Succeeded, bool CursorExpired, string? HistoryId, IReadOnlyList<GmailMessageMetadata> Messages, string? ErrorCode = null);

public sealed partial class GmailRestAdapter(IHttpTransport transport, int maximumResponseBytes = 256 * 1024)
{
    private const string Origin = "https://gmail.googleapis.com/gmail/v1/users/me/";
    private const int MaximumMessages = 25;
    private const int MaximumThreadMessages = 20;
    private const int MaximumHeaderCharacters = 2_048;
    private const int MaximumBodyCharacters = 64 * 1024;

    public async Task<GmailIdentityResult> ValidateAsync(string accessToken, CancellationToken cancellationToken = default)
    {
        var response = await SendAsync("profile", accessToken, cancellationToken).ConfigureAwait(false);
        if (!response.Succeeded) return new(false, null, response.ErrorCode);
        try
        {
            using var document = JsonDocument.Parse(response.Body!);
            var root = document.RootElement;
            return new(true, new(RequiredText(root, "emailAddress", 320), RequiredNonNegativeInt64(root, "messagesTotal"), RequiredNonNegativeInt64(root, "threadsTotal"), RequiredText(root, "historyId", 128)));
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or InvalidOperationException or FormatException)
        {
            return new(false, null, "provider_malformed");
        }
    }

    public async Task<GmailSearchResult> SearchMessagesAsync(string accessToken, string? query, int maximumMessages = MaximumMessages, CancellationToken cancellationToken = default)
    {
        if (maximumMessages is < 1 or > MaximumMessages) throw new ArgumentOutOfRangeException(nameof(maximumMessages));
        if (query is { Length: > 1_024 } || query?.Any(char.IsControl) == true) throw new ArgumentException("Gmail search query is invalid.", nameof(query));
        var path = $"messages?maxResults={maximumMessages}&includeSpamTrash=false";
        if (!string.IsNullOrWhiteSpace(query)) path += $"&q={Uri.EscapeDataString(query.Trim())}";
        var response = await SendAsync(path, accessToken, cancellationToken).ConfigureAwait(false);
        if (!response.Succeeded) return new(false, [], null, response.ErrorCode);
        try
        {
            using var document = JsonDocument.Parse(response.Body!);
            var root = document.RootElement;
            var references = new List<(string Id, string ThreadId)>();
            if (root.TryGetProperty("messages", out var messages))
            {
                if (messages.ValueKind != JsonValueKind.Array || messages.GetArrayLength() > maximumMessages) throw new InvalidDataException("Gmail message list exceeds the requested bound.");
                foreach (var item in messages.EnumerateArray()) references.Add((RequiredProviderId(item, "id"), RequiredProviderId(item, "threadId")));
            }
            var nextPageToken = OptionalText(root, "nextPageToken", 512);
            var values = new List<GmailMessageMetadata>(references.Count);
            foreach (var reference in references)
            {
                var detail = await SendAsync($"messages/{Uri.EscapeDataString(reference.Id)}?format=metadata&metadataHeaders=From&metadataHeaders=To&metadataHeaders=Subject&metadataHeaders=Date", accessToken, cancellationToken).ConfigureAwait(false);
                if (!detail.Succeeded) return new(false, [], nextPageToken, detail.ErrorCode);
                values.Add(ParseMetadata(detail.Body!, reference));
            }
            return new(true, values.AsReadOnly(), nextPageToken);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or InvalidOperationException or FormatException)
        {
            return new(false, [], null, "provider_malformed");
        }
    }

    public async Task<GmailMessageResult> GetMessageAsync(string accessToken, string messageId, CancellationToken cancellationToken = default)
    {
        if (!ProviderId().IsMatch(messageId)) throw new ArgumentException("Gmail message ID is invalid.", nameof(messageId));
        var response = await SendAsync($"messages/{Uri.EscapeDataString(messageId)}?format=full", accessToken, cancellationToken).ConfigureAwait(false);
        if (!response.Succeeded) return new(false, null, response.ErrorCode);
        try
        {
            using var document = JsonDocument.Parse(response.Body!);
            var root = document.RootElement;
            return new(true, ParseContent(root, (messageId, RequiredProviderId(root, "threadId"))));
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or InvalidOperationException or FormatException or DecoderFallbackException)
        {
            return new(false, null, "provider_malformed");
        }
    }

    public async Task<GmailThreadResult> GetThreadAsync(string accessToken, string threadId, CancellationToken cancellationToken = default)
    {
        if (!ProviderId().IsMatch(threadId)) throw new ArgumentException("Gmail thread ID is invalid.", nameof(threadId));
        var response = await SendAsync($"threads/{Uri.EscapeDataString(threadId)}?format=full", accessToken, cancellationToken).ConfigureAwait(false);
        if (!response.Succeeded) return new(false, null, [], response.ErrorCode);
        try
        {
            using var document = JsonDocument.Parse(response.Body!);
            var root = document.RootElement;
            if (RequiredProviderId(root, "id") != threadId) throw new InvalidDataException("Gmail thread identity changed.");
            if (!root.TryGetProperty("messages", out var messages) || messages.ValueKind != JsonValueKind.Array || messages.GetArrayLength() > MaximumThreadMessages) throw new InvalidDataException("Gmail thread messages are malformed or exceed the bound.");
            var values = new List<GmailMessageContent>(messages.GetArrayLength());
            foreach (var message in messages.EnumerateArray()) values.Add(ParseContent(message, (RequiredProviderId(message, "id"), threadId)));
            return new(true, threadId, values.AsReadOnly());
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or InvalidOperationException or FormatException or DecoderFallbackException)
        {
            return new(false, null, [], "provider_malformed");
        }
    }

    public async Task<GmailLabelsResult> ListLabelsAsync(string accessToken, CancellationToken cancellationToken = default)
    {
        var response = await SendAsync("labels", accessToken, cancellationToken).ConfigureAwait(false);
        if (!response.Succeeded) return new(false, [], response.ErrorCode);
        try
        {
            using var document = JsonDocument.Parse(response.Body!);
            if (!document.RootElement.TryGetProperty("labels", out var labels) || labels.ValueKind != JsonValueKind.Array || labels.GetArrayLength() > 500) throw new InvalidDataException("Gmail labels are malformed or exceed the bound.");
            return new(true, labels.EnumerateArray().Select(label => new GmailLabelMetadata(
                RequiredProviderId(label, "id"), RequiredText(label, "name", 256), RequiredText(label, "type", 32),
                OptionalNonNegativeInt64(label, "messagesTotal"), OptionalNonNegativeInt64(label, "messagesUnread"),
                OptionalNonNegativeInt64(label, "threadsTotal"), OptionalNonNegativeInt64(label, "threadsUnread"))).ToArray());
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or InvalidOperationException or FormatException)
        {
            return new(false, [], "provider_malformed");
        }
    }

    public async Task<GmailHistoryResult> GetHistoryAsync(string accessToken, string startHistoryId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(startHistoryId) || startHistoryId.Length > 128 || !startHistoryId.All(char.IsAsciiDigit)) throw new ArgumentException("Gmail history ID is invalid.", nameof(startHistoryId));
        var references = new Dictionary<string, string>(StringComparer.Ordinal);
        string? pageToken = null;
        string? latestHistoryId = null;
        for (var page = 0; page < 5; page++)
        {
            var path = $"history?startHistoryId={Uri.EscapeDataString(startHistoryId)}&historyTypes=messageAdded&maxResults=100";
            if (pageToken is not null) path += $"&pageToken={Uri.EscapeDataString(pageToken)}";
            var response = await SendAsync(path, accessToken, cancellationToken).ConfigureAwait(false);
            if (!response.Succeeded) return response.ErrorCode == "provider_not_found" ? new(false, true, null, [], "history_cursor_expired") : new(false, false, null, [], response.ErrorCode);
            try
            {
                using var document = JsonDocument.Parse(response.Body!);
                var root = document.RootElement;
                latestHistoryId = RequiredText(root, "historyId", 128);
                if (root.TryGetProperty("history", out var history))
                {
                    if (history.ValueKind != JsonValueKind.Array || history.GetArrayLength() > 100) throw new InvalidDataException("Gmail history page exceeds the bound.");
                    foreach (var entry in history.EnumerateArray())
                    {
                        if (!entry.TryGetProperty("messagesAdded", out var added)) continue;
                        if (added.ValueKind != JsonValueKind.Array || added.GetArrayLength() > 100) throw new InvalidDataException("Gmail history additions exceed the bound.");
                        foreach (var item in added.EnumerateArray())
                        {
                            if (!item.TryGetProperty("message", out var message)) throw new InvalidDataException("Gmail history message is missing.");
                            references[RequiredProviderId(message, "id")] = RequiredProviderId(message, "threadId");
                            if (references.Count > 500) throw new InvalidDataException("Gmail history change count exceeds the bound.");
                        }
                    }
                }
                pageToken = OptionalText(root, "nextPageToken", 512);
                if (pageToken is null) break;
                if (page == 4) return new(false, true, null, [], "history_page_limit");
            }
            catch (Exception exception) when (exception is JsonException or InvalidDataException or InvalidOperationException or FormatException)
            {
                return new(false, false, null, [], "provider_malformed");
            }
        }
        var messages = new List<GmailMessageMetadata>(references.Count);
        foreach (var reference in references)
        {
            var detail = await SendAsync($"messages/{Uri.EscapeDataString(reference.Key)}?format=metadata&metadataHeaders=From&metadataHeaders=To&metadataHeaders=Subject&metadataHeaders=Date", accessToken, cancellationToken).ConfigureAwait(false);
            if (!detail.Succeeded) return new(false, false, null, [], detail.ErrorCode);
            try { messages.Add(ParseMetadata(detail.Body!, (reference.Key, reference.Value))); }
            catch (Exception exception) when (exception is JsonException or InvalidDataException or InvalidOperationException or FormatException) { return new(false, false, null, [], "provider_malformed"); }
        }
        return new(true, false, latestHistoryId ?? startHistoryId, messages.AsReadOnly());
    }

    public async Task<GmailMutationResult> CreateDraftAsync(string accessToken, GmailMailEnvelope envelope, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        var messageId = DeterministicMessageId(idempotencyKey);
        var response = await MutateAsync("POST", "drafts", accessToken, JsonSerializer.Serialize(new { message = new { raw = BuildRawMessage(envelope, messageId) } }), cancellationToken).ConfigureAwait(false);
        if (response.Succeeded) return await ParseAndVerifyDraftAsync(response.Body!, accessToken, messageId, cancellationToken).ConfigureAwait(false);
        if (response.UnknownOutcome && await FindDraftAsync(accessToken, messageId, cancellationToken).ConfigureAwait(false) is { } reconciled) return reconciled;
        return new(false, response.UnknownOutcome, null, null, null, response.ErrorCode);
    }

    public async Task<GmailMutationResult> UpdateDraftAsync(string accessToken, string draftId, GmailMailEnvelope envelope, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        if (!ProviderId().IsMatch(draftId)) throw new ArgumentException("Gmail draft ID is invalid.", nameof(draftId));
        var messageId = DeterministicMessageId(idempotencyKey);
        var response = await MutateAsync("PUT", $"drafts/{Uri.EscapeDataString(draftId)}", accessToken, JsonSerializer.Serialize(new { message = new { raw = BuildRawMessage(envelope, messageId) } }), cancellationToken).ConfigureAwait(false);
        if (response.Succeeded) return await ParseAndVerifyDraftAsync(response.Body!, accessToken, messageId, cancellationToken).ConfigureAwait(false);
        if (response.UnknownOutcome && await VerifyDraftAsync(accessToken, draftId, messageId, cancellationToken).ConfigureAwait(false) is { } reconciled) return reconciled;
        return new(false, response.UnknownOutcome, draftId, null, null, response.ErrorCode);
    }

    public async Task<GmailMutationResult> SendMessageAsync(string accessToken, GmailMailEnvelope envelope, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        var rfcMessageId = DeterministicMessageId(idempotencyKey);
        var response = await MutateAsync("POST", "messages/send", accessToken, JsonSerializer.Serialize(new { raw = BuildRawMessage(envelope, rfcMessageId) }), cancellationToken).ConfigureAwait(false);
        if (response.Succeeded)
        {
            try
            {
                using var document = JsonDocument.Parse(response.Body!);
                var id = RequiredProviderId(document.RootElement, "id");
                var threadId = RequiredProviderId(document.RootElement, "threadId");
                var verified = await GetMessageAsync(accessToken, id, cancellationToken).ConfigureAwait(false);
                return verified.Succeeded && verified.Message is not null && verified.Message.Metadata.LabelIds.Contains("SENT", StringComparer.Ordinal)
                    ? new(true, false, id, id, threadId)
                    : new(false, true, id, id, threadId, verified.ErrorCode ?? "verification_failed");
            }
            catch (Exception exception) when (exception is JsonException or InvalidDataException or InvalidOperationException) { return new(false, true, null, null, null, "provider_malformed"); }
        }
        if (response.UnknownOutcome)
        {
            var search = await SearchMessagesAsync(accessToken, $"in:sent rfc822msgid:{rfcMessageId}", 2, cancellationToken).ConfigureAwait(false);
            var match = search.Succeeded ? search.Messages.SingleOrDefault(message => message.LabelIds.Contains("SENT", StringComparer.Ordinal)) : null;
            if (match is not null) return new(true, false, match.Id, match.Id, match.ThreadId);
        }
        return new(false, response.UnknownOutcome, null, null, null, response.ErrorCode);
    }

    private async Task<GmailMutationResult> ParseAndVerifyDraftAsync(string json, string accessToken, string expectedMessageId, CancellationToken token)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var draftId = RequiredProviderId(document.RootElement, "id");
            return await VerifyDraftAsync(accessToken, draftId, expectedMessageId, token).ConfigureAwait(false) ?? new(false, true, draftId, null, null, "verification_failed");
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or InvalidOperationException) { return new(false, true, null, null, null, "provider_malformed"); }
    }

    private async Task<GmailMutationResult?> VerifyDraftAsync(string accessToken, string draftId, string expectedRfcMessageId, CancellationToken token)
    {
        var response = await SendAsync($"drafts/{Uri.EscapeDataString(draftId)}?format=full", accessToken, token).ConfigureAwait(false);
        if (!response.Succeeded) return null;
        try
        {
            using var document = JsonDocument.Parse(response.Body!);
            var root = document.RootElement;
            if (RequiredProviderId(root, "id") != draftId || !root.TryGetProperty("message", out var message)) return null;
            var messageId = RequiredProviderId(message, "id");
            var threadId = RequiredProviderId(message, "threadId");
            return HeaderValue(message, "Message-ID") == expectedRfcMessageId ? new(true, false, draftId, messageId, threadId) : null;
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or InvalidOperationException) { return null; }
    }

    private async Task<GmailMutationResult?> FindDraftAsync(string accessToken, string rfcMessageId, CancellationToken token)
    {
        var response = await SendAsync($"drafts?maxResults=2&q={Uri.EscapeDataString($"rfc822msgid:{rfcMessageId}")}", accessToken, token).ConfigureAwait(false);
        if (!response.Succeeded) return null;
        try
        {
            using var document = JsonDocument.Parse(response.Body!);
            if (!document.RootElement.TryGetProperty("drafts", out var drafts) || drafts.ValueKind != JsonValueKind.Array || drafts.GetArrayLength() != 1) return null;
            var draftId = RequiredProviderId(drafts[0], "id");
            return await VerifyDraftAsync(accessToken, draftId, rfcMessageId, token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or InvalidOperationException) { return null; }
    }

    private static string BuildRawMessage(GmailMailEnvelope envelope, string messageId)
    {
        ValidateEnvelope(envelope);
        var builder = new StringBuilder();
        builder.Append("From: ").Append(envelope.From).Append("\r\nTo: ").Append(string.Join(", ", envelope.To));
        if (envelope.Cc.Count > 0) builder.Append("\r\nCc: ").Append(string.Join(", ", envelope.Cc));
        if (envelope.Bcc.Count > 0) builder.Append("\r\nBcc: ").Append(string.Join(", ", envelope.Bcc));
        builder.Append("\r\nSubject: =?UTF-8?B?").Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(envelope.Subject))).Append("?=\r\nMessage-ID: ").Append(messageId).Append("\r\nMIME-Version: 1.0\r\nContent-Type: text/plain; charset=UTF-8\r\nContent-Transfer-Encoding: base64\r\n\r\n").Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(envelope.PlainText), Base64FormattingOptions.InsertLineBreaks));
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(builder.ToString())).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static void ValidateEnvelope(GmailMailEnvelope envelope)
    {
        if (envelope.To.Count == 0 || envelope.To.Count + envelope.Cc.Count + envelope.Bcc.Count > 50 || envelope.Subject.Length > 998 || envelope.PlainText.Length > 256 * 1024 || ContainsHeaderBreak(envelope.Subject) || ContainsHeaderBreak(envelope.From)) throw new ArgumentException("Gmail message envelope is invalid.");
        foreach (var address in envelope.To.Concat(envelope.Cc).Concat(envelope.Bcc).Prepend(envelope.From))
        {
            if (address.Length > 320 || ContainsHeaderBreak(address)) throw new ArgumentException("Gmail address is invalid.");
            try { var parsed = new MailAddress(address); if (!string.Equals(parsed.Address, address, StringComparison.OrdinalIgnoreCase)) throw new FormatException(); }
            catch (FormatException) { throw new ArgumentException("Gmail address is invalid."); }
        }
    }

    private static bool ContainsHeaderBreak(string value) => value.Contains('\r') || value.Contains('\n');
    private static string DeterministicMessageId(string idempotencyKey)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 128) throw new ArgumentException("Gmail idempotency key is invalid.", nameof(idempotencyKey));
        return $"<tessera-{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(idempotencyKey)))}@tessera.invalid>";
    }

    private static string? HeaderValue(JsonElement message, string name)
    {
        if (!message.TryGetProperty("payload", out var payload) || !payload.TryGetProperty("headers", out var headers) || headers.ValueKind != JsonValueKind.Array) return null;
        foreach (var header in headers.EnumerateArray()) if (string.Equals(OptionalText(header, "name", 128), name, StringComparison.OrdinalIgnoreCase)) return OptionalText(header, "value", 2048);
        return null;
    }

    private async Task<RawResult> SendAsync(string path, string accessToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(accessToken)) return new(false, null, "provider_auth_required");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Authorization"] = $"Bearer {accessToken}", ["Accept"] = "application/json" };
        TransportResponse response;
        try { response = await transport.SendAsync("GET", Origin + path, headers, null, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return new(false, null, "provider_timeout"); }
        catch (TransportResponseTooLargeException) { return new(false, null, "provider_result_too_large"); }
        catch (Exception) { return new(false, null, "provider_unavailable"); }
        if (response.Status is 401 or 403) return new(false, null, "provider_auth_required");
        if (response.Status == 404) return new(false, null, "provider_not_found");
        if (response.Status == 429) return new(false, null, "rate_limited");
        if (response.Status is < 200 or >= 300) return new(false, null, "provider_unavailable");
        if (Encoding.UTF8.GetByteCount(response.Body) > maximumResponseBytes) return new(false, null, "provider_result_too_large");
        try { using var _ = JsonDocument.Parse(response.Body); }
        catch (JsonException) { return new(false, null, "provider_malformed"); }
        return new(true, response.Body, null);
    }

    private async Task<MutationRawResult> MutateAsync(string method, string path, string accessToken, string body, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(accessToken)) return new(false, false, null, "provider_auth_required");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Authorization"] = $"Bearer {accessToken}", ["Accept"] = "application/json", ["Content-Type"] = "application/json" };
        TransportResponse response;
        try { response = await transport.SendAsync(method, Origin + path, headers, body, token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return new(false, true, null, "provider_timeout"); }
        catch (TransportResponseTooLargeException) { return new(false, true, null, "provider_result_too_large"); }
        catch (Exception) { return new(false, true, null, "provider_unavailable"); }
        if (response.Status is 401 or 403) return new(false, false, null, "provider_auth_required");
        if (response.Status == 429) return new(false, false, null, "rate_limited");
        if (response.Status >= 500) return new(false, true, null, "provider_unavailable");
        if (response.Status is < 200 or >= 300) return new(false, false, null, "provider_rejected");
        if (Encoding.UTF8.GetByteCount(response.Body) > maximumResponseBytes) return new(false, true, null, "provider_result_too_large");
        try { using var _ = JsonDocument.Parse(response.Body); }
        catch (JsonException) { return new(false, true, null, "provider_malformed"); }
        return new(true, false, response.Body, null);
    }

    private static GmailMessageMetadata ParseMetadata(string json, (string Id, string ThreadId) expected)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var id = RequiredProviderId(root, "id");
        var threadId = RequiredProviderId(root, "threadId");
        if (id != expected.Id || threadId != expected.ThreadId) throw new InvalidDataException("Gmail metadata identity changed between list and get.");
        var labels = new List<string>();
        if (root.TryGetProperty("labelIds", out var labelIds))
        {
            if (labelIds.ValueKind != JsonValueKind.Array || labelIds.GetArrayLength() > 100) throw new InvalidDataException("Gmail labels are malformed.");
            foreach (var label in labelIds.EnumerateArray())
            {
                var value = label.GetString();
                if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || !ProviderId().IsMatch(value)) throw new InvalidDataException("Gmail label is malformed.");
                labels.Add(value);
            }
        }
        DateTimeOffset? internalDate = null;
        if (root.TryGetProperty("internalDate", out var internalDateValue))
        {
            var raw = internalDateValue.GetString();
            if (!long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var milliseconds) || milliseconds < 0) throw new InvalidDataException("Gmail internal date is malformed.");
            internalDate = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
        }
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("payload", out var payload) && payload.TryGetProperty("headers", out var headerValues))
        {
            if (headerValues.ValueKind != JsonValueKind.Array || headerValues.GetArrayLength() > 100) throw new InvalidDataException("Gmail headers are malformed.");
            foreach (var header in headerValues.EnumerateArray())
            {
                var name = RequiredText(header, "name", 128);
                if (name is not ("From" or "To" or "Subject" or "Date")) continue;
                headers[name] = RequiredText(header, "value", MaximumHeaderCharacters);
            }
        }
        return new(id, threadId, labels.AsReadOnly(), internalDate, headers.GetValueOrDefault("From"), headers.GetValueOrDefault("To"), headers.GetValueOrDefault("Subject"), headers.GetValueOrDefault("Date"));
    }

    private GmailMessageContent ParseContent(JsonElement root, (string Id, string ThreadId) expected)
    {
        var metadata = ParseMetadata(root.GetRawText(), expected);
        if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Gmail payload is missing.");
        var plainParts = new List<string>();
        var htmlParts = new List<string>();
        var attachments = new List<GmailAttachmentMetadata>();
        CollectMimeParts(payload, plainParts, htmlParts, attachments, 0);
        var source = plainParts.Count > 0 ? string.Join("\n\n", plainParts) : string.Join("\n\n", htmlParts.Select(SanitizeHtmlToText));
        source = NormalizeText(source);
        var truncated = source.Length > MaximumBodyCharacters;
        if (truncated) source = source[..MaximumBodyCharacters];
        return new(metadata, source, truncated, attachments.AsReadOnly());
    }

    private void CollectMimeParts(JsonElement part, List<string> plainParts, List<string> htmlParts, List<GmailAttachmentMetadata> attachments, int depth)
    {
        if (depth > 12) throw new InvalidDataException("Gmail MIME nesting exceeds the bound.");
        var mimeType = OptionalText(part, "mimeType", 256) ?? "application/octet-stream";
        string? filename = null;
        if (part.TryGetProperty("filename", out var filenameValue))
        {
            if (filenameValue.ValueKind != JsonValueKind.String) throw new InvalidDataException("Gmail filename is malformed.");
            var rawFilename = filenameValue.GetString() ?? string.Empty;
            if (rawFilename.Length > 512 || rawFilename.Any(char.IsControl)) throw new InvalidDataException("Gmail filename is malformed.");
            if (!string.IsNullOrWhiteSpace(rawFilename)) filename = rawFilename.Trim();
        }
        if (part.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.Object)
        {
            var size = OptionalNonNegativeInt64(body, "size") ?? 0;
            var hasAttachmentId = body.TryGetProperty("attachmentId", out var attachmentId) && attachmentId.ValueKind == JsonValueKind.String;
            if (!string.IsNullOrWhiteSpace(filename) || hasAttachmentId)
            {
                if (attachments.Count >= 100) throw new InvalidDataException("Gmail attachment count exceeds the bound.");
                attachments.Add(new(filename ?? "attachment", mimeType, size));
            }
            else if (body.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.String && mimeType is "text/plain" or "text/html")
            {
                var decoded = DecodeBase64UrlText(data.GetString()!);
                if (mimeType == "text/plain") plainParts.Add(decoded); else htmlParts.Add(decoded);
            }
        }
        if (!part.TryGetProperty("parts", out var parts)) return;
        if (parts.ValueKind != JsonValueKind.Array || parts.GetArrayLength() > 100) throw new InvalidDataException("Gmail MIME parts are malformed or exceed the bound.");
        foreach (var child in parts.EnumerateArray()) CollectMimeParts(child, plainParts, htmlParts, attachments, depth + 1);
    }

    private string DecodeBase64UrlText(string value)
    {
        if (value.Length > 4 * maximumResponseBytes / 3 + 8) throw new InvalidDataException("Gmail body exceeds the bound.");
        var normalized = value.Replace('-', '+').Replace('_', '/');
        normalized = normalized.PadRight((normalized.Length + 3) / 4 * 4, '=');
        return new UTF8Encoding(false, true).GetString(Convert.FromBase64String(normalized));
    }

    private static string SanitizeHtmlToText(string value) => WebUtility.HtmlDecode(HtmlTags().Replace(ActiveHtml().Replace(value, " "), " "));
    private static string NormalizeText(string value)
    {
        var builder = new StringBuilder(Math.Min(value.Length, MaximumBodyCharacters + 1));
        foreach (var character in value)
        {
            if (builder.Length > MaximumBodyCharacters) break;
            builder.Append(char.IsControl(character) && character is not ('\r' or '\n' or '\t') ? ' ' : character);
        }
        return Whitespace().Replace(builder.ToString(), " ").Trim();
    }

    private static string RequiredProviderId(JsonElement value, string property)
    {
        var result = RequiredText(value, property, 128);
        if (!ProviderId().IsMatch(result)) throw new InvalidDataException($"Gmail {property} is malformed.");
        return result;
    }

    private static string RequiredText(JsonElement value, string property, int maximumCharacters)
    {
        if (!value.TryGetProperty(property, out var item) || item.ValueKind != JsonValueKind.String) throw new InvalidDataException($"Gmail {property} is missing.");
        var result = item.GetString();
        if (string.IsNullOrWhiteSpace(result) || result.Length > maximumCharacters || result.Any(character => char.IsControl(character) && character is not '\t')) throw new InvalidDataException($"Gmail {property} is malformed.");
        return result.Trim();
    }

    private static string? OptionalText(JsonElement value, string property, int maximumCharacters)
        => !value.TryGetProperty(property, out var item) ? null : item.ValueKind == JsonValueKind.String ? RequiredText(value, property, maximumCharacters) : throw new InvalidDataException($"Gmail {property} is malformed.");

    private static long RequiredNonNegativeInt64(JsonElement value, string property)
    {
        if (!value.TryGetProperty(property, out var item) || item.ValueKind != JsonValueKind.Number || !item.TryGetInt64(out var result) || result < 0) throw new InvalidDataException($"Gmail {property} is malformed.");
        return result;
    }

    private static long? OptionalNonNegativeInt64(JsonElement value, string property)
    {
        if (!value.TryGetProperty(property, out var item)) return null;
        if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt64(out var result) || result < 0) throw new InvalidDataException($"Gmail {property} is malformed.");
        return result;
    }

    private sealed record RawResult(bool Succeeded, string? Body, string? ErrorCode);
    private sealed record MutationRawResult(bool Succeeded, bool UnknownOutcome, string? Body, string? ErrorCode);

    [GeneratedRegex("^[A-Za-z0-9_-]{1,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex ProviderId();
    [GeneratedRegex("<(script|style|noscript|iframe|object|embed)\\b[^>]*>.*?</\\1\\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex ActiveHtml();
    [GeneratedRegex("<[^>]+>", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex HtmlTags();
    [GeneratedRegex("[ \\t\\f\\v]+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();
}