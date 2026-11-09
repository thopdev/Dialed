using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Dialed.Client.Data;
using Dialed.Client.Platform;
using Dialed.Client.Sync;
using Dialed.Domain.Model;
using Dialed.Domain.Text;

namespace Dialed.Client.Scan;

/// <summary>One value read from a label; <see cref="IsUncertain"/> fields are highlighted in the review form.</summary>
public sealed record ScannedField(string? Value, double Confidence)
{
    public const double Sure = 0.6;

    public bool HasValue => !string.IsNullOrWhiteSpace(Value);

    public bool IsUncertain => HasValue && Confidence < Sure;
}

/// <summary>The label as the API read it (see docs/ai-label-scan.md).</summary>
public sealed record ScannedLabel(
    ScannedField Name,
    ScannedField Roaster,
    ScannedField Country,
    ScannedField Region,
    ScannedField Farm,
    ScannedField Process,
    ScannedField Varieties,
    ScannedField RoastLevel,
    ScannedField TastingNotes,
    ScannedField RoastDate,
    ScannedField WeightG,
    string PromptVersion,
    string ModelId);

public enum ScanProblem
{
    /// <summary>Not signed in: scanning runs on the server.</summary>
    SignedOut,

    /// <summary>No connection (scans are online only for now).</summary>
    Offline,

    /// <summary>Daily quota used up (429).</summary>
    QuotaUsed,

    /// <summary>The kill switch is on (503, code ai-disabled).</summary>
    Disabled,

    /// <summary>The model failed or answered something unusable.</summary>
    Unavailable,
}

public sealed class LabelScanException(ScanProblem problem, string? message = null) : Exception(message ?? problem.ToString())
{
    public ScanProblem Problem { get; } = problem;
}

public interface ILabelScanner
{
    /// <summary>Sends the photo to the API and returns what was read. Throws <see cref="LabelScanException"/>.</summary>
    Task<ScannedLabel> ScanAsync(CapturedPhoto photo, CancellationToken cancellationToken = default);
}

/// <summary>Calls <c>POST /ai/label-scans</c>.</summary>
public sealed class LabelScanner(HttpClient http, IAccessTokenProvider tokens, IConnectivity connectivity) : ILabelScanner
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record Response(Draft Draft, string PromptVersion, string ModelId);

    private sealed record Draft(
        ScannedField Name, ScannedField Roaster, ScannedField Country, ScannedField Region, ScannedField Farm, ScannedField Process,
        ScannedField Varieties, ScannedField RoastLevel, ScannedField TastingNotes, ScannedField RoastDate, ScannedField WeightG);

    public async Task<ScannedLabel> ScanAsync(CapturedPhoto photo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(photo);

        var token = await tokens.GetAccessTokenAsync(cancellationToken) ?? throw new LabelScanException(ScanProblem.SignedOut);
        if (!connectivity.IsOnline)
        {
            throw new LabelScanException(ScanProblem.Offline);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "ai/label-scans") { Content = new ByteArrayContent(photo.Content) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException)
        {
            throw new LabelScanException(ScanProblem.Offline);
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
            {
                var body = await ReadFromJsonOrNullAsync(response.Content, cancellationToken)
                    ?? throw new LabelScanException(ScanProblem.Unavailable);
                var d = body.Draft;
                return new ScannedLabel(d.Name, d.Roaster, d.Country, d.Region, d.Farm, d.Process, d.Varieties, d.RoastLevel, d.TastingNotes, d.RoastDate, d.WeightG, body.PromptVersion, body.ModelId);
            }

            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            throw response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => new LabelScanException(ScanProblem.SignedOut),
                HttpStatusCode.TooManyRequests => new LabelScanException(ScanProblem.QuotaUsed),
                HttpStatusCode.ServiceUnavailable when text.Contains("ai-disabled", StringComparison.Ordinal) => new LabelScanException(ScanProblem.Disabled),
                _ => new LabelScanException(ScanProblem.Unavailable),
            };
        }
    }

    private static async Task<Response?> ReadFromJsonOrNullAsync(HttpContent content, CancellationToken cancellationToken)
    {
        try
        {
            return await content.ReadFromJsonAsync<Response>(Json, cancellationToken);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>What the person confirmed in the review form.</summary>
public sealed record ScanReview(
    string Name,
    string? RoasterName,
    string? ExistingRoasterId,
    string? Country,
    string? Region,
    string? Process,
    string? Varieties,
    string? RoastLevel,
    string? Notes,
    decimal? WeightG,
    DateOnly? RoastDate,
    string? ExistingBeanId,
    string? PhotoId);

public sealed record ScanSaved(Bean Bean, Bag? Bag, bool CreatedBean, bool CreatedRoaster);

/// <summary>Saves a reviewed scan: roaster (linked or new), bean (linked or new) and the bag the label describes.</summary>
public static class ScanSaver
{
    public static async Task<ScanSaved> SaveAsync(ScanReview review, IRepository<Bean> beans, IRepository<Bag> bags, IRepository<Vendor> vendors, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(review);

        string? roasterId = review.ExistingRoasterId;
        var createdRoaster = false;
        if (roasterId is null && !string.IsNullOrWhiteSpace(review.RoasterName))
        {
            var roaster = await vendors.SaveAsync(new Vendor { Name = review.RoasterName.Trim(), Kinds = VendorKinds.Roaster }, cancellationToken);
            roasterId = roaster.Id;
            createdRoaster = true;
        }

        var bean = review.ExistingBeanId is { } existing ? await beans.GetAsync(existing, cancellationToken) : null;
        var createdBean = false;
        if (bean is null)
        {
            bean = await beans.SaveAsync(new Bean
            {
                Name = review.Name.Trim(),
                RoasterId = roasterId,
                Origin = new BeanOrigin(Country: Clean(review.Country), Region: Clean(review.Region)),
                Process = Clean(review.Process),
                Varieties = Domain.Import.BeanImport.SplitList(review.Varieties),
                RoastLevel = Domain.Import.BeanImport.ParseRoast(review.RoastLevel),
                Notes = Clean(review.Notes),
                PhotoIds = review.PhotoId is null ? [] : [review.PhotoId],
            }, cancellationToken);
            createdBean = true;
        }

        Bag? bag = null;
        if (review.WeightG is > 0)
        {
            bag = await bags.SaveAsync(new Bag
            {
                BeanId = bean.Id,
                WeightG = review.WeightG.Value,
                RoastDate = review.RoastDate,
                Status = Domain.Inventory.BagStatus.Sealed,
            }, cancellationToken);
        }

        return new ScanSaved(bean, bag, createdBean, createdRoaster);
    }

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
